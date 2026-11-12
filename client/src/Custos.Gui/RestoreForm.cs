using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// Restore to a date (section 6.11): find the sets of a task (or in any folder, for example a backup disk on a new
/// computer), pick a date, pick where to, restore. The service does the work, so the window may be closed meanwhile.
/// </summary>
internal sealed class RestoreForm : Form
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(10);

    private readonly ServiceMonitor _monitor;
    private readonly RadioButton _byTask = new() { Text = Strings.RestoreByTask, AutoSize = true, Checked = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _task = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly RadioButton _byFolder = new() { Text = Strings.RestoreByFolder, AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly Button _browseFolder = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Button _search = new() { Text = Strings.RestoreSearch, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly ListView _sets = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Label _found = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 4) };
    private readonly RadioButton _toOriginal = new() { Text = Strings.RestoreToOriginal, AutoSize = true, Checked = true, Anchor = AnchorStyles.Left };
    private readonly RadioButton _toFolder = new() { Text = Strings.RestoreToFolder, AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _target = new() { Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _browseTarget = new() { Text = Strings.ButtonBrowse, AutoSize = true, Enabled = false };
    private readonly ComboBox _existing = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Label _dumpsLabel = new() { Text = Strings.RestoreDumpsFolder, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 6) };
    private readonly TextBox _dumps = new() { Dock = DockStyle.Fill };
    private readonly Button _browseDumps = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly Label _dumpsNote = new() { Text = Strings.RestoreDumpsNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Height = 18, Visible = false };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };
    private readonly Button _start = new() { Text = Strings.RestoreStart, AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    private readonly Button _stop = new() { Text = Strings.ToolStop, AutoSize = true, MinimumSize = new Size(88, 0), Visible = false };
    private readonly Button _close = new() { Text = Strings.ButtonClose, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };
    private readonly List<string?> _accountIds = [null];
    private List<TaskDefinition> _tasks = [];
    private bool _running;
    private bool _polling;

    public RestoreForm(ServiceMonitor monitor, string? taskId)
    {
        _monitor = monitor;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.RestoreTitle;
        Icon = AppIcons.App();
        Size = new Size(900, 680);
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        CancelButton = _close;

        var where = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 12, 12, 0) };
        where.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        where.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        where.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        where.Controls.Add(_byTask, 0, 0);
        where.Controls.Add(_task, 1, 0);
        where.Controls.Add(_byFolder, 0, 1);
        where.Controls.Add(_folder, 1, 1);
        where.Controls.Add(_browseFolder, 2, 1);
        where.Controls.Add(new Label { Text = Strings.RestoreAccount, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(20, 6, 8, 6) }, 0, 2);
        where.Controls.Add(_account, 1, 2);
        where.Controls.Add(new Label { Text = Strings.RestorePassword, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(20, 6, 8, 6) }, 0, 3);
        where.Controls.Add(_password, 1, 3);
        where.Controls.Add(_search, 2, 3);
        var note = new Label { Text = Strings.RestorePasswordNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 0, 0, 6) };
        where.Controls.Add(note, 0, 4);
        where.SetColumnSpan(note, 3);

        _sets.Columns.Add(Strings.ColumnStarted, 140);
        _sets.Columns.Add(Strings.ColumnType, 130);
        _sets.Columns.Add(Strings.ColumnSet, 260);
        _sets.Columns.Add(Strings.ColumnFiles, 80, HorizontalAlignment.Right);
        _sets.Columns.Add(Strings.ColumnSize, 90, HorizontalAlignment.Right);
        _sets.Columns.Add(Strings.ColumnWhere, 200);
        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 6, 12, 0) };
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        middle.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        middle.Controls.Add(_sets, 0, 0);
        middle.Controls.Add(_found, 0, 1);

        _existing.Items.AddRange([Strings.RestoreExistingSkip, Strings.RestoreExistingOverwrite, Strings.RestoreExistingOlder]);
        _existing.SelectedIndex = 0;
        var to = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 6, 12, 0) };
        to.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        to.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        to.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var heading = new Label { Text = Strings.RestoreWhereTo, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        heading.Font = new Font(heading.Font, FontStyle.Bold);
        to.Controls.Add(heading, 0, 0);
        to.SetColumnSpan(heading, 3);
        to.Controls.Add(_toOriginal, 0, 1);
        to.SetColumnSpan(_toOriginal, 3);
        to.Controls.Add(_toFolder, 0, 2);
        to.Controls.Add(_target, 1, 2);
        to.Controls.Add(_browseTarget, 2, 2);
        to.Controls.Add(new Label { Text = Strings.RestoreExisting, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 6) }, 0, 3);
        to.Controls.Add(_existing, 1, 3);
        to.Controls.Add(_dumpsLabel, 0, 4);
        to.Controls.Add(_dumps, 1, 4);
        to.Controls.Add(_browseDumps, 2, 4);
        to.Controls.Add(_dumpsNote, 0, 5);
        to.SetColumnSpan(_dumpsNote, 3);
        to.Controls.Add(_bar, 0, 6);
        to.SetColumnSpan(_bar, 3);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(12, 8, 12, 12) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_start, 1, 0);
        bottom.Controls.Add(_stop, 2, 0);
        bottom.Controls.Add(_close, 3, 0);

        // Docking goes from the last control added to the first.
        Controls.Add(middle);
        Controls.Add(where);
        Controls.Add(to);
        Controls.Add(bottom);
        ResumeLayout(performLayout: true);

        FillChoices(taskId);
        _byTask.CheckedChanged += (_, _) => UpdateControls();
        _byFolder.CheckedChanged += (_, _) => UpdateControls();
        _toFolder.CheckedChanged += (_, _) => UpdateControls();
        _sets.SelectedIndexChanged += (_, _) => UpdateControls();
        _task.SelectedIndexChanged += (_, _) => ClearSets();
        _folder.TextChanged += (_, _) => ClearSets();
        _browseFolder.Click += (_, _) => Browse(_folder);
        _browseTarget.Click += (_, _) => Browse(_target);
        _browseDumps.Click += (_, _) => Browse(_dumps);
        _search.Click += (_, _) => Search();
        _start.Click += (_, _) => Start();
        _stop.Click += (_, _) => Ui.Run(this, () => _monitor.CommandAsync(PipeProtocol.Methods.RestoreStop));
        _poll.Tick += (_, _) => Poll();
        Shown += (_, _) =>
        {
            if (!_monitor.IsConnected)
            {
                return;
            }

            Poll();
            if (_byTask.Checked && _task.SelectedIndex >= 0)
            {
                Search();
            }
        };
        UpdateControls();
    }

    /// <summary>The sets found, newest first.</summary>
    internal ListView SetList => _sets;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running && e.CloseReason == CloseReason.UserClosing)
        {
            Ui.ShowInfo(this, Strings.RestoreGoesOn);
        }

        _poll.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
        }

        base.Dispose(disposing);
    }

    private void FillChoices(string? taskId)
    {
        var snapshot = _monitor.Snapshot;
        _tasks = snapshot is null ? [] : [.. snapshot.Tasks.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
        foreach (var task in _tasks)
        {
            _task.Items.Add(task.Name);
        }

        int index = _tasks.FindIndex(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        _task.SelectedIndex = index >= 0 ? index : _tasks.Count > 0 ? 0 : -1;
        if (_tasks.Count == 0)
        {
            _byFolder.Checked = true;
        }

        _account.Items.Add(Strings.RestoreServiceAccount);
        foreach (var credential in snapshot?.Credentials.Where(c => c.Kind == CredentialKinds.Windows) ?? [])
        {
            _account.Items.Add(credential.DisplayName);
            _accountIds.Add(credential.Id);
        }

        _account.SelectedIndex = 0;
    }

    private void UpdateControls()
    {
        bool idle = !_running;
        _task.Enabled = idle && _byTask.Checked;
        _folder.Enabled = idle && _byFolder.Checked;
        _browseFolder.Enabled = idle && _byFolder.Checked;
        _account.Enabled = idle && _byFolder.Checked;
        _byTask.Enabled = idle && _tasks.Count > 0;
        _byFolder.Enabled = idle;
        _password.Enabled = idle;
        _search.Enabled = idle;
        _sets.Enabled = idle;
        // A copy of a Linux server goes into a folder only, as tars and dumps (CUSTOS-remote-pull.md 4.5).
        bool remote = _sets.SelectedItems.Count == 1 && _sets.SelectedItems[0].Tag is RestoreSetInfo { Header.SourceKind: SourceKinds.Ssh };
        if (remote && _toOriginal.Checked)
        {
            _toFolder.Checked = true;
        }

        _toOriginal.Enabled = idle && !remote;
        _toFolder.Enabled = idle;
        _target.Enabled = idle && _toFolder.Checked;
        _browseTarget.Enabled = idle && _toFolder.Checked;
        _existing.Enabled = idle;

        // Dumps go into «db» of the other folder; to the original places they need a folder of their own.
        bool dumps = _sets.SelectedItems.Count == 1 && _sets.SelectedItems[0].Tag is RestoreSetInfo { Header.Databases.Count: > 0 };
        _dumpsNote.Text = remote ? Strings.RestoreRemoteNote : Strings.RestoreDumpsNote;
        _dumpsNote.Visible = dumps || remote;
        _dumpsLabel.Visible = _dumps.Visible = _browseDumps.Visible = dumps && _toOriginal.Checked;
        _dumps.Enabled = _browseDumps.Enabled = idle;
        _start.Enabled = idle && _sets.SelectedItems.Count == 1;
        _stop.Visible = _running;
        _bar.Visible = _running;
    }

    private void ClearSets()
    {
        _sets.Items.Clear();
        _found.Text = string.Empty;
        UpdateControls();
    }

    private RestoreListRequest Source() => new()
    {
        TaskId = _byTask.Checked && _task.SelectedIndex >= 0 ? _tasks[_task.SelectedIndex].Id : null,
        Folder = _byFolder.Checked ? _folder.Text.Trim() : null,
        CredentialId = _byFolder.Checked ? _accountIds[Math.Max(0, _account.SelectedIndex)] : null,
        Password = _password.Text.Length > 0 ? _password.Text : null,
    };

    private void Search()
    {
        if (_byFolder.Checked && !Path.IsPathFullyQualified(_folder.Text.Trim()))
        {
            Ui.ShowWarning(this, Strings.RestoreChooseFolder);
            return;
        }

        ClearSets();
        _found.Text = Strings.RestoreSearching;
        _search.Enabled = false;
        UseWaitCursor = true;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.RestoreList, Source(),
                    IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult, ListTimeout);
                ShowSets(result);
            }
            catch (ServiceUnavailableException)
            {
                _found.Text = string.Empty;
                throw;
            }
            finally
            {
                UseWaitCursor = false;
                UpdateControls();
            }
        });
    }

    /// <summary>Shows the sets the service found (also used by the window tests).</summary>
    internal void ShowSets(RestoreListResult result)
    {
        _sets.BeginUpdate();
        _sets.Items.Clear();
        foreach (var set in result.Sets)
        {
            var header = set.Header;
            string size = set.Storage == "folder" ? Sizes.Format(header.SetBytes) : Sizes.Format(set.Bytes);
            var item = new ListViewItem(
            [
                Ui.Time(header.StartedAt),
                Words.BackupType(header.BackupType),
                header.SetName + StorageSuffix(set),
                Ui.Number(header.SetFileCount),
                size,
                set.Where,
            ])
            { Tag = set };
            _sets.Items.Add(item);
        }

        _sets.EndUpdate();
        string found = result.Sets.Count == 0 ? Strings.RestoreNothingFound : Strings.Format(Strings.RestoreFound, result.Sets.Count);
        _found.Text = result.Problems.Count == 0 ? found : found + Environment.NewLine + string.Join(Environment.NewLine, result.Problems.Take(5));
        _found.ForeColor = result.Problems.Count == 0 ? SystemColors.GrayText : Color.Firebrick;
        if (_sets.Items.Count > 0)
        {
            _sets.Items[0].Selected = true;
        }
    }

    private static string StorageSuffix(RestoreSetInfo set) => set.Storage switch
    {
        "folder" => string.Empty,
        _ when set.Parts > 1 => Strings.Format(Strings.RestoreArchiveVolumes, set.Storage == ArchiveFormats.Zip ? "ZIP" : "7z", set.Parts),
        _ => Strings.Format(Strings.RestoreArchive, set.Storage == ArchiveFormats.Zip ? "ZIP" : "7z"),
    };

    private void Start()
    {
        if (_sets.SelectedItems.Count != 1 || _sets.SelectedItems[0].Tag is not RestoreSetInfo set)
        {
            return;
        }

        string? target = null;
        if (_toFolder.Checked)
        {
            target = _target.Text.Trim();
            if (!Path.IsPathFullyQualified(target))
            {
                Ui.ShowWarning(this, Strings.RestoreChooseTarget);
                return;
            }
        }

        string? dumps = null;
        if (target is null && set.Header.Databases.Count > 0 && _dumps.Text.Trim().Length > 0)
        {
            dumps = _dumps.Text.Trim();
            if (!Path.IsPathFullyQualified(dumps))
            {
                Ui.ShowWarning(this, Strings.RestoreChooseTarget);
                return;
            }
        }

        var existing = _existing.SelectedIndex switch
        {
            1 => RestoreExisting.Overwrite,
            2 => RestoreExisting.OverwriteOlder,
            _ => RestoreExisting.Skip,
        };
        string question = target is null
            ? Strings.Format(existing == RestoreExisting.Skip ? Strings.RestoreConfirmOriginal : Strings.RestoreConfirmOriginalOverwrite, set.Header.SetName)
            : Strings.Format(Strings.RestoreConfirmFolder, set.Header.SetName, target);
        if (!Ui.Confirm(this, question))
        {
            return;
        }

        var source = Source();
        var request = new RestoreStartRequest
        {
            TaskId = source.TaskId,
            Folder = source.Folder,
            CredentialId = source.CredentialId,
            Password = source.Password,
            Location = set.Location,
            TargetFolder = target,
            DumpsFolder = dumps,
            Existing = existing,
        };
        _start.Enabled = false;
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.RestoreStart, request,
                    IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.OkResult, ListTimeout);
                _running = true;
                _status.Text = Strings.RestoreStarting;
                _poll.Start();
            }
            finally
            {
                UpdateControls();
            }
        });
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
            if (!IsDisposed)
            {
                ShowStatus(status);
            }
        }
        catch (ServiceUnavailableException ex)
        {
            // Asked a few times a second: no message box, just the reason in the status line.
            if (!IsDisposed)
            {
                _status.Text = ex.Message;
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowStatus(RestoreStatusResult status)
    {
        bool wasRunning = _running;
        _running = status.Running;
        if (status.Running)
        {
            _poll.Start();
            var progress = status.Progress;
            _bar.Value = progress is null ? 0 : (int)Math.Clamp(progress.Percent, 0, 100);
            _status.Text = progress is null
                ? Strings.RestoreStarting
                : Strings.Format(Strings.RestoreProgressText, status.SetName, Ui.Number(progress.FilesDone), Ui.Number(progress.FilesTotal), Sizes.Format(progress.BytesDone));
        }
        else
        {
            _poll.Stop();
            if (wasRunning && status.Result is { } result)
            {
                _status.Text = Strings.Format(Strings.RestoreResultText, Words.Status(result.Status), Ui.Number(result.FilesRestored),
                    Ui.Number(result.FilesSkipped), Ui.Number(result.FilesFailed));
                string text = _status.Text + (result.ErrorMessage is { } error ? Environment.NewLine + Environment.NewLine + error : string.Empty)
                    + (status.LogFile is { } log ? Environment.NewLine + Environment.NewLine + Strings.Format(Strings.RestoreLogFile, log) : string.Empty);
                if (result.Status == RunStatuses.Success)
                {
                    Ui.ShowInfo(this, text);
                }
                else
                {
                    Ui.ShowWarning(this, text);
                }
            }
        }

        UpdateControls();
    }

    private void Browse(TextBox box)
    {
        using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = Strings.RestoreChooseFolder };
        if (Directory.Exists(box.Text))
        {
            dialog.InitialDirectory = box.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = dialog.SelectedPath;
        }
    }
}
