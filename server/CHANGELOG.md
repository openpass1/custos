# Changelog: CUSTOS Monitor

Changes of the monitoring site, newest first. Each version is a `server-v…` release on the
[Releases](https://github.com/openpass1/custos/releases) page; its release text comes from this file.

Sections: **Added** (new features), **Changed** (existing behaviour), **Fixed** (bugs), **Removed**. Each item says
what a user sees, not how the code does it, and stays on one line: GitHub shows the line breaks of a release text
as they are. A change goes under the version at the top; if that version is already released, it goes under a new
heading for the next one. The version at the top is always `VERSION` in `src/App.php`.

The release text is made by `.github/release-notes.sh`: every version from the top down to the previous release, so
a version that was never released by itself is not lost.

## 1.1.4

### Changed
- Moving between sections is smooth again, as before Material: the old page dissolves into the new one in 0.3 s, the menu and the content together, instead of the content being drawn in after the new page came. A click lights up the mark of the section at once. In Firefox the content still fades out and in.

## 1.1.3

### Changed
- The menu and the tabs of a page work as before Material: no circle on a press, the press deepens the colour and the mark moves smoothly to the section pressed.
- The press circle of a button or link that opens a page (for example **Show** of a filter or a saved form) is no longer cut off: it goes on, from where it was, on the page that opens when that page has the same button or link.
- The press circle of buttons, chips and tiles is twice as slow: it grows for about a second and fades slowly after the button is let go.

## 1.1.2

### Changed
- The press effect follows Material 3 timing: the circle has a soft edge, grows for about half a second and fades slowly after the button is let go. A press on a section of the menu or on a tab goes on, from where it was, on the page that opens, instead of being cut off half-way.

## 1.1.1

### Changed
- Moving between sections is smooth: a click in the menu marks the section at once, the old content fades out and the new one comes in. The old and the new page no longer show through each other for a moment.
- Between the tabs of **Settings** the heading and the tabs stand still, only the content changes. A filter, the next page of a list or a saved form shows at once, without any movement.

### Fixed
- The letters of a page no longer change their shape a moment after it opens: the fonts come together with the page.
- New installs by `install.sh` and the nginx example of the install guide let the browser keep styles, fonts and icons for 30 days (`location /assets/`). An existing install made by `install.sh` gets it by running `bash install.sh` again.

## 1.1.0

### Added
- Search of machines and tasks in the top bar: matches show while you type, the arrows and **Enter** open one, **Enter** alone opens a page with every match, the **/** key jumps to the field.
- A light/dark switch in the top bar, next to the bell.
- Phones get a bottom navigation bar with the main sections and **More** for the rest.
- The task list of a machine shows a running task as **now** with a small progress circle and its percent.

### Changed
- New look in the style of Material Design 3: a navigation rail on the left (narrow on mid-size screens), rounded cards, Roboto font, Material Symbols icons, status badges with icons, filled and tonal buttons, fields with floating labels, ripple and hover effects. The old look is gone.
- Progress bars are 8 px thick, with a gap and an end dot, a moving sheen while a copy runs and smooth movement between updates.
- Animations follow the system setting to reduce motion.

## 1.0.2

### Changed
- Task editor: the days of a monthly schedule are a small calendar of 1–30 and **last day of the month**. The 31st is gone: a task saved with it shows **last day of the month**, which it always meant.

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
