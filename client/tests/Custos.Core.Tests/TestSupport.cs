using Custos.Core.Archives;
using Custos.Core.Backup;
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

    public override string? FindVolumeByLabel(string label) => Volumes.TryGetValue(label, out string? root) ? root : null;

    public override long? GetFreeSpace(string path) => FreeSpace;
}

/// <summary>7z works only on Windows with a 7z.dll (CUSTOS_7Z_DLL, set by build.ps1).</summary>
internal static class SevenZipCheck
{
    /// <summary>Skips a 7z test where 7z cannot work; with CUSTOS_REQUIRE_7Z=1 (the Windows CI) it fails instead, so
    /// that a lost 7z.dll cannot quietly turn every 7z test off.</summary>
    public static void SkipUnlessAvailable(string format)
    {
        if (format != ArchiveFormats.SevenZip)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable("CUSTOS_7Z_DLL");
        bool available = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(path) && File.Exists(path);
        if (!available && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_7Z") == "1")
        {
            Assert.Fail("7z.dll is required here (CUSTOS_REQUIRE_7Z=1), but CUSTOS_7Z_DLL does not point to it.");
        }

        Assert.SkipUnless(available, "7z needs Windows and 7z.dll (CUSTOS_7Z_DLL).");
    }
}
