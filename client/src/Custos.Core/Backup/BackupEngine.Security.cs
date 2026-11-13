using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>NTFS permissions of plain copies (section 6.8): files as they are copied, folders after the files.</summary>
public sealed partial class BackupEngine
{
    /// <summary>The task copies permissions and a destination is left that can hold them (a folder on NTFS, not a server).</summary>
    private static bool KeepsSecurity(RunContext run) =>
        run.Task.Copy.PreserveAcl && !IsArchive(run.Task) && run.Targets.Any(t => !t.Failed && !t.SecurityFailed);

    /// <summary>
    /// Gives the folders of the copy the permissions of the source folders. This runs after the files: a folder that
    /// lets fewer people write must not stop the copy into it.
    /// </summary>
    private void CopyFolderSecurity(RunContext run)
    {
        if (!KeepsSecurity(run))
        {
            return;
        }

        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true, IsFile: false }))
        {
            // The top folder of the source only when it has its own folder in the set.
            IEnumerable<string> folders = context.Target.Length > 0 ? [string.Empty, .. context.Scan!.Folders] : context.Scan!.Folders;
            foreach (string folder in folders)
            {
                run.Cancellation.ThrowIfCancellationRequested();
                var targets = run.ActiveTargets.Where(t => !t.SecurityFailed).ToList();
                if (targets.Count == 0)
                {
                    return;
                }

                string source = folder.Length == 0 ? context.ReadRoot : Path.Combine(context.ReadRoot, folder);
                byte[]? security = null;
                foreach (var target in targets)
                {
                    string copy = target.Store.Combine(target.WriteFolder, context.Target, folder);
                    if (!target.Store.FolderExists(copy))
                    {
                        continue;
                    }

                    security ??= context.Session.Run(() => _platform.ReadSecurity(source, folder: true));
                    if (security is null)
                    {
                        break;
                    }

                    ApplySecurity(run, target, copy, folder: true, security);
                }
            }
        }
    }

    private void ApplySecurity(RunContext run, Target target, string path, bool folder, byte[] security)
    {
        if (target.SecurityFailed)
        {
            return;
        }

        try
        {
            if (!target.Store.WriteSecurity(path, folder, security))
            {
                // FAT or exFAT: there is nothing to keep permissions in, and nothing to warn about.
                target.SecurityFailed = true;
            }
        }
        catch (BackupException ex)
        {
            target.SecurityFailed = true;
            run.Result.Warnings++;
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.SecurityNotCopied, target.Label, ex.Message));
        }
    }
}
