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

cleanup() { docker rm -f "$name" > /dev/null 2>&1 || true; }
trap cleanup EXIT

step() { printf '\n### %s\n' "$*"; }
fail() { printf '\n[FAIL] %s\n' "$*" >&2; exit 1; }
in_box() { docker exec "$name" bash -c "$1"; }

step "Container $base"
docker run -d --name "$name" \
    -v "$root/server/install.sh:/opt/install.sh:ro" \
    -v "$package:/opt/custos-server.zip:ro" \
    "$base" sleep infinity > /dev/null

step "Install without questions"
docker exec -e CUSTOS_ADMIN_PASSWORD="$password" "$name" \
    bash /opt/install.sh --yes --package=/opt/custos-server.zip --url=http://localhost --admin=admin \
        --language=en --timezone=Europe/Berlin | tee /tmp/"$name"-install.txt
connection=$(sed -nE 's/^[[:space:]]*Connection string:[[:space:]]+(http[^ ]+).*/\1/p' /tmp/"$name"-install.txt | tail -n 1)
[ -n "$connection" ] || fail "The script printed no connection string."
echo "Connection string: $connection"
php=$(in_box 'sed -n "s/^PHP=//p" /etc/custos/install.conf')
[ -n "$php" ] || fail "/etc/custos/install.conf has no PHP version."

step "The site answers"
code=$(in_box 'curl -s -o /dev/null -w "%{http_code}" http://localhost/login')
[ "$code" = 200 ] || fail "GET /login answered $code instead of 200."
in_box 'curl -s http://localhost/login' | grep -q 'CUSTOS' || fail "The login page does not mention CUSTOS."
health=$(in_box 'curl -s http://localhost/api/health')
echo "$health"
echo "$health" | grep -q '"status":"ok"' || fail "/api/health is not ok: $health"
in_box 'grep -q "cron.php" /etc/cron.d/custos' || fail "No cron job."
in_box 'stat -c "%U %a" /var/www/custos/server/config/config.php' | grep -qx 'custos 600' \
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

step "Update to the same version: nothing to do; with --force: unpacked and migrated"
docker exec "$name" bash /opt/install.sh --update --package=/opt/custos-server.zip | tee /tmp/"$name"-update.txt
grep -q 'is already installed' /tmp/"$name"-update.txt || fail "--update did not see that the version is installed."
docker exec "$name" bash /opt/install.sh --update --force --package=/opt/custos-server.zip | tee /tmp/"$name"-force.txt
grep -q 'Database schema is up to date' /tmp/"$name"-force.txt || fail "--update --force did not run migrate.php."
in_box 'curl -s http://localhost/api/health' | grep -q '"status":"ok"' || fail "/api/health is not ok after the update."

printf '\n[PASS] %s\n' "$base"
