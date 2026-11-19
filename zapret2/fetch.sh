#!/usr/bin/env bash
# Fetches the pinned zapret2 release, verifies its sha256 and unpacks only what TgwsOne ships into
# out/zapret2/linux-x64/ and out/zapret2/win-x64/ (keeping the lua/ and files/fake/ sub-paths).
# Z2_ZIP=<file> uses a local copy of the archive instead of downloading it; the hash is checked either way.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(dirname "$here")
# shellcheck source=/dev/null
. "$here/zapret2.lock"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

zip=${Z2_ZIP:-}
if [ -z "$zip" ]; then
    zip=$work/zapret2.zip
    echo "загрузка $Z2_URL"
    curl -fL --retry 3 --silent --show-error -o "$zip" "$Z2_URL"
fi

actual=$(sha256sum "$zip" | cut -d' ' -f1)
if [ "$actual" != "$Z2_SHA256" ]; then
    echo "контрольная сумма архива не совпадает: ожидалась $Z2_SHA256, получена $actual" >&2
    exit 1
fi

top=zapret2-$Z2_VERSION
common=(
    "$top/lua/zapret-lib.lua"
    "$top/lua/zapret-antidpi.lua"
    "$top/lua/zapret-auto.lua"
    "$top/files/fake/tls_clienthello_www_google_com.bin"
    "$top/files/fake/tls_clienthello_vk_com.bin"
)
linux=("$top/binaries/linux-x86_64/nfqws2")
win=(
    "$top/binaries/windows-x86_64/winws2.exe"
    "$top/binaries/windows-x86_64/WinDivert.dll"
    "$top/binaries/windows-x86_64/WinDivert64.sys"
    "$top/binaries/windows-x86_64/cygwin1.dll"
)

unzip -q "$zip" "${common[@]}" "${linux[@]}" "${win[@]}" -d "$work/x"

out=$root/out/zapret2
rm -rf "$out/linux-x64" "$out/win-x64"
for rid in linux-x64 win-x64; do
    mkdir -p "$out/$rid/lua" "$out/$rid/files/fake"
    cp "$work/x/$top"/lua/zapret-{lib,antidpi,auto}.lua "$out/$rid/lua/"
    cp "$work/x/$top"/files/fake/tls_clienthello_{www_google_com,vk_com}.bin "$out/$rid/files/fake/"
done
install -m 0755 "$work/x/$top/binaries/linux-x86_64/nfqws2" "$out/linux-x64/nfqws2"
cp "$work/x/$top"/binaries/windows-x86_64/{winws2.exe,WinDivert.dll,WinDivert64.sys,cygwin1.dll} "$out/win-x64/"

# Prints e.g. "github version v1.0.7 (<commit>) lua_compat_ver 6".
version=$("$out/linux-x64/nfqws2" --version | head -n 1)
case "$version" in
    *" $Z2_VERSION "*) ;;
    *) echo "nfqws2 --version сообщает «$version», ожидалась $Z2_VERSION" >&2; exit 1 ;;
esac
echo "zapret2 $Z2_VERSION готов: $out"
