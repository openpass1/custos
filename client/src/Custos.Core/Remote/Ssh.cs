using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace Custos.Core.Remote;

/// <summary>Where and how to log on to a Linux server.</summary>
/// <param name="KeySeed">The private Ed25519 key: its 32-byte seed.</param>
/// <param name="HostKeySha256">The pinned key of the server ("SHA256:…"); null when the wizard sees it the first time.</param>
public sealed record SshTarget(string Host, int Port, string User, byte[] KeySeed, string? HostKeySha256);

/// <summary>How a command on the server ended: its exit code and what it wrote to stderr (the last part of it).</summary>
public readonly record struct SshExit(int Code, string Errors);

/// <summary>Opens SSH connections. The service uses SSH.NET; tests may give a fake server.</summary>
public interface ISshConnector
{
    /// <summary>Connects, checks the key of the server against the pinned one and logs on with the key.</summary>
    /// <exception cref="SshHostKeyException">The key of the server is not the pinned one, or none is pinned yet.</exception>
    /// <exception cref="SshFailedException">The server cannot be reached or refuses the key.</exception>
    ISshSession Connect(SshTarget target, CancellationToken cancellationToken);
}

/// <summary>A connection to a server. Commands run one after another, each on its own channel.</summary>
public interface ISshSession : IDisposable
{
    /// <summary>The key of the server, "SHA256:…".</summary>
    string HostKeySha256 { get; }

    /// <summary>Starts a command; its output is read through <see cref="ISshCommand.Output"/>.</summary>
    /// <exception cref="SshFailedException">The connection is gone.</exception>
    ISshCommand Start(string command, CancellationToken cancellationToken);
}

/// <summary>A command running on the server. Disposing before the end stops it.</summary>
public interface ISshCommand : IDisposable
{
    /// <summary>Its stdout. Ends when the command ends.</summary>
    Stream Output { get; }

    /// <summary>Bytes received from the server and not read yet. SSH.NET keeps them in memory without a limit.</summary>
    long Buffered { get; }

    /// <summary>Waits for the end, after <see cref="Output"/> was read to its end.</summary>
    SshExit WaitForExit();
}

/// <summary>An SSH connection failed or broke; the message is for the log.</summary>
public class SshFailedException : Exception
{
    public SshFailedException()
    {
    }

    public SshFailedException(string message)
        : base(message)
    {
    }

    public SshFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The key of the server is not the one pinned in the task (or none is pinned yet).</summary>
public sealed class SshHostKeyException : SshFailedException
{
    public SshHostKeyException()
    {
    }

    public SshHostKeyException(string message)
        : base(message)
    {
    }

    public SshHostKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SshHostKeyException(string message, string? expected, string actual)
        : base(message)
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The pinned key; null when none was pinned.</summary>
    public string? Expected { get; }

    /// <summary>The key the server showed, "SHA256:…".</summary>
    public string Actual { get; } = string.Empty;
}

/// <summary>Ed25519 keys of the agent: made here, the private part kept by the service under DPAPI.</summary>
public static class SshKeys
{
    public const int SeedLength = 32;

    /// <summary>A new private key (its seed).</summary>
    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(SeedLength);

    /// <summary>The public key as an authorized_keys line: "ssh-ed25519 AAAA… comment".</summary>
    public static string PublicKeyLine(byte[] seed, string comment)
    {
        ArgumentNullException.ThrowIfNull(seed);
        using var key = new ED25519Key(seed);
        string line = "ssh-ed25519 " + Convert.ToBase64String(PublicKeyBlob(key.PublicKey));
        string clean = new([.. (comment ?? string.Empty).Where(c => c > ' ' && c < 127)]);
        return clean.Length > 0 ? line + " " + clean : line;
    }

    /// <summary>"SHA256:…" of a key blob, as OpenSSH shows it (base64 without padding).</summary>
    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    /// <summary>Whether a text is a fingerprint in the form "SHA256:" + 43 base64 characters.</summary>
    public static bool IsFingerprint(string? text) =>
        text is { Length: 50 } && text.StartsWith("SHA256:", StringComparison.Ordinal)
        && text.AsSpan(7).IndexOfAnyExcept(Base64Chars) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64Chars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    private static byte[] PublicKeyBlob(byte[] publicKey)
    {
        byte[] name = Encoding.ASCII.GetBytes("ssh-ed25519");
        var blob = new byte[4 + name.Length + 4 + publicKey.Length];
        BinaryPrimitives.WriteInt32BigEndian(blob, name.Length);
        name.CopyTo(blob, 4);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4 + name.Length), publicKey.Length);
        publicKey.CopyTo(blob, 8 + name.Length);
        return blob;
    }
}

/// <summary>SSH through SSH.NET.</summary>
public sealed class SshNetConnector : ISshConnector
{
    public static readonly SshNetConnector Instance = new();

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    public ISshSession Connect(SshTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var key = new ED25519Key(target.KeySeed);
        var keyFile = new PrivateKeyFile(key);
        var info = new ConnectionInfo(target.Host, target.Port, target.User, new PrivateKeyAuthenticationMethod(target.User, keyFile))
        {
            Timeout = ConnectTimeout,
        };
        var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            e.CanTrust = target.HostKeySha256 is not null && string.Equals(seen, target.HostKeySha256, StringComparison.Ordinal);
        };
        try
        {
            client.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
            return new Session(client, keyFile, seen ?? string.Empty);
        }
        catch (Exception ex) when (ex is SshException or SocketException or TimeoutException or IOException or InvalidOperationException)
        {
            client.Dispose();
            keyFile.Dispose();
            string where = $"{target.Host}:{target.Port}";
            if (seen is not null && !string.Equals(seen, target.HostKeySha256, StringComparison.Ordinal))
            {
                throw target.HostKeySha256 is null
                    ? new SshHostKeyException(Messages.Format(Messages.RemoteHostKeyNew, where, seen), null, seen)
                    : new SshHostKeyException(Messages.Format(Messages.RemoteHostKeyChanged, where, target.HostKeySha256, seen), target.HostKeySha256, seen);
            }

            throw ex is SshAuthenticationException
                ? new SshFailedException(Messages.Format(Messages.RemoteKeyRefused, where, target.User), ex)
                : new SshFailedException(Messages.Format(Messages.RemoteUnreachable, where, ex.Message), ex);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            keyFile.Dispose();
            throw;
        }
    }

    private sealed class Session(SshClient client, PrivateKeyFile keyFile, string hostKey) : ISshSession
    {
        public string HostKeySha256 { get; } = hostKey;

        public ISshCommand Start(string command, CancellationToken cancellationToken)
        {
            try
            {
                var ssh = client.CreateCommand(command, Encoding.UTF8);
                return new Command(ssh, cancellationToken);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException)
            {
                throw new SshFailedException(Messages.Format(Messages.RemoteConnectionLost, ex.Message), ex);
            }
        }

        public void Dispose()
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Gone already.
            }

            client.Dispose();
            keyFile.Dispose();
        }
    }

    private sealed class Command : ISshCommand
    {
        private const int KeptErrorChars = 16 * 1024;

        private readonly SshCommand _command;
        private readonly IAsyncResult _pending;
        private readonly CancellationTokenRegistration _cancel;
        private bool _ended;

        public Command(SshCommand command, CancellationToken cancellationToken)
        {
            _command = command;
            _pending = command.BeginExecute();
            Output = new Reader(command.OutputStream);
            _cancel = cancellationToken.Register(Stop);
        }

        public Stream Output { get; }

        public long Buffered
        {
            get
            {
                try
                {
                    return _command.OutputStream.Length;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException)
                {
                    return 0;
                }
            }
        }

        public SshExit WaitForExit()
        {
            try
            {
                _command.EndExecute(_pending);
                _ended = true;
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException)
            {
                throw new SshFailedException(Messages.Format(Messages.RemoteConnectionLost, ex.Message), ex);
            }

            string errors;
            using (var reader = new StreamReader(_command.ExtendedOutputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
            {
                errors = reader.ReadToEnd();
            }

            if (errors.Length > KeptErrorChars)
            {
                errors = errors[^KeptErrorChars..];
            }

            return new SshExit(_command.ExitStatus ?? -1, errors.Trim());
        }

        public void Dispose()
        {
            _cancel.Dispose();
            if (!_ended)
            {
                Stop();
            }

            _command.Dispose();
        }

        private void Stop()
        {
            try
            {
                _command.CancelAsync(forceKill: true, millisecondsTimeout: 1000);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The channel is closed already.
            }
        }

        /// <summary>The output of SSH.NET, with its failures as <see cref="IOException"/>.</summary>
        private sealed class Reader(Stream inner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                try
                {
                    return inner.Read(buffer);
                }
                catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
                {
                    throw new IOException(Messages.Format(Messages.RemoteConnectionLost, ex.Message), ex);
                }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
