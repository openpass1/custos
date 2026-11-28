using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Custos.Core;
using Custos.Core.Monitor;

namespace Custos.Service.Platform;

internal sealed record DesktopUpdateAsset(string Name, long Size, string Sha256, Uri DownloadUrl);

/// <summary>The native assets referenced by a Linux update ZIP already authenticated by Monitor.</summary>
internal static class DesktopUpdatePackage
{
    internal const string DescriptorName = "linux-desktop-packages.json";
    private const long MaxAssetSize = 500L * 1024 * 1024;
    private const int MaxManifestSize = 256 * 1024;
    private const int MaxDescriptorSize = 16 * 1024;
    private const int MaxRedirects = 3;
    private const string ReleasePrefix = "https://github.com/" + CustosInfo.GitHubRepository + "/releases/download/client-v";

    internal static DesktopUpdateAsset Read(string zipPath, string version, string nativeFormat, Architecture architecture)
    {
        if (!IsReleaseVersion(version))
        {
            throw new InvalidDataException(Messages.MonitorPackageBadCommand);
        }

        string selected = (nativeFormat, architecture) switch
        {
            ("deb", Architecture.X64) => $"custos-desktop_{version}_amd64.deb",
            ("deb", Architecture.Arm64) => $"custos-desktop_{version}_arm64.deb",
            ("rpm", Architecture.X64) => $"custos-desktop-{version}-1.x86_64.rpm",
            ("rpm", Architecture.Arm64) => $"custos-desktop-{version}-1.aarch64.rpm",
            _ => throw new InvalidDataException(Messages.ProgramManagedByPackage),
        };

        using var zip = ZipFile.OpenRead(zipPath);
        try
        {
            using var manifestJson = JsonDocument.Parse(ReadEntry(zip, "manifest.json", MaxManifestSize));
            if (!HasUniqueProperties(manifestJson.RootElement))
            {
                throw BadDescriptor();
            }

            var manifest = manifestJson.RootElement.Deserialize(MonitorJsonContext.Default.UpdateManifest);
            if (manifest is null || manifest.Version != version)
            {
                throw new InvalidDataException(Messages.Format(Messages.MonitorPackageOtherVersion, manifest?.Version, version));
            }

            if (manifest.MinProtocol > int.Parse(MonitorClient.ProtocolVersion, CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException(Messages.Format(Messages.MonitorPackageProtocol, manifest.MinProtocol));
            }

            if (manifest.Edition != "linux")
            {
                throw new InvalidDataException(Messages.MonitorPackageNotLinux);
            }

            var entries = manifest.Files?.Where(file => file is not null && string.Equals(file.Path, DescriptorName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries is { Length: 0 } && !zip.Entries.Any(entry => string.Equals(entry.FullName, DescriptorName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(LinuxStrings.DesktopUpdateUnavailable);
            }

            if (entries is not [{ Path: DescriptorName } entry])
            {
                throw new InvalidDataException(Messages.Format(Messages.MonitorPackageMissingFile, DescriptorName));
            }

            byte[] descriptor = ReadEntry(zip, DescriptorName, MaxDescriptorSize);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(descriptor)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(Messages.Format(Messages.MonitorPackageFileHash, DescriptorName));
            }

            return ParseDescriptor(descriptor, version, selected);
        }
        catch (Exception ex) when (ex is JsonException or EndOfStreamException)
        {
            throw BadDescriptor(ex);
        }
    }

    private static bool HasUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().All(property => names.Add(property.Name) && HasUniqueProperties(property.Value));
        }

        return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(HasUniqueProperties);
    }

    private static DesktopUpdateAsset ParseDescriptor(byte[] descriptor, string version, string selected)
    {
        using var document = JsonDocument.Parse(descriptor);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("version", out var declared) || declared.ValueKind != JsonValueKind.String || declared.GetString() != version
            || !root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object)
        {
            throw BadDescriptor();
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            $"custos-desktop_{version}_amd64.deb",
            $"custos-desktop_{version}_arm64.deb",
            $"custos-desktop-{version}-1.x86_64.rpm",
            $"custos-desktop-{version}-1.aarch64.rpm",
        };

        DesktopUpdateAsset? match = null;
        foreach (var item in packages.EnumerateObject())
        {
            if (!expected.Remove(item.Name) || item.Value.ValueKind != JsonValueKind.Object || item.Value.EnumerateObject().Count() != 2
                || !item.Value.TryGetProperty("size", out var bytes) || bytes.ValueKind != JsonValueKind.Number
                || !bytes.TryGetInt64(out long size) || size is < 1 or > MaxAssetSize
                || !item.Value.TryGetProperty("sha256", out var hashValue) || hashValue.ValueKind != JsonValueKind.String
                || hashValue.GetString() is not { Length: 64 } hash || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw BadDescriptor();
            }

            if (item.Name == selected)
            {
                match = new DesktopUpdateAsset(item.Name, size, hash, new Uri(ReleasePrefix + version + "/" + selected));
            }
        }

        return expected.Count == 0 && match is not null ? match : throw BadDescriptor();
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, int maxBytes)
    {
        var entries = zip.Entries.Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries is not [var entry] || entry.FullName != name || entry.Length <= 0 || entry.Length > maxBytes)
        {
            throw new InvalidDataException(Messages.Format(Messages.MonitorPackageMissingFile, name));
        }

        var bytes = new byte[(int)entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(bytes);
        if (stream.ReadByte() >= 0)
        {
            throw BadDescriptor();
        }

        return bytes;
    }

    internal static bool IsReleaseVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length is >= 1 and <= 5 && part.All(char.IsAsciiDigit));
    }

    private static InvalidDataException BadDescriptor(Exception? inner = null) =>
        new(Messages.Format(Messages.MonitorPackageFileHash, DescriptorName), inner);

    /// <summary>Fetches only the selected authenticated asset. The caller supplies a client with automatic redirects disabled.</summary>
    internal static async Task<string> DownloadAsync(HttpClient http, DesktopUpdateAsset asset, string folder, CancellationToken cancellationToken)
    {
        if (Path.GetFileName(asset.Name) != asset.Name || asset.Size is < 1 or > MaxAssetSize
            || !asset.DownloadUrl.IsAbsoluteUri || asset.DownloadUrl.UserInfo.Length != 0 || !asset.DownloadUrl.IsDefaultPort
            || !asset.DownloadUrl.AbsoluteUri.StartsWith(ReleasePrefix, StringComparison.Ordinal)
            || !asset.DownloadUrl.AbsoluteUri.EndsWith("/" + asset.Name, StringComparison.Ordinal))
        {
            throw BadAsset(asset);
        }

        // No Monitor request object or credentials are used for these requests.
        using var response = await RequestReleaseAsync(http, asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
        {
            throw BadAsset(asset);
        }

        string package = Path.Combine(folder, asset.Name);
        bool created = false;
        try
        {
            var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(package, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Options = FileOptions.Asynchronous,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                created = true;
                await using (output.ConfigureAwait(false))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > asset.Size)
                        {
                            throw BadAsset(asset);
                        }

                        hash.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    if (total != asset.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
                    {
                        throw BadAsset(asset);
                    }
                }
            }

            return package;
        }
        catch
        {
            if (created)
            {
                File.Delete(package);
            }

            throw;
        }
    }

    private static InvalidDataException BadAsset(DesktopUpdateAsset asset) =>
        new(Messages.Format(Messages.MonitorPackageFileHash, asset.Name));

    private static async Task<HttpResponseMessage> RequestReleaseAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        for (int redirects = 0; ; redirects++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                {
                    throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"GitHub HTTP {(int)response.StatusCode}"), null, response.StatusCode);
                }

                if (redirects == MaxRedirects || response.Headers.Location is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } location
                    || location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                {
                    throw new HttpRequestException("GitHub release download returned an unsupported redirect.");
                }

                uri = location;
            }
        }
    }
}
