using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Custos.Core;
using Custos.Core.Monitor;
using Custos.Service.Platform;
using Microsoft.Data.Sqlite;

namespace Custos.Service.Agent;

internal sealed partial class AgentService
{
    private const string DesktopUpdateWorkPrefix = "desktop-update-";
    private const string DesktopUpdaterScript = "DesktopPackageUpdate.sh";
    private const string DesktopUpdateResult = "update-result.json";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupAndOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode GroupAndOtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static readonly TimeSpan DesktopUpdateDeadline = TimeSpan.FromMinutes(23);
    private static readonly HttpClient DesktopReleaseHttp = CreateDesktopReleaseHttp();

    private static HttpClient CreateDesktopReleaseHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }.WithTls());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CUSTOS");
        return client;
    }

    private static string? DesktopPackageFormat() => AppContext.BaseDirectory.TrimEnd('/') switch
    {
        "/usr/lib/custos-desktop/agent" => "deb",
        "/usr/libexec/custos-desktop/agent" => "rpm",
        _ => null,
    };

    private static string CreateDesktopUpdateWork()
    {
        // Every ancestor is protected from unprivileged replacement, including CUSTOS_DATA_DIR overrides.
        int directoryFlags = Native.DirectoryPathFlags;
        var directory = Native.open("/", directoryFlags);
        try
        {
            foreach (string component in Path.GetFullPath(DataFolders.Updates).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = Native.openat(directory, component, directoryFlags);
                directory.Dispose();
                directory = child;
                if (Native.Stat(directory) is not { Uid: 0 } stat
                    || (((UnixFileMode)stat.Mode & GroupAndOtherWrite) != 0 && ((UnixFileMode)stat.Mode & UnixFileMode.StickyBit) == 0))
                {
                    throw new IOException(Messages.ProgramManagedByPackage);
                }
            }

            if (!Native.IsRoot || Native.Stat(directory) is not { Uid: 0 } updates || ((UnixFileMode)updates.Mode & GroupAndOtherAccess) != 0)
            {
                throw new IOException(Messages.ProgramManagedByPackage);
            }

            string work = Path.Combine(DataFolders.Updates, DesktopUpdateWorkPrefix + Guid.NewGuid().ToString("N"));
            if (Native.mkdir(work, (uint)PrivateDirectory) != 0)
            {
                throw new IOException(Messages.ProgramManagedByPackage);
            }

            return work;
        }
        finally
        {
            directory.Dispose();
        }
    }

    private async Task<HandOverProblem?> HandOverDesktopAsync(string zip, string work, string version, string commandId,
        CancellationToken cancellationToken)
    {
        if (DesktopPackageFormat() is not { } format || !Directory.Exists("/run/systemd/system") || !File.Exists("/usr/bin/systemd-run"))
        {
            return new HandOverProblem(Messages.ProgramManagedByPackage, BadPackage: false);
        }

        string package;
        try
        {
            var asset = DesktopUpdatePackage.Read(zip, version, format, RuntimeInformation.ProcessArchitecture);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            package = await DesktopUpdatePackage.DownloadAsync(DesktopReleaseHttp, asset, work, timeout.Token).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return new HandOverProblem(Messages.Format(Messages.MonitorPackageBad, ex.Message), BadPackage: true);
        }
        catch (HttpRequestException ex)
        {
            // Never report a redirect URL, which may contain GitHub's short-lived storage credentials.
            string reason = ex.StatusCode is { } status ? string.Create(CultureInfo.InvariantCulture, $"GitHub HTTP {(int)status}") : "GitHub";
            return new HandOverProblem(Messages.Format(Messages.MonitorDownloadFailed, reason), BadPackage: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HandOverProblem(Messages.Format(Messages.MonitorDownloadFailed, Messages.MonitorTimeout), BadPackage: false);
        }

        string script = Path.Combine(work, DesktopUpdaterScript);
        using (var embedded = typeof(AgentService).Assembly.GetManifestResourceStream("Custos.Linux.DesktopPackageUpdate.sh")
            ?? throw new IOException(Messages.MonitorUpdateNoStop))
        using (var file = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            embedded.CopyTo(file);
        }

        // The artifact's authenticated, fixed name describes the native version, including the RPM release.
        string nativeVersion = format == "rpm" ? version + "-1" : version;
        string[] arguments = [script, package, Path.Combine(work, DesktopUpdateResult), info.Version, version,
            CustosInfo.LinuxServiceUnit, Path.Combine(CustosInfo.LogsDirectory, "update.log"), nativeVersion];
        var start = new ProcessStartInfo("/usr/bin/systemd-run")
        {
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string recover = string.Join(' ', arguments.Take(1).Concat(["recover"]).Concat(arguments.Skip(1)).Select(SystemdArgument));
        string[] launch =
        [
            "--unit=custos-desktop-update", "--collect", "--quiet", "--service-type=exec", "--property=RuntimeMaxSec=15min",
            "--property=TimeoutStopSec=6min", "--property=ExecStopPost=-:/bin/sh " + recover, "/bin/sh", .. arguments,
        ];
        foreach (string argument in launch)
        {
            start.ArgumentList.Add(argument);
        }

        return await StartUpdaterAsync(start, version, commandId, Path.GetFileName(work), cancellationToken).ConfigureAwait(false);
    }

    private static string SystemdArgument(string value) => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal) + '"';

    private static async Task<string?> DesktopUpdaterStartFailureAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return process.ExitCode == 0 ? null : Messages.MonitorUpdateNoStop;
        }
        catch (TimeoutException)
        {
            // The request may already have reached systemd. Keep the receipt and let the watcher resolve it.
            return null;
        }
    }

    private static string? DesktopWorkPath(string name) => name.StartsWith(DesktopUpdateWorkPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name.AsSpan(DesktopUpdateWorkPrefix.Length), "N", out _) ? Path.Combine(DataFolders.Updates, name) : null;

    private static void CleanAbandonedDesktopWork(string? referenced)
    {
        if (!ProgramManagedByPackage || !Native.IsRoot || !Directory.Exists(DataFolders.Updates))
        {
            return;
        }

        // A canceled service may have downloaded most of an update without ever starting the transient unit.
        // Keep the referenced transaction and leave newer entries alone well past the worker lifetime.
        var before = DateTime.UtcNow.AddDays(-1);
        foreach (string folder in Directory.EnumerateDirectories(DataFolders.Updates, DesktopUpdateWorkPrefix + "*"))
        {
            string name = Path.GetFileName(folder);
            if (name != referenced && DesktopWorkPath(name) is not null && Directory.GetLastWriteTimeUtc(folder) < before)
            {
                TryDeleteFolder(folder);
            }
        }
    }

    private void ResumeDesktopUpdate(PendingUpdate pending)
    {
        if (DesktopWorkPath(pending.DesktopPackageWork!) is null)
        {
            CompletePendingUpdate(pending, null, Messages.MonitorUpdateNoStop, requireUpdaterResult: true);
            return;
        }

        // StartMonitor runs before the runner and IPC start. Keep them off the package during health verification or rollback.
        _sevenZipGate.Wait(_stopping);
        lock (_restoreLock)
        {
            _sevenZipUpdating = true;
        }
        Interlocked.Exchange(ref _updateRunning, 1);
        WatchDesktopUpdate(pending);
    }

    private void WatchDesktopUpdate(PendingUpdate pending)
    {
        string work = DesktopWorkPath(pending.DesktopPackageWork!)!;
        _ = Task.Run(async () =>
        {
            bool complete = false;
            bool blockingRuns = true;
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var result = ReadUpdateResult(Path.Combine(work, DesktopUpdateResult));
                    bool matches = result is not null && result.FromVersion == pending.FromVersion && result.ToVersion == pending.ToVersion;
                    if (!matches && DateTimeOffset.UtcNow - pending.StartedAt < DesktopUpdateDeadline)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), _stopping).ConfigureAwait(false);
                        continue;
                    }

                    // The package process has finished. Let tasks and restores continue even if the report outbox is unavailable.
                    if (blockingRuns)
                    {
                        ReleaseUpdateGate();
                        blockingRuns = false;
                    }

                    try
                    {
                        CompletePendingUpdate(pending, matches ? result : null, matches ? null : Messages.MonitorUpdateNoStop, requireUpdaterResult: true);
                        complete = true;
                        break;
                    }
                    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                    {
                        ServiceLog.ConfigProblem(logger, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(30), _stopping).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // The worker owns the handover while the service is being restarted.
            }
            finally
            {
                if (complete)
                {
                    TryDeleteFolder(work);
                    Interlocked.Exchange(ref _updateRunning, 0);
                    PublishSnapshot();
                }
            }
        }, CancellationToken.None);
    }
}
