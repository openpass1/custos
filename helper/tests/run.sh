#!/bin/bash
# Builds a test server image from a distribution and runs the custos-helper tests in it.
# Usage, from anywhere: helper/tests/run.sh <base image> "<database engines>"
# Example: helper/tests/run.sh debian:12 "mariadb postgresql"
set -euo pipefail
base=${1:?usage: run.sh <base image> "<database engines>"}
engines=${2-}
helper=$(cd "$(dirname "$0")/.." && pwd)
tag=custos-helper-test:$(printf '%s-%s' "$base" "$engines" | tr -c 'a-z0-9.' '-')
# The build log is shown only when the build fails, e.g. when a package mirror is down.
log=$(mktemp)
trap 'rm -f "$log"' EXIT
if ! docker build --progress=plain --build-arg BASE="$base" --build-arg ENGINES="$engines" \
    --tag "$tag" "$helper/tests" > "$log" 2>&1; then
  tail -n 80 "$log" >&2
  exit 1
fi
# SYS_NICE: RHEL's /usr/libexec/mysqld carries the cap_sys_nice file capability, and a
# container without it in its bounding set refuses to run the binary at all.
docker run --rm --cap-add SYS_NICE --volume "$helper:/helper:ro" "$tag" bash /helper/tests/test.sh
