using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Custos.Core.Archives.SevenZip;

/// <summary>
/// Puts the 7z.dll of a 7-Zip installer into the program folder of CUSTOS: one operation for the button on the client
/// and, later, for updates sent by the server. The installer must already be downloaded and checked; this unpacks it
/// with the 7z.dll in use, tests the new one, and only then replaces the file.
/// </summary>
/// <remarks>
/// A loaded DLL cannot be loaded again from the same path in the same process, so the new 7z.dll is used from the
/// staging folder until the service restarts; the next start loads it from the program folder. The old file stays as
/// <c>7z.dll.old</c> to go back by hand.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SevenZipInstaller
{
    public const string LicenseName = "7-Zip-License.txt";
    public const string OldSuffix = ".old";

    /// <summary>How far into the installer to look for the 7z archive (it follows a small .exe that unpacks it).</summary>
    private const long SfxSearchLimit = 16L * 1024 * 1024;

    private const string TestPassword = "custos-self-test";

    /// <summary>Installs the 7z.dll of <paramref name="installer"/>.</summary>
    /// <param name="installer">The x64 installer of 7-Zip (7z2606-x64.exe), already checked by the caller.</param>
    /// <param name="current">The 7z.dll in use: it unpacks the installer.</param>
    /// <param name="programFolder">Where the service loads 7z.dll from at start.</param>
    /// <param name="staging">An empty folder only SYSTEM and administrators may write to.</param>
    /// <param name="log">Each step, in words, for the live log.</param>
    /// <returns>The new 7z.dll, loaded and tested.</returns>
    /// <exception cref="ArchiveException">Something failed; the 7z.dll in the program folder is the old one.</exception>
    public static SevenZipLibrary Install(string installer, SevenZipLibrary current, string programFolder, string staging, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(log);
        Directory.CreateDirectory(staging);
        string dll = Path.Combine(staging, SevenZipLibrary.FileName);
        string license = Path.Combine(staging, "License.txt");

        log(Messages.Format(Messages.SevenZipUnpacking, Path.GetFileName(installer)));
        Unpack(installer, current, staging, cancellationToken);

        var library = SevenZipLibrary.Load(dll);
        SelfTest(library, cancellationToken);
        log(Messages.Format(Messages.SevenZipSelfTestOk, library.Version));

        Replace(programFolder, dll, File.Exists(license) ? license : null);
        return library;
    }

    /// <summary>Packs and unpacks a little data with a password, as the tasks do.</summary>
    /// <exception cref="ArchiveException">The library does not do it right.</exception>
    public static void SelfTest(SevenZipLibrary library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        // Half noise, half zeros: both the compressor and the plain copy of incompressible data are used.
        byte[] data = new byte[256 * 1024];
        uint state = 2606;
        for (int i = 0; i < data.Length / 2; i++)
        {
            state = (state * 1664525) + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        try
        {
            var archive = new MemoryStream();
            new SevenZipWriter(library).Write(
                [new ArchiveItem { Path = @"custos\test.bin", Size = data.Length, ModifiedUtc = DateTime.UtcNow, Open = () => new MemoryStream(data, writable: false) }],
                archive,
                new ArchiveWriteOptions { Level = 5, Password = TestPassword, EncryptNames = true },
                cancellationToken);
            archive.Position = 0;
            using var reader = SevenZipReader.Open(library, archive, TestPassword);
            var entry = reader.Entries.Single(e => !e.IsFolder);
            using var output = new MemoryStream();
            var outcome = ExtractOutcome.Damaged;
            reader.Extract([entry.Index], _ => output, (_, result) => outcome = result, cancellationToken);
            if (outcome != ExtractOutcome.Ok || !output.ToArray().AsSpan().SequenceEqual(data))
            {
                throw new ArchiveException(Messages.Format(Messages.SevenZipSelfTestFailed, library.Version, outcome));
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new ArchiveException(Messages.Format(Messages.SevenZipSelfTestFailed, library.Version, ex.Message), ex);
        }
    }

    /// <summary>Unpacks 7z.dll and License.txt from the installer into <paramref name="folder"/>.</summary>
    private static void Unpack(string installer, SevenZipLibrary current, string folder, CancellationToken cancellationToken)
    {
        var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = SevenZipReader.Open(current, stream, password: null, SfxSearchLimit);
        var wanted = reader.Entries
            .Where(e => !e.IsFolder && (e.Path.Equals(SevenZipLibrary.FileName, StringComparison.OrdinalIgnoreCase) || e.Path.Equals("License.txt", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (!wanted.Any(e => e.Path.Equals(SevenZipLibrary.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArchiveException(Messages.Format(Messages.SevenZipNotInInstaller, Path.GetFileName(installer)));
        }

        var outputs = new Dictionary<int, FileStream>();
        var problems = new List<string>();
        try
        {
            reader.Extract(
                [.. wanted.Select(e => e.Index)],
                entry =>
                {
                    var file = new FileStream(Path.Combine(folder, entry.Path), FileMode.Create, FileAccess.Write, FileShare.None);
                    outputs[entry.Index] = file;
                    return file;
                },
                (entry, outcome) =>
                {
                    if (outcome != ExtractOutcome.Ok)
                    {
                        problems.Add($"{entry.Path}: {outcome}");
                    }
                },
                cancellationToken);
        }
        finally
        {
            foreach (var file in outputs.Values)
            {
                file.Dispose();
            }
        }

        if (problems.Count > 0)
        {
            throw new ArchiveException(Messages.Format(Messages.SevenZipNotInInstaller, $"{Path.GetFileName(installer)} ({string.Join(", ", problems)})"));
        }
    }

    /// <summary>
    /// Replaces 7z.dll in the program folder: the old one becomes 7z.dll.old (a loaded DLL may be renamed, not
    /// overwritten), the new one is copied next to it and renamed into place. On failure the old one is put back.
    /// </summary>
    private static void Replace(string programFolder, string newDll, string? newLicense)
    {
        string target = Path.Combine(programFolder, SevenZipLibrary.FileName);
        string old = target + OldSuffix;
        string incoming = target + ".new";
        bool movedOld = false;
        try
        {
            File.Copy(newDll, incoming, overwrite: true);
            if (!Hash(incoming).AsSpan().SequenceEqual(Hash(newDll)))
            {
                throw new IOException(Messages.Format(Messages.SevenZipCopyDiffers, incoming));
            }

            RemoveOld(old);
            if (File.Exists(target))
            {
                File.Move(target, old);
                movedOld = true;
            }

            File.Move(incoming, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (movedOld && !File.Exists(target))
                {
                    File.Move(old, target);
                }

                File.Delete(incoming);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                // The message below says what to check; the old file is still there as 7z.dll.old.
            }

            throw new ArchiveException(Messages.Format(Messages.SevenZipSwapFailed, programFolder, ex.Message), ex);
        }

        if (newLicense is not null)
        {
            try
            {
                File.Copy(newLicense, Path.Combine(programFolder, LicenseName), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The license of the older 7-Zip stays: it is the same license.
            }
        }
    }

    /// <summary>
    /// Makes room for a new 7z.dll.old. The previous one may still be loaded (it was in use when this process started
    /// and was replaced since): then it cannot be deleted, only renamed; it is deleted at the next start.
    /// </summary>
    private static void RemoveOld(string old)
    {
        if (!File.Exists(old))
        {
            return;
        }

        try
        {
            File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(old, $"{old}.{DateTime.UtcNow:yyyyMMddHHmmss}{LeftoverSuffix}");
        }
    }

    /// <summary>Files of earlier updates to delete at the next start.</summary>
    public const string LeftoverSuffix = ".delete";

    /// <summary>Deletes what earlier updates could not (at the start of the service, before 7z.dll is loaded).</summary>
    public static void CleanUp(string programFolder, string stagingRoot)
    {
        foreach (string path in SafeFiles(programFolder, "*" + LeftoverSuffix).Concat(SafeFiles(programFolder, SevenZipLibrary.FileName + ".new")))
        {
            TryDelete(() => File.Delete(path));
        }

        if (Directory.Exists(stagingRoot))
        {
            foreach (string folder in Directory.EnumerateDirectories(stagingRoot))
            {
                TryDelete(() => Directory.Delete(folder, recursive: true));
            }

            foreach (string file in SafeFiles(stagingRoot, "*.partial").Concat(SafeFiles(stagingRoot, "7z*.exe")))
            {
                TryDelete(() => File.Delete(file));
            }
        }
    }

    private static string[] SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use; the next start tries again.
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }
}
