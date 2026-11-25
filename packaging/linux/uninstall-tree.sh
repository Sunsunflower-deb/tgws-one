#!/usr/bin/env bash
# Убирает ровно то, что положил install-tree.sh; каталог — только если в нём больше ничего нет.
#
#   uninstall-tree.sh [--desktop-only] <destdir> <prefix>
#
# Что создала сама служба (/var/lib/tgwsone, /var/log/tgwsone) и файлы администратора (/etc/tgwsone) — не наши, их тут не
# трогают. Включённую службу сначала нужно остановить: systemctl disable --now tgwsone-zapret
set -euo pipefail

desktop_only=0
if [ "${1:-}" = "--desktop-only" ]; then
    desktop_only=1
    shift
fi
if [ $# -ne 2 ]; then
    echo "использование: $0 [--desktop-only] <destdir> <prefix>" >&2
    exit 64
fi
dest=$1
prefix=$2

root="$dest$prefix"
libdir="$root/lib/tgwsone"
bindir="$root/bin"
unitdir="$root/lib/systemd/system"
appsdir="$root/share/applications"
icondir="$root/share/icons/hicolor/256x256/apps"
unit=tgwsone-zapret.service
native_libs=(libSkiaSharp.so libHarfBuzzSharp.so)   # те же, что в install-tree.sh

if [ "$desktop_only" -eq 0 ]; then
    rm -f "$libdir/tgwsone" "$bindir/tgwsone" "$unitdir/$unit"
    for lib in "${native_libs[@]}"; do rm -f "$libdir/$lib"; done
    rm -rf "$libdir/zapret2" "$libdir/lists"
    rmdir "$libdir" "$unitdir" 2>/dev/null || true
fi

rm -f "$appsdir/tgwsone.desktop" "$icondir/tgwsone.png"
rmdir "$icondir" "$root/share/icons/hicolor/256x256" 2>/dev/null || true
