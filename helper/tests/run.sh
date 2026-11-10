#!/bin/bash
# Builds a test server image from a distribution and runs the custos-helper tests in it.
# Usage, from anywhere: helper/tests/run.sh <base image> "<database engines>"
# Example: helper/tests/run.sh debian:12 "mariadb postgresql"
set -euo pipefail
base=${1:?usage: run.sh <base image> "<database engines>"}
engines=${2-}
helper=$(cd "$(dirname "$0")/.." && pwd)
tag=custos-helper-test:$(printf '%s-%s' "$base" "$engines" | tr -c 'a-z0-9.' '-')
docker build --quiet --build-arg BASE="$base" --build-arg ENGINES="$engines" --tag "$tag" "$helper/tests" > /dev/null
# SYS_NICE: RHEL's /usr/libexec/mysqld carries the cap_sys_nice file capability, and a
# container without it in its bounding set refuses to run the binary at all.
docker run --rm --cap-add SYS_NICE --volume "$helper:/helper:ro" "$tag" bash /helper/tests/test.sh
