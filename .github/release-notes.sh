#!/usr/bin/env bash
# Prints the release text of one part from its CHANGELOG.md (client/ or server/): the sections of every version from
# the top down to the previous release of that part, so a version that was never released by itself is not lost.
# With one version its heading is left out (the release title already says the version).
#
# Usage: .github/release-notes.sh client|server VERSION [PREVIOUS]
#   VERSION   the version being released: it must be the top section of the file ("top" takes whatever is there)
#   PREVIOUS  the version of the previous release of the part, if there is one
# Exits with 1 and a message on stderr when the file has no section for VERSION or the section is empty.
set -euo pipefail

part=${1:?part: client or server}
version=${2:?version}
previous=${3:-}
dir=$(cd "$(dirname "$0")/.." && pwd)
file="$part/CHANGELOG.md"
[ -f "$dir/$file" ] || { echo "$file is missing." >&2; exit 1; }

top=$(sed -nE 's/^## +([0-9]+\.[0-9]+\.[0-9]+) *$/\1/p' "$dir/$file" | head -n 1)
[ "$version" != top ] || version=$top
if [ "$top" != "$version" ]; then
    echo "$file starts with version ${top:-(none)}, not $version: add a section \"## $version\" at the top with the changes of this release." >&2
    exit 1
fi

# A previous release whose section is no longer in the file (its versions were folded into a later one): only the
# section of this version.
if [ -n "$previous" ] && ! grep -qE "^## +${previous//./\\.} *$" "$dir/$file"; then
    previous=$(sed -nE 's/^## +([0-9]+\.[0-9]+\.[0-9]+) *$/\1/p' "$dir/$file" | sed -n 2p)
fi

# From the first "## " heading on, until the heading of the previous release.
body=$(awk -v prev="$previous" '
    /^## / { if ($2 == prev) exit; started = 1 }
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
