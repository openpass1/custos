using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

public sealed class FileFilterTests
{
    private const FileAttributes Normal = FileAttributes.Normal;

    [Theory]
    [InlineData("report.docx", @"Docs\report.docx", true)]
    [InlineData("~$report.docx", @"Docs\~$report.docx", false)]
    [InlineData("cache.TMP", @"cache.TMP", false)]
    public void Default_filters_leave_out_office_locks_and_temporary_files(string name, string relative, bool expected)
    {
        var filter = new FileFilter(new TaskFilters());

        Assert.Equal(expected, filter.IncludesFile(name, relative, @"D:\Data\" + relative, Normal));
    }

    [Fact]
    public void Include_masks_limit_the_files()
    {
        var filter = new FileFilter(new TaskFilters { Include = ["*.xlsx", "*.docx"] });

        Assert.True(filter.IncludesFile("a.XLSX", "a.XLSX", @"D:\Data\a.XLSX", Normal));
        Assert.False(filter.IncludesFile("a.pdf", "a.pdf", @"D:\Data\a.pdf", Normal));
    }

    [Fact]
    public void Masks_with_a_backslash_match_the_relative_or_the_full_path()
    {
        var filter = new FileFilter(new TaskFilters
        {
            Exclude = [@"Archive\*.zip", @"D:\Data\Secret\*"],
            ExcludeDirs = [@"Projects\Old", "node_modules", "bin/"],
        });

        Assert.False(filter.IncludesFile("x.zip", @"Archive\x.zip", @"D:\Data\Archive\x.zip", Normal));
        Assert.True(filter.IncludesFile("x.zip", @"Other\x.zip", @"D:\Data\Other\x.zip", Normal));
        Assert.False(filter.IncludesFile("key.txt", @"Secret\key.txt", @"D:\Data\Secret\key.txt", Normal));
        Assert.False(filter.IncludesFolder("Old", @"Projects\Old", @"D:\Data\Projects\Old", FileAttributes.Directory));
        Assert.True(filter.IncludesFolder("Old", @"Other\Old", @"D:\Data\Other\Old", FileAttributes.Directory));
        Assert.False(filter.IncludesFolder("node_modules", @"web\node_modules", @"D:\Data\web\node_modules", FileAttributes.Directory));
        Assert.False(filter.IncludesFolder("bin", "bin", @"D:\Data\bin", FileAttributes.Directory));
    }

    [Fact]
    public void Attributes_are_skipped_as_configured()
    {
        var filter = new FileFilter(new TaskFilters { SkipHidden = true, SkipSystem = true, SkipTemp = true });
        var keep = new FileFilter(new TaskFilters { SkipHidden = false, SkipSystem = false, SkipTemp = false, Exclude = [] });

        Assert.False(filter.IncludesFile("a", "a", @"D:\a", FileAttributes.Hidden));
        Assert.False(filter.IncludesFile("a", "a", @"D:\a", FileAttributes.System));
        Assert.False(filter.IncludesFile("a", "a", @"D:\a", FileAttributes.Temporary));
        Assert.False(filter.IncludesFolder("a", "a", @"D:\a", FileAttributes.Directory | FileAttributes.Hidden));
        Assert.True(keep.IncludesFile("a", "a", @"D:\a", FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary));
        Assert.True(keep.IncludesFolder("a", "a", @"D:\a", FileAttributes.Directory | FileAttributes.Hidden));
    }

    [Theory]
    [InlineData(@"D:\Data", "Data")]
    [InlineData(@"D:\Data\", "Data")]
    [InlineData(@"D:\", "D")]
    [InlineData(@"\\nas\share", "share")]
    [InlineData(@"\\nas\share\", "share")]
    [InlineData(@"\\nas\share\docs", "docs")]
    public void The_top_folder_is_named_after_the_source(string source, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            source = source.Replace('\\', '/');
        }

        Assert.Equal(expected, SourceScanner.TopFolderName(source));
    }
}
