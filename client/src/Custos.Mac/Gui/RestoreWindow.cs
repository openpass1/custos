using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>
/// «Восстановить» (section 7): the backups of a task or of a folder, one date of them back into the original places
/// or into another folder. The service restores; the window only follows, and may be closed meanwhile.
/// </summary>
internal sealed class RestoreWindow : Window
{
    private readonly ServiceMonitor _monitor;
    private readonly RadioButton _byTask;
    private readonly RadioButton _byFolder;
    private readonly ComboBox _task;
    private readonly TextBox _folder = Ui.TextBox(null, "/Volumes/BACKUP/CUSTOS");
    private readonly TextBox _password = Ui.TextBox();
    private readonly ListBox _sets = Ui.List(200);
    private readonly TextBlock _found = Ui.Note(string.Empty);
    private readonly RadioButton _toOriginal;
    private readonly RadioButton _toFolder;
    private readonly TextBox _target = Ui.TextBox(null, "/Users/Shared/Restored");
    private readonly ComboBox _existing;
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressLine _bar = new() { Height = 8, IsVisible = false };
    private readonly Button _start;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<RestoreSetInfo> _found_sets = [];

    public RestoreWindow(ServiceMonitor monitor, string? taskId)
    {
        _monitor = monitor;
        Title = Strings.RestoreTitle;
        Width = 820;
        Height = 760;
        MinWidth = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var tasks = monitor.Snapshot?.Tasks ?? [];
        _byTask = Ui.Radio("from", Strings.RestoreByTask, true);
        _byFolder = Ui.Radio("from", Strings.RestoreByFolder, false);
        _task = Ui.Choice(tasks.Select(t => (t.Id, t.Name)), taskId ?? tasks.FirstOrDefault()?.Id, 420);
        _password.PasswordChar = '•';
        _toOriginal = Ui.Radio("to", Strings.RestoreToOriginal, false);
        _toFolder = Ui.Radio("to", Strings.RestoreToFolder, true);
        _existing = Ui.Choice([(nameof(RestoreExisting.Skip), Strings.RestoreExistingSkip), (nameof(RestoreExisting.Overwrite), Strings.RestoreExistingOverwrite),
            (nameof(RestoreExisting.OverwriteOlder), Strings.RestoreExistingOlder)], nameof(RestoreExisting.Skip), 320);
        var browseFolder = Ui.Text(Strings.ButtonBrowse, () => { });
        browseFolder.OnClick(async () => _folder.Text = await PickAsync() ?? _folder.Text);
        var browseTarget = Ui.Text(Strings.ButtonBrowse, () => { });
        browseTarget.OnClick(async () => _target.Text = await PickAsync() ?? _target.Text);
        var search = Ui.Outlined(Strings.RestoreSearch, () => { });
        search.OnClick(() => SearchAsync(search), ex => _found.Text = ex.Message);
        _start = Ui.Filled(Strings.RestoreStart, () => { });
        _start.OnClick(StartAsync, ex => _state.Text = ex.Message);
        _start.IsEnabled = false;
        _sets.SelectionChanged += (_, _) => _start.IsEnabled = _sets.SelectedIndex >= 0;
        _folder.Width = _target.Width = 420;

        var body = Ui.Page(
            Ui.Title(Strings.RestoreTitle),
            _byTask,
            _task,
            _byFolder,
            Ui.Horizontal(_folder, browseFolder),
            Ui.Row(Strings.RestorePassword, _password, Strings.RestorePasswordNote, 170),
            Ui.Horizontal(search),
            _found,
            _sets,
            Ui.Heading(Strings.RestoreWhereTo),
            _toOriginal,
            _toFolder,
            Ui.Horizontal(_target, browseTarget),
            Ui.Row(Strings.RestoreExisting, _existing, labelWidth: 220),
            _bar,
            _state,
            Ui.Horizontal(Ui.Text(Strings.ButtonClose, Close), _start));
        Content = body;
        _poll.Tick += (_, _) => Safe.Run(PollAsync);
        Closed += (_, _) => _poll.Stop();
        Opened += (_, _) => Safe.Run(PollAsync);
    }

    private async Task<string?> PickAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.RestoreChooseFolder });
        return (folders.Count > 0 ? folders[0].TryGetLocalPath() : null);
    }

    private T Source<T>(T request)
        where T : RestoreSource
    {
        if (_byTask.IsChecked == true)
        {
            request.TaskId = Ui.Read(_task);
        }
        else
        {
            request.Folder = (_folder.Text ?? string.Empty).Trim();
        }

        request.Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text;
        return request;
    }

    private async Task SearchAsync(Button search)
    {
        search.IsEnabled = false;
        _found.Text = Strings.RestoreSearching;
        _sets.ItemsSource = null;
        try
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.RestoreList, Source(new RestoreListRequest()),
                IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult, TimeSpan.FromMinutes(5));
            _found_sets = result.Sets;
            _sets.ItemsSource = result.Sets.Select(Describe).ToList();
            string text = result.Sets.Count == 0 ? Strings.RestoreNothingFound : Strings.Format(Strings.RestoreFound, result.Sets.Count);
            _found.Text = result.Problems.Count == 0 ? text : text + "\n" + string.Join("\n", result.Problems);
        }
        finally
        {
            search.IsEnabled = true;
        }
    }

    private static string Describe(RestoreSetInfo set)
    {
        string storage = set.Storage == "folder" ? string.Empty
            : set.Parts > 1 ? Strings.Format(Strings.RestoreArchiveVolumes, set.Storage == ArchiveFormats.Zip ? "ZIP" : "7z", set.Parts)
            : Strings.Format(Strings.RestoreArchive, set.Storage == ArchiveFormats.Zip ? "ZIP" : "7z");
        return $"{Mac.Gui.Describe.Time(set.Header.StartedAt)} · {Words.BackupType(set.Header.BackupType)} · {set.Header.TaskName}{storage} — {set.Where}";
    }

    private async Task StartAsync()
    {
        int index = _sets.SelectedIndex;
        if (index < 0 || index >= _found_sets.Count)
        {
            return;
        }

        var set = _found_sets[index];
        string? target = _toFolder.IsChecked == true ? (_target.Text ?? string.Empty).Trim() : null;
        if (target is { Length: 0 })
        {
            _state.Text = Strings.RestoreChooseTarget;
            return;
        }

        var existing = Enum.Parse<RestoreExisting>(Ui.Read(_existing));
        string question = target is null
            ? Strings.Format(existing == RestoreExisting.Skip ? Strings.RestoreConfirmOriginal : Strings.RestoreConfirmOriginalOverwrite, set.Header.SetName)
            : Strings.Format(Strings.RestoreConfirmFolder, set.Header.SetName, target);
        if (!await Dialogs.Ask(this, question, Strings.RestoreStart))
        {
            return;
        }

        var request = Source(new RestoreStartRequest { Location = set.Location, Where = set.Where, TargetFolder = target, Existing = existing });
        _start.IsEnabled = false;
        _state.Text = Strings.RestoreStarting;
        await _monitor.RequestAsync(PipeProtocol.Methods.RestoreStart, request, IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.OkResult,
            TimeSpan.FromMinutes(2));
        _poll.Start();
    }

    private async Task PollAsync()
    {
        var status = await _monitor.RequestAsync(PipeProtocol.Methods.RestoreStatus, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest,
            IpcJsonContext.Default.RestoreStatusResult);
        if (status.Running && status.Progress is { } progress)
        {
            _poll.Start();
            _bar.IsVisible = true;
            _bar.Value = progress.Percent;
            _bar.Live = true;
            _state.Text = Strings.Format(Strings.RestoreProgressText, status.SetName, Mac.Gui.Describe.Number(progress.FilesDone),
                Mac.Gui.Describe.Number(progress.FilesTotal), Sizes.Format(progress.BytesDone)) + "\n" + Strings.RestoreGoesOn;
            _start.IsEnabled = false;
        }
        else if (status.Result is { } result && _poll.IsEnabled)
        {
            _poll.Stop();
            _bar.Live = false;
            _bar.Value = 100;
            _state.Text = Strings.Format(Strings.RestoreResultText, Words.Status(result.Status), result.FilesRestored, result.FilesSkipped, result.FilesFailed)
                + (result.ErrorMessage is { } error ? "\n" + error : string.Empty)
                + (status.LogFile is { } log ? "\n" + Strings.Format(Strings.RestoreLogFile, log) : string.Empty);
            _start.IsEnabled = _sets.SelectedIndex >= 0;
        }
    }
}
