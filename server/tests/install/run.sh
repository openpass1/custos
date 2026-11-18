#!/usr/bin/env bash
# Tests server/install.sh in a clean container of one system: a new installation without questions, the site and
# its health check, a fake agent that registers, a second run of the script and an update.
#
#   server/tests/install/run.sh debian:12 /path/to/custos-server.zip
#
# Needs Docker. The package is the zip the Release workflow (or the Server workflow) makes: the folder server/ with
# vendor/. Containers have no systemd, so the script starts the services with their init scripts.

set -euo pipefail

base=$1
package=$(realpath "$2")
root=$(cd "$(dirname "$0")/../../.." && pwd)
name="custos-install-test-$$"
password='Test-password-1'
work=$(mktemp -d)

cleanup() { docker rm -f "$name" > /dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

step() { printf '\n### %s\n' "$*"; }
fail() { printf '\n[FAIL] %s\n' "$*" >&2; exit 1; }
in_box() { docker exec "$name" bash -c "$1"; }

step "Container $base"
# --init: a small init process reaps the processes that the init scripts stop. Without it a stopped php-fpm stays
# a zombie, its init script waits for it to go and gives up, as no real server with systemd would.
docker run -d --init --name "$name" \
    -v "$root/server/install.sh:/opt/install.sh:ro" \
    -v "$package:/opt/custos-server.zip:ro" \
    "$base" sleep infinity > /dev/null

step "Install without questions"
docker exec -e CUSTOS_ADMIN_PASSWORD="$password" "$name" \
    bash /opt/install.sh --yes --package=/opt/custos-server.zip --url=http://localhost --admin=admin \
        --language=en --timezone=Europe/Berlin | tee /tmp/"$name"-install.txt
connection=$(sed -nE 's/^[[:space:]]*Connection string:[[:space:]]+(http[^ ]+).*/\1/p' /tmp/"$name"-install.txt | tail -n 1)
[ -n "$connection" ] || fail "The script printed no connection string."
grep -qE "^  Connection string:  $connection\$" /tmp/"$name"-install.txt \
    || fail "The summary at the end does not show the connection string."
grep -q 'Add a cron job' /tmp/"$name"-install.txt && fail "The output asks to add the cron job that the script has added."
echo "Connection string: $connection"
php=$(in_box 'sed -n "s/^PHP=//p" /etc/custos/install.conf')
[ -n "$php" ] || fail "/etc/custos/install.conf has no PHP version."

step "The site answers"
code=$(in_box 'curl -s -o /dev/null -w "%{http_code}" http://localhost/login')
[ "$code" = 200 ] || fail "GET /login answered $code instead of 200."
# Outputs go into variables before grep -q: with pipefail, grep -q that stops reading early can fail the writer.
login=$(in_box 'curl -s http://localhost/login')
[[ "$login" == *CUSTOS* ]] || fail "The login page does not mention CUSTOS."
# Styles, fonts and icons are kept by the browser (location /assets/ of the nginx site).
assets=$(in_box 'curl -s -o /dev/null -D - http://localhost/assets/app.css')
[[ "${assets,,}" == *'cache-control: max-age=2592000'* ]] || fail "/assets/app.css comes without Cache-Control: max-age=2592000: $assets"
health=$(in_box 'curl -s http://localhost/api/health')
echo "$health"
[[ "$health" == *'"status":"ok"'* ]] || fail "/api/health is not ok: $health"
in_box 'grep -q "cron.php" /etc/cron.d/custos' || fail "No cron job."
[ "$(in_box 'stat -c "%U %a" /var/www/custos/server/config/config.php')" = 'custos 600' ] \
    || fail "config.php must belong to custos with mode 600."

step "A fake agent registers"
in_box "cd /var/www/custos/server && runuser -u custos -- php$php tools/fake-agent.php --connect='$connection' --once" \
    | tee /tmp/"$name"-agent.txt
grep -q 'every answer was signed and accepted' /tmp/"$name"-agent.txt || fail "The fake agent was not accepted."

step "Run the script again: nothing is lost"
docker exec "$name" bash /opt/install.sh --yes --package=/opt/custos-server.zip | tee /tmp/"$name"-again.txt
grep -q 'Users already exist' /tmp/"$name"-again.txt || fail "The second run did not keep the users."
grep -q 'Server key pair already exists' /tmp/"$name"-again.txt || fail "The second run did not keep the key."
grep -qF "$connection" /tmp/"$name"-again.txt || fail "The connection string changed after the second run."

step "Update to the same version: nothing to do; with --force: installed again by bin/update.php"
docker exec "$name" bash /opt/install.sh --update --package=/opt/custos-server.zip | tee /tmp/"$name"-update.txt
grep -q 'is already installed' /tmp/"$name"-update.txt || fail "--update did not see that the version is installed."
# The copy inside the site: the update replaces the install.sh that is running.
docker exec "$name" bash /var/www/custos/server/install.sh --update --force --package=/opt/custos-server.zip \
    | tee /tmp/"$name"-force.txt
grep -q 'Updating with bin/update.php' /tmp/"$name"-force.txt || fail "--update --force did not use bin/update.php."
grep -q 'The site is updated' /tmp/"$name"-force.txt || fail "bin/update.php did not install the package again."
grep -q '^==> Done: CUSTOS Monitor' /tmp/"$name"-force.txt || fail "--update --force did not finish."
health=$(in_box 'curl -s http://localhost/api/health')
[[ "$health" == *'"status":"ok"'* ]] || fail "/api/health is not ok after the update: $health"

step "Update to a newer version, then an older package is refused"
# The same package with the version X.Y.Z.1, zipped the way the Release workflow zips it.
old=$(unzip -p "$package" server/src/App.php | sed -nE "s/.*const VERSION = '([^']+)'.*/\1/p")
[ -n "$old" ] || fail "No version in the package."
newer="$old.1"
(cd "$work" && unzip -q "$package" \
    && sed -i "s/const VERSION = '[^']*'/const VERSION = '$newer'/" server/src/App.php \
    && zip -q -r -X "custos-server-$newer.zip" server)
docker cp "$work/custos-server-$newer.zip" "$name:/opt/custos-server-$newer.zip"
docker exec "$name" bash /opt/install.sh --update --package="/opt/custos-server-$newer.zip" | tee /tmp/"$name"-newer.txt
grep -qF "The site is updated: $old -> $newer" /tmp/"$name"-newer.txt || fail "The site was not updated to $newer."
grep -qF "==> Done: CUSTOS Monitor $old -> $newer" /tmp/"$name"-newer.txt || fail "--update to $newer did not finish."
[ "$(in_box "sed -nE \"s/.*const VERSION = '([^']+)'.*/\\1/p\" /var/www/custos/server/src/App.php")" = "$newer" ] \
    || fail "The files of the site are not $newer."
health=$(in_box 'curl -s http://localhost/api/health')
[[ "$health" == *'"status":"ok"'* ]] || fail "/api/health is not ok after the update to $newer: $health"
docker exec "$name" bash /opt/install.sh --update --package=/opt/custos-server.zip | tee /tmp/"$name"-older.txt
grep -q 'is older than the installed version' /tmp/"$name"-older.txt || fail "An older package was not refused."

printf '\n[PASS] %s\n' "$base"
