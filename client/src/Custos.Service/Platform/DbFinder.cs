using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Custos.Core.Databases;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Service.Platform;

/// <summary>
/// "Find" in the database server dialog (section 6.2): the running servers (mysqld, mariadbd, postgres) with the
/// address and port they listen on, then the folders of programs of servers that are not running. OSPanel 6 gives
/// every module its own address (127.127.126.x), so the address is taken from the listening socket, not assumed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DbFinder
{
    private static readonly (string Process, string Engine)[] ServerProcesses =
    [
        ("mysqld", DbEngines.MySql),
        ("mariadbd", DbEngines.MariaDb),
        ("postgres", DbEngines.PostgreSql),
    ];

    public static DbFindResult Find(CancellationToken cancellationToken = default)
    {
        var result = new DbFindResult();
        var listeners = Listeners();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, engine) in ServerProcesses)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? folder = ProgramFolder(process);
                    if (folder is null)
                    {
                        continue;
                    }

                    // PostgreSQL runs many postgres.exe; only the first of them listens. MySQL 8 also listens on 33060
                    // (X protocol): the lowest port is the one for mysqldump.
                    var endpoint = listeners.Where(l => l.Pid == process.Id).OrderBy(l => l.Endpoint.Port).Select(l => l.Endpoint).FirstOrDefault();
                    if (endpoint is null && engine == DbEngines.PostgreSql)
                    {
                        continue;
                    }

                    var described = DbServerFinder.Describe(folder);
                    string host = endpoint is null ? "127.0.0.1" : HostOf(endpoint.Address);
                    int port = endpoint?.Port ?? DbEngines.DefaultPort(engine);
                    if (!seen.Add(folder + "|" + host + "|" + port))
                    {
                        continue;
                    }

                    runningFolders.Add(folder);
                    result.Servers.Add(new DbCandidate
                    {
                        Engine = described?.Engine ?? engine,
                        BinDir = folder,
                        Version = described?.Version ?? DbServerFinder.FolderVersion(folder),
                        Host = host,
                        Port = port,
                        Running = true,
                    });
                }
            }
        }

        var roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && IsReady(d)).Select(d => d.RootDirectory.FullName);
        foreach (var folder in DbServerFinder.Scan(roots, cancellationToken))
        {
            if (!runningFolders.Contains(folder.BinDir))
            {
                result.Servers.Add(new DbCandidate
                {
                    Engine = folder.Engine,
                    BinDir = folder.BinDir,
                    Version = folder.Version,
                    Port = DbEngines.DefaultPort(folder.Engine),
                });
            }
        }

        return result;
    }

    private static bool IsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ProgramFolder(Process process)
    {
        string? path;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // A 32-bit program (the legacy edition) cannot see the modules of a 64-bit server: Windows still names its file.
            path = NativeMethods.ProcessImagePath(process.Id);
        }

        return path is null ? null : Path.GetDirectoryName(path);
    }

    /// <summary>"0.0.0.0" and "::" accept connections on 127.0.0.1 too; any other address is used as it is.</summary>
    private static string HostOf(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? "127.0.0.1" : address.ToString();

    /// <summary>The listening TCP sockets with the processes that own them (IPv4 and IPv6).</summary>
    private static List<(int Pid, IPEndPoint Endpoint)> Listeners()
    {
        var list = new List<(int, IPEndPoint)>();
        ReadTable(NativeMethods.AF_INET, 24, list, (row) =>
        {
            uint address = (uint)Marshal.ReadInt32(row, 4);
            int port = Port(Marshal.ReadInt32(row, 8));
            return (Marshal.ReadInt32(row, 20), new IPEndPoint(new IPAddress(address), port));
        });
        ReadTable(NativeMethods.AF_INET6, 56, list, (row) =>
        {
            byte[] address = new byte[16];
            Marshal.Copy(row, address, 0, 16);
            long scope = (uint)Marshal.ReadInt32(row, 16);
            int port = Port(Marshal.ReadInt32(row, 20));
            return (Marshal.ReadInt32(row, 52), new IPEndPoint(new IPAddress(address, scope), port));
        });
        return list;
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static void ReadTable(int family, int rowSize, List<(int, IPEndPoint)> list, Func<IntPtr, (int, IPEndPoint)> read)
    {
        int size = 0;
        _ = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint error = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (error == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                {
                    continue;
                }

                if (error != 0)
                {
                    return;
                }

                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                {
                    var (pid, endpoint) = read(buffer + 4 + (i * rowSize));
                    if (endpoint.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        list.Add((pid, endpoint));
                    }
                }

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
