#!/usr/bin/env bash
# Builds the headless tg-ws-proxy engine for Linux: out/engine/linux-x64/tgws-engine.
#
# Downloads the pinned upstream commit, verifies its sha256, installs the dependencies from the hashed
# requirements.lock into a fresh venv (uv) and bundles proxy/tg_ws_proxy.py with PyInstaller --onefile.
# Everything happens in a temporary directory that is removed afterwards. Needs: bash, curl, tar, sha256sum, uv.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
# shellcheck source=engine.lock
source "$here/engine.lock"

out="$root/out/engine/linux-x64"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The uv cache and the temp dir usually sit on different filesystems: hardlinking would only warn.
export UV_LINK_MODE=copy

url="https://github.com/$UPSTREAM_REPO/archive/$UPSTREAM_COMMIT.tar.gz"
echo "==> downloading $UPSTREAM_REPO $UPSTREAM_TAG ($UPSTREAM_COMMIT)"
curl -fsSL --retry 3 --max-time 300 -o "$work/upstream.tar.gz" "$url"

actual="$(sha256sum "$work/upstream.tar.gz" | cut -d' ' -f1)"
if [[ -z "${UPSTREAM_SHA256:-}" ]]; then
    echo "UPSTREAM_SHA256 is empty in engine/engine.lock; the download hashes to:" >&2
    echo "  $actual" >&2
    echo "Check it, record it in engine/engine.lock (trust on first use), then run again." >&2
    exit 1
fi
if [[ "$actual" != "$UPSTREAM_SHA256" ]]; then
    echo "sha256 mismatch for $url" >&2
    echo "  expected $UPSTREAM_SHA256" >&2
    echo "  actual   $actual" >&2
    exit 1
fi
echo "    sha256 ok"

# The source is untrusted data: unpack it into its own empty directory and never run anything from inside it.
mkdir "$work/src"
tar -xzf "$work/upstream.tar.gz" -C "$work/src" --strip-components=1 --no-same-owner --no-same-permissions

echo "==> python $PYTHON venv and pinned packages"
uv venv --python "$PYTHON" "$work/venv"
py="$work/venv/bin/python"
# requirements.lock pins every package with hashes (the Windows-only and macOS-only ones sit under markers).
# --require-hashes refuses anything that is missing from it, unpinned, or does not match its hash.
uv pip install --python "$py" --require-hashes -r "$here/requirements.lock"

echo "==> bundling"
mkdir -p "$out"
# utils.logging_setup is imported lazily inside main(), so PyInstaller cannot see it. The GUI libraries are
# excluded so the binary starts without a display. -I: isolated mode, nothing from the cwd is imported.
"$py" -I -m PyInstaller --noconfirm --clean --onefile --name tgws-engine \
    --paths "$work/src" \
    --hidden-import utils.logging_setup \
    --exclude-module customtkinter --exclude-module pystray --exclude-module PIL --exclude-module tkinter \
    --distpath "$out" --workpath "$work/build" --specpath "$work" \
    "$here/entry.py"

echo "==> smoke test"
help="$("$out/tgws-engine" --help)"   # set -e: a non-zero exit fails the build
# Every flag EngineCommand (src/TgwsOne.Core) puts on the command line must exist in this build, or an
# upstream bump would silently break the engine's argv.
for flag in --host --port --secret --dc-ip --no-cfproxy --no-h2 --no-secure --cfproxy-domain \
            --cfproxy-worker-domain --verbose --log-file --log-max-mb --log-backups --buf-kb --pool-size; do
    if ! grep -q -e "$flag" <<< "$help"; then
        echo "engine --help does not list $flag: EngineCommand and this build disagree" >&2
        exit 1
    fi
done
# No display must be needed.
env -u DISPLAY -u WAYLAND_DISPLAY "$out/tgws-engine" --help > /dev/null

echo "==> $out/tgws-engine ($(du -h "$out/tgws-engine" | cut -f1))"
