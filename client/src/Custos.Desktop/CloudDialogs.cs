using System.Net.Sockets;
using Custos.Core;
using Custos.Core.Cloud;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>Names of the cloud drives in the language of the interface (section 6.3, «Облачные диски»).</summary>
internal static class CloudNames
{
    /// <summary>The cloud drives in the order of the lists.</summary>
    public static readonly string[] Kinds = [DestinationKinds.GDrive, DestinationKinds.YaDisk, DestinationKinds.Dropbox];

    /// <summary>«Google Диск», «Яндекс Диск», «Dropbox».</summary>
    public static string Name(string kind) => kind switch
    {
        DestinationKinds.GDrive => Strings.KindGDrive,
        DestinationKinds.YaDisk => Strings.KindYaDisk,
        DestinationKinds.Dropbox => Strings.KindDropbox,
        _ => kind,
    };

    /// <summary>The folder of CUSTOS in the account, as the person sees it: «CUSTOS» in Google Drive, the app folder elsewhere.</summary>
    public static string AppFolder(string kind) => kind == DestinationKinds.GDrive ? "CUSTOS" : Strings.CloudAppFolder;
}

/// <summary>
/// «Подключить аккаунт» of a cloud drive (section 6.3): the service prepares the sign-in, the browser shows the page
/// of the cloud drive, and the code comes back either by itself to a loopback port of this computer or pasted by the
/// person. The service trades it for tokens; the window never sees them (CloudConnectDialog of Custos.exe).
/// </summary>
internal sealed class CloudConnectDialog : FormDialog, IDisposable
{
    private readonly ServiceMonitor _monitor;
    private readonly string _kind;
    private readonly Credential? _existing;
    private readonly ButtonItem _open;
    private readonly ButtonItem _copy;
    private readonly TextItem _answer;
    private readonly NoteItem _status = new(string.Empty);
    private readonly CancellationTokenSource _closing = new();
    private LoopbackReceiver? _receiver;
    private CloudBeginResult? _begun;
    private bool _finishing;
    private bool _closed;

    /// <param name="monitor">The connection to the service.</param>
    /// <param name="kind">gdrive, yadisk or dropbox.</param>
    /// <param name="existing">An account to connect again (its tasks keep it), or null for a new one.</param>
    public CloudConnectDialog(ServiceMonitor monitor, string kind, Credential? existing = null)
        : base(existing is null ? Strings.Format(Strings.CloudConnectTitle, CloudNames.Name(kind)) : Strings.Format(Strings.CloudReconnectTitle, existing.DisplayName), 660)
    {
        _monitor = monitor;
        _kind = kind;
        _existing = existing;
        OkButton.Content = Strings.CloudConnectButton;
        _open = ButtonItem.Of("open_in_new", Strings.CloudOpenPage, () => Safe.Run(OpenPageAsync));
        _copy = new ButtonItem("content_copy", Strings.CloudCopyLink, CopyLinkAsync, ButtonLook.Outlined) { Failed = ex => ShowStatus(ex.Message, error: true) };
        _open.IsEnabled = _copy.IsEnabled = false;
        _answer = new TextItem(Strings.CloudAnswer.TrimEnd(':', ' '))
        {
            Secret = true, ActionIcon = "content_paste", ActionTip = Strings.CloudPasteTip, Action = PasteAsync,
        };

        ClipboardText = ReadClipboardAsync;
        Items.Add(new LabelItem(Strings.Format(Strings.CloudConnectSteps, CloudNames.Name(kind), CloudNames.AppFolder(kind))));
        if (existing is not null)
        {
            Items.Add(new NoteItem(Strings.Format(Strings.CloudReconnectNote, existing.User)));
        }

        Items.Add(new RowItem(_open, _copy));
        Items.Add(_answer);
        Items.Add(_status);

        Opened += (_, _) => Safe.Run(() => BeginAsync(null), ex => ShowStatus(ex.Message, error: true));
        Closed += (_, _) => Dispose();
    }

    /// <summary>The account as the service stored it, after it was connected.</summary>
    public Credential? Saved { get; private set; }

    /// <summary>Reads the text of the clipboard; the checks put their own text here.</summary>
    internal Func<Task<string?>> ClipboardText { get; set; }

    /// <summary>The page of the service, once the service prepared the sign-in.</summary>
    internal string? SignInUrl => _begun?.Url;

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _closing.Cancel();
        CloseReceiver();
        _closing.Dispose();
    }

    protected override async Task<bool> OkAsync()
    {
        string answer = _answer.Text.Trim();
        if (answer.Length == 0)
        {
            _answer.Error = Strings.CloudAnswerRequired;
            return false;
        }

        return await FinishAsync(answer);
    }

    private async Task<string?> ReadClipboardAsync() =>
        Clipboard is { } clipboard ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) : null;

    private static string Join(string? first, string second) => first is null ? second : first + Environment.NewLine + second;

    /// <summary>Asks the service for a sign-in: a new one at the start and after a failed attempt.</summary>
    /// <param name="problem">What went wrong with the attempt before, shown in front of the new state.</param>
    private async Task BeginAsync(string? problem)
    {
        var service = CloudService.Of(_kind);
        _begun = null;
        _open.IsEnabled = _copy.IsEnabled = false;
        ShowStatus(Strings.CloudPreparing, error: false);
        CloseReceiver();
        // Match the Linux CLI: Yandex Disk and Dropbox can return a code without registering a new redirect.
        if (!CustosEdition.IsLinux || service.CodeOnScreen is null)
        {
            _receiver = LoopbackReceiver.TryStart(service.LoopbackPort);
        }

        if (CustosEdition.IsLinux && _receiver is null && service.CodeOnScreen is null)
        {
            ShowStatus(Join(problem, DesktopStrings.LinuxCloudNoLoopback), error: true);
            return;
        }

        // Without a port of our own the service shows the code on its page; Google has no such page, so the browser
        // goes to a loopback address nobody answers, and its address line holds the code.
        string? redirect = _receiver is { } receiver ? service.LoopbackRedirect(receiver.Port)
            : service.CodeOnScreen is null ? service.LoopbackRedirect(CloudService.FixedPort)
            : null;
        CloudBeginResult begun;
        try
        {
            begun = await _monitor.RequestAsync(PipeProtocol.Methods.CloudBegin,
                new CloudBeginRequest { Kind = _kind, RedirectUri = redirect, CredentialId = _existing?.Id },
                IpcJsonContext.Default.CloudBeginRequest, IpcJsonContext.Default.CloudBeginResult);
        }
        catch (ServiceUnavailableException ex)
        {
            CloseReceiver();
            ShowStatus(Join(problem, ex.Message), error: true);
            return;
        }

        if (_closed)
        {
            return;
        }

        _begun = begun;
        _open.IsEnabled = _copy.IsEnabled = true;
        string ready = _receiver is not null ? Strings.CloudReady
            : service.CodeOnScreen is null ? Strings.CloudReadyAddress
            : Strings.CloudReadyCode;
        ShowStatus(Join(problem, ready), error: problem is not null);
        if (_receiver is { } listening && redirect is not null)
        {
            Safe.Run(() => ListenAsync(listening, begun, redirect));
        }
    }

    /// <summary>Waits for the browser to come back to the loopback port with the answer of this sign-in.</summary>
    private async Task ListenAsync(LoopbackReceiver receiver, CloudBeginResult begun, string redirect)
    {
        string target;
        try
        {
            target = await receiver.WaitAsync(Strings.CloudBrowserDone, _closing.Token, begun.State);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
            // The window was closed, or the listening stopped.
            return;
        }

        if (_closed || !ReferenceEquals(begun, _begun))
        {
            return;
        }

        Activate();
        Busy = true;
        bool done;
        try
        {
            done = await FinishAsync(redirect.TrimEnd('/') + target);
        }
        finally
        {
            Busy = false;
        }

        if (done)
        {
            Done();
        }
    }

    /// <summary>Gives the answer of the browser to the service, which trades it for the tokens of the account.</summary>
    private async Task<bool> FinishAsync(string answer)
    {
        if (_finishing || _begun is not { } begun)
        {
            return false;
        }

        _finishing = true;
        _open.IsEnabled = _copy.IsEnabled = false;
        ShowStatus(Strings.CloudConnecting, error: false);
        try
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.CloudFinish, new CloudFinishRequest { State = begun.State, Answer = answer },
                IpcJsonContext.Default.CloudFinishRequest, IpcJsonContext.Default.CredentialSaveResult, TimeSpan.FromMinutes(3));
            Saved = result.Credential;
            _answer.Load(string.Empty);
            return true;
        }
        catch (ServiceUnavailableException ex)
        {
            if (!_closed)
            {
                // The service forgets a sign-in once its answer came: a new attempt needs a new page.
                _answer.Load(string.Empty);
                await BeginAsync(ex.Message + " " + Strings.CloudTryAgain);
            }

            return false;
        }
        finally
        {
            _finishing = false;
        }
    }

    private async Task OpenPageAsync()
    {
        if (_begun is not { } begun)
        {
            return;
        }

        if (!Uri.TryCreate(begun.Url, UriKind.Absolute, out var uri) || !await Launcher.LaunchUriAsync(uri))
        {
            ShowStatus(Strings.Format(Strings.CloudBrowserFailed, begun.Url), error: true);
        }
    }

    private async Task CopyLinkAsync()
    {
        if (_begun is not { } begun)
        {
            return;
        }

        await CopyAsync(this, begun.Url);
        ShowStatus(Strings.CloudLinkCopied, error: false);
    }

    /// <summary>The first line of the clipboard with text goes into the field.</summary>
    private async Task PasteAsync()
    {
        string? line = (await ClipboardText())?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrEmpty(line))
        {
            _answer.Text = line;
        }
    }

    private void ShowStatus(string text, bool error)
    {
        if (!_closed)
        {
            _status.Show(text, error ? Tone.Error : Tone.Plain);
        }
    }

    private void CloseReceiver()
    {
        _receiver?.Dispose();
        _receiver = null;
    }
}

/// <summary>
/// «Свои приложения» of the cloud drives (settings, «Облачные диски», section 6.3): the client id and secret of an
/// application the administrator registered at a service himself. Local to this computer.
/// </summary>
internal sealed class CloudAppsDialog : FormDialog
{
    private readonly ServiceMonitor _monitor;
    private readonly Dictionary<string, (TextItem Id, TextItem Secret)> _fields = [];

    public CloudAppsDialog(ServiceMonitor monitor, CloudAppsResult current)
        : base(Strings.CloudAppsTitle, 660, sizable: true)
    {
        _monitor = monitor;
        Items.Add(new NoteItem(Strings.CloudAppsNote));
        foreach (string kind in CloudNames.Kinds)
        {
            Items.Add(new HeadingItem(CloudNames.Name(kind)));
            Items.Add(new NoteItem(current.BuiltIn.Contains(kind) ? Strings.CloudAppsBuiltIn : Strings.CloudAppsNoBuiltIn));
            var app = current.Own.Of(kind);
            var id = new TextItem(Strings.CloudAppsClientId.TrimEnd(':', ' '), app?.ClientId ?? string.Empty);
            var secret = new TextItem(Strings.CloudAppsSecret.TrimEnd(':', ' '), app?.ClientSecret ?? string.Empty) { Secret = true };
            Items.Add(id);
            if (kind != DestinationKinds.Dropbox)
            {
                Items.Add(secret);
            }

            _fields[kind] = (id, secret);
        }

        Items.Add(new NoteItem(CustosEdition.IsLinux ? DesktopStrings.LinuxCloudSecretNote : Strings.CloudAppsSecretNote));
    }

    /// <summary>The applications as entered.</summary>
    internal CloudAppsFile Entered()
    {
        CloudApp? Of(string kind)
        {
            var (id, secret) = _fields[kind];
            string clientId = id.Text.Trim();
            string clientSecret = secret.Text.Trim();
            return clientId.Length == 0 ? null : new CloudApp { ClientId = clientId, ClientSecret = clientSecret.Length == 0 ? null : clientSecret };
        }

        return new CloudAppsFile { Gdrive = Of(DestinationKinds.GDrive), Yadisk = Of(DestinationKinds.YaDisk), Dropbox = Of(DestinationKinds.Dropbox) };
    }

    protected override async Task<bool> OkAsync()
    {
        var apps = Entered();
        if (apps.Gdrive is { ClientSecret: null })
        {
            ShowError(Strings.ErrorCloudSecretRequired);
            return false;
        }

        if (CustosEdition.IsLinux && apps.Yadisk is { ClientSecret: null })
        {
            ShowError(DesktopStrings.LinuxCloudYandexSecretRequired);
            return false;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.CloudAppsSave, new CloudAppsSaveRequest { Apps = apps },
            IpcJsonContext.Default.CloudAppsSaveRequest, IpcJsonContext.Default.OkResult);
        return true;
    }
}
