using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Custos.Core;
using Custos.Core.Updates;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>Desktop packages have their own release channel even when the GUI talks to an external agent.</summary>
internal sealed class LinuxPackageUpdates
{
    private const string Package = "custos-desktop";
    private static readonly HttpClient Http = new();

    private readonly FormDialog _owner;
    private readonly bool _rpm;
    private readonly string _architecture;
    private readonly string _cacheFile;
    private readonly ChoiceItem _channel;
    private readonly NoteItem _installed = new(string.Empty);
    private readonly NoteItem _result = new(string.Empty);
    private readonly ButtonItem _check;
    private readonly ButtonItem _page;
    private readonly ButtonItem _download;
    private ProgramCheckState _state;
    private string _savedChannel;
    private bool _available;
    private CancellationTokenSource? _refresh;

    public static LinuxPackageUpdates? AddTo(FormDialog owner, Action<Exception> failed)
    {
        if (!CustosEdition.IsLinux || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) switch
        {
            "/usr/lib/custos-desktop/gui" => new LinuxPackageUpdates(owner, rpm: false, failed),
            "/usr/libexec/custos-desktop/gui" => new LinuxPackageUpdates(owner, rpm: true, failed),
            _ => null,
        };
    }

    public static ChoiceItem ChannelChoice(string? channel) => new(DesktopStrings.LinuxUpdateChannel,
        [DesktopStrings.LinuxUpdateStable, DesktopStrings.LinuxUpdatePreview], channel == ProgramReleases.PreviewChannel ? 1 : 0, 300);

    public static string SelectedChannel(ChoiceItem choice) =>
        choice.Selected == 1 ? ProgramReleases.PreviewChannel : ProgramReleases.StableChannel;

    private LinuxPackageUpdates(FormDialog owner, bool rpm, Action<Exception> failed)
    {
        _owner = owner;
        _rpm = rpm;
        _architecture = (rpm, RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.X64) => "x86_64",
            (true, _) => "aarch64",
            (false, Architecture.X64) => "amd64",
            _ => "arm64",
        };
        _cacheFile = Path.Combine(UiState.Folder, $"package-updates-{(rpm ? "rpm" : "deb")}-{_architecture}.json");
        _state = Load();
        _savedChannel = ProgramReleases.NormalizeChannel(_state.Channel);
        _state = _state.ForChannel(_savedChannel);
        _channel = ChannelChoice(_savedChannel);
        _check = new ButtonItem("update", Strings.UpdatesCheckNow, () => RefreshAsync(askGitHub: true)) { Failed = failed };
        _page = new ButtonItem(null, DesktopStrings.LinuxUpdateReleaseNotes, () => OpenAsync(download: false), ButtonLook.Outlined) { Failed = failed };
        _download = new ButtonItem(null, DesktopStrings.LinuxUpdateDownloadPackage, () => OpenAsync(download: true), ButtonLook.Outlined) { Failed = failed };
        _channel.OnChange = () =>
        {
            _refresh?.Cancel();
            _check.IsEnabled = true;
            _state = _state.ForChannel(SelectedChannel(_channel));
            _available = false;
            ShowResult();
        };

        owner.Items.Add(new HeadingItem(DesktopStrings.LinuxDesktopUpdates));
        owner.Items.Add(new NoteItem(DesktopStrings.LinuxDesktopUpdatesHelp));
        owner.Items.Add(_installed);
        owner.Items.Add(_channel);
        owner.Items.Add(_result);
        owner.Items.Add(new RowItem(_check, _page, _download));
        ShowResult();

        owner.Opened += (_, _) => Safe.Run(() => RefreshAsync(askGitHub: CheckDue()), failed);
        owner.Closed += (_, _) => _refresh?.Cancel();
    }

    /// <summary>A changed desktop channel belongs to the user and is saved only when the settings are accepted.</summary>
    public bool SaveChannel()
    {
        string channel = SelectedChannel(_channel);
        if (channel == _savedChannel)
        {
            return true;
        }

        if (!TrySave(_state))
        {
            return false;
        }

        _savedChannel = channel;
        return true;
    }

    private bool CheckDue() => _state.CheckedAt is not { } at
        || DateTimeOffset.UtcNow - at >= TimeSpan.FromDays(1) || at > DateTimeOffset.UtcNow;

    private async Task RefreshAsync(bool askGitHub)
    {
        if (!_check.IsEnabled)
        {
            return;
        }

        using var refresh = new CancellationTokenSource();
        _refresh = refresh;
        var cancellationToken = refresh.Token;
        _check.IsEnabled = _page.IsEnabled = _download.IsEnabled = false;
        try
        {
            if (askGitHub)
            {
                string channel = SelectedChannel(_channel);
                var state = new ProgramCheckState { Channel = channel };
                try
                {
                    state.Latest = await ProgramReleases.CheckAsync(Http, channel, cancellationToken, AssetName);
                }
                catch (ReleaseCheckException ex)
                {
                    state.Error = ex.Message;
                }

                cancellationToken.ThrowIfCancellationRequested();
                state.CheckedAt = DateTimeOffset.UtcNow;
                _state = state;
                if (channel == _savedChannel)
                {
                    TrySave(state);
                }
            }

            var installed = await ReadInstalledAsync(cancellationToken);
            bool available = installed is not null && PublishedRelease() is { } release
                && await IsNewerAsync(release.Version, installed, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _installed.Show(installed is { } version ? Strings.Format(DesktopStrings.LinuxUpdateInstalledVersion, version)
                : DesktopStrings.LinuxUpdateInstalledUnknown, installed is null ? Tone.Caution : Tone.Plain);
            _available = available;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_refresh, refresh))
            {
                _refresh = null;
                _check.IsEnabled = true;
                ShowResult();
            }
        }
    }

    private void ShowResult()
    {
        var latest = PublishedRelease();
        string found = _state.Error ?? (latest is null ? DesktopStrings.LinuxUpdateNoPackage
            : _available ? Strings.Format(latest.Prerelease ? DesktopStrings.LinuxUpdatePreviewAvailable : Strings.UpdatesAvailable, latest.Version)
            : DesktopStrings.LinuxUpdateNoNewerPackage);
        _result.Show(_state.CheckedAt is { } at ? Strings.Format(Strings.UpdatesLastCheck, Describe.Time(at), found) : Strings.UpdatesNever,
            _state.Error is null ? Tone.Plain : Tone.Caution);
        _page.IsEnabled = _download.IsEnabled = _available && latest is not null;
    }

    private async Task OpenAsync(bool download)
    {
        if (_available && PublishedRelease() is { } release)
        {
            var uri = download ? release.PackageUrl : release.PageUrl;
            if (!await _owner.Launcher.LaunchUriAsync(uri))
            {
                _result.Show(DesktopStrings.LinuxUpdateBrowserFailed, Tone.Caution);
            }
        }
    }

    private string AssetName(string version) => _rpm ? $"{Package}-{version}-1.{_architecture}.rpm" : $"{Package}_{version}_{_architecture}.deb";

    private ProgramRelease? PublishedRelease()
    {
        if (_state.Latest is not { } latest || ProgramReleases.ParseVersion(latest.Version) is not { Length: 3 }
            || latest.Version.Any(c => c != '.' && (c < '0' || c > '9'))
            || latest.Prerelease && SelectedChannel(_channel) != ProgramReleases.PreviewChannel)
        {
            return null;
        }

        string releases = $"https://github.com/{CustosInfo.GitHubRepository}/releases/";
        string tag = ProgramReleases.TagPrefix + latest.Version;
        string package = AssetName(latest.Version);
        return latest.PackageName == package && latest.PackageUrl?.OriginalString == releases + "download/" + tag + "/" + package
            && latest.PageUrl?.OriginalString == releases + "tag/" + tag ? latest : null;
    }

    private async Task<string?> ReadInstalledAsync(CancellationToken cancellationToken)
    {
        if (_rpm)
        {
            return (await RunAsync("rpm", cancellationToken, "-q", "--qf", "%{VERSION}-%{RELEASE}", $"{Package}.{_architecture}")
                .ConfigureAwait(false)) is (0, { Length: > 0 } version) ? version : null;
        }

        var (code, output) = await RunAsync("dpkg-query", cancellationToken, "-W", "-f=${db:Status-Status} ${Version}", $"{Package}:{_architecture}")
            .ConfigureAwait(false);
        const string installed = "installed ";
        return code == 0 && output.StartsWith(installed, StringComparison.Ordinal) && output.Length > installed.Length
            ? output[installed.Length..] : null;
    }

    private async Task<bool> IsNewerAsync(string version, string installed, CancellationToken cancellationToken)
    {
        // Native ordering includes the Preview suffixes from the package database, even at equal product versions.
        if (!_rpm)
        {
            return (await RunAsync("dpkg", cancellationToken, "--compare-versions", version, "gt", installed).ConfigureAwait(false)).Code == 0;
        }

        if (installed.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '~' or '^' or '-' or '_' or '+')))
        {
            return false;
        }

        return await RunAsync("rpm", cancellationToken, "--eval", $"%{{lua:print(rpm.vercmp(\"{version}-1\", \"{installed}\"))}}")
            .ConfigureAwait(false) is (0, "1");
    }

    private static async Task<(int Code, string Output)> RunAsync(string command, CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return (process.ExitCode, output.Trim());
        }
        catch (Win32Exception)
        {
            return (-1, string.Empty);
        }
    }

    private ProgramCheckState Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_cacheFile), UpdatesJsonContext.Default.ProgramCheckState) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private bool TrySave(ProgramCheckState state)
    {
        string temporary = _cacheFile + "." + Path.GetRandomFileName();
        try
        {
            Directory.CreateDirectory(UiState.Folder);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, UpdatesJsonContext.Default.ProgramCheckState));
            File.Move(temporary, _cacheFile, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The saved cache stays in place.
            }

            return false;
        }
    }
}
