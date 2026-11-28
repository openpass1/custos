# CUSTOS Linux desktop preview

[По-русски](README.ru.md)

The desktop preview adds a graphical window to the CUSTOS Linux backup service. The service runs scheduled tasks
with the window closed. CUSTOS Monitor is optional for local backups.

## Download and install

Open **Assets** in a [GitHub release](https://github.com/openpass1/custos/releases) that includes Linux desktop and
choose the package for your distribution and processor. Both packages use systemd for the backup service and X11
for the window. Wayland sessions need XWayland.

| Distribution | 64-bit Intel or AMD | 64-bit ARM |
| --- | --- | --- |
| Debian 12/13, Ubuntu 22.04/24.04/26.04 and compatible Linux Mint | `custos-desktop_<version>_amd64.deb` | `custos-desktop_<version>_arm64.deb` |
| Fedora 44 | `custos-desktop-<version>-1.x86_64.rpm` | `custos-desktop-<version>-1.aarch64.rpm` |

For a test build, sign in to GitHub and open a successful
[Linux desktop preview run](https://github.com/openpass1/custos/actions/workflows/linux-desktop-preview.yml) for
the desired revision. Under **Artifacts**, download `custos-linux-desktop-preview-deb-amd64` or
`custos-linux-desktop-preview-deb-arm64` for Debian-based systems, or `custos-linux-desktop-preview-rpm-x86_64` or
`custos-linux-desktop-preview-rpm-aarch64` for Fedora. Extract the package from the ZIP.

A manual **Linux desktop preview** workflow run can include the project's Google Drive app when it is configured in
the repository. With a pull request build, set up your own app for each cloud drive you use.

Open the package in your graphical package installer and select **Install**. In a terminal, run the command for
your distribution from the download folder, using the actual filename.

```sh
sudo apt install ./filename.deb  # Debian, Ubuntu, Linux Mint
sudo dnf install ./filename.rpm  # Fedora
```

Open **CUSTOS** from the applications menu as your own user. On a fresh installation the package starts the
backup service. In a live session without persistence, the package and settings last until reboot.

A desktop panel with tray support can display the CUSTOS icon while the window is open.

## Update or remove the package

**Update from Monitor.** Starting with Linux Desktop 1.5.1, Monitor can update both the window and the service installed by
the `custos-desktop` package. On a machine running 1.5.0 or an earlier preview, install the first compatible DEB or RPM
locally. Later upgrades use the normal Monitor workflow.

1. In Monitor **Updates**, download the Linux update ZIP from a newer release that includes Linux Desktop. Mark that Linux version as current.
2. Select the machine in **Updates → Machines** and choose the CUSTOS update action. The machine needs HTTPS access to
   GitHub Releases to fetch its DEB or RPM. The selected Linux ZIP supplies the expected file size and SHA-256.
3. Check the command result and the reported agent version in Monitor. Reopen any running CUSTOS window to use the new GUI.

If the desktop window uses a separately installed agent in `/opt/custos`, the Monitor command updates that agent.
Use the desktop package installer to update the window on that machine.

Open **Settings → All settings… → Updates**. **Stable** checks stable releases. **Preview** also includes
pre-releases.

- For a `.deb` or `.rpm` installation, open **Desktop updates**, choose an **Update channel** and select **Check now**.
  **Release notes** opens the release page. **Download package** opens the matching package for your system and
  processor. Select **OK** to save the channel. Install the downloaded file with APT or DNF to update the window
  and its packaged service.
- For a separately installed service that checks for updates on its own, open **Agent updates**. Choose an
  **Update channel** and select **OK** before checking for its Linux update ZIP.
- If Monitor manages the separately installed service, update it from Monitor. The **Desktop updates** check for a
  `.deb` or `.rpm` installation still works separately.

`custos update channel` shows the service's current channel or which system manages its updates. For an agent that
updates itself, use `sudo custos update channel stable` or `sudo custos update channel preview` to choose a channel,
and `sudo custos update check` to look for an update. Use `sudo custos update install` when you're ready to install it.
If you set a CUSTOS program password, the terminal asks for it when you change the channel or install an update.

Use matching versions of the window and the agent for all desktop features. On Debian-based systems, the
`custos-desktop` package replaces the earlier `custos-desktop-preview` package and keeps its data. The first
install under the new name enables the service. To keep it stopped through this migration, first run
`sudo systemctl mask --now custos.service`.

Run `sudo apt remove custos-desktop` or `sudo dnf remove custos-desktop` to remove the package. Tasks, settings, run
history and existing backups are retained.

If you previously ran `add-menu-entry.sh` from a standalone archive, remove the launcher it created under
`${XDG_DATA_HOME:-$HOME/.local/share}/applications` so the menu opens the packaged copy.

## Create your first backup task

1. Open CUSTOS from a Linux administrator account in the `sudo` or `wheel` group and choose **Create**.
2. Enter a name. Add the folder containing your files under **Sources**. Under **Destinations**, choose a separate
   backup folder outside the source.
3. Choose **Save**, select the task in the main window and choose **Run**. Check the result under **Run history**.

New Linux tasks request a filesystem snapshot by default. If the volume cannot be snapshotted, as with some live
VMs, the agent continues with a warning. If your task requires a snapshot, enable the option to stop on failure under
**Advanced**.

## Manage tasks

Select a task to edit, clone, switch on or off, or delete it. A clone starts switched off. The menu next to
**Create** imports and exports CUSTOS task files. Export requires a Linux administrator account. If an imported
task has incompatible paths, CUSTOS keeps those paths for editing and switches the task off.

On a narrow screen, look under **More** for additional task actions. **Create** and **Settings** stay on the toolbar.

To back up a hosting server, choose **Linux server…** under **Sources**. Use **Own lists** to enter the approved
folders, exclusions and database engine. Follow the [SSH source guide](../../README.md#linux-servers-custos-helper-over-ssh)
to configure `custos-helper` on the server.

## Window and service settings

To change the window language, open **Settings → All settings… → Look → Interface language**. Choose a language
and select **OK** to apply it to the window and tray menu. This setting is available when the service is stopped.
Other settings in this dialog include permissions, log retention, the program password and the separate language
for service logs.

Under **Settings → CUSTOS service**, choose **Start the service**, **Stop the service** or **Restart the service**.
These actions control the standard `custos.service` systemd unit, even when the window is disconnected from it.
CUSTOS asks for confirmation before a stop or restart. If Linux asks for authorization, approve it in your
desktop's PolicyKit dialog. The same actions are available in the tray menu on desktops that display the icon.

## Connect to CUSTOS Monitor

In CUSTOS, go to **Settings → All settings… → Monitoring server**. Copy the connection string from **Install** in
Monitor. In the connection field, use **Paste from the clipboard**, then choose **Connect**. Monitor 1.4.2 shows
the connection string under **Windows and macOS**. Once the machine registers, compare its key fingerprint with
the one shown in Monitor and approve it under **Machines**.

If the agent is already connected, **Monitoring server** shows the current address and key fingerprints. Choose
**Change server…** to enter a new connection string. CUSTOS asks for confirmation before switching to a different
server. Switching servers discards reports still waiting to be sent to the old server.

## Restore files

Choose **Restore**, select a task, a folder with backups, or a server or cloud location, then choose **Find backups**.
Select a backup date and a destination folder, and choose **Restore** again. The default skips files that already
exist. Use the archive password when the backup is encrypted and the service has no saved password for it. Restore
requires a Linux administrator account.

## Connect a cloud drive

Use a Linux administrator account. In **Settings → All settings…**, find **Accounts for network folders, servers and
cloud drives**, choose Google Drive, Yandex Disk or Dropbox, and open the sign-in page in your browser. Google Drive
returns the result to CUSTOS. For Yandex Disk or Dropbox, paste the code shown in the browser into the CUSTOS dialog.
Then select the connected account in the task's **Destinations**.

If sign-in reports a missing app, select **Own applications…** in the dialog, save the app you registered for that
provider and continue signing in from the same window. Google Drive and Yandex Disk require a client ID and a
client secret. Dropbox requires a client ID. See the
[cloud drive guide](../../README.md#cloud-drives-google-drive-yandex-disk-dropbox) for supported formats and app registration.

## Standalone GUI archive

If the Linux agent is already installed, use the standalone `custos-linux-desktop-<version>-x64.tar.gz` or
`custos-linux-desktop-<version>-arm64.tar.gz` from a desktop release. To build and open the archive from the repository
root, use the .NET 10 SDK and GNU `tar`. Choose a version and use `arm64` in place of `x64` if needed.

```sh
version=1.5.0
bash client/linux/build-desktop.sh x64 client/artifacts-linux/desktop "$version"
tar -xzf "client/artifacts-linux/desktop/custos-linux-desktop-$version-x64.tar.gz"
"./custos-linux-desktop-$version-x64/custos-gui"
```

The bundle includes its .NET runtime and uses the host's glibc, ICU and graphical system libraries. Install the
[Linux backup agent](../../README.md#custos-for-linux) separately. Start the window as your own user and keep the
extracted files together. It connects to the agent at `/var/run/custos.sock` and uses the agent's access rules. If you
run a test agent on a different socket, set `CUSTOS_SOCKET` to its trusted absolute path before opening the window.

Run `custos-gui --demo` to open the window with sample data when testing without an agent.

For a launcher in your application menu, run `./add-menu-entry.sh` from the extracted folder. It registers
the extracted copy for your user. Run it again if you move the folder. To remove the entry, delete
`org.custos.Desktop.desktop` from `${XDG_DATA_HOME:-$HOME/.local/share}/applications`. Early test builds used
`org.custos.DesktopPreview.desktop` instead.

The build script takes the architecture, an output directory and a version. By default it uses `x64`,
`client/artifacts-linux/desktop` and the first version in the [client changelog](../../CHANGELOG.md).
The bundled PNG is the 256 px image from `client/src/Custos.Desktop/Assets/custos.ico`.

## Build the release packages

In GitHub, open **Actions → Release → Run workflow**, select `client` and enter the top version in the client
changelog. Selecting `linux` builds the headless archives and Monitor update ZIP. Leave `linux_desktop` selected
to include the `.deb`, `.rpm` and standalone GUI archive, or clear it for an agent-only Linux release. Review the
draft release and its `SHA256SUMS.txt` before publishing.

## Compatibility checks

| Environment | Check |
| --- | --- |
| Debian 13 amd64, Wayland (Weston 14.0.2 and XWayland 24.1.6) | Upgraded from the 1.4 preview, kept service data and enablement, opened 1.5.0 from the installed launcher and connected to the running service as a desktop user. |
| Ubuntu 24.04, x64 and ARM64 | The **Linux desktop preview** workflow exercises `.deb` installation, reinstallation, removal and coexistence with a preinstalled agent. |
| Fedora 44 container, x86_64 and aarch64 | The workflow checks RPM installation, the launcher and binaries, and package removal. |
