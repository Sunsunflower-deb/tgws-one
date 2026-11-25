#!/usr/bin/env bash
# Раскладка TgwsOne по файловой системе — единственная реализация: её вызывают `make install`, install.sh из архива
# и package() в PKGBUILD.
#
#   install-tree.sh [--desktop-only] <destdir> <prefix> <srcdir>
#
# <srcdir> содержит tgwsone (движок прокси встроен), libSkiaSharp.so, libHarfBuzzSharp.so, zapret2/, lists/ и
# packaging/linux/{tgwsone-zapret.service.in,tgwsone.desktop,tgwsone.png}. <destdir> — корень для упаковщиков (пусто для
# настоящей установки), <prefix> — например /usr или /usr/local. Службу не включает и systemctl не вызывает.
# --desktop-only кладёт только пункт меню и значок: для них собирать ничего не нужно.
set -euo pipefail

desktop_only=0
if [ "${1:-}" = "--desktop-only" ]; then
    desktop_only=1
    shift
fi
if [ $# -ne 3 ]; then
    echo "использование: $0 [--desktop-only] <destdir> <prefix> <srcdir>" >&2
    exit 64
fi
dest=$1
prefix=$2
src=$3

root="$dest$prefix"
libdir="$root/lib/tgwsone"
bindir="$root/bin"
unitdir="$root/lib/systemd/system"
appsdir="$root/share/applications"
icondir="$root/share/icons/hicolor/256x256/apps"
pkg="$src/packaging/linux"
unit=tgwsone-zapret.service

# Родные библиотеки интерфейса лежат рядом с исполняемым файлом, а не внутри него: распаковывать их в домашний каталог
# пришлось бы каждому пользователю, а служба от root на это рассчитывать не может.
native_libs=(libSkiaSharp.so libHarfBuzzSharp.so)

need() {
    [ -e "$1" ] || { echo "в $src нет ${1#"$src"/}: сначала соберите проект (make zapret2 build)" >&2; exit 1; }
}

install_desktop() {
    install -d "$appsdir" "$icondir"
    install -m 0644 "$pkg/tgwsone.desktop" "$appsdir/tgwsone.desktop"
    install -m 0644 "$pkg/tgwsone.png" "$icondir/tgwsone.png"
}

need "$pkg/tgwsone.desktop"
need "$pkg/tgwsone.png"
if [ "$desktop_only" -eq 1 ]; then
    install_desktop
    exit 0
fi

need "$src/tgwsone"
for lib in "${native_libs[@]}"; do need "$src/$lib"; done
need "$src/zapret2/nfqws2"
need "$src/lists"
need "$pkg/$unit.in"

install -d "$libdir" "$bindir" "$unitdir"
install -m 0755 "$src/tgwsone" "$libdir/tgwsone"
for lib in "${native_libs[@]}"; do install -m 0755 "$src/$lib" "$libdir/$lib"; done
ln -sf ../lib/tgwsone/tgwsone "$bindir/tgwsone"

# Пакет zapret2 копируется с --no-preserve=ownership, а не `cp -a`: после установки от root файлы должны принадлежать root, а не
# тому, кто собирал, — nfqws2 и Lua-скрипты исполняются от root. Завершающая «/.» работает и тогда, когда <srcdir>/zapret2 — ссылка.
rm -rf "$libdir/zapret2" "$libdir/lists"
mkdir -p "$libdir/zapret2" "$libdir/lists"
cp -r --no-preserve=ownership,mode "$src/zapret2/." "$libdir/zapret2/"
cp -r --no-preserve=ownership,mode "$src/lists/." "$libdir/lists/"
chmod -R u=rwX,go=rX "$libdir/zapret2" "$libdir/lists"
chmod 0755 "$libdir/zapret2/nfqws2"

# Путь в ExecStart — тот, по которому файл будет лежать в работающей системе, то есть без <destdir>.
escaped_prefix=$(printf '%s' "$prefix" | sed 's/[\\&|]/\\&/g')
sed "s|@PREFIX@|$escaped_prefix|g" "$pkg/$unit.in" > "$unitdir/$unit"
chmod 0644 "$unitdir/$unit"

install_desktop
