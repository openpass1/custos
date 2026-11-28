#!/bin/bash
# Check the real ZIP builder with small stand-ins for the Linux release assets.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

version=1.5.0
out="$work/release output"
desktop="$work/desktop packages"
zip="$out/custos-linux-$version.zip"
descriptor=linux-desktop-packages.json
archives=(custos-linux-x64.tar.gz custos-linux-arm64.tar.gz)
packages=(
  "custos-desktop_${version}_amd64.deb"
  "custos-desktop_${version}_arm64.deb"
  "custos-desktop-$version-1.x86_64.rpm"
  "custos-desktop-$version-1.aarch64.rpm"
)

mkdir -p "$out" "$desktop"
for name in "${archives[@]}"; do
  printf '%s\n' "$name" > "$work/payload"
  tar -czf "$out/$name" -C "$work" payload
done
for name in "${packages[@]}" "custos-linux-desktop-$version-x64.tar.gz" 7z.so; do
  printf 'Release test asset %s\n' "$name" > "$desktop/$name"
done

verify_manifest() {
  local names=("$@") name sha
  unzip -p "$zip" manifest.json > "$work/manifest.json"
  jq -e --arg version "$version" --argjson count "${#names[@]}" '
    .version == $version and .edition == "linux" and .min_protocol == 1 and (.files | length) == $count
  ' "$work/manifest.json" > /dev/null
  for name in "${names[@]}"; do
    sha=$(unzip -p "$zip" "$name" | sha256sum)
    jq -e --arg name "$name" --arg sha "${sha%% *}" '
      .files | any(.path == $name and .sha256 == $sha)
    ' "$work/manifest.json" > /dev/null
  done
}

bash "$here/build-linux.sh" package "$version" "$out" "$desktop"
diff -u <(printf '%s\n' manifest.json "${archives[@]}" "$descriptor") <(unzip -Z1 "$zip")
verify_manifest "${archives[@]}" "$descriptor"
unzip -p "$zip" "$descriptor" > "$work/descriptor.json"
jq -e --arg version "$version" '.version == $version and (.packages | length) == 4' "$work/descriptor.json" > /dev/null
for name in "${packages[@]}"; do
  sha=$(sha256sum < "$desktop/$name")
  size=$(stat -c %s "$desktop/$name")
  jq -e --arg name "$name" --arg sha "${sha%% *}" --argjson size "$size" '
    .packages[$name].sha256 == $sha and .packages[$name].size == $size
  ' "$work/descriptor.json" > /dev/null
done

archive_sha=$(sha256sum < "$zip")
rm -- "$desktop/${packages[0]}"
if bash "$here/build-linux.sh" package "$version" "$out" "$desktop"; then
  echo "The builder accepted an incomplete desktop release." >&2
  exit 1
fi
test "$(sha256sum < "$zip")" = "$archive_sha"

bash "$here/build-linux.sh" package "$version" "$out"
diff -u <(printf '%s\n' manifest.json "${archives[@]}") <(unzip -Z1 "$zip")
verify_manifest "${archives[@]}"
echo "Linux update ZIP smoke passed with and without the desktop descriptor."
