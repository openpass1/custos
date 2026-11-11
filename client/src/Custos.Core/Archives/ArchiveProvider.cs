using Custos.Core.Archives.SevenZip;
using Custos.Core.Model;

namespace Custos.Core.Archives;

/// <summary>Writers and readers for the archive formats of a task (section 6.5).</summary>
public interface IArchiveProvider
{
    /// <summary>Version of the 7z.dll in use, like "26.06", or null if it is not loaded (yet, or at all).</summary>
    string? SevenZipVersion { get; }

    /// <exception cref="ArchiveException">The format cannot be written here (7z.dll missing or broken).</exception>
    IArchiveWriter CreateWriter(string format);

    /// <summary>Opens an archive of the given format; the stream is disposed with the reader.</summary>
    /// <exception cref="ArchiveException">The archive cannot be opened.</exception>
    IArchiveReader OpenReader(string format, Stream stream, string? password);
}

/// <summary>ZIP through SharpZipLib everywhere; 7z through the 7z.dll at <see cref="SevenZipPath"/> on Windows.</summary>
public sealed class ArchiveProvider(string sevenZipPath) : IArchiveProvider
{
    private volatile SevenZipLibrary? _library;

    public string SevenZipPath { get; } = sevenZipPath;

    public string? SevenZipVersion => OperatingSystem.IsWindows() ? _library?.Version : null;

    /// <summary>Loads 7z.dll now, to log its version or report what is wrong with it.</summary>
    /// <returns>The version.</returns>
    /// <exception cref="ArchiveException">7z.dll is missing, not 64-bit or broken.</exception>
    public string LoadSevenZip() => OperatingSystem.IsWindows() ? Library().Version : throw new ArchiveException(Messages.SevenZipWindowsOnly);

    /// <summary>The 7z.dll in use, loaded now if it is not yet.</summary>
    /// <exception cref="ArchiveException">7z.dll is missing, not 64-bit or broken.</exception>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public SevenZipLibrary CurrentSevenZip() => Library();

    /// <summary>Uses another 7z.dll from now on: the one an update installed (see <see cref="SevenZipInstaller"/>).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void UseSevenZip(SevenZipLibrary library) => _library = library ?? throw new ArgumentNullException(nameof(library));

    public IArchiveWriter CreateWriter(string format) => format switch
    {
        ArchiveFormats.Zip => new ZipWriter(),
        ArchiveFormats.SevenZip when OperatingSystem.IsWindows() => new SevenZipWriter(Library()),
        ArchiveFormats.SevenZip => throw new ArchiveException(Messages.SevenZipWindowsOnly),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public IArchiveReader OpenReader(string format, Stream stream, string? password)
    {
        ArgumentNullException.ThrowIfNull(stream);
        switch (format)
        {
            case ArchiveFormats.Zip:
                return ZipReader.Open(stream, password);
            case ArchiveFormats.SevenZip when OperatingSystem.IsWindows():
                SevenZipLibrary library;
                try
                {
                    library = Library();
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                return SevenZipReader.Open(library, stream, password);
            default:
                stream.Dispose();
                throw new ArchiveException(format == ArchiveFormats.SevenZip ? Messages.SevenZipWindowsOnly : Messages.Format(Messages.ArchiveUnreadable, format));
        }
    }

    private SevenZipLibrary Library()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ArchiveException(Messages.SevenZipWindowsOnly);
        }

        return _library ??= SevenZipLibrary.Load(SevenZipPath);
    }
}
