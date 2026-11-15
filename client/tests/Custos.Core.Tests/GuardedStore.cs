using Custos.Core.Destinations;

namespace Custos.Core.Tests;

/// <summary>
/// A store that passes everything to another one, but refuses to delete the files <see cref="Refuse"/> names, as
/// storage with WORM refuses the files it protects; and notes every deletion in the order it was made.
/// </summary>
internal sealed class GuardedStore : DestinationStore
{
    private readonly DestinationStore _inner;

    public GuardedStore(DestinationStore inner, List<string>? deleted = null)
    {
        _inner = inner;
        Deleted = deleted ?? [];
        Worm = inner.Worm;
    }

    /// <summary>The files that may not be deleted, by path.</summary>
    public Func<string, bool> Refuse { get; set; } = _ => false;

    /// <summary>Files and folders deleted, in order.</summary>
    public List<string> Deleted { get; }

    public override string Kind => _inner.Kind;

    public override string Root => _inner.Root;

    public override string Label => _inner.Label;

    public override string? Account => _inner.Account;

    public override string CacheKey => _inner.CacheKey;

    public override bool CanSeek => _inner.CanSeek;

    public override bool RenamesFolders => _inner.RenamesFolders;

    public override bool CanRenameFolders(string folder) => _inner.CanRenameFolders(folder);

    public override bool HasFolders => _inner.HasFolders;

    public override bool KeepsTimes => _inner.KeepsTimes;

    public override bool KeepsSecurity => _inner.KeepsSecurity;

    public override bool ProtectsFiles => _inner.ProtectsFiles;

    public override string Combine(string folder, string relative) => _inner.Combine(folder, relative);

    public override string? Parent(string path) => _inner.Parent(path);

    public override string Name(string path) => _inner.Name(path);

    public override string Relative(string folder, string path) => _inner.Relative(folder, path);

    public override void CreateFolder(string path) => _inner.CreateFolder(path);

    public override bool FolderExists(string path) => _inner.FolderExists(path);

    public override bool FileExists(string path) => _inner.FileExists(path);

    public override List<StoreEntry> List(string folder) => _inner.List(folder);

    /// <summary>Deletes what it may, as a NAS does, and then refuses if anything inside is protected.</summary>
    public override void DeleteFolder(string path)
    {
        var files = _inner.FolderExists(path) ? _inner.ListFiles(path).ToList() : [];
        var refused = files.Where(f => Refuse(f.Path)).ToList();
        if (refused.Count == 0)
        {
            Deleted.Add(path);
            _inner.DeleteFolder(path);
            return;
        }

        foreach (var file in files.Where(f => !Refuse(f.Path)))
        {
            Deleted.Add(file.Path);
            _inner.DeleteFile(file.Path);
        }

        throw new UnauthorizedAccessException("Access to the path '" + refused[0].Path + "' is denied.");
    }

    public override bool RemoveEmptyFolder(string path) => _inner.RemoveEmptyFolder(path);

    public override void MoveFolder(string source, string target) => _inner.MoveFolder(source, target);

    public override Stream OpenRead(string path) => _inner.OpenRead(path);

    public override long GetFileSize(string path) => _inner.GetFileSize(path);

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks) =>
        _inner.BeginWrite(path, tempSuffix, mode);

    public override void DeleteFile(string path)
    {
        if (Refuse(path))
        {
            throw new UnauthorizedAccessException("Access to the path '" + path + "' is denied.");
        }

        Deleted.Add(path);
        _inner.DeleteFile(path);
    }

    public override void MoveFile(string source, string target) => _inner.MoveFile(source, target);

    public override void SetFileTimes(string path, DateTime createdUtc, DateTime modifiedUtc, FileAttributes attributes) =>
        _inner.SetFileTimes(path, createdUtc, modifiedUtc, attributes);

    public override bool WriteSecurity(string path, bool folder, byte[] descriptor) => _inner.WriteSecurity(path, folder, descriptor);

    public override StoreSpace GetSpace(string path) => _inner.GetSpace(path);

    public override DateTimeOffset? LockedUntil(string path) => _inner.LockedUntil(path);

    public override void CheckReady() => _inner.CheckReady();

    public override IReadOnlyList<string> Describe() => _inner.Describe();

    public override void Probe() => _inner.Probe();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
