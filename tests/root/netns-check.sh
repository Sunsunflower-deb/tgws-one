#!/usr/bin/env bash
# Root-only, opt-in check of the real thing: nfqws2 + nftables + the bypass service, in two throwaway network
# namespaces joined by a veth pair. Run it as root (sudo -A tests/root/netns-check.sh) after `make zapret2 build`.
#
#   tgws-c (10.77.0.1)  the client: the service (tgwsone --service), its nft table and nfqws2 live here
#   tgws-s (10.77.0.2)  the server: a TLS server for two test host names, and later a rule that eats its replies
#
# Everything that touches nftables or sysctls runs under `ip netns exec`, so the host's own network configuration
# is never modified. (The script only *reads* the host's list of nft tables, before and after, to prove that.)
# KEEP=1 keeps the work folder (always kept when a check fails); TGWS_APP=<path> uses another tgwsone binary;
# TGWS_ZAPRET_EXE=<path> (passed on to the service) replaces nfqws2, which the rootless rehearsal uses.
# The work folder gets evidence/: the debug log after each phase, and a capture of what the server received.
#
# nfqws2 drops root privileges after start-up (to uid 0x7FFFFFFF) and runs all Lua, and re-reads the lists, as that
# unprivileged user. Everything it touches therefore has to be reachable by everybody: the run happens in a copy of the
# app (and a work folder) that anyone can traverse, and a preflight checks that before the service is started.
set -euo pipefail
umask 022

if [ "$(id -u)" -ne 0 ]; then
    echo "нужны права root: sudo -A $0" >&2
    exit 2
fi

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
APP=${TGWS_APP:-$repo/out/publish/linux-x64/tgwsone}
if [ ! -x "$APP" ] || [ ! -x "$(dirname "$APP")/zapret2/nfqws2" ]; then
    echo "не найден $APP или пакет zapret2 рядом с ним: сначала выполните make zapret2 и make build" >&2
    exit 2
fi
for tool in ip nft openssl curl; do
    command -v "$tool" >/dev/null || { echo "не найден $tool" >&2; exit 2; }
done

NS_C=tgws-c
NS_S=tgws-s
SERVER_IP=10.77.0.2
HOST=tgtest.example         # goes through the rotation checks
HOST2=excluded.example      # the one that gets excluded and included again
WORK=$(mktemp -d /tmp/tgws-netns.XXXXXX)
chmod 755 "$WORK"   # mktemp makes it 0700, and the unprivileged uid nfqws2 drops to must get through it
ROOT=$WORK/svc
DEBUG=$ROOT/debug.log
EVIDENCE=$WORK/evidence
NOBODY=2147483647   # 0x7FFFFFFF, nfqws2's default uid and gid after dropping privileges
mkdir -p "$ROOT/lists" "$EVIDENCE"

pass=0
fail=0
svc_pid=
srv_pid=
cap_pid=

cns() { ip netns exec "$NS_C" "$@"; }
sns() { ip netns exec "$NS_S" "$@"; }
cli() { TGWS_SERVICE_ROOT=$ROOT "$APP" --zapret "$@"; }
ok() { echo "PASS  $*"; pass=$((pass + 1)); }
bad() { echo "FAIL  $*"; fail=$((fail + 1)); }
info() { echo "INFO  $*"; }

# wait_for <seconds> <command...>: polls until the command succeeds.
wait_for() {
    local end=$((SECONDS + $1))
    shift
    while ((SECONDS < end)); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 0.3
    done
    return 1
}

# fetch [seconds] [host]: one HTTPS request from the client namespace; prints the HTTP status (000 when nothing came back).
fetch() {
    local host=${2:-$HOST}
    cns curl -sk --resolve "$host:443:$SERVER_IP" -o /dev/null -w '%{http_code}' --max-time "${1:-8}" "https://$host/" || true
}

reachable() { [ "$(fetch 3)" = 200 ]; }
debug_size() { stat -c %s "$DEBUG" 2>/dev/null || echo 0; }
debug_since() { tail -c +$(($1 + 1)) "$DEBUG" 2>/dev/null || true; }
# snap <name>: keeps the debug log as it is now (it starts afresh every time nfqws2 is restarted).
snap() { cp "$DEBUG" "$EVIDENCE/debug-$1.log" 2>/dev/null || true; }
table_present() { cns nft list table inet tgwsone; }
is_running() {
    local out
    out=$(cli status) || return 1
    [[ $out == *'обход DPI: работает'* ]]
}
child_pid() { cli status | sed -n 's/^процесс: \([0-9]*\).*/\1/p'; }
host_tables() { nft list tables 2>/dev/null | sort; }
alive() { kill -0 "$1" 2>/dev/null; }

# control <json>: a raw request to the control socket (the CLI has no set-exclusions).
control() {
    python3 -I - "$ROOT/control.sock" "$1" <<'PY'
import socket, sys
s = socket.socket(socket.AF_UNIX)
s.settimeout(5)
s.connect(sys.argv[1])
s.sendall(sys.argv[2].encode() + b"\n")
print(s.makefile().readline().strip())
PY
}

diag() {
    echo
    echo "--- диагностика (папка запуска: $WORK, доказательства: $EVIDENCE) ---"
    echo "status:"; cli status 2>&1 | sed 's/^/  /' || true
    echo "service.log (хвост):"; tail -n 15 "$ROOT/logs/service.log" 2>/dev/null | sed 's/^/  /' || true
    echo "zapret.log (хвост):"; tail -n 15 "$ROOT/logs/zapret.log" 2>/dev/null | sed 's/^/  /' || true
    echo "service.out (хвост):"; tail -n 10 "$WORK/service.out" 2>/dev/null | sed 's/^/  /' || true
    echo "debug.log (хвост):"; tail -n 20 "$DEBUG" 2>/dev/null | cut -c1-200 | sed 's/^/  /' || true
}

cleanup() {
    local rc=$?
    set +e
    snap final
    [ -n "$svc_pid" ] && kill "$svc_pid" 2>/dev/null
    [ -n "$srv_pid" ] && kill "$srv_pid" 2>/dev/null
    [ -n "$cap_pid" ] && kill "$cap_pid" 2>/dev/null
    sleep 0.5
    for ns in "$NS_C" "$NS_S"; do
        ip netns pids "$ns" 2>/dev/null | xargs -r kill -9 2>/dev/null
    done
    ip netns delete "$NS_C" 2>/dev/null
    ip netns delete "$NS_S" 2>/dev/null
    # Started with sudo: leave the logs readable by (and removable by) the person who asked for the run.
    [ -n "${SUDO_UID:-}" ] && chown -R "$SUDO_UID:${SUDO_GID:-$SUDO_UID}" "$WORK" 2>/dev/null
    if [ "$fail" -gt 0 ] || [ "${KEEP:-0}" = 1 ]; then
        echo "логи запуска: $WORK"
    else
        rm -rf "$WORK"
    fi
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# ---- setup -------------------------------------------------------------------------------------------------------

# The installed layout: the executable with zapret2/ and lists/ next to it, in a place everybody can read.
src_dir=$(dirname "$APP")
mkdir -p "$WORK/app"
cp -r --no-preserve=ownership,mode "$src_dir/tgwsone" "$src_dir/zapret2" "$src_dir/lists" "$WORK/app/"
chmod -R u=rwX,go=rX "$WORK/app"
chmod 755 "$WORK/app/tgwsone" "$WORK/app/zapret2/nfqws2"
APP=$WORK/app/tgwsone

ip netns delete "$NS_C" 2>/dev/null || true   # left over from an aborted run
ip netns delete "$NS_S" 2>/dev/null || true
host_tables_before=$(host_tables)

ip netns add "$NS_C"
ip netns add "$NS_S"
# The peer is created straight inside the other namespace: neither end ever exists in the host's.
ip -n "$NS_C" link add vc type veth peer name vs netns "$NS_S"
ip -n "$NS_C" addr add 10.77.0.1/24 dev vc
ip -n "$NS_S" addr add "$SERVER_IP/24" dev vs
for ns in "$NS_C" "$NS_S"; do ip -n "$ns" link set lo up; done
ip -n "$NS_C" link set vc up
ip -n "$NS_S" link set vs up

# What the server receives (the packets nfqws2 crafted, as they arrive), for the analysis of any failure.
if command -v tcpdump >/dev/null; then
    ip netns exec "$NS_S" tcpdump -i vs -nn -U -w "$EVIDENCE/server.pcap" tcp port 443 >/dev/null 2>&1 &
    cap_pid=$!
fi

# One certificate serves both names: curl runs with -k.
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 2 \
    -subj "/CN=$HOST" -addext "subjectAltName=DNS:$HOST,DNS:$HOST2" \
    -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
# Started directly under `ip netns exec` (not through a shell function), so that $! is the server's own PID.
start_server() {
    ip netns exec "$NS_S" openssl s_server -accept 443 -cert "$WORK/cert.pem" -key "$WORK/key.pem" -www -quiet >>"$WORK/server.log" 2>&1 &
    srv_pid=$!
}
start_server

if wait_for 10 reachable; then
    ok "без службы запрос проходит напрямую (сеть между пространствами работает)"
else
    bad "без службы запрос не проходит: дальше проверять нечего"
    cat "$WORK/server.log"
    exit 1
fi

# Both test hosts are on the Telegram list. The Cloudflare list must not be empty (an empty list may mean "match
# everything" to nfqws2) and must not match the server: TEST-NET-1 does neither.
printf '# test\n%s\n%s\n' "$HOST" "$HOST2" >"$ROOT/lists/telegram.txt"
printf '# test\n192.0.2.0/24\n' >"$ROOT/lists/cloudflare-ips.txt"

# The debug file is opened by nfqws2 itself; make it writable for the uid it ends up as.
: >"$DEBUG"
chmod 666 "$DEBUG"

# Preflight: can the unprivileged uid read what it will need? (--dry-run checks exactly this, and so does a reload.)
if command -v setpriv >/dev/null; then
    for f in "$ROOT/lists/telegram.txt" "$ROOT/lists/cloudflare-ips.txt" "$WORK/app/zapret2/lua/zapret-lib.lua" \
             "$WORK/app/zapret2/lua/tgws-state.lua" "$WORK/app/zapret2/files/fake/tls_clienthello_vk_com.bin"; do
        if ! setpriv --reuid="$NOBODY" --regid="$NOBODY" --clear-groups cat "$f" >/dev/null 2>&1; then
            echo "uid $NOBODY не может прочитать $f: nfqws2 после сброса прав тоже не сможет" >&2
            namei -m "$f" >&2 || true
            exit 2
        fi
    done
    info "предпроверка: uid $NOBODY читает списки и скрипты Lua"
else
    info "предпроверка пропущена: нет setpriv"
fi

# Started directly under `ip netns exec` (which execs, and so do env and the service): $! is the service's PID.
ip netns exec "$NS_C" env TGWS_SERVICE_ROOT="$ROOT" TGWS_ZAPRET_DEBUG="$DEBUG" "$APP" --service >"$WORK/service.out" 2>&1 &
svc_pid=$!

if ! wait_for 25 is_running; then
    bad "служба не вышла в состояние «работает» за 25 с"
    diag
    exit 1
fi
info "служба работает (pid $svc_pid), процесс обхода: $(child_pid)"
info "nf_conntrack_tcp_be_liberal в пространстве клиента: $(cns sysctl -n net.netfilter.nf_conntrack_tcp_be_liberal 2>/dev/null || echo нет)"

# ---- 1: the nft table follows on / off ----------------------------------------------------------------------------

if table_present >/dev/null 2>&1; then ok "1. таблица inet tgwsone есть, пока служба включена"; else bad "1. таблицы inet tgwsone нет при включённой службе"; fi

cli off >/dev/null
if wait_for 10 sh -c "! ip netns exec $NS_C nft list table inet tgwsone"; then ok "1. после off таблицы нет"; else bad "1. после off таблица осталась"; fi
cli on >/dev/null
if wait_for 15 is_running && table_present >/dev/null 2>&1; then ok "1. после on таблица снова есть"; else bad "1. после on таблицы нет или служба не работает"; fi

# ---- 2: a request goes through and the ClientHello is desynchronised ----------------------------------------------

mark=$(debug_size)
code=$(fetch 8)
sleep 1
new=$(debug_since "$mark")
echo "$new" >"$EVIDENCE/check2-new.log"
if [ "$code" = 200 ]; then ok "2. запрос через обход вернул 200"; else bad "2. запрос через обход вернул $code"; fi
if grep -Eq "plan_instance_execute: calling '(fake|multidisorder)" <<<"$new"; then
    ok "2. в журнале отладки видно применение стратегии к ClientHello (fake / multidisorder)"
else
    bad "2. в журнале отладки нет следов fake / multidisorder для этого соединения"
fi

# ---- 3: circular rotates after failures, and the UI file shows it -------------------------------------------------

mark=$(debug_size)
sns nft -f - <<'RULES'
table inet tgwsdrop {
  chain out {
    type filter hook output priority 0; policy accept;
    tcp sport 443 tcp flags & (syn | ack) != (syn | ack) drop
  }
}
RULES
for _ in 1 2 3; do fetch 5 >/dev/null; done
sns nft delete table inet tgwsdrop
# openssl s_server serves one connection at a time, and it is still stuck in the handshakes whose replies were just
# dropped: the next request would queue behind them for many seconds and look like a failure of whatever strategy
# runs (the server's own late replies would also show up as traffic of an old flow in the debug log). Start a new one.
kill "$srv_pid" 2>/dev/null || true
wait "$srv_pid" 2>/dev/null || true
start_server
if ! wait_for 10 sh -c "ip netns exec $NS_S ss -ltn | grep -q ':443 '"; then bad "3. тестовый сервер не поднялся заново"; fi
new=$(debug_since "$mark")
echo "$new" >"$EVIDENCE/check3-new.log"
if grep -q "circular: rotate strategy to 2" <<<"$new"; then
    ok "3. после трёх неудачных соединений circular переключился на стратегию 2"
else
    bad "3. в журнале нет «circular: rotate strategy to 2»"
fi
if wait_for 10 sh -c "TGWS_SERVICE_ROOT='$ROOT' '$APP' --zapret status | grep -Eq '$HOST +split '"; then
    ok "3. status показывает $HOST → split (файл состояния Авто обновился)"
else
    bad "3. status не показывает $HOST → split в течение 10 с"
    cli status | sed 's/^/      /' || true
fi
snap after-check3

# The strategy auto has just rotated onto has to work against an ordinary server too.
mark=$(debug_size)
code=$(fetch 8)
sleep 1
debug_since "$mark" >"$EVIDENCE/check3b-new.log"
if [ "$code" = 200 ]; then ok "3. после ротации запрос со стратегией split проходит"; else bad "3. после ротации запрос со стратегией split вернул $code"; fi
snap after-check3b

# ---- 6: every built-in strategy against an ordinary TLS server -----------------------------------------------------
# No DPI here, so a strategy that breaks a plain connection is a strategy that cannot be used at all.

for id in fake-tls split split-stealth aggressive; do
    cli use "$id" >/dev/null
    wait_for 15 is_running >/dev/null || true
    mark=$(debug_size)
    code=$(fetch 8)
    sleep 1
    debug_since "$mark" >"$EVIDENCE/strategy-$id.log"
    snap "strategy-$id"
    if [ "$code" = 200 ]; then ok "6. стратегия $id: запрос к обычному серверу проходит"; else bad "6. стратегия $id: запрос к обычному серверу вернул $code"; fi
done
cli use auto >/dev/null
wait_for 15 is_running >/dev/null || true

# ---- 5 (extra): a host is excluded and included again without restarting the child ---------------------------------
# Judged per host: circular logs "host record key 'autostate.tgws.<host>'" for every packet of a host whose profile
# is active, and a connection that was already running keeps its profile whatever the lists say now, so a time window
# on the whole log would also catch such leftovers of an earlier host.

if command -v python3 >/dev/null; then
    key="autostate.tgws.$HOST2'"
    mark=$(debug_size)
    code=$(fetch 8 "$HOST2")
    sleep 1
    new=$(debug_since "$mark")
    echo "$new" >"$EVIDENCE/check5-before.log"
    if [ "$code" = 200 ] && grep -Fq "$key" <<<"$new"; then
        ok "5. до исключения $HOST2 идёт через обход: 200, стратегия применяется"
    else
        bad "5. до исключения $HOST2: код $code, следов обработки узла в журнале: $(grep -Fc "$key" <<<"$new")"
    fi

    pid_before=$(child_pid)
    control "{\"cmd\":\"set-exclusions\",\"hosts\":[\"$HOST2\"],\"ips\":[]}" >/dev/null
    sleep 1.5
    mark=$(debug_size)
    code=$(fetch 8 "$HOST2")
    sleep 1
    new=$(debug_since "$mark")
    echo "$new" >"$EVIDENCE/check5-excluded.log"
    snap after-check5-excluded
    if [ "$code" = 200 ] && ! grep -Fq "$key" <<<"$new" && [ "$(child_pid)" = "$pid_before" ]; then
        ok "5. исключённый узел идёт напрямую: 200, стратегия не применялась, процесс не перезапускался"
    else
        bad "5. исключённый узел: код $code, процесс $pid_before → $(child_pid), следов обработки узла в журнале: $(grep -Fc "$key" <<<"$new")"
    fi

    control '{"cmd":"set-exclusions","hosts":[],"ips":[]}' >/dev/null
    sleep 1.5
    mark=$(debug_size)
    code=$(fetch 8 "$HOST2")
    sleep 1
    new=$(debug_since "$mark")
    echo "$new" >"$EVIDENCE/check5-included.log"
    if [ "$code" = 200 ] && grep -Fq "$key" <<<"$new"; then
        ok "5. после снятия исключения стратегия снова применяется"
    else
        bad "5. после снятия исключения: код $code, следов обработки узла в журнале: $(grep -Fc "$key" <<<"$new")"
    fi
else
    info "5. пропущено: нет python3 для запроса set-exclusions к сокету"
fi

# ---- 4: the service and its child die without cleanup: the queue's "bypass" keeps traffic flowing -----------------

nfq_pid=$(child_pid)
kill -9 "$svc_pid" 2>/dev/null || true   # the service first, so nothing restarts the child
wait "$svc_pid" 2>/dev/null || true
if alive "$svc_pid" || cli status >/dev/null 2>&1; then
    bad "4. служба пережила SIGKILL или всё ещё отвечает: проверка bypass недействительна"
fi
svc_pid=
if [ -n "$nfq_pid" ]; then kill -9 "$nfq_pid" 2>/dev/null || true; fi
if wait_for 5 sh -c "! kill -0 $nfq_pid"; then
    info "служба (SIGKILL) и nfqws2 ($nfq_pid) завершены, канал управления молчит"
else
    bad "4. nfqws2 ($nfq_pid) не завершился после SIGKILL"
fi
if table_present >/dev/null 2>&1; then
    info "правила остались без службы и без nfqws2, как и должно быть при аварийном завершении"
else
    info "таблица исчезла вместе со службой (неожиданно, но трафик это не нарушает)"
fi
code=$(fetch 8)
if [ "$code" = 200 ]; then ok "4. без службы и без nfqws2 запрос проходит (bypass в очереди)"; else bad "4. без службы и без nfqws2 запрос вернул $code"; fi
cns nft delete table inet tgwsone 2>/dev/null || true

# ---- the host stayed as it was ------------------------------------------------------------------------------------

if [ "$(host_tables)" = "$host_tables_before" ] && ! nft list table inet tgwsone >/dev/null 2>&1; then
    ok "хост не затронут: список таблиц nft прежний, таблицы tgwsone в основном пространстве нет"
else
    bad "список таблиц nft в основном пространстве изменился или в нём появилась таблица tgwsone"
fi

echo
echo "итог: пройдено $pass, не пройдено $fail"
if [ "$fail" -gt 0 ]; then
    diag
    exit 1
fi
