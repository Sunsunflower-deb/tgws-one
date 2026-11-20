#!/usr/bin/env bash
# Удаление TgwsOne, поставленного install.sh: останавливает и отключает службу, убирает ровно то, что было положено.
# Запускать от root:   sudo ./uninstall.sh [--purge]
#
# Без --purge остаются настройки и данные (/etc/tgwsone — свои стратегии и списки, /var/lib/tgwsone — исключения и состояние),
# чтобы переустановка их подхватила. С --purge убираются и они, и журналы службы (/var/log/tgwsone).
# PREFIX, DESTDIR, SYSTEMCTL и TGWS_TEST — те же, что у install.sh (PREFIX должен совпадать с тем, с которым ставили).
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
prefix=${PREFIX:-/usr/local}
dest=${DESTDIR:-}
unit=tgwsone-zapret

purge=0
case "${1:-}" in
    "") ;;
    --purge) purge=1 ;;
    *) echo "использование: $0 [--purge]" >&2; exit 64 ;;
esac
[ $# -le 1 ] || { echo "использование: $0 [--purge]" >&2; exit 64; }

if [ "${TGWS_TEST:-0}" != 1 ] && [ "$(id -u)" -ne 0 ]; then
    echo "нужны права root: sudo $0" >&2
    exit 1
fi

use_systemctl=1
[ -n "$dest" ] && [ -z "${SYSTEMCTL:-}" ] && use_systemctl=0
systemctl_cmd=${SYSTEMCTL:-systemctl}
command -v "$systemctl_cmd" >/dev/null 2>&1 || use_systemctl=0

# Службы может и не быть (не ставили, уже убрали): это не причина прерываться.
[ "$use_systemctl" -eq 1 ] && { "$systemctl_cmd" disable --now "$unit" || true; }

"$here/packaging/linux/uninstall-tree.sh" "$dest" "$prefix"

[ "$use_systemctl" -eq 1 ] && "$systemctl_cmd" daemon-reload

if [ "$purge" -eq 1 ]; then
    rm -rf "$dest/etc/tgwsone" "$dest/var/lib/tgwsone" "$dest/var/log/tgwsone"
    echo "TgwsOne удалён вместе с настройками, данными и журналами."
else
    echo "TgwsOne удалён. Настройки (/etc/tgwsone) и данные (/var/lib/tgwsone) оставлены; убрать и их: sudo $0 --purge"
fi
