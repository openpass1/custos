#!/usr/bin/env bash
# Starts the servers of the CI job "destinations" (.github/workflows/build.yml) for RealDestinationTests: remote
# destinations of stage 4 (section 6.3 of CUSTOS-client.md) against real servers on 127.0.0.1.
#
#   SFTP     OpenSSH on port 2223, login by password and by key
#   FTP      vsftpd on port 2121
#   FTPS     vsftpd on port 2122, explicit TLS with a self-signed certificate
#   WebDAV   Apache mod_dav on http://127.0.0.1:8081/dav and https://127.0.0.1:8443/dav (the same certificate)
#   S3       MinIO on port 9000, built from its source with Go when CUSTOS_MINIO=build (the CI job; MinIO no longer
#            publishes programs or images); otherwise moto_server if it is installed (development: it checks no
#            passwords and has no Object Lock, so those tests are skipped)
#
# Needs root (or sudo without a password) and the packages openssh-server, vsftpd, apache2. Writes the variables
# CUSTOS_TEST_SFTP, _FTP, _FTPS, _WEBDAV, _WEBDAVS and _S3 to GITHUB_ENV. Run again, it starts everything afresh.
set -euo pipefail

user=custosdst
password='Dst-Test-2026'
dir=${CUSTOS_DEST_DIR:-/tmp/custos-destinations}
sudo=()
[[ $(id -u) -eq 0 ]] || sudo=(sudo)
out=${GITHUB_ENV:-/dev/stdout}

if ! command -v vsftpd > /dev/null || ! command -v apache2 > /dev/null || [[ ! -x /usr/sbin/sshd ]]; then
  "${sudo[@]}" apt-get update -q > /dev/null
  DEBIAN_FRONTEND=noninteractive "${sudo[@]}" apt-get install -y -q openssh-server vsftpd apache2 > /dev/null
fi

# ---- What a previous run left ------------------------------------------------------------------------------------
for pid in "$dir"/*.pid; do
  [[ -f $pid ]] && "${sudo[@]}" kill "$(cat "$pid")" 2> /dev/null || true
done
"${sudo[@]}" pkill -f "vsftpd $dir/" 2> /dev/null || true
"${sudo[@]}" pkill -f "moto_server.*-p 9000" 2> /dev/null || true
sleep 1
"${sudo[@]}" rm -rf "$dir"
mkdir -p "$dir"

# ---- The account all servers know --------------------------------------------------------------------------------
id "$user" > /dev/null 2>&1 || "${sudo[@]}" useradd --create-home --shell /bin/bash "$user"
echo "$user:$password" | "${sudo[@]}" chpasswd
home=$(getent passwd "$user" | cut -d: -f6)
"${sudo[@]}" rm -rf "$home/sftp" "$home/ftp" "$home/ftps" "$home/dav"
"${sudo[@]}" install -d -o "$user" -g "$user" "$home/sftp" "$home/ftp" "$home/ftps" "$home/dav"

openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=127.0.0.1" -addext "subjectAltName=IP:127.0.0.1" \
  -keyout "$dir/tls.key" -out "$dir/tls.crt" 2> /dev/null

# ---- SFTP --------------------------------------------------------------------------------------------------------
ssh-keygen -q -t ed25519 -N '' -f "$dir/ssh_host_ed25519_key"
ssh-keygen -q -t ed25519 -N '' -C custos-test -f "$dir/client_key"
cp "$dir/client_key.pub" "$dir/authorized_keys"
chmod 644 "$dir/authorized_keys"
cat > "$dir/sshd_config" <<EOF
Port 2223
ListenAddress 127.0.0.1
HostKey $dir/ssh_host_ed25519_key
PidFile $dir/sshd.pid
PasswordAuthentication yes
KbdInteractiveAuthentication no
PubkeyAuthentication yes
AuthorizedKeysFile $dir/authorized_keys
StrictModes no
UsePAM no
Subsystem sftp internal-sftp
AllowUsers $user
EOF
"${sudo[@]}" install -d -m 0755 /run/sshd
"${sudo[@]}" /usr/sbin/sshd -f "$dir/sshd_config"

# ---- FTP and FTPS ------------------------------------------------------------------------------------------------
ftp_config() { # name port first-passive-port
  cat > "$dir/$1.conf" <<EOF
listen=YES
listen_address=127.0.0.1
listen_port=$2
background=YES
anonymous_enable=NO
local_enable=YES
write_enable=YES
local_umask=022
pasv_enable=YES
pasv_address=127.0.0.1
pasv_min_port=$3
pasv_max_port=$(($3 + 19))
seccomp_sandbox=NO
xferlog_enable=NO
pam_service_name=vsftpd
utf8_filesystem=YES
EOF
}
ftp_config vsftpd 2121 30100
ftp_config vsftpd-tls 2122 30120
cat >> "$dir/vsftpd-tls.conf" <<EOF
ssl_enable=YES
rsa_cert_file=$dir/tls.crt
rsa_private_key_file=$dir/tls.key
force_local_logins_ssl=YES
force_local_data_ssl=YES
require_ssl_reuse=NO
ssl_ciphers=HIGH
EOF
"${sudo[@]}" install -d -m 0755 /var/run/vsftpd/empty
"${sudo[@]}" chown root:root "$dir/vsftpd.conf" "$dir/vsftpd-tls.conf"
"${sudo[@]}" vsftpd "$dir/vsftpd.conf"
"${sudo[@]}" vsftpd "$dir/vsftpd-tls.conf"

# ---- WebDAV ------------------------------------------------------------------------------------------------------
modules=/usr/lib/apache2/modules
mkdir -p "$dir/www" "$dir/davlock"
"${sudo[@]}" chown "$user" "$dir/davlock"
printf '%s:%s\n' "$user" "$(openssl passwd -apr1 "$password")" > "$dir/htpasswd"
cat > "$dir/apache.conf" <<EOF
ServerRoot $dir
ServerName 127.0.0.1
PidFile $dir/apache.pid
ErrorLog $dir/apache-error.log
LogLevel warn
User $user
Group $user
LoadModule mpm_event_module $modules/mod_mpm_event.so
LoadModule authn_core_module $modules/mod_authn_core.so
LoadModule authn_file_module $modules/mod_authn_file.so
LoadModule authz_core_module $modules/mod_authz_core.so
LoadModule authz_user_module $modules/mod_authz_user.so
LoadModule auth_basic_module $modules/mod_auth_basic.so
LoadModule alias_module $modules/mod_alias.so
LoadModule dav_module $modules/mod_dav.so
LoadModule dav_fs_module $modules/mod_dav_fs.so
LoadModule socache_shmcb_module $modules/mod_socache_shmcb.so
LoadModule ssl_module $modules/mod_ssl.so
Listen 127.0.0.1:8081
Listen 127.0.0.1:8443
DocumentRoot $dir/www
DavLockDB $dir/davlock/lock
Alias /dav $home/dav
<Directory $home/dav>
  Dav On
  AuthType Basic
  AuthName "CUSTOS test"
  AuthUserFile $dir/htpasswd
  Require valid-user
</Directory>
<VirtualHost 127.0.0.1:8443>
  SSLEngine on
  SSLCertificateFile $dir/tls.crt
  SSLCertificateKeyFile $dir/tls.key
</VirtualHost>
EOF
"${sudo[@]}" apache2 -f "$dir/apache.conf" -k start

# ---- S3 ----------------------------------------------------------------------------------------------------------
s3_key=custosminio
s3_secret='Minio-Test-Secret-2026'
s3_kind=
# The last version of the MinIO server, as the Go module proxy names it; its checksum is checked by Go (sum.golang.org).
minio_version=${MINIO_VERSION:-v0.0.0-20260212201848-7aac2a2c5b7c}
minio=${CUSTOS_CI_CACHE:-$HOME/.cache/custos-ci}/minio-$minio_version
if [[ ${CUSTOS_MINIO:-} == build && ! -x $minio ]]; then
  echo "S3: building MinIO $minio_version (a few minutes; the CI job keeps the result for the next runs)"
  command -v go > /dev/null || { echo "Go is needed to build MinIO" >&2; exit 1; }
  mkdir -p "$(dirname "$minio")"
  GOBIN=$dir/gobin GOTOOLCHAIN=auto CGO_ENABLED=0 go install -ldflags '-s -w' "github.com/minio/minio@$minio_version"
  mv "$dir/gobin/minio" "$minio"
fi
if [[ ${CUSTOS_MINIO:-} == build ]]; then
  mkdir -p "$dir/minio-data"
  MINIO_ROOT_USER="$s3_key" MINIO_ROOT_PASSWORD="$s3_secret" MINIO_BROWSER=off MINIO_UPDATE=off \
    nohup "$minio" server --quiet --address 127.0.0.1:9000 "$dir/minio-data" > "$dir/minio.log" 2>&1 &
  echo $! > "$dir/minio.pid"
  s3_kind=minio
else
  moto=$(command -v moto_server || true)
  [[ -n $moto || ! -x /opt/moto-venv/bin/moto_server ]] || moto=/opt/moto-venv/bin/moto_server
  if [[ -n $moto ]]; then
    nohup "$moto" -H 127.0.0.1 -p 9000 > "$dir/moto.log" 2>&1 &
    echo $! > "$dir/moto.pid"
    s3_kind=moto
  fi
fi

# ---- Wait until they answer ----------------------------------------------------------------------------------------
wait_port() {
  for _ in $(seq 60); do
    if (exec 3<> "/dev/tcp/127.0.0.1/$1") 2> /dev/null; then
      return 0
    fi
    sleep 0.5
  done
  echo "nothing answers on port $1" >&2
  return 1
}
for port in 2223 2121 2122 8081 8443; do
  wait_port "$port"
done
ssh-keyscan -p 2223 127.0.0.1 2> /dev/null | ssh-keygen -lf -

{
  echo "CUSTOS_TEST_SFTP=127.0.0.1|2223|$user|$password|$home/sftp|$dir/client_key"
  echo "CUSTOS_TEST_FTP=127.0.0.1|2121|$user|$password|ftp"
  echo "CUSTOS_TEST_FTPS=127.0.0.1|2122|$user|$password|ftps"
  echo "CUSTOS_TEST_WEBDAV=http://127.0.0.1:8081/dav|$user|$password"
  echo "CUSTOS_TEST_WEBDAVS=https://127.0.0.1:8443/dav|$user|$password"
} >> "$out"
if [[ -n $s3_kind ]]; then
  wait_port 9000 || { cat "$dir"/minio.log "$dir"/moto.log 2> /dev/null; exit 1; }
  echo "CUSTOS_TEST_S3=http://127.0.0.1:9000|$s3_key|$s3_secret|$s3_kind" >> "$out"
fi
