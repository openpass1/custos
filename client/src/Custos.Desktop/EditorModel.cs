using System.Globalization;
using Avalonia;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Scheduling;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>A piece of an editor page; EditorWindow.axaml has a template for each kind.</summary>
internal abstract record EditorItem;

internal sealed record HeadingItem(string Text, string? Help = null) : EditorItem
{
    public bool HasHelp => Help is not null;
}

internal sealed record NoteItem(string Text, bool Warning = false) : EditorItem;

/// <summary>An outlined text field with its label on the frame and a hint under it.</summary>
internal sealed record FieldItem(string Label, string Value, string? Hint = null, bool Multiline = false, string? Icon = null, double Width = 520) : EditorItem
{
    public double Height => Multiline ? 96 : double.NaN;

    public bool HasIcon => Icon is not null;
}

internal sealed record ChoiceItem(string Label, IReadOnlyList<string> Options, int Selected, string? Hint = null, double Width = 520) : EditorItem;

/// <summary>A switch with its title and description; in a tonal card when <see cref="Card"/>.</summary>
internal sealed record SwitchItem(string Title, string? Description, bool On, bool Card = false) : EditorItem
{
    public bool HasDescription => Description is not null;
}

internal sealed record CheckItem(string Title, bool On, string? Description = null, double Indent = 0) : EditorItem
{
    public bool HasDescription => Description is not null;

    public Thickness Offset => new(Indent, 0, 0, 0);
}

/// <summary>«☑ Каждый [7] -й запуск».</summary>
internal sealed record CheckFieldItem(string Title, string Value, string Suffix, bool On) : EditorItem;

/// <summary>«☑ В день недели [воскресенье ▾]».</summary>
internal sealed record CheckChoiceItem(string Title, IReadOnlyList<string> Options, int Selected, bool On) : EditorItem;

/// <summary>«Хранить последние [12] полных копий»: a number inside a sentence.</summary>
internal sealed record PhraseItem(string Prefix, string Value, string Suffix, double Indent = 0) : EditorItem
{
    public Thickness Offset => new(Indent, 0, 0, 0);
}

/// <summary>A radio button; with <see cref="Value"/> the title goes on with a number field and <see cref="Suffix"/>.</summary>
internal sealed record RadioItem(string Group, string Title, string? Hint, bool On, double Indent = 0, string? Value = null, string? Suffix = null) : EditorItem
{
    public bool HasHint => Hint is not null;

    public bool HasValue => Value is not null;

    public Thickness Offset => new(Indent, 0, 0, 0);
}

internal sealed record Segment(string Group, string Text, bool On);

internal sealed record SegmentsItem(string Label, IReadOnlyList<Segment> Segments) : EditorItem;

internal sealed record Chip(string Text, bool On);

internal sealed record ChipsItem(string Label, IReadOnlyList<Chip> Chips) : EditorItem;

internal sealed record CardTag(string Icon, string Text, bool Accent = false);

/// <summary>A source, a destination or an event: an icon, two lines, tags, and the buttons to change or remove it.</summary>
internal sealed record CardItem(string Icon, string Title, string? Subtitle, IReadOnlyList<CardTag> Tags) : EditorItem
{
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public bool HasTags => Tags.Count > 0;
}

internal sealed record ButtonItem(string Icon, string Text) : EditorItem;

/// <summary>The schedule «in plain words» and the next runs.</summary>
internal sealed record InfoItem(string Icon, string Title, string? Detail) : EditorItem
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>Several items side by side.</summary>
internal sealed record RowItem(IReadOnlyList<EditorItem> Items) : EditorItem;

internal sealed record EditorPage(string Icon, string Title, string? Subtitle, IReadOnlyList<EditorItem> Items)
{
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
}

/// <summary>
/// The task editor of the new window: the pages of Custos.exe's editor (EditorPages.cs) with the values of the task,
/// in Material 3 controls. Nothing typed here goes anywhere.
/// </summary>
internal sealed class EditorModel : Observable
{
    private readonly TaskDefinition _task;
    private readonly AgentSnapshot? _snapshot;
    private EditorPage _page;

    public EditorModel(TaskDefinition task, AgentSnapshot? snapshot, bool isNew)
    {
        _task = task;
        _snapshot = snapshot;
        Title = isNew ? Strings.EditorTitleNew : Strings.Format(Strings.EditorTitle, task.Name);
        Revision = isNew ? Strings.EditorNewTask
            : Strings.Format(Strings.EditorRevision, task.Revision, Describe.Time(task.UpdatedAt), Describe.Author(task.UpdatedBy));
        var pages = new List<EditorPage> { Basic(), Sources() };
        if (task.DbServers.Count > 0)
        {
            pages.Add(Databases());
        }

        pages.AddRange([Destinations(), Schedule(), Archive(), Retention(), Events(), Advanced()]);
        Pages = pages;
        _page = pages[0];
    }

    public string Title { get; }

    public string Revision { get; }

    public IReadOnlyList<EditorPage> Pages { get; }

    public EditorPage Page { get => _page; set => Set(ref _page, value); }

    public void Open(string title) => Page = Pages.FirstOrDefault(p => p.Title == title) ?? Page;

    private static string L(string label) => label.TrimEnd(':', ' ');

    private EditorPage Basic()
    {
        var items = new List<EditorItem>
        {
            new FieldItem(L(Strings.EditorName), _task.Name),
            new SwitchItem(Strings.EditorEnabled, null, _task.Enabled),
            new HeadingItem(Strings.EditorBackupType),
        };
        foreach (var (type, title, hint) in new[]
        {
            (BackupTypes.Full, Strings.TypeFullTitle, Strings.TypeFullHint),
            (BackupTypes.Incremental, Strings.TypeIncrementalTitle, Strings.TypeIncrementalHint),
            (BackupTypes.Differential, Strings.TypeDifferentialTitle, Strings.TypeDifferentialHint),
            (BackupTypes.Mirror, Strings.TypeMirrorTitle, Strings.TypeMirrorHint),
        })
        {
            items.Add(new RadioItem("type", title, hint, _task.BackupType == type));
        }

        if (_task.BackupType == BackupTypes.Mirror)
        {
            items.Add(new RadioItem("mirror", Strings.MirrorExact, null, _task.MirrorMode != MirrorModes.Safe, 40));
            items.Add(new RadioItem("mirror", Strings.MirrorSafe + " " + _task.Retention.SafeMirrorDays.ToString(CultureInfo.CurrentCulture) + " " + Strings.MirrorSafeDays,
                null, _task.MirrorMode == MirrorModes.Safe, 40));
        }
        else
        {
            var days = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
            var weekdays = Enumerable.Range(1, 7).Select(d => days[d % 7]).ToList();
            var monthDays = Enumerable.Range(1, 30).Select(d => d.ToString(CultureInfo.CurrentCulture)).Append(Strings.LastDayOfMonth).ToList();
            var force = _task.ForceFull;
            items.Add(new HeadingItem(Strings.EditorForceFull));
            items.Add(new CheckFieldItem(Strings.ForceEvery, (force.EveryRuns > 0 ? force.EveryRuns : 7).ToString(CultureInfo.CurrentCulture),
                Strings.ForceEveryRunsSuffix, force.EveryRuns > 0));
            items.Add(new CheckChoiceItem(Strings.ForceWeekday, weekdays, (force.Weekday ?? 7) - 1, force.Weekday is not null));
            items.Add(new CheckChoiceItem(Strings.ForceMonthDay, monthDays, force.MonthDay is -1 ? 30 : Math.Clamp((force.MonthDay ?? 1) - 1, 0, 29),
                force.MonthDay is not null));
            items.Add(new NoteItem(Strings.ForceFullNote));
        }

        items.Add(new HeadingItem(Strings.EditorComment));
        items.Add(new FieldItem(string.Empty, _task.Comment, Multiline: true));
        return new EditorPage("description", Strings.PageBasic, null, items);
    }

    private EditorPage Sources()
    {
        var items = new List<EditorItem>();
        foreach (var source in _task.Sources)
        {
            if (source.Kind == SourceKinds.Ssh)
            {
                items.Add(new CardItem("dns", DesktopStrings.LinuxServer + " " + source.Host, source.Path, []));
                continue;
            }

            bool network = source.Kind == SourceKinds.Unc || source.Path.StartsWith(@"\\", StringComparison.Ordinal);
            string? subtitle = source.Kind == SourceKinds.File ? Strings.SourceFile
                : source.Recursive ? Strings.SourceRecursive : Strings.Format(Strings.CardNotRecursive, Strings.SourceFolder);
            if (Account(source.CredentialId) is { } account)
            {
                subtitle += " · " + account;
            }

            items.Add(new CardItem(source.Kind == SourceKinds.File ? "description" : network ? "lan" : "folder", source.Path, subtitle, []));
        }

        items.Add(new RowItem([new ButtonItem("add", Strings.SourceAddTitle), new ButtonItem("dns", Strings.SourceAddLinux)]));
        items.Add(new SwitchItem(Strings.KeepTopDir, Strings.KeepTopDirHelp.Replace("\\n", Environment.NewLine, StringComparison.Ordinal), _task.Layout.KeepTopDir));
        items.Add(new HeadingItem(Strings.FiltersHeading));
        items.Add(new NoteItem(Strings.FiltersNote));
        var filters = _task.Filters;
        items.Add(new FieldItem(L(Strings.FilterInclude), string.Join("; ", filters.Include), Strings.FilterIncludeHelp));
        items.Add(new FieldItem(L(Strings.FilterExclude), string.Join("; ", filters.Exclude), Strings.FilterExcludeHelp));
        items.Add(new FieldItem(L(Strings.FilterExcludeDirs), string.Join(Environment.NewLine, filters.ExcludeDirs), Strings.FilterExcludeDirsHelp, Multiline: true, Icon: "folder_open"));
        items.Add(new CheckItem(Strings.SkipHidden, filters.SkipHidden));
        items.Add(new CheckItem(Strings.SkipSystem, filters.SkipSystem));
        items.Add(new CheckItem(Strings.SkipTemp, filters.SkipTemp, Strings.SkipTempHelp));
        return new EditorPage("folder", Strings.PageSources, Strings.SourcesNote, items);
    }

    private EditorPage Databases()
    {
        var items = new List<EditorItem>();
        foreach (var server in _task.DbServers)
        {
            items.Add(new CardItem("database", DbEngines.Title(server.Engine) + " " + server.Host, Describe.Databases(_task), []));
        }

        return new EditorPage("database", Strings.PageDatabases, null, items);
    }

    private EditorPage Destinations()
    {
        var items = new List<EditorItem>();
        foreach (var destination in _task.Destinations)
        {
            var tags = new List<CardTag>();
            if (WormSettings.Of(destination) is { } worm)
            {
                tags.Add(new CardTag("lock_clock", Strings.Format(DesktopStrings.WormTag, worm.Days), Accent: true));
            }

            if (destination.Kind == DestinationKinds.S3 && destination.ObjectLockDays > 0)
            {
                tags.Add(new CardTag("lock_clock", Strings.Format(DesktopStrings.ObjectLockTag, destination.ObjectLockDays), Accent: true));
            }

            if (destination.SpeedLimitKbps > 0)
            {
                tags.Add(new CardTag("speed", Sizes.FormatSpeed(destination.SpeedLimitKbps * 1024L)));
            }

            string? account = Account(destination.CredentialId);
            // The title names the place; the line under it says what kind it is where the title does not.
            var (icon, title, subtitle) = destination.Kind switch
            {
                DestinationKinds.Local => ("folder", destination.Path, Strings.KindLocal),
                DestinationKinds.Unc => ("lan", destination.Path, Strings.KindUnc),
                DestinationKinds.Usb => ("usb", TaskDescriber.Destination(destination), (string?)null),
                _ when DestinationKinds.IsCloudDrive(destination.Kind) => ("cloud_upload", TaskDescriber.Destination(destination), null),
                _ => ("dns", TaskDescriber.Destination(destination), null),
            };
            if (account is not null)
            {
                subtitle = string.IsNullOrEmpty(subtitle) ? account : subtitle + " · " + account;
            }

            items.Add(new CardItem(icon, title, subtitle, tags));
        }

        items.Add(new ButtonItem("add", Strings.DestinationAddTitle));
        items.Add(new HeadingItem(Strings.EditorLayout, Strings.SeparateFoldersHelp.Replace("\\n", Environment.NewLine, StringComparison.Ordinal)));
        if (_task.BackupType == BackupTypes.Mirror)
        {
            items.Add(new NoteItem(Strings.LayoutMirrorNote));
        }
        else
        {
            items.Add(new SwitchItem(Strings.SeparateFolders, null, _task.Layout.SeparateFolders));
            string example = Core.Backup.SetNaming.Expand(_task.Layout.NameTemplate, _task.Name.Length > 0 ? _task.Name : Strings.NewTaskName,
                DemoData.Now.LocalDateTime, "OFFICE-PC-03", _task.BackupType) + _task.Archive.Format switch
                {
                    ArchiveFormats.Zip => ".zip",
                    ArchiveFormats.SevenZip => ".7z",
                    _ => string.Empty,
                };
            items.Add(new ChoiceItem(L(Strings.NameTemplate), [_task.Layout.NameTemplate, .. TemplatePresets.Where(t => t != _task.Layout.NameTemplate)], 0,
                Strings.Format(Strings.NameTemplateExample, example)));
        }

        return new EditorPage("hard_drive", Strings.PageDestinations, Strings.DestinationsNote, items);
    }

    private EditorPage Schedule()
    {
        var schedule = _task.Schedule;
        string[] types = [ScheduleTypes.Daily, ScheduleTypes.Weekly, ScheduleTypes.Monthly, ScheduleTypes.Interval, ScheduleTypes.Manual];
        string[] titles = [Strings.ScheduleDaily, Strings.ScheduleWeekly, Strings.ScheduleMonthly, Strings.ScheduleByInterval.Split(',')[0], Strings.ScheduleManual];
        var items = new List<EditorItem>
        {
            new SegmentsItem(L(Strings.ScheduleType), [.. types.Select((t, i) => new Segment("schedule", titles[i], schedule.Type == t))]),
        };
        if (schedule.Type is ScheduleTypes.Daily or ScheduleTypes.Weekly)
        {
            var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
            items.Add(new ChipsItem(L(Strings.ScheduleWeekdays), [.. Enumerable.Range(1, 7).Select(d => new Chip(names[d % 7], schedule.DaysOfWeek.Contains(d)))]));
        }

        if (schedule.Type == ScheduleTypes.Monthly)
        {
            string days = string.Join(", ", schedule.DaysOfMonth.Select(d => d == -1 ? Strings.LastDayOfMonth : d.ToString(CultureInfo.CurrentCulture)));
            items.Add(new FieldItem(L(Strings.ScheduleMonthDays), days, Icon: "event_available", Width: 360));
        }

        if (schedule.Type == ScheduleTypes.Interval)
        {
            items.Add(new PhraseItem(L(Strings.ScheduleInterval), schedule.IntervalMinutes.ToString(CultureInfo.CurrentCulture), DesktopStrings.EveryMinutes));
        }
        else if (schedule.Type != ScheduleTypes.Manual)
        {
            items.Add(new FieldItem(L(Strings.ScheduleTimes), string.Join(", ", schedule.Times), Strings.ScheduleTimesHelp, Icon: "schedule", Width: 360));
        }

        if (schedule.Type != ScheduleTypes.Manual)
        {
            items.Add(new SwitchItem(Strings.ScheduleCatchUp, Strings.ScheduleCatchUpHelp, schedule.CatchUp, Card: true));
            var next = ScheduleCalculator.Upcoming(schedule, DemoData.Now, TimeZoneInfo.Local, 3);
            items.Add(new InfoItem("event_available", TaskDescriber.ScheduleWithCatchUp(schedule),
                next.Count == 0 ? null : Strings.Format(Strings.ScheduleUpcoming, string.Join(", ", next.Select(t => Describe.Time(t))))));
        }

        return new EditorPage("schedule", Strings.PageSchedule, Strings.ScheduleTimeZoneNote, items);
    }

    private EditorPage Archive()
    {
        var archive = _task.Archive;
        var items = new List<EditorItem>();
        if (_task.BackupType == BackupTypes.Mirror)
        {
            items.Add(new NoteItem(Strings.ArchiveMirrorNote));
        }
        else
        {
            items.Add(new HeadingItem(Strings.ArchiveFormat));
            items.Add(new RadioItem("format", Strings.ArchiveNone, null, archive.Format == ArchiveFormats.None));
            items.Add(new RadioItem("format", Strings.ArchiveSevenZip, Strings.ArchiveSevenZipHelp, archive.Format == ArchiveFormats.SevenZip));
            items.Add(new RadioItem("format", Strings.ArchiveZip, Strings.ArchiveZipHelp, archive.Format == ArchiveFormats.Zip));
            if (archive.Format != ArchiveFormats.None)
            {
                items.Add(new ChoiceItem(L(Strings.ArchiveLevel), [.. Enumerable.Range(0, 10).Select(i => i.ToString(CultureInfo.CurrentCulture))],
                    Math.Clamp(archive.Level, 0, 9), Width: 240));
                items.Add(new NoteItem(Strings.ArchiveLevelHelp.Replace("\\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)));
                items.Add(new HeadingItem(Strings.ArchiveEncryption));
                items.Add(new SwitchItem(Strings.ArchiveEncrypt, archive.Encrypt ? Strings.ArchivePasswordSet : Strings.ArchivePasswordNotSet, archive.Encrypt));
                items.Add(new CheckItem(Strings.ArchiveEncryptNames, archive.EncryptNames, Strings.ArchiveEncryptNamesHelp));
                items.Add(new NoteItem(Strings.ArchivePasswordWarning, Warning: true));
            }
        }

        items.Add(new HeadingItem(Strings.ArchiveVerify));
        items.Add(new FieldItem(L(Strings.VerifyEveryRuns), _task.Verify.EveryRuns.ToString(CultureInfo.CurrentCulture), Strings.VerifyEveryRunsHelp));
        items.Add(new FieldItem(L(Strings.VerifyStoredDays), _task.Verify.StoredEveryDays.ToString(CultureInfo.CurrentCulture), Strings.VerifyStoredDaysHelp));
        return new EditorPage("folder_zip", Strings.PageArchive, Strings.ArchiveOpenAnywhere, items);
    }

    private EditorPage Retention()
    {
        var retention = _task.Retention;
        var items = new List<EditorItem>();
        if (_task.BackupType == BackupTypes.Mirror)
        {
            items.Add(new NoteItem(Strings.RetentionMirrorNote));
        }
        else
        {
            items.Add(new RadioItem("retention", Strings.RetentionNone, null, retention.Mode == RetentionModes.None));
            items.Add(new RadioItem("retention", Strings.RetentionCount, Strings.RetentionCountHint, retention.Mode == RetentionModes.Count,
                Value: (retention.KeepFull > 0 ? retention.KeepFull : 3).ToString(CultureInfo.CurrentCulture), Suffix: Strings.RetentionCountSuffix));
            items.Add(new RadioItem("retention", Strings.RetentionAge, Strings.RetentionAgeHint, retention.Mode == RetentionModes.Age,
                Value: (retention.MaxAgeDays > 0 ? retention.MaxAgeDays : 30).ToString(CultureInfo.CurrentCulture), Suffix: Strings.RetentionAgeSuffix));
            items.Add(new RadioItem("retention", Strings.RetentionGfs, Strings.RetentionGfsHint, retention.Mode == RetentionModes.Gfs));
            items.Add(new RowItem(
            [
                new PhraseItem(L(Strings.GfsDaily), retention.Gfs.Daily.ToString(CultureInfo.CurrentCulture), string.Empty, 40),
                new PhraseItem(L(Strings.GfsWeekly), retention.Gfs.Weekly.ToString(CultureInfo.CurrentCulture), string.Empty, 16),
                new PhraseItem(L(Strings.GfsMonthly), retention.Gfs.Monthly.ToString(CultureInfo.CurrentCulture), string.Empty, 16),
            ]));
            foreach (var destination in _task.Destinations)
            {
                if (WormSettings.Of(destination) is { } worm)
                {
                    items.Add(new NoteItem(Strings.Format(Strings.RetentionWormNote, worm.Days), Warning: true));
                    break;
                }
            }
        }

        items.Add(new HeadingItem(Strings.RetentionRules));
        items.Add(new NoteItem(Strings.RetentionRulesText.Replace("\\n", Environment.NewLine, StringComparison.Ordinal)));
        return new EditorPage("layers", Strings.PageRetention, null, items);
    }

    private EditorPage Events()
    {
        var events = _task.Events;
        var items = new List<EditorItem> { new HeadingItem(Strings.EventsPreHeading) };
        items.AddRange(events.Pre.Select(Card));
        items.Add(new ButtonItem("add", Strings.EventAddTitle));
        items.Add(new CheckItem(Strings.EventsAbortOnPre, events.AbortOnPreFailure));
        items.Add(new HeadingItem(Strings.EventsPostHeading));
        items.AddRange(events.Post.Select(Card));
        items.Add(new ButtonItem("add", Strings.EventAddTitle));
        items.Add(new NoteItem(Strings.EventsPostNote));
        items.Add(new HeadingItem(Strings.AfterHeading));
        string[] after = [AfterActions.None, AfterActions.Shutdown, AfterActions.Restart, AfterActions.Sleep, AfterActions.Hibernate];
        items.Add(new ChoiceItem(string.Empty, [Strings.AfterNone, Strings.AfterShutdown, Strings.AfterRestart, Strings.AfterSleep, Strings.AfterHibernate],
            Math.Max(0, Array.IndexOf(after, events.After)), Strings.AfterNote, Width: 360));
        return new EditorPage("bolt", Strings.PageEvents, Strings.EventsNote, items);

        static EditorItem Card(TaskEvent item) => new CardItem(
            item.Kind switch
            {
                TaskEventKinds.ServiceStop => "stop_circle",
                TaskEventKinds.ServiceStart => "play_circle",
                _ => "terminal",
            },
            Describe.Event(item),
            Strings.Format(Strings.EventTimeoutShown, Math.Max(1, item.TimeoutS / 60)),
            []);
    }

    private EditorPage Advanced()
    {
        string[] levels = [LogLevels.Inherit, LogLevels.Summary, LogLevels.Files];
        var items = new List<EditorItem>
        {
            new HeadingItem(Strings.EditorVss),
            new SwitchItem(Strings.VssEnabled, Strings.VssHelp, _task.Vss.Enabled),
            new ChoiceItem(L(Strings.VssOnFailure), [Strings.VssAbort, Strings.VssContinue], _task.Vss.OnFailure == VssFailureActions.Continue ? 1 : 0, Width: 360),
            new HeadingItem(Strings.EditorReading),
            new FieldItem(L(Strings.ReadRetries), _task.Copy.ReadRetries.ToString(CultureInfo.CurrentCulture), Strings.ReadRetriesHelp, Width: 360),
            new CheckItem(Strings.PreserveAcl, _task.Copy.PreserveAcl),
            new HeadingItem(Strings.EditorLogAndQueue),
            new ChoiceItem(L(Strings.LogLevel), [Strings.LogLevelInherit, Strings.LogLevelSummary, Strings.LogLevelFiles],
                Math.Max(0, Array.IndexOf(levels, _task.LogLevel)), Width: 360),
            new FieldItem(L(Strings.Priority), _task.Priority.ToString(CultureInfo.CurrentCulture), Strings.PriorityHelp, Width: 360),
        };
        return new EditorPage("tune", Strings.PageAdvanced, null, items);
    }

    /// <summary>«под учётной записью backup», or null.</summary>
    private string? Account(string? credentialId) =>
        credentialId is { } id && _snapshot?.Credentials.FirstOrDefault(c => Describe.SameId(c.Id, id)) is { } credential
            ? Strings.Format(Strings.CardAsAccount, string.Empty, credential.DisplayName).TrimStart(' ', '—')
            : null;

    /// <summary>The name templates offered in the list (EditorPages.cs, DestinationsPage).</summary>
    private static readonly string[] TemplatePresets =
    [
        LayoutOptions.DefaultNameTemplate,
        "{task} {yyyy}-{MM}-{dd} {HH}-{mm}",
        "{task} {yyyy}-{MM}-{dd} ({type})",
        "{yyyy}-{MM}-{dd} {HH}-{mm} {task} ({type})",
        "{computer} {task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})",
    ];
}
