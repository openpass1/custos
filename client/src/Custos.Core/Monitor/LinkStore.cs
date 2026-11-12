using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custos.Core.Agent;

namespace Custos.Core.Monitor;

/// <summary>
/// What the agent keeps about its monitoring server (A.3): its own RSA key in <c>config\agent.key</c> (PKCS#8 under
/// DPAPI) and the binding in <c>config\server.json</c>. Every change is written at once, through a temporary file.
/// Thread-safe; the objects it returns are copies.
/// </summary>
public sealed class LinkStore
{
    public const string FileName = "server.json";
    public const string KeyFileName = "agent.key";

    /// <summary>Most entries kept in the lists that wait for the next configuration snapshot.</summary>
    private const int MaxNotes = 1000;

    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly Lock _lock = new();
    private readonly List<string> _problems = [];
    private LinkState _state = new();
    private RSA? _key;

    public LinkStore(string directory, ISecretProtector protector)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>What went wrong while loading, for the log and the program window.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_lock)
            {
                return [.. _problems];
            }
        }
    }

    /// <summary>The agent key; made at the first start of the service.</summary>
    public RSA AgentKey
    {
        get
        {
            lock (_lock)
            {
                return _key ?? throw new InvalidOperationException("LinkStore.Load was not called.");
            }
        }
    }

    /// <summary>The public key of the agent, PEM.</summary>
    public string AgentPublicPem => MonitorCrypto.PublicPem(AgentKey);

    /// <summary>Reads both files; makes the agent key if there is none.</summary>
    public void Load()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_directory);
            _problems.Clear();
            _state = ReadState() ?? new LinkState();
            bool newKey;
            _key?.Dispose();
            _key = LoadKey(out newKey);
            if (newKey && _state.AgentId is not null)
            {
                // The server knows the old key only: register again with the stored code (A.3).
                _state.AgentId = null;
                Write(_state);
            }
        }
    }

    public LinkState Get()
    {
        lock (_lock)
        {
            return Copy(_state);
        }
    }

    /// <summary>Changes the state and writes it.</summary>
    public LinkState Update(Action<LinkState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var copy = Copy(_state);
            change(copy);
            Write(copy);
            _state = copy;
            return Copy(copy);
        }
    }

    /// <summary>The registration code as typed (A.3), or null if there is none or it cannot be read here.</summary>
    public string? GetCode()
    {
        string? stored;
        lock (_lock)
        {
            stored = _state.RegistrationCodeDpapi;
        }

        if (stored is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(stored)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The registration code under DPAPI, for <see cref="LinkState.RegistrationCodeDpapi"/>.</summary>
    public string ProtectCode(string code) => Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(code)));

    /// <summary>A task changed (A.6): it goes into <c>changed</c> of the next snapshot.</summary>
    public void NoteChanged(TaskChangeNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        Update(state =>
        {
            state.Changed.RemoveAll(c => string.Equals(c.TaskId, note.TaskId, StringComparison.OrdinalIgnoreCase));
            state.Changed.Add(note);
            state.DeletedTaskIds.RemoveAll(id => string.Equals(id, note.TaskId, StringComparison.OrdinalIgnoreCase));
            Trim(state.Changed);
        });
    }

    public void NoteDeleted(string taskId)
    {
        Update(state =>
        {
            state.Changed.RemoveAll(c => string.Equals(c.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            if (!state.DeletedTaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase))
            {
                state.DeletedTaskIds.Add(taskId);
            }

            Trim(state.DeletedTaskIds);
        });
    }

    /// <summary>A server edit replaced a version the server had not seen (A.4).</summary>
    public void NoteOverwritten(JsonElement task)
    {
        Update(state =>
        {
            state.Overwritten.Add(task.Clone());
            Trim(state.Overwritten);
        });
    }

    /// <summary>The server has the snapshot: forgets what it carried, keeps what came in meanwhile.</summary>
    public void SnapshotSent(LinkState sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        Update(state =>
        {
            foreach (var note in sent.Changed)
            {
                state.Changed.RemoveAll(c => string.Equals(c.TaskId, note.TaskId, StringComparison.OrdinalIgnoreCase) && c.Revision == note.Revision);
            }

            state.DeletedTaskIds.RemoveAll(id => sent.DeletedTaskIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            state.Overwritten.RemoveRange(0, Math.Min(sent.Overwritten.Count, state.Overwritten.Count));
        });
    }

    private static void Trim<T>(List<T> list)
    {
        if (list.Count > MaxNotes)
        {
            list.RemoveRange(0, list.Count - MaxNotes);
        }
    }

    private RSA LoadKey(out bool created)
    {
        string path = Path.Combine(_directory, KeyFileName);
        created = false;
        if (File.Exists(path))
        {
            try
            {
                byte[] der = _protector.Unprotect(Convert.FromBase64String(File.ReadAllText(path).Trim()));
                try
                {
                    var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(der, out _);
                    return rsa;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(der);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
            {
                string kept = SetAside(path);
                _problems.Add(Messages.Format(Messages.MonitorKeyUnreadable, ex.Message, kept));
            }
        }

        var key = MonitorCrypto.NewKey();
        byte[] pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            WriteFile(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(_protector.Protect(pkcs8))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        created = true;
        return key;
    }

    private LinkState? ReadState()
    {
        string path = Path.Combine(_directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize(stream, MonitorJsonContext.Default.LinkState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string kept = SetAside(path);
            _problems.Add(Messages.Format(Messages.ConfigDamaged, FileName, ex.Message, kept));
            return null;
        }
    }

    private void Write(LinkState state) =>
        WriteFile(Path.Combine(_directory, FileName), JsonSerializer.SerializeToUtf8Bytes(state, MonitorJsonContext.Default.LinkState));

    private static void WriteFile(string path, byte[] content)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string SetAside(string path)
    {
        string kept = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, kept);
            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static LinkState Copy(LinkState state) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(state, MonitorJsonContext.Default.LinkState), MonitorJsonContext.Default.LinkState)!;
}
