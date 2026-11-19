using Custos.Core;
using Custos.Core.Archives;
using Custos.Core.Archives.SevenZip;
using Custos.Core.Ipc;
using Custos.Core.Text;
using Custos.Service.Platform;

namespace Custos.Service.Agent;

/// <summary>
/// The button «Проверить обновление 7-Zip» (section 6.5): only when an administrator asks, never by itself. The new
/// 7z.dll comes from the official 7-Zip releases on GitHub and is used only if its SHA-256 matches the one GitHub
/// publishes and it passes a test of packing and unpacking.
/// </summary>
internal sealed partial class AgentService
{
    private static readonly TimeSpan SevenZipCheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Held by a run and by an update of 7z.dll: a 7z.dll is never replaced under a running task.</summary>
    private readonly SemaphoreSlim _sevenZipGate = new(1, 1);

    /// <summary>Guarded by <see cref="_restoreLock"/>: no restore starts while 7z.dll is replaced.</summary>
    private bool _sevenZipUpdating;

    /// <summary>An update from the server waits for <see cref="_sevenZipGate"/>: the queue lets it in before the next run.</summary>
    private int _sevenZipWanted;

    public async Task<SevenZipCheckResult> CheckSevenZipAsync(CancellationToken cancellationToken)
    {
        string? installed = InstalledSevenZip();
        if (!OperatingSystem.IsWindows())
        {
            // The releases of 7-Zip carry 7z.dll only in installers for Windows; the 7z.so of macOS comes with CUSTOS.
            return new SevenZipCheckResult { Installed = installed, Error = Messages.SevenZipMacBundled };
        }

        try
        {
            var release = await NewestSevenZipAsync(cancellationToken).ConfigureAwait(false);
            return new SevenZipCheckResult
            {
                Installed = installed,
                Latest = release.Version,
                UpdateAvailable = SevenZipReleases.IsNewer(release.Version, installed),
            };
        }
        catch (ArchiveException ex)
        {
            return new SevenZipCheckResult { Installed = installed, Error = ex.Message };
        }
    }

    /// <summary>Downloads the newest 7-Zip, checks it and installs its 7z.dll.</summary>
    public async Task<OkResult> UpdateSevenZipAsync(string user, CancellationToken cancellationToken)
    {
        string? installed = InstalledSevenZip();
        if (!OperatingSystem.IsWindows())
        {
            return new OkResult { Ok = false, Message = Messages.SevenZipMacBundled };
        }

        string installer;
        try
        {
            var release = await NewestSevenZipAsync(cancellationToken).ConfigureAwait(false);
            if (!SevenZipReleases.IsNewer(release.Version, installed))
            {
                return new OkResult { Ok = false, Message = Messages.Format(Messages.SevenZipUpToDate, installed) };
            }

            // Runs go on while it downloads; the gate is taken only to replace the file.
            Log(LogLineLevels.Info, Messages.Format(Messages.SevenZipDownloading, release.InstallerUrl, Sizes.Format(release.Size)));
            installer = await SevenZipReleases.DownloadAsync(Http, release, DataFolders.Updates, cancellationToken).ConfigureAwait(false);
            Log(LogLineLevels.Info, Messages.Format(Messages.SevenZipVerified, release.InstallerName));
        }
        catch (ArchiveException ex)
        {
            return SevenZipFailed(ex.Message);
        }

        try
        {
            return InstallSevenZip(installer, user, cancellationToken);
        }
        finally
        {
            TryDeleteFile(installer);
        }
    }

    /// <summary>
    /// Installs the 7z.dll of a 7-Zip installer that is already downloaded and checked: for the button and for updates
    /// sent by the server. While a task runs the button is refused, and an update of the server waits for its end.
    /// A running restore refuses both.
    /// </summary>
    public OkResult InstallSevenZip(string installer, string user, CancellationToken cancellationToken, bool waitForRuns = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return SevenZipFailed(Messages.SevenZipMacBundled);
        }

        if (Platform.Archives is not ArchiveProvider provider)
        {
            return SevenZipFailed(Messages.SevenZipWindowsOnly);
        }

        bool entered;
        if (waitForRuns)
        {
            Interlocked.Increment(ref _sevenZipWanted);
            try
            {
                _sevenZipGate.Wait(cancellationToken);
                entered = true;
            }
            finally
            {
                Interlocked.Decrement(ref _sevenZipWanted);
            }
        }
        else
        {
            entered = _sevenZipGate.Wait(0, cancellationToken);
        }

        if (!entered)
        {
            string task;
            lock (_lock)
            {
                task = _current?.Task.Name ?? "?";
            }

            return new OkResult { Ok = false, Message = Messages.Format(Messages.SevenZipBusyRun, task) };
        }

        try
        {
            lock (_restoreLock)
            {
                if (_restore is { Running: true } || _sevenZipUpdating)
                {
                    return new OkResult { Ok = false, Message = _sevenZipUpdating ? Messages.SevenZipBusyUpdate : Messages.SevenZipBusyRestore };
                }

                _sevenZipUpdating = true;
            }

            try
            {
                SevenZipLibrary current;
                try
                {
                    current = provider.CurrentSevenZip();
                }
                catch (ArchiveException ex)
                {
                    return SevenZipFailed(Messages.Format(Messages.SevenZipNeedsCurrent, ex.Message));
                }

                string staging = Path.Combine(DataFolders.Updates, "7z-" + Guid.NewGuid().ToString("N")[..8]);
                var library = SevenZipInstaller.Install(installer, current, Path.GetDirectoryName(provider.SevenZipPath)!, staging,
                    line => Log(LogLineLevels.Info, line), cancellationToken);
                provider.UseSevenZip(library);
                _sevenZipProblem = null;
                string message = Messages.Format(Messages.SevenZipUpdated, current.Version, library.Version, user);
                Log(LogLineLevels.Info, message);
                ServiceLog.SevenZipUpdated(logger, current.Version, library.Version, user);
                PublishSnapshot();
                return new OkResult { Message = message };
            }
            catch (ArchiveException ex)
            {
                return SevenZipFailed(ex.Message);
            }
            finally
            {
                lock (_restoreLock)
                {
                    _sevenZipUpdating = false;
                }
            }
        }
        finally
        {
            _sevenZipGate.Release();
        }
    }

    /// <summary>The version of the 7z.dll in use; loads it if it is not yet. Null if none works.</summary>
    private string? InstalledSevenZip()
    {
        if (Platform.Archives is not ArchiveProvider provider)
        {
            return null;
        }

        try
        {
            return provider.CurrentSevenZip().Version;
        }
        catch (ArchiveException)
        {
            return null;
        }
    }

    private OkResult SevenZipFailed(string reason)
    {
        string message = Messages.Format(Messages.SevenZipUpdateFailed, reason);
        Log(LogLineLevels.Error, message);
        ServiceLog.SevenZipUpdateFailed(logger, reason);
        return new OkResult { Ok = false, Message = message };
    }

    private static async Task<SevenZipRelease> NewestSevenZipAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SevenZipCheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SevenZipReleases.ApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return SevenZipReleases.Newest(stream);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            string reason = ex is OperationCanceledException ? Messages.Format(Messages.SevenZipNoAnswer, (int)SevenZipCheckTimeout.TotalSeconds) : ex.Message;
            throw new ArchiveException(Messages.Format(Messages.SevenZipCheckFailed, reason), ex);
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CUSTOS");
        return http;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted at the next start.
        }
    }
}
