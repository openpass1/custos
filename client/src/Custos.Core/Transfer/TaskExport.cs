using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custos.Core.Cloud;
using Custos.Core.Json;
using Custos.Core.Model;

namespace Custos.Core.Transfer;

/// <summary>
/// Writes and reads files of exported tasks (<see cref="ExportFile"/>, section 9a of the client spec). Secrets go into a
/// file only when an export password is given: PBKDF2-HMAC-SHA256 makes the key, AES-256-GCM encrypts them (section 11).
/// </summary>
public static class TaskExport
{
    public const string FormatName = "custos-tasks";
    public const int FormatVersion = 1;
    public const string KdfName = "pbkdf2-sha256";
    public const string CipherName = "aes-256-gcm";

    /// <summary>The export password must be at least this long.</summary>
    public const int MinPasswordLength = 8;

    /// <summary>PBKDF2 rounds for a new file (OWASP 2023 for HMAC-SHA256): a fraction of a second once, years for a guesser.</summary>
    public const int Iterations = 600_000;

    /// <summary>A file asking for fewer rounds was not made by CUSTOS; more than this would take minutes to open.</summary>
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>Binds the encrypted part to this format: it cannot be passed off as anything else.</summary>
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(FormatName + "/" + FormatVersion);

    /// <summary>
    /// Makes the file for <paramref name="tasks"/>, with the accounts they refer to. With a password, the secrets of those
    /// accounts and the own cloud applications their tokens need go in encrypted.
    /// </summary>
    /// <param name="credentials">The accounts stored on this computer (without secrets); those the tasks use go in, and
    /// with <paramref name="allAccounts"/> also every other account of the settings.</param>
    /// <param name="secretOf">The secret of an account by id, null when it has none; only asked when a password is given.</param>
    /// <param name="ownApps">The administrator's own applications at the cloud drives.</param>
    /// <param name="password">The export password; null = no secrets in the file.</param>
    /// <param name="allAccounts">Also the accounts no exported task uses (<see cref="IsListed"/>): moving a whole computer
    /// takes the accounts that wait for a task, too.</param>
    /// <exception cref="ExportException">The password is too short.</exception>
    public static ExportFile Create(
        IReadOnlyList<TaskDefinition> tasks,
        IReadOnlyList<Credential> credentials,
        Func<string, string?>? secretOf,
        CloudAppsFile? ownApps,
        string? password,
        string computer,
        DateTimeOffset now,
        bool allAccounts = false)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(credentials);
        if (password is not null && password.Length < MinPasswordLength)
        {
            throw new ExportException(Messages.Format(Messages.ExportPasswordTooShort, MinPasswordLength));
        }

        var used = UsedCredentialIds(tasks);
        var file = new ExportFile
        {
            Format = FormatName,
            Version = FormatVersion,
            ExportedAt = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero),
            Computer = computer,
            ProgramVersion = CustosInfo.Version,
            Tasks = [.. tasks.Select(t => t.Clone())],
            Credentials = [.. credentials.Where(c => used.Contains(c.Id) || (allAccounts && IsListed(c.Kind))).Select(Copy)],
        };

        if (password is null)
        {
            return file;
        }

        var secrets = new ExportSecrets();
        foreach (var credential in file.Credentials)
        {
            if (secretOf?.Invoke(credential.Id) is { Length: > 0 } secret)
            {
                secrets.Credentials[credential.Id] = secret;
            }
        }

        secrets.CloudApps = AppsFor(file.Credentials.Where(c => secrets.Credentials.ContainsKey(c.Id)), ownApps);
        file.Secrets = Seal(secrets, password);
        return file;
    }

    /// <summary>The file as written to disk: indented JSON in UTF-8 without BOM.</summary>
    public static byte[] Serialize(ExportFile file) => JsonSerializer.SerializeToUtf8Bytes(file, ConfigJsonContext.Default.ExportFile);

    /// <summary>Reads a file and checks that it is an export of CUSTOS this version understands.</summary>
    /// <exception cref="ExportException">Not such a file, made by a newer CUSTOS, or damaged.</exception>
    public static ExportFile Read(ReadOnlySpan<byte> json)
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ExportFile);
        }
        catch (JsonException ex)
        {
            throw new ExportException(Messages.ExportNotAFile, ex);
        }

        if (file is null || !string.Equals(file.Format, FormatName, StringComparison.Ordinal) || file.Version < 1)
        {
            throw new ExportException(Messages.ExportNotAFile);
        }

        if (file.Version > FormatVersion)
        {
            throw new ExportException(Messages.Format(Messages.ExportNewerVersion, file.Version));
        }

        file.Tasks ??= [];
        file.Credentials ??= [];
        if (file.Tasks.Any(t => t is null) || file.Credentials.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id)))
        {
            throw new ExportException(Messages.Format(Messages.ExportDamaged, "null"));
        }

        var known = file.Credentials.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var task in file.Tasks)
        {
            if (UsedCredentialIds([task]).FirstOrDefault(id => !known.Contains(id)) is not null)
            {
                throw new ExportException(Messages.Format(Messages.ExportDamaged, Messages.Format(Messages.ExportUnknownAccount, task.Name)));
            }
        }

        return file;
    }

    /// <summary>Decrypts the secrets of a file.</summary>
    /// <exception cref="ExportException">The file has no secrets, they are damaged, or the password is wrong
    /// (<see cref="ExportException.IsWrongPassword"/>).</exception>
    public static ExportSecrets Open(ExportFile file, string password)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(password);
        var box = file.Secrets ?? throw new ExportException(Messages.ExportNoSecrets);
        byte[] salt, nonce, data;
        try
        {
            salt = Convert.FromBase64String(box.Salt ?? string.Empty);
            nonce = Convert.FromBase64String(box.Nonce ?? string.Empty);
            data = Convert.FromBase64String(box.Data ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new ExportException(Messages.Format(Messages.ExportDamaged, ex.Message), ex);
        }

        if (!string.Equals(box.Kdf, KdfName, StringComparison.Ordinal) || !string.Equals(box.Cipher, CipherName, StringComparison.Ordinal)
            || box.Iterations is < MinIterations or > MaxIterations || salt.Length < SaltBytes || nonce.Length != NonceBytes || data.Length < TagBytes)
        {
            throw new ExportException(Messages.Format(Messages.ExportDamaged, box.Kdf + "/" + box.Cipher));
        }

        byte[] key = DeriveKey(password, salt, box.Iterations);
        byte[] plain = new byte[data.Length - TagBytes];
        try
        {
            using (var gcm = NewAesGcm(key))
            {
                gcm.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, AssociatedData);
            }

            var secrets = JsonSerializer.Deserialize(plain, ConfigJsonContext.Default.ExportSecrets)
                ?? throw new ExportException(Messages.Format(Messages.ExportDamaged, "null"));
            secrets.Credentials = new Dictionary<string, string>(secrets.Credentials ?? [], StringComparer.OrdinalIgnoreCase);
            return secrets;
        }
        catch (CryptographicException ex)
        {
            throw new ExportException(Messages.ExportWrongPassword, ex) { IsWrongPassword = true };
        }
        catch (JsonException ex)
        {
            throw new ExportException(Messages.Format(Messages.ExportDamaged, ex.Message), ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>An account of the settings list, which lives on its own: not an SSH key or an archive password, which
    /// belong to their task.</summary>
    public static bool IsListed(string? kind) => kind is not (CredentialKinds.SshKey or CredentialKinds.ArchivePassword);

    /// <summary>Ids of the accounts the tasks refer to: network sources, SSH keys, database servers, destinations and the
    /// archive password.</summary>
    public static HashSet<string> UsedCredentialIds(IEnumerable<TaskDefinition> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            ids.UnionWith(task.Sources.Select(s => s.CredentialId).OfType<string>());
            ids.UnionWith(task.Destinations.Select(d => d.CredentialId).OfType<string>());
            ids.UnionWith(task.DbServers.Select(d => d.CredentialId).OfType<string>());
            if (task.Archive.PasswordCredentialId is { } archive)
            {
                ids.Add(archive);
            }
        }

        ids.Remove(string.Empty);
        return ids;
    }

    /// <summary>The own applications that issued the tokens of these cloud accounts; null when none is needed.</summary>
    private static CloudAppsFile? AppsFor(IEnumerable<Credential> accounts, CloudAppsFile? own)
    {
        if (own is null)
        {
            return null;
        }

        var result = new CloudAppsFile();
        bool any = false;
        foreach (var account in accounts.Where(a => CredentialKinds.IsCloud(a.Kind) && a.ClientId is not null))
        {
            if (own.Of(account.Kind) is { } app && string.Equals(app.ClientId, account.ClientId, StringComparison.Ordinal))
            {
                var copy = new CloudApp { ClientId = app.ClientId, ClientSecret = app.ClientSecret };
                switch (account.Kind)
                {
                    case CredentialKinds.GDrive:
                        result.Gdrive = copy;
                        break;
                    case CredentialKinds.YaDisk:
                        result.Yadisk = copy;
                        break;
                    default:
                        result.Dropbox = copy;
                        break;
                }

                any = true;
            }
        }

        return any ? result : null;
    }

    private static ExportSecretsBox Seal(ExportSecrets secrets, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] key = DeriveKey(password, salt, Iterations);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(secrets, ConfigJsonContext.Default.ExportSecrets);
        byte[] data = new byte[plain.Length + TagBytes];
        try
        {
            using var gcm = NewAesGcm(key);
            gcm.Encrypt(nonce, plain, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), AssociatedData);
            return new ExportSecretsBox
            {
                Kdf = KdfName,
                Iterations = Iterations,
                Cipher = CipherName,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Data = Convert.ToBase64String(data),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Credential Copy(Credential credential) => new()
    {
        Id = credential.Id,
        Kind = credential.Kind,
        User = credential.User,
        Domain = credential.Domain,
        HasSecret = credential.HasSecret,
        PublicKey = credential.PublicKey,
        ClientId = credential.ClientId,
        ExtensionData = credential.ExtensionData is null ? null : new Dictionary<string, JsonElement>(credential.ExtensionData),
    };

    /// <summary>AES-GCM with a 16-byte tag. .NET 6 (the legacy edition) has only the constructor without the tag size.</summary>
    private static AesGcm NewAesGcm(byte[] key) =>
#if CUSTOS_LEGACY
        new(key);
#else
        new(key, TagBytes);
#endif
}
