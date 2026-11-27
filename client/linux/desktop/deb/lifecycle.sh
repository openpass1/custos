#!/bin/sh
set -eu

unit=custos.service
unit_path=/usr/lib/systemd/system/$unit
enable_link=/etc/systemd/system/multi-user.target.wants/$unit
program=/usr/lib/custos-desktop-preview/agent/custos
state_dir=/var/lib/custos-desktop-preview
install_marker=$state_dir/new-install

path_exists() {
  [ -e "$1" ] || [ -L "$1" ]
}

clear_install_marker() {
  rm -f -- "$install_marker"
  rmdir -- "$state_dir" 2>/dev/null || true
}

external_unit() {
  for path in /etc/systemd/system.control /run/systemd/system.control /run/systemd/transient \
      /etc/systemd/system /run/systemd/system /usr/local/lib/systemd/system; do
    if path_exists "$path/$unit"; then
      printf '%s\n' "$path/$unit"
      return 0
    fi
  done
  return 1
}

external_agent() {
  external_unit >/dev/null || path_exists /opt/custos || path_exists /usr/local/bin/custos
}

systemd_running() {
  [ -d /run/systemd/system ]
}

package_unit_loaded() {
  fragment=$(systemctl show --property=FragmentPath --value "$unit" 2>/dev/null) || return 1
  [ -n "$fragment" ] && [ "$(readlink -f -- "$fragment")" = "$unit_path" ] \
    && systemctl show --property=ExecStart --value "$unit" 2>/dev/null | grep -F "path=$program " >/dev/null
}
