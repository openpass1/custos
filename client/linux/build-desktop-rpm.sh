#!/bin/bash
# Package the same self-contained agent, desktop and service files built for Debian.
# Build on Linux with dpkg-deb and rpmbuild: build-desktop-rpm.sh package.deb output-directory
set -euo pipefail
umask 022
deb=${1:?usage: build-desktop-rpm.sh package.deb output-directory}
out=${2:?usage: build-desktop-rpm.sh package.deb output-directory}
here=$(cd "$(dirname "$0")" && pwd)
package=$(dpkg-deb -f "$deb" Package)
version=$(dpkg-deb -f "$deb" Version)
[[ $package == custos-desktop && $version =~ ^[0-9]+(\.[0-9]+){2,3}(~[[:alnum:].]+)?$ ]] || {
  echo "Expected a CUSTOS desktop package with a product or preview version" >&2
  exit 1
}
case $(dpkg-deb -f "$deb" Architecture) in
  amd64) target=x86_64 ;;
  arm64) target=aarch64 ;;
  *) echo "Unsupported Debian package architecture" >&2; exit 1 ;;
esac

mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/SOURCES"
dpkg-deb --fsys-tarfile "$deb" > "$work/SOURCES/payload.tar"
sed 's|^program=/usr/lib/custos-desktop/|program=/usr/libexec/custos-desktop/|' \
  "$here/desktop/deb/lifecycle.sh" > "$work/SOURCES/lifecycle.sh"
cp "$here/desktop/deb/postrm" "$work/SOURCES/"
rpmbuild -bb --target "$target" --define "_topdir $work" --define "custos_version $version" \
  "$here/desktop/rpm/custos-desktop.spec"
rpm="$package-$version-1.$target.rpm"
install -m644 "$work/RPMS/$target/$rpm" "$out/$rpm"
printf '%s\n' "$out/$rpm"
