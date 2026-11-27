using System.Net;
using System.Security.Cryptography;
using System.Text;
using Custos.Core.Updates;

namespace Custos.Core.Tests;

/// <summary>
/// New versions of the program on GitHub (computers without a monitoring site): the answer of GitHub is recorded
/// here, nothing goes to the network.
/// </summary>
public sealed class ProgramReleaseTests : IDisposable
{
    private const string Downloads = "https://github.com/" + CustosInfo.GitHubRepository + "/releases/download/";
    private static readonly byte[] Package = Encoding.ASCII.GetBytes("PK pretend update package of CUSTOS 1.2.0");
    private static readonly string PackageSha = Convert.ToHexStringLower(SHA256.HashData(Package));

    /// <summary>The package of this edition: custos-1.2.0.zip, or custos-macos-1.2.0.zip when the tests run on a Mac.</summary>
    private static string Zip(string version) => CustosEdition.PackageName(version);

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_newest_release_of_the_program_is_picked_by_its_number()
    {
        var release = ProgramReleases.Newest(Json(Releases($"\"sha256:{PackageSha}\"")));

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release.Version);
        Assert.Equal(Zip("1.2.0"), release.PackageName);
        Assert.Equal(new Uri(Downloads + "client-v1.2.0/" + Zip("1.2.0")), release.PackageUrl);
        Assert.Equal(PackageSha, release.Sha256);
        Assert.Equal(Package.Length, release.Size);
        Assert.Equal(new Uri($"https://github.com/{CustosInfo.GitHubRepository}/releases/tag/client-v1.2.0"), release.PageUrl);
    }

    [Fact]
    public void Without_a_digest_the_sums_file_of_the_release_is_used()
    {
        var release = ProgramReleases.Newest(Json(Releases("null")));

        Assert.NotNull(release);
        Assert.Null(release.Sha256);
        Assert.Equal(new Uri(Downloads + "client-v1.2.0/SHA256SUMS.txt"), release.SumsUrl);
    }

    [Fact]
    public void A_release_with_nothing_to_check_the_package_with_is_skipped()
    {
        string json = Releases("null").Replace("\"name\": \"SHA256SUMS.txt\"", "\"name\": \"notes.txt\"", StringComparison.Ordinal);

        var release = ProgramReleases.Newest(Json(json));

        Assert.NotNull(release);
        Assert.Equal("1.1.0", release.Version);
    }

    [Fact]
    public void A_package_from_anywhere_else_is_not_taken()
    {
        string json = Releases($"\"sha256:{PackageSha}\"").Replace(Downloads + "client-v1.2.0/" + Zip("1.2.0"), "https://example.org/" + Zip("1.2.0"), StringComparison.Ordinal);

        var release = ProgramReleases.Newest(Json(json));

        Assert.NotNull(release);
        Assert.Equal("1.1.0", release.Version);
    }

    [Fact]
    public void A_newer_release_without_the_package_of_this_system_is_passed_over()
    {
        // A release made for the other system only (a fix of the Mac alone, or of Windows alone) is not an update here.
        string other = $$"""
            {"tag_name": "client-v1.5.0", "draft": false, "prerelease": false, "assets": [
              {"name": "{{OtherZip("1.5.0")}}", "size": 1000, "digest": "sha256:{{new string('a', 64)}}",
               "browser_download_url": "{{Downloads}}client-v1.5.0/{{OtherZip("1.5.0")}}"},
              {"name": "SHA256SUMS.txt", "size": 300, "digest": null,
               "browser_download_url": "{{Downloads}}client-v1.5.0/SHA256SUMS.txt"}]},
            """;
        string releases = Releases($"\"sha256:{PackageSha}\"");
        string json = "[" + other + releases[(releases.IndexOf('[', StringComparison.Ordinal) + 1)..];

        var release = ProgramReleases.Newest(Json(json));

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release.Version);
    }

    [Fact]
    public void Preview_can_pick_a_newer_prerelease_and_switching_to_stable_hides_its_cache()
    {
        string json = Releases($"\"sha256:{PackageSha}\"");
        var preview = ProgramReleases.Newest(Json(json), ProgramReleases.PreviewChannel);
        var stable = ProgramReleases.Newest(Json(json), ProgramReleases.StableChannel);

        Assert.Equal("1.3.0", preview?.Version);
        Assert.True(preview?.Prerelease);
        Assert.Equal("1.2.0", stable?.Version);
        Assert.False(stable?.Prerelease);

        var state = new ProgramCheckState { Channel = ProgramReleases.PreviewChannel, Latest = preview, CheckedAt = DateTimeOffset.UtcNow };
        Assert.Same(state, state.ForChannel(ProgramReleases.PreviewChannel));
        var switched = state.ForChannel(ProgramReleases.StableChannel);
        Assert.Null(switched.Latest);
        Assert.Null(switched.CheckedAt);
        Assert.Equal(ProgramReleases.StableChannel, ProgramReleases.NormalizeChannel(null));
    }

    [Theory]
    [InlineData("custos-desktop_{0}_amd64.deb", "custos-desktop_{0}_arm64.deb")]
    [InlineData("custos-desktop_{0}_arm64.deb", "custos-desktop_{0}_amd64.deb")]
    [InlineData("custos-desktop-{0}-1.x86_64.rpm", "custos-desktop-{0}-1.aarch64.rpm")]
    [InlineData("custos-desktop-{0}-1.aarch64.rpm", "custos-desktop-{0}-1.x86_64.rpm")]
    public void Desktop_needs_the_exact_official_package_even_when_another_release_is_newer(string expected, string other)
    {
        string AssetName(string version) => expected.Replace("{0}", version, StringComparison.Ordinal);
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            Release("client-v2.5.0", AssetName("2.5.0"), urlTag: "client-v2.4.0"),
            Release("client-v2.4.0", AssetName("2.4.0"), draft: true),
            Release("client-v2.3.0", other.Replace("{0}", "2.3.0", StringComparison.Ordinal)),
            Release("client-v2.2.0-test", AssetName("2.2.0-test")),
            Release("client-v2.1.0", AssetName("2.1.0"), preview: true),
            Release("client-v2.0.0", AssetName("2.0.0")),
        });
        var preview = ProgramReleases.Newest(Json(json), ProgramReleases.PreviewChannel, AssetName);
        var stable = ProgramReleases.Newest(Json(json), ProgramReleases.StableChannel, AssetName);

        Assert.Equal("2.1.0", preview?.Version);
        Assert.Equal("2.0.0", stable?.Version);
        Assert.Equal(AssetName("2.0.0"), stable?.PackageName);
        Assert.Equal(new Uri(Downloads + "client-v2.0.0/" + AssetName("2.0.0")), stable?.PackageUrl);
    }

    [Fact]
    public void No_release_of_the_program_gives_null()
    {
        Assert.Null(ProgramReleases.Newest(Json("[]")));
        Assert.Null(ProgramReleases.Newest(Json("""[{"tag_name": "server-v2.0.0", "draft": false, "prerelease": false, "assets": []}]""")));
        Assert.Throws<ReleaseCheckException>(() => ProgramReleases.Newest(Json("""{"message": "Not Found"}""")));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.0.0", "0.5.69", true)]
    [InlineData("1.0.0", "1.0.0.1", false)]
    [InlineData("1.0.1", "1.0.0.5", true)]
    [InlineData("1.0.0", "1.0.0+abc", false)]
    [InlineData("1.0", "0.9.0", false)]
    [InlineData("beta", "1.0.0", false)]
    [InlineData("1.0.0", "?", false)]
    public void Versions_compare_by_number(string candidate, string installed, bool newer) =>
        Assert.Equal(newer, ProgramReleases.IsNewer(candidate, installed));

    [Fact]
    public void The_sum_of_the_package_is_found_in_sha256sums()
    {
        string sums = $"{new string('a', 64)}  CUSTOS-Setup-1.2.0.exe\n{PackageSha.ToUpperInvariant()}  custos-1.2.0.zip\r\n{new string('b', 64)} *custos-helper\n";

        Assert.Equal(PackageSha, ProgramReleases.FindSum(sums, "custos-1.2.0.zip"));
        Assert.Equal(new string('b', 64), ProgramReleases.FindSum(sums, "custos-helper"));
        Assert.Null(ProgramReleases.FindSum(sums, "custos-1.1.0.zip"));
        Assert.Null(ProgramReleases.FindSum("not a sums file", "custos-1.2.0.zip"));
    }

    [Fact]
    public async Task The_package_is_kept_only_when_its_checksum_matches()
    {
        var release = ProgramReleases.Newest(Json(Releases($"\"sha256:{PackageSha}\"")))!;
        string folder = _temp.Combine("updates");

        using (var http = new HttpClient(new Answer(Package)))
        {
            string path = await ProgramReleases.DownloadAsync(http, release, PackageSha, folder, TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(folder, Zip("1.2.0")), path);
            Assert.Equal(Package, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }

        File.Delete(Path.Combine(folder, release.PackageName));
        using (var http = new HttpClient(new Answer(Encoding.ASCII.GetBytes("PK something else"))))
        {
            var ex = await Assert.ThrowsAsync<ReleaseCheckException>(() => ProgramReleases.DownloadAsync(http, release, PackageSha, folder, TestContext.Current.CancellationToken));
            Assert.Contains(PackageSha, ex.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(folder));
        using (var http = new HttpClient(new Answer(null)))
        {
            await Assert.ThrowsAsync<ReleaseCheckException>(() => ProgramReleases.DownloadAsync(http, release, PackageSha, folder, TestContext.Current.CancellationToken));
        }

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void The_state_of_the_check_survives_a_restart()
    {
        var state = new ProgramCheckState
        {
            CheckedAt = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
            Latest = ProgramReleases.Newest(Json(Releases($"\"sha256:{PackageSha}\""))),
            Announced = "1.2.0",
        };

        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state, UpdatesJsonContext.Default.ProgramCheckState);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, UpdatesJsonContext.Default.ProgramCheckState)!;

        Assert.Equal(state.CheckedAt, back.CheckedAt);
        Assert.Equal("1.2.0", back.Latest?.Version);
        Assert.Equal(PackageSha, back.Latest?.Sha256);
        Assert.Equal(state.Latest!.PackageUrl, back.Latest?.PackageUrl);
        Assert.Equal("1.2.0", back.Announced);
        Assert.Null(back.Error);
    }

    private static MemoryStream Json(string text) => new(Encoding.UTF8.GetBytes(text));

    private static object Release(string tag, string name, bool draft = false, bool preview = false, string? urlTag = null) =>
        new
        {
            tag_name = tag,
            draft,
            prerelease = preview,
            assets = new[]
            {
                new { name, digest = $"sha256:{PackageSha}", browser_download_url = $"{Downloads}{urlTag ?? tag}/{name}" },
            },
        };

    /// <summary>The package of the edition that shares the releases with this one: Windows next to macOS.</summary>
    private static string OtherZip(string version) => CustosEdition.IsMac ? $"custos-{version}.zip" : $"custos-{CustosEdition.MacName}-{version}.zip";

    /// <summary>
    /// Shaped like the answer of api.github.com/repos/…/releases (fields that are not used left out): releases of the
    /// site, a newer pre-release, a draft and an older release of the program must not win over client-v1.2.0, and the
    /// package of the other edition in the same release (Windows or macOS) is never taken.
    /// </summary>
    private static string Releases(string digest) => $$"""
        [
          {"tag_name": "server-v1.5.0", "draft": false, "prerelease": false, "assets": [
            {"name": "custos-server-1.5.0.zip", "size": 900000, "digest": "sha256:{{new string('c', 64)}}",
             "browser_download_url": "{{Downloads}}server-v1.5.0/custos-server-1.5.0.zip"}]},
          {"tag_name": "client-v1.3.0", "draft": false, "prerelease": true, "assets": [
            {"name": "{{Zip("1.3.0")}}", "size": 1000, "digest": "sha256:{{new string('d', 64)}}",
             "browser_download_url": "{{Downloads}}client-v1.3.0/{{Zip("1.3.0")}}"}]},
          {"tag_name": "client-v1.4.0", "draft": true, "prerelease": false, "assets": []},
          {"tag_name": "client-v1.2.0", "draft": false, "prerelease": false,
           "html_url": "https://github.com/{{CustosInfo.GitHubRepository}}/releases/tag/client-v1.2.0", "assets": [
            {"name": "CUSTOS-Setup-1.2.0.exe", "size": 50000000, "digest": "sha256:{{new string('e', 64)}}",
             "browser_download_url": "{{Downloads}}client-v1.2.0/CUSTOS-Setup-1.2.0.exe"},
            {"name": "{{Zip("1.2.0")}}", "size": {{Package.Length}}, "digest": {{digest}},
             "browser_download_url": "{{Downloads}}client-v1.2.0/{{Zip("1.2.0")}}"},
            {"name": "{{OtherZip("1.2.0")}}", "size": 1000, "digest": "sha256:{{new string('9', 64)}}",
             "browser_download_url": "{{Downloads}}client-v1.2.0/{{OtherZip("1.2.0")}}"},
            {"name": "SHA256SUMS.txt", "size": 300, "digest": null,
             "browser_download_url": "{{Downloads}}client-v1.2.0/SHA256SUMS.txt"}]},
          {"tag_name": "client-v1.1.0", "draft": false, "prerelease": false, "assets": [
            {"name": "{{Zip("1.1.0")}}", "size": 1000, "digest": "sha256:{{new string('f', 64)}}",
             "browser_download_url": "{{Downloads}}client-v1.1.0/{{Zip("1.1.0")}}"}]}
        ]
        """;

    /// <summary>Answers every request with these bytes, or 404 for null.</summary>
    private sealed class Answer(byte[]? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
