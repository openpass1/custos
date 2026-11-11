using System.Globalization;

namespace Custos.Core.Text;

/// <summary>Sizes, speeds and durations in words, in the language of <see cref="Messages"/>.</summary>
public static class Sizes
{
    /// <summary>"3,2 ГБ", "512 Б".</summary>
    public static string Format(long bytes)
    {
        string[] units = [Messages.UnitBytes, Messages.UnitKilobytes, Messages.UnitMegabytes, Messages.UnitGigabytes, Messages.UnitTerabytes];
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string number = unit == 0 || value >= 100
            ? Math.Round(value).ToString("0", Messages.FormatCulture)
            : value.ToString("0.0", Messages.FormatCulture);
        return number + " " + units[unit];
    }

    /// <summary>"48,6 МБ/с".</summary>
    public static string FormatSpeed(long bytesPerSecond) => Messages.Format(Messages.SpeedFormat, Format(bytesPerSecond));

    /// <summary>"1 ч 05 мин", "4 мин 12 с", "3 с".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return Messages.Format(Messages.DurationHours, (int)duration.TotalHours, duration.Minutes.ToString("00", CultureInfo.InvariantCulture));
        }

        if (duration.TotalMinutes >= 1)
        {
            return Messages.Format(Messages.DurationMinutes, duration.Minutes, duration.Seconds.ToString("00", CultureInfo.InvariantCulture));
        }

        return Messages.Format(Messages.DurationSeconds, (int)Math.Round(duration.TotalSeconds));
    }

    /// <summary>Russian plural: the form for 1, for 2–4 and for 5 and more (21 файл, 22 файла, 25 файлов).
    /// Other languages use the first two forms (one, many).</summary>
    public static string Plural(long count, string one, string few, string many)
    {
        if (!Messages.IsRussian)
        {
            return Math.Abs(count) == 1 ? one : many;
        }

        long n = Math.Abs(count) % 100;
        if (n is >= 11 and <= 14)
        {
            return many;
        }

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}

/// <summary>Words for the values of tasks and runs, in the language of <see cref="Messages"/>.</summary>
public static class Words
{
    public static string BackupType(string type) => type switch
    {
        Model.BackupTypes.Full => Messages.TypeFull,
        Model.BackupTypes.Incremental => Messages.TypeIncremental,
        Model.BackupTypes.Differential => Messages.TypeDifferential,
        Model.BackupTypes.Mirror => Messages.TypeMirror,
        _ => type,
    };

    public static string Status(string? status) => status switch
    {
        Model.RunStatuses.Success => Messages.StatusSuccess,
        Model.RunStatuses.Warning => Messages.StatusWarning,
        Model.RunStatuses.Error => Messages.StatusError,
        Model.RunStatuses.Aborted => Messages.StatusAborted,
        null => Messages.StatusRunning,
        _ => status,
    };

    /// <summary>What happens to the computer after a run: "выключение компьютера", "переход в сон".</summary>
    public static string PowerAction(string action) => action switch
    {
        Model.AfterActions.Shutdown => Messages.PowerShutdown,
        Model.AfterActions.Restart => Messages.PowerRestart,
        Model.AfterActions.Sleep => Messages.PowerSleep,
        Model.AfterActions.Hibernate => Messages.PowerHibernate,
        _ => action,
    };

    /// <summary>How a run was started: "по расписанию", "вручную, OFFICE\anna", "догоняющий за 27.10 00:00".</summary>
    public static string Trigger(string trigger, DateTimeOffset? scheduledFor, string? requestedBy) => trigger switch
    {
        Model.RunTriggers.Schedule => Messages.TriggerSchedule,
        Model.RunTriggers.CatchUp => Messages.Format(Messages.TriggerCatchUp,
            scheduledFor is { } slot ? slot.ToLocalTime().ToString("g", Messages.FormatCulture) : "?"),
        Model.RunTriggers.Manual => string.IsNullOrEmpty(requestedBy) ? Messages.TriggerManual : Messages.Format(Messages.TriggerManualBy, requestedBy),
        Model.RunTriggers.Remote => Messages.TriggerRemote,
        _ => trigger,
    };
}
