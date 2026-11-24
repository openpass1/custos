# Changelog: CUSTOS for Windows, macOS and Linux

One section per release, newest first. The section of a version is the text of its `client-v…` release on the
[Releases](https://github.com/openpass1/custos/releases) page, word for word.

How to write it:
- Groups in this order: **Added**, **Improved**, **Fixed**, **Removed**. Leave out a group with nothing in it.
- One short line per item (up to 140 characters): what the user gets. No explanations, no "before, it did …".
- New items go under the version at the top while it is not released; if the version changes before the release,
  rename that heading. Once a version is released, start a new section above it.
- The version at the top is the one to type when running the Release workflow.
- One version for every system. An item for one system only starts with its name (`Windows:`, `macOS:`, `Linux:`);
  a release carries the files of the systems that changed, so the others skip it.

`.github/release-notes.sh` prints the text of a release and checks these rules; CI runs it on every change.

The edition for Windows 7 and 8.1 has its own file, [CHANGELOG-legacy.md](CHANGELOG-legacy.md).

## 1.4.0

### Added
- Linux: List and restore backup sets from the terminal, with progress, status and cancellation.
- Linux: Sign in to Google Drive, Yandex Disk and Dropbox from the terminal for use as backup destinations.
- Linux: Restore files from a task's backup set through CUSTOS Monitor 1.4.2 into a private directory on the agent.

## 1.3.0

### Added
- Linux: CUSTOS for Linux servers (x86_64 and ARM), a service without a window that a monitoring site manages.
- Linux: Ubuntu 20.04+, Debian 10+, RHEL, AlmaLinux, Rocky and Oracle Linux 8+, openSUSE 15+.
- Linux: one command from the **Install** page of a monitoring site 1.4.0 or newer installs and connects it.
- Linux: files are read from an LVM, btrfs or ZFS snapshot; the site says which disk to add when there is no room for one.

### Fixed
- Windows: the service no longer logs «CUSTOS service terminated unexpectedly» each time it is stopped normally.

## 1.2.0

### Added
- macOS: CUSTOS for Macs with Apple silicon or Intel, macOS 15 or newer: one installer with the service, the window and a menu bar icon.
- macOS: the same tasks as on Windows; files are read from an APFS snapshot, passwords are kept in the System keychain.
- macOS: the window of the new Windows look, with the theme and the five colour schemes in the **Settings** menu.
- macOS: updates from GitHub releases or from the monitoring site 1.3.0 or newer.

### Fixed
- Windows: the service no longer logs «CUSTOS service terminated unexpectedly» each time it is stopped normally.

## 1.1.0

### Added
- New program window in the Material 3 style, with light and dark themes and five colour schemes.
- Privacy note in the installer: what the program sends over the network and how to turn it off.
- **Support the project** link in the main window.
- A separate edition for Windows 7 SP1 and 8.1 is released as `client-legacy-v…`.

### Improved
- Monthly schedule: days are picked in a calendar of 1–30 and **last day of the month**.
- The manual install of custos-helper asks for the public key and the office IP itself.

### Fixed
- A monthly task on the 29th, 30th or 31st no longer skips shorter months.
- Changing **Office IP** of a Linux server task replaces the old address on the server.

## 1.0.0

### Added
- First public release.
