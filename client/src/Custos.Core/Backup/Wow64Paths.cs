namespace Custos.Core.Backup;

/// <summary>
/// The 32-bit legacy edition on 64-bit Windows runs under WOW64, which shows it <c>C:\Windows\SysWOW64</c> whenever it
/// opens <c>C:\Windows\System32</c>. <c>C:\Windows\Sysnative</c> is the real System32 for such a program, so a source
/// in System32 that is read directly (not from a shadow copy, where no redirection happens) is read through it.
/// WOW64 also gives such a program <c>%ProgramFiles%</c> and <c>%CommonProgramFiles%</c> of the 32-bit programs
/// (<c>C:\Program Files (x86)</c>): the paths of a task mean those of Windows itself. Nothing changes for a 64-bit
/// program or on 32-bit Windows.
/// </summary>
public static class Wow64Paths
{
    private static readonly string? System32 = Redirected() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32") : null;

    /// <summary>The path to open to read <paramref name="path"/> as the administrator meant it.</summary>
    public static string Native(string path)
    {
        if (System32 is null || path is null || !path.StartsWith(System32, StringComparison.OrdinalIgnoreCase)
            || (path.Length > System32.Length && path[System32.Length] is not ('\\' or '/')))
        {
            return path!;
        }

        return Path.Combine(Path.GetDirectoryName(System32)!, "Sysnative") + path[System32.Length..];
    }

    /// <summary>
    /// An environment variable of this process as a 64-bit program would see it: <c>ProgramFiles</c>,
    /// <c>CommonProgramFiles</c> and their <c>ProgramW6432</c>-style twins are what Windows has for 64-bit programs.
    /// </summary>
    public static string? Variable(string name)
    {
        if (System32 is not null && name is not null)
        {
            if (name.Equals("ProgramFiles", StringComparison.OrdinalIgnoreCase))
            {
                return Environment.GetEnvironmentVariable("ProgramW6432");
            }

            if (name.Equals("CommonProgramFiles", StringComparison.OrdinalIgnoreCase))
            {
                return Environment.GetEnvironmentVariable("CommonProgramW6432");
            }
        }

        return Environment.GetEnvironmentVariable(name!);
    }

    private static bool Redirected() => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess;
}
