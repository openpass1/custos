using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Custos.Core.Monitor;

/// <summary>
/// Signatures, fingerprints, the registration code and the secrets of the monitoring protocol (appendix A.2, A.3).
/// The server side of the same rules is server/src/Infra/Crypto.php; server/tools/src/AgentClient.php is the reference
/// agent this code is checked against.
/// </summary>
public static class MonitorCrypto
{
    /// <summary>RSA key size of the agent and of the server (A.2).</summary>
    public const int KeyBits = 3072;

    private const int WrappedKeyBytes = 384;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>A new agent key.</summary>
    public static RSA NewKey()
    {
        var rsa = RSA.Create();
        rsa.KeySize = KeyBits;
        // Make it now, not lazily on first use.
        rsa.ExportParameters(includePrivateParameters: false);
        return rsa;
    }

    /// <summary>The public key as PEM (SubjectPublicKeyInfo), as the server stores it.</summary>
    public static string PublicPem(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return PemEncoding.WriteString("PUBLIC KEY", key.ExportSubjectPublicKeyInfo()) + "\n";
    }

    /// <summary>Loads a public key in PEM; null when it is not an RSA public key.</summary>
    public static RSA? LoadPublicPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            return null;
        }
    }

    /// <summary>
    /// First 16 hex characters of SHA-256 of the DER of the public key, in groups of four: <c>3F2A-91C0-77DE-0B14</c>
    /// (A.2). Shown in the program and on the site so that a person can compare them.
    /// </summary>
    public static string Fingerprint(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string hex = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()), 0, 8);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>The fingerprint of a PEM public key, or null when it cannot be read.</summary>
    public static string? Fingerprint(string? publicPem)
    {
        using var key = LoadPublicPem(publicPem);
        return key is null ? null : Fingerprint(key);
    }

    /// <summary>A fingerprint as typed by a person: without dashes and spaces, in upper case.</summary>
    public static string NormalizeFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return new string([.. fingerprint.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)]);
    }

    /// <summary>Two PEM public keys are the same key.</summary>
    public static bool SameKey(string? a, string? b)
    {
        using var ka = LoadPublicPem(a);
        using var kb = LoadPublicPem(b);
        return ka is not null && kb is not null && ka.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(kb.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The string an agent request is signed over: <c>"{METHOD}\n{PATH}\n{TIME}\n{NONCE}\n{sha256hex(body)}"</c> (A.2).</summary>
    public static string RequestString(string method, string path, string time, string nonce, ReadOnlySpan<byte> body) =>
        $"{method}\n{path}\n{time}\n{nonce}\n{Sha256Hex(body)}";

    /// <summary>The string a server answer is signed over: <c>"{STATUS}\n{TIME}\n{NONCE}\n{sha256hex(body)}"</c> (A.2).</summary>
    public static string ResponseString(int status, string time, string nonce, ReadOnlySpan<byte> body) =>
        string.Create(CultureInfo.InvariantCulture, $"{status:000}\n{time}\n{nonce}\n{Sha256Hex(body)}");

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256, in base64.</summary>
    public static string Sign(RSA key, string data)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public static bool Verify(RSA? key, string data, string? signatureBase64)
    {
        if (key is null || string.IsNullOrEmpty(signatureBase64))
        {
            return false;
        }

        try
        {
            byte[] signature = Convert.FromBase64String(signatureBase64);
            return signature.Length > 0 && key.VerifyData(Encoding.UTF8.GetBytes(data), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>HMAC-SHA256 in base64, keyed by the ASCII bytes of the normalized registration key (A.3).</summary>
    public static string Hmac(string registrationKey, string data) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(registrationKey), Encoding.UTF8.GetBytes(data)));

    public static bool HmacValid(string registrationKey, string data, string? macBase64)
    {
        if (string.IsNullOrEmpty(macBase64))
        {
            return false;
        }

        byte[] expected = Encoding.ASCII.GetBytes(Hmac(registrationKey, data));
        byte[] actual = Encoding.ASCII.GetBytes(macBase64);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>32 random hex characters for <c>X-Custos-Nonce</c>.</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Decrypts <c>secret_enc</c> (A.2): base64( RSA-OAEP(SHA-1) wrapped AES-256 key, 384 bytes ‖ nonce, 12 ‖
    /// ciphertext ‖ tag, 16 ), the data in AES-256-GCM.
    /// </summary>
    /// <exception cref="CryptographicException">It is not a secret for this key, or it was changed on the way.</exception>
    public static string DecryptSecret(RSA agentKey, string secretEnc)
    {
        ArgumentNullException.ThrowIfNull(agentKey);
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(secretEnc ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("secret_enc is not base64.", ex);
        }

        if (raw.Length < WrappedKeyBytes + NonceBytes + TagBytes)
        {
            throw new CryptographicException("secret_enc is too short.");
        }

        byte[] aesKey = agentKey.Decrypt(raw.AsSpan(0, WrappedKeyBytes).ToArray(), RSAEncryptionPadding.OaepSHA1);
        try
        {
            var nonce = raw.AsSpan(WrappedKeyBytes, NonceBytes);
            var cipher = raw.AsSpan(WrappedKeyBytes + NonceBytes, raw.Length - WrappedKeyBytes - NonceBytes - TagBytes);
            var tag = raw.AsSpan(raw.Length - TagBytes);
            byte[] plain = new byte[cipher.Length];
            using var gcm = new AesGcm(aesKey, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    /// <summary>The other way round, for tests and the fake server: what the server does in Crypto::encryptSecret.</summary>
    public static string EncryptSecret(RSA agentPublicKey, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(agentPublicKey);
        byte[] aesKey = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagBytes];
        using (var gcm = new AesGcm(aesKey, TagBytes))
        {
            gcm.Encrypt(nonce, plain, cipher, tag);
        }

        byte[] wrapped = agentPublicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA1);
        return Convert.ToBase64String([.. wrapped, .. nonce, .. cipher, .. tag]);
    }
}

/// <summary>
/// The registration code an administrator types (A.3): the registration key and, optionally, after <c>#</c>, the
/// fingerprint of the server key: <c>K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14</c>.
/// </summary>
public sealed record RegistrationCode(string Key, string? Fingerprint)
{
    /// <summary>Length of the registration key without dashes (25 Base32 characters, about 125 bits).</summary>
    public const int KeyLength = 25;

    /// <summary>
    /// The connection string the site copies with one button (A.3): the address of the site, <c>#</c>, then the code,
    /// <c>http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14</c>. When the text starts with
    /// http:// or https://, everything before the first <c>#</c> is the address and the rest is the code.
    /// </summary>
    /// <returns>False when the text is not a connection string (a bare code or a bare address).</returns>
    public static bool TrySplitConnection(string? text, out string url, out string code)
    {
        url = string.Empty;
        code = string.Empty;
        string value = (text ?? string.Empty).Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return false;
        }

        url = value[..hash].Trim().TrimEnd('/');
        code = value[(hash + 1)..].Trim();
        return true;
    }

    /// <summary>Reads a code as typed or pasted; null when it cannot be one (the reason in <paramref name="problem"/>).</summary>
    public static RegistrationCode? TryParse(string? text, out string? problem)
    {
        problem = null;
        string code = (text ?? string.Empty).Trim();
        int hash = code.IndexOf('#', StringComparison.Ordinal);
        string keyPart = hash < 0 ? code : code[..hash];
        string? fingerprint = hash < 0 ? null : code[(hash + 1)..].Trim();
        string key = new([.. keyPart.Where(c => c is not ('-' or ' ' or '\t')).Select(char.ToUpperInvariant)]);
        if (key.Length != KeyLength || key.Any(c => c is not ((>= 'A' and <= 'Z') or (>= '2' and <= '7'))))
        {
            problem = Messages.Format(Messages.MonitorCodeBad, KeyLength);
            return null;
        }

        if (string.IsNullOrEmpty(fingerprint))
        {
            return new RegistrationCode(key, null);
        }

        string normalized = MonitorCrypto.NormalizeFingerprint(fingerprint);
        if (normalized.Length != 16 || !normalized.All(Uri.IsHexDigit))
        {
            problem = Messages.MonitorCodeBadFingerprint;
            return null;
        }

        return new RegistrationCode(key, normalized);
    }

    /// <summary>The code as the site shows it: the key in groups of five, then the fingerprint.</summary>
    public string Display()
    {
        string key = string.Join('-', Enumerable.Range(0, Key.Length / 5).Select(i => Key.Substring(i * 5, 5)));
        return Fingerprint is null ? key : key + "#" + string.Join('-', Enumerable.Range(0, 4).Select(i => Fingerprint.Substring(i * 4, 4)));
    }
}
