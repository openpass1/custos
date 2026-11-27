using System.Net;
using System.Text.Json;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Text;
using Custos.Core.Updates;
using Custos.Service.Platform;

namespace Custos.Service.Agent;

/// <summary>
/// New versions of the program from GitHub, for a computer without a monitoring site. Once a day the service asks
/// for the newest release <c>client-vX.Y.Z</c> and shows it in the program window; it is installed only when an
/// administrator presses «Обновить». The package is checked against the SHA-256 of the release and then goes the same
/// way as an update from the site: Custos.Updater stops the service, replaces the files and starts it again.
/// A computer connected to a site never asks GitHub: the site decides about updates.
/// </summary>
internal sealed partial class AgentService
{
    private static readonly TimeSpan ProgramCheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ProgramCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A stalled download must not hold the update forever; the window waits 15 minutes.</summary>
    private static readonly TimeSpan ProgramDownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How often the loop looks whether a check is due (the setting may have been turned on meanwhile).</summary>
    private static readonly TimeSpan ProgramLoopTick = TimeSpan.FromHours(1);

    private static readonly string ProgramStateFile = Path.Combine(DataFolders.Config, "program-update.json");

    private readonly Lock _programLock = new();
    private readonly SemaphoreSlim _programCheckGate = new(1, 1);

    /// <summary>Guarded by <see cref="_programLock"/>; replaced as a whole, never changed in place.</summary>
    private ProgramCheckState _programState = new();

    /// <summary>The daily check is on: the setting says so and no monitoring site is connected.</summary>
    private bool ProgramChecking => !ProgramManagedByPackage && _config.GetSettings().CheckUpdates && !ServerConfigured;

    /// <summary>For the snapshot: what the program window shows about new versions.</summary>
    private ProgramUpdateInfo DescribeProgramUpdate()
    {
        ProgramCheckState state;
        lock (_programLock)
        {
            state = _programState;
        }

        bool checking = ProgramChecking;
        var latest = state.Latest;
        return new ProgramUpdateInfo
        {
            Checking = checking,
            ManagedBySite = ServerConfigured && !ProgramManagedByPackage,
            CheckedAt = state.CheckedAt,
            Latest = latest?.Version,
            Available = checking && latest is not null && ProgramReleases.IsNewer(latest.Version, info.Version),
            PageUrl = latest?.PageUrl.ToString(),
            Error = state.Error,
            Installing = Volatile.Read(ref _updateRunning) == 1,
        };
    }

    /// <summary>Asks GitHub once a day while <see cref="ProgramChecking"/>; the time of the last check survives restarts.</summary>
    private async Task ProgramUpdateLoopAsync(CancellationToken stoppingToken)
    {
        LoadProgramState();

        // Not at once: the computer has just started, and the computers of one office would all ask in the same minute.
        var delay = TimeSpan.FromMinutes(Random.Shared.Next(2, 11));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = ProgramLoopTick;
            DateTimeOffset? checkedAt;
            lock (_programLock)
            {
                checkedAt = _programState.CheckedAt;
            }

            var now = DateTimeOffset.UtcNow;
            bool due = checkedAt is not { } last || now - last >= ProgramCheckInterval || last > now + ProgramLoopTick;
            if (!due || !ProgramChecking)
            {
                continue;
            }

            try
            {
                await CheckProgramAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed check must not stop the loop; it is tried again tomorrow.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ServiceLog.ProgramCheckCrashed(logger, ex);
            }
        }
    }

    /// <summary>Asks GitHub now (the daily check and the button «Проверить сейчас»).</summary>
    public async Task<ProgramUpdateInfo> CheckProgramAsync(CancellationToken cancellationToken)
    {
        if (ProgramManagedByPackage || ServerConfigured)
        {
            return DescribeProgramUpdate();
        }

        await _programCheckGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProgramRelease? latest = null;
            string? error = null;
            try
            {
                latest = await NewestProgramAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ReleaseCheckException ex)
            {
                error = ex.Message;
            }

            string? announce = null;
            lock (_programLock)
            {
                var previous = _programState;
                var state = new ProgramCheckState
                {
                    CheckedAt = DateTimeOffset.UtcNow,

                    // Without an answer the last known release stays: the button still works when GitHub answers again.
                    Latest = error is null ? latest : previous.Latest,
                    Error = error,
                    Announced = previous.Announced,
                };
                if (latest is not null && ProgramReleases.IsNewer(latest.Version, info.Version) && latest.Version != previous.Announced)
                {
                    state.Announced = announce = latest.Version;
                }

                _programState = state;
                SaveProgramState(state);
            }

            // The live log tells a new version once. A failed check goes only to the service log: a computer
            // without internet would otherwise get the same line every day.
            if (announce is not null)
            {
                Log(LogLineLevels.Info, Messages.Format(Messages.ProgramFound, announce, info.Version));
            }

            ServiceLog.ProgramCheck(logger, error ?? (latest is null ? Messages.ProgramNoRelease : Messages.Format(Messages.ProgramUpToDate, latest.Version)));
            PublishSnapshot();
            return DescribeProgramUpdate();
        }
        finally
        {
            _programCheckGate.Release();
        }
    }

    /// <summary>
    /// «Обновить»: asks GitHub again (the release may have changed since the last check), downloads the package and
    /// checks its SHA-256 while the window waits, then installs it as soon as no task runs.
    /// </summary>
    public async Task<OkResult> UpdateProgramAsync(string user, CancellationToken cancellationToken)
    {
        if (ProgramManagedByPackage)
        {
            return ProgramRefused(Messages.ProgramManagedByPackage);
        }

        if (ServerConfigured)
        {
            return ProgramRefused(Messages.ProgramManagedBySite);
        }

        if (info.RunMode != RunMode.Service)
        {
            return ProgramRefused(Messages.MonitorUpdateConsole);
        }

        ProgramRelease? release;
        try
        {
            release = await NewestProgramAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ReleaseCheckException ex)
        {
            return ProgramRefused(ex.Message);
        }

        if (release is null || !ProgramReleases.IsNewer(release.Version, info.Version))
        {
            return ProgramRefused(Messages.Format(Messages.ProgramNothingNewer, info.Version));
        }

        if (Interlocked.CompareExchange(ref _updateRunning, 1, 0) == 1)
        {
            return ProgramRefused(Messages.MonitorUpdateBusy);
        }

        string version = release.Version;
        string? package = null;
        bool handedOver = false;
        PublishSnapshot();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProgramDownloadTimeout);
        try
        {
            string sha256 = release.Sha256 ?? await ReadPublishedSumAsync(release, limit.Token).ConfigureAwait(false);
            Log(LogLineLevels.Info, Messages.Format(Messages.ProgramUpdateBy, version, user));
            Log(LogLineLevels.Info, Messages.Format(Messages.ProgramDownloading, version, Sizes.Format(release.Size)));
            package = await ProgramReleases.DownloadAsync(Http, release, sha256, DataFolders.Updates, limit.Token).ConfigureAwait(false);
            Log(LogLineLevels.Info, Messages.Format(Messages.ProgramVerified, release.PackageName));

            // The window need not wait for the end of a run: the service goes on by itself.
            string checkedPackage = package;
            _ = Task.Run(() => HandOverProgramAsync(checkedPackage, version), CancellationToken.None);
            handedOver = true;
            return new OkResult { Ok = true, Message = Messages.Format(Messages.ProgramUpdateStarted, version) };
        }
        catch (ReleaseCheckException ex)
        {
            return ProgramFailed(version, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProgramFailed(version, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProgramFailed(version, Messages.Format(Messages.ProgramDownloadFailed, release.PackageUrl,
                Messages.Format(Messages.SevenZipNoAnswer, (int)ProgramDownloadTimeout.TotalSeconds)));
        }
        finally
        {
            if (!handedOver)
            {
                if (package is not null)
                {
                    TryDeleteFile(package);
                }

                Interlocked.Exchange(ref _updateRunning, 0);
                PublishSnapshot();
            }
        }
    }

    /// <summary>The checked package goes to Custos.Updater once the run in progress has ended.</summary>
    private async Task HandOverProgramAsync(string package, string version)
    {
        bool done = false;
        try
        {
            if (await HandOverAsync(package, version, commandId: null, _stopping).ConfigureAwait(false) is { } problem)
            {
                _ = ProgramFailed(version, problem.Reason);
            }
            else
            {
                done = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The service stops: the update is not installed, the button can be pressed again.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _ = ProgramFailed(version, ex.Message);
        }
        finally
        {
            TryDeleteFile(package);
            if (!done)
            {
                Interlocked.Exchange(ref _updateRunning, 0);
                PublishSnapshot();
            }
        }
    }

    /// <summary>The newest release that can be installed; null when GitHub has none (or the repository is private).</summary>
    private static async Task<ProgramRelease?> NewestProgramAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProgramCheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProgramReleases.ApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new ReleaseCheckException(Messages.Format(Messages.ProgramCheckFailed, Messages.ProgramCheckLimited));
            }

            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return ProgramReleases.Newest(stream);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            string reason = ex is OperationCanceledException ? Messages.Format(Messages.SevenZipNoAnswer, (int)ProgramCheckTimeout.TotalSeconds) : ex.Message;
            throw new ReleaseCheckException(Messages.Format(Messages.ProgramCheckFailed, reason), ex);
        }
    }

    /// <summary>The SHA-256 of the package from SHA256SUMS.txt, for a release where GitHub shows no digest.</summary>
    private static async Task<string> ReadPublishedSumAsync(ProgramRelease release, CancellationToken cancellationToken)
    {
        if (release.SumsUrl is not { } url)
        {
            throw new ReleaseCheckException(Messages.Format(Messages.ProgramNoSum, release.PackageName));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProgramCheckTimeout);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ProgramReleases.MaxSumsBytes)
            {
                throw new ReleaseCheckException(Messages.Format(Messages.ProgramNoSum, release.PackageName));
            }

            string sums = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ProgramReleases.FindSum(sums, release.PackageName)
                ?? throw new ReleaseCheckException(Messages.Format(Messages.ProgramNoSum, release.PackageName));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            string reason = ex is OperationCanceledException ? Messages.Format(Messages.SevenZipNoAnswer, (int)ProgramCheckTimeout.TotalSeconds) : ex.Message;
            throw new ReleaseCheckException(Messages.Format(Messages.ProgramDownloadFailed, url, reason), ex);
        }
    }

    private void LoadProgramState()
    {
        try
        {
            if (File.Exists(ProgramStateFile)
                && JsonSerializer.Deserialize(File.ReadAllBytes(ProgramStateFile), UpdatesJsonContext.Default.ProgramCheckState) is { } state)
            {
                lock (_programLock)
                {
                    _programState = state;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Asked again at the next check.
            ServiceLog.ConfigProblem(logger, ex.Message);
        }
    }

    private void SaveProgramState(ProgramCheckState state)
    {
        try
        {
            File.WriteAllBytes(ProgramStateFile, JsonSerializer.SerializeToUtf8Bytes(state, UpdatesJsonContext.Default.ProgramCheckState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Then the next start asks again.
            ServiceLog.ConfigProblem(logger, ex.Message);
        }
    }

    private static OkResult ProgramRefused(string reason) => new() { Ok = false, Message = reason };

    private OkResult ProgramFailed(string version, string reason)
    {
        ServiceLog.UpdateRejected(logger, version, reason);
        string message = Messages.Format(Messages.MonitorUpdateFailed, version, reason);
        Log(LogLineLevels.Error, message);
        return new OkResult { Ok = false, Message = message };
    }
}
