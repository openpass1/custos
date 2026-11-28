#!/bin/sh
# Exercises the native updater against the Debian package installed on this disposable runner.
set -eu

original=$1
version=$2
helper=$(realpath -- "$(dirname -- "$0")/../../src/Custos.Linux/Agent/DesktopPackageUpdate.sh")
scratch=$(mktemp -d /var/lib/custos/updates/native-smoke.XXXXXX)
trap 'rm -rf -- "$scratch"' EXIT

dpkg-deb -R "$original" "$scratch/payload"
sed -i "s/^Version: .*/Version: $version/" "$scratch/payload/DEBIAN/control"
dpkg-deb --root-owner-group -Zgzip -z1 --build "$scratch/payload" "$scratch/new.deb"
installed_version() { dpkg-query -W -f='${Version}' custos-desktop; }
pid() { systemctl show --property=MainPID --value -- custos.service; }

prepare() {
    work="$scratch/$1"
    install -d -m 0700 -- "$work"
    install -m 0700 -- "$helper" "$work/updater.sh"
    install -m 0600 -- "$2" "$work/package.deb"
    result="$work/update-result.json"
    log="$work/update.log"
    unit="custos-$(basename -- "$scratch")-$1"
    recovery="ExecStopPost=/bin/sh $work/updater.sh recover $work/package.deb $result $version $version custos $log $3"
}

run_updater() {
    systemd-run --quiet --wait --collect --service-type=exec --unit="$unit" --property="$recovery" \
        --property=RuntimeMaxSec=3min /bin/sh "$work/updater.sh" "$work/package.deb" "$result" \
        "$version" "$version" custos "$log" "$1"
}

failed() {
    printf 'Native update smoke failed for %s.\n' "$unit" >&2
    if [ -r "$log" ]; then cat "$log" >&2; else printf 'The updater left no local log.\n' >&2; fi
    if [ -r "$result" ]; then cat "$result" >&2; else printf 'The updater left no result.\n' >&2; fi
    journalctl --unit="$unit.service" --unit=custos.service --lines=80 --no-pager >&2 || true
    exit 1
}

expect_result() { grep -F -- "$1" "$result" || failed; }

previous=$(installed_version)
before=$(pid)
prepare identity "$scratch/new.deb" "$version.0"
if run_updater "$version.0"; then failed; fi
expect_result 'Debian package identity does not match'
test "$(pid)" = "$before" || failed
test "$(installed_version)" = "$previous" || failed

prepare upgrade "$scratch/new.deb" "$version"
run_updater "$version" || failed
expect_result '"ok":true'
test "$(installed_version)" = "$version" || failed
test "$(pid)" != "$before" || failed
custos status > /dev/null || failed

prepare downgrade "$original" "$previous"
before=$(pid)
if run_updater "$previous"; then failed; fi
expect_result 'not newer than the installed Debian package'
test "$(pid)" = "$before" || failed
test "$(installed_version)" = "$version" || failed

# Death of the transient updater after stopping the agent must leave an atomic failure and restart the same package.
prepare interrupted "$scratch/new.deb" "$version"
printf '%s\n' "$version" > "$work/original-native"
if systemd-run --quiet --wait --collect --service-type=exec --unit="$unit" --property="$recovery" \
    /bin/sh -c 'systemctl stop -- custos.service && kill -KILL $$'; then failed; fi
expect_result '"ok":false'
expect_result 'stopped before reporting a result'
test "$(pid)" != "$before" || failed
test "$(installed_version)" = "$version" || failed
timeout 20 sh -c 'until custos status >/dev/null 2>&1; do sleep 1; done' || failed
