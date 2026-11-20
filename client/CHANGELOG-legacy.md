# Changelog: CUSTOS for Windows 7 and 8.1

One section per release, newest first. The section of a version is the text of its `client-legacy-v…` release on the
[Releases](https://github.com/openpass1/custos/releases) page, word for word.

How to write it:
- Groups in this order: **Added**, **Improved**, **Fixed**, **Removed**. Leave out a group with nothing in it.
- One short line per item (up to 140 characters): what the user gets. No explanations, no "before, it did …".
- New items go under the version at the top while it is not released; if the version changes before the release,
  rename that heading. Once a version is released, start a new section above it.
- The version at the top is the one to type when running the Release workflow (part `client-legacy`).

`.github/release-notes.sh` prints the text of a release and checks these rules; CI runs it on every change.

This edition is the program for Windows 10 and 11 built for old Windows; its changes usually repeat
[CHANGELOG.md](CHANGELOG.md), less what this edition does not have.

## 1.0.1

### Added
- First release of the edition for Windows 7 SP1, 8.1 and Server 2008 R2 – 2012 R2, 32 and 64-bit.
- The .NET 6 Desktop Runtime comes inside the installer.
- The same tasks as the standard edition, except cloud drives, the dark theme and the 7-Zip update from the site.
- On 64-bit Windows it reads the 64-bit system folders and registry, like the standard edition.
