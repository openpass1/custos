using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Custos.Service.Platform;

/// <summary>The Windows functions the service calls directly.</summary>
internal static partial class NativeMethods
{
    public const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    public const int LOGON32_PROVIDER_WINNT50 = 3;

    public const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

    /// <summary>POWER_REQUEST_TYPE.PowerRequestSystemRequired: the computer must not go to sleep by itself.</summary>
    public const int PowerRequestSystemRequired = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    /// <summary>The full path of a process's program, whatever its bitness; null when Windows does not give it.</summary>
    public static string? ProcessImagePath(int processId)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[32768];
        int size = buffer.Length;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, [Out] char[] name, ref int size);

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LogonUser(string userName, string? domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint GetOEMCP();

    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    /// <summary>TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER: listening sockets with the process that owns each.</summary>
    public const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll")]
    public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    /// <summary>TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES; the LUID is aligned to 4 bytes there.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TokenPrivilege
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivilege newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);

    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileIoPriorityHintInfo.</summary>
    public const int FileIoPriorityHintInfo = 12;

    /// <summary>PRIORITY_HINT.IoPriorityHintLow.</summary>
    public const int IoPriorityHintLow = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, ref int fileInformation, int bufferSize);

    /// <summary>The file system keeps permissions (NTFS, ReFS; not FAT or exFAT).</summary>
    public const uint FILE_PERSISTENT_ACLS = 0x8;

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(string rootPathName, IntPtr volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, IntPtr fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, int bufferLength);
}
