# Changelog: CUSTOS Monitor

Changes of the monitoring site, newest first. Each version is a `server-v…` release on the
[Releases](https://github.com/openpass1/custos/releases) page; its release text comes from this file.

Sections: **Added** (new features), **Changed** (existing behaviour), **Fixed** (bugs), **Removed**. Each item says
what a user sees, not how the code does it, and stays on one line: GitHub shows the line breaks of a release text
as they are. A change goes under the version at the top; if that version is already released, it goes under a new
heading for the next one. The version at the top is always `VERSION` in `src/App.php`.

The release text is made by `.github/release-notes.sh`: every version from the top down to the previous release, so
a version that was never released by itself is not lost.

## 1.0.1

### Added
- **Tasks** page: all tasks of all machines in one list, errors and missed runs first, with quick filters (the parts of the dashboard tile, plus **Disabled**) and a search by task or machine. The **Machines** page links to it with **All tasks**.
- The run list shows how much its runs wrote in all.
- A quiet **Support the project** link in the page footer.

### Fixed
- Dashboard tiles: **Tasks** opened the machine list, the same as **Machines**. Now **Tasks** opens the task list, **Least free space** opens that machine at its storage with the destination outlined, and **Written in a day** opens the runs of the last 24 hours.

## 1.0.0

First public release.
