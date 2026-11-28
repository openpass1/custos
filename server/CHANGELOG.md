# Changelog: CUSTOS Monitor

One section per release, newest first. The section of a version is the text of its `server-v…` release on the
[Releases](https://github.com/openpass1/custos/releases) page, word for word.

How to write it:
- Groups in this order: **Added**, **Improved**, **Fixed**, **Removed**. Leave out a group with nothing in it.
- One short line per item (up to 140 characters): what the user gets. No explanations, no "before, it did …".
- New items go under the version at the top while it is not released; if the version changes before the release,
  rename that heading. Once a version is released, start a new section above it.
- The version at the top is always `VERSION` in `src/App.php`.

`.github/release-notes.sh` prints the text of a release and checks these rules; CI runs it on every change.

## 1.4.5

### Improved
- View update packages by platform, see which versions are assigned for deployment and upload CUSTOS or 7-Zip from one place.
- Update a mixed selection of CUSTOS clients with one button. Ticking a machine switches the target from all outdated to selected machines.
- Group problem reports with the affected machine, task, dates and notification status on desktop and mobile.

### Fixed
- Give update buttons consistent spacing and keep long action labels visible on narrow screens.
- Vertically align task details, status badges and actions in task and machine lists.

## 1.4.3

### Added
- Choose Stable or Preview for CUSTOS Monitor and client releases discovered on GitHub.

## 1.4.2

### Added
- Linux agents that support cloud drives can use Google Drive, Yandex Disk and Dropbox as task destinations.
- Find a Linux task's backup sets, restore files to the agent, request status and stop the restore from the site.

### Fixed
- Improve text and button contrast for pending machines in the Graphite and Blue colour schemes.

## 1.4.1

### Fixed
- The Linux package can be downloaded from GitHub under Updates before adding the first Linux machine.
- The Linux install command downloads the archive on CloudPanel and other nginx setups that serve .gz URLs as static files.

## 1.4.0

### Added
- **Install** page and **Add a machine** button: how to connect a new Windows, Mac or Linux machine.
- Linux servers install CUSTOS with one command copied from **Install**: it downloads the program from this site and connects it.
- Machines with CUSTOS for macOS or Linux are marked **macOS** or **Linux** across the site.
- **Updates** take packages of CUSTOS for macOS and for Linux, uploaded or found on GitHub, each in a section of its own.
- A Mac or a Linux server is offered only its own update packages and never 7-Zip.
- The task editor of a Mac or a Linux server shows its own paths and its own snapshot: APFS, or LVM, btrfs and ZFS.
- **Snapshots** block on the page of a Linux machine, with the command to fix a volume group without room.
- A problem and a notification when a Linux volume has no room for a snapshot; it closes once snapshots work again.

### Improved
- A network folder may also be written as `//server/share`, as macOS writes it.
- The settings of a Linux machine leave out the program password, the tray icon and other users, which it does not have.

## 1.2.1

### Improved
- Pages open at once, without the dissolve between them.
- The press effect on buttons is quick again, with Material 3 timing.

### Fixed
- The logo is visible in the **Blue** colour scheme.
- Clicks are no longer lost when the mouse moves slightly during a press.
- Clicks are no longer lost on blocks that refresh themselves.
- Quick clicks in Chrome on Windows soon after a page opens are no longer lost.

## 1.2.0

### Added
- Machines with CUSTOS for Windows 7 and 8.1 are marked **Windows 7, 8.1** across the site.
- Such machines are offered only what their edition supports: no cloud drives and no 7-Zip update.
- **Updates** take packages of the Windows 7 and 8.1 edition, uploaded or found on GitHub.
- **Colour** in the user menu: Neutral grey, Graphite, Blue, Warm or Sepia, each light and dark.

### Improved
- Neutral grey backgrounds and cards instead of slightly blue-violet ones.
- Smoother press effect and page changes.

## 1.1.1

### Added
- Search of machines and tasks in the top bar; the **/** key jumps to it.
- Light/dark switch in the top bar.
- Bottom navigation bar on phones.
- A running task shows its progress in the task list of its machine.

### Improved
- New Material Design 3 look: navigation rail, rounded cards, new icons, buttons and fields.
- Thicker progress bars that move smoothly.
- Animations follow the system setting to reduce motion.
- Smooth moves between sections; the tabs of **Settings** stay in place.
- Monthly schedule: days are picked in a calendar of 1–30 and **last day of the month**.
- `install.sh` and the nginx example let browsers cache styles, fonts and icons for 30 days.

### Fixed
- A monthly schedule on the 29th or 30th no longer skips February.
- Letters no longer change their shape a moment after a page opens.

## 1.0.1

### Added
- **Tasks** page: all tasks of all machines in one list, with filters and search.
- The run list shows the total amount written.
- **Support the project** link in the page footer.

### Fixed
- The dashboard tiles **Tasks**, **Least free space** and **Written in a day** open their own pages.

## 1.0.0

### Added
- First public release.
