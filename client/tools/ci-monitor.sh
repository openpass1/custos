#!/usr/bin/env bash
# Starts CUSTOS Monitor (server/) for the CI job "monitor" (.github/workflows/build.yml) and RealMonitorTests.
#
# The database is the MariaDB service of the job on 127.0.0.1:3306 (root / custos-test, database "custos"), the web
# server is PHP's own on 127.0.0.1:8080. Installs the server the way its README says, takes the registration code
# that install.php prints and writes CUSTOS_TEST_MONITOR (url|code|sql command) to GITHUB_ENV.
set -euo pipefail

server=$(cd "$(dirname "$0")/../../server" && pwd)
url=http://127.0.0.1:8080
cd "$server"

composer install --no-dev --no-interaction --no-progress
cat > config/config.php <<'PHP'
<?php
return [
    'db' => ['host' => '127.0.0.1', 'port' => 3306, 'name' => 'custos', 'user' => 'root', 'password' => 'custos-test'],
    'url' => 'http://127.0.0.1:8080',
    'timezone' => 'UTC',
    'session_timeout_hours' => 8,
    'trusted_proxies' => [],
];
PHP

out=$(CUSTOS_ADMIN_PASSWORD='Ci-admin-password-1' php bin/install.php --admin-login=admin < /dev/null)
echo "$out"
code=$(printf '%s\n' "$out" | sed -n 's/.*Registration code:[[:space:]]*//p' | tr -d '[:space:]')
if [ -z "$code" ]; then
    echo "install.php printed no registration code" >&2
    exit 1
fi

nohup php -S 127.0.0.1:8080 -t public public/index.php > "${RUNNER_TEMP:-/tmp}/custos-monitor.log" 2>&1 &
for _ in $(seq 1 30); do
    if curl -s -o /dev/null "$url/"; then
        break
    fi
    sleep 1
done
curl -s -o /dev/null "$url/" || { echo "The server did not start:" >&2; cat "${RUNNER_TEMP:-/tmp}/custos-monitor.log" >&2; exit 1; }

echo "CUSTOS_TEST_MONITOR=$url|$code|mysql -N -B -h127.0.0.1 -uroot -pcustos-test custos" >> "$GITHUB_ENV"
