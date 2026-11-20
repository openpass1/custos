using Avalonia.Threading;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// Restore to a date (section 6.11): find the sets of a task (or in any folder, for example a backup disk on a new
/// computer, or on a server or a cloud drive given like a destination), pick a date, pick where to, restore. The
/// service does the work, so the window may be closed meanwhile (RestoreForm of Custos.exe).
/// </summary>
internal sealed class RestoreDialog : FormDialog
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(10);

    private readonly ServiceMonitor _monitor;
    private readonly RadioGroup _from = new();
    private readonly RadioItem _byTask;
    private readonly ChoiceItem _task;
    private readonly RadioItem _byFolder;
    private readonly TextItem _folder;
    private readonly ChoiceItem _account;
    private readonly RadioItem _byStore;
    private readonly InfoItem _store = new("cloud", Strings.RestoreStoreNone) { Indent = 30 };
    private readonly ButtonItem _chooseStore;
    private readonly TextItem _password = new(L(Strings.RestorePassword), width: 360) { Secret = true };
    private readonly ButtonItem _search;
    private readonly NoteItem _found = new(string.Empty) { IsVisible = false };
    private readonly RadioGroup _sets = new();
    private readonly List<RadioItem> _setItems = [];
    private readonly HeadingItem _whereHeading = new(Strings.RestoreWhereTo);
    private readonly RadioGroup _to = new();
    private readonly RadioItem _toOriginal;
    private readonly RadioItem _toFolder;
    private readonly TextItem _target;
    private readonly ChoiceItem _existing = new(L(Strings.RestoreExisting),
        [Strings.RestoreExistingSkip, Strings.RestoreExistingOverwrite, Strings.RestoreExistingOlder], 0, 420);

    private readonly TextItem _dumps;
    private readonly NoteItem _dumpsNote = new(Strings.RestoreDumpsNote) { IsVisible = false };
    private readonly ProgressItem _progress = new() { IsVisible = false };
    private readonly NoteItem _goesOn = new(Strings.RestoreGoesOn) { IsVisible = false };
    private readonly Avalonia.Controls.Button _stop;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<string?> _accountIds = [null];
    private List<TaskDefinition> _tasks = [];
    private TaskDestination? _storeDestination;
    private bool _running;
    private bool _polling;

    public RestoreDialog(ServiceMonitor monitor, string? taskId)
        : base(Strings.RestoreTitle, 860, sizable: true)
    {
        _monitor = monitor;
        _byTask = new RadioItem(_from, Strings.RestoreByTask, on: true);
        _task = new ChoiceItem(null, [], 0, 420) { Indent = 30 };
        _byFolder = new RadioItem(_from, Strings.RestoreByFolder);
        _folder = new TextItem(null) { Indent = 30, ActionIcon = "folder_open", ActionTip = Strings.ButtonBrowse, Action = () => BrowseAsync(_folder!) };
        _account = new ChoiceItem(L(Strings.RestoreAccount), [], 0, 420) { Indent = 30 };
        _byStore = new RadioItem(_from, Strings.RestoreByStore);
        _chooseStore = new ButtonItem("cloud", Strings.RestoreChooseStore, ChooseStoreAsync, ButtonLook.Outlined) { Indent = 30, Failed = ex => ShowError(ex.Message) };
        _search = new ButtonItem("search", Strings.RestoreSearch, SearchAsync) { Failed = ex => _found.Show(ex.Message, Tone.Error) };
        _toOriginal = new RadioItem(_to, Strings.RestoreToOriginal, on: true);
        _toFolder = new RadioItem(_to, Strings.RestoreToFolder);
        _target = new TextItem(null) { Indent = 30, ActionIcon = "folder_open", ActionTip = Strings.ButtonBrowse, Action = () => BrowseAsync(_target!) };
        _dumps = new TextItem(L(Strings.RestoreDumpsFolder)) { ActionIcon = "folder_open", ActionTip = Strings.ButtonBrowse, Action = () => BrowseAsync(_dumps!) };

        Items.Add(_byTask);
        Items.Add(_task);
        Items.Add(_byFolder);
        Items.Add(_folder);
        Items.Add(_account);
        Items.Add(_byStore);
        Items.Add(_store);
        Items.Add(_chooseStore);
        Items.Add(_password);
        Items.Add(new NoteItem(Strings.RestorePasswordNote));
        Items.Add(_search);
        Items.Add(_found);
        Items.Add(_whereHeading);
        Items.Add(_toOriginal);
        Items.Add(_toFolder);
        Items.Add(_target);
        Items.Add(_existing);
        Items.Add(_dumps);
        Items.Add(_dumpsNote);
        Items.Add(_progress);
        Items.Add(_goesOn);

        OkButton.Content = Strings.RestoreStart;
        CancelButton.Content = Strings.ButtonClose;
        _stop = AddButton(Strings.ToolStop, () => _monitor.CommandAsync(PipeProtocol.Methods.RestoreStop));
        FillChoices(taskId);

        foreach (var item in _from.Items)
        {
            item.OnChange = () =>
            {
                ClearSets();
                UpdateControls();
            };
        }

        _to.Items[0].OnChange = UpdateControls;
        _task.OnChange = ClearSets;
        _folder.OnChange = ClearSets;
        _poll.Tick += (_, _) => Poll();
        Opened += (_, _) =>
        {
            if (!_monitor.IsConnected)
            {
                return;
            }

            Poll();
            if (_byTask.On && _tasks.Count > 0)
            {
                _search.Press();
            }
        };
        Closed += (_, _) => _poll.Stop();
        UpdateControls();
    }

    /// <summary>The sets found, newest first.</summary>
    internal IReadOnlyList<RadioItem> SetItems => _setItems;

    /// <summary>Starts the restore of the chosen set; the window stays open and shows how it goes.</summary>
    protected override async Task<bool> OkAsync()
    {
        if (Chosen() is not { } set)
        {
            return false;
        }

        string? target = null;
        if (_toFolder.On)
        {
            target = _target.Value;
            if (!Path.IsPathFullyQualified(target))
            {
                _target.Error = Strings.RestoreChooseTarget;
                return false;
            }
        }

        string? dumps = null;
        if (target is null && set.Header.Databases.Count > 0 && _dumps.Value.Length > 0)
        {
            dumps = _dumps.Value;
            if (!Path.IsPathFullyQualified(dumps))
            {
                _dumps.Error = Strings.RestoreChooseTarget;
                return false;
            }
        }

        var existing = _existing.Selected switch
        {
            1 => RestoreExisting.Overwrite,
            2 => RestoreExisting.OverwriteOlder,
            _ => RestoreExisting.Skip,
        };
        string question = target is null
            ? Strings.Format(existing == RestoreExisting.Skip ? Strings.RestoreConfirmOriginal : Strings.RestoreConfirmOriginalOverwrite, set.Header.SetName)
            : Strings.Format(Strings.RestoreConfirmFolder, set.Header.SetName, target);
        if (!await Dialogs.Ask(this, question, Strings.RestoreStart, Strings.ButtonCancel))
        {
            return false;
        }

        var source = Source();
        await _monitor.RequestAsync(PipeProtocol.Methods.RestoreStart, new RestoreStartRequest
        {
            TaskId = source.TaskId,
            Folder = source.Folder,
            Destination = source.Destination,
            CredentialId = source.CredentialId,
            Password = source.Password,
            Location = set.Location,
            Where = set.Where,
            TargetFolder = target,
            DumpsFolder = dumps,
            Existing = existing,
        },
            IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.OkResult, ListTimeout);
        _running = true;
        _progress.Percent = 0;
        _progress.Text = Strings.RestoreStarting;
        _poll.Start();
        UpdateControls();
        return false;
    }

    /// <summary>Shows the sets the service found (also used by the pictures).</summary>
    internal void ShowSets(RestoreListResult result)
    {
        ClearSets();
        int at = Items.IndexOf(_found) + 1;
        foreach (var set in result.Sets)
        {
            var header = set.Header;
            string size = set.Storage == "folder" ? Sizes.Format(header.SetBytes) : Sizes.Format(set.Bytes);
            var item = new RadioItem(_sets,
                Describe.Time(header.StartedAt) + " · " + Words.BackupType(header.BackupType) + " · " + header.SetName + StorageSuffix(set),
                Strings.Format(DesktopStrings.RestoreSetHint, Describe.Number(header.SetFileCount), size, set.Where),
                on: _setItems.Count == 0)
            {
                Tag = set,
                OnChange = UpdateControls,
            };
            _setItems.Add(item);
            Items.Insert(at++, item);
        }

        string found = result.Sets.Count == 0 ? Strings.RestoreNothingFound : Strings.Format(Strings.RestoreFound, result.Sets.Count);
        _found.Show(result.Problems.Count == 0 ? found : found + Environment.NewLine + string.Join(Environment.NewLine, result.Problems.Take(5)),
            result.Problems.Count == 0 ? Tone.Plain : Tone.Error);
        UpdateControls();
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private static string StorageSuffix(RestoreSetInfo set) => set.Storage switch
    {
        "folder" => string.Empty,
        _ when set.Parts > 1 => Strings.Format(Strings.RestoreArchiveVolumes, set.Storage == ArchiveFormats.Zip ? "ZIP" : "7z", set.Parts),
        _ => Strings.Format(Strings.RestoreArchive, set.Storage == ArchiveFormats.Zip ? "ZIP" : "7z"),
    };

    private RestoreSetInfo? Chosen() => _setItems.FirstOrDefault(i => i.On)?.Tag as RestoreSetInfo;

    private void FillChoices(string? taskId)
    {
        var snapshot = _monitor.Snapshot;
        _tasks = snapshot is null ? [] : [.. snapshot.Tasks.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
        _task.Options = [.. _tasks.Select(t => t.Name)];
        int index = _tasks.FindIndex(t => Describe.SameId(t.Id, taskId));
        _task.Load(index >= 0 ? index : 0);
        if (_tasks.Count == 0)
        {
            _byFolder.Load();
        }

        var names = new List<string> { Strings.RestoreServiceAccount };
        foreach (var credential in snapshot?.Credentials.Where(c => c.Kind == CredentialKinds.Windows) ?? [])
        {
            names.Add(credential.DisplayName);
            _accountIds.Add(credential.Id);
        }

        _account.Options = names;
        _account.Load(0);
    }

    private void UpdateControls()
    {
        bool idle = !_running;
        _byTask.IsEnabled = idle && _tasks.Count > 0;
        _task.IsEnabled = idle && _byTask.On;
        _byFolder.IsEnabled = idle;
        _folder.IsEnabled = _account.IsEnabled = idle && _byFolder.On;
        _byStore.IsEnabled = idle;
        _chooseStore.IsEnabled = idle && _byStore.On;
        _password.IsEnabled = idle;
        _search.IsEnabled = idle;
        foreach (var item in _setItems)
        {
            item.IsEnabled = idle;
        }

        // A copy of a Linux server goes into a folder only, as tars and dumps (CUSTOS-remote-pull.md 4.5).
        var set = Chosen();
        bool remote = set?.Header.SourceKind == SourceKinds.Ssh;
        if (remote && _toOriginal.On)
        {
            _toFolder.Load();
        }

        bool any = _setItems.Count > 0;
        _whereHeading.IsVisible = _toOriginal.IsVisible = _toFolder.IsVisible = _target.IsVisible = _existing.IsVisible = any;
        _toOriginal.IsEnabled = idle && !remote;
        _toFolder.IsEnabled = idle;
        _target.IsEnabled = idle && _toFolder.On;
        _existing.IsEnabled = idle;

        // Dumps go into «db» of the other folder; to the original places they need a folder of their own.
        bool dumps = set is { Header.Databases.Count: > 0 };
        _dumpsNote.Text = remote ? Strings.RestoreRemoteNote : Strings.RestoreDumpsNote;
        _dumpsNote.IsVisible = dumps || remote;
        _dumps.IsVisible = dumps && _toOriginal.On;
        _dumps.IsEnabled = idle;
        OkButton.IsEnabled = idle && set is not null;
        _stop.IsVisible = _running;
        _progress.IsVisible = _goesOn.IsVisible = _running;
    }

    /// <summary>
    /// «Указать…»: a server or a cloud drive, entered like a destination of a task (on another computer: the same
    /// account and the folder of the sets). A folder of this computer or of the network goes to «Копии в папке».
    /// </summary>
    private async Task ChooseStoreAsync()
    {
        var start = _storeDestination ?? new TaskDestination { Kind = CustosEdition.IsLegacy ? DestinationKinds.Sftp : DestinationKinds.GDrive };
        var dialog = new DestinationDialog(new EditorContext(_monitor) { Owner = this }, start, isNew: _storeDestination is null, Strings.RestoreStoreTitle);
        if (!await dialog.ShowAsync(this))
        {
            return;
        }

        var chosen = dialog.Destination;
        if (DestinationKinds.IsFileSystem(chosen.Kind))
        {
            if (chosen.Kind == DestinationKinds.Usb || !Path.IsPathFullyQualified(chosen.Path))
            {
                ShowError(Strings.RestoreChooseFolder);
                return;
            }

            _folder.Text = chosen.Path;
            _account.Load(Math.Max(0, _accountIds.FindIndex(id => Describe.SameId(id, chosen.CredentialId))));
            _byFolder.On = true;
            return;
        }

        _storeDestination = chosen;
        _store.Title = TaskDescriber.Destination(chosen);
        _byStore.On = true;
        ClearSets();
    }

    private void ClearSets()
    {
        foreach (var item in _setItems)
        {
            Items.Remove(item);
        }

        _setItems.Clear();
        _found.Show(null);
        UpdateControls();
    }

    private RestoreListRequest Source() => new()
    {
        TaskId = _byTask.On && _task.Selected >= 0 && _task.Selected < _tasks.Count ? _tasks[_task.Selected].Id : null,
        Folder = _byFolder.On ? _folder.Value : null,
        Destination = _byStore.On ? _storeDestination : null,
        CredentialId = _byFolder.On ? _accountIds[Math.Max(0, _account.Selected)] : null,
        Password = _password.Text.Length > 0 ? _password.Text : null,
    };

    private async Task SearchAsync()
    {
        if (_byFolder.On && !Path.IsPathFullyQualified(_folder.Value))
        {
            _folder.Error = Strings.RestoreChooseFolder;
            return;
        }

        if (_byStore.On && _storeDestination is null)
        {
            ShowError(Strings.RestoreStoreRequired);
            return;
        }

        ClearSets();
        _found.Show(Strings.RestoreSearching, Tone.Plain);
        var result = await _monitor.RequestAsync(PipeProtocol.Methods.RestoreList, Source(),
            IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult, ListTimeout);
        ShowSets(result);
    }

    private async void Poll()
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            var status = await _monitor.RequestAsync(PipeProtocol.Methods.RestoreStatus, EmptyRequest.Instance,
                IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.RestoreStatusResult);
            await ShowStatusAsync(status);
        }
        catch (ServiceUnavailableException ex)
        {
            // Asked a few times a second: no message, just the reason under the bar.
            _progress.Text = ex.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task ShowStatusAsync(RestoreStatusResult status)
    {
        bool wasRunning = _running;
        _running = status.Running;
        if (status.Running)
        {
            _poll.Start();
            var progress = status.Progress;
            _progress.Percent = progress is null ? 0 : Math.Clamp(progress.Percent, 0, 100);
            _progress.Text = progress is null
                ? Strings.RestoreStarting
                : Strings.Format(Strings.RestoreProgressText, status.SetName, Describe.Number(progress.FilesDone), Describe.Number(progress.FilesTotal),
                    Sizes.Format(progress.BytesDone));
            UpdateControls();
            return;
        }

        _poll.Stop();
        UpdateControls();
        if (wasRunning && status.Result is { } result)
        {
            string line = Strings.Format(Strings.RestoreResultText, Words.Status(result.Status), Describe.Number(result.FilesRestored),
                Describe.Number(result.FilesSkipped), Describe.Number(result.FilesFailed));
            string text = line + (result.ErrorMessage is { } error ? Environment.NewLine + Environment.NewLine + error : string.Empty)
                + (status.LogFile is { } log ? Environment.NewLine + Environment.NewLine + Strings.Format(Strings.RestoreLogFile, log) : string.Empty);
            ShowError(result.Status == RunStatuses.Success ? null : line);
            await Dialogs.Show(this, text);
        }
    }

    private async Task BrowseAsync(TextItem box)
    {
        if (await PickFolderAsync(this, Strings.RestoreChooseFolder, box.Value) is { } folder)
        {
            box.Text = folder;
        }
    }
}
