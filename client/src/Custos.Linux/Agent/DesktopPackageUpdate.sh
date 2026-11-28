#!/bin/sh
# Runs in a systemd unit separate from the package-owned agent being updated.
set -efu
umask 077
export PATH=/usr/sbin:/usr/bin:/sbin:/bin LC_ALL=C

# APT supplies its actual transaction with its native locks held. Both steps must target the
# same installed package and the exact local archive. Even a second dependency upgrade is refused.
if [ "${1:-}" = apt-plan ]; then
    IFS= read -r protocol && [ "$protocol" = 'VERSION 2' ] || exit 1
    while IFS= read -r line && [ -n "$line" ]; do :; done
    unpacked=false
    configured=false
    while IFS=' ' read -r name before relation after action; do
        case "$name" in custos-desktop|"custos-desktop:$CUSTOS_NATIVE_ARCH") ;; *) exit 1 ;; esac
        [ "$before" = "$CUSTOS_NATIVE_FROM" ] && [ "$relation" = '<' ] && [ "$after" = "$CUSTOS_NATIVE_TO" ] || exit 1
        case "$action" in
            "$CUSTOS_NATIVE_FILE") [ "$unpacked" = false ] || exit 1; unpacked=true ;;
            '**CONFIGURE**') [ "$configured" = false ] || exit 1; configured=true ;;
            *) exit 1 ;;
        esac
    done
    [ "$unpacked" = true ] && [ "$configured" = true ]
    exit
fi

recover=false
if [ "${1:-}" = recover ]; then recover=true; shift; fi
[ "$#" -eq 7 ] && [ "$(id -u)" -eq 0 ] || exit 1
package=$1 result=$2 from=$3 to=$4 log=$6 expected=$7
desktop=custos-desktop unit=custos.service vendor=/usr/lib/systemd/system/custos.service
case "$5" in custos|custos.service) ;; *) exit 1 ;; esac
case "$from:$to:$expected" in *[!0-9A-Za-z.+~_:-]*|:*|*::*) exit 1 ;; esac
case "$0" in /*) ;; *) exit 1 ;; esac
work=$(dirname -- "$0")
[ "$(stat -c '%u:%a' -- "$work")" = '0:700' ] && [ ! -L "$work" ] || exit 1
updates=$(dirname -- "$work")
[ "$(stat -c '%u:%a' -- "$updates")" = '0:700' ] && [ ! -L "$updates" ] || exit 1
[ "$(dirname -- "$package")" = "$work" ] || exit 1
case "$result:$log" in /*:/*) ;; *) exit 1 ;; esac
[ ! -L "$result" ] && [ ! -L "$log" ] || exit 1

say() { printf '%s\n' "$*" >&2; }

finish() {
    temp=$(mktemp -- "$result.XXXXXX") || exit 1
    printf '{"ok":%s,"from_version":"%s","to_version":"%s","message":%s,"rolled_back":false}\n' \
        "$1" "$from" "$to" "$2" >"$temp"
    mv -fT -- "$temp" "$result"
}

case "$package" in
    *.deb) kind=deb; program=/usr/lib/custos-desktop/agent/custos ;;
    *.rpm) kind=rpm; program=/usr/libexec/custos-desktop/agent/custos ;;
    *) exit 1 ;;
esac

native_version() {
    if [ "$kind" = deb ]; then
        metadata=$(dpkg-query -W -f='${db:Status-Status} ${Architecture} ${Version}\n' -- "$desktop") || return 1
        set -- $metadata
        [ "$#" -eq 3 ] && [ "$1" = installed ] && [ "$2" = "$arch" ] || return 1
        printf '%s\n' "$3"
    else
        metadata=$(rpm -q --qf '%{NAME} %{ARCH} %{EPOCHNUM} %{VERSION}-%{RELEASE}\n' -- "$desktop") || return 1
        set -- $metadata
        [ "$#" -eq 4 ] && [ "$1" = "$desktop" ] && [ "$2" = "$arch" ] && [ "$3" = 0 ] || return 1
        printf '%s\n' "$4"
    fi
}

owns_path() {
    if [ "$kind" = deb ]; then
        owner=$(dpkg-query -S -- "$1") || return 1
        [ "$owner" = "$desktop: $1" ] || [ "$owner" = "$desktop:$arch: $1" ]
    else
        [ "$(rpm -qf --qf '%{NAME}\n' -- "$1")" = "$desktop" ]
    fi
}

owns_unit() {
    # Keep external installations and local replacements out of both upgrade and recovery.
    for path in /etc/systemd/system.control /run/systemd/system.control /run/systemd/transient \
        /etc/systemd/system /run/systemd/system /usr/local/lib/systemd/system; do
        [ ! -e "$path/$unit" ] && [ ! -L "$path/$unit" ] || return 1
    done
    owns_path "$vendor" && owns_path "$program" || return 1
    [ -f "$vendor" ] && [ -x "$program" ] || return 1
    fragment=$(systemctl show --property=FragmentPath --value -- "$unit") || return 1
    [ -n "$fragment" ] && [ "$(readlink -f -- "$fragment")" = "$vendor" ] || return 1
    command_line=$(systemctl show --property=ExecStart --value -- "$unit") || return 1
    case "$command_line" in "{ path=$program ; argv[]=$program agent --systemd ;"*) return 0 ;; esac
    return 1
}

running_agent() {
    systemctl is-active --quiet -- "$unit" || return 1
    pid=$(systemctl show --property=MainPID --value -- "$unit") || return 1
    case "$pid" in ''|0|*[!0-9]*) return 1 ;; esac
    [ "$(readlink -- "/proc/$pid/exe")" = "$program" ]
}

start_owned() {
    owns_unit && timeout 15s systemctl daemon-reload && owns_unit && timeout 45s systemctl start -- "$unit"
}

healthy() {
    [ "$(native_version)" = "$1" ] && owns_unit && [ "$(timeout 10s "$program" version)" = "$2" ] || return 1
    for attempt in 1 2 3 4 5 6 7 8; do
        if running_agent && timeout 3s "$program" status >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    return 1
}

restore_service() {
    [ -f "$work/original-native" ] || return 0
    previous=$(cat -- "$work/original-native")
    actual=$(native_version) || return 1
    case "$actual" in
        "$previous"|"$expected") start_owned ;;
        *) return 1 ;;
    esac
}

with_recovery() {
    message=$1
    if ! restore_service; then
        say 'Could not restart the original package-owned CUSTOS unit.'
        message='"The package update failed and the CUSTOS service could not safely be restarted. Check the package manager and the local update log."'
    fi
    finish false "$message"
}

exec >>"$log" 2>&1
fail() { say "$1"; finish false "\"$1\""; exit 1; }
[ "$recover" = false ] || [ ! -f "$result" ] || exit 0
exec 9>>"$updates/desktop-package-update.lock"
flock -n 9 || fail 'Another CUSTOS package update is running.'

if [ "$kind" = deb ]; then
    arch=$(dpkg --print-architecture) || fail 'Could not determine the Debian package architecture.'
else
    arch=$(rpm --eval '%{_arch}') || fail 'Could not determine the RPM package architecture.'
fi

if [ "$recover" = true ]; then
    if [ ! -f "$result" ]; then
        say 'The package updater exited before writing a result.'
        if [ -f "$work/install-finished" ] && start_owned && healthy "$expected" "$to"; then
            finish true null
        else
            with_recovery '"The package updater stopped before reporting a result. Check the local update log."'
        fi
    fi
    exit 0
fi

[ -f "$package" ] && [ ! -L "$package" ] || fail 'The verified package file is missing.'
case "$kind:$arch:$(uname -m)" in deb:amd64:x86_64|deb:arm64:aarch64|rpm:x86_64:x86_64|rpm:aarch64:aarch64) ;;
    *) fail 'The package manager architecture does not match this machine.' ;;
esac
if [ "$kind" = deb ]; then
    [ "$(dpkg-deb -f "$package" Package)" = "$desktop" ] &&
    [ "$(dpkg-deb -f "$package" Architecture)" = "$arch" ] &&
    [ "$(dpkg-deb -f "$package" Version)" = "$expected" ] || fail 'The Debian package identity does not match the verified update.'
else
    [ "$(rpm -qp --qf '%{NAME} %{ARCH} %{EPOCHNUM} %{VERSION}-%{RELEASE}' -- "$package")" = "$desktop $arch 0 $expected" ] ||
        fail 'The RPM package identity does not match the verified update.'
    obsoletes=$(rpm -qp --obsoletes -- "$package") || fail 'Could not read the requirements of the RPM update.'
    [ -z "$obsoletes" ] || fail 'The RPM update would permit replacement of another package.'
fi

current=$(native_version) || fail 'The CUSTOS desktop package is not installed for this architecture.'
case "$current" in ''|*[!0-9A-Za-z.+~_:-]*) fail 'The installed package version is invalid.' ;; esac
if [ "$kind" = deb ]; then
    dpkg --compare-versions "$expected" gt "$current" || fail 'The update is not newer than the installed Debian package.'
else
    [ "$(rpm --eval "%{lua: print(rpm.vercmp(\"$expected\", \"$current\"))}")" = 1 ] ||
        fail 'The update is not newer than the installed RPM package.'
    rpm --freshen --test -- "$package" || fail 'The RPM upgrade cannot run without changing dependencies.'
fi

owns_unit && running_agent && [ "$(timeout 10s "$program" version)" = "$from" ] ||
    fail 'The running CUSTOS agent is not owned by the installed desktop package.'

# The marker is written before stopping the agent. ExecStopPost can recover even from a killed updater.
printf '%s\n' "$current" >"$work/original-native.tmp"
mv -fT -- "$work/original-native.tmp" "$work/original-native"
if ! timeout 75s systemctl stop -- "$unit"; then
    with_recovery '"Could not stop the CUSTOS service for the package update."'
    exit 1
fi

install_package() {
    if [ "$kind" = rpm ]; then
        rpm --freshen -- "$package" </dev/null
        return
    fi

    # Local-only APT inputs. --no-download breaks local archive paths on APT 3, so provide empty sources and indexes.
    mkdir -p -- "$work/apt-sources" "$work/apt-lists"
    : >"$work/apt-sources.list"
    export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=l
    export CUSTOS_NATIVE_ARCH="$arch" CUSTOS_NATIVE_FROM="$current" CUSTOS_NATIVE_TO="$expected"
    export CUSTOS_NATIVE_FILE="$package" CUSTOS_UPDATE_HELPER="$0"
    apt-get -y --only-upgrade --no-remove --no-install-recommends \
        -o "Dir::Etc::SourceList=$work/apt-sources.list" -o "Dir::Etc::SourceParts=$work/apt-sources" \
        -o "Dir::State::Lists=$work/apt-lists" -o DPkg::Lock::Timeout=0 \
        -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold -o Dpkg::Options::=--no-force-downgrade \
        -o 'DPkg::Pre-Install-Pkgs::=/bin/sh "$CUSTOS_UPDATE_HELPER" apt-plan' \
        -o 'DPkg::Tools::Options::/bin/sh::Version=2' install "$package" </dev/null
}

if ! install_package; then
    with_recovery '"The package manager refused or failed the CUSTOS package upgrade. Check the local update log."'
    exit 1
fi
: >"$work/install-finished"

if ! start_owned || ! healthy "$expected" "$to"; then
    with_recovery '"The package manager finished, but the updated CUSTOS service did not pass its health check."'
    exit 1
fi
finish true null
