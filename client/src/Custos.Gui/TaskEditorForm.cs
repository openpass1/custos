using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>
/// The task editor (docs/client-ui.md, section 3): pages on the left as in Cobian, the selected page on the right, the
/// revision and the last author at the bottom left, "OK" and "Cancel" at the bottom right. The service checks the
/// task again when it is saved; what it refuses is shown and the editor stays open.
/// </summary>
internal sealed class TaskEditorForm : Form
{
    private const int PageIconSize = 20;

    private readonly ServiceMonitor _monitor;
    private readonly TaskDefinition _task;
    private readonly bool _isNew;
    private readonly int? _baseRevision;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };
    private readonly ListView _pageList = new()
    {
        Dock = DockStyle.Left,
        Width = 180,
        View = View.Details,
        HeaderStyle = ColumnHeaderStyle.None,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };

    private readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 12, 8) };
    private readonly Label _info = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Button _ok = new() { Text = Strings.ButtonOk, AutoSize = true, MinimumSize = new Size(88, 0), Anchor = AnchorStyles.Right };
    private readonly Button _cancel = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };
    private readonly List<EditorPage> _pages;
    private ImageList? _pageImages;
    private bool _saving;
    private EditorPage? _shownPage;

    public TaskEditorForm(ServiceMonitor monitor, TaskDefinition task, bool isNew)
    {
        _monitor = monitor;
        _task = task;
        _isNew = isNew;
        _baseRevision = isNew ? null : task.Revision;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = isNew ? Strings.EditorTitleNew : Strings.Format(Strings.EditorTitle, task.Name);
        Icon = AppIcons.App();
        Size = new Size(860, 660);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AcceptButton = _ok;
        CancelButton = _cancel;

        var context = new EditorContext(monitor, _toolTip) { ProfileSid = task.ProfileSid };
        _pages =
        [
            new BasicPage(context),
            new SourcesPage(context),
            new DatabasesPage(context),
            new DestinationsPage(context),
            new SchedulePage(context),
            new ArchivePage(context),
            new RetentionPage(context),
            new EventsPage(context),
            new AdvancedPage(context),
        ];

        _pageList.Columns.Add(string.Empty, 170);
        foreach (var page in _pages)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _host.Controls.Add(page);
            page.LoadFrom(task);
            page.EndBuild();
            _pageList.Items.Add(new ListViewItem(page.Title)
            {
                ImageKey = page.Glyph.ToString(),
                ForeColor = page.Available ? SystemColors.WindowText : SystemColors.GrayText,
                Tag = page,
            });
        }

        _pageList.SelectedIndexChanged += (_, _) => ShowSelectedPage();

        var separator = new Label { Dock = DockStyle.Left, Width = 1, BackColor = SystemColors.ControlDark };
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 8, 12, 10) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_info, 0, 0);
        bottom.Controls.Add(_ok, 1, 0);
        bottom.Controls.Add(_cancel, 2, 0);
        _info.Text = isNew ? Strings.EditorNewTask : Strings.Format(Strings.EditorRevision, task.Revision, Ui.Time(task.UpdatedAt), TaskCard.DescribeAuthor(task.UpdatedBy));
        var line = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlDark };

        // Docking goes from the last control added to the first.
        Controls.Add(_host);
        Controls.Add(separator);
        Controls.Add(_pageList);
        Controls.Add(line);
        Controls.Add(bottom);
        ResumeLayout(performLayout: true);

        _ok.Click += (_, _) => Save();
        CreateImages();
        _pageList.Items[0].Selected = true;
        ShowPage(_pages[0]);
    }

    /// <summary>The task as the service stored it, after "OK".</summary>
    public TaskDefinition? Saved { get; private set; }

    /// <summary>A new task with the defaults of appendix A.9 and a name not used yet.</summary>
    public static TaskDefinition NewTask(AgentSnapshot? snapshot)
    {
        var task = new TaskDefinition { Name = Strings.NewTaskName };
        for (int i = 2; snapshot?.Tasks.Any(t => string.Equals(t.Name, task.Name, StringComparison.OrdinalIgnoreCase)) == true; i++)
        {
            task.Name = $"{Strings.NewTaskName} {i}";
        }

        return task;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        CreateImages();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The answer of the service is awaited; closing now would leave the user unsure whether the task was saved.
        if (_saving)
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _pageImages?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CreateImages()
    {
        var old = _pageImages;
        int size = LogicalToDeviceUnits(PageIconSize);
        _pageImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
        foreach (var page in _pages)
        {
            _pageImages.Images.Add(page.Glyph.ToString(), Glyphs.Draw(page.Glyph, size, page.Available ? Glyphs.Ink : Glyphs.Grey));
        }

        _pageList.SmallImageList = _pageImages;
        _pageList.Columns[0].Width = _pageList.ClientSize.Width - 4;
        old?.Dispose();
    }

    /// <summary>The pages, in the order of the list.</summary>
    internal IReadOnlyList<EditorPage> Pages => _pages;

    /// <summary>Selects a page in the list and shows it.</summary>
    internal void SelectPage(EditorPage page)
    {
        foreach (ListViewItem item in _pageList.Items)
        {
            item.Selected = item.Tag == page;
        }

        ShowPage(page);
    }

    /// <summary>Every page writes into a copy of the task; the first page that finds a problem is shown.</summary>
    /// <returns>The task as edited, or null with <paramref name="problem"/> in words.</returns>
    internal TaskDefinition? Collect(out string? problem)
    {
        var task = _task.Clone();
        foreach (var page in _pages)
        {
            if (page.SaveTo(task) is { } found)
            {
                SelectPage(page);
                problem = found;
                return null;
            }
        }

        problem = null;
        return task;
    }

    private void ShowSelectedPage()
    {
        if (_pageList.SelectedItems.Count > 0)
        {
            ShowPage((EditorPage)_pageList.SelectedItems[0].Tag!);
        }
    }

    private void ShowPage(EditorPage selected)
    {
        if (selected == _shownPage)
        {
            return;
        }

        _shownPage = selected;
        foreach (var page in _pages)
        {
            if (page != selected)
            {
                page.Visible = false;
            }
        }

        selected.Visible = true;
        selected.Shown();
    }

    private void Save()
    {
        if (Collect(out string? problem) is not { } task)
        {
            Ui.ShowWarning(this, problem!);
            return;
        }

        _ok.Enabled = false;
        _cancel.Enabled = false;
        UseWaitCursor = true;
        _saving = true;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.TaskSave,
                    new TaskSaveRequest { Task = task, BaseRevision = _baseRevision },
                    IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult);
                Saved = result.Task;
                _saving = false;
                DialogResult = DialogResult.OK;
            }
            finally
            {
                _saving = false;
                _ok.Enabled = true;
                _cancel.Enabled = true;
                UseWaitCursor = false;
            }
        });
    }
}

/// <summary>What the pages of the editor share: the connection, the tooltips and the accounts.</summary>
internal sealed class EditorContext(ServiceMonitor monitor, ToolTip toolTip)
{
    private readonly List<Credential> _added = [];

    public ServiceMonitor Monitor { get; } = monitor;

    public ToolTip ToolTip { get; } = toolTip;

    /// <summary>Raised when an account was added while the editor is open.</summary>
    public event EventHandler? CredentialsChanged;

    /// <summary>Raised when the name, the backup type or the archive format changed on their pages.</summary>
    public event EventHandler? ShapeChanged;

    /// <summary>The name as typed on the first page.</summary>
    public string TaskName { get; private set; } = string.Empty;

    /// <summary>The backup type chosen on the first page.</summary>
    public string BackupType { get; private set; } = BackupTypes.Incremental;

    /// <summary>The format chosen on the archive page.</summary>
    public string ArchiveFormat { get; private set; } = ArchiveFormats.None;

    public bool IsMirror => BackupType == BackupTypes.Mirror;

    /// <summary>How many database servers the task has now (a task with them needs no sources).</summary>
    public int DbServerCount { get; set; }

    /// <summary>Raised when a Linux server was added to the sources or removed from them.</summary>
    public event EventHandler? RemoteChanged;

    /// <summary>Whether the source is a Linux server: then it is the only one, and its databases are its own.</summary>
    public bool HasRemoteSource
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                RemoteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whose profile %USERPROFILE% in the paths means; null until the task is first saved.</summary>
    public string? ProfileSid { get; init; }

    /// <summary>Tells the other pages what changed; null leaves a value as it is.</summary>
    public void SetShape(string? taskName = null, string? backupType = null, string? archiveFormat = null)
    {
        bool changed = (taskName is not null && taskName != TaskName)
            || (backupType is not null && backupType != BackupType)
            || (archiveFormat is not null && archiveFormat != ArchiveFormat);
        TaskName = taskName ?? TaskName;
        BackupType = backupType ?? BackupType;
        ArchiveFormat = archiveFormat ?? ArchiveFormat;
        if (changed)
        {
            ShapeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The stored Windows accounts, including the ones added in this editor.</summary>
    public List<Credential> Credentials
    {
        get
        {
            var stored = Monitor.Snapshot?.Credentials ?? [];
            return
            [
                .. stored.Concat(_added.Where(a => !stored.Any(s => string.Equals(s.Id, a.Id, StringComparison.OrdinalIgnoreCase))))
                    .Where(c => c.Kind == CredentialKinds.Windows)
                    .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ];
        }
    }

    /// <summary>A stored account of any kind, including the ones saved in this editor; null if there is none.</summary>
    public Credential? FindCredential(string? id) =>
        id is null ? null
        : _added.LastOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? Monitor.Snapshot?.Credentials.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keeps an account saved by a dialog of this editor (a database account) until the service sends it.</summary>
    public void RememberCredential(Credential credential)
    {
        _added.Add(credential);
        if (credential.Kind == CredentialKinds.Windows)
        {
            CredentialsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Asks for a new account and stores it in the service.</summary>
    /// <returns>The stored account, or null if the user cancelled.</returns>
    public Credential? AddCredential(IWin32Window owner)
    {
        using var dialog = new CredentialDialog(Monitor, null);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Saved is not { } saved)
        {
            return null;
        }

        _added.Add(saved);
        CredentialsChanged?.Invoke(this, EventArgs.Empty);
        return saved;
    }
}

/// <summary>One page of the task editor.</summary>
internal abstract class EditorPage : UserControl
{
    private readonly TableLayoutPanel _table = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 3,
        Padding = new Padding(0, 0, 8, 8),
    };

    protected EditorPage(EditorContext context, string title, Glyph glyph)
    {
        Context = context;
        Title = title;
        Glyph = glyph;

        // Without this every added row lays out the whole table again, wrapped notes measured anew each time: the
        // editor took seconds to open. The editor ends it with EndBuild after LoadFrom.
        SuspendLayout();
        _table.SuspendLayout();
        AutoScroll = true;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(_table);
        _table.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.25f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        });
        _table.SetColumnSpan(_table.Controls[^1], 3);
    }

    public string Title { get; }

    public Glyph Glyph { get; }

    /// <summary>False for pages of features that come in a later version: shown, but only with a note.</summary>
    public virtual bool Available => true;

    protected EditorContext Context { get; }

    protected ToolTip ToolTip => Context.ToolTip;

    public abstract void LoadFrom(TaskDefinition task);

    /// <summary>Lets the page lay out again, once, after it was built and loaded.</summary>
    internal void EndBuild()
    {
        _table.ResumeLayout(performLayout: false);
        ResumeLayout(performLayout: false);
    }

    /// <summary>Writes the page into the task.</summary>
    /// <returns>What is wrong, in words, or null.</returns>
    public abstract string? SaveTo(TaskDefinition task);

    /// <summary>Called each time the page is shown.</summary>
    public virtual void Shown()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _table.Controls[0].Font.Dispose();
        }

        base.Dispose(disposing);
    }

    protected void AddHeading(string text)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 12, 0, 4) };
        label.Font = new Font(label.Font, FontStyle.Bold);
        label.Disposed += (_, _) => label.Font.Dispose();
        AddWide(label);
    }

    /// <summary>A caption, the control and an optional "?" with an explanation.</summary>
    /// <param name="alignTop">For a tall control: the caption stays at its top instead of the middle.</param>
    protected void AddRow(string caption, Control control, string? help = null, bool alignTop = false)
    {
        _table.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            Anchor = alignTop ? AnchorStyles.Top | AnchorStyles.Left : AnchorStyles.Left,
            Margin = new Padding(0, 6, 12, 6),
        });
        control.Margin = new Padding(0, 3, 0, 3);
        _table.Controls.Add(control);
        AddHelp(help);
    }

    /// <summary>A control over the whole width (a check box, a group).</summary>
    protected void AddWide(Control control, string? help = null, int indent = 0)
    {
        if (indent > 0)
        {
            control.Margin = new Padding(indent, control.Margin.Top, control.Margin.Right, control.Margin.Bottom);
        }

        _table.Controls.Add(control);
        _table.SetColumnSpan(control, help is null ? 3 : 2);
        if (help is not null)
        {
            AddHelp(help);
        }
    }

    /// <summary>A grey explanation over the whole width, wrapped.</summary>
    protected Label AddNote(string text, int indent = 0)
    {
        var note = new Label
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(indent, 0, 0, 6),
        };
        AddWide(note);
        return note;
    }

    private void AddHelp(string? help)
    {
        if (help is null)
        {
            _table.Controls.Add(new Label { AutoSize = true, Width = 0 });
        }
        else
        {
            _table.Controls.Add(Ui.HelpIcon(ToolTip, help));
        }
    }
}
