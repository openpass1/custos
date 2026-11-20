#!/usr/bin/env bash
# Prints the text of one release: the section of that version in the part's changelog, without its heading (the
# release title already says the version). It is the whole list of changes of the release, nothing from other versions.
#
# Usage: .github/release-notes.sh client|client-legacy|server VERSION
#   client         client/CHANGELOG.md, releases client-v…
#   client-legacy  client/CHANGELOG-legacy.md, releases client-legacy-v… (the edition for Windows 7 and 8.1)
#   server         server/CHANGELOG.md, releases server-v…
#   VERSION        the version being released, or "top" for whatever is at the top of the file
#
# It also checks the rules written at the top of each changelog, and exits with 1 and a message on stderr when one is
# broken:
# - VERSION is the top section;
# - every section below the top is a released version (its tag exists on origin), so a version that was never released
#   cannot be left behind with changes nobody sees: merge it into the top one instead. RELEASE_NOTES_OFFLINE=1 skips
#   this check (no network);
# - a section holds only the groups "### Added", "### Improved", "### Fixed", "### Removed", in this order, each with
#   items of one line ("- …", at most 140 characters), and nothing else.
set -euo pipefail
export LC_ALL=C.UTF-8

part=${1:?part: client, client-legacy or server}
version=${2:?version}
dir=$(cd "$(dirname "$0")/.." && pwd)
case "$part" in
    client) file=client/CHANGELOG.md ;;
    client-legacy) file=client/CHANGELOG-legacy.md ;;
    server) file=server/CHANGELOG.md ;;
    *) echo "Unknown part \"$part\": client, client-legacy or server." >&2; exit 1 ;;
esac
[ -f "$dir/$file" ] || { echo "$file is missing." >&2; exit 1; }
fail() { echo "$file: $1" >&2; exit 1; }

sections=$(sed -nE 's/^## +([0-9]+\.[0-9]+\.[0-9]+) *$/\1/p' "$dir/$file")
top=$(head -n 1 <<< "$sections")
[ -n "$top" ] || fail "no section \"## <version>\" at all."
[ "$version" != top ] || version=$top
[ "$top" = "$version" ] || fail "the top section is $top, not $version: put the changes of $version under \"## $version\" at the top."

# The form of every section, from the first "## " heading to the end of the file.
problems=$(awk '
    function bad(msg) { printf "line %d: %s\n", NR, msg; n++ }
    /^## / { if (in_section && !items) bad("section " section " has no items"); section = $2; in_section = 1; rank = 0; group = ""; items = 0; next }
    !in_section { next }
    /^[[:space:]]*$/ { next }
    /^### / {
        g = substr($0, 5); r = (g == "Added") ? 1 : (g == "Improved") ? 2 : (g == "Fixed") ? 3 : (g == "Removed") ? 4 : 0
        if (r == 0) bad("unknown group \"" g "\": Added, Improved, Fixed or Removed")
        else if (r <= rank) bad("group \"" g "\" out of order or twice: Added, Improved, Fixed, Removed")
        rank = r; group = g; next
    }
    /^- / {
        if (group == "") bad("an item outside a group")
        items++; next
    }
    { bad("only groups and one-line items belong in a section: \"" substr($0, 1, 40) "\"") }
    END { if (in_section && !items) bad("section " section " has no items"); exit n > 0 }
' "$dir/$file") || fail "$(head -n 5 <<< "$problems")"
# Lengths in characters, not bytes (awk may count bytes).
while IFS= read -r line; do
    [ "${#line}" -le 142 ] || fail "an item longer than 140 characters, keep it to what the user gets: \"${line:0:60}…\""
done < <(awk '/^## /{ s = 1 } s && /^- /' "$dir/$file")

if [ "${RELEASE_NOTES_OFFLINE:-}" != 1 ]; then
    released=$(git -C "$dir" ls-remote --tags --refs origin "refs/tags/$part-v*" | sed -nE "s#.*refs/tags/$part-v##p")
    while read -r v; do
        [ -n "$v" ] || continue
        grep -qxF "$v" <<< "$released" \
            || fail "section $v was never released as $part-v$v: merge its items into the section of $version and remove it."
    done < <(tail -n +2 <<< "$sections")
fi

# The section of the version, without blank lines at its start and end ($(...) drops the ones at the end).
body=$(awk -v ver="$version" '
    /^## / { if (started) exit; if ($2 == ver) { started = 1; next } }
    started { print }
' "$dir/$file")
printf '%s\n' "$body" | sed '/[^[:space:]]/,$!d'
