using System.Globalization;
using System.Text;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Core.Transfer;

namespace Custos.Gui;

/// <summary>
/// Export of tasks to a file (section 9a of the spec): the tasks to put in, and whether their passwords and keys go
/// along, encrypted with an export password. The service makes the file (only it can read the passwords); this window
/// saves it where the person chooses, as that person.
/// </summary>
internal sealed class TaskExportForm : Form
{
    private readonly ServiceMonitor _monitor;
    private readonly ListView _tasks = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _checkAll = new() { Text = Strings.ExportCheckAll, AutoSize = true };
    private readonly Button _checkNone = new() { Text = Strings.ExportCheckNone, AutoSize = true };
    private readonly Label _chosen = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 6, 0, 6) };
    private readonly CheckBox _include = new() { Text = Strings.ExportIncludeSecrets, AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 220 };
    private readonly TextBox _repeat = new() { UseSystemPasswordChar = true, Width = 220 };
    private readonly CheckBox _show = new() { Text = Strings.ShowPassword, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 3, 0, 3) };
    private readonly Label _counts = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
    private readonly Label _note = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4) };
    private readonly Button _save = new() { Text = Strings.ExportSave, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly Button _cancel = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
    private bool _filling;

    public TaskExportForm(ServiceMonitor monitor)
    {
        _monitor = monitor;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.ExportTitle;
        Icon = AppIcons.App();
        Size = new Size(820, 640);
        MinimumSize = new Size(620, 520);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        CancelButton = _cancel;

        var heading = new Label { Text = Strings.ExportTasksHeading, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 6) };
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        listButtons.Controls.AddRange([_checkAll, _checkNone, _chosen]);
        _tasks.Columns.Add(Strings.ColumnTask, 260);
        _tasks.Columns.Add(Strings.ColumnType, 120);
        _tasks.Columns.Add(Strings.ColumnSchedule, 260);
        _tasks.Columns.Add(Strings.ColumnEnabled, 90);
        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4) };
        middle.Controls.Add(_tasks);
        middle.Controls.Add(listButtons);
        middle.Controls.Add(heading);

        var passwords = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(20, 0, 0, 0) };
        passwords.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        passwords.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        passwords.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        passwords.Controls.Add(Caption(Strings.ExportPassword), 0, 0);
        passwords.Controls.Add(_password, 1, 0);
        passwords.Controls.Add(Caption(Strings.ArchivePasswordRepeat), 0, 1);
        passwords.Controls.Add(_repeat, 1, 1);
        passwords.Controls.Add(_show, 2, 1);

        // Passwords and keys: a table that sizes itself to its rows, so the list above takes the rest of the window.
        var secrets = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 4, 12, 0) };
        secrets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        secrets.Controls.Add(Ui.Heading(Strings.ExportSecretsGroup, Font));
        secrets.Controls.Add(_include);
        secrets.Controls.Add(passwords);
        secrets.Controls.Add(_counts);
        secrets.Controls.Add(_note);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 8, 12, 12) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(new Label { AutoSize = true }, 0, 0);
        bottom.Controls.Add(_save, 1, 0);
        bottom.Controls.Add(_cancel, 2, 0);

        // Docking goes from the last control added to the first.
        Controls.Add(middle);
        Controls.Add(secrets);
        Controls.Add(bottom);
        ResumeLayout(performLayout: true);

        // The list is filled once its window exists: a ListView that gets its window later checks its items again one by
        // one, and its item list is not complete while it does.
        Load += (_, _) =>
        {
            FillTasks();
            UpdateState();
        };
        _checkAll.Click += (_, _) => CheckAll(true);
        _checkNone.Click += (_, _) => CheckAll(false);
        _tasks.ItemChecked += (_, _) =>
        {
            if (!_filling)
            {
                UpdateState();
            }
        };
        _include.CheckedChanged += (_, _) => UpdateState();
        _show.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_show.Checked;
            _repeat.UseSystemPasswordChar = !_show.Checked;
        };
        _save.Click += (_, _) => Save();
        Resize += (_, _) => WrapNote();
        UpdateState();
    }

    /// <summary>The tasks with their check boxes (window tests).</summary>
    internal ListView TaskList => _tasks;

    /// <summary>«Put passwords and keys into the file» (window tests).</summary>
    internal CheckBox IncludeSecrets => _include;

    /// <summary>The export password fields (window tests).</summary>
    internal TextBox PasswordBox => _password;

    internal TextBox RepeatBox => _repeat;

    /// <summary>The line with the counts of secrets in the chosen tasks (window tests).</summary>
    internal string CountsText => _counts.Text;

    /// <summary>The name the save dialog offers: <c>custos-tasks-PC-2026-11-13.json</c>.</summary>
    internal static string DefaultFileName(string computer, DateTime today)
    {
        var safe = new StringBuilder();
        foreach (char c in computer)
        {
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '-' : c);
        }

        return string.Create(CultureInfo.InvariantCulture, $"custos-tasks-{(safe.Length == 0 ? "pc" : safe.ToString())}-{today:yyyy-MM-dd}.json");
    }

    /// <summary>What the checks of the window find wrong before saving; null when it can go.</summary>
    internal string? Problem()
    {
        if (Chosen().Count == 0)
        {
            return Strings.ExportNothingChosen;
        }

        if (!_include.Checked)
        {
            return null;
        }

        if (_password.Text.Length < TaskExport.MinPasswordLength)
        {
            return Messages.Format(Messages.ExportPasswordTooShort, TaskExport.MinPasswordLength);
        }

        return string.Equals(_password.Text, _repeat.Text, StringComparison.Ordinal) ? null : Strings.ArchivePasswordMismatch;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _password.Clear();
            _repeat.Clear();
        }

        base.Dispose(disposing);
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };

    private void FillTasks()
    {
        _filling = true;
        _tasks.BeginUpdate();
        foreach (var task in _monitor.Snapshot?.Tasks ?? [])
        {
            _tasks.Items.Add(new ListViewItem(
            [
                task.Name,
                Words.BackupType(task.BackupType),
                TaskDescriber.ScheduleWithCatchUp(task.Schedule),
                task.Enabled ? Strings.Yes : Strings.No,
            ])
            {
                Tag = task,
                Checked = true,
            });
        }

        _tasks.EndUpdate();
        _filling = false;
    }

    private void CheckAll(bool value)
    {
        _filling = true;
        foreach (ListViewItem item in _tasks.Items)
        {
            item.Checked = value;
        }

        _filling = false;
        UpdateState();
    }

    private List<TaskDefinition> Chosen() => [.. _tasks.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => i.Tag).OfType<TaskDefinition>()];

    private void UpdateState()
    {
        var chosen = Chosen();
        _chosen.Text = Strings.Format(Strings.ExportChosen, chosen.Count, _tasks.Items.Count);
        _password.Enabled = _include.Checked;
        _repeat.Enabled = _include.Checked;
        _show.Enabled = _include.Checked;

        // What the chosen tasks hold that is secret: so the person sees what a file without passwords leaves behind.
        var used = TaskExport.UsedCredentialIds(chosen);
        var accounts = (_monitor.Snapshot?.Credentials ?? []).Where(c => used.Contains(c.Id) && c.HasSecret).ToList();
        int keys = accounts.Count(c => c.Kind == CredentialKinds.SshKey);
        int clouds = accounts.Count(c => CredentialKinds.IsCloud(c.Kind));
        _counts.Text = Strings.Format(Strings.ExportSecretsCount, accounts.Count - keys - clouds, keys, clouds);
        _note.Text = _include.Checked ? Strings.ExportSecretsNote : Strings.ExportNoSecretsNote;
        WrapNote();
        _save.Enabled = chosen.Count > 0;
    }

    private void WrapNote()
    {
        int width = Math.Max(200, ClientSize.Width - LogicalToDeviceUnits(80));
        _note.MaximumSize = new Size(width, 0);
        _counts.MaximumSize = new Size(width, 0);
    }

    private void Save()
    {
        if (Problem() is { } problem)
        {
            Ui.ShowWarning(this, problem);
            return;
        }

        string computer = _monitor.Snapshot?.ComputerName ?? Environment.MachineName;
        using var dialog = new SaveFileDialog
        {
            Filter = Strings.ExportFilter,
            FileName = DefaultFileName(computer, DateTime.Now),
            Title = Strings.ExportTitle,
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string path = dialog.FileName;
        var request = new TasksExportRequest
        {
            TaskIds = [.. Chosen().Select(t => t.Id)],
            Password = _include.Checked ? _password.Text : null,
        };
        _save.Enabled = false;
        _cancel.Enabled = false;
        UseWaitCursor = true;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.TasksExport, request,
                    IpcJsonContext.Default.TasksExportRequest, IpcJsonContext.Default.TasksExportResult);
                try
                {
                    // Written by this window, so as the person: to their USB stick or share, with their rights.
                    await File.WriteAllTextAsync(path, result.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Ui.ShowError(this, Strings.Format(Strings.ExportCannotWrite, ex.Message));
                    return;
                }

                string text = Strings.Format(request.Password is null ? Strings.ExportDone : Strings.ExportDoneSecrets, result.Tasks, path);
                if (result.Unreadable.Count > 0)
                {
                    text += Environment.NewLine + Environment.NewLine + Strings.Format(Strings.ExportUnreadable, string.Join(", ", result.Unreadable));
                }

                Ui.ShowInfo(this, text);
                DialogResult = DialogResult.OK;
            }
            finally
            {
                request.Password = null;
                if (!IsDisposed)
                {
                    _save.Enabled = true;
                    _cancel.Enabled = true;
                    UseWaitCursor = false;
                }
            }
        });
    }
}
