using System.Security.Cryptography;
using Custos.Core.Agent;

namespace Custos.Service.Platform;

/// <summary>
/// Protects the stored passwords on Linux, as DPAPI does on Windows (section 3.3): AES-256-GCM with a key of this
/// computer in <c>/var/lib/custos/config/credentials.key</c>, which only root can read (the folder is closed to the
/// others too). The format is that of CUSTOS for macOS. Linux has no keychain of the system for services; whoever is
/// root can read the key, as whoever is an administrator of Windows can use DPAPI of the computer.
/// </summary>
internal sealed class KeyFileProtector : ISecretProtector
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] Header = "CK1"u8.ToArray();
    private static readonly Lock KeyLock = new();
    private static byte[]? _key;

    /// <summary>The key file.</summary>
    internal static string KeyFile => Path.Combine(DataFolders.Config, "credentials.key");

    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        byte[] result = new byte[Header.Length + NonceSize + TagSize + data.Length];
        Header.CopyTo(result, 0);
        var nonce = result.AsSpan(Header.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, data, result.AsSpan(Header.Length + NonceSize + TagSize), result.AsSpan(Header.Length + NonceSize, TagSize), Header);
        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < Header.Length + NonceSize + TagSize || !data.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            throw new CryptographicException("Not protected by this computer.");
        }

        byte[] plain = new byte[data.Length - Header.Length - NonceSize - TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Decrypt(data.AsSpan(Header.Length, NonceSize), data.AsSpan(Header.Length + NonceSize + TagSize), data.AsSpan(Header.Length + NonceSize, TagSize), plain, Header);
        return plain;
    }

    private static byte[] Key()
    {
        lock (KeyLock)
        {
            return _key ??= LoadKey();
        }
    }

    private static byte[] LoadKey()
    {
        if (File.Exists(KeyFile))
        {
            byte[] stored = File.ReadAllBytes(KeyFile);
            if (stored.Length == KeySize)
            {
                return stored;
            }

            throw new CryptographicException($"{KeyFile} is damaged: it should hold {KeySize} bytes.");
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        Directory.CreateDirectory(DataFolders.Config);
        using (var file = new FileStream(KeyFile, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            file.Write(key);
            file.Flush(flushToDisk: true);
        }

        return key;
    }
}
