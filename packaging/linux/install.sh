#!/usr/bin/env bash
# Установка TgwsOne из распакованного архива: программа, служба обхода DPI (systemd), пункт меню и значок.
# Запускать от root:   sudo ./install.sh
#
#   PREFIX=/usr/local   куда ставить (по умолчанию /usr/local)
#   DESTDIR=            корень для упаковщиков; systemctl при этом не вызывается, если SYSTEMCTL не задан явно
#   SYSTEMCTL=systemctl чем управлять службой (для проверки без root: SYSTEMCTL=echo)
#   TGWS_TEST=1         не требовать root (только для проверки во временном каталоге)
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
prefix=${PREFIX:-/usr/local}
dest=${DESTDIR:-}
unit=tgwsone-zapret

if [ "${TGWS_TEST:-0}" != 1 ] && [ "$(id -u)" -ne 0 ]; then
    echo "нужны права root: sudo $0" >&2
    exit 1
fi

# Служба уже стояла — значит, это обновление, и работающую нужно перезапустить, чтобы она взяла новые файлы.
upgrade=0
[ -e "$dest$prefix/lib/systemd/system/$unit.service" ] && upgrade=1

"$here/packaging/linux/install-tree.sh" "$dest" "$prefix" "$here"

if ! command -v nft >/dev/null 2>&1; then
    echo "предупреждение: не найден nft (пакет nftables) — без него служба не сможет включить обход" >&2
fi

service="не запущена"
if [ -n "$dest" ] && [ -z "${SYSTEMCTL:-}" ]; then
    echo "DESTDIR задан: файлы разложены, службу не включаю (systemctl daemon-reload && systemctl enable --now $unit — на целевой системе)"
else
    systemctl_cmd=${SYSTEMCTL:-systemctl}
    if command -v "$systemctl_cmd" >/dev/null 2>&1; then
        "$systemctl_cmd" daemon-reload
        "$systemctl_cmd" enable --now "$unit"
        [ "$upgrade" -eq 1 ] && "$systemctl_cmd" restart "$unit"
        service="включена и запущена"
    else
        echo "предупреждение: systemd не найден — файлы установлены, службу запустите сами: $prefix/lib/tgwsone/tgwsone --service" >&2
    fi
fi

cat <<EOF

TgwsOne установлен в $dest$prefix
  программа       $prefix/bin/tgwsone, пункт «TgwsOne» в меню приложений
  служба обхода   $unit (systemd): $service
Обычному пользователю права администратора не нужны: приложение управляет службой через локальный сокет.
Удалить: sudo ./uninstall.sh [--purge] из этого же каталога (с тем же PREFIX, если он не по умолчанию).
EOF
