using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Custos.Updater;

/// <summary>
/// Installs an update of CUSTOS (A.10). The service checks the package, unpacks it, writes what it is doing and starts
/// a copy of this program from C:\ProgramData\CUSTOS\updates:
/// <code>Custos.Updater.exe --install &lt;unpacked&gt; --target &lt;program folder&gt; --result &lt;json&gt; --service CustosService --from 0.3.1 --to 0.3.2</code>
/// It stops the service and the program windows, keeps the old files, copies the new ones, starts the service and
/// checks that it keeps running; if anything fails, it puts the old files back and starts the old service. The
/// windows are opened again (in the tray) in the sessions where they were. The result goes to the JSON file, which the
/// service reads at its start and reports to the server; the details go to C:\ProgramData\CUSTOS\logs\updater.log.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string GuiFile = "Custos.exe";
    private const string ManifestFile = "manifest.json";
    private const string SevenZipFile = "7z.dll";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(1);

    /// <summary>The new service must keep running this long to count as started.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(20);

    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CUSTOS");

    private static int Main(string[] args)
    {
        string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var options = Parse(args);
        if (options is null)
        {
            Console.WriteLine($"Custos.Updater {version}");
            return args.Length == 0 || args[0] is "--version" or "-v" ? 0 : 2;
        }

        using var log = new UpdateLog(Path.Combine(DataFolder, "logs", "updater.log"));
        log.Write($"Custos.Updater {version}: {options.From} -> {options.To}, from {options.Staging} to {options.Target}");
        try
        {
            return Update(options, log) ? 0 : 1;
        }
#pragma warning disable CA1031 // Whatever broke is written down for the service to report.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write("Failed: " + ex);
            WriteResult(options, ok: false, rolledBack: false, ex.Message, log);

            // The service must come back in any case.
            TryStart(options.Service, log);
            return 1;
        }
    }

    private static bool Update(Options options, UpdateLog log)
    {
        var sessions = GuiSessions(options.Target, log);
        log.Write("Stopping the service " + options.Service);
        ServiceManager.Stop(options.Service, StopTimeout, log);
        StopGui(options.Target, log);

        string backup = Path.Combine(DataFolder, "updates", "backup-" + Safe(options.From));
        log.Write("Keeping the old files in " + backup);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        CopyTree(options.Target, backup, null, log);
        try
        {
            CopyTree(options.Staging, options.Target, KeepNewer, log);
            WriteResult(options, ok: true, rolledBack: false, null, log);
            log.Write("Starting the new service");
            ServiceManager.Start(options.Service, StartTimeout);
            Thread.Sleep(SettleTime);
            if (!ServiceManager.IsRunning(options.Service))
            {
                throw new InvalidOperationException("The new version of the service stopped right after its start (see C:\\ProgramData\\CUSTOS\\logs).");
            }
        }
#pragma warning disable CA1031 // Any failure means: back to the old version.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write("Update failed, putting the old files back: " + ex);
            try
            {
                ServiceManager.Stop(options.Service, StopTimeout, log);
            }
#pragma warning disable CA1031 // The service may not be running at all.
            catch (Exception stopProblem)
#pragma warning restore CA1031
            {
                log.Write("Stopping the new service: " + stopProblem.Message);
            }

            CopyTree(backup, options.Target, null, log);
            WriteResult(options, ok: false, rolledBack: true, ex.Message, log);
            TryStart(options.Service, log);
            StartGui(options.Target, sessions, log);
            return false;
        }

        StartGui(options.Target, sessions, log);
        TryDelete(backup, log);
        log.Write("Done: " + options.To);
        return true;
    }

    /// <summary>7z.dll is replaced only by a newer file version: an administrator may have put a newer one in (section 10.2).</summary>
    private static bool KeepNewer(string relative, string source, string target, UpdateLog log)
    {
        if (string.Equals(relative, ManifestFile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(relative, SevenZipFile, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
        {
            return true;
        }

        var newVersion = FileVersion(source);
        var oldVersion = FileVersion(target);
        if (newVersion > oldVersion)
        {
            return true;
        }

        log.Write($"7z.dll {oldVersion} kept: the package has {newVersion}");
        return false;
    }

    private static Version FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    /// <summary>Copies every file of <paramref name="from"/> into <paramref name="to"/>, overwriting; a locked file is tried again.</summary>
    private static void CopyTree(string from, string to, Func<string, string, string, UpdateLog, bool>? filter, UpdateLog log)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            string target = Path.Combine(to, relative);
            if (filter is not null && !filter(relative, file, target, log))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
                {
                    log.Write($"{target}: {ex.Message}; trying again");
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }
        }
    }

    // ---- The program windows -----------------------------------------------------------------------------------

    /// <summary>The sessions where Custos.exe of this folder runs: it is started there again after the update.</summary>
    private static HashSet<int> GuiSessions(string target, UpdateLog log)
    {
        var sessions = new HashSet<int>();
        foreach (var process in GuiProcesses(target))
        {
            using (process)
            {
                sessions.Add(process.SessionId);
            }
        }

        log.Write(sessions.Count == 0 ? "No program windows open" : "Program windows open in sessions " + string.Join(", ", sessions));
        return sessions;
    }

    private static void StopGui(string target, UpdateLog log)
    {
        foreach (var process in GuiProcesses(target))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(10_000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log.Write($"Closing Custos.exe {process.Id}: {ex.Message}");
                }
            }
        }
    }

    private static List<Process> GuiProcesses(string target)
    {
        string expected = Path.GetFullPath(Path.Combine(target, GuiFile));
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GuiFile)))
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone, or not ours to look at.
            }

            if (path is not null && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return found;
    }

    private static void StartGui(string target, HashSet<int> sessions, UpdateLog log)
    {
        string gui = Path.Combine(target, GuiFile);
        foreach (int session in sessions)
        {
            try
            {
                Sessions.StartAsUser(session, gui, "--tray", target);
                log.Write($"Custos.exe started again in session {session}");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log.Write($"Custos.exe not started in session {session}: {ex.Message}");
            }
        }
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------------

    private static void TryStart(string service, UpdateLog log)
    {
        try
        {
            ServiceManager.Start(service, StartTimeout);
        }
#pragma warning disable CA1031 // Written down; nothing else can be done here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write("Starting the service: " + ex.Message);
        }
    }

    private static void TryDelete(string folder, UpdateLog log)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"{folder}: {ex.Message}");
        }
    }

    /// <summary>The result for the service (fields of UpdaterResult in Custos.Core).</summary>
    private static void WriteResult(Options options, bool ok, bool rolledBack, string? message, UpdateLog log)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", ok);
                writer.WriteString("from_version", options.From);
                writer.WriteString("to_version", options.To);
                writer.WriteString("message", message);
                writer.WriteBoolean("rolled_back", rolledBack);
                writer.WriteString("finished_at", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            File.WriteAllBytes(options.Result, buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Writing the result: " + ex.Message);
        }
    }

    private static string Safe(string text) => string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

    private static Options? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i]] = args[i + 1];
        }

        string? Get(string name) => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        if (Get("--install") is not { } staging || Get("--target") is not { } target || Get("--result") is not { } result)
        {
            return null;
        }

        return new Options(staging, target, result, Get("--service") ?? "CustosService", Get("--from") ?? "?", Get("--to") ?? "?");
    }

    private sealed record Options(string Staging, string Target, string Result, string Service, string From, string To);
}

/// <summary>updater.log: appended to, one line per step with the time.</summary>
internal sealed class UpdateLog : IDisposable
{
    private readonly StreamWriter? _writer;

    public UpdateLog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    public void Write(string text)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text;
        Console.WriteLine(line);
        try
        {
            _writer?.WriteLine(line);
        }
        catch (IOException)
        {
            // The log is not worth failing the update.
        }
    }

    public void Dispose() => _writer?.Dispose();
}
