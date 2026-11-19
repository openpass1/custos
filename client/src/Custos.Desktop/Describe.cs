using System.Globalization;
using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// Tasks and runs in words, as Custos.exe shows them (TaskCard, TaskListView, ProgressView). These are copies of the
/// small functions that live in WinForms classes there; a real move to Avalonia would put them in one shared place.
/// </summary>
internal static class Describe
{
    /// <summary>"04.11.2026 16:41", or "—".</summary>
    public static string Time(DateTimeOffset? time) =>
        time is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "—";

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Set by the screenshot run, so that the made-up log of 18.11.2026 counts as today.</summary>
    public static DateTime? FixedToday { get; set; }

    public static string LogTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == (FixedToday ?? DateTime.Today)
            ? local.ToString("T", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture) + " " + local.ToString("T", CultureInfo.CurrentCulture);
    }

    public static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    /// <summary>"Running 47 %", "Paused 47 %", "Stopping…".</summary>
    public static string RunningText(bool stopping, bool paused, double percent) => stopping
        ? Strings.ResultStopping
        : Strings.Format(paused ? Strings.ResultPaused : Strings.ResultRunning, (int)Math.Floor(percent));

    public static RowState StateOf(string? status) => status switch
    {
        RunStatuses.Success => RowState.Ok,
        RunStatuses.Warning or RunStatuses.Aborted => RowState.Warn,
        null => RowState.None,
        _ => RowState.Bad,
    };

    /// <summary>The progress shown for a run: the latest event when it is about this run, else what the snapshot holds.</summary>
    public static (BackupProgress Progress, bool Paused) Latest(CurrentRun run, RunProgressEvent? progress)
    {
        bool fresh = progress is not null && progress.RunId == run.RunId;
        return fresh ? (progress!.Progress, progress.Paused) : (run.Progress ?? new BackupProgress(), run.Paused);
    }

    /// <summary>The line under the bar in the card: «Выполняется 47 % · осталось 12 мин».</summary>
    public static string ProgressLine(CurrentRun run, BackupProgress p, bool paused)
    {
        string line = RunningText(run.Stopping, paused, p.Percent);
        if (!run.Stopping && !paused)
        {
            string? more = p.EtaS is { } eta ? Strings.Format(Strings.CardProgressLeft, Sizes.FormatDuration(TimeSpan.FromSeconds(eta)))
                : p.Phase != BackupPhases.Copy ? PhaseText(p.Phase)
                : null;
            if (more is not null)
            {
                line += " · " + more;
            }
        }

        return line;
    }

    public static string PhaseText(string phase) => phase switch
    {
        BackupPhases.Vss => Strings.PhaseVss,
        BackupPhases.Scan => Strings.PhaseScan,
        BackupPhases.Copy => Strings.PhaseCopy,
        BackupPhases.Verify => Strings.PhaseVerify,
        BackupPhases.Retention => Strings.PhaseRetention,
        BackupPhases.PreEvents or BackupPhases.PostEvents => Strings.PhaseEvents,
        _ => phase,
    };

    public static string DestinationState(string state) => state switch
    {
        DestinationStates.Pending => Strings.DestinationPending,
        DestinationStates.Writing => Strings.DestinationWriting,
        DestinationStates.Done => Strings.DestinationDone,
        DestinationStates.Failed => Strings.DestinationFailed,
        _ => state,
    };

    public static string Sources(TaskDefinition task) => task.Sources.Count == 0 ? Strings.CardNone
        : string.Join(Environment.NewLine, task.Sources.Select(s => s.Kind == SourceKinds.Ssh ? s.Path + " (" + PresetTitle(s.Preset) + ")"
            : s.Recursive ? s.Path : Strings.Format(Strings.CardNotRecursive, s.Path)));

    public static string? Databases(TaskDefinition task)
    {
        if (task.DbServers.Count > 0)
        {
            return string.Join(Environment.NewLine, task.DbServers.Select(d =>
                string.Concat(DbEngines.Title(d.Engine), " ", d.Host, ":", (d.Port ?? DbEngines.DefaultPort(d.Engine)).ToString(CultureInfo.InvariantCulture))
                + " — " + (d.AllDatabases
                    ? d.Exclude.Count == 0 ? Strings.DbAll : Strings.Format(Strings.DbAllExcept, string.Join(", ", d.Exclude))
                    : Strings.Format(Strings.DbOnly, string.Join(", ", d.Include)))));
        }

        if (task.Sources.FirstOrDefault(s => s.Kind == SourceKinds.Ssh) is { Helper.DbEngines: { Count: > 0 } engines } remote)
        {
            string chosen = remote.Databases switch
            {
                { AllDatabases: false } choice => Strings.Format(Strings.DbOnly, RemoteDatabaseChoice.Describe(choice.Include)),
                { Exclude.Count: > 0 } choice => Strings.Format(Strings.DbAllExcept, RemoteDatabaseChoice.Describe(choice.Exclude)),
                _ => Strings.DbAll,
            };
            return string.Join(", ", engines.Select(DbEngines.Title)) + " — " + chosen;
        }

        return null;
    }

    public static string Destination(TaskDestination destination, AgentSnapshot snapshot)
    {
        string text = TaskDescriber.Destination(destination);
        if (destination.CredentialId is { } id && snapshot.Credentials.FirstOrDefault(c => SameId(c.Id, id)) is { } credential)
        {
            text = Strings.Format(Strings.CardAsAccount, text, credential.DisplayName);
        }

        return text;
    }

    public static string? Protection(TaskDefinition task)
    {
        var lines = new List<string>();
        foreach (var destination in task.Destinations)
        {
            string? text = WormSettings.Of(destination) is { } worm ? Strings.Format(Strings.CardWorm, worm.Days, worm.LockHours)
                : destination.Kind == DestinationKinds.S3 && destination.ObjectLockDays > 0
                    ? Strings.Format(Strings.CardObjectLock, destination.ObjectLockDays,
                        destination.ObjectLockMode == ObjectLockModes.Compliance ? "Compliance" : "Governance")
                : null;
            if (text is not null)
            {
                lines.Add(task.Destinations.Count > 1 ? Strings.Format(Strings.CardProtectionOf, TaskDescriber.Destination(destination), text) : text);
            }
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    public static string Archive(TaskDefinition task)
    {
        var archive = task.Archive;
        string? format = task.BackupType == BackupTypes.Mirror ? null : archive.Format switch
        {
            ArchiveFormats.SevenZip => Strings.ArchiveSevenZip,
            ArchiveFormats.Zip => Strings.ArchiveZip,
            _ => null,
        };
        if (format is null)
        {
            return Strings.ArchiveNone;
        }

        var parts = new List<string> { Strings.Format(Strings.CardArchiveLevel, format, Math.Clamp(archive.Level, 0, 9)) };
        if (archive.Format == ArchiveFormats.SevenZip && archive.SplitMb > 0)
        {
            parts.Add(Strings.Format(Strings.CardArchiveVolumes, Sizes.Format(archive.SplitMb * 1024L * 1024L)));
        }

        if (archive.Encrypt)
        {
            parts.Add(archive.EncryptNames && archive.Format == ArchiveFormats.SevenZip ? Strings.CardArchiveEncryptedNames : Strings.CardArchiveEncrypted);
        }

        return string.Join(", ", parts);
    }

    public static string Event(TaskEvent item) => item.Kind switch
    {
        TaskEventKinds.ServiceStop => Strings.Format(Strings.EventShowStop, item.Service),
        TaskEventKinds.ServiceStart => Strings.Format(Strings.EventShowStart, item.Service),
        _ => string.IsNullOrWhiteSpace(item.Args) ? item.Command ?? string.Empty : $"{item.Command} {item.Args}",
    };

    public static string? After(string? after) => after switch
    {
        AfterActions.Shutdown => Strings.AfterShutdown,
        AfterActions.Restart => Strings.AfterRestart,
        AfterActions.Sleep => Strings.AfterSleep,
        AfterActions.Hibernate => Strings.AfterHibernate,
        _ => null,
    };

    public static string Author(string? updatedBy) => updatedBy switch
    {
        null => "—",
        _ when updatedBy.StartsWith("local:", StringComparison.Ordinal) => updatedBy["local:".Length..],
        _ when updatedBy.StartsWith("server:", StringComparison.Ordinal) => Strings.Format(Strings.CardChangedOnServer, updatedBy["server:".Length..]),
        _ => updatedBy,
    };

    public static string Verify(RunVerify? verify) => verify switch
    {
        null => string.Empty,
        { Ok: false } => Strings.Format(Strings.HistoryVerifyFailed, string.Join(", ", verify.FailedSets)),
        { Scope: VerifyScopes.Stored } => Strings.Format(Strings.HistoryVerifyOkStored, verify.SetsChecked),
        _ => Strings.HistoryVerifyOkNew,
    };

    public static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string PresetTitle(string? preset) => preset switch
    {
        RemotePresets.IspmanagerSites => Strings.RemotePresetIspSites,
        RemotePresets.IspmanagerFull => Strings.RemotePresetIspFull,
        RemotePresets.Linux => Strings.RemotePresetLinux,
        _ => Strings.RemotePresetCustom,
    };
}

/// <summary>How a task or a run is shown: the icon and the colour of its label.</summary>
internal enum RowState
{
    /// <summary>Never ran.</summary>
    None,
    Ok,
    Warn,
    Bad,
    Running,
    Queued,

    /// <summary>The task is off.</summary>
    Off,
}
