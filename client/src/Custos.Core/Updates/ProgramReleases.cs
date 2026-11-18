using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custos.Core.Updates;

/// <summary>
/// A published release of CUSTOS for Windows on GitHub: its update package and how to check it. The SHA-256 comes
/// from GitHub's own digest of the file, or else from the file SHA256SUMS.txt of the same release.
/// </summary>
public sealed class ProgramRelease
{
    /// <summary>"1.1.0": the tag without <see cref="ProgramReleases.TagPrefix"/>.</summary>
    public required string Version { get; init; }

    /// <summary><c>custos-1.1.0.zip</c>, the same package a monitoring site installs.</summary>
    public required string PackageName { get; init; }

    public required Uri PackageUrl { get; init; }

    public long Size { get; init; }

    /// <summary>Lower-case hex, from the digest GitHub shows for the file; null when GitHub shows none.</summary>
    public string? Sha256 { get; init; }

    /// <summary>SHA256SUMS.txt of the release, for when <see cref="Sha256"/> is null.</summary>
    public Uri? SumsUrl { get; init; }

    /// <summary>The page of the release: what is new.</summary>
    public required Uri PageUrl { get; init; }
}

/// <summary>What the service last learned from GitHub, kept in <c>program-update.json</c> between restarts.</summary>
public sealed class ProgramCheckState
{
    /// <summary>When GitHub was last asked (also after a failed attempt: the next one is a day later).</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>The newest release found, newer or not than the installed version; null when there is none.</summary>
    public ProgramRelease? Latest { get; set; }

    /// <summary>Why the last attempt failed; null when it worked.</summary>
    public string? Error { get; set; }

    /// <summary>The newest version already written to the log, so that each one is told once.</summary>
    public string? Announced { get; set; }
}

/// <summary>Why GitHub could not tell or give a release.</summary>
public sealed class ReleaseCheckException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// New versions of CUSTOS for Windows on GitHub (releases with the tag <c>client-vX.Y.Z</c>). A computer that is
/// not connected to a monitoring site asks once a day; the update is installed only when an administrator presses
/// «Обновить». A computer connected to a site never asks: the site decides about updates.
/// </summary>
public static class ProgramReleases
{
    /// <summary>Tags of the program (<c>client-v</c>, or <c>client-legacy-v</c> for the legacy edition); the site has its own (<c>server-v</c>).</summary>
    public static string TagPrefix => CustosEdition.ReleaseTagPrefix;

    /// <summary>The update package never is bigger; a bigger answer is not saved.</summary>
    public const long MaxPackageBytes = 500L * 1024 * 1024;

    /// <summary>SHA256SUMS.txt holds a few lines; a bigger file is not one of ours.</summary>
    public const int MaxSumsBytes = 64 * 1024;

    /// <summary>The releases of the repository, newest first. Drafts are not listed to anyone but the owner.</summary>
    public static Uri ApiUrl => new($"https://api.github.com/repos/{CustosInfo.GitHubRepository}/releases?per_page=30");

    private static string ReleasesPrefix => $"https://github.com/{CustosInfo.GitHubRepository}/releases/";

    /// <summary>
    /// The newest published release of the program in an answer of <see cref="ApiUrl"/> that can be installed: it
    /// has the update package and a SHA-256 to check it with. Pre-releases are skipped. Null when there is none.
    /// </summary>
    /// <exception cref="ReleaseCheckException">The answer is not a list of releases.</exception>
    public static ProgramRelease? Newest(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ReleaseCheckException(Messages.Format(Messages.ProgramCheckFailed, ex.Message), ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ReleaseCheckException(Messages.Format(Messages.ProgramCheckFailed, Messages.ProgramBadAnswer));
            }

            ProgramRelease? best = null;
            int[]? bestVersion = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (IsTrue(release, "draft") || IsTrue(release, "prerelease")
                    || Text(release, "tag_name") is not { } tag || !tag.StartsWith(TagPrefix, StringComparison.Ordinal)
                    || ParseVersion(tag[TagPrefix.Length..]) is not { } version
                    || (bestVersion is not null && Compare(version, bestVersion) <= 0)
                    || Read(release, tag[TagPrefix.Length..]) is not { } found)
                {
                    continue;
                }

                best = found;
                bestVersion = version;
            }

            return best;
        }
    }

    /// <summary>"1.2.3" or "1.2.3.4" → its numbers; null if it is not a version like that. "+…" and "-…" are cut off.</summary>
    public static int[]? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string plain = text.Trim();
        int suffix = plain.IndexOfAny(['+', '-']);
        if (suffix >= 0)
        {
            plain = plain[..suffix];
        }

        string[] parts = plain.Split('.');
        if (parts.Length is < 3 or > 4)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 9 || !parts[i].All(char.IsAsciiDigit))
            {
                return null;
            }

            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return numbers;
    }

    /// <summary>True if <paramref name="candidate"/> is a newer version than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? candidate, string? installed) =>
        ParseVersion(candidate) is { } a && ParseVersion(installed) is { } b && Compare(a, b) > 0;

    /// <summary>The SHA-256 of <paramref name="fileName"/> in the text of SHA256SUMS.txt (lines "hash  name" as
    /// sha256sum writes them); null if the file is not listed.</summary>
    public static string? FindSum(string sums, string fileName)
    {
        ArgumentNullException.ThrowIfNull(sums);
        foreach (string raw in sums.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 66 || line[64] != ' ')
            {
                continue;
            }

            string hash = line[..64];
            string name = line[65..].TrimStart(' ', '*');
            if (hash.All(Uri.IsHexDigit) && string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads the update package into <paramref name="folder"/> and checks its SHA-256. A file that does not
    /// match is deleted and never used.
    /// </summary>
    /// <returns>The path of the checked file.</returns>
    /// <exception cref="ReleaseCheckException">The download failed, the file is too big or the checksum differs.</exception>
    public static async Task<string> DownloadAsync(HttpClient http, ProgramRelease release, string sha256, string folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, release.PackageName);
        string partial = target + ".partial";
        try
        {
            using (var response = await http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxPackageBytes)
                {
                    throw new ReleaseCheckException(Messages.Format(Messages.ProgramDownloadFailed, release.PackageUrl, Messages.ProgramTooBig));
                }

                var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (input.ConfigureAwait(false))
                {
                    var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await using (output.ConfigureAwait(false))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxPackageBytes)
                            {
                                throw new ReleaseCheckException(Messages.Format(Messages.ProgramDownloadFailed, release.PackageUrl, Messages.ProgramTooBig));
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            string actual = await Sha256Async(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ReleaseCheckException(Messages.Format(Messages.ProgramChecksumMismatch, release.PackageName, actual, sha256));
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            TryDelete(partial);
            throw new ReleaseCheckException(Messages.Format(Messages.ProgramDownloadFailed, release.PackageUrl, ex.Message), ex);
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

    /// <summary>The package of a release and how to check it; null if it has no package or nothing to check it with.</summary>
    private static ProgramRelease? Read(JsonElement release, string version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string packageName = CustosEdition.PackageName(version);
        string downloads = ReleasesPrefix + "download/";
        JsonElement? package = null;
        Uri? sums = null;
        foreach (var asset in assets.EnumerateArray())
        {
            string? name = Text(asset, "name");
            string? url = Text(asset, "browser_download_url");
            if (url is null || !url.StartsWith(downloads, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(name, packageName, StringComparison.Ordinal))
            {
                package = asset;
            }
            else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.Ordinal))
            {
                sums = new Uri(url);
            }
        }

        if (package is not { } file)
        {
            return null;
        }

        string? digest = Text(file, "digest");
        string? sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64
            && digest[7..].All(Uri.IsHexDigit) ? digest[7..].ToLowerInvariant() : null;
        if (sha256 is null && sums is null)
        {
            return null;
        }

        string? page = Text(release, "html_url");
        return new ProgramRelease
        {
            Version = version,
            PackageName = packageName,
            PackageUrl = new Uri(Text(file, "browser_download_url")!),
            Size = file.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0,
            Sha256 = sha256,
            SumsUrl = sums,
            PageUrl = page is not null && page.StartsWith(ReleasesPrefix, StringComparison.Ordinal)
                ? new Uri(page) : new Uri(ReleasesPrefix + "tag/" + TagPrefix + version),
        };
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

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProgramCheckState))]
public sealed partial class UpdatesJsonContext : JsonSerializerContext;
