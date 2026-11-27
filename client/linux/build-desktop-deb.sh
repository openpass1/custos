#!/bin/bash
# Builds the CUSTOS desktop Debian package and the standalone GUI archive.
# Usage: bash client/linux/build-desktop-deb.sh [x64|arm64] [output folder] [version]
set -euo pipefail
umask 022

here=$(cd "$(dirname "$0")" && pwd)
client=$(dirname "$here")
repo=$(dirname "$client")
arch=${1:-x64}
case "$arch" in
  x64) deb_arch=amd64; native_machine=x86_64 ;;
  arm64) deb_arch=arm64; native_machine=aarch64 ;;
  *) echo 'Usage: build-desktop-deb.sh [x64|arm64] [output folder] [version]' >&2; exit 1 ;;
esac

version=${3:-$(awk '/^## [0-9]+\.[0-9]+\.[0-9]+$/ {print $2; exit}' "$client/CHANGELOG.md")}
if ! [[ "$version" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]]; then
  echo 'The version must look like 1.4.0.' >&2
  exit 1
fi
deb_version=${CUSTOS_DESKTOP_DEB_VERSION:-$version~preview1}
dpkg --validate-version "$deb_version"
name="custos-desktop_${deb_version}_${deb_arch}.deb"
out=${2:-$client/artifacts-linux/desktop}
mkdir -p -- "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d "$out/.desktop-deb.$arch.XXXXXX")
trap 'rm -rf -- "$work"' EXIT

gui="custos-linux-desktop-$version-$arch"
bash "$here/build-desktop.sh" "$arch" "$work" "$version"

native="$out/native-$arch"
if [ ! -f "$native/7z.so" ] || [ ! -f "$native/libe_sqlite3.so" ]; then
  if [ "$(uname -m)" != "$native_machine" ]; then
    echo "Build or place native libraries for $arch into $native first." >&2
    exit 1
  fi
  bash "$here/build-native-linux.sh" "$native"
fi
ln -s "$native" "$work/native-$arch"
CustosPackageManaged=true bash "$here/build-linux.sh" "$version" "$arch" "$work"

stage="$work/deb"
payload="$stage/usr/lib/custos-desktop"
doc="$stage/usr/share/doc/custos-desktop"
mkdir -p "$stage/DEBIAN" "$stage/usr/bin" "$payload/agent" "$payload/gui" "$doc"
tar -xzf "$work/custos-linux-$arch.tar.gz" -C "$payload/agent"
tar -xzf "$work/$gui.tar.gz" -C "$payload/gui" --strip-components=1
test -x "$payload/agent/custos"
test -x "$payload/gui/custos-gui"
rm -- "$payload/agent/install.sh" "$payload/agent/uninstall.sh" "$payload/agent/custos.service"
rm -- "$payload/gui/add-menu-entry.sh" "$payload/gui/README.md" "$payload/gui/README.ru.md"
rm -- "$payload/gui/org.custos.Desktop.desktop" "$payload/gui/custos-gui.png"

ln -s ../lib/custos-desktop/agent/custos "$stage/usr/bin/custos"
ln -s ../lib/custos-desktop/gui/custos-gui "$stage/usr/bin/custos-gui"
install -Dm644 "$here/desktop/org.custos.Desktop.desktop" "$stage/usr/share/applications/org.custos.Desktop.desktop"
install -Dm644 "$here/desktop/custos-gui.png" "$stage/usr/share/icons/hicolor/256x256/apps/custos-gui.png"
install -Dm644 "$repo/LICENSE" "$doc/copyright"

unit="$stage/usr/lib/systemd/system/custos.service"
mkdir -p "$(dirname "$unit")"
sed -e 's|^ExecStart=/opt/custos/|ExecStart=/usr/lib/custos-desktop/agent/|' \
    -e '/^Wants=network-online.target$/a ConditionPathExists=!/opt/custos/custos' \
    "$here/custos.service" > "$unit"
grep -Fx 'ExecStart=/usr/lib/custos-desktop/agent/custos agent --systemd' "$unit" >/dev/null

for script in preinst postinst prerm postrm; do
  cat "$here/desktop/deb/lifecycle.sh" "$here/desktop/deb/$script" > "$stage/DEBIAN/$script"
  chmod 755 "$stage/DEBIAN/$script"
done

installed_size=$(du -sk "$stage/usr" | cut -f1)
cat > "$stage/DEBIAN/control" <<EOF
Package: custos-desktop
Version: $deb_version
Section: utils
Priority: optional
Architecture: $deb_arch
Maintainer: CUSTOS contributors <openpass1@users.noreply.github.com>
Installed-Size: $installed_size
Depends: systemd, init-system-helpers, util-linux, ca-certificates, libc6 (>= 2.34), libgcc-s1, libstdc++6, libgssapi-krb5-2, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70, zlib1g, libx11-6, libx11-xcb1, libice6, libsm6, libfontconfig1, libxext6, libxrandr2, libxi6, libxkbcommon0
Recommends: cifs-utils, lvm2, btrfs-progs, xfsprogs, xdg-desktop-portal, libgtk-3-0t64 | libgtk-3-0, xwayland
Conflicts: custos-desktop-preview
Replaces: custos-desktop-preview
Homepage: https://github.com/openpass1/custos
Description: CUSTOS backup service and desktop interface
 Includes the CUSTOS Linux service and its desktop window for configuring
 backup tasks, controlling runs, reading logs and restoring files.
EOF

chmod -R a+rX "$stage"
dpkg-deb --root-owner-group --build "$stage" "$work/$name"
mv -fT -- "$work/$name" "$out/$name"
mv -fT -- "$work/$gui.tar.gz" "$out/$gui.tar.gz"
printf '\n%s\n' "$out/$name"
