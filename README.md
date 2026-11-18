# CUSTOS

Backup for Windows computers with central monitoring. A free replacement for Cobian Backup 11.

[Русская версия](README.ru.md)

[![Windows client](https://img.shields.io/github/v/release/openpass1/custos?filter=client-v*&label=Windows%20client)](https://github.com/openpass1/custos/releases)
[![Monitor](https://img.shields.io/github/v/release/openpass1/custos?filter=server-v*&label=Monitor)](https://github.com/openpass1/custos/releases)
[![Client build](https://github.com/openpass1/custos/actions/workflows/build.yml/badge.svg)](https://github.com/openpass1/custos/actions/workflows/build.yml)
[![Server](https://github.com/openpass1/custos/actions/workflows/server.yml/badge.svg)](https://github.com/openpass1/custos/actions/workflows/server.yml)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

CUSTOS has two parts. The Windows program makes the backups and works on its own. The monitoring site is optional: it
shows every computer in one place, warns when something goes wrong and lets you manage the tasks remotely.

| | CUSTOS for Windows | CUSTOS Monitor |
|---|---|---|
| What it is | Backup program: a Windows service and a tray app | Web site: dashboard, alerts, remote management |
| Runs on | Windows 10 and 11, Windows Server 2019, 2022 and 2025 (x64) | Linux with nginx, PHP 8.3+, MariaDB 10.11+ or MySQL 8.0+ |
| Download | [Releases](https://github.com/openpass1/custos/releases), «CUSTOS for Windows»: `CUSTOS-Setup-<version>.exe` | [Releases](https://github.com/openpass1/custos/releases), «CUSTOS Monitor»: `custos-server-<version>.zip` |
| Guide | [client/README.md](client/README.md) | [server/INSTALL.en.md](server/INSTALL.en.md) |

<img src="docs/screenshots/ru/client-main-window.png" alt="CUSTOS for Windows: the main window with the task list and the card of the selected task (Russian interface)">

<img src="docs/screenshots/en/site-dashboard.png" alt="CUSTOS Monitor: dashboard with seven computers, an open problem, two copies in progress and the results of the last 30 days">

## CUSTOS for Windows

- Full, incremental, differential and mirror backups (exact, or safe with deleted files kept for a while). Schedules
  by day, week, month or timer; a run missed while the computer was off is made after it starts.
- Writes straight to the destination, without a temporary copy on the local disk. Destinations: a local folder, a
  network share with its own account, a USB disk found by its volume label, SFTP, FTP and FTPS, WebDAV, S3 (with
  Object Lock against deletion), Google Drive, Yandex Disk and Dropbox. One task can write to several destinations at
  once.
- Google Drive works in the released program as it is. For Yandex Disk and Dropbox, register a free application of
  your own with the service and enter it under Settings → Cloud drives → Own applications…
  ([steps](client/README.md#custos-applications-at-the-services)).
- 7z or ZIP archives with AES-256 encryption, 7z volumes, verification of new and stored copies.
- Retention by count, by age or grandfather-father-son (GFS). A set is deleted only as a whole, never after a failed
  run, and the last good full copy is always kept.
- Open files through VSS, NTFS permissions, low priority so that the computer stays usable during a backup.
- Database dumps: MySQL, MariaDB and PostgreSQL, OSPanel included.
- Linux servers: files and database dumps are pulled over SSH through the read-only script
  [custos-helper](helper/README.md).
- Restore to a date, moving tasks to another computer (export to a file and import, passwords encrypted if wanted),
  import of tasks from Cobian Backup 11.
- A program password and a limited interface for users who are not administrators (state only, or hidden); actions on
  several tasks at once.
- Russian and English, light and dark theme.

## CUSTOS Monitor

- Notices not only failed backups but also the ones that never happened: a disabled task, a hung service, a computer
  that was switched off.
- Dashboard with live progress, a 30-day heat map, run history with logs, free space of the storages with a forecast.
- Alerts in Telegram and by e-mail, a Zabbix template, a wall screen for a TV.
- Remote control: run and stop tasks, edit them, apply templates to groups of computers, update the program and 7-Zip.
- A computer is approved on the site by its key fingerprint; every request and answer is signed.
- Search of machines and tasks; Russian and English, light and dark theme, works on a phone.

## Quick start

1. **The site (optional).** On a clean Debian 12 or 13, Ubuntu 24.04 or 26.04 server, as root (on Ubuntu put `sudo` before `bash`):
   ```bash
   wget https://raw.githubusercontent.com/openpass1/custos/main/server/install.sh
   bash install.sh
   ```
   The script installs nginx, MariaDB and PHP, sets up the site and cron and prints the connection string at the
   end. `bash install.sh --help` lists its options; `bash install.sh --update` updates the site later. On CloudPanel
   follow [server/INSTALL.en.md](server/INSTALL.en.md).
2. **The program.** Run `CUSTOS-Setup-<version>.exe` on each Windows computer.
3. **Connect a computer to the site.** On the site: Settings → Connecting agents → copy the connection string. In the
   program: Settings → Monitoring server → paste it and press Connect. Then approve the computer on the site
   (Machines).

## Documentation

| Document | What is in it |
|---|---|
| [client/README.md](client/README.md) | Building the client; databases, Linux servers, remote destinations, cloud drives, updates, moving tasks and from Cobian Backup, files on the computer |
| [server/INSTALL.en.md](server/INSTALL.en.md), [server/INSTALL.md](server/INSTALL.md) | Installing and updating the site on CloudPanel or plain nginx, Telegram, e-mail, Zabbix |
| [server/README.md](server/README.md) | How the site is built, tests, the fake agent |
| [helper/README.md](helper/README.md) | custos-helper on Linux servers (in Russian) |
| [docs/](docs/) | Specifications (in Russian) and the [protocol](docs/protocol.md) between the program and the site |

## Checking a download

Every release has `SHA256SUMS.txt`. On Windows, compare it with the output of
`certutil -hashfile CUSTOS-Setup-<version>.exe SHA256`; on Linux run `sha256sum -c SHA256SUMS.txt` in the folder with
the files. The Windows files are not code-signed yet: the project has applied for free code signing by SignPath
Foundation, see the [code signing policy](CODE_SIGNING.md).

## Code signing policy and privacy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/) (applied for, releases up to 1.0.0 are not signed). What is signed, how
and by whom: [CODE_SIGNING.md](CODE_SIGNING.md).

CUSTOS has no telemetry and sends nothing to its authors. The only connection that you do not set up yourself is a
daily question to GitHub about a new version, and it can be turned off: [PRIVACY.md](PRIVACY.md).

## Feedback and security

Bugs and ideas: [Issues](https://github.com/openpass1/custos/issues), in English or Russian. Please report security
problems privately, as described in [SECURITY.md](SECURITY.md).

## Support the project

CUSTOS is free. If it is useful to you, you can support its development with a donation in cryptocurrency:
the wallets, their networks and QR codes are in [DONATE.md](DONATE.md).

## License

CUSTOS is free software under the [GNU General Public License v3.0](LICENSE). It ships with third-party components
under their own licenses: [client/THIRD-PARTY-NOTICES.txt](client/THIRD-PARTY-NOTICES.txt),
[server/THIRD-PARTY-NOTICES.txt](server/THIRD-PARTY-NOTICES.txt). 7-Zip (`7z.dll`) is licensed under the GNU LGPL.

CUSTOS comes **without any warranty**, to the extent permitted by law: it is provided "as is", and its authors are not
liable for any damage from using it, including lost data (sections 15 and 16 of the license). Check your backups by
restoring them now and then.
