using System.Globalization;
using Avalonia.Platform.Storage;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Transfer;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// What the buttons of the main window do: the same requests to the service as the window of Custos.exe (MainForm).
/// Several selected tasks are switched on or off, run, stopped and deleted together; editing and cloning take one.
/// </summary>
internal sealed class MainActions(MainWindow window, MainViewModel vm)
{
    private ServiceMonitor Monitor => vm.Monitor;

    // The windows for changes ask for the program password before they open, not after a whole task was filled in.
    public async Task CreateAsync()
    {
        if (!vm.CanEdit || (!vm.IsDemo && !await Monitor.EnsureUnlockedAsync()))
        {
            return;
        }

        var task = new TaskDefinition { Name = vm.UniqueName(Strings.NewTaskName) };
        await OpenEditorAsync(new EditorModel(Monitor, task, isNew: true, vm.IsDemo));
    }

    public async Task EditAsync()
    {
        if (vm.SelectedDefinition is not { } task)
        {
            return;
        }

        if (!vm.CanEdit)
        {
            vm.Snack(Strings.ToolAdminsOnly);
            return;
        }

        if (!vm.IsDemo && !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        // The task as it is now: the snapshot may have changed while the password was typed.
        await OpenEditorAsync(new EditorModel(Monitor, vm.SelectedDefinition ?? task, isNew: false, vm.IsDemo));
    }

    /// <summary>A copy of the selected task, switched off, with a name of its own; it is selected once it is there.</summary>
    public async Task CloneAsync()
    {
        if (vm.SelectedDefinition is not { } task || !vm.CanEdit || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        var copy = task.Clone();
        copy.Id = TaskDefinition.NewId();
        copy.Name = vm.UniqueName(Strings.Format(Strings.CloneName, task.Name));
        copy.Enabled = false;
        copy.Revision = 0;
        copy.UpdatedAt = null;
        copy.UpdatedBy = null;
        foreach (var source in copy.Sources)
        {
            source.Id = TaskDefinition.NewId();
        }

        foreach (var destination in copy.Destinations)
        {
            destination.Id = TaskDefinition.NewId();
        }

        var result = await Monitor.RequestAsync(PipeProtocol.Methods.TaskSave, new TaskSaveRequest { Task = copy },
            IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult);
        vm.SelectWhenShown(result.Task.Id);
    }

    public async Task DeleteAsync()
    {
        var tasks = vm.SelectedDefinitions;
        if (tasks.Count == 0 || !vm.CanDelete)
        {
            return;
        }

        string question = tasks.Count == 1 ? Strings.Format(Strings.ConfirmDelete, tasks[0].Name) : Strings.Format(Strings.ConfirmDeleteMany, tasks.Count);
        if (!await Dialogs.Ask(window, question, Strings.ToolDelete) || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        foreach (var task in tasks)
        {
            await Monitor.RequestAsync(PipeProtocol.Methods.TaskDelete, new TaskRequest { TaskId = task.Id },
                IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult);
        }
    }

    /// <summary>Switches the selected tasks on (when one of them is off) or off, with one request.</summary>
    public async Task ToggleAsync()
    {
        bool enabled = vm.ToggleOn;
        var ids = vm.SelectedDefinitions.Where(t => t.Enabled != enabled).Select(t => t.Id).ToList();
        if (ids.Count == 0 || !vm.CanEdit || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        var result = await Monitor.RequestAsync(PipeProtocol.Methods.TasksEnable, new TasksEnableRequest { TaskIds = ids, Enabled = enabled },
            IpcJsonContext.Default.TasksEnableRequest, IpcJsonContext.Default.TasksEnableResult);
        if (result.Problems.Count > 0)
        {
            await Dialogs.Show(window, Strings.TasksNotChanged + "\n\n" + string.Join("\n", result.Problems));
        }
    }

    /// <summary>The selected tasks that neither run nor wait, one after another in the order of the list: the
    /// service queues them.</summary>
    public async Task RunAsync()
    {
        var snapshot = Monitor.Snapshot;
        var current = snapshot?.Current;
        var tasks = vm.SelectedDefinitions.Where(t => !IsWaiting(t, snapshot) && !(current is not null && Describe.SameId(t.Id, current.TaskId))).ToList();
        var messages = new List<string>();
        foreach (var task in tasks)
        {
            var result = await Monitor.RunControlAsync(PipeProtocol.Methods.TaskRun, task.Id);
            if (!result.Ok && result.Message is { } message)
            {
                messages.Add(tasks.Count == 1 ? message : task.Name + ": " + message);
            }
        }

        if (messages.Count == 1)
        {
            vm.Snack(messages[0]);
        }
        else if (messages.Count > 1)
        {
            await Dialogs.Show(window, string.Join("\n", messages));
        }
    }

    /// <summary>Stops the selected tasks that run or wait; with nothing selected, the run in progress.</summary>
    public async Task StopAsync()
    {
        var snapshot = Monitor.Snapshot;
        var current = snapshot?.Current;
        var tasks = vm.SelectedDefinitions;
        List<string?> ids = tasks.Count == 0
            ? [null]
            : [.. tasks.Where(t => IsWaiting(t, snapshot) || (current is not null && Describe.SameId(t.Id, current.TaskId))).Select(t => (string?)t.Id)];
        foreach (string? taskId in ids)
        {
            await Monitor.RunControlAsync(PipeProtocol.Methods.RunStop, taskId);
        }
    }

    public async Task PauseAsync()
    {
        if (Monitor.Snapshot?.Current is { } current)
        {
            await Monitor.RunControlAsync(vm.IsPaused ? PipeProtocol.Methods.RunResume : PipeProtocol.Methods.RunPause, current.TaskId);
        }
    }

    /// <summary>
    /// The theme of the CUSTOS settings: one for all users of the computer, so it is saved in the service like the
    /// rest of the settings (administrators, and the program password when one is set).
    /// </summary>
    public async Task SetThemeAsync(string theme)
    {
        if (vm.IsDemo)
        {
            vm.SetDemoTheme(theme);
            return;
        }

        if (Monitor.Snapshot?.Settings is not { } current || current.Theme == theme)
        {
            return;
        }

        if (!vm.CanEdit)
        {
            vm.Snack(DesktopStrings.ThemeAdminsOnly);
            return;
        }

        if (!await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        // A copy: the snapshot keeps what the service said until it says otherwise.
        var json = IpcJsonContext.Default.AgentSettings;
        var settings = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(current, json), json)!;
        settings.Theme = theme;
        await Monitor.RequestAsync(PipeProtocol.Methods.SettingsSave, new SettingsSaveRequest { Settings = settings },
            IpcJsonContext.Default.SettingsSaveRequest, IpcJsonContext.Default.OkResult);
    }

    /// <summary>«Импорт заданий из файла»: a file of «Экспорт заданий» (section 9a), all its tasks.</summary>
    public async Task ImportAsync()
    {
        if (!vm.CanEdit || Monitor.Snapshot is not { } snapshot || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.ImportFileTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CUSTOS") { Patterns = ["*.json"] }, FilePickerFileTypes.All],
        });
        if ((files.Count > 0 ? files[0].TryGetLocalPath() : null) is not { } path)
        {
            return;
        }

        ExportFile file;
        try
        {
            file = TaskExport.Read(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.Show(window, Strings.Format(Strings.ImportFileCannotRead, ex.Message));
            return;
        }

        ExportSecrets? secrets = null;
        while (file.Secrets is not null && secrets is null)
        {
            string? password = await Dialogs.AskText(window, Strings.ExportPasswordTitle, Strings.ExportPasswordAsk, secret: true, note: Strings.ExportPasswordSkipNote);
            if (password is null)
            {
                break;
            }

            try
            {
                secrets = TaskExport.Open(file, password);
            }
            catch (ExportException ex)
            {
                await Dialogs.Show(window, ex.Message);
            }
        }

        var plan = new TaskImport(file, secrets, snapshot.Tasks, snapshot.Credentials);
        if (plan.Items.Count == 0)
        {
            await Dialogs.Show(window, Strings.Format(Strings.ImportFileFound, 0));
            return;
        }

        string names = string.Join("\n", plan.Items.Select(i => "• " + i.Task.Name + (i.Here is not null ? " — " + Strings.ImportHereReplaced : string.Empty)));
        if (!await Dialogs.Ask(window, Strings.Format(Strings.ImportFileFound, plan.Items.Count) + "\n\n" + names, Strings.ImportButton))
        {
            return;
        }

        // The passwords the file does not have: asked one by one; an empty one leaves the task switched off.
        var typed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in plan.AccountsToAsk(plan.Items))
        {
            string? secret = await Dialogs.AskText(window, Strings.AccountPassword.TrimEnd(':'),
                account.Credential.DisplayName + " — " + Strings.Format(Strings.ImportAccountTasks, string.Join(", ", account.TaskNames)), secret: true);
            if (!string.IsNullOrEmpty(secret))
            {
                typed[account.Credential.Id] = secret;
            }
        }

        var build = plan.Build(plan.Items, typed, new Dictionary<string, string>());
        await Monitor.RequestAsync(PipeProtocol.Methods.TasksImport, build.Request, IpcJsonContext.Default.TasksImportRequest, IpcJsonContext.Default.OkResult);
        string text = Strings.Format(Strings.ImportFileDone, build.Request.Tasks.Count);
        if (build.SwitchedOff.Count > 0)
        {
            text += "\n\n" + Strings.Format(Strings.ImportFileSwitchedOff, string.Join(", ", build.SwitchedOff));
        }

        await Dialogs.Show(window, text);
    }

    /// <summary>«Экспорт заданий в файл»: all tasks, with the passwords when an export password is given. The stored
    /// passwords leave the service only this way: administrators only (section 9a).</summary>
    public async Task ExportAsync()
    {
        if (Monitor.Status.Hello?.IsAdmin != true || Monitor.Snapshot is not { } snapshot || !await Monitor.EnsureUnlockedAsync())
        {
            if (Monitor.IsConnected && Monitor.Status.Hello?.IsAdmin != true)
            {
                vm.Snack(Strings.ToolAdminsOnly);
            }

            return;
        }

        string? password = null;
        if (await Dialogs.Ask(window, Strings.ExportSecretsNote, Strings.ExportIncludeSecrets, Strings.ExportPasswordSkip, Strings.ExportTitle))
        {
            password = await Dialogs.NewPassword(window, Strings.ExportPasswordTitle, null, minLength: 8);
            if (password is null)
            {
                return;
            }
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.ExportTitle,
            SuggestedFileName = string.Create(CultureInfo.InvariantCulture, $"custos-tasks-{Safe(snapshot.ComputerName)}-{DateTime.Today:yyyy-MM-dd}.json"),
            DefaultExtension = "json",
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        var request = new TasksExportRequest { Password = password, AllAccounts = true };
        var result = await Monitor.RequestAsync(PipeProtocol.Methods.TasksExport, request, IpcJsonContext.Default.TasksExportRequest, IpcJsonContext.Default.TasksExportResult);
        try
        {
            await File.WriteAllTextAsync(path, result.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Show(window, Strings.Format(Strings.ExportCannotWrite, ex.Message));
            return;
        }

        string text = Strings.Format(password is null ? Strings.ExportDone : Strings.ExportDoneSecrets, result.Tasks, path);
        if (result.Unreadable.Count > 0)
        {
            text += "\n\n" + Strings.Format(Strings.ExportUnreadable, string.Join(", ", result.Unreadable));
        }

        await Dialogs.Show(window, text);

        static string Safe(string name) => new([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
    }

    /// <summary>«Импорт из Cobian Backup»: the tasks of Cobian Backup 11, added switched off.</summary>
    public async Task CobianImportAsync()
    {
        if (!vm.CanEdit || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        await new CobianImportDialog(Monitor).ShowAsync(window);
    }

    /// <summary>«Восстановить»: administrators only, with the program password when one is set (section 6.11).</summary>
    public async Task RestoreAsync()
    {
        if (!vm.IsDemo && (Monitor.Status.Hello?.IsAdmin != true || !await Monitor.EnsureUnlockedAsync()))
        {
            return;
        }

        await new RestoreDialog(Monitor, vm.SelectedTask?.TaskId).ShowAsync(window);
    }

    /// <summary>«Обновить»: the service downloads the version from GitHub, checks it and installs it.</summary>
    public async Task InstallUpdateAsync()
    {
        if (Monitor.Snapshot?.ProgramUpdate is not { Available: true, Latest: { } version }
            || !await Dialogs.Ask(window, Strings.Format(Strings.UpdateAsk, version), Strings.UpdateInstall, Strings.ButtonCancel)
            || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        var result = await Monitor.RequestAsync(PipeProtocol.Methods.ProgramUpdate, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult, TimeSpan.FromMinutes(15));
        await Dialogs.Show(window, result.Ok ? result.Message ?? Strings.UpdateInstalling : result.Message ?? Strings.ProblemNoAnswer);
    }

    /// <summary>«Все настройки»: everyone may look; the window asks for the program password before a change is sent.</summary>
    public async Task SettingsAsync()
    {
        if (!Monitor.IsConnected)
        {
            return;
        }

        if (vm.CanEdit && !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        await new SettingsDialog(Monitor).ShowAsync(window);
    }

    private async Task OpenEditorAsync(EditorModel model)
    {
        var editor = new EditorWindow(model);
        await editor.ShowDialog(window);
        if (editor.Saved is not { } saved)
        {
            return;
        }

        if (model.IsDemo && Monitor.Snapshot is { } snapshot)
        {
            // The made-up tasks have no service: the window keeps the change itself.
            int index = snapshot.Tasks.FindIndex(t => t.Id == saved.Id);
            if (index >= 0)
            {
                snapshot.Tasks[index] = saved;
            }
            else
            {
                snapshot.Tasks.Add(saved);
            }

            Monitor.SimulateSnapshot(snapshot);
        }

        vm.SelectWhenShown(saved.Id);
    }

    /// <summary>Whether the task waits in the queue.</summary>
    private static bool IsWaiting(TaskDefinition task, AgentSnapshot? snapshot) =>
        snapshot?.States.FirstOrDefault(s => Describe.SameId(s.TaskId, task.Id))?.Queued == true;
}
