#!/usr/bin/env bash
# Prints the release text of one part from its CHANGELOG.md (client/ or server/): the sections of every version from
# the top down to the previous release of that part, so a version that was never released by itself is not lost.
# With one version its heading is left out (the release title already says the version).
#
# Usage: .github/release-notes.sh client|client-legacy|server VERSION [PREVIOUS]
#   VERSION   the version being released: it must be the top section of the file ("top" takes whatever is there).
#             client-legacy (the edition for Windows 7 and 8.1, text from client/CHANGELOG.md) may also release an
#             older section: that edition is released only when needed and can stay behind the standard one.
#   PREVIOUS  the version of the previous release of the part, if there is one
# Exits with 1 and a message on stderr when the file has no section for VERSION or the section is empty.
set -euo pipefail

part=${1:?part: client, client-legacy or server}
version=${2:?version}
previous=${3:-}
dir=$(cd "$(dirname "$0")/.." && pwd)
file="${part%-legacy}/CHANGELOG.md"
[ -f "$dir/$file" ] || { echo "$file is missing." >&2; exit 1; }

sections=$(sed -nE 's/^## +([0-9]+\.[0-9]+\.[0-9]+) *$/\1/p' "$dir/$file")
top=$(head -n 1 <<< "$sections")
[ "$version" != top ] || version=$top
if [ "$part" = client-legacy ] && grep -qxF "$version" <<< "$sections"; then
    :
elif [ "$top" != "$version" ]; then
    echo "$file starts with version ${top:-(none)}, not $version: add a section \"## $version\" at the top with the changes of this release." >&2
    exit 1
fi

# The text stops at the section of the previous release or, when that section is no longer in the file (its versions
# were folded into a later one, as 1.1.1-1.1.5 into 1.1.6), at the first section older than it.
stop=
if [ -n "$previous" ]; then
    while read -r v; do
        if [ "$(printf '%s\n%s\n' "$v" "$previous" | sort -V | head -n 1)" = "$v" ]; then
            stop=$v
            break
        fi
    done <<< "$sections"
fi

# From the heading of the version on, until that heading.
body=$(awk -v ver="$version" -v prev="$stop" '
    /^## / { if (started && $2 == prev) exit; if ($2 == ver) started = 1 }
    started { print }
' "$dir/$file")
if [ "$(grep -c '^## ' <<< "$body")" -eq 1 ]; then
    body=$(sed '1d' <<< "$body")
fi
# No blank lines at the start (the end has none: $(...) drops them).
body=$(sed '/[^[:space:]]/,$!d' <<< "$body")
if ! grep -v '^#' <<< "$body" | grep -q '[^[:space:]]'; then
    echo "The section \"## $version\" of $file says nothing: list the changes of this release." >&2
    exit 1
fi
printf '%s\n' "$body"
