using System.Net;
using System.Security.Cryptography;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

public sealed class DesktopUpdateDownloadTests
{
    private const string Name = "custos-desktop_1.5.0_amd64.deb";
    private static readonly Uri ReleaseUrl = new("https://github.com/openpass1/custos/releases/download/client-v1.5.0/" + Name);
    private static readonly Uri AssetUrl = new("https://release-assets.githubusercontent.com/fixture");

    [Theory]
    [InlineData("release-assets.githubusercontent.com")]
    [InlineData("objects.githubusercontent.com")]
    public async Task The_official_release_redirect_yields_a_private_verified_file(string host)
    {
        byte[] content = "native package fixture"u8.ToArray();
        string folder = Staging();
        var target = new Uri("https://" + host + "/fixture");
        var handler = new Responses(_ => Redirect(target), _ => Bytes(content));
        using var http = new HttpClient(handler);

        string path = await DesktopUpdatePackage.DownloadAsync(http, Asset(content), folder, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ReleaseUrl, target }, handler.Requests);
        Assert.Equal(folder, Path.GetDirectoryName(path));
        Assert.Equal(Name, Path.GetFileName(path));
        Assert.Equal(content, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Theory]
    [InlineData("https://packages.example.invalid/asset")]
    [InlineData("http://release-assets.githubusercontent.com/asset")]
    [InlineData("https://release-assets.githubusercontent.com.example.invalid/asset")]
    [InlineData("https://release-assets.githubusercontent.com:444/asset")]
    [InlineData("https://example@release-assets.githubusercontent.com/asset")]
    [InlineData("/relative/asset")]
    public async Task Disallowed_redirects_are_rejected_before_any_request_to_the_new_URL(string location)
    {
        string folder = Staging();
        var handler = new Responses(_ => Redirect(new Uri(location, UriKind.RelativeOrAbsolute)));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DesktopUpdatePackage.DownloadAsync(http, Asset("native package fixture"u8.ToArray()), folder, TestContext.Current.CancellationToken));

        Assert.Equal(ReleaseUrl, Assert.Single(handler.Requests));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task Too_many_official_redirects_are_rejected()
    {
        string folder = Staging();
        var handler = new Responses([.. Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(_ => Redirect(AssetUrl), 12)]);
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DesktopUpdatePackage.DownloadAsync(http, Asset("native package fixture"u8.ToArray()), folder, TestContext.Current.CancellationToken));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Theory]
    [InlineData("wrong digest")]
    [InlineData("short response")]
    [InlineData("oversized Content-Length")]
    [InlineData("oversized streamed body")]
    public async Task A_bad_download_leaves_no_partial_package_and_can_be_retried(string problem)
    {
        byte[] expected = "test bytes from the native package"u8.ToArray();
        byte[] wrong = [.. expected];
        wrong[0] ^= 1;
        byte[] oversized = [.. expected, 0];
        string folder = Staging();
        using var bad = new HttpClient(new Responses(_ => problem switch
        {
            "wrong digest" => Bytes(wrong),
            "short response" => Bytes(expected[..^1]),
            "oversized Content-Length" => Bytes(oversized),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableBody(oversized)) },
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DesktopUpdatePackage.DownloadAsync(bad, Asset(expected), folder, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));

        using var good = new HttpClient(new Responses(_ => Bytes(expected)));
        string path = await DesktopUpdatePackage.DownloadAsync(good, Asset(expected), folder, TestContext.Current.CancellationToken);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_after_writing_bytes_removes_the_partial_package()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        byte[] expected = "test bytes from the native package"u8.ToArray();
        string folder = Staging();
        using var body = new CancelledBody(expected, Path.Combine(folder, Name), cancellation);
        using var http = new HttpClient(new Responses(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DesktopUpdatePackage.DownloadAsync(http, Asset(expected), folder, cancellation.Token));

        Assert.True(body.SawPartialPackage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task A_file_already_in_the_private_staging_folder_is_never_replaced_or_removed()
    {
        byte[] expected = "test bytes from the native package"u8.ToArray();
        byte[] existing = "previously staged bytes"u8.ToArray();
        string folder = Staging();
        string path = Path.Combine(folder, Name);
        await File.WriteAllBytesAsync(path, existing, TestContext.Current.CancellationToken);
        using var http = new HttpClient(new Responses(_ => Bytes(expected)));

        await Assert.ThrowsAsync<IOException>(() =>
            DesktopUpdatePackage.DownloadAsync(http, Asset(expected), folder, TestContext.Current.CancellationToken));

        Assert.Equal(existing, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    private static DesktopUpdateAsset Asset(byte[] data) => new(Name, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), ReleaseUrl);

    private static string Staging()
    {
        string path = TestPlace.NewFolder("desktop-download");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static HttpResponseMessage Redirect(Uri location) => new(HttpStatusCode.Redirect) { Headers = { Location = location } };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class Responses(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain(request.Headers, header => header.Key.StartsWith("X-Custos-", StringComparison.OrdinalIgnoreCase));
            int index = Requests.Count;
            Requests.Add(request.RequestUri!);
            Assert.True(index < replies.Length, "A request went beyond the expected redirect sequence.");
            return Task.FromResult(replies[index](request));
        }
    }

    private sealed class UnseekableBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class CancelledBody(byte[] bytes, string path, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public bool SawPartialPackage { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0)
            {
                return base.ReadAsync(buffer[..1], cancellationToken);
            }

            SawPartialPackage = File.Exists(path);
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }
}
