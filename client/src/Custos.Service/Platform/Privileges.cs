using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Custos.Service.Platform;

/// <summary>Turns on privileges the account of the service holds but has off by default.</summary>
[SupportedOSPlatform("windows")]
internal static class Privileges
{
    public const string Shutdown = "SeShutdownPrivilege";
    public const string Restore = "SeRestorePrivilege";
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";
    public const string Security = "SeSecurityPrivilege";

    /// <summary>Enables a privilege for the whole process.</summary>
    /// <returns>False if the account does not hold it.</returns>
    /// <exception cref="Win32Exception">The token could not be changed.</exception>
    public static bool Enable(string name)
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            if (!NativeMethods.LookupPrivilegeValue(null, name, out long luid))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var state = new NativeMethods.TokenPrivilege { PrivilegeCount = 1, Luid = luid, Attributes = NativeMethods.SE_PRIVILEGE_ENABLED };
            if (!NativeMethods.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // ERROR_NOT_ALL_ASSIGNED: the call works, but the account does not hold the privilege.
            return Marshal.GetLastPInvokeError() != 1300;
        }
    }
}
