# Changelog: CUSTOS for Windows

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

### Changed
- Task editor, **Schedule**: the days of a monthly schedule are picked in a drop-down calendar of 1–30 and **last day of the month**, on one line with the time, instead of a text field and a separate box. The 31st is gone: a task with it shows **last day of the month**, which it always meant. The day of a forced full backup on **Basic** offers the same days.

### Fixed
- A monthly task on the 29th, 30th or 31st no longer skips the months without that day: it runs on the last day of such a month (28 or 29 February, 30 April). The same goes for a full backup forced on a day of the month.

### Changed
- The manual install block of custos-helper (helper/README.md) asks for the public key and the office IP itself, with `any` for a dynamic IP, instead of carrying an example address to edit; its README tells how to change the address later.

### Fixed
- Running the server block of a Linux server task again after changing **Office IP** replaces the address of the agent's key on the server; before, the old address stayed allowed in a second line.

## 1.0.0

First public release.
