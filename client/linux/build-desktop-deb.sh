#!/bin/bash
# Builds an optional CUSTOS agent and Linux desktop preview Debian package.
# Usage: bash client/linux/build-desktop-deb.sh [x64|arm64] [output folder]
set -euo pipefail
umask 022

here=$(cd "$(dirname "$0")" && pwd)
client=$(dirname "$here")
repo=$(dirname "$client")
arch=${1:-x64}
case "$arch" in
  x64) deb_arch=amd64; native_machine=x86_64 ;;
  arm64) deb_arch=arm64; native_machine=aarch64 ;;
  *) echo 'Usage: build-desktop-deb.sh [x64|arm64] [output folder]' >&2; exit 1 ;;
esac

version=$(awk '/^## [0-9]+\.[0-9]+\.[0-9]+$/ {print $2; exit}' "$client/CHANGELOG.md")
test -n "$version"
deb_version=${CUSTOS_DESKTOP_DEB_VERSION:-$version~preview1}
dpkg --validate-version "$deb_version"
name="custos-desktop-preview_${deb_version}_${deb_arch}.deb"
out=${2:-$client/artifacts-linux/desktop-preview}
mkdir -p -- "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d "$out/.desktop-deb.$arch.XXXXXX")
trap 'rm -rf -- "$work"' EXIT

gui="custos-linux-desktop-preview-$arch"
if [ ! -f "$out/$gui.tar.gz" ]; then
  bash "$here/build-desktop-preview.sh" "$arch" "$out"
fi

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
payload="$stage/usr/lib/custos-desktop-preview"
doc="$stage/usr/share/doc/custos-desktop-preview"
mkdir -p "$stage/DEBIAN" "$stage/usr/bin" "$payload/agent" "$payload/gui" "$doc"
tar -xzf "$work/custos-linux-$arch.tar.gz" -C "$payload/agent"
tar -xzf "$out/$gui.tar.gz" -C "$payload/gui" --strip-components=1
test -x "$payload/agent/custos"
test -x "$payload/gui/custos-gui"
rm -- "$payload/agent/install.sh" "$payload/agent/uninstall.sh" "$payload/agent/custos.service"
rm -- "$payload/gui/add-menu-entry.sh" "$payload/gui/README.md" "$payload/gui/README.ru.md"
rm -- "$payload/gui/org.custos.DesktopPreview.desktop" "$payload/gui/custos-gui.png"

ln -s ../lib/custos-desktop-preview/agent/custos "$stage/usr/bin/custos"
ln -s ../lib/custos-desktop-preview/gui/custos-gui "$stage/usr/bin/custos-gui"
install -Dm644 "$here/desktop/org.custos.DesktopPreview.desktop" "$stage/usr/share/applications/org.custos.DesktopPreview.desktop"
install -Dm644 "$here/desktop/custos-gui.png" "$stage/usr/share/icons/hicolor/256x256/apps/custos-gui.png"
install -Dm644 "$repo/LICENSE" "$doc/copyright"

unit="$stage/usr/lib/systemd/system/custos.service"
mkdir -p "$(dirname "$unit")"
sed -e 's|^ExecStart=/opt/custos/|ExecStart=/usr/lib/custos-desktop-preview/agent/|' \
    -e '/^Wants=network-online.target$/a ConditionPathExists=!/opt/custos/custos' \
    "$here/custos.service" > "$unit"
grep -Fx 'ExecStart=/usr/lib/custos-desktop-preview/agent/custos agent --systemd' "$unit" >/dev/null

for script in preinst postinst prerm postrm; do
  cat "$here/desktop/deb/lifecycle.sh" "$here/desktop/deb/$script" > "$stage/DEBIAN/$script"
  chmod 755 "$stage/DEBIAN/$script"
done

installed_size=$(du -sk "$stage/usr" | cut -f1)
cat > "$stage/DEBIAN/control" <<EOF
Package: custos-desktop-preview
Version: $deb_version
Section: utils
Priority: optional
Architecture: $deb_arch
Maintainer: CUSTOS contributors <openpass1@users.noreply.github.com>
Installed-Size: $installed_size
Depends: systemd, init-system-helpers, ca-certificates, libc6 (>= 2.34), libgcc-s1, libstdc++6, libgssapi-krb5-2, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70, zlib1g, libx11-6, libice6, libsm6, libfontconfig1, libxext6, libxrandr2, libxi6
Recommends: cifs-utils, xdg-desktop-portal
Homepage: https://github.com/openpass1/custos
Description: CUSTOS backup agent and desktop preview
 Includes the CUSTOS Linux backup service and an optional desktop window for
 looking at tasks, controlling runs and reading their logs.
EOF

chmod -R a+rX "$stage"
dpkg-deb --root-owner-group --build "$stage" "$work/$name"
mv -fT -- "$work/$name" "$out/$name"
printf '\n%s\n' "$out/$name"
