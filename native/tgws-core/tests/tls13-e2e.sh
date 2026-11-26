#!/usr/bin/env bash
# End-to-end TLS 1.3 client check against a local `openssl s_server`.
#
# The script generates a throw-away self-signed certificate, starts s_server on
# loopback with TLS_AES_128_GCM_SHA256 only, runs the ignored Rust test that
# performs a real handshake and a protected `GET /`, then prints OK or FAIL.
#
# Requirements: bash, openssl with TLS 1.3 support, cargo/rustc.
# Set TLS13_E2E_PORT to override the default port 9443.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
crate_dir="$(cd "$here/.." && pwd)"

# Some machines put a rustup shim first on PATH without a default toolchain.
# Fall back to the distribution toolchain when that shim cannot run.
if ! cargo --version >/dev/null 2>&1; then
  if [ -x /usr/sbin/cargo ] && [ -x /usr/sbin/rustc ]; then
    export PATH="/usr/sbin:/usr/bin:$PATH"
  fi
fi

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl is not installed" >&2
  echo "FAIL"
  exit 1
fi

if ! openssl s_server -help 2>&1 | grep -q -- '-tls1_3'; then
  echo "this openssl build does not support TLS 1.3 (-tls1_3); skipping" >&2
  echo "FAIL"
  exit 77
fi

tmp="$(mktemp -d "${TMPDIR:-/tmp}/tgws-tls13-e2e.XXXXXX")"
port="${TLS13_E2E_PORT:-9443}"
pid=""

cleanup() {
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  rm -rf "$tmp"
}
trap cleanup EXIT

openssl req -x509 -newkey rsa:2048 -sha256 -nodes \
  -keyout "$tmp/key.pem" -out "$tmp/cert.pem" -days 1 \
  -subj "/CN=localhost" >"$tmp/req.log" 2>&1

openssl s_server -www -tls1_3 -accept "127.0.0.1:$port" \
  -cert "$tmp/cert.pem" -key "$tmp/key.pem" \
  -ciphersuites TLS_AES_128_GCM_SHA256 -alpn http/1.1 >"$tmp/server.log" 2>&1 &
pid=$!

status=0
(
  cd "$crate_dir"
  TLS13_E2E_ADDR="127.0.0.1:$port" \
  TLS13_E2E_SNI="localhost" \
    cargo test --test tls13_e2e -- --ignored --nocapture
) || status=$?

if [ "$status" -ne 0 ]; then
  echo "----- openssl s_server log -----"
  cat "$tmp/server.log" || true
  echo "FAIL"
  exit "$status"
fi

echo "OK"
