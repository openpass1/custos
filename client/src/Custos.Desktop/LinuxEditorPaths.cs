using Custos.Core.Backup;

namespace Custos.Desktop;

internal static class LinuxEditorPaths
{
    // Do not check existence from the desktop session: the service reads paths with its own access and mounts.
    public static bool IsLocal(string path)
    {
        if (path.Contains('\0', StringComparison.Ordinal) || BackupEngine.IsNetworkPath(path))
        {
            return false;
        }

        if (path.StartsWith('/'))
        {
            return true;
        }

        // The agent expands variables in local paths, but a backslash after a variable is not a Linux separator.
        int variableEnd = path.StartsWith('%') ? path.IndexOf('%', 1) : -1;
        return variableEnd > 1 && (variableEnd == path.Length - 1 || path[variableEnd + 1] == '/')
            && PathVariables.LooksAbsolute(path);
    }

    public static string NormalizeShare(string path) => path.Replace('\\', '/');

    public static bool IsShare(string path)
    {
        if (!BackupEngine.IsNetworkPath(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = NormalizeShare(path)[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is [not ("." or ".."), not ("." or ".."), ..] && !parts.Contains("..", StringComparer.Ordinal);
    }

    public static bool IsVolumeFolder(string path) =>
        !path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains('\0', StringComparison.Ordinal) && !PathVariables.HasVariables(path)
        && !path.Split('/').Contains("..", StringComparer.Ordinal);
}
