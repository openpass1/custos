using System.Diagnostics;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Service.Platform;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Agent;

/// <summary>
/// What the service of Linux does differently: updates (A.10) and the state of the snapshots for <c>custos status</c>
/// and the site.
/// </summary>
/// <remarks>
/// <para>Updates: the package of the site or of GitHub (<c>custos-linux-X.Y.Z.zip</c>) holds the program for both
/// processors, <c>custos-linux-x64.tar.gz</c> and <c>custos-linux-arm64.tar.gz</c>. A small script unpacks the one of
/// this computer next to /opt/custos, stops the service, swaps the folders, puts the unit of the new version in place
/// and starts the service; if the new one does not stay up it puts the old one back. The script runs as a unit of its
/// own (systemd-run): stopping the service stops everything in its control group.</para>
/// <para>Snapshots: <see cref="SnapshotCheck"/> runs when the tasks change, after each run, with each full inventory and
/// at most every ten minutes otherwise; it starts lvs and btrfs, so the state of the service keeps the last result.</para>
/// </remarks>
internal sealed partial class AgentService
{
    private const string UpdateScriptFile = "update.sh";

    private static readonly TimeSpan ReadinessAge = TimeSpan.FromMinutes(10);

    /// <summary>The archive of the program for this processor; without it a package is no CUSTOS for Linux here.</summary>
    private static readonly string[] RequiredFiles = [LinuxPaths.ProgramArchive];

    private readonly Lock _readinessLock = new();
    private List<SnapshotReadiness> _readiness = [];
    private long _readinessRevision = -1;
    private DateTimeOffset _readinessAt;
    private int _readinessRunning;

    /// <summary>
    /// Unpacks the archive next to the program folder, swaps them with the service stopped and starts it; the result goes
    /// in the fields of UpdaterResult. Arguments: archive, result file, from, to, unit, program folder, log file, and the
    /// folder of the units of systemd (/etc/systemd/system when left out; the tests give one of their own).
    /// </summary>
    internal const string UpdateScript = """
        #!/bin/sh
        # Written by the CUSTOS service: installs an update of CUSTOS for Linux and leaves the result for the service.
        archive="$1"; result="$2"; from="$3"; to="$4"; unit="$5"; program="$6"; log="$7"; units="${8:-/etc/systemd/system}"
        new="$program.new"; old="$program.old"
        say() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >>"$log"; }
        finish() {
          printf '{"ok":%s,"from_version":"%s","to_version":"%s","message":%s,"rolled_back":%s}\n' "$1" "$from" "$to" "$2" "$3" >"$result"
          say "result: ok=$1 rolled_back=$3"
        }
        sleep 2
        say "installing CUSTOS $to over $from"
        rm -rf "$new" "$old"
        if ! mkdir -p "$new" || ! tar -xzf "$archive" -C "$new" >>"$log" 2>&1 || [ ! -x "$new/custos" ]; then
          rm -rf "$new"
          finish false '"the package could not be unpacked"' false
          exit 0
        fi
        systemctl stop "$unit" >>"$log" 2>&1
        if ! mv "$program" "$old" || ! mv "$new" "$program"; then
          [ -d "$program" ] || mv "$old" "$program"
          finish false '"the program folder could not be replaced"' false
          systemctl start "$unit" >>"$log" 2>&1
          exit 0
        fi
        if command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled; then
          # As install.sh: the new program must be one systemd may start.
          restorecon -RF "$program" >>"$log" 2>&1
          chcon -t bin_t "$program/custos" >>"$log" 2>&1
        fi
        if [ -f "$program/custos.service" ]; then
          cp "$program/custos.service" "$units/$unit.service" && systemctl daemon-reload >>"$log" 2>&1
        fi
        finish true null false
        systemctl start "$unit" >>"$log" 2>&1
        sleep 15
        if systemctl is-active --quiet "$unit"; then
          rm -rf "$old"
          say "the new service is running"
          exit 0
        fi
        say "the new service did not stay up: putting $from back"
        systemctl stop "$unit" >>"$log" 2>&1
        rm -rf "$program" && mv "$old" "$program"
        if [ -f "$program/custos.service" ]; then
          cp "$program/custos.service" "$units/$unit.service" && systemctl daemon-reload >>"$log" 2>&1
        fi
        finish false '"the new version did not start; the previous one is back"' true
        systemctl start "$unit" >>"$log" 2>&1
        exit 0
        """;

    private static void PrepareUpdater(string staging, string updaterFolder)
    {
        string script = Path.Combine(updaterFolder, UpdateScriptFile);
        File.WriteAllText(script, UpdateScript.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(Path.Combine(staging, LinuxPaths.ProgramArchive)))
        {
            throw new IOException(Messages.Format(Messages.MonitorPackageMissingFile, LinuxPaths.ProgramArchive));
        }
    }

    private ProcessStartInfo UpdaterStart(string staging, string updaterFolder, string version)
    {
        string[] script =
        [
            "/bin/sh", Path.Combine(updaterFolder, UpdateScriptFile), Path.Combine(staging, LinuxPaths.ProgramArchive), UpdateResultFile,
            info.Version, version, CustosInfo.LinuxServiceUnit, AppContext.BaseDirectory.TrimEnd('/'),
            Path.Combine(CustosInfo.LogsDirectory, "update.log"),
        ];

        // A unit of its own, outside the control group of the service, which «systemctl stop» empties.
        bool systemd = Directory.Exists("/run/systemd/system") && Tool.Find("systemd-run") is not null;
        var start = new ProcessStartInfo(systemd ? Tool.Find("systemd-run")! : Tool.Find("setsid") ?? "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterFolder,
        };
        IEnumerable<string> arguments = systemd
            ? ["--unit=custos-update-" + version.Replace('.', '-'), "--collect", "--quiet", "--service-type=exec", .. script]
            : start.FileName == "/bin/sh" ? script[1..] : script;
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    partial void AddPlatformState(AgentSnapshot snapshot)
    {
        bool stale;
        lock (_readinessLock)
        {
            snapshot.Snapshots = [.. _readiness];
            stale = _readinessRevision != _config.ConfigRevision || DateTimeOffset.UtcNow - _readinessAt > ReadinessAge;
        }

        if (stale && _database is not null)
        {
            RefreshReadinessLater();
        }
    }

    partial void AfterRun(TaskDefinition task) => RefreshReadiness();

    partial void AddPlatformInventory(InventoryBody body)
    {
        RefreshReadiness();
        lock (_readinessLock)
        {
            body.Snapshots = [.. _readiness];
        }
    }

    private void RefreshReadinessLater()
    {
        if (Interlocked.Exchange(ref _readinessRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                RefreshReadiness();
            }
            finally
            {
                Interlocked.Exchange(ref _readinessRunning, 0);
            }

            PublishSnapshot();
        });
    }

    private void RefreshReadiness()
    {
        long revision = _config.ConfigRevision;
        List<SnapshotReadiness>? result = null;
        try
        {
            result = SnapshotCheck.Check(_config.GetTasks());
        }
#pragma warning disable CA1031 // A failed check only leaves the warnings as they were; it is made again later.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ServiceLog.MonitorMessage(logger, LogLevel.Warning, "Snapshot check failed: " + ex.Message);
        }

        lock (_readinessLock)
        {
            if (result is not null)
            {
                _readiness = result;
            }

            _readinessRevision = revision;
            _readinessAt = DateTimeOffset.UtcNow;
        }
    }
}
