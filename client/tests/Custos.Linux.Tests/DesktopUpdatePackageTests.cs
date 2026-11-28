using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core;
using Custos.Core.Monitor;
using Custos.Service.Agent;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

public sealed class DesktopUpdatePackageTests
{
    private const string Version = "1.5.0";
    private const string DescriptorEntry = "linux-desktop-packages.json";
    private static readonly byte[] HeadlessArchive = "fixture from the standalone Linux archive"u8.ToArray();

    private static readonly (string Name, string Content)[] Assets =
    [
        ("custos-desktop_1.5.0_amd64.deb", "test DEB amd64"),
        ("custos-desktop_1.5.0_arm64.deb", "test DEB arm64"),
        ("custos-desktop-1.5.0-1.x86_64.rpm", "test RPM x86_64"),
        ("custos-desktop-1.5.0-1.aarch64.rpm", "test RPM aarch64"),
    ];

    [Theory]
    [InlineData("deb", Architecture.X64, 0)]
    [InlineData("deb", Architecture.Arm64, 1)]
    [InlineData("rpm", Architecture.X64, 2)]
    [InlineData("rpm", Architecture.Arm64, 3)]
    public void Desktop_asset_is_selected_by_native_package_format_and_architecture(string format, Architecture architecture, int index)
    {
        string zip = WriteZip(Descriptor());

        var selected = DesktopUpdatePackage.Read(zip, Version, format, architecture);

        var expected = Assets[index];
        Assert.Equal(expected.Name, selected.Name);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected.Content), selected.Size);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(expected.Content)), selected.Sha256);
        Assert.Equal("https://github.com/openpass1/custos/releases/download/client-v1.5.0/" + expected.Name,
            selected.DownloadUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("other", Architecture.X64)]
    [InlineData("deb", Architecture.X86)]
    public void Unknown_package_format_or_architecture_is_refused(string format, Architecture architecture)
    {
        string zip = WriteZip(Descriptor());

        Assert.Throws<InvalidDataException>(() => DesktopUpdatePackage.Read(zip, Version, format, architecture));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("edition")]
    [InlineData("protocol")]
    [InlineData("descriptor hash")]
    [InlineData("not declared")]
    [InlineData("duplicate member")]
    public void The_descriptor_must_be_declared_once_in_the_expected_linux_manifest(string change)
    {
        string zip = WriteZip(Descriptor(), change);

        Assert.Throws<InvalidDataException>(() => DesktopUpdatePackage.Read(zip, Version, "deb", Architecture.X64));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missing asset")]
    [InlineData("extra asset")]
    [InlineData("empty asset")]
    [InlineData("oversized asset")]
    [InlineData("bad hash")]
    [InlineData("asset URL")]
    [InlineData("duplicate field")]
    [InlineData("oversized descriptor")]
    public void Invalid_desktop_metadata_is_refused_even_when_the_manifest_hash_matches(string change)
    {
        string zip = WriteZip(Descriptor(change));

        Assert.Throws<InvalidDataException>(() => DesktopUpdatePackage.Read(zip, Version, "deb", Architecture.X64));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_standalone_agent_extracts_both_old_archives_and_archives_with_desktop_metadata(bool withDesktopDescriptor)
    {
        string zip = WriteZip(withDesktopDescriptor ? Descriptor() : null);
        string staging = Path.Combine(TestPlace.NewFolder("headless-update"), "staging");

        Assert.Null(AgentService.Unpack(zip, staging, Version));
        Assert.Equal(HeadlessArchive, File.ReadAllBytes(Path.Combine(staging, LinuxPaths.ProgramArchive)));
        if (!withDesktopDescriptor)
        {
            Assert.Throws<InvalidDataException>(() => DesktopUpdatePackage.Read(zip, Version, "deb", Architecture.X64));
        }
    }

    [Fact]
    public void The_existing_monitor_signature_binds_the_linux_edition_version_and_entire_archive()
    {
        string archiveHash = Hash(File.ReadAllBytes(WriteZip(Descriptor())));
        string otherHash = Hash(File.ReadAllBytes(WriteZip(Descriptor(replacementContents: "different native release bytes"))));
        using var monitorKey = RSA.Create(2048);
        using var agentKey = RSA.Create();
        agentKey.ImportRSAPublicKey(monitorKey.ExportRSAPublicKey(), out _);
        string signature = MonitorCrypto.Sign(monitorKey, $"linux\n{Version}\n{archiveHash}");

        Assert.True(MonitorCrypto.Verify(agentKey, CustosEdition.PackageSignedString(Version, archiveHash), signature));
        Assert.False(MonitorCrypto.Verify(agentKey, CustosEdition.PackageSignedString("1.5.1", archiveHash), signature));
        Assert.False(MonitorCrypto.Verify(agentKey, CustosEdition.PackageSignedString(Version, otherHash), signature));
        Assert.False(MonitorCrypto.Verify(agentKey, $"macos\n{Version}\n{archiveHash}", signature));
    }

    private static string Descriptor(string? change = null, string? replacementContents = null)
    {
        var packages = new JsonObject();
        foreach (var asset in Assets)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(replacementContents ?? asset.Content);
            packages[asset.Name] = new JsonObject { ["size"] = bytes.Length, ["sha256"] = Hash(bytes) };
        }

        var first = packages[Assets[0].Name]!.AsObject();
        string version = Version;
        switch (change)
        {
            case "version": version = "1.5.1"; break;
            case "missing asset": packages.Remove(Assets[3].Name); break;
            case "extra asset": packages["custos-optional.deb"] = first.DeepClone(); break;
            case "empty asset": first["size"] = 0; break;
            case "oversized asset": first["size"] = 500L * 1024 * 1024 + 1; break;
            case "bad hash": first["sha256"] = "not-a-sha256"; break;
            case "asset URL": first["url"] = "https://packages.example.invalid/custom.deb"; break;
        }

        string json = new JsonObject { ["version"] = version, ["packages"] = packages }.ToJsonString();
        return change switch
        {
            "duplicate field" => "{\"version\":\"1.5.0\"," + json[1..],
            "oversized descriptor" => json.PadRight(16 * 1024 + 1),
            _ => json,
        };
    }

    private static string WriteZip(string? descriptor, string? change = null)
    {
        string zipPath = Path.Combine(TestPlace.NewFolder("desktop-update"), "update.zip");
        byte[]? descriptorBytes = descriptor is not null ? Encoding.UTF8.GetBytes(descriptor) : null;
        var manifest = new UpdateManifest
        {
            Version = change == "version" ? "1.5.1" : Version,
            Edition = change == "edition" ? "macos" : "linux",
            MinProtocol = change == "protocol" ? int.MaxValue : 1,
        };
        manifest.Files.Add(new UpdateManifestFile { Path = LinuxPaths.ProgramArchive, Sha256 = Hash(HeadlessArchive) });
        if (descriptorBytes is not null && change != "not declared")
        {
            manifest.Files.Add(new UpdateManifestFile
            {
                Path = DescriptorEntry,
                Sha256 = change == "descriptor hash" ? new string('0', 64) : Hash(descriptorBytes),
            });
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Write(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, MonitorJsonContext.Default.UpdateManifest));
        Write(zip, LinuxPaths.ProgramArchive, HeadlessArchive);
        if (descriptorBytes is not null)
        {
            Write(zip, DescriptorEntry, descriptorBytes);
            if (change == "duplicate member")
            {
                Write(zip, DescriptorEntry, descriptorBytes);
            }
        }

        return zipPath;
    }

    private static void Write(ZipArchive zip, string path, byte[] content)
    {
        using var stream = zip.CreateEntry(path).Open();
        stream.Write(content);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
