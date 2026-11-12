#!/usr/bin/env bash
# Starts the Linux server of the CI job "remote" (.github/workflows/build.yml) for RealRemoteTests.
#
# The server is the test image of custos-helper (helper/tests/Dockerfile: OpenSSH, sudo, rsyslog, MariaDB and
# PostgreSQL from Debian's own packages), run with its SSH port on 127.0.0.1:2222. custos-helper is not installed
# here: the tests install it with the block of the wizard, through "docker exec" as root. Writes CUSTOS_TEST_SSH
# (host|port|root shell|engines|db defaults file) to GITHUB_ENV.
set -euo pipefail

base=${BASE:-debian:12}
engines=${ENGINES-mariadb postgresql}
port=${PORT:-2222}
name=custos-remote
repo=$(cd "$(dirname "$0")/../.." && pwd)

# The build log is shown only when the build fails, e.g. when a package mirror is down.
log=$(mktemp)
trap 'rm -f "$log"' EXIT
if ! docker build --progress=plain --build-arg BASE="$base" --build-arg ENGINES="$engines" \
    --tag custos-remote-test "$repo/helper/tests" > "$log" 2>&1; then
  tail -n 80 "$log" >&2
  exit 1
fi

docker rm -f "$name" > /dev/null 2>&1 || true
docker run --detach --name "$name" --cap-add SYS_NICE --publish "127.0.0.1:$port:22" \
  --env ENGINES="$engines" custos-remote-test sleep infinity > /dev/null

# The services, started as on a server (the same steps as start_services in helper/tests/test.sh, for Debian).
docker exec -i "$name" bash -s <<'SCRIPT'
set -euo pipefail
printf '%s\n' 'module(load="imuxsock")' '*.* /var/log/custos-test.log' > /etc/custos-rsyslog.conf
rsyslogd -f /etc/custos-rsyslog.conf -i /run/custos-rsyslog.pid
install -d -m 0755 /run/sshd
ssh-keygen -A > /dev/null
/usr/sbin/sshd
for e in $ENGINES; do
  case $e in
    mariadb | mysql)
      install -d -o mysql -g mysql /run/mysqld
      [[ -d /var/lib/mysql/mysql ]] || mariadb-install-db --user=mysql > /dev/null
      mysqld_safe --user=mysql > /dev/null 2>&1 &
      for _ in $(seq 90); do mariadb -e 'SELECT 1' > /dev/null 2>&1 && break; sleep 1; done
      mariadb -e 'SELECT VERSION()' ;;
    postgresql)
      for v in /etc/postgresql/*/main; do v=${v%/main}; pg_ctlcluster "${v##*/}" main start; done
      runuser -u postgres -- psql -X -At -c 'SELECT version()' ;;
  esac
done
SCRIPT

for _ in $(seq 30); do
  if ssh-keyscan -p "$port" 127.0.0.1 > /dev/null 2>&1; then
    break
  fi
  sleep 1
done
ssh-keyscan -p "$port" 127.0.0.1 2> /dev/null | ssh-keygen -lf - || { echo "sshd does not answer on port $port" >&2; exit 1; }

echo "CUSTOS_TEST_SSH=127.0.0.1|$port|docker exec -i $name bash -s|$engines|" >> "${GITHUB_ENV:-/dev/stdout}"
