using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Custos.Core.Archives.SevenZip;

/// <summary>A release of 7-Zip: its x64 installer and the SHA-256 GitHub publishes for it.</summary>
public sealed record SevenZipRelease(string Version, string InstallerName, Uri InstallerUrl, string Sha256, long Size);

/// <summary>
/// Finds the newest 7-Zip on GitHub (github.com/ip7z/7zip, the official releases) and downloads its installer,
/// checking it against the SHA-256 GitHub shows for the file. Only when an administrator presses the button: the
/// client never looks for updates by itself.
/// </summary>
public static class SevenZipReleases
{
    /// <summary>The releases of 7-Zip, newest first. The "latest" mark on GitHub is not always moved, so the newest
    /// stable one is picked by its version number.</summary>
    public static readonly Uri ApiUrl = new("https://api.github.com/repos/ip7z/7zip/releases?per_page=30");

    private const string DownloadPrefix = "https://github.com/ip7z/7zip/releases/download/";

    /// <summary>The installer that matches this program: <c>7z2606-x64.exe</c> for the 64-bit program, <c>7z2606.exe</c>
    /// for the 32-bit legacy edition.</summary>
    internal static string InstallerSuffix => Environment.Is64BitProcess ? "-x64" : string.Empty;

    /// <summary>The newest stable release in an answer of <see cref="ApiUrl"/>.</summary>
    /// <exception cref="ArchiveException">There is none, or its x64 installer has no SHA-256 to check it with.</exception>
    public static SevenZipRelease Newest(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArchiveException(Messages.Format(Messages.SevenZipCheckFailed, ex.Message), ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArchiveException(Messages.SevenZipNoRelease);
            }

            JsonElement? best = null;
            int[]? bestVersion = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (IsTrue(release, "draft") || IsTrue(release, "prerelease")
                    || !release.TryGetProperty("tag_name", out var tag) || ParseVersion(tag.GetString()) is not { } version)
                {
                    continue;
                }

                if (bestVersion is null || Compare(version, bestVersion) > 0)
                {
                    best = release;
                    bestVersion = version;
                }
            }

            if (best is not { } chosen || bestVersion is null)
            {
                throw new ArchiveException(Messages.SevenZipNoRelease);
            }

            string versionText = chosen.GetProperty("tag_name").GetString()!;
            string installer = string.Create(CultureInfo.InvariantCulture, $"7z{versionText.Replace(".", string.Empty, StringComparison.Ordinal)}{InstallerSuffix}.exe");
            if (chosen.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(Text(asset, "name"), installer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? url = Text(asset, "browser_download_url");
                    string? digest = Text(asset, "digest");
                    if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 7 + 64)
                    {
                        throw new ArchiveException(Messages.Format(Messages.SevenZipNoDigest, installer));
                    }

                    long size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out long s) ? s : 0;
                    return new SevenZipRelease(versionText, installer, new Uri(url), digest[7..].ToLowerInvariant(), size);
                }
            }

            throw new ArchiveException(Messages.Format(Messages.SevenZipNoInstaller, versionText, installer));
        }
    }

    /// <summary>"26.06" → [26, 6]; null if it is not a plain version like that.</summary>
    public static int[]? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Trim().Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    /// <summary>True if <paramref name="candidate"/> is a newer version than <paramref name="installed"/> (or nothing is installed).</summary>
    public static bool IsNewer(string candidate, string? installed)
    {
        var a = ParseVersion(candidate);
        if (a is null)
        {
            return false;
        }

        return ParseVersion(installed) is not { } b || Compare(a, b) > 0;
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and checks its SHA-256. A file that does not match is
    /// deleted and never used.
    /// </summary>
    /// <returns>The path of the checked file.</returns>
    /// <exception cref="ArchiveException">The download failed or the checksum differs.</exception>
    public static async Task<string> DownloadAsync(HttpClient http, SevenZipRelease release, string folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, release.InstallerName);
        string partial = target + ".partial";
        try
        {
            using (var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await using (output.ConfigureAwait(false))
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            string actual = await Sha256Async(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new ArchiveException(Messages.Format(Messages.SevenZipChecksumMismatch, release.InstallerName, actual, release.Sha256));
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            TryDelete(partial);
            throw new ArchiveException(Messages.Format(Messages.SevenZipDownloadFailed, release.InstallerUrl, ex.Message), ex);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>The SHA-256 of a file in lower-case hex.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    private static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .partial is never used; the next download overwrites it.
        }
    }
}
