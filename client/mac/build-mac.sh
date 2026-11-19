#!/bin/bash
# Builds CUSTOS for macOS (Apple silicon and Intel, macOS 15 and newer) on a Mac with the .NET 10 SDK and the command
# line tools of Xcode:
#   - Custos.app: the program, published for osx-arm64 and for osx-x64, each with its own .NET, 7z.so and the icon;
#   - Custos-<version>.pkg: one installer for both (the program of this Mac's processor, the launchd jobs of the
#     service and of the menu bar icon);
#   - custos-macos-<version>.zip: the update package (A.10): manifest.json and Custos.pkg, as the service installs it.
#
#   bash client/mac/build-mac.sh <version> [<output folder>]      default output: client/artifacts-mac
#
# The program is signed ad hoc (no Apple Developer ID yet), which Apple silicon requires; the installer opens with
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
lipo "$out/7z/7z.so" -verify_arch arm64 x86_64

# Two builds of the program, one for each kind of processor, in one installer: Installer takes the one of this Mac
# (distribution.xml). 7z.so is one universal library for both.
archs="arm64 x86_64"
rid() { [ "$1" = arm64 ] && echo osx-arm64 || echo osx-x64; }

for arch in $archs; do
  step "The program $version for $arch"
  # One file: Contents/MacOS of a signed bundle may hold only code (Mach-O). The .NET assemblies go inside the
  # program, the native libraries (Avalonia, Skia, SQLite) stay next to it.
  dotnet publish "$client/src/Custos.Mac/Custos.Mac.csproj" -c Release -r "$(rid "$arch")" --self-contained true \
    -p:Version="$version" -p:UseAppHost=true -p:PublishSingleFile=true -p:DebugType=none -o "$work/$arch/publish" -nologo

  step "Custos.app for $arch"
  root="$work/$arch/root"
  app="$root/Applications/Custos.app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$root/Library/LaunchDaemons" "$root/Library/LaunchAgents"
  cp -R "$work/$arch/publish/." "$app/Contents/MacOS/"
  cp "$out/7z/7z.so" "$app/Contents/MacOS/7z.so"
  cp "$client/src/Custos.Mac/Assets/custos.icns" "$app/Contents/Resources/custos.icns"
  cp "$here/uninstall.sh" "$app/Contents/Resources/uninstall.sh"
  cp "$repo/LICENSE" "$app/Contents/Resources/LICENSE.txt"
  cp "$client/THIRD-PARTY-NOTICES.txt" "$app/Contents/Resources/THIRD-PARTY-NOTICES.txt"
  chmod 755 "$app/Contents/MacOS/Custos" "$app/Contents/Resources/uninstall.sh"
  sed -e "s/__VERSION__/$version/g" -e "s/__ARCH__/$arch/g" "$here/Info.plist" > "$app/Contents/Info.plist"
  plutil -lint "$app/Contents/Info.plist"
  cp "$here/org.custos.agent.plist" "$root/Library/LaunchDaemons/org.custos.agent.plist"
  cp "$here/org.custos.menu.plist" "$root/Library/LaunchAgents/org.custos.menu.plist"
  plutil -lint "$root/Library/LaunchDaemons/org.custos.agent.plist" "$root/Library/LaunchAgents/org.custos.menu.plist"
  lipo "$app/Contents/MacOS/Custos" -verify_arch "$arch"

  step "Ad-hoc signature for $arch"
  others=$(find "$app/Contents/MacOS" -type f | while read -r f; do file -b "$f" | grep -q 'Mach-O' || echo "$f"; done)
  if [ -n "$others" ]; then
    echo "Contents/MacOS may hold only programs and libraries; these are not:" >&2
    echo "$others" >&2
    exit 1
  fi
  # Every library must run on this kind of Mac (one of the processor or universal).
  find "$app/Contents/MacOS" -type f | while read -r f; do
    lipo "$f" -verify_arch "$arch" || { echo "$f has no $arch code" >&2; exit 1; }
  done
  # The libraries first (Avalonia, Skia, SQLite, 7-Zip), then the bundle, which signs the program itself with them.
  find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Custos" | while read -r f; do
    codesign --force --sign - --timestamp=none "$f"
  done
  codesign --force --sign - --timestamp=none "$app"
  codesign --verify --strict --verbose=2 "$app"

  step "The $arch part of the installer"
  # The bundle must land in /Applications even if someone moved an older copy: not relocatable.
  pkgbuild --analyze --root "$root" "$work/$arch/components.plist"
  plutil -replace 0.BundleIsRelocatable -bool NO "$work/$arch/components.plist"
  pkgbuild --root "$root" --component-plist "$work/$arch/components.plist" --scripts "$here/scripts" \
    --identifier "org.custos.pkg.$arch" --version "$version" --install-location / "$work/Custos-$arch.pkg"
done

step "Custos-$version.pkg"
mkdir -p "$work/resources"
cp -R "$here/resources/." "$work/resources/"
for lang in en ru; do
  cp "$repo/LICENSE" "$work/resources/$lang.lproj/License.txt"
done
sed "s/__VERSION__/$version/g" "$here/distribution.xml" > "$work/distribution.xml"
productbuild --distribution "$work/distribution.xml" --resources "$work/resources" --package-path "$work" \
  "$out/Custos-$version.pkg"
pkgutil --check-signature "$out/Custos-$version.pkg" || true
# Both parts must be inside.
rm -rf "$work/expanded"
pkgutil --expand "$out/Custos-$version.pkg" "$work/expanded"
for arch in $archs; do
  [ -f "$work/expanded/Custos-$arch.pkg/Payload" ] || { echo "Custos-$version.pkg has no $arch part" >&2; exit 1; }
done

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
