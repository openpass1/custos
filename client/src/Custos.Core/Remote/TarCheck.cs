using System.Security.Cryptography;
using Custos.Core.Backup;

namespace Custos.Core.Remote;

/// <summary>
/// A stream to write a tar into: it goes through a <see cref="TarParser"/> to the members of the derived class and is
/// not kept anywhere. A damaged tar does not throw (an archive reader writes here from its own code): parsing stops and
/// <see cref="Broken"/> says why.
/// </summary>
public abstract class TarFeedStream : Stream, ITarSink
{
    private readonly TarParser _parser;

    protected TarFeedStream()
    {
        _parser = new TarParser(this);
    }

    /// <summary>Why the tar could not be followed to its end; null while all is well.</summary>
    public string? Broken { get; private set; }

    /// <summary>The two zero blocks at the end were read.</summary>
    public bool Ended => _parser.Ended;

    /// <summary>Bytes of the tar so far.</summary>
    public long Bytes => _parser.Position;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _parser.Position;

    public override long Position
    {
        get => _parser.Position;
        set => throw new NotSupportedException();
    }

    /// <summary>Null if the tar was whole, or why not (it broke off, or is damaged).</summary>
    public string? Problem => Broken ?? (Ended ? null : Messages.Format(Messages.RemoteTarCut, _parser.Position));

    public abstract void MemberStart(TarMember member, ReadOnlySpan<byte> header);

    public abstract void MemberData(ReadOnlySpan<byte> data);

    public abstract void MemberEnd();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Broken is not null)
        {
            return;
        }

        try
        {
            _parser.Write(buffer);
        }
        catch (InvalidDataException ex)
        {
            Broken = ex.Message;
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Checks the tar of a set (files.tar) without unpacking it: every file it should hold (by key, with the SHA-256 of
/// the manifest) is hashed and compared. Used by the verification of sets of Linux servers (section 6.5).
/// </summary>
public sealed class TarCheckStream(IReadOnlyDictionary<string, string> expected) : TarFeedStream
{
    private readonly IReadOnlyDictionary<string, string> _expected = expected ?? throw new ArgumentNullException(nameof(expected));
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private TarMember? _member;
    private string? _differs;

    /// <summary>Null if the tar is whole and holds every expected file with its SHA-256, or the first problem.</summary>
    public string? Finish()
    {
        if (_differs is not null)
        {
            return _differs;
        }

        if (Problem is { } problem)
        {
            return Messages.Format(Messages.VerifyFileProblem, BackupEngine.RemoteTarName, problem);
        }

        string? missing = _expected.Keys.FirstOrDefault(k => !_seen.Contains(k));
        return missing is null ? null : Messages.Format(Messages.VerifyFileProblem, RemotePath.Display(missing), Messages.VerifyFileMissing);
    }

    public override void MemberStart(TarMember member, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(member);
        _member = member.IsFile && _expected.ContainsKey(member.Key) ? member : null;
    }

    public override void MemberData(ReadOnlySpan<byte> data)
    {
        if (_member is not null)
        {
            _hash.AppendData(data);
        }
    }

    public override void MemberEnd()
    {
        if (_member is not { } member)
        {
            return;
        }

        string key = member.Key;
        string hash = SetManifest.ToHex(_hash.GetHashAndReset());
        _member = null;
        if (_seen.Add(key) && !string.Equals(hash, _expected[key], StringComparison.OrdinalIgnoreCase))
        {
            _differs ??= Messages.Format(Messages.VerifyFileProblem, RemotePath.Display(member.AbsolutePath), Messages.VerifyContentDiffers);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
