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

previous=$(installed_version)
before=$(pid)
prepare identity "$scratch/new.deb" "$version.0"
if run_updater "$version.0"; then exit 1; fi
grep -F 'Debian package identity does not match' "$result"
test "$(pid)" = "$before"
test "$(installed_version)" = "$previous"

prepare upgrade "$scratch/new.deb" "$version"
run_updater "$version" || { cat "$log"; exit 1; }
grep -F '"ok":true' "$result"
test "$(installed_version)" = "$version"
test "$(pid)" != "$before"
custos status > /dev/null

prepare downgrade "$original" "$previous"
before=$(pid)
if run_updater "$previous"; then exit 1; fi
grep -F 'not newer than the installed Debian package' "$result"
test "$(pid)" = "$before"
test "$(installed_version)" = "$version"

# Death of the transient updater after stopping the agent must leave an atomic failure and restart the same package.
prepare interrupted "$scratch/new.deb" "$version"
printf '%s\n' "$version" > "$work/original-native"
if systemd-run --quiet --wait --collect --service-type=exec --unit="$unit" --property="$recovery" \
    /bin/sh -c 'systemctl stop -- custos.service && kill -KILL $$'; then exit 1; fi
grep -F '"ok":false' "$result"
grep -F 'stopped before reporting a result' "$result"
test "$(pid)" != "$before"
test "$(installed_version)" = "$version"
timeout 20 sh -c 'until custos status >/dev/null 2>&1; do sleep 1; done'
