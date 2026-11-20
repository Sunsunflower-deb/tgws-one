#!/usr/bin/env bash
# Did a test run or a GUI run touch the real user profile? Takes a snapshot of the places TgwsOne would write to if a run were not
# sandboxed (the autostart entry, its config, state and data directories) and compares a later state with it. Anything that
# appeared, changed or vanished is a leak and fails loudly. What was there before the run (an installed, used TgwsOne) is not
# a leak by itself, so the comparison is with the snapshot, not with "nothing".
#
#   tools/leak-check.sh snapshot <file>      record the current state
#   tools/leak-check.sh verify   <file>      exit 1 and say what differs if it is not the recorded state
#
# LEAK_CHECK_HOME overrides the home directory to look at (a script that runs the app with another HOME passes the real one).
set -uo pipefail

home=${LEAK_CHECK_HOME:-$HOME}
places=(
    "$home/.config/autostart/tgwsone.desktop"
    "$home/.config/tgwsone"
    "$home/.local/state/tgwsone"
    "$home/.local/share/tgwsone"
)

state() {
    for place in "${places[@]}"; do
        # path, modification time, size: a rewritten file differs even if it kept its name
        if [ -e "$place" ]; then find "$place" -printf '%p %T@ %s\n'; fi
    done | sort
}

case "${1:-}" in
snapshot)
    state >"${2:?snapshot file}"
    ;;
verify)
    before=${2:?snapshot file}
    [ -f "$before" ] || { echo "leak-check: no snapshot at $before" >&2; exit 2; }
    if ! diff=$(diff <(cat "$before") <(state)); then
        {
            echo
            echo "################################################################"
            echo "LEAK: a run touched the real profile ($home)."
            echo "  < was    > is now"
            printf '%s\n' "$diff" | awk '{print "  " $0}'
            echo "################################################################"
        } >&2
        exit 1
    fi
    ;;
*)
    echo "usage: $0 snapshot|verify <file>" >&2
    exit 2
    ;;
esac
