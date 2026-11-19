#!/bin/bash
# Builds CUSTOS for macOS (Apple silicon, macOS 15 and newer) on a Mac with the .NET 10 SDK and the command line tools
# of Xcode:
#   - Custos.app: the program, published for osx-arm64 with its own .NET, 7z.so and the icon;
#   - Custos-<version>.pkg: the installer (the program, the launchd jobs of the service and of the menu bar icon);
#   - custos-macos-<version>.zip: the update package (A.10): manifest.json and Custos.pkg, as the service installs it.
#
#   bash client/mac/build-mac.sh <version> [<output folder>]      default output: client/artifacts-mac
#
# The program is signed ad hoc (no Apple Developer ID yet): it runs on Apple silicon, and the installer opens with
# right click → Open the first time.
set -euo pipefail

version=${1:?"usage: build-mac.sh <version> [<output folder>]"}
if ! [[ $version =~ ^[0-9]+(\.[0-9]+){2,3}$ ]]; then
  echo "The version must look like 1.0.0, not \"$version\"." >&2
  exit 1
fi

here=$(cd "$(dirname "$0")" && pwd)
client=$(dirname "$here")
repo=$(dirname "$client")
out=${2:-$client/artifacts-mac}
mkdir -p "$out"
out=$(cd "$out" && pwd)
work="$out/work"
rm -rf "$work"
mkdir -p "$work"
step() { echo; echo "== $*"; }

step "7-Zip"
if [ ! -f "$out/7z/7z.so" ]; then
  bash "$here/build-7z.sh" "$out/7z"
fi
file "$out/7z/7z.so"

step "The program $version"
dotnet publish "$client/src/Custos.Mac/Custos.Mac.csproj" -c Release -r osx-arm64 --self-contained true \
  -p:Version="$version" -p:UseAppHost=true -o "$work/publish" -nologo

step "Custos.app"
root="$work/root"
app="$root/Applications/Custos.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$root/Library/LaunchDaemons" "$root/Library/LaunchAgents"
cp -R "$work/publish/." "$app/Contents/MacOS/"
cp "$out/7z/7z.so" "$app/Contents/MacOS/7z.so"
cp "$client/src/Custos.Mac/Assets/custos.icns" "$app/Contents/Resources/custos.icns"
cp "$here/uninstall.sh" "$app/Contents/Resources/uninstall.sh"
cp "$repo/LICENSE" "$app/Contents/Resources/LICENSE.txt"
cp "$client/THIRD-PARTY-NOTICES.txt" "$app/Contents/Resources/THIRD-PARTY-NOTICES.txt"
chmod 755 "$app/Contents/MacOS/Custos" "$app/Contents/Resources/uninstall.sh"
sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"
cp "$here/org.custos.agent.plist" "$root/Library/LaunchDaemons/org.custos.agent.plist"
cp "$here/org.custos.menu.plist" "$root/Library/LaunchAgents/org.custos.menu.plist"
plutil -lint "$root/Library/LaunchDaemons/org.custos.agent.plist" "$root/Library/LaunchAgents/org.custos.menu.plist"

step "Ad-hoc signature"
# Every Mach-O file first (the program, the libraries of .NET, Avalonia and 7-Zip), then the bundle.
find "$app/Contents/MacOS" -type f | while read -r f; do
  if file -b "$f" | grep -q 'Mach-O'; then
    codesign --force --sign - --timestamp=none "$f"
  fi
done
codesign --force --sign - --timestamp=none "$app"
codesign --verify --strict --verbose=2 "$app"

step "Custos-$version.pkg"
# The bundle must land in /Applications even if someone moved an older copy: not relocatable.
pkgbuild --analyze --root "$root" "$work/components.plist"
plutil -replace 0.BundleIsRelocatable -bool NO "$work/components.plist"
pkgbuild --root "$root" --component-plist "$work/components.plist" --scripts "$here/scripts" \
  --identifier org.custos.pkg --version "$version" --install-location / "$work/Custos-component.pkg"
mkdir -p "$work/resources"
cp -R "$here/resources/." "$work/resources/"
for lang in en ru; do
  cp "$repo/LICENSE" "$work/resources/$lang.lproj/License.txt"
done
sed "s/__VERSION__/$version/g" "$here/distribution.xml" > "$work/distribution.xml"
productbuild --distribution "$work/distribution.xml" --resources "$work/resources" --package-path "$work" \
  "$out/Custos-$version.pkg"
pkgutil --check-signature "$out/Custos-$version.pkg" || true

step "custos-macos-$version.zip"
mkdir -p "$work/update"
cp "$out/Custos-$version.pkg" "$work/update/Custos.pkg"
sha=$(shasum -a 256 "$work/update/Custos.pkg" | cut -d' ' -f1)
# "edition": "macos" keeps the package away from Windows: their service and the site refuse an edition they do not run.
printf '{\n  "version": "%s",\n  "edition": "macos",\n  "min_protocol": 1,\n  "files": [\n    { "path": "Custos.pkg", "sha256": "%s" }\n  ]\n}\n' \
  "$version" "$sha" > "$work/update/manifest.json"
rm -f "$out/custos-macos-$version.zip"
(cd "$work/update" && zip -q -X "$out/custos-macos-$version.zip" manifest.json Custos.pkg)

step "Done"
ls -l "$out"
shasum -a 256 "$out/Custos-$version.pkg" "$out/custos-macos-$version.zip"
