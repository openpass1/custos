using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Custos.Core.Archives.SevenZip;

/// <summary>
/// 7z.dll loaded from a given path (section 2: 7-Zip x64, LGPL). Any x64 build of 7-Zip works: the administrator may
/// replace the file with a newer one without a new CUSTOS. Only the 7z format is used, through
/// <c>CreateObject</c>; the library stays loaded until the process ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class SevenZipLibrary
{
    public const string FileName = "7z.dll";

    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineI386 = 0x014C;

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, SevenZipLibrary> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly delegate* unmanaged<Guid*, Guid*, void**, int> _createObject;

    private SevenZipLibrary(string path, IntPtr handle, string version)
    {
        Path = path;
        Version = version;
        _createObject = (delegate* unmanaged<Guid*, Guid*, void**, int>)NativeLibrary.GetExport(handle, "CreateObject");
    }

    /// <summary>Full path of the loaded file.</summary>
    public string Path { get; }

    /// <summary>Version of 7-Zip the file comes from, like "26.06".</summary>
    public string Version { get; }

    /// <summary>
    /// Loads 7z.dll from <paramref name="path"/>, or returns the one already loaded from there. A file replaced
    /// after it was loaded is only picked up by the next start of the process.
    /// </summary>
    /// <exception cref="ArchiveException">The file is missing, not 64-bit or not a 7-Zip library.</exception>
    public static SevenZipLibrary Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full = System.IO.Path.GetFullPath(path);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(full, out var loaded))
            {
                return loaded;
            }

            if (!File.Exists(full))
            {
                throw new ArchiveException(Messages.Format(Environment.Is64BitProcess ? Messages.SevenZipMissing : Messages.SevenZipMissingLegacy, full));
            }

            // The 32-bit legacy edition loads the 32-bit 7z.dll, the standard one the 64-bit 7z.dll.
            ushort machine = ReadMachine(full);
            if (machine != (Environment.Is64BitProcess ? MachineAmd64 : MachineI386))
            {
                throw new ArchiveException(Messages.Format(Environment.Is64BitProcess ? Messages.SevenZipNot64Bit : Messages.SevenZipNot32Bit, full));
            }

            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(full);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                throw new ArchiveException(Messages.Format(Messages.SevenZipBroken, full, ex.Message), ex);
            }

            if (!NativeLibrary.TryGetExport(handle, "CreateObject", out _))
            {
                throw new ArchiveException(Messages.Format(Messages.SevenZipBroken, full, "no CreateObject"));
            }

            var library = new SevenZipLibrary(full, handle, ReadVersion(full, handle));
            Cache[full] = library;
            return library;
        }
    }

    /// <summary>A new 7z handler for writing.</summary>
    internal IOutArchive CreateWriter() => (IOutArchive)Create(typeof(IOutArchive).GUID);

    /// <summary>A new 7z handler for reading.</summary>
    internal IInArchive CreateReader() => (IInArchive)Create(typeof(IInArchive).GUID);

    private object Create(Guid iid)
    {
        Guid clsid = SevenZipConst.Format7z;
        void* pointer = null;
        int hr = _createObject(&clsid, &iid, &pointer);
        if (hr != SevenZipConst.S_OK || pointer == null)
        {
            throw new ArchiveException(Messages.Format(Messages.SevenZipBroken, Path, Hresult(hr)));
        }

        try
        {
            return Marshal.GetObjectForIUnknown((IntPtr)pointer);
        }
        finally
        {
            Marshal.Release((IntPtr)pointer);
        }
    }

    internal static string Hresult(int hr) => "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The CPU the DLL is built for (IMAGE_FILE_HEADER.Machine), or 0 if the file is not a DLL.</summary>
    internal static ushort ReadMachine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return 0;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > stream.Length - 6)
            {
                return 0;
            }

            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550 ? reader.ReadUInt16() : (ushort)0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException(Messages.Format(Messages.SevenZipBroken, path, ex.Message), ex);
        }
    }

    /// <summary>"26.06": from the version resource of the file, or from GetModuleProp of 7-Zip 23 and newer.</summary>
    private static string ReadVersion(string path, IntPtr handle)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart > 0 || info.FileMinorPart > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{info.FileMajorPart}.{info.FileMinorPart:00}");
        }

        if (NativeLibrary.TryGetExport(handle, "GetModuleProp", out IntPtr export))
        {
            var getModuleProp = (delegate* unmanaged<uint, PropVariant*, int>)export;
            PropVariant value = default;
            if (getModuleProp(1, &value) == SevenZipConst.S_OK && value.VarType == PropVariant.VtUi4)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{value.UInt32Value >> 16}.{value.UInt32Value & 0xFFFF:00}");
            }
        }

        return "?";
    }
}
