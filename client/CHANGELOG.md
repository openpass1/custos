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

### Changed
- The manual install block of custos-helper (helper/README.md) asks for the public key and the office IP itself, with `any` for a dynamic IP, instead of carrying an example address to edit; its README tells how to change the address later.

### Fixed
- Running the server block of a Linux server task again after changing **Office IP** replaces the address of the agent's key on the server; before, the old address stayed allowed in a second line.

## 1.0.0

First public release.
