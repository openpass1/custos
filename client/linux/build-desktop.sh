#!/bin/bash
# Builds the standalone Linux desktop archive with the .NET 10 SDK.
# Usage: bash client/linux/build-desktop.sh [x64|arm64] [output folder] [version]
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
client=$(dirname "$here")
repo=$(dirname "$client")
arch=${1:-x64}
case "$arch" in
  x64|arm64) ;;
  *) echo 'Usage: build-desktop.sh [x64|arm64] [output folder] [version]' >&2; exit 1 ;;
esac

version=${3:-$(awk '/^## [0-9]+\.[0-9]+\.[0-9]+$/ {print $2; exit}' "$client/CHANGELOG.md")}
if ! [[ "$version" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]]; then
  echo 'The version must look like 1.4.0.' >&2
  exit 1
fi

out=${2:-$client/artifacts-linux/desktop}
mkdir -p -- "$out"
out=$(cd "$out" && pwd)
name="custos-linux-desktop-$version-$arch"
work=$(mktemp -d "$out/.$name.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
bundle="$work/$name"

dotnet publish "$client/src/Custos.Linux.Desktop/Custos.Linux.Desktop.csproj" -c Release -r "linux-$arch" \
  --self-contained true -p:Version="$version" -p:DebugType=None -o "$bundle" --nologo
test -x "$bundle/custos-gui"

cp "$here"/desktop/{org.custos.Desktop.desktop,add-menu-entry.sh,custos-gui.png,README.md,README.ru.md} "$bundle/"
cp "$repo/LICENSE" "$bundle/LICENSE.txt"
cp "$client/THIRD-PARTY-NOTICES.txt" "$bundle/"
cp "$client/src/Custos.Windows/THIRD-PARTY-NOTICES-AVALONIA.txt" "$bundle/"
cp "$client/src/Custos.Desktop/Assets/Fonts/Roboto-LICENSE.txt" "$bundle/"
chmod 755 "$bundle/add-menu-entry.sh"

tar --sort=name --owner=0 --group=0 --numeric-owner -czf "$work/$name.tar.gz" -C "$work" "$name"
mv -fT -- "$work/$name.tar.gz" "$out/$name.tar.gz"
printf '\n%s\n' "$out/$name.tar.gz"
