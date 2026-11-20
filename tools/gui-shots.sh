#!/usr/bin/env bash
# Visual checkpoint of the SP3 window: runs the real app on a private, headless X server and takes a screenshot of every page,
# dialog, the first-run wizard and the stopped / connected / error states, in the light and the dark theme.
#
#   tools/gui-shots.sh [output dir]        (default: /Development/personal/dump/tgws/screens/sp3)
#   tools/gui-shots.sh [output dir] <scene>...   only the named scenes (see the list in the SCENES array below)
#
# Nothing here touches the user's desktop or profile:
#   - the X server is Xvnc on :99 listening on localhost only (a viewer may attach with `vncviewer localhost:99`); there is no
#     window manager in it; the script refuses to run if :99 is already taken; every xdotool/import call names :99;
#   - each app runs inside `dbus-run-session` on a bus of its own (a minimal config: no service directories, so nothing can be
#     activated on it): its notifications go nowhere, the tray check sees no watcher, no portal answers;
#   - HOME and all five XDG directories are fresh per scene (the wizard's default-on autostart writes its entry into that
#     config directory, not the real one), plus its own data directory, user-level bypass service (FakeZapret, no nftables) and
#     port; the real engine is used for the proxy; everything the scene started is stopped at its end;
#   - everything that hands off to the desktop (xdg-open, gio, kde-open, exo-open, notify-send, …) and everything that elevates
#     (pkexec, sudo) is a stand-in first in PATH that only records the call;
#   - tools/leak-check.sh compares the real profile with a snapshot taken before the first scene after every scene, and the
#     script stops with a loud message at the first difference.
# Needs: dotnet, Xvnc, xdotool, import (ImageMagick), python3, and a build of the app (the script builds it and FakeZapret).
set -uo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-/Development/personal/dump/tgws/screens/sp3}
shift || true
work=${GUI_SHOTS_WORK:-/Development/personal/dump/tgws/gui-shots-work}

app_dll=$repo/src/TgwsOne/bin/Debug/net10.0/TgwsOne.dll
fake_zapret=$repo/tests/FakeZapret/bin/Debug/net10.0/FakeZapret
engine=$repo/out/engine/linux-x64/tgws-engine

[ -x "$engine" ] || { echo "no engine at $engine: run make engine first" >&2; exit 1; }
if [ -e /tmp/.X11-unix/X99 ]; then
    echo "display :99 is already in use: something else is running there" >&2
    exit 1
fi
for tool in Xvnc xdotool import python3 dotnet dbus-run-session; do
    command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 1; }
done

echo "building…"
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS dotnet build "$repo/src/TgwsOne" -v q --nologo >/dev/null || exit 1
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS dotnet build "$repo/tests/FakeZapret" -v q --nologo >/dev/null || exit 1

mkdir -p "$out" "$work"
rm -rf "${work:?}"/*

# Whatever hands off to the real desktop is replaced, first in PATH of every app the script starts, by something that only
# records the call (the app opens links and folders through xdg-open: on this machine a running Telegram takes a tg:// link from
# a second process whatever DISPLAY says). Whatever elevates (saving a custom strategy asks pkexec, then sudo) answers «no».
mkdir -p "$work/bin"
export STUB_LOG=$work/stubs.log
for handoff in xdg-open gio gvfs-open gnome-open kde-open kde-open5 kde-open6 exo-open xdg-email sensible-browser x-www-browser notify-send; do
    cat >"$work/bin/$handoff" <<'STUB'
#!/bin/sh
printf '%s %s\n' "$(basename "$0")" "$*" >>"${STUB_LOG:-/dev/null}"
exit 0
STUB
    chmod +x "$work/bin/$handoff"
done
for elevate in pkexec sudo; do
    cat >"$work/bin/$elevate" <<'STUB'
#!/bin/sh
printf '%s %s\n' "$(basename "$0")" "$*" >>"${STUB_LOG:-/dev/null}"
exit 1
STUB
    chmod +x "$work/bin/$elevate"
done
export PATH="$work/bin:$PATH"

# One private session bus per app, from this config: no <servicedir>, so a service file on this machine (dunst, a portal) cannot
# be started on it.
cat >"$work/bus.conf" <<'CONF'
<!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
<busconfig>
  <type>session</type>
  <listen>unix:tmpdir=/tmp</listen>
  <auth>EXTERNAL</auth>
  <policy context="default"><allow send_destination="*" eavesdrop="true"/><allow eavesdrop="true"/><allow own="*"/></policy>
</busconfig>
CONF

# The real profile as it is before anything runs; compared with after every scene.
real_home=$HOME
leak_check() {
    LEAK_CHECK_HOME=$real_home "$repo/tools/leak-check.sh" "$@"
}
leak_check snapshot "$work/leak.before" || exit 1

# ----- the private display -----
Xvnc :99 -localhost -SecurityTypes None -geometry 1280x800 -depth 24 >"$work/xvnc.log" 2>&1 &
xvnc_pid=$!
for _ in $(seq 100); do
    [ -S /tmp/.X11-unix/X99 ] && break
    kill -0 "$xvnc_pid" 2>/dev/null || { echo "Xvnc did not start, see $work/xvnc.log" >&2; exit 1; }
    sleep 0.1
done
[ -S /tmp/.X11-unix/X99 ] || { echo "Xvnc did not come up, see $work/xvnc.log" >&2; exit 1; }
export DISPLAY=:99

app_pid=""
holder_pid=""
root=""
cleanup_scene() {
    [ -n "$holder_pid" ] && kill "$holder_pid" 2>/dev/null
    if [ -n "$app_pid" ]; then
        kill -TERM -- "-$app_pid" 2>/dev/null   # the whole group: dbus-run-session, its bus, the app
        wait "$app_pid" 2>/dev/null
    fi
    if [ -n "$root" ]; then
        # the engine is detached and outlives the app; the CLI of the same data dir stops it (and must never see a display)
        env -u DISPLAY -u WAYLAND_DISPLAY dotnet "$app_dll" --stop >/dev/null 2>&1
        service stop
    fi
    app_pid=""
    holder_pid=""
    return 0
}
cleanup_all() {
    cleanup_scene
    kill "$xvnc_pid" 2>/dev/null
    wait "$xvnc_pid" 2>/dev/null
    return 0
}
trap cleanup_all EXIT

# ----- the user-level bypass service of a scene -----
service() {
    case "$1" in
    start)
        env -u DISPLAY -u WAYLAND_DISPLAY \
            TGWS_NO_NFT=1 TGWS_ZAPRET_EXE="$fake_zapret" TGWS_ZAPRET_DIR="$TGWS_SERVICE_ROOT/bundle" \
            FAKEZ_CTL="$TGWS_SERVICE_ROOT/fakez.ctl" FAKEZ_ARGS="$TGWS_SERVICE_ROOT/fakez.args" FAKEZ_HUP="$TGWS_SERVICE_ROOT/fakez.hup" \
            dotnet "$app_dll" --service >"$TGWS_SERVICE_ROOT/service.out" 2>&1 &
        echo $! >"$TGWS_SERVICE_ROOT/service.pid"
        for _ in $(seq 100); do
            [ -S "$TGWS_SERVICE_ROOT/control.sock" ] && break
            sleep 0.1
        done
        # «Что выбрал Авто»: the table the real Lua script writes (host, strategy number, failures); the service deletes the
        # file on every start it applies, so it goes in only now.
        mkdir -p "$TGWS_SERVICE_ROOT/state/zapret-state"
        printf 'kws2.web.telegram.org\t2\t1\nkws4.web.telegram.org\t1\t0\nkws1.web.telegram.org\t2\t0\nkws3.web.telegram.org\t3\t5\n' \
            >"$TGWS_SERVICE_ROOT/state/zapret-state/autostate.tsv"
        ;;
    stop)
        if [ -f "${TGWS_SERVICE_ROOT:-/nonexistent}/service.pid" ]; then
            kill "$(cat "$TGWS_SERVICE_ROOT/service.pid")" 2>/dev/null
            rm -f "$TGWS_SERVICE_ROOT/service.pid"
        fi
        # anchored: an unanchored pattern also matches the shell that mentions the path
        pkill -f "^${fake_zapret}( |\$)" 2>/dev/null
        ;;
    esac
    return 0
}

# ----- one scene = one fresh app on one fresh profile -----
port_base=18500
scene_no=0

# top-left corner of a window: prints "X Y"
origin_of() { xdotool getwindowgeometry --shell "$1" | awk -F= '/^X=/{x=$2} /^Y=/{y=$2} END{print x, y}'; }

# position of the main window (title exactly «TgwsOne»): sets wx wy
main_geometry() {
    local id
    id=$(xdotool search --onlyvisible --name '^TgwsOne$' 2>/dev/null | head -1)
    [ -n "$id" ] || return 1
    read -r wx wy < <(origin_of "$id")
}

# position of the newest dialog: the last visible window of the app's class that is not the main one (found by class, not by
# title: a regular expression over Cyrillic text does not match in every locale). Sets dx dy.
dialog_geometry() {
    local main id
    main=$(xdotool search --onlyvisible --name '^TgwsOne$' 2>/dev/null | head -1)
    id=$(xdotool search --onlyvisible --class 'TgwsOne' 2>/dev/null | grep -v -x "${main:-none}" | tail -1)
    [ -n "$id" ] || return 1
    read -r dx dy < <(origin_of "$id")
}

# clicks at a point given relative to the main window / to the last dialog
click_main() { main_geometry && xdotool mousemove $((wx + $1)) $((wy + $2)) && sleep 0.3 && xdotool click 1 && sleep "${3:-1}"; }
click_dialog() { xdotool mousemove $((dx + $1)) $((dy + $2)) && sleep 0.3 && xdotool click 1 && sleep "${3:-1}"; }
hover_main() { main_geometry && xdotool mousemove $((wx + $1)) $((wy + $2)) && sleep "${3:-1}"; }
type_text() { xdotool type --delay 40 -- "$1" && sleep 0.5; }

# scrolls the page under the pointer by <notches> wheel clicks (positive: down)
scroll_main() {
    main_geometry || return 1
    xdotool mousemove $((wx + 500)) $((wy + 300))
    sleep 0.3
    local i
    for ((i = 0; i < $1; i++)); do xdotool click 5; sleep 0.05; done
    sleep 0.8
}

# the sidebar: the five sections, 42 px apart
nav() {
    local y
    case "$1" in home) y=75 ;; bypass) y=117 ;; diagnostics) y=159 ;; logs) y=201 ;; settings) y=243 ;; esac
    click_main 100 "$y" 1.5
}

# SHOT_KEEP_POINTER=1 leaves the pointer where it is (a hovered button, an open menu)
shot() {
    [ "${SHOT_KEEP_POINTER:-0}" = 1 ] || { xdotool mousemove 8 8; sleep 0.4; }
    import -display :99 -window root "$out/$theme-$1.png" && echo "  $theme-$1.png"
}

# scene <name> <setup function> <actions function>
#   setup runs before the app starts (environment, INI, service); actions run once the window is up
run_scene() {
    local name=$1 setup=$2 actions=$3
    scene_no=$((scene_no + 1))
    for theme in light dark; do
        root=$work/$name-$theme
        mkdir -p "$root/data" "$root/service" "$root/xdg" "$root/xdg-run"
        chmod 700 "$root/xdg-run"
        printf '[proxy]\nport=%s\n' "$((port_base + scene_no))" >"$root/data/tgwsone.ini"
        [ "${WIZARD:-0}" = 1 ] || printf '[app]\nfirstrun=done\n' >>"$root/data/tgwsone.ini"

        mkdir -p "$root/home" "$root/xdg-config" "$root/xdg-state" "$root/xdg-data" "$root/xdg-cache"
        export TGWS_DATA_DIR=$root/data TGWS_SERVICE_ROOT=$root/service TGWS_ENGINE=$engine TGWS_THEME=$theme
        export HOME=$root/home XDG_CONFIG_HOME=$root/xdg-config XDG_STATE_HOME=$root/xdg-state
        export XDG_DATA_HOME=$root/xdg-data XDG_CACHE_HOME=$root/xdg-cache XDG_RUNTIME_DIR=$root/xdg-run
        unset DBUS_SESSION_BUS_ADDRESS TGWS_FAKE_PROBES
        "$setup"

        # setsid: its own process group, so that the end of the scene can take the bus down with the app
        setsid dbus-run-session --config-file="$work/bus.conf" -- dotnet "$app_dll" >"$root/app.log" 2>&1 &
        app_pid=$!
        for _ in $(seq 150); do
            xdotool search --onlyvisible --name 'TgwsOne' >/dev/null 2>&1 && break
            kill -0 "$app_pid" 2>/dev/null || { echo "  $name ($theme): the app exited early, see $root/app.log" >&2; break; }
            sleep 0.1
        done
        sleep 2.5
        "$actions"
        cleanup_scene
        unset TGWS_THEME TGWS_FAKE_PROBES WIZARD
        if ! leak_check verify "$work/leak.before"; then
            echo "scene $name ($theme) leaked into the real profile: stopping" >&2
            exit 1
        fi
    done
}

# ----- setups -----
setup_service() { service start; }
setup_plain() { :; }
setup_no_engine() { export TGWS_ENGINE=/nonexistent/tgws-engine; service start; }
# the engine dies 100 ms after every start: the service restarts it with a backoff («перезапуск после сбоя…»)
setup_bypass_crash() {
    printf 'crash_after_ms=100\n' >"$TGWS_SERVICE_ROOT/fakez.ctl"
    service start
}
# the engine rejects its command line (--dry-run fails): the service has nothing to run and says «error» at once
setup_bypass_error() {
    printf 'dryrun_fail=1\n' >"$TGWS_SERVICE_ROOT/fakez.ctl"
    service start
}
setup_fake_probes() { export TGWS_FAKE_PROBES=1; service start; }
setup_lan() {
    local host=0.0.0.0
    printf '[proxy]\nport=%s\nhost=%s\n[app]\nfirstrun=done\n' "$((port_base + scene_no))" "$host" >"$TGWS_DATA_DIR/tgwsone.ini"
    service start
}
setup_wizard_no_engine() { setup_wizard; export TGWS_ENGINE=/nonexistent/tgws-engine; }
setup_wizard() { WIZARD=1; export WIZARD; printf '[proxy]\nport=%s\n' "$((port_base + scene_no))" >"$TGWS_DATA_DIR/tgwsone.ini"; service start; }

# ----- actions (coordinates are relative to the window; see the screenshots they produce) -----
connect() { click_main 335 164 4; }

act_home_stopped() { shot home-stopped; }
act_home_connected() { connect; shot home-connected; }
act_home_telegram() {
    connect
    # a client connected to the proxy port: what Telegram looks like to the proxy
    python3 -I -c "import socket,time; s=socket.create_connection(('127.0.0.1', $((port_base + scene_no)))); time.sleep(12)" &
    holder_pid=$!
    sleep 5
    shot home-telegram
}
act_home_error() { connect; shot home-error; }
act_home_menu() { connect; click_main 607 162 1; SHOT_KEEP_POINTER=1 shot home-menu; }

act_bypass_running() { nav bypass; sleep 1.5; shot bypass; scroll_main 15; shot bypass-bottom; }
act_bypass_restarting() { sleep 3; nav bypass; sleep 1.5; shot bypass-restarting; }
act_bypass_error() { sleep 3; nav bypass; sleep 1.5; shot bypass-error; }

# the pointer inside the newest dialog (clicks work without a window manager; keys do not reliably: nothing gives the dialog focus)
pointer_into_dialog() { dialog_geometry && xdotool mousemove $((dx + ${1:-200})) $((dy + ${2:-150})) && sleep 0.4; }

act_bypass_exclusions() { nav bypass; sleep 1; scroll_main 15; click_main 788 450 1; shot bypass-exclusions; scroll_main 15; shot bypass-exclusions-bottom; }

act_finder() {
    nav bypass
    click_main 338 269 2
    pointer_into_dialog
    SHOT_KEEP_POINTER=1 shot finder-confirm
    # «Начать подбор»: clicked, not entered (without a window manager nothing hands the dialog the keyboard focus reliably)
    click_dialog 514 100 1.5
    SHOT_KEEP_POINTER=1 shot finder-running-1
    sleep 3
    SHOT_KEEP_POINTER=1 shot finder-running-2
    sleep 13
    SHOT_KEEP_POINTER=1 shot finder-done
}

act_custom_editor() {
    nav bypass
    scroll_main 15
    click_main 793 537 2
    pointer_into_dialog
    SHOT_KEEP_POINTER=1 shot custom-editor
}

act_settings_confirm() {
    nav settings
    sleep 1
    click_main 381 440 1.5    # «Новый…» (a new secret): asks first
    shot settings-confirm
}

act_settings_advanced() {
    nav settings
    sleep 1
    click_main 315 515 1
    scroll_main 15
    shot settings-advanced
}

act_qr() {
    click_main 443 164 1      # «···»
    click_main 522 228 2      # «QR для телефона»
    pointer_into_dialog
    SHOT_KEEP_POINTER=1 shot qr
}

act_wizard() {
    sleep 3
    shot wizard-1
    dialog_geometry || return 0
    click_dialog 477 387 1.5   # «Далее»
    shot wizard-2
    click_dialog 477 387 1.5   # «Пропустить»
    shot wizard-3
    click_dialog 477 387 2     # «Готово»
    shot wizard-finished
    echo "  autostart entry written to the scene's own config dir: $(find "$XDG_CONFIG_HOME/autostart" -type f -printf '%f ' 2>/dev/null)"
    echo "  first-run flag in the scene's ini: $(grep -c 'firstrun=done' "$TGWS_DATA_DIR/tgwsone.ini")"
}

act_wizard_telegram() {
    sleep 3
    dialog_geometry || return 0
    click_dialog 477 387 1.5   # «Далее»
    python3 -I -c "import socket,time; s=socket.create_connection(('127.0.0.1', $((port_base + scene_no)))); time.sleep(12)" &
    holder_pid=$!
    sleep 5
    shot wizard-2-telegram
}

act_wizard_failure() { sleep 4; shot wizard-fail; }

act_diagnostics() { nav diagnostics; sleep 1; shot diagnostics-before; click_main 790 46 9; shot diagnostics; }
act_logs() {
    connect                   # so that the proxy's own log has lines
    nav logs
    sleep 1.5
    shot logs
    click_main 396 91 1.5     # «Прокси»
    shot logs-proxy
    click_main 486 91 1.5     # «Обход DPI»
    shot logs-service
}
act_settings() { nav settings; sleep 1; shot settings; }


SCENES=(
    "home-stopped setup_service act_home_stopped"
    "home-connected setup_service act_home_connected"
    "home-telegram setup_service act_home_telegram"
    "home-error setup_no_engine act_home_error"
    "home-menu setup_service act_home_menu"
    "bypass setup_service act_bypass_running"
    "bypass-restarting setup_bypass_crash act_bypass_restarting"
    "bypass-error setup_bypass_error act_bypass_error"
    "bypass-exclusions setup_service act_bypass_exclusions"
    "diagnostics setup_service act_diagnostics"
    "logs setup_service act_logs"
    "settings setup_service act_settings"
    "finder setup_fake_probes act_finder"
    "custom-editor setup_service act_custom_editor"
    "settings-confirm setup_service act_settings_confirm"
    "settings-advanced setup_service act_settings_advanced"
    "qr setup_lan act_qr"
    "wizard setup_wizard act_wizard"
    "wizard-telegram setup_wizard act_wizard_telegram"
    "wizard-failure setup_wizard_no_engine act_wizard_failure"
)

want=("$@")
for entry in "${SCENES[@]}"; do
    read -r name setup actions <<<"$entry"
    if [ ${#want[@]} -gt 0 ] && ! printf '%s\n' "${want[@]}" | grep -qx "$name"; then continue; fi
    echo "scene $name"
    run_scene "$name" "$setup" "$actions"
done
leak_check verify "$work/leak.before" || exit 1
echo "leak check: the real profile is exactly as it was before the run"
if [ -s "$STUB_LOG" ]; then
    echo "calls that were handed to stand-ins (nothing reached the desktop):"
    sed 's/^/  /' "$STUB_LOG"
fi
echo "done: $out"
