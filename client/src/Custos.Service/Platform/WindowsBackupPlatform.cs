using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Backup;
using Microsoft.Win32.SafeHandles;

namespace Custos.Service.Platform;

/// <summary>
/// The backup engine's view of Windows: network logons with stored accounts, shadow copies, sleep prevention and free
/// space (sections 6.3, 6.7, 6.8).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBackupPlatform(ConfigStore config, ShadowCopies shadows) : LocalBackupPlatform
{
    public override INetworkSession OpenSession(string? credentialId)
    {
        if (credentialId is null)
        {
            return ProcessSession.Instance;
        }

        var credential = config.GetCredential(credentialId) ?? throw new BackupException(Messages.CredentialNotFound);
        string? password;
        try
        {
            password = config.GetPassword(credentialId);
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }

        if (password is null)
        {
            throw new BackupException(Messages.Format(Messages.CredentialNoPassword, credential.DisplayName));
        }

        return NetworkLogon.Open(credential.User, credential.Domain, password);
    }

    public override ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths) => shadows.Create(localPaths);

    public override long? GetFreeSpace(string path)
    {
        string directory = Path.TrimEndingDirectorySeparator(path) + "\\";
        return NativeMethods.GetDiskFreeSpaceEx(directory, out ulong available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
    }

    public override IDisposable KeepAwake(string reason) => PowerRequest.Create(reason);
}

/// <summary>
/// A logon with a stored account for network access only (LOGON32_LOGON_NEW_CREDENTIALS): locally the service stays
/// SYSTEM, shares see the account. The password is checked by the share on first access, not here.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NetworkLogon : INetworkSession
{
    private readonly SafeAccessTokenHandle _token;

    private NetworkLogon(SafeAccessTokenHandle token, string account)
    {
        _token = token;
        Account = account;
    }

    public string? Account { get; }

    /// <exception cref="BackupException">Windows refused the logon.</exception>
    public static NetworkLogon Open(string user, string? domain, string password)
    {
        // A user written as DOMAIN\user or user@domain in the user field works too.
        if (string.IsNullOrEmpty(domain) && user.IndexOf('\\', StringComparison.Ordinal) is int slash and > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }

        string account = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        string? logonDomain = string.IsNullOrEmpty(domain) ? (user.Contains('@', StringComparison.Ordinal) ? null : ".") : domain;
        if (!NativeMethods.LogonUser(user, logonDomain, password, NativeMethods.LOGON32_LOGON_NEW_CREDENTIALS, NativeMethods.LOGON32_PROVIDER_WINNT50, out var token))
        {
            int error = Marshal.GetLastPInvokeError();
            token.Dispose();
            throw new BackupException(Messages.Format(Messages.LogonFailed, account, new Win32Exception(error).Message));
        }

        return new NetworkLogon(token, account);
    }

    public void Run(Action action) => WindowsIdentity.RunImpersonated(_token, action);

    public T Run<T>(Func<T> action) => WindowsIdentity.RunImpersonated(_token, action);

    public void Dispose() => _token.Dispose();
}

/// <summary>Keeps the computer awake while a run goes on; shown by <c>powercfg /requests</c> with its reason (section 6.7).</summary>
[SupportedOSPlatform("windows")]
internal sealed class PowerRequest : IDisposable
{
    private readonly IntPtr _handle;
    private readonly IntPtr _reason;
    private bool _disposed;

    private PowerRequest(IntPtr handle, IntPtr reason)
    {
        _handle = handle;
        _reason = reason;
    }

    public static IDisposable Create(string reason)
    {
        IntPtr text = Marshal.StringToHGlobalUni(reason);
        var context = new NativeMethods.ReasonContext
        {
            Version = NativeMethods.POWER_REQUEST_CONTEXT_VERSION,
            Flags = NativeMethods.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = text,
        };
        IntPtr handle = NativeMethods.PowerCreateRequest(ref context);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !NativeMethods.PowerSetRequest(handle, NativeMethods.PowerRequestSystemRequired))
        {
            // Not being able to stay awake is no reason to skip the backup.
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                NativeMethods.CloseHandle(handle);
            }

            Marshal.FreeHGlobal(text);
            return ProcessSession.Instance;
        }

        return new PowerRequest(handle, text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.PowerClearRequest(_handle, NativeMethods.PowerRequestSystemRequired);
        NativeMethods.CloseHandle(_handle);
        Marshal.FreeHGlobal(_reason);
    }
}
