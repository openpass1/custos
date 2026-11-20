# Changelog: CUSTOS for Windows, macOS and Linux

Changes of the program (and of custos-helper, which is released with it), newest first. Each version is a
`client-v…` release on the [Releases](https://github.com/openpass1/custos/releases) page; its release text comes
from this file.

Sections: **Added** (new features), **Changed** (existing behaviour), **Fixed** (bugs), **Removed**. Each item says
what a user sees, not how the code does it, and stays on one line: GitHub shows the line breaks of a release text
as they are. A change goes under the version at the top; if that version is already released, it goes under a new
heading for the next one. The version at the top is the one to type when running the Release workflow.

The release text is made by `.github/release-notes.sh`: every version from the top down to the previous release, so
a version that was never released by itself is not lost.

## 1.0.1

### Added
- The installer shows a short privacy note before installing: what the program sends over the network and how to turn it off.
- A quiet **Support the project** link in the status bar of the main window.
- An edition for Windows 7 SP1 and 8.1 (32 and 64-bit, Windows Server 2008 R2 SP1 and 2012 / 2012 R2 too): `CUSTOS-Legacy-Setup-<version>.exe`, a 32-bit program with the .NET 6 Desktop Runtime inside its setup, released as is as `client-legacy-v…`. It has no cloud drives, no dark theme and no 7-Zip update from the site; with a monitoring site 1.2.0 or newer, the site hides those for such a machine and gives it only update packages of its own (`custos-legacy-<version>.zip`).
- On 64-bit Windows the edition for Windows 7 and 8.1 reads System32 and the 64-bit registry, takes `%ProgramFiles%` as the 64-bit folder, runs task scripts in the 64-bit cmd and PowerShell, and makes shadow copies through the 64-bit provider, so tasks behave as in the standard edition.
- CUSTOS for macOS (Macs with Apple silicon or Intel, macOS 15 or newer): one `Custos-<version>.pkg` for both kinds of Mac installs the program, its service (started by the system, as on Windows) and an icon in the menu bar. The same tasks as on Windows: folders and files, mirror, zip and 7z archives with encryption, schedules, network folders (SMB), USB disks, SFTP, FTP, WebDAV, S3 with Object Lock, retention, WORM, restore, run logs, the monitoring site (1.3.0 or newer also updates Macs), updates from GitHub (`custos-macos-<version>.zip`). Files are read from an APFS snapshot instead of a shadow copy; passwords are kept in the System keychain. The window of the Mac does not set up database servers, Linux servers and cloud drives yet: tasks imported from Windows or made on the monitoring site keep them. The program is not signed by Apple yet: the first time the installer is allowed in System Settings → Privacy & Security → **Open Anyway**, and the program needs Full Disk Access (System Settings → Privacy & Security).
- The window of CUSTOS for macOS has five colours in Settings → **Look** → **Colour**: neutral grey (the default), graphite, blue, warm and sepia; each user of the Mac keeps their own.
- The window of CUSTOS for macOS looks like the new window of Windows: compact rows and buttons, hints behind (?) marks, a **Settings** menu with the theme and the colour, a right-click menu of a task, and a forced full backup on a weekday or a day of the month in the task editor.
- CUSTOS for Linux servers (x86_64 and ARM; Ubuntu 20.04+, Debian 10+, RHEL, AlmaLinux, Rocky and Oracle Linux 8+, openSUSE 15+): one command, `install.sh`, installs the agent as the systemd service `custos` with the command `custos` (`status`, `run`, `stop`, `log`, `snapshots`, `connect`). It has no window: tasks are made on the monitoring site. Files are read from a snapshot of the volume (LVM, btrfs or ZFS); when a volume group has no free room for an LVM snapshot, `custos snapshots` and the site say so and how to fix it (add a disk to the group). Network folders (SMB), MySQL, MariaDB and PostgreSQL, updates from the site or GitHub (`custos-linux-<version>.zip`, the program for both processors).

### Changed
- Task editor, **Schedule**: the days of a monthly schedule are picked in a drop-down calendar of 1–30 and **last day of the month**, on one line with the time, instead of a text field and a separate box. The 31st is gone: a task with it shows **last day of the month**, which it always meant. The day of a forced full backup on **Basic** offers the same days.
- The manual install block of custos-helper (helper/README.md) asks for the public key and the office IP itself, with `any` for a dynamic IP, instead of carrying an example address to edit; its README tells how to change the address later.

### Fixed
- The service no longer logs «CUSTOS service terminated unexpectedly» and ends with an error code each time it is stopped normally.
- A monthly task on the 29th, 30th or 31st no longer skips the months without that day: it runs on the last day of such a month (28 or 29 February, 30 April). The same goes for a full backup forced on a day of the month.
- Running the server block of a Linux server task again after changing **Office IP** replaces the address of the agent's key on the server; before, the old address stayed allowed in a second line.

## 1.0.0

First public release.
