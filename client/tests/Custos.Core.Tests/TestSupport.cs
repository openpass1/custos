using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Databases;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>A folder under the temp directory, deleted with everything inside on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "custos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file (creating its folders) and gives it a known modification time.</summary>
    public string WriteFile(string relativePath, string content, DateTime modifiedUtc)
    {
        string path = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    public void Dispose()
    {
        try
        {
            SetCatalog.DeleteSetFolder(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the system to clean up.
        }
    }
}

/// <summary>Collects what the engine writes to the run log.</summary>
internal sealed class TestLog : IRunLog
{
    private readonly Lock _lock = new();

    public List<(RunLogLevel Level, string Message)> Lines { get; } = [];

    public void Write(RunLogLevel level, string message)
    {
        lock (_lock)
        {
            Lines.Add((level, message));
        }
    }
}

/// <summary>The local platform with a fixed computer name, USB disks by label and a settable free space.</summary>
internal sealed class TestPlatform : LocalBackupPlatform
{
    public Dictionary<string, string> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long? FreeSpace { get; set; }

    /// <summary>Archives of a test (for example a 7z.dll that does not exist); null = the usual ones.</summary>
    public IArchiveProvider? ArchivesOverride { get; set; }

    public override IArchiveProvider Archives => ArchivesOverride ?? base.Archives;

    public override string ComputerName => "TESTPC";

    /// <summary>The fake drives behind <see cref="LocalBackupPlatform.CloudAccounts"/>, by account id.</summary>
    public Dictionary<string, FakeCloud> Clouds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override string? FindVolumeByLabel(string label) => Volumes.TryGetValue(label, out string? root) ? root : null;

    /// <summary>What folders and shares answer to <see cref="Destinations.DestinationStore.CanRenameFolders"/> (false: a NAS
    /// with WORM); null = they try.</summary>
    public bool? FolderRenames { get; set; }

    /// <summary>Puts every store the engine opens into another one (<see cref="GuardedStore"/>); null = as it is.</summary>
    public Func<Destinations.DestinationStore, Destinations.DestinationStore>? WrapStore { get; set; }

    public override Destinations.DestinationStore OpenStore(TaskDestination destination, INetworkSession? session = null)
    {
        var store = base.OpenStore(destination, session);
        if (store is Destinations.LocalStore local)
        {
            local.KnownFolderRenames = FolderRenames;
        }

        return WrapStore is null ? store : WrapStore(store);
    }

    public override long? GetFreeSpace(string path) => FreeSpace;

    /// <summary>Files the engine asked to read and write with low I/O priority.</summary>
    public int LoweredFiles => _loweredFiles;

    private int _loweredFiles;

    public override void LowerIoPriority(Microsoft.Win32.SafeHandles.SafeFileHandle handle) => Interlocked.Increment(ref _loweredFiles);

    /// <summary>Permissions "set" by the engine: path, folder or file, and the descriptor (the source path in UTF-8).</summary>
    public List<(string Path, bool Folder, string Descriptor)> SecurityWritten { get; } = [];

    /// <summary>Setting permissions fails, as on a share that does not allow it.</summary>
    public bool SecurityBroken { get; set; }

    public override byte[]? ReadSecurity(string path, bool folder) => System.Text.Encoding.UTF8.GetBytes(path);

    public override bool WriteSecurity(string path, bool folder, byte[] descriptor)
    {
        if (SecurityBroken)
        {
            throw new BackupException("access denied");
        }

        SecurityWritten.Add((path, folder, System.Text.Encoding.UTF8.GetString(descriptor)));
        return true;
    }

    /// <summary>Environment variables of the test; the profile ones only for <see cref="ProfileSid"/>.</summary>
    public Dictionary<string, string> EnvironmentValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SID whose profile variables <see cref="EnvironmentValues"/> holds.</summary>
    public string ProfileSid { get; set; } = "S-1-5-21-1-2-3-1001";

    public override Func<string, string?> Variables(string? profileSid) => name =>
        PathVariables.ProfileNames.Contains(name) && profileSid != ProfileSid ? null
        : EnvironmentValues.TryGetValue(name, out string? value) ? value : Environment.GetEnvironmentVariable(name);

    /// <summary>Database clients of a test (<see cref="FakeDbClient"/>); null = the real programs.</summary>
    public Func<TaskDatabaseServer, DbAccount?, IDbClient>? DbClients { get; set; }

    /// <summary>The Linux server of a test (<see cref="FakeLinuxServer"/>); null = SSH.NET.</summary>
    public Remote.ISshConnector? SshOverride { get; set; }

    public override Remote.ISshConnector Ssh => SshOverride ?? base.Ssh;

    public override IDbClient CreateDbClient(TaskDatabaseServer server, DbAccount? account) =>
        DbClients?.Invoke(server, account) ?? base.CreateDbClient(server, account);

    /// <summary>Services started (true) or stopped (false) by events, in order.</summary>
    public List<(string Name, bool Start)> Services { get; } = [];

    /// <summary>A service name that fails to start or stop.</summary>
    public string? BrokenService { get; set; }

    public override void ControlService(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.Equals(name, BrokenService, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupException(Messages.Format(Messages.EventServiceFailed, name, "broken"));
        }

        Services.Add((name, start));
    }
}

/// <summary>7z needs the 7-Zip library: 7z.dll on Windows (CUSTOS_7Z_DLL, set by build.ps1), 7z.so built from the
/// sources elsewhere (CUSTOS_7Z_DLL, set by the macOS CI or by hand).</summary>
internal static class SevenZipCheck
{
    /// <summary>Skips a 7z test where 7z cannot work; with CUSTOS_REQUIRE_7Z=1 (the Windows and macOS CI) it fails
    /// instead, so that a lost library cannot quietly turn every 7z test off.</summary>
    public static void SkipUnlessAvailable(string format)
    {
        if (format != ArchiveFormats.SevenZip)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable("CUSTOS_7Z_DLL");
        bool available = !string.IsNullOrEmpty(path) && File.Exists(path);
        if (!available && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_7Z") == "1")
        {
            Assert.Fail("The 7-Zip library is required here (CUSTOS_REQUIRE_7Z=1), but CUSTOS_7Z_DLL does not point to it.");
        }

        Assert.SkipUnless(available, "7z needs the 7-Zip library (CUSTOS_7Z_DLL: 7z.dll or 7z.so).");
    }
}

/// <summary>
/// The usual archives, but every writer opens the files sorted by path the way 7-Zip does (upper case, ordinal), so
/// that what 7z does on Windows can be tested with ZIP anywhere: «custos-set.json» comes before «Data\…».
/// </summary>
internal sealed class NameSortingArchives(IArchiveProvider inner) : IArchiveProvider
{
    /// <summary>Called just before an item is opened (to delete or lock its file at that moment).</summary>
    public Action<ArchiveItem>? BeforeOpen { get; set; }

    public string? SevenZipVersion => inner.SevenZipVersion;

    public IArchiveWriter CreateWriter(string format) => new Writer(inner.CreateWriter(format), this);

    public IArchiveReader OpenReader(string format, Stream stream, string? password) => inner.OpenReader(format, stream, password);

    private sealed class Writer(IArchiveWriter inner, NameSortingArchives owner) : IArchiveWriter
    {
        public void Write(IReadOnlyList<ArchiveItem> items, Stream output, ArchiveWriteOptions options, CancellationToken cancellationToken)
        {
            var sorted = items.Where(i => i.IsFolder)
                .Concat(items.Where(i => !i.IsFolder).OrderBy(i => ArchivePaths.ToWindows(i.Path).ToUpperInvariant(), StringComparer.Ordinal))
                .Select(item => item.Open is null ? item : new ArchiveItem
                {
                    Path = item.Path,
                    IsFolder = item.IsFolder,
                    Size = item.Size,
                    ModifiedUtc = item.ModifiedUtc,
                    CreatedUtc = item.CreatedUtc,
                    Attributes = item.Attributes,
                    Separate = item.Separate,
                    Open = () =>
                    {
                        owner.BeforeOpen?.Invoke(item);
                        return item.Open();
                    },
                })
                .ToList();
            inner.Write(sorted, output, options, cancellationToken);
        }
    }
}

/// <summary>A database server without a server: databases and their dumps are given by the test.</summary>
internal sealed class FakeDbClient(string engine) : IDbClient
{
    public string Version { get; set; } = "8.0.36";

    public List<string> Databases { get; } = [];

    public string? DataDir { get; set; }

    /// <summary>Thrown by <see cref="Inspect"/>, as when the server is not running.</summary>
    public DbException? Unavailable { get; set; }

    /// <summary>Dumps that break off after some SQL: the program ends with code 2 and no end line.</summary>
    public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

    /// <summary>Dumps whose program fails at once, writing nothing.</summary>
    public HashSet<string> Refused { get; } = new(StringComparer.Ordinal);

    /// <summary>What was dumped, in order (null = the users).</summary>
    public List<string?> Dumped { get; } = [];

    public DbServerInfo Inspect(CancellationToken cancellationToken) =>
        Unavailable is not null ? throw Unavailable : new DbServerInfo(Version, [.. Databases], DataDir);

    public IDbDump StartDump(string? database, string version, CancellationToken cancellationToken)
    {
        Dumped.Add(database);
        string name = database ?? "_users";
        if (Refused.Contains(name))
        {
            return new Dump(string.Empty, new DbDumpExit(2, "Access denied for user"));
        }

        return Broken.Contains(name)
            ? new Dump(Sql(name, complete: false), new DbDumpExit(2, "Lost connection to server during query"))
            : new Dump(Sql(name, complete: true), new DbDumpExit(0, string.Empty));
    }

    /// <summary>The SQL a complete dump of <paramref name="name"/> has.</summary>
    public string Sql(string name, bool complete = true)
    {
        var text = new System.Text.StringBuilder();
        text.Append("-- dump of ").Append(name).Append('\n');
        text.Append("CREATE TABLE t (id int);\nINSERT INTO t VALUES (1);\n");
        if (complete)
        {
            text.Append(engine == DbEngines.PostgreSql ? "--\n-- PostgreSQL database dump complete\n--\n\n\\unrestrict abc\n\n" : "-- Dump completed on 2026-11-01 10:00:00\n");
        }

        return text.ToString();
    }

    private sealed class Dump(string sql, DbDumpExit exit) : IDbDump
    {
        public Stream Output { get; } = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sql));

        public DbDumpExit WaitForExit() => exit;

        public void Dispose() => Output.Dispose();
    }
}
