#!/bin/bash
# Installs or updates CUSTOS for Linux, the backup agent of a Linux server: the program in /opt/custos, the command
# custos, the systemd service custos. Run as root:
#
#   bash install.sh ['<connection line>'] [--archive custos-linux-x64.tar.gz | --url <address of the archive>]
#
# The connection line (address#key#fingerprint, from the monitoring site) connects the agent to the site; without it the
# agent works on its own until «custos connect '<line>'». Without --archive or --url the archive of this processor is
# downloaded from the newest release of CUSTOS on GitHub. Settings, the database and the logs (/var/lib/custos,
# /var/log/custos) are kept when the program is installed again.
#
# Needs: x86_64 or aarch64, glibc 2.28 or newer (Ubuntu 20.04+, Debian 10+, RHEL/AlmaLinux/Rocky/Oracle 8+,
# openSUSE 15+), systemd. Installs libicu (languages for .NET), OpenSSL (the connection to the site) and cifs-utils
# (network folders) when they are missing. --packages-only installs those and stops (CI checks it in containers).
set -euo pipefail

program=/opt/custos
command_link=/usr/local/bin/custos
unit=/etc/systemd/system/custos.service
repository=openpass1/custos

ru=false
case "${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}" in ru*) ru=true ;; esac
say() { if $ru; then echo "$2"; else echo "$1"; fi; }
fail() { if $ru; then echo "Ошибка: $2" >&2; else echo "Error: $1" >&2; fi; exit 1; }

line=""
archive=""
url=""
packages_only=false
while [ $# -gt 0 ]; do
  case "$1" in
    --archive) archive=${2:?}; shift 2 ;;
    --url) url=${2:?}; shift 2 ;;
    --packages-only) packages_only=true; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    -*) fail "unknown option $1" "неизвестный параметр $1" ;;
    *) line=$1; shift ;;
  esac
done

[ "$(id -u)" = 0 ] || fail "run it as root (sudo)" "запустите от root (через sudo)"
[ "$(uname -s)" = Linux ] || fail "this is not Linux" "это не Linux"
$packages_only || [ -d /run/systemd/system ] || fail "systemd is not running here; CUSTOS runs as a systemd service" \
  "здесь не работает systemd, а CUSTOS работает как служба systemd"

case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) fail "the processor $(uname -m) is not supported (x86_64 and aarch64 are)" "процессор $(uname -m) не поддерживается (только x86_64 и aarch64)" ;;
esac

glibc=$(getconf GNU_LIBC_VERSION 2> /dev/null | awk '{print $2}' || true)
if [ -z "$glibc" ]; then
  fail "no glibc found (Alpine and other musl systems are not supported)" "не найдена glibc (Alpine и другие системы на musl не поддерживаются)"
fi
if [ "$(printf '%s\n2.28\n' "$glibc" | sort -V | head -n 1)" != 2.28 ]; then
  fail "glibc $glibc is too old: 2.28 or newer is needed (CentOS 7 and Ubuntu 18.04 cannot run CUSTOS)" \
    "glibc $glibc слишком старая, нужна 2.28 или новее (на CentOS 7 и Ubuntu 18.04 CUSTOS не работает)"
fi

# ---- Packages the program needs ----------------------------------------------------------------------------------
# grep reads the whole list (no -q): with pipefail, ldconfig cut off by an early grep would make a found library missing.
has_library() { ldconfig -p 2> /dev/null | grep "$1" > /dev/null; }
has_icu() { has_library 'libicuuc\.so\.[0-9]'; }
has_ssl() { has_library 'libssl\.so\.[0-9]'; }
install_packages() {
  if command -v apt-get > /dev/null; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq > /dev/null || true
    local wanted=()
    for name in "$@"; do
      if [ "$name" = libicu ]; then
        # The package has the version in its name: libicu72, libicu74 …
        name=$(apt-cache pkgnames libicu 2> /dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1)
        [ -n "$name" ] || continue
      fi
      wanted+=("$name")
    done
    [ ${#wanted[@]} -eq 0 ] || apt-get install -y -qq "${wanted[@]}" > /dev/null
  elif command -v dnf > /dev/null; then
    dnf -y -q install "$@" > /dev/null
  elif command -v yum > /dev/null; then
    yum -y -q install "$@" > /dev/null
  elif command -v zypper > /dev/null; then
    local wanted=()
    for name in "$@"; do
      if [ "$name" = libicu ]; then
        # libicu73_2 on newer releases, libicu-suse65_1 on older ones.
        name=$(zypper -q search -t package 'libicu*' 2> /dev/null | awk -F'|' '{gsub(/ /,"",$2); print $2}' | grep -E '^libicu(-suse)?[0-9_]+$' | sort -V | tail -n 1)
        [ -n "$name" ] || continue
      fi
      wanted+=("$name")
    done
    [ ${#wanted[@]} -eq 0 ] || zypper -n -q install "${wanted[@]}" > /dev/null
  else
    return 1
  fi
}

missing=()
has_icu || missing+=(libicu)
has_ssl || missing+=(openssl)
command -v mount.cifs > /dev/null || [ -x /sbin/mount.cifs ] || [ -x /usr/sbin/mount.cifs ] || missing+=(cifs-utils)
command -v tar > /dev/null || missing+=(tar)
command -v gzip > /dev/null || missing+=(gzip)
command -v curl > /dev/null || [ -n "$archive" ] || $packages_only || missing+=(curl)
if [ ${#missing[@]} -gt 0 ]; then
  say "Installing: ${missing[*]}" "Устанавливаю: ${missing[*]}"
  install_packages "${missing[@]}" || say "Could not install ${missing[*]}: install them with the package manager of the system." \
    "Не удалось установить ${missing[*]}: установите их менеджером пакетов системы."
fi
# Without OpenSSL the service cannot make the key of its connection to the site and does not start. Without libicu it
# runs in English with formats of no country.
has_ssl || fail "OpenSSL (libssl) is missing: install the package openssl" "нет OpenSSL (libssl): установите пакет openssl"
if $packages_only; then
  has_icu || fail "libicu is still missing" "libicu так и не установлена"
  exit 0
fi

# ---- The archive -------------------------------------------------------------------------------------------------
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
if [ -z "$archive" ]; then
  if [ -z "$url" ]; then
    # The newest release of the program (client-v…) that has the archive of this processor.
    url=$(curl -fsSL "https://api.github.com/repos/$repository/releases?per_page=30" \
      | grep -o "\"browser_download_url\": *\"[^\"]*/download/client-v[^\"]*/custos-linux-$arch.tar.gz\"" \
      | head -n 1 | sed -E 's/.*"(https:[^"]+)"/\1/' || true)
    [ -n "$url" ] || fail "no release of CUSTOS for Linux on GitHub yet; give the archive with --archive" \
      "на GitHub ещё нет выпуска CUSTOS для Linux, укажите архив через --archive"
  fi
  say "Downloading $url" "Скачиваю $url"
  curl -fSL --retry 3 -o "$work/custos.tar.gz" "$url"
  archive="$work/custos.tar.gz"
fi
[ -f "$archive" ] || fail "no file $archive" "нет файла $archive"

mkdir "$work/new"
tar -xzf "$archive" -C "$work/new"
[ -x "$work/new/custos" ] || fail "$archive is not CUSTOS for Linux" "$archive — это не CUSTOS для Linux"
if ! version=$("$work/new/custos" version 2>&1); then
  fail "the program does not start here: $(echo "$version" | head -n 3)" "программа здесь не запускается: $(echo "$version" | head -n 3)"
fi

# ---- Install -----------------------------------------------------------------------------------------------------
if systemctl is-active --quiet custos 2> /dev/null; then
  say "Stopping the service to replace the program" "Останавливаю службу, чтобы заменить программу"
  systemctl stop custos
fi
rm -rf "$program.new" "$program.old"
mkdir -p "$program.new"
cp -a "$work/new/." "$program.new/"
chown -R root:root "$program.new"
chmod 755 "$program.new"
[ -d "$program" ] && mv "$program" "$program.old"
mv "$program.new" "$program"
rm -rf "$program.old"
ln -sfn "$program/custos" "$command_link"

# SELinux (RHEL and its kin): the program must be one systemd may start.
if command -v selinuxenabled > /dev/null && selinuxenabled; then
  if command -v semanage > /dev/null; then
    semanage fcontext -a -t bin_t "$program/custos" 2> /dev/null || semanage fcontext -m -t bin_t "$program/custos" 2> /dev/null || true
  fi
  restorecon -RF "$program" 2> /dev/null || true
  chcon -t bin_t "$program/custos" 2> /dev/null || true
fi

cp "$program/custos.service" "$unit"
chmod 644 "$unit"
# Without libicu the program itself runs in .NET's invariant mode: English texts only, formats of no country.
has_icu || say "libicu is missing: CUSTOS runs without languages (English only)." "Нет libicu: CUSTOS работает без языков (только по-английски)."
systemctl daemon-reload
systemctl enable --now custos > /dev/null 2>&1 || systemctl enable --now custos

# The socket of the service appears once it has started.
for _ in $(seq 1 30); do
  [ -S /run/custos.sock ] && break
  sleep 1
done
[ -S /run/custos.sock ] || fail "the service did not start: journalctl -u custos" "служба не запустилась: journalctl -u custos"
say "CUSTOS $version is installed: the service custos runs." "CUSTOS $version установлен, служба custos работает."

if [ -n "$line" ]; then
  "$command_link" connect "$line" || true
fi

say "
Next:
  custos status       the service and its tasks
  custos snapshots    whether the disks can have snapshots
  custos help         all commands
Tasks are created on the monitoring site. To remove CUSTOS: $program/uninstall.sh" "
Дальше:
  custos status       служба и её задания
  custos snapshots    можно ли делать снимки дисков
  custos help         все команды
Задания создаются на сайте мониторинга. Удалить CUSTOS: $program/uninstall.sh"
