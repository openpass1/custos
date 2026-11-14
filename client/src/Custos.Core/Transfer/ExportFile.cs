using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core.Cloud;
using Custos.Core.Model;

namespace Custos.Core.Transfer;

/// <summary>
/// A file of exported tasks (section 9a of the client spec): the tasks as in <c>tasks.json</c>, the accounts they use
/// without their secrets, and, only when the person asked for it, the secrets encrypted with an export password. It is
/// meant for moving tasks to a reinstalled or another computer, so it is plain JSON a person can read and edit.
/// </summary>
public sealed class ExportFile
{
    /// <summary><see cref="TaskExport.FormatName"/> in a file of CUSTOS; empty in anything else.</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary><see cref="TaskExport.FormatVersion"/>; grows when the file changes in a way an older CUSTOS cannot read.</summary>
    public int Version { get; set; }

    public DateTimeOffset? ExportedAt { get; set; }

    /// <summary>The computer the tasks came from; only shown to the person.</summary>
    public string? Computer { get; set; }

    /// <summary>The version of CUSTOS that wrote the file; only shown to the person.</summary>
    public string? ProgramVersion { get; set; }

    public List<TaskDefinition> Tasks { get; set; } = [];

    /// <summary>
    /// The accounts the tasks refer to, without secrets. <see cref="Credential.HasSecret"/> says whether the account had
    /// one where it was exported; whether that secret is in the file depends on <see cref="Secrets"/>.
    /// </summary>
    public List<Credential> Credentials { get; set; } = [];

    /// <summary>The secrets, encrypted with the export password; null when the file carries none.</summary>
    public ExportSecretsBox? Secrets { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// The encrypted part of an <see cref="ExportFile"/>: <see cref="ExportSecrets"/> as JSON, encrypted with AES-256-GCM
/// under a key derived from the export password with PBKDF2-HMAC-SHA256.
/// </summary>
public sealed class ExportSecretsBox
{
    /// <summary><see cref="TaskExport.KdfName"/>.</summary>
    public string Kdf { get; set; } = string.Empty;

    public int Iterations { get; set; }

    /// <summary>Base64.</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary><see cref="TaskExport.CipherName"/>.</summary>
    public string Cipher { get; set; } = string.Empty;

    /// <summary>Base64, 12 bytes.</summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>Base64 of the ciphertext followed by the 16-byte tag.</summary>
    public string Data { get; set; } = string.Empty;
}

/// <summary>What the encrypted part holds.</summary>
public sealed class ExportSecrets
{
    /// <summary>Account id → its secret: a password, the private SSH key (base64 of its seed) or the refresh token of a
    /// cloud drive, exactly as the service keeps it under DPAPI.</summary>
    public Dictionary<string, string> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The administrator's own applications at the cloud drives that issued exported tokens: a token works only with the
    /// application it was given to. Null when none are needed.
    /// </summary>
    public CloudAppsFile? CloudApps { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A file that is not an export of CUSTOS, is damaged, or cannot be opened with the password given.</summary>
public sealed class ExportException : Exception
{
    public ExportException()
    {
    }

    public ExportException(string message)
        : base(message)
    {
    }

    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The export password is wrong (or the encrypted part was changed).</summary>
    public bool IsWrongPassword { get; init; }
}
