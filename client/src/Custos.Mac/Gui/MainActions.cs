using System.Globalization;
using Avalonia.Platform.Storage;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Transfer;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>What the buttons of the main window do (the same requests as the window of Windows, MainForm).</summary>
internal sealed class MainActions(MainWindow window, MainViewModel vm)
{
    private ServiceMonitor Monitor => vm.Monitor;

    // The windows for changes ask for the program password before they open, not after a whole task was filled in.
    public async Task CreateAsync()
    {
        if (!Monitor.CanEdit || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        var task = new TaskDefinition { Name = UniqueName(Strings.NewTaskName) };
        var editor = new TaskEditorWindow(Monitor, task, isNew: true);
        await editor.ShowDialog(window);
        if (editor.Saved is { } saved)
        {
            vm.SelectWhenShown(saved.Id);
        }
    }

    public async Task EditAsync()
    {
        if (vm.SelectedDefinition is not { } task)
        {
            return;
        }

        if (!Monitor.CanEdit)
        {
            vm.Snack(Strings.ToolAdminsOnly);
            return;
        }

        if (!await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        // The task as it is now: the snapshot may have changed while the password was typed.
        var editor = new TaskEditorWindow(Monitor, (vm.SelectedDefinition ?? task).Clone(), isNew: false);
        await editor.ShowDialog(window);
    }

    public async Task CloneAsync()
    {
        if (vm.SelectedDefinition is not { } task || !Monitor.CanEdit)
        {
            return;
        }

        var copy = task.Clone();
        copy.Id = TaskDefinition.NewId();
        copy.Name = UniqueName(Strings.Format(Strings.CloneName, task.Name));
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
        if (vm.SelectedDefinition is not { } task || !Monitor.CanEdit)
        {
            return;
        }

        if (await Dialogs.Ask(window, Strings.Format(Strings.ConfirmDelete, task.Name), Strings.ToolDelete))
        {
            await Monitor.RequestAsync(PipeProtocol.Methods.TaskDelete, new TaskRequest { TaskId = task.Id },
                IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult);
        }
    }

    public async Task ToggleAsync()
    {
        if (vm.SelectedDefinition is not { } task || !Monitor.CanEdit)
        {
            return;
        }

        var result = await Monitor.RequestAsync(PipeProtocol.Methods.TasksEnable, new TasksEnableRequest { TaskIds = [task.Id], Enabled = !task.Enabled },
            IpcJsonContext.Default.TasksEnableRequest, IpcJsonContext.Default.TasksEnableResult);
        if (result.Problems.Count > 0)
        {
            await Dialogs.Show(window, Strings.TasksNotChanged + "\n\n" + string.Join("\n", result.Problems));
        }
    }

    public async Task RunAsync()
    {
        if (vm.SelectedDefinition is not { } task)
        {
            return;
        }

        var result = await Monitor.RunControlAsync(PipeProtocol.Methods.TaskRun, task.Id);
        if (!result.Ok && result.Message is { } message)
        {
            vm.Snack(message);
        }
    }

    public async Task StopAsync() => await Monitor.RunControlAsync(PipeProtocol.Methods.RunStop, vm.SelectedDefinition?.Id);

    public async Task PauseAsync()
    {
        if (Monitor.Snapshot?.Current is { } current)
        {
            await Monitor.RunControlAsync(vm.IsPaused ? PipeProtocol.Methods.RunResume : PipeProtocol.Methods.RunPause, current.TaskId);
        }
    }

    public async Task RestoreAsync()
    {
        if (!vm.CanRestore || !await Monitor.EnsureUnlockedAsync())
        {
            return;
        }

        await new RestoreWindow(Monitor, vm.SelectedDefinition?.Id).ShowDialog(window);
    }

    /// <summary>The theme of the «Настройки» menu: a setting of the service, the same for all users of this Mac.</summary>
    public async Task SetThemeAsync(string theme)
    {
        if (vm.Demo)
        {
            vm.ThemeChoice = theme;
            return;
        }

        if (Monitor.Snapshot?.Settings is not { } current || current.Theme == theme)
        {
            return;
        }

        if (!vm.CanEdit)
        {
            vm.Snack(MacStrings.ThemeAdminsOnly);
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

    public async Task SettingsAsync() => await new SettingsWindow(Monitor).ShowDialog(window);

    /// <summary>«Импорт заданий из файла»: a file of «Экспорт заданий» (section 9a), all its tasks.</summary>
    public async Task ImportAsync()
    {
        if (!Monitor.CanEdit || Monitor.Snapshot is not { } snapshot || !await Monitor.EnsureUnlockedAsync())
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

    /// <summary>«Экспорт заданий в файл»: all tasks, with the passwords when an export password is given.</summary>
    public async Task ExportAsync()
    {
        if (!Monitor.CanEdit || Monitor.Snapshot is not { } snapshot || !await Monitor.EnsureUnlockedAsync())
        {
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

    private string UniqueName(string name)
    {
        var tasks = Monitor.Snapshot?.Tasks ?? [];
        string candidate = name;
        for (int i = 2; tasks.Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }
}
