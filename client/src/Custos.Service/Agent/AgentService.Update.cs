using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Monitor;
using Custos.Service.Platform;

namespace Custos.Service.Agent;

/// <summary>
/// <c>update.install</c> (A.10): the package is downloaded and checked here, Custos.Updater stops the service,
/// replaces the program files and starts the service again; the result reaches the server after the restart.
/// </summary>
internal sealed partial class AgentService
{
    private const long MaxPackageBytes = 500L * 1024 * 1024;

    /// <summary>The update is handed to Custos.Updater (written before the service stops).</summary>
    private static readonly string UpdatePendingFile = Path.Combine(DataFolders.Config, "update-pending.json");

    /// <summary>What Custos.Updater did (written by it).</summary>
    private static readonly string UpdateResultFile = Path.Combine(DataFolders.Config, "update-result.json");

#if !CUSTOS_MAC && !CUSTOS_LINUX
    /// <summary>The files Custos.Updater needs to run (it is a .NET program without other references).</summary>
    private static readonly string[] UpdaterFiles = ["Custos.Updater.exe", "Custos.Updater.dll", "Custos.Updater.runtimeconfig.json", "Custos.Updater.deps.json"];

    /// <summary>Without them a package is no CUSTOS.</summary>
    private static readonly string[] RequiredFiles = ["CustosService.exe", "CustosService.dll", "Custos.exe", "Custos.Core.dll", .. UpdaterFiles];
#endif

    /// <summary>If the service still runs this long after the updater was started, the update did not happen.</summary>
    private static readonly TimeSpan UpdateDeadline = TimeSpan.FromMinutes(10);

    private int _updateRunning;

    // Debian packages keep this executable and its service unit under dpkg ownership.
    private static bool ProgramManagedByPackage =>
#if CUSTOS_PACKAGE_MANAGED
        true;
#else
        false;
#endif

    private async Task<CommandOutcome?> InstallUpdateAsync(MonitorCommand command, CancellationToken cancellationToken)
    {
        string? version = Text(command.Payload, "version");
        string? sha256 = Text(command.Payload, "sha256")?.ToLowerInvariant();
        string? signature = Text(command.Payload, "signature");
        if (version is null || sha256 is null || !IsSha256(sha256) || !VersionPattern().IsMatch(version))
        {
            return Rejected(version ?? "?", Messages.MonitorPackageBadCommand);
        }

        if (!_link!.VerifyServerSignature(CustosEdition.PackageSignedString(version, sha256), signature))
        {
            return Rejected(version, Messages.MonitorPackageBadSignature);
        }

        if (ProgramManagedByPackage)
        {
            return CommandOutcome.Failed(Messages.ProgramManagedByPackage);
        }

        if (version == info.Version)
        {
            return CommandOutcome.Done(Messages.Format(Messages.MonitorUpdateSame, version));
        }

        if (info.RunMode != RunMode.Service)
        {
            return CommandOutcome.Failed(Messages.MonitorUpdateConsole);
        }

        if (Interlocked.CompareExchange(ref _updateRunning, 1, 0) == 1)
        {
            return CommandOutcome.Failed(Messages.MonitorUpdateBusy);
        }

        bool handedOver = false;
        string package = Path.Combine(DataFolders.Updates, $"custos-{version}.zip");
        try
        {
            Directory.CreateDirectory(DataFolders.Updates);
            Log(LogLineLevels.Info, Messages.Format(Messages.MonitorUpdateDownloading, version));
            var answer = await _link.DownloadAsync($"agent/updates/{version}", package, MaxPackageBytes, cancellationToken).ConfigureAwait(false);
            if (!answer.Ok)
            {
                return Failed(version, Messages.Format(Messages.MonitorDownloadFailed, answer.Describe()));
            }

            if (!string.Equals(FileSha256(package), sha256, StringComparison.Ordinal))
            {
                return Rejected(version, Messages.MonitorPackageBadHash);
            }

            if (await HandOverAsync(package, version, command.Id, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return problem.BadPackage ? Rejected(version, problem.Reason) : Failed(version, problem.Reason);
            }

            handedOver = true;

            // The result is sent by the service that starts after the update (or by the watch below).
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Failed(version, ex.Message);
        }
        finally
        {
            TryDeleteFile(package);
            if (!handedOver)
            {
                Interlocked.Exchange(ref _updateRunning, 0);
            }
        }
    }

    /// <summary>Why a checked package was not handed to Custos.Updater; <see cref="BadPackage"/> when the package itself is wrong.</summary>
    private readonly record struct HandOverProblem(string Reason, bool BadPackage);

    /// <summary>
    /// The common end of an update from the server and from GitHub: the downloaded and checked package is unpacked,
    /// the runs are let finish, and Custos.Updater takes over (it stops the service, replaces the files and starts it).
    /// Null when the updater was started; the caller holds <see cref="_updateRunning"/>, which then stays taken.
    /// </summary>
    private async Task<HandOverProblem?> HandOverAsync(string package, string version, string? commandId, CancellationToken cancellationToken)
    {
        if (ProgramManagedByPackage)
        {
            return new HandOverProblem(Messages.ProgramManagedByPackage, BadPackage: false);
        }

        bool handedOver = false;
        string staging = Path.Combine(DataFolders.Updates, $"custos-{version}");
        try
        {
            if (Unpack(package, staging, version) is { } problem)
            {
                return new HandOverProblem(Messages.Format(Messages.MonitorPackageBad, problem), BadPackage: true);
            }

            string updaterFolder = Path.Combine(DataFolders.Updates, "updater-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(updaterFolder);
            PrepareUpdater(staging, updaterFolder);

            // No run may start now, and the one in progress ends first (A.10).
            Log(LogLineLevels.Info, Messages.Format(Messages.MonitorUpdateWaiting, version));
            Interlocked.Increment(ref _sevenZipWanted);
            try
            {
                await _sevenZipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _sevenZipWanted);
            }

            bool restoreRunning;
            lock (_restoreLock)
            {
                restoreRunning = _restore is { Running: true } || _sevenZipUpdating;
            }

            if (restoreRunning)
            {
                _sevenZipGate.Release();
                return new HandOverProblem(Messages.MonitorUpdateRestoreRunning, BadPackage: false);
            }

            var pending = new PendingUpdate { CommandId = commandId, FromVersion = info.Version, ToVersion = version, StartedAt = DateTimeOffset.UtcNow };
            File.WriteAllBytes(UpdatePendingFile, JsonSerializer.SerializeToUtf8Bytes(pending, MonitorJsonContext.Default.PendingUpdate));
            TryDeleteFile(UpdateResultFile);

            var start = UpdaterStart(staging, updaterFolder, version);
            ServiceLog.UpdateStarting(logger, version);
            Log(LogLineLevels.Warning, Messages.Format(Messages.MonitorUpdateInstalling, info.Version, version));
            try
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                TryDeleteFile(UpdatePendingFile);
                _sevenZipGate.Release();
                return new HandOverProblem(ex.Message, BadPackage: false);
            }

            handedOver = true;
            WatchUpdate(version);
            return null;
        }
        finally
        {
            if (!handedOver)
            {
                TryDeleteFolder(staging);
            }
        }
    }

#if !CUSTOS_MAC && !CUSTOS_LINUX
    /// <summary>Custos.Updater runs from a copy: the program folder is what it replaces.</summary>
    private static void PrepareUpdater(string staging, string updaterFolder)
    {
        foreach (string name in UpdaterFiles)
        {
            File.Copy(Path.Combine(staging, name), Path.Combine(updaterFolder, name), overwrite: true);
        }
    }

    private ProcessStartInfo UpdaterStart(string staging, string updaterFolder, string version)
    {
        var start = new ProcessStartInfo(Path.Combine(updaterFolder, "Custos.Updater.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterFolder,
        };
        foreach (string argument in new[]
        {
            "--install", staging, "--target", AppContext.BaseDirectory.TrimEnd('\\'), "--result", UpdateResultFile,
            "--service", CustosInfo.ServiceName, "--from", info.Version, "--to", version,
        })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
#endif

    /// <summary>The updater stops this service within seconds; if it never does, the runs go on and the server hears why.</summary>
    private void WatchUpdate(string version)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(UpdateDeadline, _stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The service stops: the update goes on.
                return;
            }

            string reason = ReadUpdateResult()?.Message ?? Messages.MonitorUpdateNoStop;
            FinishPendingUpdate(reason);
            _sevenZipGate.Release();
            Interlocked.Exchange(ref _updateRunning, 0);
            ServiceLog.UpdateRejected(logger, version, reason);
        }, CancellationToken.None);
    }

    /// <summary>
    /// After a restart: the result of an update handed to Custos.Updater goes to the server, events 4000 or 4001 to the
    /// Windows log. The version running now is the truth.
    /// </summary>
    private void FinishPendingUpdate(string? failure = null)
    {
        CleanUpdates();
        if (!File.Exists(UpdatePendingFile))
        {
            return;
        }

        PendingUpdate? pending = null;
        try
        {
            pending = JsonSerializer.Deserialize(File.ReadAllBytes(UpdatePendingFile), MonitorJsonContext.Default.PendingUpdate);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ServiceLog.ConfigProblem(logger, ex.Message);
        }

        var result = ReadUpdateResult();
        TryDeleteFile(UpdatePendingFile);
        TryDeleteFile(UpdateResultFile);
        if (pending is null)
        {
            return;
        }

        CommandOutcome outcome;
        if (failure is null && info.Version == pending.ToVersion)
        {
            string message = Messages.Format(Messages.MonitorUpdateDone, pending.FromVersion, pending.ToVersion);
            ServiceLog.UpdateInstalled(logger, pending.FromVersion, pending.ToVersion);
            Log(LogLineLevels.Info, message);
            outcome = CommandOutcome.Done(message);
        }
        else
        {
            string reason = failure ?? result?.Message ?? Messages.MonitorUpdateNoResult;
            string message = Messages.Format(Messages.MonitorUpdateFailed, pending.ToVersion, reason);
            if (failure is null)
            {
                ServiceLog.UpdateRejected(logger, pending.ToVersion, reason);
            }

            Log(LogLineLevels.Error, message);
            outcome = CommandOutcome.Failed(message);
        }

        if (pending.CommandId is { } commandId)
        {
            _link?.FinishById(commandId, outcome);
        }
    }

    private static UpdaterResult? ReadUpdateResult()
    {
        try
        {
            return File.Exists(UpdateResultFile) ? JsonSerializer.Deserialize(File.ReadAllBytes(UpdateResultFile), MonitorJsonContext.Default.UpdaterResult) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Unpacks the files of the manifest into <paramref name="staging"/>; null when the package is good, else why not.</summary>
    private static string? Unpack(string package, string staging, string version)
    {
        TryDeleteFolder(staging);
        using var zip = ZipFile.OpenRead(package);
        var manifestEntry = zip.GetEntry("manifest.json");
        if (manifestEntry is null)
        {
            return Messages.MonitorPackageNoManifest;
        }

        UpdateManifest? manifest;
        using (var stream = manifestEntry.Open())
        {
            try
            {
                manifest = JsonSerializer.Deserialize(stream, MonitorJsonContext.Default.UpdateManifest);
            }
            catch (JsonException ex)
            {
                return ex.Message;
            }
        }

        if (manifest is null || manifest.Version != version)
        {
            return Messages.Format(Messages.MonitorPackageOtherVersion, manifest?.Version, version);
        }

        if (manifest.MinProtocol > int.Parse(MonitorClient.ProtocolVersion, System.Globalization.CultureInfo.InvariantCulture))
        {
            return Messages.Format(Messages.MonitorPackageProtocol, manifest.MinProtocol);
        }

        // The editions are different programs (.NET 10 x64, .NET 6 x86, the program of macOS): none installs another's package.
        if (!string.Equals(manifest.Edition, CustosEdition.Wire, StringComparison.Ordinal))
        {
            return CustosEdition.PackageOfOtherEdition(manifest.Edition);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        foreach (var file in manifest.Files)
        {
            string path = file.Path.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(staging, path));
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal)
                || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !names.Add(path))
            {
                return Messages.Format(Messages.MonitorPackageBadPath, file.Path);
            }

            var entry = zip.GetEntry(file.Path) ?? zip.GetEntry(path);
            if (entry is null)
            {
                return Messages.Format(Messages.MonitorPackageMissingFile, file.Path);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            using var stream = File.OpenRead(target);
            if (file.Sha256 is null || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Messages.Format(Messages.MonitorPackageFileHash, file.Path);
            }
        }

        foreach (string required in RequiredFiles)
        {
            if (!names.Contains(required))
            {
                return Messages.Format(Messages.MonitorPackageMissingFile, required);
            }
        }

        return null;
    }

    /// <summary>What earlier updates left in <c>updates</c>: unpacked packages and copies of the updater.</summary>
    private static void CleanUpdates()
    {
        if (!Directory.Exists(DataFolders.Updates))
        {
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(DataFolders.Updates))
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith("custos-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("updater-", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFolder(folder);
            }
        }

        foreach (string file in Directory.EnumerateFiles(DataFolders.Updates, "custos-*.zip"))
        {
            TryDeleteFile(file);
        }
    }

    private CommandOutcome Rejected(string version, string reason)
    {
        ServiceLog.UpdateRejected(logger, version, reason);
        Log(LogLineLevels.Error, Messages.Format(Messages.MonitorUpdateFailed, version, reason));
        return CommandOutcome.Rejected(reason);
    }

    private CommandOutcome Failed(string version, string reason)
    {
        ServiceLog.UpdateRejected(logger, version, reason);
        Log(LogLineLevels.Error, Messages.Format(Messages.MonitorUpdateFailed, version, reason));
        return CommandOutcome.Failed(reason);
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted at the next start.
        }
    }

    /// <summary>The form of versions the server accepts for packages.</summary>
    private static readonly Regex VersionPatternRegex = new(@"^\d{1,5}(\.\d{1,5}){1,3}([-+][0-9A-Za-z.-]{1,20})?$");

    private static Regex VersionPattern() => VersionPatternRegex;
}
