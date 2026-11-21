#!/bin/bash
# Removes CUSTOS for Linux: the service custos, the program in /opt/custos and the command custos. The settings, the
# database and the logs stay in /var/lib/custos and /var/log/custos for a later installation; --purge removes them
# too. Run as root:   /opt/custos/uninstall.sh [--purge]
set -euo pipefail

ru=false
case "${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}" in ru*) ru=true ;; esac
say() { if $ru; then echo "$2"; else echo "$1"; fi; }

[ "$(id -u)" = 0 ] || { say "Run it as root (sudo)." "Запустите от root (через sudo)."; exit 1; }
purge=false
[ "${1:-}" = --purge ] && purge=true

# Stopping the service ends a run in progress and removes its snapshots.
if systemctl list-unit-files custos.service > /dev/null 2>&1; then
  systemctl disable --now custos > /dev/null 2>&1 || true
fi
rm -f /etc/systemd/system/custos.service
rm -rf /etc/systemd/system/custos.service.d
systemctl daemon-reload || true

# LVM snapshots a crash may have left (tagged custos).
if command -v lvs > /dev/null; then
  for lv in $(lvs --noheadings -o vg_name,lv_name @custos 2> /dev/null | awk '$2 ~ /^custos-snap-/ {print $1 "/" $2}'); do
    lvremove -f "$lv" > /dev/null 2>&1 || true
  done
fi

if command -v semanage > /dev/null; then
  semanage fcontext -d /opt/custos/custos 2> /dev/null || true
fi
rm -f /usr/local/bin/custos /run/custos.sock
rm -rf /opt/custos /opt/custos.new /opt/custos.old
if $purge; then
  rm -rf /var/lib/custos /var/log/custos
  say "CUSTOS is removed with its settings and logs." "CUSTOS удалён вместе с настройками и журналами."
else
  say "CUSTOS is removed. Settings and logs are kept in /var/lib/custos and /var/log/custos (--purge removes them)." \
    "CUSTOS удалён. Настройки и журналы остались в /var/lib/custos и /var/log/custos (--purge удаляет и их)."
fi
