namespace Custos.Desktop;

/// <summary>A label of a state: icon name and the class that colours it (ok, warn, bad, run).</summary>
internal abstract class StateItem : Observable
{
    private RowState _state;

    public RowState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Changed(nameof(StateIcon));
                Changed(nameof(ResultIcon));
                Changed(nameof(IsOk));
                Changed(nameof(IsWarn));
                Changed(nameof(IsBad));
                Changed(nameof(IsRun));
                Changed(nameof(IsNone));
                Changed(nameof(IsRunning));
                Changed(nameof(HasResultIcon));
            }
        }
    }

    /// <summary>The icon at the start of a row: never ran = an empty ring, off = a grey circle with a line.</summary>
    public string StateIcon => State switch
    {
        RowState.Ok => "check_circle",
        RowState.Warn => "warning",
        RowState.Bad => "cancel",
        RowState.Running => "sync",
        RowState.Queued => "schedule",
        RowState.Off => "do_not_disturb_on",
        _ => "radio_button_unchecked",
    };

    /// <summary>The icon inside the result label (none for «never ran» and «running», which has the ring).</summary>
    public string ResultIcon => State switch
    {
        RowState.Queued => "hourglass_top",
        _ => StateIcon,
    };

    public bool HasResultIcon => State is not (RowState.None or RowState.Running);

    public bool IsOk => State == RowState.Ok;

    public bool IsWarn => State == RowState.Warn;

    public bool IsBad => State == RowState.Bad;

    public bool IsRun => State is RowState.Running or RowState.Queued;

    public bool IsRunning => State == RowState.Running;

    public bool IsNone => State == RowState.None;
}

/// <summary>A row of the task list.</summary>
internal sealed class TaskRow : StateItem
{
    private string _name = string.Empty;
    private string _type = string.Empty;
    private string _schedule = string.Empty;
    private string _lastRun = string.Empty;
    private string _result = string.Empty;
    private string _nextRun = string.Empty;
    private double _percent;

    public required string TaskId { get; init; }

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Type { get => _type; set => Set(ref _type, value); }

    public string Schedule { get => _schedule; set => Set(ref _schedule, value); }

    public string LastRun { get => _lastRun; set => Set(ref _lastRun, value); }

    public string Result { get => _result; set => Set(ref _result, value); }

    public string NextRun { get => _nextRun; set => Set(ref _nextRun, value); }

    public double Percent { get => _percent; set => Set(ref _percent, value); }

    public DateTimeOffset? LastRunTime { get; set; }

    public DateTimeOffset? NextRunTime { get; set; }

    public void CopyFrom(TaskRow other)
    {
        Name = other.Name;
        Type = other.Type;
        Schedule = other.Schedule;
        LastRun = other.LastRun;
        Result = other.Result;
        NextRun = other.NextRun;
        Percent = other.Percent;
        State = other.State;
        LastRunTime = other.LastRunTime;
        NextRunTime = other.NextRunTime;
    }
}

/// <summary>A line of the card: «Источники — D:\Photos».</summary>
internal sealed record CardRow(string Key, string Value, bool Accent = false);

/// <summary>The card of the selected task.</summary>
internal sealed class CardModel : StateItem
{
    private string _progressLine = string.Empty;
    private double _percent;
    private bool _live;
    private bool _showProgress;

    public required string TaskId { get; init; }

    public required string Name { get; init; }

    public required string Subtitle { get; init; }

    public string? Error { get; init; }

    public bool HasError => Error is not null;

    public required IReadOnlyList<CardRow> Rows { get; init; }

    public bool ShowProgress { get => _showProgress; set => Set(ref _showProgress, value); }

    public double Percent { get => _percent; set => Set(ref _percent, value); }

    public bool Live { get => _live; set => Set(ref _live, value); }

    public string ProgressLine { get => _progressLine; set => Set(ref _progressLine, value); }
}

/// <summary>A line of the journal.</summary>
internal sealed class LogRow
{
    public required long Seq { get; init; }

    public required string Time { get; init; }

    public required string Level { get; init; }

    public string? TaskId { get; init; }

    /// <summary>"Photos: " before the text, or empty for the lines of the service itself.</summary>
    public required string Task { get; init; }

    public required string Text { get; init; }

    public string Icon => Level switch
    {
        Core.Ipc.LogLineLevels.Error => "error",
        Core.Ipc.LogLineLevels.Warning => "warning",
        Core.Ipc.LogLineLevels.Detail => "radio_button_unchecked",
        _ => "info",
    };

    public bool IsInfo => Level == Core.Ipc.LogLineLevels.Info;

    public bool IsWarn => Level == Core.Ipc.LogLineLevels.Warning;

    public bool IsBad => Level == Core.Ipc.LogLineLevels.Error;
}

/// <summary>A run in the history of the selected task.</summary>
internal sealed class HistoryRow : StateItem
{
    public required string Started { get; init; }

    public required string Duration { get; init; }

    public required string Type { get; init; }

    public required string Trigger { get; init; }

    public required string Files { get; init; }

    public required string Size { get; init; }

    public required string Result { get; init; }

    public required string Details { get; init; }
}

/// <summary>A destination of the run in progress.</summary>
internal sealed record DestinationRow(string Label, string State, string Error, bool Failed);
