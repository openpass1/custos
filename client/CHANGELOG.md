# Changelog: CUSTOS for Windows and macOS

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
