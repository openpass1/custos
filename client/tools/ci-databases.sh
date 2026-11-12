#!/usr/bin/env bash
# Prepares the database servers of the CI job "databases" (.github/workflows/build.yml) for RealDatabaseTests.
#
# The servers run in service containers whose ids come in MYSQL_ID, MARIADB_ID and POSTGRES_ID. Their client
# programs run inside the same containers: for each server a folder gets small scripts named like the programs
# (mysqldump, mysql, pg_dump …), so the tests see a folder of programs, as on Windows. The address the tests use is
# the one inside the container. Writes CUSTOS_TEST_<ENGINE>=folder|host|port|user|password to GITHUB_ENV.
set -euo pipefail

password=custos-test
root="${RUNNER_TEMP:-/tmp}/custos-db-tools"

# make_tools <folder> <container> <program>...
make_tools() {
    local folder="$root/$1" container="$2"
    shift 2
    mkdir -p "$folder"
    for program in "$@"; do
        cat > "$folder/$program" <<SCRIPT
#!/bin/sh
exec docker exec -e MYSQL_PWD -e PGPASSWORD -e PGCONNECT_TIMEOUT -e PGCLIENTENCODING $container $program "\$@"
SCRIPT
        chmod +x "$folder/$program"
    done
}

make_tools mysql "$MYSQL_ID" mysqldump mysql
make_tools mariadb "$MARIADB_ID" mariadb-dump mariadb
make_tools postgresql "$POSTGRES_ID" pg_dump pg_dumpall psql

docker exec -e MYSQL_PWD="$password" "$MYSQL_ID" mysql -h 127.0.0.1 -uroot -e "
    CREATE DATABASE shop; CREATE TABLE shop.items (id INT PRIMARY KEY, name VARCHAR(50));
    INSERT INTO shop.items VALUES (1, 'tea'); CREATE DATABASE \`old site\`;
    CREATE USER custos_reader@'%' IDENTIFIED BY 'reader'; GRANT SELECT ON shop.* TO custos_reader@'%';"
docker exec -e MYSQL_PWD="$password" "$MARIADB_ID" mariadb -h 127.0.0.1 -uroot -e "
    CREATE DATABASE shop; CREATE TABLE shop.items (id INT PRIMARY KEY, name VARCHAR(50));
    INSERT INTO shop.items VALUES (1, 'tea'); CREATE DATABASE \`old site\`;
    CREATE USER custos_reader@'%' IDENTIFIED BY 'reader'; GRANT SELECT ON shop.* TO custos_reader@'%';"
docker exec -e PGPASSWORD="$password" "$POSTGRES_ID" psql -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1 \
    -c "CREATE ROLE custos_reader LOGIN PASSWORD 'reader'" -c "CREATE DATABASE shop"
docker exec -e PGPASSWORD="$password" "$POSTGRES_ID" psql -h 127.0.0.1 -U postgres -d shop -v ON_ERROR_STOP=1 \
    -c "CREATE TABLE items (id INT PRIMARY KEY, name TEXT); INSERT INTO items VALUES (1, 'tea'); GRANT SELECT ON items TO custos_reader"

{
    echo "CUSTOS_TEST_MYSQL=$root/mysql|127.0.0.1|3306|root|$password"
    echo "CUSTOS_TEST_MARIADB=$root/mariadb|127.0.0.1|3306|root|$password"
    echo "CUSTOS_TEST_POSTGRESQL=$root/postgresql|127.0.0.1|5432|postgres|$password"
} >> "$GITHUB_ENV"
