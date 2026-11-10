#!/bin/bash
# custos-helper tests. Runs as root inside the test container (see run.sh): starts the
# services, installs custos-helper with the block from helper/README.md and checks every
# operation over real SSH. Database checks run for each engine listed in $ENGINES.
set -uo pipefail
umask 022
export LC_ALL=C

H=/helper            # helper/ of the repository, mounted read-only
T=/tmp/custos-test   # test scratch space
ENGINES=${ENGINES-}
PASSED=0 FAILED=0

ok() { PASSED=$((PASSED + 1)); echo "ok    $1"; }
bad() { FAILED=$((FAILED + 1)); echo "FAIL  $1"; }
# check <name> <command...>: the command must succeed.
check() { local name=$1; shift; if "$@"; then ok "$name"; else bad "$name"; fi; }
# code <expected exit code> <name> <command...>: output goes to $T/out and $T/err.
code() {
  local want=$1 name=$2 got; shift 2
  "$@" > "$T/out" 2> "$T/err"; got=$?
  if (( got == want )); then ok "$name"; else bad "$name: exit $got, expected $want; $(head -c 300 "$T/err")"; fi
}
has() { grep -qF -- "$1" "$2"; }
# Values from the JSON in a file: json <file> <python expression over d>.
json() { python3 -I -c 'import json, sys; d = json.load(open(sys.argv[1])); print(eval(sys.argv[2]))' "$@"; }
want() { [[ $1 == "$2" ]] || { echo "      got: $1" >&2; echo "      expected: $2" >&2; return 1; }; }

SSH_OPTS=(-o BatchMode=yes -o StrictHostKeyChecking=accept-new -o LogLevel=ERROR)
r() { ssh -i /root/.ssh/k1 "${SSH_OPTS[@]}" custos@127.0.0.1 "$@"; }   # call over SSH
l() { /usr/local/sbin/custos-helper "$@"; }                            # call as root, locally

my() { "$(command -v mariadb || command -v mysql)" --default-character-set=utf8mb4 "$@"; }
pg() { runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 "$@"; }
my_family() { [[ $1 == mariadb || $1 == mysql ]]; }

conf() { printf '%s\n' "$@" > /etc/custos/helper.conf; chmod 0600 /etc/custos/helper.conf; }

# --- Services ------------------------------------------------------------------------

start_services() {
  printf '%s\n' 'module(load="imuxsock")' '*.* /var/log/custos-test.log' > /etc/custos-rsyslog.conf
  rsyslogd -f /etc/custos-rsyslog.conf -i /run/custos-rsyslog.pid
  install -d -m 0755 /run/sshd
  ssh-keygen -A > /dev/null
  /usr/sbin/sshd
  local e v
  for e in $ENGINES; do
    case $e in
      mariadb | mysql)
        install -d -o mysql -g mysql /run/mysqld
        local mysqld=/usr/libexec/mysqld   # RHEL keeps it there
        [[ -x $mysqld ]] || mysqld=$(command -v mysqld || echo /usr/sbin/mysqld)
        if [[ ! -d /var/lib/mysql/mysql ]]; then
          if command -v mariadb-install-db > /dev/null; then mariadb-install-db --user=mysql > /dev/null
          elif command -v mysql_install_db > /dev/null; then mysql_install_db --user=mysql > /dev/null
          else "$mysqld" --initialize-insecure --user=mysql; fi
        fi
        if command -v mysqld_safe > /dev/null; then mysqld_safe --user=mysql > /dev/null 2>&1 &
        else "$mysqld" --user=mysql > /var/log/custos-mysqld.log 2>&1 & fi
        for _ in $(seq 90); do my -e 'SELECT 1' > /dev/null 2>&1 && break; sleep 1; done
        my -e 'SELECT VERSION()' ;;
      postgresql)
        if command -v pg_ctlcluster > /dev/null; then
          for v in /etc/postgresql/*/main; do v=${v%/main}; pg_ctlcluster "${v##*/}" main start; done
        else
          install -d -o postgres -g postgres /run/postgresql
          [[ -e /var/lib/pgsql/data/PG_VERSION ]] ||
            runuser -u postgres -- initdb --auth-local=peer -D /var/lib/pgsql/data > /dev/null
          runuser -u postgres -- pg_ctl -D /var/lib/pgsql/data -l /var/lib/pgsql/server.log -w start > /dev/null
        fi
        pg -At -c 'SELECT version()' ;;
    esac
  done
}

# --- Test data -----------------------------------------------------------------------

W="/srv/custos test/www"   # a root with a space in its path
D="$W/site1/data"
C=/srv/conf

make_files() {
  mkdir -p "$D/www/sub" "$D/logs" "$D/tmp/sess" "$D/logs-keep" "$C/sub" /srv/speed
  echo hello > "$D/www/index.php"
  echo spaces > "$D/www/name with spaces.txt"
  echo newline > "$D/www/new"$'\n'"line.txt"
  echo tab > "$D/www/tab"$'\t'"name"
  echo latin1 > "$D/www/bad"$'\xff'"name"
  echo old > "$D/www/old.txt"
  echo untouched > "$D/www/untouched.txt"
  echo restored > "$D/www/restored.txt"
  echo owned > "$D/www/owned.txt"; chown custos:custos "$D/www/owned.txt"; chmod 0640 "$D/www/owned.txt"
  echo deep > "$D/www/sub/deep.txt"
  ln -s /etc/shadow "$D/www/link-out"
  ln -s /etc "$D/www/dir-link"
  python3 -I -c 'import socket, sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])' "$D/php.sock"
  mkfifo "$D/fifo"
  echo excluded > "$D/logs/access.log"
  echo excluded > "$D/tmp/sess/sess_1"
  echo kept > "$D/logs-keep/keep.log"
  echo conf > "$C/sub/app.conf"
  head -c 614400 /dev/urandom > /srv/speed/600k.bin
  ln -s /etc /srv/link-root
}

make_databases() {
  local e
  for e in $ENGINES; do
    if my_family "$e"; then
      my <<'SQL'
CREATE DATABASE site_db CHARACTER SET utf8mb4;
USE site_db;
CREATE TABLE t (id INT PRIMARY KEY, txt VARCHAR(100), bin VARBINARY(16)) ENGINE=InnoDB;
INSERT INTO t VALUES (1, 'привет 🙂', 0x00FF10), (2, 'tab\there', NULL);
CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW SET NEW.txt = NEW.txt;
CREATE PROCEDURE p() SELECT 1;
CREATE EVENT ev ON SCHEDULE EVERY 1 DAY DO SELECT 1;
CREATE DATABASE `my-db$1`;
CREATE DATABASE `bad name`;
CREATE DATABASE skipped_db;
CREATE USER 'site_user'@'localhost' IDENTIFIED BY 'Site-Secret-123';
GRANT SELECT, INSERT ON site_db.* TO 'site_user'@'localhost';
CREATE USER 'custos_ro'@'localhost' IDENTIFIED BY 'Ro-Secret-123';
GRANT SELECT, SHOW VIEW, TRIGGER, EVENT, LOCK TABLES, PROCESS ON *.* TO 'custos_ro'@'localhost';
SQL
    else
      pg <<'SQL'
CREATE DATABASE site_db;
CREATE DATABASE "my-db$1";
CREATE DATABASE "bad name";
CREATE DATABASE skipped_db;
CREATE ROLE site_user LOGIN PASSWORD 'Site-Secret-123';
GRANT CONNECT ON DATABASE site_db TO site_user;
\c site_db
CREATE TABLE t (id int PRIMARY KEY, txt text, bin bytea);
INSERT INTO t VALUES (1, 'привет 🙂', '\x00ff10'), (2, E'tab\there', NULL);
CREATE FUNCTION f() RETURNS int LANGUAGE sql AS 'SELECT 1';
SQL
    fi
  done
}

# --- Install ---------------------------------------------------------------------------

install_block() { # install_block <key file> <from>: the README block with KEY and FROM filled in
  awk '/^<!-- install-block/ {f = 1; next} f && /^```bash$/ {b = 1; next} b && /^```$/ {exit} b' "$H/README.md" |
    sed -e "s|^KEY=.*|KEY='$(cat "$1")'|" -e "s|^FROM=.*|FROM='$2'|" > "$T/install.sh"
  bash "$T/install.sh"
}

test_install() {
  ssh-keygen -q -t ed25519 -N '' -C test-key-1 -f /root/.ssh/k1
  ssh-keygen -q -t ed25519 -N '' -C test-key-2 -f /root/.ssh/k2
  cp "$H/custos-helper" /tmp/custos-helper
  awk '/^<!-- install-block/ {f = 1; next} f && /^```bash$/ {b = 1; next} b && /^```$/ {exit} b' "$H/README.md" > "$T/install.sh"
  code 1 "install: the block refuses the placeholder key" bash "$T/install.sh"
  code 0 "install: the block installs custos-helper" install_block /root/.ssh/k1.pub 127.0.0.1
  check "install: the block ends with OK" has 'OK: custos-helper' "$T/out"
  check "install: the preset config is valid (info prints JSON)" json <(grep '^{' "$T/out") 'd["protocol"]' > /dev/null
  code 0 "install: running the block again is safe" install_block /root/.ssh/k1.pub 127.0.0.1
  code 0 "install: a second key, limited to another IP" install_block /root/.ssh/k2.pub 203.0.113.10
  check "install: one line per key in authorized_keys" want "$(wc -l < ~custos/.ssh/authorized_keys)" 2
  check "install: modes of script, config, sudoers" want \
    "$(stat -c '%a %U' /usr/local/sbin/custos-helper /etc/custos/helper.conf /etc/sudoers.d/custos | tr '\n' ' ')" \
    "755 root 600 root 440 root "
  code 1 "install: custos cannot read the config" runuser -u custos -- cat /etc/custos/helper.conf
  code 1 "install: custos cannot sudo anything else" runuser -u custos -- sudo -n /bin/true
}

# --- Command line and config -------------------------------------------------------------

test_refusals() {
  local c
  code 64 "refuse: no command" r
  for c in foo 'info x' tar-since 'tar-since abc' 'tar-since 1 2 3' 'tar-since 1 x' 'tar-since -1' \
    'cat /etc/shadow' 'info;id' 'info && id' "info\$(id)" $'info\nid' 'db-list' 'db-list oracle' \
    'db-dump' 'db-users' 'manifest x'; do
    code 64 "refuse: $(printf %q "$c")" r "$c"
  done
  for c in mariadb mysql postgresql; do
    [[ " $ENGINES " == *" $c "* ]] || code 64 "refuse: db-list $c (not in the config)" r db-list "$c"
  done
}

test_config_errors() {
  local good; good=$(cat /etc/custos/helper.conf)
  code 0 "config: comments, blank lines, CRLF and trailing slashes are fine" bash -c \
    "printf '# comment\r\n\r\n  root = /srv/conf/ \r\n' > /etc/custos/helper.conf && /usr/local/sbin/custos-helper info"
  local -A bad=(
    ["unknown key"]='rot = /srv' ["no ="]='root /srv' ["relative root"]='root = srv' ["root = /"]='root = /'
    ["root = ///"]='root = ///' ["relative exclude"]='exclude = *.log' ["unknown engine"]='db_engine = oracle'
    ["engine none"]='db_engine = none' ["mariadb and mysql"]=$'db_engine = mariadb\ndb_engine = mysql'
    ["missing defaults file"]='db_defaults_file = /nonexistent' ["relative defaults file"]='db_defaults_file = my.cnf'
  )
  for c in "${!bad[@]}"; do conf "${bad[$c]}"; code 78 "config: $c" l info; done
  conf 'root = /srv'
  chmod 0666 /etc/custos/helper.conf; code 78 "config: writable by others" l info
  chmod 0620 /etc/custos/helper.conf; code 78 "config: writable by the group" l info
  chmod 0600 /etc/custos/helper.conf; chown custos /etc/custos/helper.conf; code 78 "config: not owned by root" l info
  rm /etc/custos/helper.conf; code 78 "config: missing" l info
  printf '%s\n' "$good" > /etc/custos/helper.conf; chmod 0600 /etc/custos/helper.conf
}

# --- Operations ----------------------------------------------------------------------------

test_info() {
  code 0 "info: over SSH" r info
  cp "$T/out" "$T/info.json"
  check "info: valid JSON" json "$T/info.json" 'len(d)' > /dev/null
  check "info: version" want "$(json "$T/info.json" 'd["helper_version"]')" \
    "$(sed -n 's/^# custos-helper \([0-9.]*\):.*/\1/p' "$H/custos-helper")"
  check "info: protocol" want "$(json "$T/info.json" 'd["protocol"]')" 1
  check "info: engines" want "$(json "$T/info.json" '" ".join(d["db_engines"])')" "$ENGINES"
  check "info: config hash" want "$(json "$T/info.json" 'd["config_sha256"]')" \
    "$(sha256sum < /etc/custos/helper.conf | cut -d' ' -f1)"
  check "info: server time" test "$(( $(json "$T/info.json" 'd["now"]') - $(date +%s) ))" -le 5
  check "info: roots and free space" want "$(json "$T/info.json" \
    '"|".join(r["path"] + ("+" if r["free_bytes"] > 0 else "-") for r in d["roots"])')" "$W+|$C+"
  # ssh closes the session itself when a terminal it insisted on (-tt) is refused.
  code 255 "ssh: a terminal is refused (ssh -tt)" ssh -tt -i /root/.ssh/k1 "${SSH_OPTS[@]}" custos@127.0.0.1 info
  check "ssh: the refusal is reported" has 'PTY allocation request failed' "$T/err"
}

dump_ok() { # dump_ok <engine> <file> [users]: the end marker the agent checks
  if my_family "$1"; then [[ $(tail -n 1 "$2") == '-- Dump completed'* ]]; return; fi
  local marker='-- PostgreSQL database dump complete'
  [[ ${3-} == users ]] && marker='-- PostgreSQL database cluster dump complete'
  # After the marker: only "--", blank lines and one \unrestrict line (pg_dump since August 2025).
  awk -v m="$marker" '$0 == m {f = 1; n = 0; next} f && !/^(--)?$/ && !(/^\\unrestrict / && !n++) {f = 0} END {exit !f}' "$2"
}

data_of() { # data_of <engine> <database>: the test table and objects as text, to compare copies
  if my_family "$1"; then
    my -N -e "SELECT id, HEX(txt), HEX(bin) FROM t ORDER BY id; SHOW TRIGGERS;
      SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '$2';
      SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = '$2';" "$2" | cut -f1-3
  else
    pg -At -d "$2" -c "SELECT id, encode(convert_to(txt, 'UTF8'), 'hex'), encode(bin, 'hex') FROM t ORDER BY id" \
      -c "SELECT proname FROM pg_proc WHERE proname = 'f'"
  fi
}

test_databases() {
  local e names
  for e in $ENGINES; do
    names="my-db\$1 site_db"; [[ $e == postgresql ]] && names="my-db\$1 postgres site_db"
    code 0 "$e: db-list" r db-list "$e"
    check "$e: db-list skips system databases and db_exclude" want "$(sort "$T/out" | tr '\n' ' ')" "$names "
    check "$e: db-list reports an odd name on stderr" has 'bad\ name' "$T/err"

    code 0 "$e: db-dump" r db-dump "$e" site_db
    cp "$T/out" "$T/$e.sql"
    check "$e: dump ends with the completion marker" dump_ok "$e" "$T/$e.sql"
    l db-dump "$e" site_db > "$T/$e-local.sql" 2> /dev/null
    check "$e: dump over SSH equals the local one" cmp -s \
      <(grep -Ev '^(-- Dump completed|\\(un)?restrict )' "$T/$e.sql") \
      <(grep -Ev '^(-- Dump completed|\\(un)?restrict )' "$T/$e-local.sql")
    for c in mysql skipped_db missing_db 'my-db' '-x' '../site_db'; do
      code 64 "$e: refuse db-dump $c" r db-dump "$e" "$c"
    done
    code 64 "$e: refuse db-dump of a name with a space" r db-dump "$e" bad name

    if my_family "$e"; then
      my -e 'CREATE DATABASE site_copy' && my site_copy < "$T/$e.sql"
    else
      pg -c 'CREATE DATABASE site_copy' && pg -d site_copy < "$T/$e.sql" > /dev/null
    fi
    check "$e: dump restores into an equal copy" want "$(data_of "$e" site_copy)" "$(data_of "$e" site_db)"

    code 0 "$e: db-users" r db-users "$e"
    cp "$T/out" "$T/$e-users.sql"
    check "$e: users end with the completion marker" dump_ok "$e" "$T/$e-users.sql" users
    check "$e: users include site_user" has site_user "$T/$e-users.sql"
    if my_family "$e"; then
      check "$e: users are created with IF NOT EXISTS" has 'CREATE USER IF NOT EXISTS' "$T/$e-users.sql"
      my -e "DROP USER 'site_user'@'localhost'"
      my < "$T/$e-users.sql"
      check "$e: users restore with their grants" has 'site_db' <(my -N -e "SHOW GRANTS FOR 'site_user'@'localhost'")
    else
      pg -c 'REVOKE CONNECT ON DATABASE site_db FROM site_user' -c 'DROP ROLE site_user'
      runuser -u postgres -- psql -X -q -f "$T/$e-users.sql" > /dev/null 2>&1
      check "$e: roles restore" want "$(pg -At -c "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'site_user'")" t
    fi
  done
}

test_defaults_file() { # MySQL family through db_defaults_file and the read-only user from the README
  local e good; good=$(cat /etc/custos/helper.conf)
  for e in $ENGINES; do
    my_family "$e" || continue
    printf '[client]\nuser = custos_ro\npassword = Ro-Secret-123\n' > /etc/custos/my.cnf
    chmod 0600 /etc/custos/my.cnf
    conf "db_engine = $e" 'db_defaults_file = /etc/custos/my.cnf'
    code 0 "$e: db-list as the read-only user" r db-list "$e"
    code 0 "$e: db-dump as the read-only user" r db-dump "$e" site_db
    check "$e: read-only dump is complete" dump_ok "$e" "$T/out"
    code 0 "$e: db-users as the read-only user" r db-users "$e"
    sed -i 's/Ro-Secret-123/wrong/' /etc/custos/my.cnf
    code 1 "$e: a wrong password fails with the client's code, not 64" r db-dump "$e" site_db
    check "$e: the client's error reaches stderr" has 'Access denied' "$T/err"
  done
  printf '%s\n' "$good" > /etc/custos/helper.conf; chmod 0600 /etc/custos/helper.conf
}

members() { tar -tf "$1" | sed 's|/$||'; } # tar escapes \n and \t in names

test_files() {
  code 0 "manifest: over SSH" r manifest
  cp "$T/out" "$T/manifest.gz"
  check "manifest: no warnings (the socket is skipped quietly)" test ! -s "$T/err"
  check "manifest: content" python3 -I - "$T/manifest.gz" <<'PY'
import gzip, sys
f = gzip.open(sys.argv[1]).read().split(b'\0')
assert f.pop() == b'' and len(f) % 11 == 0, 'not 11 NUL-terminated fields per record'
rec = {f[i + 10]: f[i:i + 10] for i in range(0, len(f), 11)}
D = b'/srv/custos test/www/site1/data'
need = [b'/srv/custos test/www', b'/srv/conf', b'/srv/conf/sub/app.conf', D + b'/www/index.php',
        D + b'/www/new\nline.txt', D + b'/www/tab\tname', D + b'/www/bad\xffname', D + b'/logs-keep/keep.log',
        D + b'/fifo']
bad = [p for p in need if p not in rec]
bad += [p for p in rec if b'/data/logs/' in p or b'/data/tmp' in p or p.endswith(b'/data/logs')
        or b'php.sock' in p or b'dir-link/' in p]
t, size, mtime, ctime, mode, uid, gid, user, group, link = rec[D + b'/www/link-out']
bad += [] if (t, link) == (b'l', b'/etc/shadow') else [b'link-out']
t, size, mtime, ctime, mode, uid, gid, user, group, link = rec[D + b'/www/owned.txt']
bad += [] if (t, size, mode, user, group) == (b'f', b'6', b'640', b'custos', b'custos') else [b'owned.txt']
float(mtime), float(ctime)
if bad:
    sys.exit('\n'.join(map(repr, bad)))
PY

  code 0 "tar-since 0: over SSH" r tar-since 0
  cp "$T/out" "$T/full.tar"
  check "tar-since 0: no warnings" test ! -s "$T/err"
  l tar-since 0 > "$T/full-local.tar" 2> /dev/null
  check "tar-since 0: byte for byte equal to a local run" cmp -s "$T/full.tar" "$T/full-local.tar"
  check "tar-since 0: names are relative" test -z "$(members "$T/full.tar" | grep '^/')"
  members "$T/full.tar" > "$T/full.list"
  check "tar-since 0: excluded folders are left out" test -z "$(grep -E '/data/(logs|tmp)(/|$)' "$T/full.list")"
  check "tar-since 0: other folders stay" has 'site1/data/logs-keep/keep.log' "$T/full.list"
  rm -rf "$T/x"; mkdir "$T/x"
  check "tar-since 0: unpacks" tar -xpf "$T/full.tar" -C "$T/x"
  local X="$T/x$D"
  check "tar-since 0: file contents" cmp -s "$X/www/index.php" "$D/www/index.php"
  check "tar-since 0: name with a newline" cmp -s "$X/www/new"$'\n'"line.txt" "$D/www/new"$'\n'"line.txt"
  check "tar-since 0: name with a tab" cmp -s "$X/www/tab"$'\t'"name" "$D/www/tab"$'\t'"name"
  check "tar-since 0: name not in UTF-8" cmp -s "$X/www/bad"$'\xff'"name" "$D/www/bad"$'\xff'"name"
  check "tar-since 0: owner and mode" want "$(stat -c '%U:%G %a' "$X/www/owned.txt")" 'custos:custos 640'
  check "tar-since 0: a link is stored as a link" want "$(readlink "$X/www/link-out")" /etc/shadow
  check "tar-since 0: a link to a folder is not followed" test -L "$X/www/dir-link"
  check "tar-since 0: a FIFO is kept" test -p "$X/fifo"
  check "tar-since 0: no socket" test ! -e "$X/php.sock"

  # Changes after time T: content, mode only, rename, edit with the old mtime put back, new file.
  sleep 1.1; local since; since=$(date +%s); sleep 1.1
  echo more >> "$D/www/index.php"
  chmod 0600 "$D/www/name with spaces.txt"
  mv "$D/www/old.txt" "$D/www/renamed.txt"
  echo changed > "$D/www/restored.txt"; touch -r "$D/www/untouched.txt" "$D/www/restored.txt"
  echo new > "$D/www/sub/new.txt"
  code 0 "tar-since T: over SSH" r tar-since "$since"
  tar -tvf "$T/out" | grep -v '^d' | sed 's|.*site1/data/www/||' | sort > "$T/changed.list"
  check "tar-since T: exactly the changed files" want "$(tr '\n' '|' < "$T/changed.list")" \
    'index.php|name with spaces.txt|renamed.txt|restored.txt|sub/new.txt|'
  check "tar-since T: all folders are included" want "$(tar -tvf "$T/out" | grep -c '^d')" \
    "$(tar -tvf "$T/full.tar" | grep -c '^d')"
  cp "$T/out" "$T/changed.tar"
  code 0 "tar-since: a time with a leading zero is still decimal" r tar-since "0$since"
  check "tar-since: same result with the leading zero" cmp -s "$T/out" "$T/changed.tar"

  # A missing root and a root that is a link are skipped with a warning (exit 1).
  local good; good=$(cat /etc/custos/helper.conf)
  conf "root = $C" 'root = /srv/missing' 'root = /srv/link-root'
  code 1 "warning: tar-since with a missing root and a linked root" r tar-since 0
  check "warning: names the missing root" has '/srv/missing' "$T/err"
  check "warning: names the linked root" has '/srv/link-root' "$T/err"
  check "warning: the other root is still there" has 'srv/conf/sub/app.conf' <(members "$T/out")
  code 1 "warning: manifest with a missing root" r manifest
  conf '# no roots'
  code 0 "files: no roots gives an empty but valid tar" r tar-since 0
  check "files: the empty tar is valid" test -z "$(tar -tf "$T/out")"

  # Speed limit: 600 KB at 100 KB/s means a second of sleep every 100 KB.
  conf 'root = /srv/speed'
  local start; start=$(date +%s)
  code 0 "speed: tar-since 0 100" r tar-since 0 100
  check "speed: took at least 4 seconds" test $(( $(date +%s) - start )) -ge 4
  cp "$T/out" "$T/slow.tar"
  start=$(date +%s)
  code 0 "speed: unlimited" r tar-since 0
  check "speed: unlimited is fast" test $(( $(date +%s) - start )) -le 3
  check "speed: same data with and without a limit" cmp -s "$T/slow.tar" "$T/out"
  printf '%s\n' "$good" > /etc/custos/helper.conf; chmod 0600 /etc/custos/helper.conf
}

test_ssh_limits() {
  code 255 "ssh: the key limited to 203.0.113.10 is refused" \
    ssh -i /root/.ssh/k2 "${SSH_OPTS[@]}" custos@127.0.0.1 info
  code 255 "ssh: remote port forwarding is refused" \
    ssh -i /root/.ssh/k1 "${SSH_OPTS[@]}" -o ExitOnForwardFailure=yes -R 127.0.0.1:2222:127.0.0.1:22 custos@127.0.0.1 info
  check "ssh: sftp does not work" bash -c \
    '! sftp -b - -i /root/.ssh/k1 -o BatchMode=yes -o LogLevel=ERROR custos@127.0.0.1 <<< "get /etc/passwd /tmp/p" 2>/dev/null'
  check "ssh: scp does not work" bash -c \
    '! scp -i /root/.ssh/k1 -o BatchMode=yes -o LogLevel=ERROR custos@127.0.0.1:/etc/passwd /tmp/p 2>/dev/null'
}

test_syslog() {
  local log=/var/log/custos-test.log
  for _ in $(seq 10); do grep -q 'cmd=info exit=0' "$log" 2> /dev/null && break; sleep 1; done
  check "syslog: a successful call" grep -q 'custos-helper.*cmd=info exit=0' "$log"
  check "syslog: a refused call, with its command" grep -qF 'cmd=cat\ /etc/shadow exit=64' "$log"
}

test_database_down() { # last: stops the servers
  local e
  for e in $ENGINES; do
    if my_family "$e"; then "$(command -v mariadb-admin || command -v mysqladmin)" shutdown
    elif command -v pg_ctlcluster > /dev/null; then pg_ctlcluster "$(ls /etc/postgresql)" main stop
    else runuser -u postgres -- pg_ctl -D /var/lib/pgsql/data -w stop > /dev/null; fi
    sleep 1
    code "$( [[ $e == postgresql ]] && echo 2 || echo 1 )" "$e: server down: db-dump fails with the client's code" \
      r db-dump "$e" site_db
  done
}

main() {
  mkdir -p "$T" /root/.ssh
  echo "== $(grep '^PRETTY_NAME=' /etc/os-release), bash $BASH_VERSION, $(tar --version | head -n 1)"
  start_services
  test_install
  make_files
  make_databases
  local -a lines=("root = $W/" "root = $C" "exclude = $W/*/data/logs" "exclude = $W/*/data/tmp/" 'db_exclude = skipped_db')
  for e in $ENGINES; do lines+=("db_engine=$e"); done   # also checks that spaces around = are optional
  conf "${lines[@]}"
  test_refusals
  test_config_errors
  test_info
  test_databases
  test_defaults_file
  test_files
  test_ssh_limits
  test_syslog
  test_database_down
  echo "== $PASSED passed, $FAILED failed"
  (( FAILED == 0 ))
}

main
