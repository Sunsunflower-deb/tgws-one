#!/usr/bin/env bash
# Root-only check of the desync layer on the wire (plan E2.6). Three throwaway network namespaces are joined by two
# veth pairs; only the router namespace forwards, and the host's own network configuration is never changed:
#
#   tgwsd-c (10.88.0.1)  the probe: EgressSocket + SslStream with one strategy
#   tgwsd-r             the router: ip_forward=1 lives only here; low-TTL segments die on it
#   tgwsd-s (10.88.1.2)  openssl s_server -www, plus a capture of every segment that actually arrives
#
# Every strategy is run in a fresh connection against a fresh server, with a client-side and a server-side capture.
# tests/root/pcap-check.py parses the captures; the checks below name the exact capture fact that did not match.
#
#   sudo tests/root/netns-desync.sh            # PASS/FAIL table, netns removed afterwards
#   KEEP=1 sudo tests/root/netns-desync.sh     # keep the work folder for debugging
#   PORT=8443 sudo ...                         # another server port
#   EVIDENCE=/path sudo ...                    # another evidence folder
#
# Evidence (pcaps, probe JSON, stderr) is written to out/android-evidence/E2.6/ by default. tcpdump is used when it is
# installed; otherwise the script writes a classic libpcap file itself with a tiny AF_PACKET capture (no packages are
# installed by this script). The probe is built with System.Net.DisableIPv6=true (see its csproj): this stend tests the
# IPv4 TTL, and a dual-mode IPv6 socket would make the TTL step set the IPv6 hop limit instead. A jumbo MTU keeps the
# 1.5 KB ClientHello in one segment for the control case.
set -euo pipefail
umask 022

if [ "$(id -u)" -ne 0 ]; then
    echo "нужны права root: sudo $0" >&2
    exit 2
fi

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
for tool in ip openssl python3 ss; do
    command -v "$tool" >/dev/null || { echo "не найден $tool" >&2; exit 2; }
done

# dotnet may live outside root's PATH (sudo keeps only a secure path): prefer DOTNET, then the caller's install.
DOTNET=${DOTNET:-}
if [ -z "$DOTNET" ] && command -v dotnet >/dev/null; then DOTNET=$(command -v dotnet); fi
if [ -z "$DOTNET" ] && [ -n "${SUDO_USER:-}" ]; then
    caller_home=$(getent passwd "$SUDO_USER" | cut -d: -f6)
    [ -x "$caller_home/.dotnet/dotnet" ] && DOTNET="$caller_home/.dotnet/dotnet"
fi
if [ -z "$DOTNET" ]; then
    for candidate in /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /opt/dotnet/dotnet; do
        [ -x "$candidate" ] && DOTNET=$candidate && break
    done
fi
[ -n "$DOTNET" ] || { echo "не найден dotnet; задайте DOTNET=/path/to/dotnet" >&2; exit 2; }

PROBE_PROJECT=$repo/tests/DesyncProbe/DesyncProbe.csproj
PROBE_DLL=$repo/tests/DesyncProbe/bin/Debug/net10.0/DesyncProbe.dll
PCAP_CHECK=$repo/tests/root/pcap-check.py
WORK=$(mktemp -d /tmp/tgws-desync.XXXXXX)
EVIDENCE=${EVIDENCE:-$repo/out/android-evidence/E2.6}
mkdir -p "$WORK/home" "$EVIDENCE"

# The probe is a test helper, not a solution project: build it pointwise instead of touching TgwsOne.slnx.
caller_home=
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then
    caller_home=$(getent passwd "$SUDO_USER" | cut -d: -f6)
fi
probe_stale() {
    [ -f "$PROBE_DLL" ] || return 0
    [ -n "$(find "$repo/tests/DesyncProbe" "$repo/src/TgwsOne.Engine" -type f \
        \( -name '*.cs' -o -name '*.csproj' \) -newer "$PROBE_DLL" -print -quit 2>/dev/null)" ]
}
if probe_stale; then
    echo "INFO  сборка $PROBE_PROJECT"
    if [ -n "$caller_home" ] && command -v runuser >/dev/null; then
        runuser -u "$SUDO_USER" -- env PATH="$(dirname "$DOTNET"):$PATH" HOME="$caller_home" \
            DOTNET_CLI_HOME="$caller_home" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            "$DOTNET" build "$PROBE_PROJECT" -c Debug -v quiet
    else
        env PATH="$(dirname "$DOTNET"):$PATH" HOME="$WORK/home" DOTNET_CLI_HOME="$WORK/home" \
            DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            "$DOTNET" build "$PROBE_PROJECT" -c Debug -v quiet
    fi
fi
[ -f "$PROBE_DLL" ] || { echo "не собрался $PROBE_DLL" >&2; exit 2; }

NS_C=tgwsd-c
NS_R=tgwsd-r
NS_S=tgwsd-s
CLIENT_IP=10.88.0.1
ROUTER_CLIENT_IP=10.88.0.2
ROUTER_SERVER_IP=10.88.1.1
SERVER_IP=10.88.1.2
PORT=${PORT:-443}
REAL_SNI=${REAL_SNI:-probe.example}
FAKE_SNI=www.google.com
# A jumbo MTU keeps a 1.5 KB ClientHello in one segment, so the "none" control case is one segment as the plan says.
MTU=${MTU:-9000}

pass=0
fail=0
srv_pid=
cap_c=
cap_s=

ok() { echo "PASS  $*"; pass=$((pass + 1)); }
bad() { echo "FAIL  $*"; fail=$((fail + 1)); }
info() { echo "INFO  $*"; }
cns() { ip netns exec "$NS_C" "$@"; }
sns() { ip netns exec "$NS_S" "$@"; }

wait_for() {
    local end=$((SECONDS + $1))
    shift
    while ((SECONDS < end)); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 0.1
    done
    return 1
}

jget() {
    python3 - "$1" "$2" <<'PY'
import json, sys
try:
    value = json.load(open(sys.argv[1])).get(sys.argv[2])
except Exception:
    value = None
if value is None:
    print("")
elif isinstance(value, bool):
    print("true" if value else "false")
else:
    print(value)
PY
}

run_pcap_check() {
    local label=$1
    shift
    local output
    if output=$(python3 "$PCAP_CHECK" "$@" 2>&1); then
        ok "$label"
    else
        bad "$label"
        printf '%s\n' "$output" | sed 's/^/      /'
    fi
}

start_server() {
    ip netns exec "$NS_S" openssl s_server -accept "$SERVER_IP:$PORT" \
        -cert "$WORK/cert.pem" -key "$WORK/key.pem" -www -quiet >>"$WORK/server.log" 2>&1 &
    srv_pid=$!
    wait_for 8 sh -c "ip netns exec $NS_S ss -ltn 2>/dev/null | grep -q ':$PORT '"
}

stop_server() {
    [ -n "$srv_pid" ] || return 0
    kill "$srv_pid" 2>/dev/null || true
    sleep 0.2
    kill -9 "$srv_pid" 2>/dev/null || true
    srv_pid=
}

start_captures() {
    local strategy=$1
    if command -v tcpdump >/dev/null; then
        ip netns exec "$NS_C" tcpdump -i c0 -nn -U -w "$EVIDENCE/$strategy-client.pcap" \
            "tcp and dst port $PORT" >/dev/null 2>&1 &
        cap_c=$!
        ip netns exec "$NS_S" tcpdump -i s0 -nn -U -w "$EVIDENCE/$strategy-server.pcap" \
            "tcp and dst port $PORT" >/dev/null 2>&1 &
        cap_s=$!
    else
        ip netns exec "$NS_C" python3 "$WORK/capture.py" c0 "$EVIDENCE/$strategy-client.pcap" &
        cap_c=$!
        ip netns exec "$NS_S" python3 "$WORK/capture.py" s0 "$EVIDENCE/$strategy-server.pcap" &
        cap_s=$!
    fi
    sleep 0.3
}

stop_captures() {
    for pid in "$cap_c" "$cap_s"; do
        [ -n "$pid" ] && kill -TERM "$pid" 2>/dev/null || true
    done
    sleep 0.4
    for pid in "$cap_c" "$cap_s"; do
        [ -n "$pid" ] && kill -9 "$pid" 2>/dev/null || true
    done
    cap_c=
    cap_s=
}

# run_probe <strategy> <timeout-ms>: one connection in the client namespace, JSON on stdout, rc in probe_rc.
run_probe() {
    local strategy=$1 timeout_ms=$2
    local seconds=$((timeout_ms / 1000 + 8))
    probe_rc=0
    timeout -k 2 "$seconds" ip netns exec "$NS_C" env \
        HOME="$WORK/home" DOTNET_CLI_HOME="$WORK/home" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        "$DOTNET" "$PROBE_DLL" --host "$SERVER_IP" --port "$PORT" --strategy "$strategy" \
        --sni "$REAL_SNI" --timeout "$timeout_ms" \
        >"$EVIDENCE/$strategy.json" 2>"$EVIDENCE/$strategy.err" || probe_rc=$?
    cat "$EVIDENCE/$strategy.json"
    if [ "$probe_rc" -eq 124 ] || [ "$probe_rc" -eq 137 ]; then
        bad "$strategy: проба не завершилась за ${seconds}s"
    fi
}

# run_strategy <strategy> <timeout-ms>: fresh server and fresh captures around one probe.
run_strategy() {
    local strategy=$1 timeout_ms=$2
    stop_server
    if ! start_server; then
        bad "$strategy: openssl s_server не поднялся"
        return 1
    fi
    start_captures "$strategy"
    info "проба $strategy: $SERVER_IP:$PORT, SNI $REAL_SNI"
    run_probe "$strategy" "$timeout_ms"
    sleep 0.8
    stop_captures
    stop_server
}

# Lifetime fields of a DHCP address count down, so only the interface/prefix pairs and the routes are compared.
host_snapshot() {
    {
        ip -o -4 addr show | awk '{print $2, $4}'
        ip -o -4 route show
        ip netns list
    } 2>/dev/null | sort
}

teardown_namespaces() {
    stop_captures
    stop_server
    for ns in "$NS_C" "$NS_R" "$NS_S"; do
        ip netns pids "$ns" 2>/dev/null | xargs -r kill -9 2>/dev/null
        ip netns delete "$ns" 2>/dev/null
    done
}

cleanup() {
    local rc=$?
    set +e
    stop_captures
    stop_server
    for ns in "$NS_C" "$NS_R" "$NS_S"; do
        ip netns pids "$ns" 2>/dev/null | xargs -r kill -9 2>/dev/null
        ip netns delete "$ns" 2>/dev/null
    done
    [ -n "${SUDO_UID:-}" ] && chown -R "$SUDO_UID:${SUDO_GID:-$SUDO_UID}" "$EVIDENCE" 2>/dev/null
    if [ "$rc" -ne 0 ] || [ "$fail" -gt 0 ] || [ "${KEEP:-0}" = 1 ]; then
        echo "рабочая папка: $WORK"
    else
        rm -rf "$WORK"
    fi
    echo "доказательства: $EVIDENCE"
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# ---- setup -------------------------------------------------------------------------------------------------------

# A small pcap writer for the case tcpdump is not installed: classic libpcap header, linktype EN10MB, flushed per
# packet so a SIGTERM never loses the last segment.
cat >"$WORK/capture.py" <<'PY'
import signal, socket, struct, sys, time
iface, out = sys.argv[1], sys.argv[2]
sock = socket.socket(socket.AF_PACKET, socket.SOCK_RAW, socket.htons(3))
sock.bind((iface, 0))
sock.settimeout(0.2)
handle = open(out, "wb")
handle.write(struct.pack("<IHHIIII", 0xA1B2C3D4, 2, 4, 0, 0, 65535, 1))
handle.flush()
stop = False
def stop_now(_signum, _frame):
    global stop
    stop = True
signal.signal(signal.SIGTERM, stop_now)
signal.signal(signal.SIGINT, stop_now)
while not stop:
    try:
        frame = sock.recv(65535)
    except socket.timeout:
        continue
    now = time.time()
    handle.write(struct.pack("<IIII", int(now), int((now % 1) * 1_000_000), len(frame), len(frame)))
    handle.write(frame)
    handle.flush()
handle.close()
PY

for ns in "$NS_C" "$NS_R" "$NS_S"; do ip netns delete "$ns" 2>/dev/null || true; done
host_before=$(host_snapshot)
ip netns add "$NS_C"
ip netns add "$NS_R"
ip netns add "$NS_S"
# Each peer is created straight inside its namespace, so neither end ever exists in the host's.
ip -n "$NS_C" link add c0 type veth peer name r0 netns "$NS_R"
ip -n "$NS_R" link add r1 type veth peer name s0 netns "$NS_S"
ip -n "$NS_C" addr add "$CLIENT_IP/24" dev c0
ip -n "$NS_R" addr add "$ROUTER_CLIENT_IP/24" dev r0
ip -n "$NS_R" addr add "$ROUTER_SERVER_IP/24" dev r1
ip -n "$NS_S" addr add "$SERVER_IP/24" dev s0
for spec in "$NS_C c0" "$NS_R r0" "$NS_R r1" "$NS_S s0"; do
    set -- $spec
    ip -n "$1" link set "$2" mtu "$MTU"
    ip -n "$1" link set "$2" up
done
for ns in "$NS_C" "$NS_R" "$NS_S"; do ip -n "$ns" link set lo up; done
ip -n "$NS_C" route add 10.88.1.0/24 via "$ROUTER_CLIENT_IP"
ip -n "$NS_S" route add 10.88.0.0/24 via "$ROUTER_SERVER_IP"
# Forwarding is turned on only in the router's namespace; the host's sysctl is untouched.
ip netns exec "$NS_R" sysctl -qw net.ipv4.ip_forward=1
ip netns exec "$NS_R" sysctl -qw net.ipv4.conf.all.rp_filter=0
ip netns exec "$NS_R" sysctl -qw net.ipv4.conf.default.rp_filter=0

openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 2 \
    -subj "/CN=$REAL_SNI" -addext "subjectAltName=DNS:$REAL_SNI" \
    -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null

if command -v tcpdump >/dev/null; then
    info "захват: tcpdump"
else
    info "захват: python AF_PACKET → pcap (tcpdump не установлен; ставить пакеты не требуется)"
fi
info "канал: client $CLIENT_IP ↔ router $ROUTER_CLIENT_IP/$ROUTER_SERVER_IP ↔ server $SERVER_IP, MTU $MTU"

# ---- none: the control group ------------------------------------------------------------------------------------

run_strategy none 8000
if [ "$(jget "$EVIDENCE/none.json" ok)" = true ]; then
    ok "none: рукопожатие успешно"
else
    bad "none: рукопожатие не завершилось ($(jget "$EVIDENCE/none.json" error))"
fi
none_ttl=$(jget "$EVIDENCE/none.json" system_ttl)
[ -n "$none_ttl" ] || none_ttl=$(cns sysctl -n net.ipv4.ip_default_ttl 2>/dev/null || echo 64)
run_pcap_check "none: один сегмент с полным ClientHello, TTL=$none_ttl, без URG" \
    --quiet --pcap "$EVIDENCE/none-client.pcap" --dst-port "$PORT" --check none --system-ttl "$none_ttl"
none_retrans=$(jget "$EVIDENCE/none.json" retransmits)
if [ "$none_retrans" = 0 ]; then
    ok "none: ретрансмиссий нет (TCP_INFO=0)"
else
    bad "none: TCP_INFO насчитал ретрансмиссии: ${none_retrans:-нет данных}"
fi

# ---- split: segment boundaries follow the cut ------------------------------------------------------------------

run_strategy split 8000
if [ "$(jget "$EVIDENCE/split.json" ok)" = true ]; then
    ok "split: рукопожатие успешно"
else
    bad "split: рукопожатие не завершилось ($(jget "$EVIDENCE/split.json" error))"
fi
split_cut=$(jget "$EVIDENCE/split.json" cut)
if [ -n "$split_cut" ]; then
    run_pcap_check "split: на клиенте границы сегментов = позиция разреза $split_cut" \
        --quiet --pcap "$EVIDENCE/split-client.pcap" --dst-port "$PORT" --check split --cut "$split_cut"
    run_pcap_check "split: на сервер обе части пришли отдельными сегментами" \
        --quiet --pcap "$EVIDENCE/split-server.pcap" --dst-port "$PORT" --check split --cut "$split_cut"
else
    bad "split: проба не увидела ClientHello, позиция разреза неизвестна"
fi

# ---- disorder: low TTL on the first part, reversed arrival at the server ---------------------------------------

run_strategy disorder 8000
if [ "$(jget "$EVIDENCE/disorder.json" ok)" = true ]; then
    ok "disorder: рукопожатие успешно"
else
    bad "disorder: рукопожатие не завершилось ($(jget "$EVIDENCE/disorder.json" error))"
fi
disorder_cut=$(jget "$EVIDENCE/disorder.json" cut)
disorder_ttl=$(jget "$EVIDENCE/disorder.json" system_ttl)
if [ -n "$disorder_cut" ] && [ -n "$disorder_ttl" ]; then
    run_pcap_check "disorder: первый кусок TTL=1, второй TTL=$disorder_ttl (клиент)" \
        --quiet --pcap "$EVIDENCE/disorder-client.pcap" --dst-port "$PORT" --check disorder-ttl \
        --cut "$disorder_cut" --system-ttl "$disorder_ttl"
    run_pcap_check "disorder: на сервер первым пришёл второй кусок, затем ретрансмиссия первого" \
        --quiet --pcap "$EVIDENCE/disorder-server.pcap" --dst-port "$PORT" --check disorder-order \
        --cut "$disorder_cut"
else
    bad "disorder: проба не сообщила позицию разреза и системный TTL"
fi
disorder_retrans=$(jget "$EVIDENCE/disorder.json" retransmits)
if [ -n "$disorder_retrans" ] && [ "$disorder_retrans" -ge 1 ]; then
    ok "disorder: TCP_INFO видит ретрансмиссию ($disorder_retrans)"
else
    bad "disorder: TCP_INFO не видит ретрансмиссии (${disorder_retrans:-нет данных}); первый кусок, похоже, дошёл"
fi

# ---- fake: the fake dies, the original arrives after the retransmission ----------------------------------------

run_strategy fake 8000
if [ "$(jget "$EVIDENCE/fake.json" ok)" = true ]; then
    ok "fake: рукопожатие успешно"
else
    bad "fake: рукопожатие не завершилось ($(jget "$EVIDENCE/fake.json" error))"
fi
fake_cut=$(jget "$EVIDENCE/fake.json" cut)
if [ -n "$fake_cut" ]; then
    run_pcap_check "fake: фейк $FAKE_SNI с TTL=1 ушёл с клиента" \
        --quiet --pcap "$EVIDENCE/fake-client.pcap" --dst-port "$PORT" --check fake-dead \
        --cut "$fake_cut" --fake-sni "$FAKE_SNI"
    run_pcap_check "fake: до сервера фейк не дошёл, оригинал с SNI $REAL_SNI дошёл" \
        --quiet --pcap "$EVIDENCE/fake-server.pcap" --dst-port "$PORT" --check fake-alive \
        --cut "$fake_cut" --fake-sni "$FAKE_SNI" --real-sni "$REAL_SNI"
else
    bad "fake: проба не увидела ClientHello, граница фейка неизвестна"
fi
fake_retrans=$(jget "$EVIDENCE/fake.json" retransmits)
if [ -n "$fake_retrans" ] && [ "$fake_retrans" -ge 1 ]; then
    ok "fake: TCP_INFO видит ретрансмиссию с оригиналом ($fake_retrans)"
else
    bad "fake: TCP_INFO не видит ретрансмиссии (${fake_retrans:-нет данных})"
fi

# ---- oob: the urgent flag is on the wire -----------------------------------------------------------------------

run_strategy oob 4000
# openssl s_server does not set SO_OOBINLINE, so the urgent byte is not put back into its stream and the handshake
# is expected to time out; the plan requires only the URG flag for this strategy.
if [ "$(jget "$EVIDENCE/oob.json" ok)" = true ]; then
    info "oob: рукопожатие успешно (для этой стратегии не требуется)"
else
    info "oob: рукопожатие не завершилось (ожидаемо: openssl без SO_OOBINLINE)"
fi
run_pcap_check "oob: у сегмента с данными стоит флаг URG" \
    --quiet --pcap "$EVIDENCE/oob-client.pcap" --dst-port "$PORT" --check oob

# ---- tlsrec (extra): the record is rebuilt and still accepted ---------------------------------------------------

run_strategy tlsrec 8000
if [ "$(jget "$EVIDENCE/tlsrec.json" ok)" = true ]; then
    ok "tlsrec: рукопожатие успешно"
else
    bad "tlsrec: рукопожатие не завершилось ($(jget "$EVIDENCE/tlsrec.json" error))"
fi

# ---- the host stayed as it was ---------------------------------------------------------------------------------

# The trap deletes the namespaces after the summary; delete them now so the host check sees the final state.
teardown_namespaces
if [ "$(host_snapshot)" = "$host_before" ]; then
    ok "хост не затронут: адреса и маршруты основного пространства прежние"
else
    bad "адреса или маршруты основного пространства изменились"
fi
for ns in "$NS_C" "$NS_R" "$NS_S"; do
    if ip netns list | grep -q "^$ns\b"; then
        bad "пространство $ns осталось после прогона"
    fi
done

echo
echo "итог: пройдено $pass, не пройдено $fail"
if [ "$fail" -gt 0 ]; then
    exit 1
fi
