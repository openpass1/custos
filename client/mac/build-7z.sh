#!/bin/bash
# Builds 7z.so, the 7-Zip library of CUSTOS for macOS, from the sources pinned in mac/7-zip-source.json (the same
# version as the 7z.dll of Windows, installer/7-zip.json). The download is refused if its SHA-256 differs.
# On a Mac it builds one universal library for Apple silicon and Intel (cmpl_mac_arm64.mak and cmpl_mac_x64.mak, joined
# by lipo); on Linux for the machine itself, for the tests.
#
#   bash client/mac/build-7z.sh <output folder>          the result is <output folder>/7z.so
set -euo pipefail

out=${1:?"usage: build-7z.sh <output folder>"}
here=$(cd "$(dirname "$0")" && pwd)
pin="$here/7-zip-source.json"
field() { sed -nE "s/.*\"$1\": *\"([^\"]+)\".*/\1/p" "$2"; }
version=$(field version "$pin")
url=$(field sourceUrl "$pin")
sha=$(field sourceSha256 "$pin")
windows=$(field version "$here/../installer/7-zip.json")
if [ "$version" != "$windows" ]; then
  echo "mac/7-zip-source.json has 7-Zip $version but installer/7-zip.json has $windows: change both together." >&2
  exit 1
fi

mkdir -p "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Downloading 7-Zip $version sources: $url"
curl -fsSL --retry 3 -o "$work/src.tar.xz" "$url"
if command -v sha256sum > /dev/null; then got=$(sha256sum "$work/src.tar.xz" | cut -d' ' -f1); else got=$(shasum -a 256 "$work/src.tar.xz" | cut -d' ' -f1); fi
if [ "$got" != "$sha" ]; then
  echo "SHA-256 of the 7-Zip sources is $got, expected $sha: refused." >&2
  exit 1
fi

mkdir "$work/src"
tar -xJf "$work/src.tar.xz" -C "$work/src"
cd "$work/src/CPP/7zip/Bundles/Format7zF"
jobs=$( (command -v nproc > /dev/null && nproc) || sysctl -n hw.ncpu)
if [ "$(uname -s)" = Darwin ]; then
  for arch in arm64 x64; do
    make -j"$jobs" -f "../../cmpl_mac_$arch.mak" > "$work/build-$arch.log" 2>&1 || { tail -n 40 "$work/build-$arch.log"; exit 1; }
  done
  lipo -create -output "$work/7z.so" b/m_arm64/7z.so b/m_x64/7z.so
  lipo "$work/7z.so" -verify_arch arm64 x86_64
  built="$work/7z.so"
else
  make -j"$jobs" -f ../../cmpl_gcc.mak > "$work/build.log" 2>&1 || { tail -n 40 "$work/build.log"; exit 1; }
  built=b/g/7z.so
fi
cp "$built" "$out/7z.so"
echo "7-Zip $version: $out/7z.so"
