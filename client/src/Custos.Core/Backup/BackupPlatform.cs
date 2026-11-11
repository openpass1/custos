using System.Diagnostics;
using System.Text;
using Custos.Core.Archives;

namespace Custos.Core.Backup;

/// <summary>
/// What the backup engine needs from Windows. The service supplies the real implementation (logon to shares, VSS,
/// power requests); tests and other systems use <see cref="LocalBackupPlatform"/>.
/// </summary>
public interface IBackupPlatform
{
    /// <summary>Name of this computer, for <c>{computer}</c> and the manifests.</summary>
    string ComputerName { get; }

    /// <summary>
    /// Opens a network logon for a stored credential (section 6.3). With a null id the service's own account is used.
    /// </summary>
    /// <exception cref="BackupException">The account cannot log on or has no password stored.</exception>
    INetworkSession OpenSession(string? credentialId);

    /// <summary>Creates shadow copies of the volumes holding these local paths (section 6.8).</summary>
    /// <exception cref="BackupException">VSS failed.</exception>
    ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths);

    /// <summary>Root folder (like <c>E:\</c>) of the ready disk with this volume label, or null.</summary>
    string? FindVolumeByLabel(string label);

    /// <summary>Bytes the caller may still write at this path, or null if that is unknown.</summary>
    long? GetFreeSpace(string path);

    /// <summary>Keeps the computer from going to sleep until disposed (section 6.7).</summary>
    IDisposable KeepAwake(string reason);

    /// <summary>Writers and readers of 7z and ZIP archives.</summary>
    IArchiveProvider Archives { get; }

    /// <summary>The password of an <c>archive_password</c> credential (section 6.5).</summary>
    /// <exception cref="BackupException">There is no such credential or it has no password.</exception>
    string GetArchivePassword(string credentialId);

    /// <summary>
    /// Runs the program or script of an event and waits for it (section 6.9): <c>.bat</c> and <c>.cmd</c> through
    /// cmd.exe, <c>.ps1</c> through PowerShell, anything else directly. Each line it prints goes to
    /// <paramref name="output"/>. After the timeout or on cancellation it is killed with everything it started.
    /// </summary>
    /// <returns>The exit code.</returns>
    /// <exception cref="BackupException">It could not be started or ran out of time.</exception>
    /// <exception cref="OperationCanceledException">The run was stopped.</exception>
    int RunProgram(string command, string arguments, TimeSpan timeout, Action<string> output, CancellationToken cancellationToken);

    /// <summary>Starts or stops a Windows service and waits until it is running or stopped (section 6.9).</summary>
    /// <exception cref="BackupException">There is no such service, or it did not get there in time.</exception>
    void ControlService(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Environment variables for the paths of a task (section 6.2): those of a user profile (<c>%USERPROFILE%</c>,
    /// <c>%APPDATA%</c> …) for the user with this SID, the others of the system. Null for an unknown variable.
    /// </summary>
    Func<string, string?> Variables(string? profileSid);

    /// <summary>Asks Windows to serve the I/O of this file after that of other programs. Failures are ignored.</summary>
    void LowerIoPriority(Microsoft.Win32.SafeHandles.SafeFileHandle handle);

    /// <summary>The NTFS permissions, owner and group of a file or folder (section 6.8); null if there are none.</summary>
    byte[]? ReadSecurity(string path, bool folder);

    /// <summary>Gives a copied file or folder the permissions read by <see cref="ReadSecurity"/>.</summary>
    /// <returns>False if the file system there keeps no permissions (FAT, exFAT): nothing to do.</returns>
    /// <exception cref="BackupException">They could not be set.</exception>
    bool WriteSecurity(string path, bool folder, byte[] descriptor);
}

/// <summary>A network logon. File operations on shares run inside <see cref="Run"/>; a file opened there stays usable
/// afterwards, so reading and writing can happen outside.</summary>
public interface INetworkSession : IDisposable
{
    /// <summary>DOMAIN\user, or null for the service's own account.</summary>
    string? Account { get; }

    void Run(Action action);

    T Run<T>(Func<T> action);
}

/// <summary>Shadow copies of some volumes. Disposing releases them.</summary>
public interface ISnapshotSet : IDisposable
{
    /// <summary>The path to read instead of <paramref name="path"/>: inside the shadow copy of its volume, or the path
    /// itself when its volume has none.</summary>
    string Map(string path);
}

/// <summary>A run cannot go on, with a message for the log.</summary>
public sealed class BackupException : Exception
{
    public BackupException()
    {
    }

    public BackupException(string message)
        : base(message)
    {
    }

    public BackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A session with no logon of its own: the process account.</summary>
public sealed class ProcessSession : INetworkSession
{
    public static readonly ProcessSession Instance = new();

    public string? Account => null;

    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }

    public void Dispose()
    {
    }
}

/// <summary>No shadow copies: every path is read as it is.</summary>
public sealed class NoSnapshots : ISnapshotSet
{
    public static readonly NoSnapshots Instance = new();

    public string Map(string path) => path;

    public void Dispose()
    {
    }
}

/// <summary>Platform without Windows specifics: the process account everywhere, no VSS, no sleep prevention.</summary>
public class LocalBackupPlatform : IBackupPlatform
{
    private IArchiveProvider? _archives;

    /// <summary>7z.dll next to the program; the variable CUSTOS_7Z_DLL can point elsewhere (tests, development).</summary>
    public virtual IArchiveProvider Archives => _archives ??= new ArchiveProvider(
        Environment.GetEnvironmentVariable("CUSTOS_7Z_DLL") is { Length: > 0 } path ? path : Path.Combine(AppContext.BaseDirectory, "7z.dll"));

    /// <summary>Passwords by credential id; the service supplies its own lookup.</summary>
    public Dictionary<string, string> ArchivePasswords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public virtual string GetArchivePassword(string credentialId) =>
        ArchivePasswords.TryGetValue(credentialId, out string? password) ? password : throw new BackupException(Messages.ArchivePasswordMissing);

    public virtual string ComputerName => Environment.MachineName;

    public virtual INetworkSession OpenSession(string? credentialId) => ProcessSession.Instance;

    public virtual ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths) => NoSnapshots.Instance;

    public virtual string? FindVolumeByLabel(string label)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && string.Equals(drive.VolumeLabel, label, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.RootDirectory.FullName;
                }
            }
            catch (IOException)
            {
                // A drive that went away while we looked.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public virtual long? GetFreeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public virtual IDisposable KeepAwake(string reason) => ProcessSession.Instance;

    /// <summary>The encoding console programs write in; null = the default of the process.</summary>
    protected virtual Encoding? ConsoleEncoding => null;

    public virtual int RunProgram(string command, string arguments, TimeSpan timeout, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(output);
        var start = StartInfo(command, arguments ?? string.Empty);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.StandardOutputEncoding = ConsoleEncoding;
        start.StandardErrorEncoding = ConsoleEncoding;
        if (Path.IsPathFullyQualified(command) && Path.GetDirectoryName(command) is { Length: > 0 } folder && Directory.Exists(folder))
        {
            start.WorkingDirectory = folder;
        }

        var sync = new Lock();
        void Line(string? line)
        {
            if (line is not null)
            {
                lock (sync)
                {
                    output(line);
                }
            }
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new BackupException(Messages.Format(Messages.EventNotStarted, command, ex.Message), ex);
        }

        // Nothing is ever typed in: a script that asks ("press any key") gets the end of its input at once.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var clock = Stopwatch.StartNew();
        while (!process.WaitForExit(250))
        {
            if (cancellationToken.IsCancellationRequested || clock.Elapsed >= timeout)
            {
                Kill(process);
                cancellationToken.ThrowIfCancellationRequested();
                throw new BackupException(Messages.Format(Messages.EventTimedOut, command, (int)timeout.TotalSeconds));
            }
        }

        // The output read to its end.
        process.WaitForExit();
        return process.ExitCode;
    }

    public virtual Func<string, string?> Variables(string? profileSid) => Environment.GetEnvironmentVariable;

    public virtual void LowerIoPriority(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // Only Windows has I/O priority hints.
    }

    public virtual byte[]? ReadSecurity(string path, bool folder) => null;

    public virtual bool WriteSecurity(string path, bool folder, byte[] descriptor) => false;

    public virtual void ControlService(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new BackupException(Messages.Format(Messages.EventServiceUnsupported, name));

    private static ProcessStartInfo StartInfo(string command, string arguments)
    {
        string extension = Path.GetExtension(command);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // cmd /s /c "…" takes everything between the outer quotes as the command line, quotes inside included.
            return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{command}\" {arguments}\"");
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{command}\" {arguments}");
        }

        return new ProcessStartInfo(command, arguments);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended on its own meanwhile, or cannot be killed: nothing more to do.
        }
    }
}
