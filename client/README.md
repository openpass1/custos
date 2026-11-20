# CUSTOS client for Windows and macOS

[По-русски](README.ru.md)

C# / .NET 10, x64. Specification: [`docs/CUSTOS-client.md`](../docs/CUSTOS-client.md), interface design: [`docs/client-ui.md`](../docs/client-ui.md) (both in Russian).

| Project | Output | What it is |
|---|---|---|
| `src/Custos.Core` | `Custos.Core.dll` | Shared code: task model, schedule, copy engine, set manifests, Cobian import, the interface/service pipe protocol |
| `src/Custos.Service` | `CustosService.exe` | Windows service (LocalSystem): does all the work — queue, runs, VSS, share logons, database |
| `src/Custos.Gui` | `Custos.exe` | Tray interface, one per user session; only shows state and sends commands |
| `src/Custos.Updater` | `Custos.Updater.exe` | Installs an update (from the monitoring server or GitHub): stops the service, replaces the files, starts it again, rolls back on failure |
| `tests/Custos.Core.Tests` | | xUnit tests of the core; `tests/fixtures` holds sample files for them |
| `tests/Custos.Gui.Tests` | | Window tests: open the real windows with made-up data (run on Windows only) |
| `src/Custos.Mac` | `Custos.app` | CUSTOS for macOS: one program that is both the service (`Custos agent`, started by launchd as root) and the window with the menu bar icon (Avalonia). The service is the code of `src/Custos.Service` with a macOS platform layer. See [macOS](#macos) |
| `tests/Custos.Mac.Tests`, `tests/Custos.Mac.Shots` | | Tests of the macOS program (its service started inside the tests and driven over its socket) and pictures of every window |
| `mac/` | | The macOS installer: `build-mac.sh`, `build-7z.sh`, the launchd jobs, the uninstaller |
| `installer/custos.iss` | | Inno Setup script |

## Build

Needs Windows x64 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[Inno Setup 6.3+](https://jrsoftware.org/isdl.php). 7-Zip need not be installed (see below).

```powershell
cd client
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The installer appears in `artifacts\installer\CUSTOS-Setup-<version>.exe`, the update package for the monitoring
server in `artifacts\update\custos-<version>.zip` (the program files and `manifest.json` with the SHA-256 of each).
Options: `-SkipTests`, `-SkipInstaller`, `-Version 0.4.1`, `-TestUpdateVersion 0.4.1.1` (also builds the same code
with a higher version into `artifacts\update-test\custos-0.4.1.1-test.zip`, to try an update from the server on a
machine installed from this very build), `-RefreshRuntime`. The first build downloads the .NET Desktop Runtime
installer (about 60 MB) into `installer\redist`; it is bundled into setup.exe so that target machines need no internet.

`7z.dll` always comes from the 7-Zip build pinned in [`installer/7-zip.json`](installer/7-zip.json) (version, links and
SHA-256 of the official x64 installer and `7zr.exe` from [github.com/ip7z/7zip](https://github.com/ip7z/7zip/releases)).
The build downloads both once into `installer\redist\7-zip\<version>`, refuses a file whose SHA-256 differs, unpacks
`7z.dll` and the 7-Zip license from the installer and ships them; the tests use the same `7z.dll`. To move to a new
7-Zip, change the seven values in `7-zip.json`. Once a month the workflow `7-Zip release check` opens an issue when a
newer 7-Zip is out, with the values to paste.

GitHub Actions runs the same script on every change under `client/` (workflow `Client build`) and keeps three files
for 14 days: the installer (`CUSTOS-Setup`, a zip around the .exe) and two files that go to the site as they are,
without unpacking: the update package `custos-0.4.<run number>.zip` and a test update
`custos-0.4.<run number>.1-test.zip`. Each run has its own version `0.4.<run number>`, so a newer build can be
installed over an older one from the server. The update package has the version of the installer of the same run: it
updates machines that run an older build. The test update is the same code with a higher version, to try an update
from the site on a machine that was just installed from this run. The page of a run says the same above the files.
The job `monitor` checks the whole protocol against a real CUSTOS Monitor (`server/`, PHP's own web server and
MariaDB). The job `destinations` runs whole backups into real servers on the runner: OpenSSH, vsftpd with and without
TLS, Apache WebDAV over http and https, MinIO with Object Lock ([`tools/ci-destinations.sh`](tools/ci-destinations.sh);
the same script starts the servers on a development machine and prints the `CUSTOS_TEST_*` variables the tests read).
MinIO no longer publishes programs or images, so the job builds the server from its source with Go at a fixed version
and keeps the result in the Actions cache.

For development: `dotnet build Custos.sln` and `dotnet test Custos.sln`. The solution also builds on Linux and macOS
(tests that need Windows are skipped there, and the window tests are not run).

The window tests open windows on the desktop for a moment; a test that waits longer than a minute (a message box
nobody clicks) fails instead of hanging the build.

Texts of the program are in `src/Custos.Core/Resources/Messages.tsv` and `src/Custos.Gui/Resources/Strings.tsv`
(English and Russian side by side); after a change run `python3 tools/make-strings.py` in `client/`.

## Edition for Windows 7 and 8.1

A separate build of the same code for old computers: **.NET 6, 32-bit (x86)**, for Windows 7 SP1 and 8.1, 32- and
64-bit. It is released as is (`client-legacy-vX.Y.Z` on [Releases](https://github.com/openpass1/custos/releases),
«CUSTOS for Windows 7 and 8.1»: `CUSTOS-Legacy-Setup-<version>.exe`) and updated only when something serious breaks.
Windows 7 and 8.1 and .NET 6 get no more fixes from Microsoft, so use it only where Windows cannot be upgraded.

- **Setup.** The installer carries the x86 .NET 6 Desktop Runtime and installs it when missing, like the standard
  one does with .NET 10. It refuses Windows 8 (8.0, needs the free update to 8.1), and Windows 7 without the updates
  .NET 6 needs: KB3063858 and the Universal C Runtime (KB2999226 or the Visual C++ 2015-2019 Redistributable x86). On
  Windows 10 and later it suggests the standard CUSTOS instead.
- **Not in this edition:** cloud drives (Google Drive, Yandex Disk, Dropbox: their sign-in needs a current browser),
  the dark theme, the 7-Zip update from the site (the site keeps the 64-bit 7-Zip). These are not shown at all, in
  the program and on the site. 7z.dll is the 32-bit one: the button «Check for a 7-Zip update» downloads the 32-bit
  7-Zip from GitHub, and by hand 7z.dll is taken from the 32-bit 7-Zip (`7zXXXX.exe`, without `-x64`).
- **Works the same:** local, network and USB folders, SFTP, FTP/FTPS, WebDAV, S3 with Object Lock, VSS, databases,
  custos-helper, scripts, the monitoring server, export and import, Cobian import. Connections over TLS use TLS 1.2
  (Windows 7 offers only TLS 1.0 unless asked; neither knows TLS 1.3).
- **64-bit Windows.** The 32-bit program reads sources as 64-bit Windows sees them: `C:\Windows\System32` (not
  SysWOW64), `%ProgramFiles%` = `C:\Program Files`, the 64-bit registry, cmd.exe and PowerShell for task scripts.
- **Updates.** Without a monitoring server it looks for `client-legacy-vX.Y.Z` releases and their
  `custos-legacy-X.Y.Z.zip`. On the site, packages of this edition are uploaded on the «Updates» page like the standard
  ones (their own block appears once such a computer is connected) and go only to computers of this edition; a
  package of one edition is never installed on the other.
- **Build:** `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Legacy` with the same .NET 10 SDK. It downloads
  the x86 .NET 6 Desktop Runtime and the 32-bit 7-Zip installer (`installer32Url` in `7-zip.json`) and writes
  `artifacts-legacy\installer\CUSTOS-Legacy-Setup-<version>.exe` and `artifacts-legacy\update\custos-legacy-<version>.zip`.
  Tests are not run for this edition (they run on the standard one; the code is the same). In code the edition is
  `-p:CustosLegacy=true` (`CUSTOS_LEGACY`); the missing .NET APIs are in `src/Legacy`. The job `legacy` of
  `Client build` builds it, installs it on the runner and checks that the 32-bit service starts.

## macOS

CUSTOS for macOS runs on Macs with Apple silicon (M1 and newer) and on Macs with Intel, with macOS 15 Sequoia or
newer. One installer serves both: it puts on the Mac the program built for its processor. It does the same
work as on Windows: the service makes the copies on schedule, also when nobody is logged in, and the window with the
icon in the menu bar only shows and sends commands. The tasks, the destinations, the monitoring site and the export
file are the same, so a task can move between Windows and a Mac.

**Install.** Run `Custos-<version>.pkg` from the release. The program is not signed by Apple yet (that needs an Apple
Developer ID, a paid membership), so macOS says it «was not opened» because Apple could not check it. Press **Done**
(not «Move to Trash»), then System Settings → Privacy & Security, scroll down to **Security**: next to the line about
`Custos-<version>.pkg` press **Open Anyway** and type the password of the Mac. Open the .pkg again and press **Open
Anyway** in the question. (macOS 15 no longer opens such a file with right click → Open.) The button stays there for
about an hour after the first try. Another way, in Terminal: type `xattr -c ` with a space, drag the .pkg into the
Terminal window, press Enter; then the .pkg opens with a double click. The installer puts `Custos.app` into
Applications, starts the service and the menu bar icon, and opens the window.

**Full Disk Access.** Without it macOS hides the Desktop, Documents, Mail and other folders of the users from the
service, and APFS snapshots cannot be read. System Settings → Privacy & Security → **Full Disk Access** → **+** →
Applications → **Custos**, and switch it on. While the program is signed only ad hoc, macOS may forget the switch after
an update: check it after each update.

**What differs from Windows:**
- Instead of a shadow copy (VSS) the files are read from an APFS snapshot of the disk («Read files from a snapshot»);
  it is deleted when the run ends.
- Passwords of accounts are encrypted with a key kept in the System keychain (item `org.custos.agent`), instead of DPAPI.
- A network folder (`//server/share` or `\\server\share`) is mounted with the stored account for the run and unmounted
  after it. A USB disk is found by its name under `/Volumes`.
- The window of the Mac does not set up database servers, Linux servers (custos-helper) and cloud drives yet. Such
  sources and destinations come with a task imported from Windows or made on the monitoring site; the window shows
  them and keeps them as they are.
- Scripts before and after a run are run as root by `/bin/sh` (`.zsh` files by `/bin/zsh`). Shutdown, restart or
  sleep after a run work as on Windows, and the Mac does not fall asleep during a run.
- 7-Zip comes inside the program (`7z.so`, built from the same pinned 7-Zip sources); there is no 7-Zip update button
  and the site does not update it.

**Updates.** Without a monitoring site the service checks the GitHub releases once a day, as on Windows, and installs
`custos-macos-<version>.zip` of the same release (`manifest.json` and `Custos.pkg`, checked by SHA-256) with the
macOS installer. A monitoring site 1.3.0 or newer updates Macs too: on its «Updates» page the packages of macOS
(`custos-macos-<version>.zip`) have their own block and go only to Macs.

**Uninstall.** In Terminal:

```sh
sudo /Applications/Custos.app/Contents/Resources/uninstall.sh          # the program; tasks, settings and logs stay
sudo /Applications/Custos.app/Contents/Resources/uninstall.sh --all    # also tasks, settings, logs and the key
```

The copies already made are never touched.

**Files on a Mac:**

| Where | What |
|---|---|
| `/Applications/Custos.app` | The program (with its own .NET and `7z.so`) |
| `/Library/LaunchDaemons/org.custos.agent.plist` | The service: `sudo launchctl kickstart -k system/org.custos.agent` restarts it |
| `/Library/LaunchAgents/org.custos.menu.plist` | The menu bar icon of every user who logs in |
| `/Library/Application Support/CUSTOS/` | `config/`, `db/`, `logs/` as in `C:\ProgramData\CUSTOS` on Windows; `logs/launchd.log` holds what the service printed |
| `/var/run/custos.sock` | The connection between the window and the service |
| `~/Library/Application Support/CUSTOS/` | Window size, card and columns of this user |

**Build** (on a Mac with Apple silicon or Intel, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and the
command line tools of Xcode: `xcode-select --install`):

```sh
bash client/mac/build-mac.sh 1.0.1
```

The installer appears in `client/artifacts-mac/Custos-<version>.pkg`, the update package in
`client/artifacts-mac/custos-macos-<version>.zip`. `7z.so` is built first from the 7-Zip sources pinned in
[`mac/7-zip-source.json`](mac/7-zip-source.json) (download checked by SHA-256). GitHub Actions does the same on a Mac
of GitHub for every change under `client/` (workflow `macOS client`): it runs the tests of the core and of the Mac
program (as a user and as root), draws every window, builds the installer, installs it on that Mac, checks that the
service answers and runs a task, opens the window and removes everything with the uninstaller. The run keeps the
installer, the update package and the pictures of the windows for 14 days.

## CUSTOS for Linux

CUSTOS for Linux is the backup agent of a Linux server: a systemd service without a window, run from a monitoring
site. It does the same work as the service on Windows (copies and archives, schedules, keeping copies, checks, the
same destinations except cloud drives) and is set up on the site: the tasks of the server are made and changed there.
It needs x86_64 or ARM64 (aarch64), glibc 2.28 or newer and systemd: Ubuntu 20.04+, Debian 10+, RHEL, AlmaLinux,
Rocky and Oracle Linux 8+, openSUSE 15. Alpine and CentOS 7 cannot run it.

**Install from the site** (a monitoring site 1.4.0 or newer). On the site open **Install** (or **Machines** → **Add a
machine**), copy the command for Linux and run it on the server:

```sh
curl -fsSL 'https://backup.example.lan/install/linux/<token>' | sudo bash
```

(`wget -qO- '…' | sudo bash` where curl is missing.) The command downloads the program for the processor of the
server from the site (the package for Linux under **Updates**), checks its SHA-256, installs it into `/opt/custos`,
starts the service `custos` and connects it to the site, where the machine waits for approval under **Machines**. The
token in the address is made from the registration key: after a new key the old commands stop working. The site needs
the package `custos-linux-<version>.zip` under **Updates** (upload it, or download it there from GitHub). The server
must reach the site, and its address must be in the networks allowed for agents. The packages the program needs
(libicu, OpenSSL, cifs-utils) are installed when they are missing.

**Install from GitHub** (another way): `install.sh` downloads the archive of its processor from the newest release;
the connection line from the site connects it.

```sh
curl -fsSL https://raw.githubusercontent.com/openpass1/custos/main/client/linux/install.sh | sudo bash -s -- 'http://backup.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14'
```

**Snapshots instead of VSS.** «Read files from a snapshot» takes an LVM snapshot of the logical volume, a btrfs snapshot
of the subvolume or a ZFS snapshot for the run and deletes it afterwards. An LVM snapshot needs free room in the volume
group, and the usual Ubuntu install gives all of it to the volume. Then a task goes on without a snapshot and warns
(the default for Linux; it can be set to stop instead). The machine page on the site shows each volume of the tasks
(block **Snapshots**) with what to do, and a volume without room, or whose last snapshot ran full, opens a problem for
its tasks, sent by Telegram or e-mail like the others. The fix is a new disk of 10–20 GB in the group; nothing has to
be shrunk:

```sh
lsblk                                            # the new disk, for example /dev/sdb
sudo pvcreate /dev/sdb && sudo vgextend ubuntu-vg /dev/sdb
custos snapshots                                 # the volumes and their state
```

**What differs from Windows:**
- Paths are Linux paths (`/var/www`, `/etc/nginx`). A network folder (`//server/share` or `\\server\share`) is mounted
  with the stored account for the run (cifs-utils) and unmounted after it.
- «Owners and permissions» are copied instead of NTFS rights.
- Scripts before and after a run are run as root by `/bin/sh`; a service to stop is a systemd unit (`mariadb`).
- No cloud drives. 7-Zip comes inside the program (`7z.so`); the site does not update it.
- No window and no program password: the service is used with the commands of the terminal and from the site.

**Commands:** `custos status` (the service, the tasks, the connection), `custos run <task>`, `custos stop`,
`custos log -f`, `custos snapshots`, `custos connect '<line>'`, `custos disconnect`, `custos version`, `custos help`.

**Updates.** On the site, **Updates** has a section for CUSTOS for Linux: the package `custos-linux-<version>.zip`
(both processors in one) goes only to Linux machines. The service replaces the program itself and puts the old one back
if the new one does not start. Running the install command again also updates; settings, tasks and logs stay.

**Uninstall:** `sudo /opt/custos/uninstall.sh` removes the program and keeps the settings, the database and the logs;
`--purge` removes them too. The copies already made are never touched.

**Files on a Linux server:**

| Where | What |
|---|---|
| `/opt/custos` | The program (with its own .NET, `7z.so` and SQLite), `install.sh`, `uninstall.sh` |
| `/usr/local/bin/custos` | The command |
| `/etc/systemd/system/custos.service` | The service: `systemctl restart custos`, `journalctl -u custos` |
| `/var/lib/custos` | Settings, the database, downloaded updates |
| `/var/log/custos` | The logs |
| `/run/custos.sock` | The connection between the commands and the service |

**Build** (on Linux with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Docker, on an x86_64
machine for x64 and an ARM machine for arm64):

```sh
bash client/linux/build-linux.sh 1.0.1 x64          # client/artifacts-linux/custos-linux-x64.tar.gz
bash client/linux/build-linux.sh package 1.0.1      # custos-linux-1.0.1.zip from both archives
```

`7z.so` and SQLite are built in AlmaLinux 8 (Docker), so that glibc 2.28 is enough. GitHub Actions does the same for
every change under `client/` (workflow `Linux agent`): on an x86_64 and an ARM machine it runs the tests (as root too,
with LVM, btrfs and XFS on loop disks), installs the program with `install.sh`, tests the installed service and removes
it; then it starts the program on other distributions in containers and makes the update package.

## Archives

A task can pack each set into one 7z or ZIP archive (the "Compression and checks" page of the task). They are ordinary
archives: 7-Zip, Windows Explorer (ZIP without a password) and other archivers open them without CUSTOS. Encrypted
archives use AES-256 and ask for the task's archive password. Each archive also holds `custos-set.json`, the list of
the set's files with their SHA-256, which restore and the checks use. A split archive is `name.7z.001`, `.002`, ...;
open the first part.

## Databases (MySQL, MariaDB, PostgreSQL, OSPanel)

Task page «Databases» → «Add» → «Find…»: CUSTOS shows the running servers (with the address and port they listen on;
OSPanel 6 uses addresses like `127.127.126.x`) and the program folders it found. Choose a server, enter the user
(`root` for MySQL and MariaDB, `postgres` for PostgreSQL) and the password, or tick «No password», and press «Test
connection». The list of databases appears; the ticked ones go into the backup. «All databases»: everything is ticked
and new databases are added by themselves; unticking a database switches the choice to «Selected databases», where
only the ticked ones are dumped.

- Every run dumps the databases whole into the `db` folder of the backup: `db\mysql\<database>.sql` and
  `db\mysql\_users.sql` (users and grants). There is no need to copy the server's data folder as files.
- If the server is not running (for example, OSPanel is off) or a dump breaks off, the run ends with an error, but the
  files and the other databases are copied and old backups are not deleted.
- On restore the dumps are written as `.sql` files into the `db` folder together with `README.txt`, which gives the
  commands to load them into a server (run them in `cmd`, not in PowerShell). CUSTOS does not load dumps into a server
  itself. To restore to the original places, give a separate folder in «Database dumps to:».
- MS SQL and 1C are dumped by their own programs from the «Events» page (before copying) into a source folder.

## Linux servers (custos-helper over SSH)

Task page «Sources» → «Linux server…». Specification: [`docs/CUSTOS-remote-pull.md`](../docs/CUSTOS-remote-pull.md)
(in Russian); the script on the server and how to install it by hand: [`helper/README.md`](../helper/README.md).

1. The address of the server, the SSH port and the office IP (the external address of this computer: the key will
   work only from it; several addresses or networks with commas). Leave it empty for a dynamic IP: the key then works
   from any address, and the window warns about it. To change the address later, edit the field and run the block
   below again: the line of the key on the server is replaced.
2. What to take: «ispmanager: sites only» (the default), «ispmanager: sites and panel» or «Plain Linux»; the folders,
   exclusions and database servers can be adjusted.
3. «Create key»: the service makes an ed25519 key; the private part stays with it (DPAPI) and never leaves.
4. The block under the key is pasted whole into a root session on the server (after `sudo -i`); use «Copy» or «Save as
   file…». It creates the user `custos`, installs custos-helper (checking its SHA-256), its settings, sudo for
   custos-helper only, and the key. Running it again is safe; after changes in the window, run it again.
5. «Test connection»: CUSTOS shows the server's key; compare it on the server
   (`for f in /etc/ssh/ssh_host_*_key.pub; do ssh-keygen -lf "$f"; done`) and confirm. Then the version of
   custos-helper, the databases, the number of files and the size are shown.
6. Databases in the backup: the server's databases appear in the list after the test, as with OSPanel. «All databases»:
   everything is ticked and new databases go into the backup by themselves; an unticked database switches the choice
   to «Selected databases», where only the ticked ones are dumped. The choice is kept in the task; the block on the
   server need not be run again after it.

- A task with a Linux server has no other sources, no mirror, and the filters of the page do not apply.
- The set: `files.tar` (all files or the changed ones), `db\<server>\<database>.sql`, `_users.sql` and
  `custos-set.json`. The tar is never unpacked on Windows.
- A changed server key, an unreachable server (three tries a minute apart) or a broken dump end the run with an error.
- Restore goes to a folder only: a tar for each folder of the server (`var-www.tar` …), the dumps and `README.txt` with
  the commands for the server. Settings (`/etc` and `etc` folders) are unpacked on the server into `/root/old-etc`, not
  over the system.
- If the server sends data faster than the archive can compress it (more than 256 MB waiting in memory), the run stops:
  set a speed limit in the server window or lower the compression level.

## Backups on servers: SFTP, FTP, WebDAV, S3

A task's destination can be a server: «SFTP server», «FTP server», «FTP server with TLS (FTPS)», «WebDAV» (Nextcloud
too) and «S3 storage» (Amazon S3 and compatible ones: MinIO, cloud providers). Everything that works with a folder
works on a server: archives and backups without compression, mirror, retention, checks, restore.

- **Accounts** for servers are added where the accounts of network folders are: «Settings» → «Accounts for network
  folders, servers and cloud drives», or with «Add…» in the destination window. FTP and WebDAV: user and password.
  S3: access key and secret key. SFTP: a password, or a private key in OpenSSH or PEM format without a passphrase
  (convert a PuTTY `.ppk` key first: PuTTYgen → Conversions → Export OpenSSH key).
- **The server's key.** On the first «Check access» to an SFTP server the program shows the fingerprint of its key
  (`SHA256:…`). Compare it with what `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` shows on the server, and
  confirm. The same goes for an FTPS, WebDAV or S3 certificate that Windows does not trust (self-signed). If the key or
  the certificate changes later, the task stops before sending anything and says so in its log: confirm the new one in
  the destination window only if you know why it changed. A key cannot be confirmed from the monitoring site.
- **7z** is written only where the program can go back to the start of the file: a folder, a network folder, a USB
  disk, SFTP. For FTP, WebDAV and S3 choose ZIP or a backup without compression.
- **Speed limit** is set on the destination in KB/s (network folders and servers); 0 means no limit.
- **A mirror on a server** compares files by size and by the time from its own list of the last run: most servers do
  not keep a file's time as it was sent. A file that someone replaced on the server itself with another one of the same
  size goes unnoticed by the mirror.
- NTFS permissions and modification times are not kept on servers; on restore the time is taken from the set's list.

### S3: rights of the key and protection from deletion (Object Lock)

CUSTOS neither creates buckets nor changes their settings. Create the bucket yourself and give the CUSTOS key only
these rights (an AWS policy; replace `office-backup` with the name of your bucket):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:ListBucket",
        "s3:ListBucketVersions",
        "s3:GetBucketVersioning",
        "s3:GetBucketObjectLockConfiguration"
      ],
      "Resource": "arn:aws:s3:::office-backup"
    },
    {
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:GetObjectVersion",
        "s3:GetObjectRetention",
        "s3:PutObject",
        "s3:PutObjectRetention",
        "s3:AbortMultipartUpload",
        "s3:DeleteObject",
        "s3:DeleteObjectVersion"
      ],
      "Resource": "arn:aws:s3:::office-backup/*"
    }
  ]
}
```

What the policy **must not** have: `s3:BypassGovernanceRetention` (lifts the protection in Governance mode),
`s3:PutBucketObjectLockConfiguration`, `s3:PutBucketVersioning`, `s3:PutLifecycleConfiguration`, `s3:DeleteBucket`,
and `s3:*` at all. Then whoever gets the CUSTOS key (for example, ransomware on this computer) cannot delete the
protected backups before their time.

- **Object Lock is turned on when the bucket is created**; it cannot be done later (MinIO: `mc mb --with-lock`). In the
  destination window «Check access» says whether the bucket has it.
- «Protection from deletion», days: each file of a set cannot be deleted or replaced for this many days after it is
  written. **Governance** mode (the default): the owner of the storage with a special right can lift the protection,
  for example to free space after a mistake. **Compliance** mode: nobody lifts the protection before its time, not the
  owner and not the provider; the space for such backups is paid for until the end of the term.
- Protection works with archives and with «A separate folder for each backup». A mirror and a shared folder replace
  files, so they do not keep the protection.
- Retention skips a set whose protection has not run out yet (the log says «protected from deletion until …») and
  deletes it on the first run after that date, with all versions of its objects. It makes sense to set no more days of
  protection than the retention keeps backups.
- Whatever a failed run managed to write into a protected bucket stays there until the end of the term, like
  everything else.
- Add a lifecycle rule to the bucket that deletes incomplete multipart uploads older than a few days: after a lost
  connection or a power cut such parts stay behind and take up space.

## Cloud drives: Google Drive, Yandex Disk, Dropbox

A task's destination can be a cloud drive: «Google Drive», «Yandex Disk», «Dropbox». CUSTOS sees only its own folder
on the drive: on Google it is the folder «CUSTOS» that the program creates itself (the other files of the drive are
invisible to it), on Yandex and Dropbox it is the app folder («Apps/…»). Archives, grandfather-father-son retention,
checks and restore work as they do with a folder.

- **Connecting an account.** «Settings» → «Cloud drives» → «Connect a cloud drive…», or «Connect…» in the destination
  window. Administrator rights are needed: the account lets the service (SYSTEM) into somebody's cloud. The program
  opens the sign-in page of the service in the browser; after sign-in and consent the browser returns to the program
  by itself. If the browser is open on another computer or could not return, copy the code from the service's page
  (or the address from the browser's address bar) into «Code or address» and press «Connect». CUSTOS never sees or
  keeps the cloud password: only an access token is kept, encrypted for this computer.
- **An account is connected on each computer separately.** It cannot be sent from the monitoring site: in a task on
  the site one of the accounts already connected on that machine is chosen. A template has no account: when it is
  applied, the account of the same service connected on the machine is used (if there are several, set the
  destination in the machine's task first).
- **Disconnecting.** Revoke the access of the CUSTOS application in the settings of the service's account (Google:
  «Third-party apps & services», Yandex: «Logins and devices», Dropbox: «Connected apps»). The task says in its log
  that the access was revoked and writes the other destinations. «Connect again…» in the settings of CUSTOS gives the
  access back to the same account in the program: the tasks need no change.
- **ZIP or a backup without compression only.** 7z is not written to a cloud. A mirror to a cloud is not supported.
- **Parts of an archive.** «Part size» (MB) cuts the archive into files `.zip.001`, `.zip.002`…; 0 means one file. For
  Yandex Disk the default is 900 MB: the free plan takes files up to 1 GB. Restore joins the parts by itself.
- **A broken connection.** Google Drive and Dropbox resume the upload where it broke off. Yandex Disk cannot: a file
  whose upload broke off is not written, the destination counts as failed for this run (the other destinations of the
  task are written), and the next run starts the file again. So it is better to write to Yandex Disk in parts.
- **Restore on another computer.** Connect the same account there, then in the restore window «Backups on a server or
  a cloud drive» → «Choose…», and pick the service, the account and the destination folder.
- **Speed limit** is set as for servers, in KB/s.

### CUSTOS applications at the services

To sign in to a cloud, the service must have an application registered. CUSTOS releases and builds from GitHub
Actions carry the project owner's application for Google Drive only. For Yandex Disk and Dropbox register your own
application with the steps below: it is free and takes a few minutes. You can have your own application for Google
too, and you need one if you build CUSTOS yourself. Enter it in «Settings» → «Cloud drives» → «Own applications…»
(on this computer only; tasks and connected accounts do not change, and an account is connected again with
«Connect again…»).
The names of the items on the services' sites change over time; the idea stays the same.

**Google Drive** (https://console.cloud.google.com):

1. Create a project, for example «CUSTOS».
2. «APIs & Services» → «Library» → «Google Drive API» → «Enable».
3. «Google Auth Platform» (formerly «OAuth consent screen») → «Get started»: app name «CUSTOS», support email, type
   «External», contact email. Do not upload a logo: with one, Google requires a brand verification.
4. «Data Access» → «Add or remove scopes» → tick `.../auth/drive.file` («See, edit, create, and delete only the
   specific Google Drive files you use with this app») → «Update» → «Save». This is a non-sensitive scope: it needs no
   app verification by Google.
5. «Clients» → «Create client» → type «Desktop app», name «CUSTOS» → «Create». Save the Client ID and the Client
   secret.
6. «Audience» → «Publish app» → status «In production». **Required:** in the «Testing» status only test users can
   sign in, and tokens stop working after 7 days, so the backups stop.

**Yandex Disk** (https://oauth.yandex.ru):

1. «Create app», name «CUSTOS», platform «Web services».
2. Redirect URI: `http://localhost:47901/`, and tick «Add debug URL» (it adds
   `https://oauth.yandex.ru/verification_code`, the page that shows a code to enter by hand).
3. Access: «Yandex Disk REST API» → «Access to the app folder on Disk» (`cloud_api:disk.app_folder`) and «Access to
   information about Disk» (`cloud_api:disk.info`). Tick nothing else.
4. Save the ClientID and the Client secret.

**Dropbox** (https://www.dropbox.com/developers/apps):

1. «Create app» → «Scoped access» → «App folder» → the app name (unique in all of Dropbox, for example
   «CUSTOS Backup <your organisation>»).
2. Tab «Permissions»: tick `account_info.read`, `files.metadata.read`, `files.metadata.write`,
   `files.content.read`, `files.content.write` → «Submit».
3. Tab «Settings»: «App folder name» — `CUSTOS` (this is what the folder in «Apps» will be called); in «Redirect
   URIs» add `http://localhost:47901/`; leave «Allow public clients (Implicit Grant & PKCE)» at «Allow». Save the «App
   key»: it is the Client ID. CUSTOS needs no secret.
4. In the «Development» status up to 500 accounts can connect the app, but after the 50th connected account there are
   two weeks to get the «Production» status («Apply for production»); otherwise no new accounts can connect until it
   is given. Applications are accepted from 50 accounts; they need an app icon on the «Branding» tab and must follow
   the Dropbox developer rules. For your own app on a few computers this does not matter.

Port 47901 on the computer must be free while connecting (for Google the port does not matter). If it is busy, you
can still sign in: the code is entered by hand.

**For the project owner: applications in GitHub Actions builds.** The identifiers are not kept in the repository. In
the repository on GitHub: «Settings» → «Secrets and variables» → «Actions».

- Tab «Variables» → «New repository variable»: `CUSTOS_GDRIVE_CLIENT_ID`, `CUSTOS_YADISK_CLIENT_ID`,
  `CUSTOS_DROPBOX_CLIENT_ID`.
- Tab «Secrets» → «New repository secret»: `CUSTOS_GDRIVE_CLIENT_SECRET`, `CUSTOS_YADISK_CLIENT_SECRET`.

The next build puts them into the program. A service without these values is built without an application: it can
be connected only with your own application. For a desktop application the Google and Yandex secret is not a real
secret (it can be taken out of the program); the sign-in is protected by PKCE. It is kept in GitHub secrets so that it
does not end up in the public repository.

## Monitoring server

Connecting is optional: CUSTOS works fully without a server. Connecting needs the connection line. It is copied on the
site («Settings» → «Connecting agents») and holds the site address, the registration code and the fingerprint of the
server key: `http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14`. The line is pasted in the
installer (page «Monitoring server») or later: CUSTOS → «Settings» → «Monitoring server» → field «Connection line» →
«Connect» (administrator rights are needed). Next to the field there is a «Paste from the clipboard» button. There are
no separate fields for the address and the code. The fingerprint protects against a substituted server on the first
connection. After connecting, the settings show the server address; the line itself is not shown again.

After connecting, the computer appears on the site under «Machines» as «awaiting approval». Until it is approved, the
service sends only heartbeats; after approval it sends run reports, progress, a snapshot of its settings and the
inventory of backups (every 6 hours). If the server cannot be reached, the reports wait in a queue (`custos.db`, up to
10,000) and go out in order when the connection is back. The state of the connection is shown in the status bar of the
main window and in the settings.

- **«Allow the server to change tasks»** (in the settings, on by default): without it the server can start and stop
  tasks, ask for logs and install updates, but cannot change tasks and settings.
- **Update from the server:** the package `custos-<version>.zip` from a build is uploaded to the site («Updates») and
  installed on the chosen computers. The service checks the server's signature and the SHA-256, waits for the current
  task to end and starts `Custos.Updater.exe`: it stops the service, replaces the files (the previous ones are kept in
  `C:\ProgramData\CUSTOS\updates\backup-<version>`), starts the service and puts the previous version back if that
  fails. Log: `C:\ProgramData\CUSTOS\logs\updater.log`; the result goes to the Windows event log (4000 / 4001).
- **«Reset the binding to the server…»** in the settings forgets the server, its key, the code and the queue of
  reports. The agent's own key (`config\agent.key`) stays, so after a new connection the server recognises the
  machine: it comes back with its previous status, without a new approval. A machine deleted on the site registers
  anew and waits for approval.
- **A machine switched off on the site** works on its own and sends only heartbeats, at the interval the server sets
  (5 minutes), so after it is switched on again on the site the connection returns within a few minutes.

## New versions from GitHub (without a monitoring server)

A computer without a monitoring server learns about new versions by itself: once a day the service asks the
[releases on GitHub](https://github.com/openpass1/custos/releases) (the first time a few minutes after it starts). Only
published releases of the program (`client-vX.Y.Z`) count; drafts and pre-releases are skipped.

- When a new version is out, a tray notice «CUSTOS X.Y.Z is out» appears once (to administrators only), and the main
  window shows a line above the task list with an **«Update»** button and a **«What's new»** link (the release page).
- «Update» is for administrators only. The service downloads the package `custos-X.Y.Z.zip` of the same release,
  compares its SHA-256 with the one published on GitHub (the file's digest or `SHA256SUMS.txt`) and installs it the
  same way as an update from the site: it waits for the current task to end, starts `Custos.Updater.exe`, and puts the
  previous version back if the new one fails. A package that does not match the sum is deleted and not installed.
- **Settings → «Updates» → «Check GitHub for new versions once a day»** (on by default) and the «Check now» button. The
  same place shows when the last check was and what it found. With the box unticked the service never asks GitHub.
- **A computer connected to a monitoring server never asks GitHub**, even with the box ticked: the site installs its
  updates.

Each check is written to the service log (`C:\ProgramData\CUSTOS\logs\service-YYYYMMDD.log`, lines `GitHub: …`); the
state of the last check is kept in `C:\ProgramData\CUSTOS\config\program-update.json`. The result of the installation
goes to the Windows event log (4000 / 4001), as with an update from the site.

The request carries only the name of the program, nothing about the computer or its tasks; the installer says so
before installing. Everything the program sends over the network is listed in the [privacy policy](../PRIVACY.md).

## Moving tasks: export and import

Needed when you reinstall Windows or move the tasks to another computer. Administrators do it.

**Export.** Main window → the arrow next to «Create» → «Export tasks to a file…». Tick the tasks (all are ticked by
default) and press «Save…». The file `custos-tasks-<computer>-<date>.json` is plain JSON; it can be read and edited
in Notepad.

The accounts the tasks need go into the file with them. «Also the accounts of the settings that the chosen tasks do
not use» (on by default) adds the other accounts of the settings too: for example, a network folder or an SFTP server
that no task uses yet. With «Show password» the field for repeating the password is hidden: a visible password need
not be typed twice.

- **Without passwords** (the default). The file has the tasks and the names of the accounts, but no passwords, SSH
  keys or cloud drive tokens. Such a file can be kept anywhere.
- **With passwords.** The box «Put passwords and keys into the file, encrypted with an export password» and an export
  password (at least 8 characters). The file then gets the passwords of network folders, servers (FTP, SFTP, WebDAV,
  S3) and databases, the archive password, the SSH keys of Linux servers, the cloud drive tokens and the own cloud
  applications that issued those tokens. All of it is encrypted with the export password (PBKDF2-SHA256, 600,000
  iterations, AES-256-GCM). Without the export password nobody can get them out, CUSTOS included. Keep such a file off
  this computer: on a USB stick or in a network folder.
- Write down the archive password separately in any case: without it the backups already made cannot be opened.

**Import.** On the new computer install CUSTOS → «Create ▾» → «Import tasks from a file…» → «Browse…». If the file has
passwords, the window asks for the export password at once: enter the one set at export and press «Open». If you do
not remember it, press «Without passwords from the file»: then the passwords of the accounts and archives can be
entered at the bottom of the import window.

- Tasks keep their identifiers. So «Restore» sees at once the backups they made before the reinstall, and retention
  goes on deleting old ones by its rules. The first run after the import makes a full backup: the database of files
  stayed on the old system.
- A task that is already on the computer (the same identifier) is not ticked by default. If you tick it, the task from
  the file replaces it.
- An account that is already on the computer stays as it is.
- Accounts of the file that no task uses are offered with a separate box at the bottom («Also the accounts of the file
  that no task uses»). If the computer already has the same one (the same kind and user), there will not be a second.
- Passwords that the file lacks (or when the file is not opened) are asked at the bottom of the window, each once.
  These are the passwords of accounts and archives, not the export password. If the computer already has the same
  account (the same user), you can leave «use the stored password».
- A task arrives **switched off** when it lacks something: a password was left empty, or the file has no SSH key or
  cloud drive token. What to do is written in «What to check» of the task:
  - cloud drive: Settings → the list of accounts → «Connect again…»;
  - Linux server: «Edit» → «Sources» → the server → «Create new key», then add the new key line on the server (the
    install block in the same window);
  - then switch the task on in the editor.
- Variables such as `%USERPROFILE%` in paths are taken from the profile of the one who created the task. If that
  profile is not on the computer (after a reinstall, local Windows accounts are new), the profile of the one who
  imports is used.
- If the computer is connected to a monitoring server, imported tasks go to the site as an ordinary local edit.

## Moving from Cobian Backup

Main window → «Import from Cobian Backup…» → the file `MainList.lst` (usually
`C:\Program Files (x86)\Cobian Backup 11\DB`). Tasks are added switched off, so that they do not run alongside Cobian:
check each one in the editor, switch them off in Cobian and switch them on here. To switch all on at once, select them
in the list (Ctrl+A, or clicks with Ctrl and Shift) and press «Switch on» on the toolbar or in the right-click menu.
Several tasks can be switched off, started, stopped or deleted the same way.

- Cobian keeps the events before and after in its own format, so the import puts them as text into the task's
  comment. Set them again on the «Events» page.
- A pair of tasks on one folder, "incremental every day" + "full once a week", is better replaced with one incremental
  task: «Forced full backup» → «On the weekday» and «Grandfather-father-son» retention (for example, 7 days, 4 weeks,
  12 months). Then the backups do not get in each other's way, and old ones are deleted as whole chains.

## Replacing 7z.dll

CUSTOS works with 7z archives through `C:\Program Files\CUSTOS\7z.dll` from 64-bit 7-Zip. A new version of 7-Zip can
be put in by hand, without waiting for a new CUSTOS build:

1. Download and install 64-bit 7-Zip (x64) from [7-zip.org](https://www.7-zip.org) or
   [github.com/ip7z/7zip](https://github.com/ip7z/7zip/releases) on any computer.
2. On the computer with CUSTOS open a command prompt as administrator (in PowerShell type `cmd` first) and stop the
   service: `net stop CustosService`.
3. Keep the old file for a rollback:
   `copy /y "C:\Program Files\CUSTOS\7z.dll" "C:\Program Files\CUSTOS\7z.dll.old"`.
4. Copy the new `7z.dll` from the 7-Zip folder (usually `C:\Program Files\7-Zip\7z.dll`) into
   `C:\Program Files\CUSTOS\`, replacing the old one.
5. Start the service: `net start CustosService`.
6. Check the version: CUSTOS → «Settings», section «Archiver». It also says there if the file does not fit (for
   example, a 32-bit one). The version is in the service log `C:\ProgramData\CUSTOS\logs\service-YYYYMMDD.log` too.

Rollback: stop the service the same way, copy `7z.dll.old` back to `7z.dll`, start the service.

Easier, with the button: CUSTOS → «Settings» → «Archiver» → «Check for a 7-Zip update» (needs administrator rights and
internet). The service shows the installed and the latest version and, after you agree, downloads the 64-bit 7-Zip
installer from GitHub, compares its SHA-256 with the published one, checks the new `7z.dll` and replaces the old one
(which stays as `7z.dll.old`). While a task or a restore runs, the update does not start. The service never checks
7-Zip by itself: only with the button.

If `7z.dll` is missing or does not fit, the service writes so to the Windows «Application» event log at start (event
3002), and only tasks with the 7z format end with an error, with a clear reason in the run log. Tasks with ZIP and
without compression work as usual. The CUSTOS installer never replaces a newer `7z.dll` with an older one.

## Rights, program password and theme

CUSTOS → «Settings» → «Rights and protection» (only an administrator of this computer can change them):

- **«Allow all users to change tasks»**: off by default. The service runs as SYSTEM, so this in effect gives every
  user administrator rights; the program asks for confirmation. Restoring backups and changing the rights themselves
  stay with administrators.
- **«Interface for users»**: what users who are not administrators see: «Full», «State only» (the tray icon and a
  window with the task list, without buttons that change anything) or «Hidden» (no icon; the Start menu opens the state
  window). Administrators always see the full interface. The countdown window before a shutdown after a task appears
  in every mode.
- **«Program password»**: «Set…», «Change…», «Remove password». When a password is set, it must be entered to change
  tasks and settings, to restore, and to export and import tasks, even by an administrator. Once entered, it is not
  asked again for 30 minutes after the last change or until the main window is closed. The password can also be set
  and removed on the monitoring site (machine → «Agent settings»). Only its hash is kept (PBKDF2-SHA256). This
  protects against accidental edits and outsiders, not against a Windows administrator.

**If the password is forgotten.** The easiest way is to remove it on the monitoring site. Without a site, on the
computer itself, with administrator rights:

1. Open a command prompt as administrator (in PowerShell type `cmd` first) and stop the service:
   `net stop CustosService`.
2. Open the settings file in Notepad: `notepad C:\ProgramData\CUSTOS\config\settings.json`.
3. Find the line `"program_password_hash": "pbkdf2-sha256$…",` and replace the value with `null`:
   `"program_password_hash": null,`. Save the file.
4. Start the service: `net start CustosService`.

**Theme**: «Settings» → «Look» → «Theme»: «As in Windows» (the default, following the colour mode of Windows apps),
«Light» or «Dark». One for the whole computer; the open windows of CUSTOS are reopened in the new theme.

**Language.** The windows follow the Windows language of the user: Russian on a Russian Windows, English on any other.
The run logs and the messages of the service have their own setting, «Settings» → «Language of the logs» («Automatic»
follows the language of Windows).

## Files on the computer

| Where | What |
|---|---|
| `C:\ProgramData\CUSTOS\config\tasks.json`, `settings.json`, `credentials.json` | Tasks, settings, accounts (passwords encrypted with DPAPI). Only SYSTEM and Administrators may read the folder |
| `C:\ProgramData\CUSTOS\db\custos.db` | Run history (kept 365 days by default) and file states for incremental and differential backups |
| `C:\ProgramData\CUSTOS\logs\` | Service log; `journal\` holds the journal of every day (the lines of the «Log» tab), `runs\YYYY-MM\` one log file per run. Kept 180 days by default |
| `C:\ProgramData\CUSTOS\config\server.json`, `agent.key` | The monitoring server, its pinned key and the registration code (DPAPI); the agent's own RSA key (DPAPI) |
| `C:\ProgramData\CUSTOS\updates\` | The 7-Zip installer downloaded by the update button and the new `7z.dll` until the service restarts; update packages from the server or GitHub and the backup of the previous version. Only SYSTEM and Administrators may read the folder |
| `C:\ProgramData\CUSTOS\config\program-update.json` | The last check of GitHub for a new version of the program (computers without a monitoring server) |
| `%APPDATA%\CUSTOS\ui.json` | Window size, card, columns of this Windows user |
| `%APPDATA%\CUSTOS\last-known.json` | The theme and the tray mode the service gave last, so the interface starts in them before the service answers |
| `%APPDATA%\CUSTOS\gui-errors.log` | Unexpected errors of the interface, with details |
| `C:\Program Files\CUSTOS\7z.dll`, `7-Zip-License.txt` | 7-Zip library for 7z archives (x64, GNU LGPL) and its license |
| `C:\Program Files\CUSTOS\THIRD-PARTY-NOTICES.txt` | Licenses of the other libraries CUSTOS ships with (SSH.NET, BouncyCastle, SharpZipLib, Serilog, SQLite …) |

The interface starts with Windows only in the tray (`Custos.exe --tray`, set by the installer); started from the
Start menu or by the installer it also opens its window. A second start only brings the running copy to the front.
The tray menu also starts and stops the service: it runs `Custos.exe --service-start` (or `--service-stop`) with
administrator rights, after the Windows UAC prompt.

## Diagnostics

- Service log: `C:\ProgramData\CUSTOS\logs\service-YYYYMMDD.log`, kept as long as the other logs (Settings →
  «Keep logs, days», 180 by default).
- The «Log» tab shows the last 2000 lines of all tasks and filters them by level, by the selected tasks and by text; the
  same lines go to `logs\journal\YYYY-MM-DD.log`. The full log of a run (with every file at «Every file» detail) opens
  with a double click on the run in «Run history».
- Windows event log "Application", source `CUSTOS`: 1000 service started, 1001 service stopped,
  2000-2005 runs, 2006-2008 shutdown or sleep after a run (done, cancelled, failed), 3002 7z.dll missing or unusable,
  3003/3004 7-Zip updated / update failed, 1100/1101 monitoring server lost for 5 minutes / back, 4000/4001 update
  installed / failed (from the server or GitHub), 9000 unexpected error (details in the log file). Full list in
  section 6.10 of the specification.
- To watch the service live, stop it and start it in an administrator console:
  `sc.exe stop CustosService`, then `& "C:\Program Files\CUSTOS\CustosService.exe"` (Ctrl+C to stop), then
  `sc.exe start CustosService`.

## Interface ↔ service pipe

`\\.\pipe\custos`. Each message is a 4-byte little-endian length followed by UTF-8 JSON:
`{"kind":"request","id":1,"method":"hello","data":{...}}`, answered by `{"kind":"response","id":1,"data":{...}}` or
`{"kind":"response","id":1,"error":{"code":"...","message":"..."}}`; the service also sends
`{"kind":"event","event":"service.state","data":{...}}` at any time. Types are in `src/Custos.Core/Ipc`.

Any signed-in local user may connect; network clients are refused. The interface connects at the Identification
impersonation level, so the service can see who is calling (and whether they are an administrator) but cannot act
on their behalf. The service creates the pipe with `FirstPipeInstance`, so another program cannot occupy the name first.
