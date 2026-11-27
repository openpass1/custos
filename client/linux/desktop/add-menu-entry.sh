#!/bin/bash
# Adds a launcher for this copy to the current user's application menu.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd -P)
if [[ ! -x "$here/custos-gui" || ! -f "$here/custos-gui.png" ]]; then
  echo 'Run this script from the extracted CUSTOS desktop preview.' >&2
  exit 1
fi

data=${XDG_DATA_HOME:-$HOME/.local/share}
[[ "$data" = /* ]] || data="$HOME/.local/share"
applications="$data/applications"
launcher=org.custos.DesktopPreview.desktop
mkdir -p -- "$applications"
temp=$(mktemp "$applications/.$launcher.XXXXXX")
trap 'rm -f -- "$temp"' EXIT

desktop_value() {
  local value=$1
  value=${value//\\/\\\\}
  value=${value//$'\n'/\\n}
  value=${value//$'\r'/\\r}
  value=${value//$'\t'/\\t}
  printf '%s' "$value"
}

# Exec quoting is applied after Desktop Entry value escaping. A literal percent is doubled for field-code expansion.
program="$here/custos-gui"
program=${program//\\/\\\\}
program=${program//\"/\\\"}
program=${program//\$/\\\$}
program=${program//\`/\\\`}
program=${program//%/%%}

while IFS= read -r line; do
  case "$line" in
    Exec=*) printf 'Exec=/usr/bin/env "%s"\n' "$(desktop_value "$program")" ;;
    Icon=*) printf 'Icon=%s\n' "$(desktop_value "$here/custos-gui.png")" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$here/$launcher" > "$temp"

chmod 644 -- "$temp"
mv -fT -- "$temp" "$applications/$launcher"
printf '%s\n' "$applications/$launcher"
