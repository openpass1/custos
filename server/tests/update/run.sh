#!/usr/bin/env bash
# The site updating itself with a real package, as CI builds it: installs a site from the package folder given
# (it holds server/), updates it with bin/update.php to a copy of the package with a higher version and a migration,
# returns to the old version with --rollback, and checks that a broken package changes nothing.
#
#   tests/update/run.sh DIST     DIST/server is the unpacked package (CI: ../dist after "Build the package")
#
# Uses the test database from CUSTOS_TEST_DB_HOST, _PORT, _NAME, _USER, _PASSWORD (everything in it is dropped).
# The PHPUnit test SiteUpdaterTest covers the same with the code of the repository; this one checks the real
# package: its vendor/, its zip as "zip -r" writes it.

set -euo pipefail

dist=$(cd "${1:?Usage: tests/update/run.sh DIST}" && pwd)
[ -f "$dist/server/src/App.php" ] || { echo "No package in $dist/server" >&2; exit 2; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

fail() { echo "FAILED: $*" >&2; exit 1; }
version_of() { sed -nE "s/.*const VERSION = '([^']+)'.*/\1/p" "$1/src/App.php"; }

db_host=${CUSTOS_TEST_DB_HOST:-127.0.0.1}
db_port=${CUSTOS_TEST_DB_PORT:-3306}
db_name=${CUSTOS_TEST_DB_NAME:-custos_test}
db_user=${CUSTOS_TEST_DB_USER:-root}
db_password=${CUSTOS_TEST_DB_PASSWORD:-}
export CUSTOS_ADMIN_PASSWORD='Update-test-password-1'

# shellcheck disable=SC2016 # PHP code
php -r '
    $pdo = new PDO(sprintf("mysql:host=%s;port=%d;dbname=%s", $argv[1], $argv[2], $argv[3]), $argv[4], $argv[5]);
    $pdo->exec("SET FOREIGN_KEY_CHECKS = 0");
    foreach ($pdo->query("SHOW TABLES")->fetchAll(PDO::FETCH_COLUMN) as $t) { $pdo->exec("DROP TABLE `$t`"); }
' "$db_host" "$db_port" "$db_name" "$db_user" "$db_password"

site=$work/site/server
mkdir -p "$work/site"
cp -a "$dist/server" "$site"
cat > "$site/config/config.php" <<PHP
<?php
return [
    'db' => ['host' => '$db_host', 'port' => $db_port, 'name' => '$db_name', 'user' => '$db_user', 'password' => '$db_password'],
    'url' => 'http://custos.test',
    'timezone' => 'UTC',
];
PHP
(cd "$site" && php bin/install.php --admin-login=admin > "$work/install.txt") || { cat "$work/install.txt"; fail "install.php"; }
old=$(version_of "$site")
echo "Installed $old from $dist"

# Packages made like release.yml makes them: the folder server/ zipped with "zip -r -X".
make_package() { # version, then commands that change $work/pkg/server
    local version=$1
    rm -rf "$work/pkg" && mkdir -p "$work/pkg"
    cp -a "$dist/server" "$work/pkg/server"
    sed -i "s/const VERSION = '[^']*'/const VERSION = '$version'/" "$work/pkg/server/src/App.php"
    shift
    "$@"
    (cd "$work/pkg" && zip -q -r -X "$work/custos-server-$version.zip" server)
    echo "$work/custos-server-$version.zip"
}
add_migration() {
    printf 'CREATE TABLE update_check (id INT NOT NULL, PRIMARY KEY (id)) ENGINE=InnoDB;\nINSERT INTO update_check (id) VALUES (7);\n' \
        > "$work/pkg/server/migrations/999_update_check.sql"
}
break_template() {
    printf '<?php if (true) { ?>\n' > "$work/pkg/server/templates/broken.php"
}
table_count() {
    # shellcheck disable=SC2016 # PHP code
    php -r '
        $pdo = new PDO(sprintf("mysql:host=%s;port=%d;dbname=%s", $argv[1], $argv[2], $argv[3]), $argv[4], $argv[5]);
        echo count($pdo->query("SHOW TABLES LIKE " . $pdo->quote($argv[6]))->fetchAll());
    ' "$db_host" "$db_port" "$db_name" "$db_user" "$db_password" "$1"
}

new=$(make_package 99.0.0 add_migration)
(cd "$site" && php bin/update.php --package="$new" --yes) | tee "$work/update.txt"
grep -q '^\[ok\] The site is updated' "$work/update.txt" || fail "update to 99.0.0"
[ "$(version_of "$site")" = 99.0.0 ] || fail "the site is not 99.0.0"
[ "$(table_count update_check)" = 1 ] || fail "the migration of 99.0.0 did not run"
[ ! -e "$site/var/update/maintenance.json" ] || fail "the maintenance page stayed"
(cd "$site" && php bin/update.php --self-test --expect=99.0.0) || fail "self-test of 99.0.0"
(cd "$site" && php bin/update.php --package="$new" --yes) | grep -q 'already installed' || fail "the same version again"

(cd "$site" && php bin/update.php --rollback --yes) | tee "$work/rollback.txt"
[ "$(version_of "$site")" = "$old" ] || fail "--rollback did not return to $old"
[ "$(table_count update_check)" = 0 ] || fail "--rollback did not drop the table of 99.0.0"
(cd "$site" && php bin/update.php --self-test --expect="$old") || fail "self-test of $old after --rollback"

broken=$(make_package 99.0.1 break_template)
if (cd "$site" && php bin/update.php --package="$broken" --yes) > "$work/broken.txt" 2>&1; then
    cat "$work/broken.txt"
    fail "a version that does not pass its self-test was installed"
fi
cat "$work/broken.txt"
grep -q 'did not pass its check' "$work/broken.txt" || fail "no reason given for the broken package"
grep -q 'templates/broken.php' "$work/broken.txt" || fail "the self-test output is not shown"
[ "$(version_of "$site")" = "$old" ] || fail "the broken package was left in place"
[ ! -e "$site/templates/broken.php" ] || fail "a file of the broken package was left"
(cd "$site" && php bin/cron.php) || fail "cron.php of $old after the failed update"

echo "Self-update test passed: $old -> 99.0.0 -> $old, broken 99.0.1 refused"
