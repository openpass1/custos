using System.Runtime.CompilerServices;

namespace Custos.Mac.Tests;

/// <summary>
/// Where the service of the tests keeps its files and its socket: set before anything reads them (CustosInfo and
/// PipeProtocol read the variables once). The socket is in /tmp because the path of a socket on macOS may not be
/// longer than 104 bytes, and the temporary folder of macOS is already half of that.
/// </summary>
internal static class TestPlace
{
    public static string Root { get; } = Path.Combine("/tmp", "cmt-" + Guid.NewGuid().ToString("N")[..8]);

    public static string Data => Path.Combine(Root, "data");

    public static string Socket => Path.Combine(Root, "c.sock");

    [ModuleInitializer]
    internal static void Set()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("CUSTOS_DATA_DIR", Data);
        Environment.SetEnvironmentVariable("CUSTOS_SOCKET", Socket);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };
    }

    /// <summary>A new empty folder for one test.</summary>
    public static string NewFolder(string name)
    {
        string path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }
}
