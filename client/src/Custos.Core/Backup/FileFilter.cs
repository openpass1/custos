using System.IO.Enumeration;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>
/// The filters of a task (section 6.2). A pattern without a backslash is matched against the name of a file or folder;
/// with a backslash it is matched against the full path (if it starts with a drive or <c>\\</c>) or against the path
/// relative to the source folder. <c>*</c> and <c>?</c> work everywhere; case does not matter.
/// </summary>
public sealed class FileFilter
{
    private readonly Pattern[] _include;
    private readonly Pattern[] _exclude;
    private readonly Pattern[] _excludeDirs;
    private readonly FileAttributes _skippedAttributes;

    public FileFilter(TaskFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        _include = Compile(filters.Include);
        _exclude = Compile(filters.Exclude);
        _excludeDirs = Compile(filters.ExcludeDirs);
        if (filters.SkipHidden)
        {
            _skippedAttributes |= FileAttributes.Hidden;
        }

        if (filters.SkipSystem)
        {
            _skippedAttributes |= FileAttributes.System;
        }

        if (filters.SkipTemp)
        {
            _skippedAttributes |= FileAttributes.Temporary;
        }
    }

    /// <summary>Whether to copy a file.</summary>
    /// <param name="name">File name.</param>
    /// <param name="relativePath">Path below the source folder, with backslashes.</param>
    /// <param name="fullPath">Original full path.</param>
    public bool IncludesFile(string name, string relativePath, string fullPath, FileAttributes attributes)
    {
        if ((attributes & _skippedAttributes) != 0)
        {
            return false;
        }

        if (_include.Length > 0 && !_include.Any(p => p.Matches(name, relativePath, fullPath)))
        {
            return false;
        }

        return !_exclude.Any(p => p.Matches(name, relativePath, fullPath));
    }

    /// <summary>Whether to go into a folder below the source folder.</summary>
    public bool IncludesFolder(string name, string relativePath, string fullPath, FileAttributes attributes)
    {
        // Temporary is a file attribute; on folders only hidden and system count.
        if ((attributes & _skippedAttributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            return false;
        }

        return !_excludeDirs.Any(p => p.Matches(name, relativePath, fullPath));
    }

    private static Pattern[] Compile(IEnumerable<string> patterns) =>
        [.. patterns.Select(p => p.Trim().Replace('/', '\\')).Where(p => p.Length > 0).Select(p => new Pattern(p))];

    private sealed class Pattern(string text)
    {
        // In a "simple expression" a backslash escapes the next character, so a literal one is written twice.
        private readonly string _text = text.TrimEnd('\\').Replace(@"\", @"\\", StringComparison.Ordinal);
        private readonly bool _isPath = text.Contains('\\', StringComparison.Ordinal);
        private readonly bool _isRooted = text.StartsWith(@"\\", StringComparison.Ordinal) || (text.Length >= 2 && text[1] == ':');

        public bool Matches(string name, string relativePath, string fullPath)
        {
            string subject = !_isPath ? name : _isRooted ? fullPath : relativePath;
            return FileSystemName.MatchesSimpleExpression(_text, subject.Replace('/', '\\'), ignoreCase: true);
        }
    }
}
