using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Custos.Updater;

/// <summary>Stopping and starting the service through the Service Control Manager, with the process id it reports.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ServiceManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ControlStop = 1;
    private const int StatusProcessInfo = 0;
    private const uint Stopped = 1;
    private const uint Running = 4;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;

    public static void Stop(string name, TimeSpan timeout, UpdateLog log)
    {
        using var service = Open(name, ServiceQueryStatus | ServiceStop);
        var status = Query(service);
        if (status.CurrentState == Stopped)
        {
            return;
        }

        int processId = (int)status.ProcessId;
        if (!ControlService(service.Handle, ControlStop, out _) && Marshal.GetLastPInvokeError() != ErrorServiceNotActive)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ControlService(STOP)");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (Query(service).CurrentState != Stopped && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
        }

        // The process may still be closing its files after it said "stopped".
        if (processId != 0)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
                {
                    log.Write($"The service process {processId} did not end: killing it");
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(30));
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        if (Query(service).CurrentState != Stopped)
        {
            Thread.Sleep(2000);
            if (Query(service).CurrentState != Stopped)
            {
                throw new InvalidOperationException($"The service {name} did not stop in {(int)timeout.TotalMinutes} min.");
            }
        }
    }

    public static void Start(string name, TimeSpan timeout)
    {
        using var service = Open(name, ServiceQueryStatus | ServiceStart);
        if (!StartService(service.Handle, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != ErrorServiceAlreadyRunning)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "StartService");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            uint state = Query(service).CurrentState;
            if (state == Running)
            {
                return;
            }

            if (state == Stopped || DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"The service {name} did not start.");
            }

            Thread.Sleep(500);
        }
    }

    public static bool IsRunning(string name)
    {
        using var service = Open(name, ServiceQueryStatus);
        return Query(service).CurrentState == Running;
    }

    private static ScHandle Open(string name, uint access)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenSCManager");
        }

        try
        {
            var service = OpenService(manager, name, access);
            return service == IntPtr.Zero ? throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenService " + name) : new ScHandle(service);
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static ServiceStatusProcess Query(ScHandle service)
    {
        if (!QueryServiceStatusEx(service.Handle, StatusProcessInfo, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "QueryServiceStatusEx");
        }

        return status;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr service, int argumentCount, IntPtr arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess buffer, int bufferSize, out int bytesNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class ScHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => CloseServiceHandle(Handle);
    }
}

/// <summary>Starts a program in the session of a logged-on user (the updater runs as SYSTEM, started by the service).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Sessions
{
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public static void StartAsUser(int sessionId, string program, string arguments, string folder)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WTSQueryUserToken");
        }

        IntPtr environment = IntPtr.Zero;
        IntPtr desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = desktop };
            char[] commandLine = ($"\"{program}\" {arguments}" + "\0").ToCharArray();
            fixed (char* line = commandLine)
            {
                if (!CreateProcessAsUser(token, program, line, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, environment, folder, in startup, out var info))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessAsUser");
                }

                CloseHandle(info.Process);
                CloseHandle(info.Thread);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            Marshal.FreeHGlobal(desktop);
            CloseHandle(token);
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr environment);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUser(IntPtr token, string applicationName, char* commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, in StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
