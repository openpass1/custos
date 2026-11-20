using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Custos.Core;
using Custos.Core.Agent;

namespace Custos.Service.Platform;

/// <summary>
/// Protects the stored passwords on macOS, as DPAPI does on Windows (section 3.3): AES-256-GCM with a key of this Mac
/// that lies in the System keychain (<c>/Library/Keychains/System.keychain</c>, item «CUSTOS» of service
/// <see cref="CustosInfo.MacServiceLabel"/>). A copy of the configuration folder alone is useless elsewhere. Where the
/// keychain cannot be used (Linux in tests, a service not run as root) the key is a file in <c>config</c> that only root
/// can read.
/// </summary>
internal sealed partial class KeychainProtector : ISecretProtector
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Account = "credentials-key";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ErrItemNotFound = -25300;

    private static readonly byte[] Header = "CK1"u8.ToArray();
    private static readonly Lock KeyLock = new();
    private static byte[]? _key;

    /// <summary>The fallback key file (when the keychain is not used).</summary>
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
        // A key file once made stays the key (the keychain was not usable then).
        if (File.Exists(KeyFile))
        {
            byte[] stored = File.ReadAllBytes(KeyFile);
            if (stored.Length == KeySize)
            {
                return stored;
            }
        }

        if (OperatingSystem.IsMacOS() && Native.IsRoot)
        {
            try
            {
                if (Keychain.Find(CustosInfo.MacServiceLabel, Account) is { Length: KeySize } found)
                {
                    return found;
                }

                byte[] created = RandomNumberGenerator.GetBytes(KeySize);
                Keychain.Add(CustosInfo.MacServiceLabel, Account, created);
                return created;
            }
            catch (Exception ex) when (ex is CryptographicException or DllNotFoundException or EntryPointNotFoundException)
            {
                // The file below.
            }
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
        }

        return key;
    }

    /// <summary>Generic passwords of the System keychain (the default keychain of root).</summary>
    internal static partial class Keychain
    {
        public static byte[]? Find(string service, string account)
        {
            IntPtr serviceText = CoreFoundation.String(service);
            IntPtr accountText = CoreFoundation.String(account);
            IntPtr query = CoreFoundation.Dictionary(
                (Constant("kSecClass"), Constant("kSecClassGenericPassword")),
                (Constant("kSecAttrService"), serviceText),
                (Constant("kSecAttrAccount"), accountText),
                (Constant("kSecReturnData"), CoreFoundation.True),
                (Constant("kSecMatchLimit"), Constant("kSecMatchLimitOne")));
            IntPtr result = IntPtr.Zero;
            try
            {
                int status;
                unsafe
                {
                    status = SecItemCopyMatching(query, &result);
                }

                if (status == ErrItemNotFound)
                {
                    return null;
                }

                if (status != 0 || result == IntPtr.Zero)
                {
                    throw new CryptographicException($"SecItemCopyMatching: {status}");
                }

                return CoreFoundation.Bytes(result);
            }
            finally
            {
                CoreFoundation.Release(result);
                CoreFoundation.Release(query);
                CoreFoundation.Release(serviceText);
                CoreFoundation.Release(accountText);
            }
        }

        public static void Add(string service, string account, byte[] secret)
        {
            IntPtr serviceText = CoreFoundation.String(service);
            IntPtr accountText = CoreFoundation.String(account);
            IntPtr label = CoreFoundation.String("CUSTOS");
            IntPtr data = CoreFoundation.Data(secret);
            IntPtr item = CoreFoundation.Dictionary(
                (Constant("kSecClass"), Constant("kSecClassGenericPassword")),
                (Constant("kSecAttrService"), serviceText),
                (Constant("kSecAttrAccount"), accountText),
                (Constant("kSecAttrLabel"), label),
                (Constant("kSecValueData"), data));
            try
            {
                int status;
                unsafe
                {
                    status = SecItemAdd(item, null);
                }

                if (status != 0)
                {
                    throw new CryptographicException($"SecItemAdd: {status}");
                }
            }
            finally
            {
                foreach (IntPtr value in new[] { item, serviceText, accountText, label, data })
                {
                    CoreFoundation.Release(value);
                }
            }
        }

        public static void Delete(string service, string account)
        {
            IntPtr serviceText = CoreFoundation.String(service);
            IntPtr accountText = CoreFoundation.String(account);
            IntPtr query = CoreFoundation.Dictionary(
                (Constant("kSecClass"), Constant("kSecClassGenericPassword")),
                (Constant("kSecAttrService"), serviceText),
                (Constant("kSecAttrAccount"), accountText));
            try
            {
                _ = SecItemDelete(query);
            }
            finally
            {
                CoreFoundation.Release(query);
                CoreFoundation.Release(serviceText);
                CoreFoundation.Release(accountText);
            }
        }

        private static IntPtr Constant(string name) => CoreFoundation.Constant(Security, name);

        [LibraryImport(Security)]
        private static unsafe partial int SecItemCopyMatching(IntPtr query, IntPtr* result);

        [LibraryImport(Security)]
        private static unsafe partial int SecItemAdd(IntPtr attributes, IntPtr* result);

        [LibraryImport(Security)]
        private static partial int SecItemDelete(IntPtr query);
    }
}
