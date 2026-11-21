#!/bin/bash
# For CI: the program of CUSTOS for Linux on one distribution, in a container without systemd. install.sh installs what
# the program needs (--packages-only), then the service is started by hand and the command custos talks to it, in
# English and in Russian. Not part of the archive.
#
#   bash client/linux/ci-smoke.sh <custos-linux-ARCH.tar.gz>
set -euo pipefail

archive=${1:?"usage: ci-smoke.sh <custos-linux-ARCH.tar.gz>"}
here=$(cd "$(dirname "$0")" && pwd)
# shellcheck disable=SC1091
. /etc/os-release
echo "== $PRETTY_NAME, $(getconf GNU_LIBC_VERSION)"
if ! bash "$here/install.sh" --packages-only; then
  # What the package manager offers for ICU, to see why none was found.
  if command -v zypper > /dev/null; then zypper -n search -t package libicu || true; fi
  if command -v apt-cache > /dev/null; then apt-cache pkgnames libicu || true; fi
  if command -v dnf > /dev/null; then dnf -q list available 'libicu*' || true; fi
  ldconfig -p | grep -i icu || true
  echo "FAILED on $PRETTY_NAME: install.sh --packages-only"
  exit 1
fi
mkdir -p /opt/custos
tar -xzf "$archive" -C /opt/custos
/opt/custos/custos version

export CUSTOS_DATA_DIR=/tmp/custos-data CUSTOS_SOCKET=/tmp/custos.sock
/opt/custos/custos agent > /tmp/agent.log 2>&1 &
agent=$!
fail() { echo "FAILED on $PRETTY_NAME: $*"; cat /tmp/agent.log; exit 1; }
for _ in $(seq 1 30); do
  [ -S "$CUSTOS_SOCKET" ] && break
  sleep 1
done
[ -S "$CUSTOS_SOCKET" ] || fail "the service did not start"
/opt/custos/custos status || fail "custos status"
/opt/custos/custos snapshots || fail "custos snapshots"
LANG=ru_RU.UTF-8 /opt/custos/custos help | grep 'Команды' > /dev/null || fail "no Russian with libicu"
grep '7z.dll .* loaded' /tmp/agent.log > /dev/null || fail "7z.so was not loaded"
[ -f "$CUSTOS_DATA_DIR/db/custos.db" ] || fail "no database"
kill "$agent"
wait "$agent" || true
if grep -E ' (ERR|FTL)\] ' /tmp/agent.log; then
  fail "the service logged errors"
fi
echo "OK: $PRETTY_NAME"
