#!/bin/bash
# Builds libe_sqlite3.so (SQLite, the database of the service) for the processor of this machine from the amalgamation
# pinned in linux/sqlite-source.json, with the options of the e_sqlite3 library of SQLitePCLRaw that Windows and macOS
# use. Run by build-native-linux.sh inside AlmaLinux 8, so that it needs nothing newer than glibc 2.28.
#
#   bash client/linux/build-sqlite.sh <output folder>          the result is <output folder>/libe_sqlite3.so
set -euo pipefail

out=${1:?"usage: build-sqlite.sh <output folder>"}
here=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$out"
out=$(cd "$out" && pwd)
field() { grep -o "\"$1\": *\"[^\"]*\"" "$here/sqlite-source.json" | sed -E 's/.*: *"([^"]*)"/\1/'; }
url=$(field sourceUrl)
want=$(field sourceSha3_256)
version=$(field version)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
echo "SQLite $version from $url"
curl -fsSL --retry 3 -o "$work/sqlite.zip" "$url"
got=$(openssl dgst -sha3-256 "$work/sqlite.zip" | awk '{print $NF}')
if [ "$got" != "$want" ]; then
  echo "The SHA3-256 of $url is $got, not $want as linux/sqlite-source.json says." >&2
  exit 1
fi
(cd "$work" && unzip -q sqlite.zip)
source=$(find "$work" -name sqlite3.c | head -n 1)
[ -n "$source" ] || { echo "No sqlite3.c in $url" >&2; exit 1; }

# The options e_sqlite3 is built with (PRAGMA compile_options of the library in the package).
gcc -O2 -fPIC -shared -s -o "$out/libe_sqlite3.so" "$source" \
  -DSQLITE_THREADSAFE=1 -DSQLITE_DEFAULT_FOREIGN_KEYS=1 -DSQLITE_DEFAULT_RECURSIVE_TRIGGERS=1 -DSQLITE_DQS=0 \
  -DSQLITE_ENABLE_COLUMN_METADATA -DSQLITE_ENABLE_FTS3 -DSQLITE_ENABLE_FTS3_PARENTHESIS -DSQLITE_ENABLE_FTS4 \
  -DSQLITE_ENABLE_FTS5 -DSQLITE_ENABLE_GEOPOLY -DSQLITE_ENABLE_MATH_FUNCTIONS -DSQLITE_ENABLE_PREUPDATE_HOOK \
  -DSQLITE_ENABLE_RTREE -DSQLITE_ENABLE_SESSION -DSQLITE_ENABLE_SNAPSHOT -DSQLITE_LIKE_DOESNT_MATCH_BLOBS \
  -DSQLITE_MAX_VARIABLE_NUMBER=32766 \
  -lpthread -ldl -lm
ls -l "$out/libe_sqlite3.so"
