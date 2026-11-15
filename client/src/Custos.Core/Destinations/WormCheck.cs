using System.Globalization;
using System.Text;
using Custos.Core.Json;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>What <see cref="WormCheck.Run"/> found.</summary>
public enum WormCheckOutcome
{
    /// <summary>A new check file was written; a later run tries to delete it.</summary>
    Started,

    /// <summary>Too early or too late to tell anything; nothing was done.</summary>
    Waiting,

    /// <summary>The storage refused to delete the check file while it should be protected: WORM works.</summary>
    Protected,

    /// <summary>The check file was deleted while it should be protected: WORM does not protect it.</summary>
    NotProtected,

    /// <summary>The check file is still protected after the days of the destination: the storage keeps it longer.</summary>
    ProtectedLonger,
}

/// <summary>The outcome of a check and the time the check file was written.</summary>
/// <param name="Next">When the next step of the check is due.</param>
public readonly record struct WormCheckResult(WormCheckOutcome Outcome, DateTimeOffset Written, DateTimeOffset Next);

/// <summary>
/// Shows that a destination with WORM really protects the files of backups (section 6.3). The storage does not tell
/// whether it has WORM, so a run leaves a small file there, written as a copied file is: with an old modification time.
/// A run after the lock delay tries to delete it: refused means protected. When its days are over the file is deleted
/// and the check starts again; a NAS on which WORM was turned off is noticed within those days.
/// </summary>
public static class WormCheck
{
    public const string FileName = ".custos-worm-check";

    /// <summary>The modification time the check file gets, like a copy of an old file.</summary>
    public static readonly DateTime OldTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string WrittenPrefix = "written: ";

    /// <summary>Takes the next step of the check in the folder of <paramref name="store"/>.</summary>
    /// <exception cref="IOException">The check file could not be read or written.</exception>
    public static WormCheckResult Run(DestinationStore store, WormSettings worm, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(worm);
        string path = store.Combine(store.Root, FileName);
        if (ReadWritten(store, path) is not { } written)
        {
            return Start(store, path, worm, now);
        }

        var age = now - written;
        if (age < worm.LockedAfter)
        {
            return new WormCheckResult(WormCheckOutcome.Waiting, written, written + worm.LockedAfter);
        }

        // Locked by now, and free again only after the lock delay and the days.
        var freed = worm.Protection - WormSettings.Margin;
        bool deleted = TryDelete(store, path);
        if (age < freed)
        {
            if (!deleted)
            {
                return new WormCheckResult(WormCheckOutcome.Protected, written, written + worm.Protection);
            }

            var next = Start(store, path, worm, now);
            return new WormCheckResult(WormCheckOutcome.NotProtected, written, next.Next);
        }

        if (deleted)
        {
            return Start(store, path, worm, now);
        }

        return age < worm.Protection
            ? new WormCheckResult(WormCheckOutcome.Waiting, written, written + worm.Protection)
            : new WormCheckResult(WormCheckOutcome.ProtectedLonger, written, now + TimeSpan.FromDays(1));
    }

    /// <summary>The time the check file says it was written; null when there is none (or nothing readable).</summary>
    private static DateTimeOffset? ReadWritten(DestinationStore store, string path)
    {
        string text;
        try
        {
            using var stream = store.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith(WrittenPrefix, StringComparison.Ordinal)
                && DateTimeOffset.TryParse(line[WrittenPrefix.Length..].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var written))
            {
                return written.ToUniversalTime();
            }
        }

        // Not a check file of ours: it goes if it can, and the check starts anew.
        TryDelete(store, path);
        return null;
    }

    private static WormCheckResult Start(DestinationStore store, string path, WormSettings worm, DateTimeOffset now)
    {
        string text = "CUSTOS: test of the protection from deletion (WORM). Do not delete; CUSTOS removes it itself.\n"
            + WrittenPrefix + UtcTimeConverter.ToText(now) + "\n";
        using (var writer = store.BeginWrite(path, Backup.BackupEngine.TempSuffix, StoreWriteMode.Stream))
        {
            writer.Stream.Write(Encoding.UTF8.GetBytes(text));
            writer.Commit();
        }

        store.SetFileTimes(path, now.UtcDateTime, OldTime, FileAttributes.Normal);
        return new WormCheckResult(WormCheckOutcome.Started, now, now + worm.LockedAfter);
    }

    private static bool TryDelete(DestinationStore store, string path)
    {
        try
        {
            store.DeleteFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
