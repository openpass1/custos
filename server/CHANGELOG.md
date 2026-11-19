# Changelog: CUSTOS Monitor

Changes of the monitoring site, newest first. Each version is a `server-v…` release on the
[Releases](https://github.com/openpass1/custos/releases) page; its release text comes from this file.

Sections: **Added** (new features), **Changed** (existing behaviour), **Fixed** (bugs), **Removed**. Each item says
what a user sees, not how the code does it, and stays on one line: GitHub shows the line breaks of a release text
as they are. A change goes under the version at the top; if that version is already released, it goes under a new
heading for the next one. The version at the top is always `VERSION` in `src/App.php`.

The release text is made by `.github/release-notes.sh`: every version from the top down to the previous release, so
a version that was never released by itself is not lost.

## 1.2.0

### Added
- Machines with CUSTOS for Windows 7 and 8.1 (the legacy edition of the program) are marked **Windows 7, 8.1** in the machine list, on the machine page, under **Updates** and on the dashboard.
- Such a machine is offered only what its program can do: no cloud drives in its task editor (a task or template with one is refused for it), no 7-Zip update, and no update packages of the standard program.
- **Updates** take packages of the edition for Windows 7 and 8.1 (`custos-legacy-<version>.zip`) through the same upload field and keep them in a section of their own with their own current version; the section shows only while the site has such a machine or package. The site also finds `client-legacy-v…` releases on GitHub and downloads them like the standard program.

### Changed
- Backgrounds, cards, lines and grey text are neutral grey instead of slightly blue-violet, in the light and the dark theme; buttons, the selected menu item and other accents keep the blue.

## 1.1.6

### Added
- Search of machines and tasks in the top bar: matches show while you type, the arrows and **Enter** open one, **Enter** alone opens a page with every match, the **/** key jumps to the field.
- A light/dark switch in the top bar, next to the bell.
- Phones get a bottom navigation bar with the main sections and **More** for the rest.
- The task list of a machine shows a running task as **now** with a small progress circle and its percent.

### Changed
- New look in the style of Material Design 3: a navigation rail on the left (narrow on mid-size screens), rounded cards, Roboto font, Material Symbols icons, status badges with icons, filled and tonal buttons, fields with floating labels, press and hover effects, a smooth change from page to page. The old look is gone.
- Progress bars are 8 px thick, with a gap and an end dot, a moving sheen while a copy runs and smooth movement between updates.
- Animations follow the system setting to reduce motion.
- Task editor: the days of a monthly schedule are a small calendar of 1–30 and **last day of the month**. The 31st is gone: a task saved with it shows **last day of the month**, which it always meant.
- New installs by `install.sh` and the nginx example of the install guide let the browser keep styles, fonts and icons for 30 days (`location /assets/`). An existing install made by `install.sh` gets it by running `bash install.sh` again.

### Fixed
- A monthly schedule on the 29th or 30th no longer skips February: a month without the chosen day runs on its last day, as the program 1.0.1 does. Expected runs and missed-run alerts follow the same rule.

## 1.0.1

### Added
- **Tasks** page: all tasks of all machines in one list, errors and missed runs first, with quick filters (the parts of the dashboard tile, plus **Disabled**) and a search by task or machine. The **Machines** page links to it with **All tasks**.
- The run list shows how much its runs wrote in all.
- A quiet **Support the project** link in the page footer.

### Fixed
- Dashboard tiles: **Tasks** opened the machine list, the same as **Machines**. Now **Tasks** opens the task list, **Least free space** opens that machine at its storage with the destination outlined, and **Written in a day** opens the runs of the last 24 hours.

## 1.0.0

First public release.
