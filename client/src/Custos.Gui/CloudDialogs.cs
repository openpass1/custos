using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using Custos.Core.Cloud;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

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
/// person. The service trades it for tokens; the window never sees them.
/// </summary>
internal sealed class CloudConnectDialog : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly string _kind;
    private readonly Credential? _existing;
    private readonly Button _open = new() { Text = Strings.CloudOpenPage, AutoSize = true, Enabled = false };
    private readonly Button _copy = new() { Text = Strings.CloudCopyLink, AutoSize = true, Enabled = false, Margin = new Padding(8, 3, 0, 3) };
    private readonly TextBox _answer = new() { Dock = DockStyle.Fill };
    private readonly Button _paste = new() { Size = new Size(30, 25), Margin = new Padding(0), AccessibleName = Strings.CloudPasteTip };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 4) };
    private readonly CancellationTokenSource _closing = new();
    private LoopbackReceiver? _receiver;
    private bool _triedReceiver;
    private CloudBeginResult? _begun;
    private bool _finishing;

    /// <param name="monitor">The connection to the service.</param>
    /// <param name="kind">gdrive, yadisk or dropbox.</param>
    /// <param name="existing">An account to connect again (its tasks keep it), or null for a new one.</param>
    public CloudConnectDialog(ServiceMonitor monitor, string kind, Credential? existing = null)
        : base(existing is null ? Strings.Format(Strings.CloudConnectTitle, CloudNames.Name(kind)) : Strings.Format(Strings.CloudReconnectTitle, existing.DisplayName),
            new Size(640, 400))
    {
        _monitor = monitor;
        _kind = kind;
        _existing = existing;
        OkButton.Text = Strings.CloudConnectButton;
        _paste.Image = Glyphs.Draw(Glyph.Paste, 16);
        Tips.SetToolTip(_paste, Strings.CloudPasteTip);

        AddWide(new Label
        {
            Text = Strings.Format(Strings.CloudConnectSteps, CloudNames.Name(kind), CloudNames.AppFolder(kind)),
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(0, 0, 0, 8),
        });
        if (existing is not null)
        {
            AddNote(Strings.Format(Strings.CloudReconnectNote, existing.User));
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        buttons.Controls.AddRange([_open, _copy]);
        AddWide(buttons);
        AddRow(Strings.CloudAnswer, _answer, _paste);
        AddWide(_status);
        EndBuild();

        _open.Click += (_, _) => OpenPage();
        _copy.Click += (_, _) => CopyLink();
        _paste.Click += (_, _) => Paste();
        FormClosed += (_, _) => StopListening();
    }

    /// <summary>The account as the service stored it, after it was connected.</summary>
    public Credential? Saved { get; private set; }

    /// <summary>Reads the text of the clipboard; the tests put their own text here.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string?> ClipboardText { get; set; } = Ui.ClipboardText;

    /// <summary>The page of the service, once the service prepared the sign-in.</summary>
    internal string? SignInUrl => _begun?.Url;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ui.Run(this, () => BeginAsync(null));
    }

    protected override void OnOk()
    {
        string answer = _answer.Text.Trim();
        if (answer.Length == 0)
        {
            Ui.ShowWarning(this, Strings.CloudAnswerRequired);
            _answer.Focus();
            return;
        }

        Ui.Run(this, () => FinishAsync(answer));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopListening();
            _closing.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Asks the service for a sign-in: a new one at the start and after a failed attempt.</summary>
    /// <param name="problem">What went wrong with the attempt before, shown in front of the new state.</param>
    private async Task BeginAsync(string? problem)
    {
        var service = CloudService.Of(_kind);
        _begun = null;
        _open.Enabled = _copy.Enabled = false;
        ShowStatus(Strings.CloudPreparing, error: false);
        if (!_triedReceiver)
        {
            // Google takes any port, the others only the one registered with the application.
            _triedReceiver = true;
            _receiver = LoopbackReceiver.TryStart(service.LoopbackPort);
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
            ShowStatus(Join(problem, ex.Message), error: true);
            return;
        }

        if (IsDisposed)
        {
            return;
        }

        _begun = begun;
        _open.Enabled = _copy.Enabled = true;
        string ready = _receiver is not null ? Strings.CloudReady
            : service.CodeOnScreen is null ? Strings.CloudReadyAddress
            : Strings.CloudReadyCode;
        ShowStatus(Join(problem, ready), error: problem is not null);
        if (_receiver is { } listening && redirect is not null)
        {
            Listen(listening, begun, redirect);
        }
    }

    /// <summary>Waits for the browser to come back to the loopback port with the answer of this sign-in.</summary>
    private async void Listen(LoopbackReceiver receiver, CloudBeginResult begun, string redirect)
    {
        string target;
        try
        {
            target = await receiver.WaitAsync(Strings.CloudBrowserDone, _closing.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
            // The window was closed, or the listening stopped.
            return;
        }

        if (IsDisposed || !ReferenceEquals(begun, _begun))
        {
            return;
        }

        Activate();
        Ui.Run(this, () => FinishAsync(redirect.TrimEnd('/') + target));
    }

    /// <summary>Gives the answer of the browser to the service, which trades it for the tokens of the account.</summary>
    private async Task FinishAsync(string answer)
    {
        if (_finishing || _begun is not { } begun)
        {
            return;
        }

        _finishing = true;
        SetBusy(true);
        _open.Enabled = _copy.Enabled = false;
        ShowStatus(Strings.CloudConnecting, error: false);
        try
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.CloudFinish, new CloudFinishRequest { State = begun.State, Answer = answer },
                IpcJsonContext.Default.CloudFinishRequest, IpcJsonContext.Default.CredentialSaveResult, TimeSpan.FromMinutes(3));
            Saved = result.Credential;
            _answer.Clear();
            DialogResult = DialogResult.OK;
        }
        catch (ServiceUnavailableException ex)
        {
            if (!IsDisposed)
            {
                // The service forgets a sign-in once its answer came: a new attempt needs a new page.
                _answer.Clear();
                SetBusy(false);
                await BeginAsync(ex.Message + " " + Strings.CloudTryAgain);
            }
        }
        finally
        {
            _finishing = false;
            if (!IsDisposed)
            {
                SetBusy(false);
            }
        }
    }

    private void OpenPage()
    {
        if (_begun is not { } begun)
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(begun.Url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowStatus(Strings.Format(Strings.CloudBrowserFailed, ex.Message), error: true);
        }
    }

    private void CopyLink()
    {
        if (_begun is not { } begun)
        {
            return;
        }

        try
        {
            Clipboard.SetText(begun.Url);
            ShowStatus(Strings.CloudLinkCopied, error: false);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            ShowStatus(ex.Message, error: true);
        }
    }

    /// <summary>The first line of the clipboard with text goes into the field.</summary>
    internal void Paste()
    {
        string? line = ClipboardText()?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrEmpty(line))
        {
            _answer.Text = line;
            _answer.SelectionStart = line.Length;
        }

        _answer.Focus();
    }

    private void ShowStatus(string text, bool error)
    {
        if (IsDisposed)
        {
            return;
        }

        _status.Text = text;
        _status.ForeColor = error ? Glyphs.ErrorText : SystemColors.GrayText;
    }

    private void StopListening()
    {
        if (!_closing.IsCancellationRequested)
        {
            _closing.Cancel();
        }

        _receiver?.Dispose();
        _receiver = null;
    }

    private static string Join(string? first, string second) => first is null ? second : first + Environment.NewLine + second;
}

/// <summary>
/// «Свои приложения» of the cloud drives (settings, «Облачные диски», section 6.3): the client id and secret of an
/// application the administrator registered at a service himself. Local to this computer.
/// </summary>
internal sealed class CloudAppsDialog : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly Dictionary<string, (TextBox Id, TextBox Secret)> _fields = [];

    public CloudAppsDialog(ServiceMonitor monitor, CloudAppsResult current)
        : base(Strings.CloudAppsTitle, new Size(640, 520))
    {
        _monitor = monitor;
        AddNote(Strings.CloudAppsNote);
        foreach (string kind in CloudNames.Kinds)
        {
            var heading = new Label { Text = CloudNames.Name(kind), AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
            heading.Font = new Font(heading.Font, FontStyle.Bold);
            heading.Disposed += (_, _) => heading.Font.Dispose();
            AddWide(heading);
            AddNote(current.BuiltIn.Contains(kind) ? Strings.CloudAppsBuiltIn : Strings.CloudAppsNoBuiltIn);
            var app = current.Own.Of(kind);
            var id = new TextBox { Dock = DockStyle.Fill, Text = app?.ClientId ?? string.Empty };
            var secret = new TextBox { Dock = DockStyle.Fill, Text = app?.ClientSecret ?? string.Empty };
            AddRow(Strings.CloudAppsClientId, id);
            if (kind != DestinationKinds.Dropbox)
            {
                AddRow(Strings.CloudAppsSecret, secret);
            }

            _fields[kind] = (id, secret);
        }

        AddNote(Strings.CloudAppsSecretNote);
        MakeSizable(new Size(560, 420));
        EndBuild();
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

    protected override void OnOk()
    {
        var apps = Entered();
        if (apps.Gdrive is { ClientSecret: null })
        {
            Ui.ShowWarning(this, Strings.ErrorCloudSecretRequired);
            return;
        }

        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.CloudAppsSave, new CloudAppsSaveRequest { Apps = apps },
                    IpcJsonContext.Default.CloudAppsSaveRequest, IpcJsonContext.Default.OkResult);
                DialogResult = DialogResult.OK;
            }
            finally
            {
                SetBusy(false);
            }
        });
    }
}
