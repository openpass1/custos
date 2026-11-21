#!/bin/bash
# Builds the two native libraries of CUSTOS for Linux for the processor of this machine (x64 or arm64): 7z.so (7-Zip,
# same sources and checks as for macOS: mac/build-7z.sh, mac/7-zip-source.json) and libe_sqlite3.so (SQLite:
# build-sqlite.sh, sqlite-source.json). They are built in a container of AlmaLinux 8 so that they need nothing newer
# than glibc 2.28, the oldest system CUSTOS for Linux supports.
#
#   bash client/linux/build-native-linux.sh <output folder>     the results are <output folder>/7z.so and libe_sqlite3.so
#
# Without Docker (or with CUSTOS_NATIVE_HERE=1) it builds on this machine as it is (for tests).
set -euo pipefail

out=${1:?"usage: build-native-linux.sh <output folder>"}
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
mkdir -p "$out"
out=$(cd "$out" && pwd)

if [ "${CUSTOS_NATIVE_HERE:-}" = 1 ] || ! command -v docker > /dev/null; then
  bash "$repo/client/mac/build-7z.sh" "$out"
  bash "$here/build-sqlite.sh" "$out"
  exit 0
fi

# The gcc 8 of AlmaLinux 8 does not know the warnings the makefiles of 7-Zip ask for: gcc-toolset-13 does, and still
# builds for the glibc and libstdc++ of the system (what it needs beyond them is linked in). A mirror of AlmaLinux now
# and then serves empty metadata ("repomd.xml parser error"): dnf tries again, which takes another mirror.
docker run --rm -v "$repo:/repo:ro" -v "$out:/out" almalinux:8 bash -euo pipefail -c '
  for try in 1 2 3; do
    dnf -y -q install gcc-toolset-13-gcc gcc-toolset-13-gcc-c++ make xz curl findutils unzip openssl > /dev/null && break
    [ "$try" != 3 ] || exit 1
    echo "dnf failed (attempt $try), trying again with fresh metadata"
    dnf clean all > /dev/null
    sleep 20
  done
  set +u
  source /opt/rh/gcc-toolset-13/enable
  set -u
  gcc --version | head -n 1
  bash /repo/client/mac/build-7z.sh /out
  bash /repo/client/linux/build-sqlite.sh /out
  chown '"$(id -u):$(id -g)"' /out/7z.so /out/libe_sqlite3.so
'
for library in 7z.so libe_sqlite3.so; do
  echo "glibc symbols needed by $library:"
  objdump -T "$out/$library" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -n 1
done
