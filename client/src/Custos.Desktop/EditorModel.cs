using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// The task editor of the new window (docs/client-ui.md, section 3): the pages of Custos.exe's editor with the same
/// rules (EditorPages.cs), the revision and its author, and saving: every page writes into a copy of the task, and the
/// service checks it again; what it refuses is shown and the editor stays open (TaskEditorForm of Custos.exe).
/// </summary>
internal sealed class EditorModel : Observable
{
    private readonly TaskDefinition _task;
    private readonly int? _baseRevision;
    private EditorPage _page;
    private string? _problem;

    public EditorModel(ServiceMonitor monitor, TaskDefinition task, bool isNew, bool demo = false)
    {
        _task = task.Clone();
        _baseRevision = isNew ? null : task.Revision;
        IsDemo = demo;
        Context = new EditorContext(monitor) { ProfileSid = task.ProfileSid, IsNew = isNew };
        Title = isNew ? Strings.EditorTitleNew : Strings.Format(Strings.EditorTitle, task.Name);
        Revision = isNew ? Strings.EditorNewTask
            : Strings.Format(Strings.EditorRevision, task.Revision, Describe.Time(task.UpdatedAt), Describe.Author(task.UpdatedBy));
        Pages =
        [
            new BasicPage(Context),
            new SourcesPage(Context),
            new DatabasesPage(Context),
            new DestinationsPage(Context),
            new SchedulePage(Context),
            new ArchivePage(Context),
            new RetentionPage(Context),
            new EventsPage(Context),
            new AdvancedPage(Context),
        ];
        foreach (var page in Pages)
        {
            page.LoadFrom(_task);
        }

        _page = Pages[0];
        _page.Shown();
    }

    public EditorContext Context { get; }

    public string Title { get; }

    public string Revision { get; }

    public IReadOnlyList<EditorPage> Pages { get; }

    /// <summary>The window shows a made-up task (--demo): saving keeps it in the window only.</summary>
    public bool IsDemo { get; }

    public EditorPage Page
    {
        get => _page;
        set
        {
            if (value is not null && Set(ref _page, value))
            {
                value.Shown();
            }
        }
    }

    /// <summary>What stops saving, under the page; null when nothing does.</summary>
    public string? Problem
    {
        get => _problem;
        set
        {
            if (Set(ref _problem, value))
            {
                Changed(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>The task as the service stored it, after «Сохранить».</summary>
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

    /// <summary>Opens the page with this title (the pictures and the checks).</summary>
    public void Open(string title) => Page = Pages.FirstOrDefault(p => p.Title == title) ?? Page;

    /// <summary>Every page writes into a copy of the task; the first page that finds a problem is shown.</summary>
    /// <returns>The task as edited, or null with the problem in <see cref="Problem"/>.</returns>
    public TaskDefinition? Collect()
    {
        var task = _task.Clone();
        foreach (var page in Pages)
        {
            if (page.SaveTo(task) is { } found)
            {
                Page = page;
                Problem = found;
                return null;
            }
        }

        if (Core.CustosEdition.IsLinux && task.Enabled && LinuxEditorPaths.HasUnsupportedPath(task))
        {
            Problem = DesktopStrings.LinuxTaskUnsupportedPaths;
            return null;
        }

        Problem = null;
        return task;
    }

    /// <summary>Saves the task in the service; false with <see cref="Problem"/> when it was not saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Collect() is not { } task)
        {
            return false;
        }

        if (IsDemo)
        {
            task.Revision = (_baseRevision ?? 0) + 1;
            task.UpdatedAt = DesktopClock.Now;
            task.UpdatedBy = "local:" + Environment.UserName;
            Saved = task;
            return true;
        }

        try
        {
            var result = await Context.Monitor.RequestAsync(PipeProtocol.Methods.TaskSave,
                new TaskSaveRequest { Task = task, BaseRevision = _baseRevision },
                IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult);
            Saved = result.Task;
            return true;
        }
        catch (ServiceUnavailableException ex)
        {
            Problem = ex.Message;
            return false;
        }
    }
}
