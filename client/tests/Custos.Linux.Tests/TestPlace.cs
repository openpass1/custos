using System.Runtime.CompilerServices;
using Custos.Core;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

/// <summary>
/// Where the service of the tests keeps its files and its socket: set before anything reads them (CustosInfo and
/// PipeProtocol read the variables once). The process is CUSTOS for Linux from the start, as the program is.
/// </summary>
internal static class TestPlace
{
    public static string Root { get; } = Path.Combine("/tmp", "clt-" + Guid.NewGuid().ToString("N")[..8]);

    public static string Data => Path.Combine(Root, "data");

    public static string Socket => Path.Combine(Root, "c.sock");

    /// <summary>
    /// CUSTOS_INSTALLED_SERVICE=1: the tests talk to the service that install.sh put on this computer (its socket is
    /// /run/custos.sock) instead of starting one of their own (<see cref="InstalledServiceTests"/>).
    /// </summary>
    public static bool Installed { get; } = Environment.GetEnvironmentVariable("CUSTOS_INSTALLED_SERVICE") == "1";

    /// <summary>Whether the tests run as root: real mounts, LVM and snapshots.</summary>
    public static bool IsRoot { get; } = Environment.UserName == "root";

    [ModuleInitializer]
    internal static void Set()
    {
        CustosEdition.UseLinux();
        Directory.CreateDirectory(Root);
        if (!Installed)
        {
            Environment.SetEnvironmentVariable("CUSTOS_DATA_DIR", Data);
            Environment.SetEnvironmentVariable("CUSTOS_SOCKET", Socket);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Clean();
    }

    /// <summary>
    /// Removes the folder of the tests, but only once nothing is mounted in it any more: a delete that went on into a
    /// mount left behind (a view of a snapshot binds real folders) would delete what is there.
    /// </summary>
    private static void Clean()
    {
        try
        {
            if (MountTable.Read().At(DataFolders.Snapshots) is not null)
            {
                Tool.Run("umount", [DataFolders.Snapshots]);
            }

            var table = MountTable.Read();
            if (table.At(Root) is not null || table.Below(Root).Any())
            {
                Console.Error.WriteLine("Something is still mounted in " + Root + ", so it is left as it is.");
                return;
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the system to clean.
        }
    }

    /// <summary>A new empty folder for one test.</summary>
    public static string NewFolder(string name)
    {
        string path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }
}
