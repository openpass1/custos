#!/usr/bin/env bash
# CUSTOS Monitor: installs the monitoring site on a clean Debian or Ubuntu server, or updates an installed one.
# Copyright (C) 2026 CUSTOS contributors. Licensed under GPL-3.0.
#
#   bash install.sh             install: nginx, MariaDB, PHP, the site, cron; asks for the site address and the
#                               first administrator
#   bash install.sh --update    update the site to the newest release
#   bash install.sh --help      all options
#
# Run as root (on Ubuntu: sudo bash install.sh). Running it again is safe: settings, keys, the database and users
# are kept. Messages are in English, like the other server scripts.

# The whole script is one block, so bash reads all of it before running any of it: an update may unpack a new copy
# of this very file over the one that is running (server/install.sh is in the package).
{

set -euo pipefail
shopt -s inherit_errexit

REPO=openpass1/custos
SUPPORTED='Debian 12, Debian 13, Ubuntu 24.04, Ubuntu 26.04'
STATE_FILE=/etc/custos/install.conf
DB_NAME=custos
DB_USER=custos

mode=install
dir=/var/www/custos
dir_given=false
site_user=custos
user_given=false
php_version=
package=
url=
admin=
language=
timezone=
https=false
email=
assume_yes=false
force=false

usage() {
    cat <<'EOF'
Usage: bash install.sh [options]

Installs CUSTOS Monitor on a clean Debian 12/13 or Ubuntu 24.04/26.04 server, desktop editions too (a newer Debian
or Ubuntu asks whether to go on): nginx, MariaDB, PHP-FPM, the site in /var/www/custos (run by the user "custos"),
its database and the cron job. With --update, updates a site that is already installed.

Options:
  --update             update the installed site to the newest release (files and database), keep everything else;
                       from 0.4.11 on this goes through bin/update.php, which puts the old version back by itself
                       when anything fails
  --package=FILE       use this custos-server zip instead of downloading the newest release from GitHub
  --url=URL            address of the site as people and agents open it, for example http://custos.lan or
                       http://192.0.2.10 (asked if not given)
  --admin=LOGIN        login of the first administrator; the password comes from the environment variable
                       CUSTOS_ADMIN_PASSWORD (without --admin both are asked)
  --language=ru|en     default language of the site (default: ru)
  --timezone=ZONE      time zone of the site, for example Europe/Berlin (default: the server's)
  --https              get a Let's Encrypt certificate for the site name (the name must be public and point to
                       this server); --email=ADDRESS gives the address for expiry notices
  --dir=DIR            folder that holds server/ (default /var/www/custos); with --update this may be another
                       installation, for example a CloudPanel site: --dir=/home/custos/htdocs/custos.lan
  --user=NAME          user that runs the site (default custos; with --update: the owner of the folder)
  --php=VERSION        PHP version to use with --update, for example 8.4 (default: the one chosen at install,
                       otherwise the newest installed)
  --force              with --update: install even if the same version is installed (a repair)
  --yes                ask nothing: needs --url, and --admin with CUSTOS_ADMIN_PASSWORD on a new site
  --help               this text

Environment:
  CUSTOS_ADMIN_PASSWORD  password of the first administrator, with --admin
  GITHUB_TOKEN           token to download releases while the repository is private
EOF
}

say() { printf '\n==> %s\n' "$*"; }
ok() { printf '[ok] %s\n' "$*"; }
warn() { printf '[warning] %s\n' "$*" >&2; }
die() {
    printf '[error] %s\n' "$*" >&2
    # Tells on_error that the reason is already printed, also from inside $(...).
    [ -z "${workdir:-}" ] || : > "$workdir/stopped"
    exit 1
}

for arg in "$@"; do
    case "$arg" in
        --update) mode=update ;;
        --package=*) package=${arg#*=} ;;
        --url=*) url=${arg#*=} ;;
        --admin=*) admin=${arg#*=} ;;
        --language=*) language=${arg#*=} ;;
        --timezone=*) timezone=${arg#*=} ;;
        --https) https=true ;;
        --email=*) email=${arg#*=} ;;
        --dir=*) dir=${arg#*=}; dir=${dir%/}; dir_given=true ;;
        --user=*) site_user=${arg#*=}; user_given=true ;;
        --php=*) php_version=${arg#*=}; php_version=${php_version#php} ;;
        --force) force=true ;;
        --yes) assume_yes=true ;;
        --help | -h) usage; exit 0 ;;
        *) die "Unknown option: $arg (see bash install.sh --help)" ;;
    esac
done

[ "$(id -u)" = 0 ] || die "Run this script as root (on Ubuntu: sudo bash install.sh)."
[ -z "$language" ] || [ "$language" = ru ] || [ "$language" = en ] || die "--language must be ru or en."
[ -z "$package" ] || [ -f "$package" ] || die "Package not found: $package"
[ -z "$php_version" ] || [[ "$php_version" =~ ^[0-9]+\.[0-9]+$ ]] || die "--php must look like 8.4."
[[ "$site_user" =~ ^[a-z_][a-z0-9_-]*$ ]] || die "--user must be a plain user name."

workdir=$(mktemp -d)
chmod 755 "$workdir"
trap 'rm -rf "$workdir"' EXIT

# A command that fails without its own message stops the script; this names it, so the stop is never silent.
set -o errtrace
# shellcheck disable=SC2329 # called by the ERR trap below
on_error() {
    # Once: not again for the line that used a failed $(...), and not after die() has given the reason.
    [ "$BASH_SUBSHELL" = 0 ] && [ ! -e "$workdir/stopped" ] || return 0
    printf '[error] Stopped at line %s of install.sh: %s\n' "$1" "$2" >&2
}
trap 'on_error "$LINENO" "$BASH_COMMAND"' ERR

# ---------------------------------------------------------------------------------------------------------------
# Helpers

# Starts a service, or restarts it when it runs. Without systemd (a container) the init script is used.
service_restart() {
    if [ -d /run/systemd/system ]; then
        systemctl enable --quiet "$1" 2>/dev/null || true
        systemctl restart "$1" || die "Could not start $1: see systemctl status $1 and journalctl -u $1."
    else
        service "$1" restart > "$workdir/service.txt" 2>&1 || { cat "$workdir/service.txt" >&2; die "Could not start $1."; }
    fi
}

service_reload() {
    if [ -d /run/systemd/system ]; then
        systemctl reload "$1"
    else
        service "$1" reload > "$workdir/service.txt" 2>&1 || { cat "$workdir/service.txt" >&2; return 1; }
    fi
}

# Runs a command as the site user, in the server folder.
as_site_user() {
    (cd "$dir/server" && runuser -u "$site_user" -- "$@")
}

github_curl() {
    local auth=()
    if [ -n "${GITHUB_TOKEN:-}" ]; then
        auth=(-H "Authorization: Bearer $GITHUB_TOKEN")
    fi
    curl -fsSL --retry 3 "${auth[@]}" "$@"
}

# Downloads the newest published server release into $workdir and checks it against its SHA256SUMS.txt.
# Prints the path of the zip.
download_release() {
    local releases line name url api_url sums_url sums_api_url
    local php=${php_bin:-php}
    command -v "$php" > /dev/null || die "PHP is needed to read the list of releases."
    releases=$(github_curl -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/releases?per_page=50") \
        || die "Cannot read the releases of github.com/$REPO. If the repository is private, set GITHUB_TOKEN, or download custos-server-<version>.zip by hand and pass --package=FILE."
    # One line: zip name, download URL, API URL of the zip, URL and API URL of SHA256SUMS.txt.
    # shellcheck disable=SC2016 # PHP code: its $ are PHP's
    line=$(printf '%s' "$releases" | "$php" -r '
        $list = json_decode(stream_get_contents(STDIN), true);
        foreach (is_array($list) ? $list : [] as $r) {
            if (!is_array($r) || !empty($r["draft"]) || !empty($r["prerelease"]) || !str_starts_with((string) ($r["tag_name"] ?? ""), "server-v")) {
                continue;
            }
            $zip = $sums = null;
            foreach ($r["assets"] ?? [] as $a) {
                if (preg_match("/^custos-server-[0-9.]+\\.zip$/", $a["name"])) { $zip = $a; }
                if ($a["name"] === "SHA256SUMS.txt") { $sums = $a; }
            }
            if ($zip && $sums) {
                echo implode("\t", [$zip["name"], $zip["browser_download_url"], $zip["url"], $sums["browser_download_url"], $sums["url"]]), "\n";
                break;
            }
        }')
    [ -n "$line" ] || die "No published CUSTOS Monitor release found on github.com/$REPO. Download custos-server-<version>.zip by hand and pass --package=FILE."
    IFS=$'\t' read -r name url api_url sums_url sums_api_url <<< "$line"
    if [ -n "${GITHUB_TOKEN:-}" ]; then
        # A private repository serves release files only through the API.
        url=$api_url
        sums_url=$sums_api_url
    fi
    echo "Downloading $name" >&2
    github_curl -H 'Accept: application/octet-stream' -o "$workdir/$name" "$url" || die "Cannot download $name."
    github_curl -H 'Accept: application/octet-stream' -o "$workdir/SHA256SUMS.txt" "$sums_url" || die "Cannot download SHA256SUMS.txt."
    (cd "$workdir" && grep -E "  $name\$" SHA256SUMS.txt | sha256sum -c --status) \
        || die "The checksum of $name does not match SHA256SUMS.txt: the download is damaged. Run the script again."
    echo "Checksum of $name is correct" >&2
    printf '%s\n' "$workdir/$name"
}

# Copies the package where the site user can read it and checks that it is a CUSTOS Monitor package.
prepare_package() {
    local source=$1 target="$workdir/custos-server.zip"
    [ "$source" = "$target" ] || cp "$source" "$target"
    chmod 644 "$target"
    unzip -l "$target" server/src/App.php > /dev/null 2>&1 \
        || die "$source is not a CUSTOS Monitor package (no server/src/App.php inside)."
    printf '%s\n' "$target"
}

package_version() {
    unzip -p "$1" server/src/App.php | sed -nE "s/.*const VERSION = '([^']+)'.*/\1/p"
}

installed_version() {
    sed -nE "s/.*const VERSION = '([^']+)'.*/\1/p" "$dir/server/src/App.php" 2>/dev/null || true
}

# ---------------------------------------------------------------------------------------------------------------
# Update

if [ "$mode" = update ]; then
    if [ "$dir_given" = false ] && [ -f "$STATE_FILE" ]; then
        dir=$(sed -n 's/^DIR=//p' "$STATE_FILE")
    fi
    [ -f "$dir/server/bin/migrate.php" ] \
        || die "No CUSTOS Monitor in $dir (no server/bin/migrate.php). Give the folder that holds server/ with --dir=."
    [ "$user_given" = true ] || site_user=$(stat -c %U "$dir/server")
    if [ -z "$php_version" ] && [ -f "$STATE_FILE" ]; then
        php_version=$(sed -n 's/^PHP=//p' "$STATE_FILE")
    fi
    if [ -z "$php_version" ]; then
        php_version=$(find /usr/bin -maxdepth 1 -name 'php[0-9]*.[0-9]*' -printf '%f\n' | sed 's/^php//' \
            | grep -E '^[0-9]+\.[0-9]+$' | sort -V | tail -n 1 || true)
    fi
    php_bin=/usr/bin/php$php_version
    [ -x "$php_bin" ] || die "PHP $php_version not found. Give the PHP version of the site with --php=, for example --php=8.4."

    say "Getting the package"
    source_zip=$package
    if [ -z "$source_zip" ]; then
        command -v curl > /dev/null || die "curl is not installed: apt-get install curl"
        source_zip=$(download_release)
    fi
    zip=$(prepare_package "$source_zip")
    old=$(installed_version)
    new=$(package_version "$zip")
    echo "Installed: ${old:-unknown}, package: ${new:-unknown}"
    if [ -n "$old" ] && [ "$old" = "$new" ] && [ "$force" = false ]; then
        ok "Version $old is already installed. Nothing to do (--force installs it again)."
        exit 0
    fi
    if [ -n "$old" ] && [ -n "$new" ] && [ "$old" != "$new" ] \
        && [ "$(printf '%s\n%s\n' "$old" "$new" | sort -V | tail -n 1)" = "$old" ]; then
        # An older package over a newer database is never unpacked: the tables may not fit the old code.
        ok "The package ($new) is older than the installed version $old. Nothing to do. To return to the version before the last update: cd $dir/server && runuser -u $site_user -- php$php_version bin/update.php --rollback"
        exit 0
    fi

    if [ -f "$dir/server/bin/update.php" ]; then
        # 0.4.11 and newer: the same update as the button of the site. It checks the package, PHP and free space,
        # shows the "being updated" page, keeps the old files and the tables it changes, tests the new version and
        # puts the old one back by itself when anything fails.
        say "Updating with bin/update.php (settings, keys and var/ stay; the old version comes back if anything fails)"
        update_args=(--package="$zip" --yes)
        [ "$force" = false ] || update_args+=(--force)
        as_site_user "$php_bin" bin/update.php "${update_args[@]}" \
            || die "The update did not go through: bin/update.php says above what failed and which version the site is on now."
    else
        # Older sites have no bin/update.php: the package is unpacked over them and the database updated.
        say "Unpacking over $dir (settings, keys and var/ are not in the package and stay)"
        runuser -u "$site_user" -- unzip -o -q "$zip" -d "$dir"
        ok "Files of $new are in $dir/server"

        say "Updating the database"
        as_site_user "$php_bin" bin/migrate.php
    fi
    if [ -f "$STATE_FILE" ] && [ -d "/etc/php/$php_version/fpm" ]; then
        service_reload "php$php_version-fpm" || true
    fi
    say "Done: CUSTOS Monitor ${old:-?} -> $new. The version is shown at the bottom of every page of the site."
    exit 0
fi

# ---------------------------------------------------------------------------------------------------------------
# Install: the system

[ -r /etc/os-release ] || die "Unknown system: /etc/os-release is missing. Supported: $SUPPORTED."
# shellcheck disable=SC1091
. /etc/os-release
# True when version $1 is $2 or newer.
version_at_least() {
    local lowest
    lowest=$(printf '%s\n%s\n' "$1" "$2" | sort -V)
    [ "${lowest%%$'\n'*}" = "$2" ]
}

case "${ID:-}:${VERSION_ID:-}" in
    debian:12 | debian:13 | ubuntu:24.04 | ubuntu:26.04) ;;
    *)
        # A newer Debian or Ubuntu (Debian 14, Ubuntu 28.04, Debian testing without a version) is most likely fine:
        # the script uses only the system's own packages there. Older versions and other systems stop here.
        newer=false
        if [ "${ID:-}" = debian ] && { [ -z "${VERSION_ID:-}" ] || version_at_least "$VERSION_ID" 14; }; then
            newer=true
        elif [ "${ID:-}" = ubuntu ] && [ -n "${VERSION_ID:-}" ] && version_at_least "$VERSION_ID" 24.04; then
            newer=true
        fi
        [ "$newer" = true ] \
            || die "${PRETTY_NAME:-This system} is not supported. Supported: $SUPPORTED. On other systems follow server/INSTALL.en.md."
        warn "${PRETTY_NAME:-This system} is newer than the systems this script is tested on ($SUPPORTED). It will most likely work; if a step fails, the script says which one."
        if [ "$assume_yes" = false ]; then
            read -r -p "Continue? [y/N]: " answer
            [[ "$answer" =~ ^[YyДд] ]] || die "Stopped, nothing was changed."
        fi
        ;;
esac
ok "System: ${PRETTY_NAME:-$ID $VERSION_ID}"

if [ -f "$dir/server/config/config.php" ]; then
    say "CUSTOS Monitor is already installed in $dir: checking and completing the installation. Settings, keys, the database and users stay. To update, use --update."
fi

# The site address.
if [ -z "$url" ] && [ -f "$dir/server/config/config.php" ]; then
    url=$(sed -nE "s/^[[:space:]]*'url' => '([^']*)'.*/\1/p" "$dir/server/config/config.php" | head -n 1)
fi
if [ -z "$url" ]; then
    [ "$assume_yes" = false ] || die "--url is required with --yes."
    guess=$(hostname -I 2>/dev/null | awk '{print $1}')
    echo
    echo "Address of the site: how people and agents will open it, for example http://custos.lan or http://${guess:-192.0.2.10}."
    echo "A name needs a DNS record (or a line in the hosts file of each computer) that points to this server."
    read -r -p "Address [http://${guess:-localhost}]: " url
    url=${url:-http://${guess:-localhost}}
fi
url=${url%/}
[[ "$url" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] \
    || die "The address must look like http://name or http://192.0.2.10, without a path: $url"
host=${url#*://}
host=${host%%:*}

if [ "$https" = true ]; then
    if [[ "$host" =~ ^[0-9.]+$ ]] || [[ "$host" != *.* ]] || [[ "$host" =~ \.(lan|local|home|internal|localdomain)$ ]]; then
        die "--https needs a public name that points to this server, not $host."
    fi
fi

if [ -z "$timezone" ]; then
    timezone=$(cat /etc/timezone 2>/dev/null || true)
    if [ -z "$timezone" ] && [ -L /etc/localtime ]; then
        timezone=$(readlink /etc/localtime | sed 's#.*/zoneinfo/##')
    fi
    timezone=${timezone:-UTC}
fi
[[ "$timezone" =~ ^[A-Za-z0-9_+-]+(/[A-Za-z0-9_+-]+)*$ ]] || die "Wrong time zone: $timezone"

if [ "$assume_yes" = true ] && [ ! -f "$dir/server/config/config.php" ]; then
    [ -n "$admin" ] && [ -n "${CUSTOS_ADMIN_PASSWORD:-}" ] \
        || die "--yes on a new site needs --admin=LOGIN and the password in CUSTOS_ADMIN_PASSWORD."
fi

say "Installing packages (nginx, MariaDB, PHP)"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q --no-install-recommends ca-certificates curl unzip cron nginx mariadb-server

# PHP 8.3 or newer from the system, or from packages.sury.org on Debian 12 (it has PHP 8.2).
if [ -z "$php_version" ]; then
    php_version=$(apt-cache depends php-fpm 2>/dev/null | sed -nE 's/.*Depends: php([0-9]+\.[0-9]+)-fpm.*/\1/p' | head -n 1)
fi
if [ -z "$php_version" ] || [ "$(printf '%s\n8.3\n' "$php_version" | sort -V | head -n 1)" != 8.3 ]; then
    [ "$ID" = debian ] || die "PHP ${php_version:-?} of this system is older than 8.3."
    say "PHP ${php_version:-?} of this system is older than 8.3: adding the PHP repository packages.sury.org"
    curl -fsSL -o "$workdir/debsuryorg-archive-keyring.deb" https://packages.sury.org/debsuryorg-archive-keyring.deb
    dpkg -i "$workdir/debsuryorg-archive-keyring.deb" > /dev/null
    echo "deb [signed-by=/usr/share/keyrings/debsuryorg-archive-keyring.gpg] https://packages.sury.org/php/ $VERSION_CODENAME main" \
        > /etc/apt/sources.list.d/php.list
    apt-get update -q
    php_version=8.4
fi
apt-get install -y -q --no-install-recommends "php$php_version-fpm" "php$php_version-cli" "php$php_version-mysql" \
    "php$php_version-curl" "php$php_version-mbstring"
php_bin=/usr/bin/php$php_version
[ -x "$php_bin" ] || die "$php_bin is missing after the installation."
ok "PHP $php_version"

service_restart mariadb
service_restart cron

# ---------------------------------------------------------------------------------------------------------------
# Install: user, files, database

say "Site user and files"
if id "$site_user" > /dev/null 2>&1; then
    ok "User $site_user exists"
else
    useradd --create-home --shell /bin/bash "$site_user"
    ok "Created user $site_user"
fi
mkdir -p "$dir"
chown "$site_user:" "$dir"
chmod 755 "$dir"

source_zip=$package
[ -n "$source_zip" ] || source_zip=$(download_release)
zip=$(prepare_package "$source_zip")
new=$(package_version "$zip")
old=$(installed_version)
if [ -n "$old" ] && [ "$(printf '%s\n%s\n' "$old" "$new" | sort -V | tail -n 1)" != "$new" ]; then
    warn "The site in $dir is $old, newer than the package ($new): its files are left as they are."
else
    runuser -u "$site_user" -- unzip -o -q "$zip" -d "$dir"
    ok "CUSTOS Monitor $new unpacked into $dir/server"
fi

config="$dir/server/config/config.php"
if [ -f "$config" ]; then
    # shellcheck disable=SC2016 # PHP code
    db_password=$("$php_bin" -r '$c = require $argv[1]; echo $c["db"]["password"] ?? "";' "$config")
    ok "Settings kept: $config"
else
    # Cut in bash, not with "| head": with pipefail a reader that stops early can fail the pipeline.
    db_password=$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')
    db_password=${db_password:0:24}
fi
[ -n "$db_password" ] || die "No database password in $config."
[[ "$db_password" =~ ^[A-Za-z0-9_.-]+$ ]] \
    || warn "The database password in $config has special characters; the database user keeps its current password."

say "Database"
if [[ "$db_password" =~ ^[A-Za-z0-9_.-]+$ ]]; then
    # The site connects to 127.0.0.1; depending on name resolution MariaDB sees that as localhost or as
    # 127.0.0.1, so the user exists for both.
    mariadb <<SQL
CREATE DATABASE IF NOT EXISTS \`$DB_NAME\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS '$DB_USER'@'localhost' IDENTIFIED BY '$db_password';
CREATE USER IF NOT EXISTS '$DB_USER'@'127.0.0.1' IDENTIFIED BY '$db_password';
ALTER USER '$DB_USER'@'localhost' IDENTIFIED BY '$db_password';
ALTER USER '$DB_USER'@'127.0.0.1' IDENTIFIED BY '$db_password';
GRANT ALL PRIVILEGES ON \`$DB_NAME\`.* TO '$DB_USER'@'localhost';
GRANT ALL PRIVILEGES ON \`$DB_NAME\`.* TO '$DB_USER'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
    ok "Database $DB_NAME, user $DB_USER"
fi

if [ ! -f "$config" ]; then
    runuser -u "$site_user" -- cp "$dir/server/config/config.example.php" "$config"
    sed -i \
        -e "s|'password' => 'change-me',|'password' => '$db_password',|" \
        -e "s|'url' => 'http://custos.lan',|'url' => '$url',|" \
        -e "s|'timezone' => 'UTC',|'timezone' => '$timezone',|" \
        "$config"
    chmod 600 "$config"
    chown "$site_user:" "$config"
    if ! grep -q "'password' => '$db_password'," "$config" || ! grep -q "'url' => '$url'," "$config"; then
        die "Could not fill in $config: compare it with config.example.php."
    fi
    ok "Settings written to $config (address $url, time zone $timezone)"
fi

# ---------------------------------------------------------------------------------------------------------------
# Install: PHP-FPM and nginx

say "PHP-FPM and nginx"
cat > "/etc/php/$php_version/fpm/pool.d/custos.conf" <<EOF
; CUSTOS Monitor, written by install.sh
[custos]
user = $site_user
group = $site_user
listen = /run/php/php$php_version-fpm-custos.sock
listen.owner = www-data
listen.group = www-data
pm = ondemand
pm.max_children = 10
php_admin_value[upload_max_filesize] = 100M
php_admin_value[post_max_size] = 100M
EOF
service_restart "php$php_version-fpm"

# The stock "Welcome to nginx" site goes away when it is the only one, so the site also opens by IP address.
default_server=
if [ -L /etc/nginx/sites-enabled/default ] && [ "$(find /etc/nginx/sites-enabled -mindepth 1 ! -name custos | wc -l)" = 1 ]; then
    rm /etc/nginx/sites-enabled/default
    default_server=' default_server'
elif [ "$(find /etc/nginx/sites-enabled -mindepth 1 ! -name custos | wc -l)" = 0 ]; then
    default_server=' default_server'
fi
# IPv6 only where the system has it: nginx refuses to start on [::]:80 otherwise (containers often).
listen_v6=
if [ -f /proc/net/if_inet6 ]; then
    listen_v6="    listen [::]:80$default_server;"
fi
site_conf=/etc/nginx/sites-available/custos
if [ -f "$site_conf" ] && grep -q 'managed by Certbot' "$site_conf"; then
    ok "nginx site $site_conf has a certificate from Certbot and is left as it is"
else
    cat > "$site_conf" <<EOF
# CUSTOS Monitor, written by install.sh
server {
    listen 80$default_server;
$listen_v6
    server_name $host;
    root $dir/server/public;
    index index.php;
    client_max_body_size 100m;   # agents need 10m; more is needed to upload update packages through the site

    location / {
        try_files \$uri /index.php?\$query_string;
    }

    location = /index.php {
        include fastcgi_params;
        fastcgi_param SCRIPT_FILENAME \$document_root\$fastcgi_script_name;
        fastcgi_pass unix:/run/php/php$php_version-fpm-custos.sock;
    }

    location ~ \.php\$ {
        return 404;
    }
}
EOF
fi
ln -sf "$site_conf" /etc/nginx/sites-enabled/custos
nginx -t 2> "$workdir/nginx-test.txt" || { cat "$workdir/nginx-test.txt" >&2; die "nginx does not accept the configuration."; }
service_restart nginx
ok "nginx serves $url from $dir/server/public"

# ---------------------------------------------------------------------------------------------------------------
# Install: the site itself

say "Installing the site"
install_args=(--cron-added)
[ -z "$language" ] || install_args+=("--language=$language")
if [ -n "$admin" ]; then
    install_args+=("--admin-login=$admin")
    as_site_user env "CUSTOS_ADMIN_PASSWORD=${CUSTOS_ADMIN_PASSWORD:-}" "$php_bin" bin/install.php "${install_args[@]}" \
        | tee "$workdir/install.txt"
else
    # install.php asks for the login and the password of the first administrator itself.
    as_site_user "$php_bin" bin/install.php "${install_args[@]}" | tee "$workdir/install.txt"
fi

cron_line="* * * * * $site_user $php_bin $dir/server/bin/cron.php"
printf '# CUSTOS Monitor background jobs, written by install.sh\n%s\n' "$cron_line" > /etc/cron.d/custos
chmod 644 /etc/cron.d/custos
as_site_user "$php_bin" bin/cron.php > /dev/null || warn "The first run of bin/cron.php failed; see $dir/server/var/log."
ok "Cron job /etc/cron.d/custos runs bin/cron.php every minute"

if [ "$https" = true ]; then
    say "HTTPS certificate from Let's Encrypt for $host"
    apt-get install -y -q --no-install-recommends certbot python3-certbot-nginx
    certbot_args=(--nginx -d "$host" --non-interactive --agree-tos --redirect)
    if [ -n "$email" ]; then certbot_args+=(-m "$email"); else certbot_args+=(--register-unsafely-without-email); fi
    if certbot "${certbot_args[@]}"; then
        sed -i "s|'url' => 'http://$host\(:[0-9]*\)\?',|'url' => 'https://$host',|" "$config"
        url="https://$host"
        ok "The site works over HTTPS; the certificate renews by itself"
    else
        warn "No certificate: the site keeps working over HTTP. Check that $host points to this server and that port 80 is open, then run the script again with --https."
    fi
fi

mkdir -p "$(dirname "$STATE_FILE")"
printf '# Written by the CUSTOS Monitor install.sh; used by install.sh --update.\nDIR=%s\nPHP=%s\n' "$dir" "$php_version" > "$STATE_FILE"

connection=$(sed -nE 's/^[[:space:]]*Connection string:[[:space:]]+//p' "$workdir/install.txt" | tail -n 1)
cat <<EOF

==> CUSTOS Monitor is installed

  Site:               $url
  Connection string:  ${connection:-on the site, Settings → Connecting agents}
                      (paste it into the program: Settings → Monitoring server)
  Files:              $dir/server (settings in config/config.php, keys in config/keys)
  Database:           $DB_NAME, user $DB_USER, password in config/config.php
  Update later:       bash install.sh --update

Keep in backups: the database $DB_NAME, $dir/server/config/config.php and $dir/server/config/keys/.
EOF
if [[ ! "$host" =~ ^[0-9.]+$ ]] && [ "$host" != localhost ]; then
    echo "The name $host must point to this server: a DNS record, or a line in the hosts file of each computer."
fi

exit 0
}
