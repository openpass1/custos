#!/bin/bash
# Builds CUSTOS for Linux (servers, x64 and arm64, glibc 2.28 and newer) with the .NET 10 SDK.
#
#   bash client/linux/build-linux.sh <version> <x64|arm64> [<output folder>]
#       custos-linux-<arch>.tar.gz: the program for one processor, as it lies in /opt/custos (the program with its own
#       .NET, 7z.so, libe_sqlite3.so, the systemd unit, install.sh and uninstall.sh). The two libraries are built for
#       the processor of this machine (build-native-linux.sh) unless they are in <output folder>/native-<arch> already.
#
#   bash client/linux/build-linux.sh package <version> [<output folder> [<desktop packages folder>]]
#       custos-linux-<version>.zip: the update package (A.10) with manifest.json and both archives, which must be in
#       the output folder already. With a desktop packages folder it also contains a descriptor of the four
#       release .deb and .rpm files in that folder.
#
# Default output folder: client/artifacts-linux.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
client=$(dirname "$here")
repo=$(dirname "$client")
step() { echo; echo "== $*"; }
check_version() {
  if ! [[ $1 =~ ^[0-9]+(\.[0-9]+){2,3}$ ]]; then
    echo "The version must look like 1.0.0, not \"$1\"." >&2
    exit 1
  fi
}

describe_desktop_packages() {
  local version=$1 desktop=$2
  local name file size sha separator=''
  local packages=(
    "custos-desktop_${version}_amd64.deb"
    "custos-desktop_${version}_arm64.deb"
    "custos-desktop-$version-1.x86_64.rpm"
    "custos-desktop-$version-1.aarch64.rpm"
  )

  if ! [[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Desktop release packages require a three-part version like 1.5.0 (received \"$version\")." >&2
    return 1
  fi

  printf '{\n  "version": "%s",\n  "packages": {\n' "$version"
  for name in "${packages[@]}"; do
    file="$desktop/$name"
    if [ ! -f "$file" ] || [ ! -s "$file" ]; then
      echo "Missing or empty desktop release package $file. Build all four packages first." >&2
      return 1
    fi
    size=$(stat -Lc %s -- "$file")
    if [ "$size" -gt $((500 * 1024 * 1024)) ]; then
      echo "$file exceeds the 500 MiB desktop package limit." >&2
      return 1
    fi
    sha=$(sha256sum < "$file")
    printf '%s    "%s": { "size": %s, "sha256": "%s" }' "$separator" "$name" "$size" "${sha%% *}"
    separator=$',\n'
  done
  printf '\n  }\n}\n'
}

if [ "${1:-}" = package ]; then
  version=${2:?"usage: build-linux.sh package <version> [<output folder> [<desktop packages folder>]]"}
  check_version "$version"
  out=${3:-$client/artifacts-linux}
  out=$(cd "$out" && pwd)
  work=$(mktemp -d "$out/.linux-package.XXXXXX")
  trap 'rm -rf "$work"' EXIT
  archive="custos-linux-$version.zip"
  step "$archive"
  entries=(custos-linux-x64.tar.gz custos-linux-arm64.tar.gz)
  for name in "${entries[@]}"; do
    [ -f "$out/$name" ] || { echo "$out/$name is missing: build it first." >&2; exit 1; }
    cp "$out/$name" "$work/$name"
  done
  if [ -n "${4:-}" ]; then
    name=linux-desktop-packages.json
    describe_desktop_packages "$version" "$4" > "$work/$name"
    entries+=("$name")
  fi
  files=""
  for name in "${entries[@]}"; do
    sha=$(sha256sum < "$work/$name")
    files="$files${files:+,
}    { \"path\": \"$name\", \"sha256\": \"${sha%% *}\" }"
  done
  # "edition": "linux" keeps the package away from Windows and macOS: their service and the site refuse an edition
  # they do not run.
  printf '{\n  "version": "%s",\n  "edition": "linux",\n  "min_protocol": 1,\n  "files": [\n%s\n  ]\n}\n' "$version" "$files" > "$work/manifest.json"
  (cd "$work" && zip -q -X "$archive" manifest.json "${entries[@]}")
  mv -fT -- "$work/$archive" "$out/$archive"
  ls -l "$out/$archive"
  exit 0
fi

version=${1:?"usage: build-linux.sh <version> <x64|arm64> [<output folder>]"}
arch=${2:?"usage: build-linux.sh <version> <x64|arm64> [<output folder>]"}
check_version "$version"
case "$arch" in x64|arm64) ;; *) echo "The processor is x64 or arm64, not \"$arch\"." >&2; exit 1 ;; esac
out=${3:-$client/artifacts-linux}
mkdir -p "$out"
out=$(cd "$out" && pwd)
work="$out/work-$arch"
rm -rf "$work"
mkdir -p "$work"

step "7-Zip and SQLite for $arch"
native="$out/native-$arch"
if [ ! -f "$native/7z.so" ] || [ ! -f "$native/libe_sqlite3.so" ]; then
  machine=$(uname -m)
  if { [ "$arch" = x64 ] && [ "$machine" != x86_64 ]; } || { [ "$arch" = arm64 ] && [ "$machine" != aarch64 ]; }; then
    echo "The libraries for $arch are built on a $arch machine; put them into $native." >&2
    exit 1
  fi
  bash "$here/build-native-linux.sh" "$native"
fi
file "$native/7z.so" "$native/libe_sqlite3.so"

step "The program $version for $arch"
# One file with the .NET assemblies inside; the native libraries (SQLite) stay next to it.
dotnet publish "$client/src/Custos.Linux/Custos.Linux.csproj" -c Release -r "linux-$arch" --self-contained true \
  -p:Version="$version" -p:PublishSingleFile=true -p:DebugType=none -o "$work/publish" -nologo

step "custos-linux-$arch.tar.gz"
program="$work/custos"
mkdir -p "$program"
cp -R "$work/publish/." "$program/"
# The SQLite of the package needs glibc 2.34: the one built for 2.28 takes its place.
cp "$native/7z.so" "$native/libe_sqlite3.so" "$program/"
cp "$here/custos.service" "$here/install.sh" "$here/uninstall.sh" "$program/"
cp "$repo/LICENSE" "$program/LICENSE.txt"
cp "$client/THIRD-PARTY-NOTICES.txt" "$program/THIRD-PARTY-NOTICES.txt"
chmod 755 "$program/custos" "$program/install.sh" "$program/uninstall.sh"
chmod 644 "$program/7z.so" "$program/libe_sqlite3.so" "$program/custos.service"
rm -f "$out/custos-linux-$arch.tar.gz"
tar --owner=0 --group=0 --numeric-owner -czf "$out/custos-linux-$arch.tar.gz" -C "$program" .
tar -tzf "$out/custos-linux-$arch.tar.gz" | sort
ls -l "$out/custos-linux-$arch.tar.gz"
