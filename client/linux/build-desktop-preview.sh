#!/bin/bash
# Builds the optional Linux desktop preview with the .NET 10 SDK.
# Usage: bash client/linux/build-desktop-preview.sh [x64|arm64] [output folder]
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
client=$(dirname "$here")
repo=$(dirname "$client")
arch=${1:-x64}
case "$arch" in
  x64|arm64) ;;
  *) echo 'Usage: build-desktop-preview.sh [x64|arm64] [output folder]' >&2; exit 1 ;;
esac

out=${2:-$client/artifacts-linux/desktop-preview}
mkdir -p -- "$out"
out=$(cd "$out" && pwd)
name="custos-linux-desktop-preview-$arch"
work=$(mktemp -d "$out/.$name.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
bundle="$work/$name"

dotnet publish "$client/src/Custos.Linux.Desktop/Custos.Linux.Desktop.csproj" \
  -c Release -r "linux-$arch" --self-contained true -p:DebugType=None -o "$bundle" --nologo
test -x "$bundle/custos-gui"

cp "$here"/desktop/{org.custos.DesktopPreview.desktop,add-menu-entry.sh,custos-gui.png,README.md,README.ru.md} "$bundle/"
cp "$repo/LICENSE" "$bundle/LICENSE.txt"
cp "$client/THIRD-PARTY-NOTICES.txt" "$bundle/"
cp "$client/src/Custos.Windows/THIRD-PARTY-NOTICES-AVALONIA.txt" "$bundle/"
cp "$client/src/Custos.Desktop/Assets/Fonts/Roboto-LICENSE.txt" "$bundle/"
chmod 755 "$bundle/add-menu-entry.sh"

tar --sort=name --owner=0 --group=0 --numeric-owner -czf "$work/$name.tar.gz" -C "$work" "$name"
mv -fT -- "$work/$name.tar.gz" "$out/$name.tar.gz"
printf '\n%s\n' "$out/$name.tar.gz"
