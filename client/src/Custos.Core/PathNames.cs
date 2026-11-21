namespace Custos.Core;

/// <summary>
/// How file names of the sources are compared. Windows and macOS (by default) do not tell «Отчёт.txt» from
/// «отчёт.txt», and the program follows them; on Linux these are two different files that may lie side by side, so
/// CUSTOS for Linux compares names exactly: otherwise one of them would be taken for the other and left out of the copy.
/// </summary>
public static class PathNames
{
    /// <summary>For sets and dictionaries of paths of source files.</summary>
    public static StringComparer Comparer => CustosEdition.IsLinux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>For <see cref="string.Equals(string, string, StringComparison)"/> and <c>StartsWith</c> on such paths.</summary>
    public static StringComparison Comparison => CustosEdition.IsLinux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
