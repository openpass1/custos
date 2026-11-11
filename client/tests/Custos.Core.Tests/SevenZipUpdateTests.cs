using System.Net;
using System.Security.Cryptography;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Archives.SevenZip;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>
/// The button «Проверить обновление 7-Zip» (section 6.5): the answer of GitHub is recorded here, nothing goes to the
/// network. Installing a 7z.dll needs Windows and the pinned 7-Zip installer (CUSTOS_7Z_INSTALLER, set by build.ps1).
/// </summary>
public sealed class SevenZipUpdateTests : IDisposable
{
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ pretend installer of 7-Zip 26.07");
    private static readonly string InstallerSha = Convert.ToHexStringLower(SHA256.HashData(Installer));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_newest_stable_release_is_picked_by_its_number()
    {
        var release = SevenZipReleases.Newest(Json(Releases(InstallerSha)));

        Assert.Equal("26.07", release.Version);
        Assert.Equal("7z2607-x64.exe", release.InstallerName);
        Assert.Equal(new Uri("https://github.com/ip7z/7zip/releases/download/26.07/7z2607-x64.exe"), release.InstallerUrl);
        Assert.Equal(InstallerSha, release.Sha256);
        Assert.Equal(Installer.Length, release.Size);
    }

    [Fact]
    public void A_release_without_a_checksum_is_not_downloaded()
    {
        var ex = Assert.Throws<ArchiveException>(() => SevenZipReleases.Newest(Json(Releases(sha: null))));
        Assert.Equal(Messages.Format(Messages.SevenZipNoDigest, "7z2607-x64.exe"), ex.Message);
    }

    [Fact]
    public void An_installer_from_anywhere_else_is_not_taken()
    {
        string json = Releases(InstallerSha).Replace("https://github.com/ip7z/7zip/releases/download/26.07/", "https://example.org/", StringComparison.Ordinal);
        var ex = Assert.Throws<ArchiveException>(() => SevenZipReleases.Newest(Json(json)));
        Assert.Contains("7z2607-x64.exe", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("26.07", "26.06", true)]
    [InlineData("26.06", "26.06", false)]
    [InlineData("26.10", "26.9", true)]
    [InlineData("25.01", "26.06", false)]
    [InlineData("26.06", null, true)]
    [InlineData("26.06", "?", true)]
    [InlineData("beta", "26.06", false)]
    public void Versions_compare_by_number(string candidate, string? installed, bool newer) =>
        Assert.Equal(newer, SevenZipReleases.IsNewer(candidate, installed));

    [Fact]
    public async Task The_installer_is_kept_only_when_its_checksum_matches()
    {
        var release = SevenZipReleases.Newest(Json(Releases(InstallerSha)));
        string folder = _temp.Combine("updates");

        using (var http = new HttpClient(new Answer(Installer)))
        {
            string path = await SevenZipReleases.DownloadAsync(http, release, folder, TestContext.Current.CancellationToken);
            Assert.Equal(Installer, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }

        File.Delete(Path.Combine(folder, release.InstallerName));
        using (var http = new HttpClient(new Answer(Encoding.ASCII.GetBytes("MZ something else"))))
        {
            var ex = await Assert.ThrowsAsync<ArchiveException>(() => SevenZipReleases.DownloadAsync(http, release, folder, TestContext.Current.CancellationToken));
            Assert.Contains(InstallerSha, ex.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task A_failed_download_says_why_and_leaves_nothing()
    {
        var release = SevenZipReleases.Newest(Json(Releases(InstallerSha)));
        string folder = _temp.Combine("updates");
        using var http = new HttpClient(new Answer(null));

        var ex = await Assert.ThrowsAsync<ArchiveException>(() => SevenZipReleases.DownloadAsync(http, release, folder, TestContext.Current.CancellationToken));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void The_7z_dll_of_the_installer_replaces_the_old_one_after_a_test()
    {
        string? installer = Environment.GetEnvironmentVariable("CUSTOS_7Z_INSTALLER");
        SevenZipCheck.SkipUnlessAvailable(ArchiveFormats.SevenZip);
        Assert.SkipUnless(OperatingSystem.IsWindows() && installer is not null && File.Exists(installer), "Needs the 7-Zip installer (CUSTOS_7Z_INSTALLER).");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var current = SevenZipLibrary.Load(Environment.GetEnvironmentVariable("CUSTOS_7Z_DLL")!);
        string program = _temp.Combine("Program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "7z.dll"), "the old 7z.dll");
        var lines = new List<string>();

        var installed = SevenZipInstaller.Install(installer!, current, program, _temp.Combine("updates", "7z-test"), lines.Add, TestContext.Current.CancellationToken);

        Assert.Equal(current.Version, installed.Version);
        Assert.Equal("the old 7z.dll", File.ReadAllText(Path.Combine(program, "7z.dll.old")));
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(installed.Path)), SHA256.HashData(File.ReadAllBytes(Path.Combine(program, "7z.dll"))));
        Assert.True(File.Exists(Path.Combine(program, SevenZipInstaller.LicenseName)));
        string version = installed.Version;
        Assert.Contains(lines, l => l.Contains(version, StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_is_not_an_installer_changes_nothing()
    {
        SevenZipCheck.SkipUnlessAvailable(ArchiveFormats.SevenZip);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var current = SevenZipLibrary.Load(Environment.GetEnvironmentVariable("CUSTOS_7Z_DLL")!);
        string program = _temp.Combine("Program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "7z.dll"), "the old 7z.dll");
        string fake = _temp.Combine("7z2699-x64.exe");
        File.WriteAllBytes(fake, Installer);

        ArchiveException? refused = null;
        try
        {
            SevenZipInstaller.Install(fake, current, program, _temp.Combine("updates", "7z-test"), _ => { }, TestContext.Current.CancellationToken);
        }
        catch (ArchiveException ex)
        {
            refused = ex;
        }

        Assert.NotNull(refused);

        Assert.Equal("the old 7z.dll", File.ReadAllText(Path.Combine(program, "7z.dll")));
        Assert.False(File.Exists(Path.Combine(program, "7z.dll.old")));
    }

    [Fact]
    public void The_7z_dll_in_use_passes_the_self_test()
    {
        SevenZipCheck.SkipUnlessAvailable(ArchiveFormats.SevenZip);
        if (OperatingSystem.IsWindows())
        {
            SevenZipInstaller.SelfTest(SevenZipLibrary.Load(Environment.GetEnvironmentVariable("CUSTOS_7Z_DLL")!), TestContext.Current.CancellationToken);
        }
    }

    private static MemoryStream Json(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Shaped like the answer of api.github.com/repos/ip7z/7zip/releases (fields that are not used left out): the
    /// "latest" first is an older one, a newer pre-release and a draft must be skipped.
    /// </summary>
    private static string Releases(string? sha)
    {
        string digest = sha is null ? "null" : $"\"sha256:{sha}\"";
        return $$"""
            [
              {"tag_name": "26.03", "draft": false, "prerelease": false, "assets": [
                {"name": "7z2603-x64.exe", "size": 1600000, "digest": "sha256:{{new string('a', 64)}}",
                 "browser_download_url": "https://github.com/ip7z/7zip/releases/download/26.03/7z2603-x64.exe"}]},
              {"tag_name": "26.08", "draft": false, "prerelease": true, "assets": []},
              {"tag_name": "26.09", "draft": true, "prerelease": false, "assets": []},
              {"tag_name": "26.07", "draft": false, "prerelease": false, "assets": [
                {"name": "7z2607.exe", "size": 1500000, "digest": "sha256:{{new string('b', 64)}}",
                 "browser_download_url": "https://github.com/ip7z/7zip/releases/download/26.07/7z2607.exe"},
                {"name": "7z2607-x64.exe", "size": {{Installer.Length}}, "digest": {{digest}},
                 "browser_download_url": "https://github.com/ip7z/7zip/releases/download/26.07/7z2607-x64.exe"}]},
              {"tag_name": "26.06", "draft": false, "prerelease": false, "assets": []}
            ]
            """;
    }

    /// <summary>Answers every request with these bytes, or 404 for null.</summary>
    private sealed class Answer(byte[]? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
