using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custos.Core.Model;

/// <summary>
/// Account details a task refers to by id (A.9). The secret itself never leaves the agent: in
/// <c>credentials.json</c> it is stored DPAPI-protected (<see cref="StoredCredential"/>), everywhere else only
/// <see cref="HasSecret"/> says whether there is one.
/// </summary>
public sealed class Credential
{
    public string Id { get; set; } = TaskDefinition.NewId();

    /// <summary>One of <see cref="CredentialKinds"/>.</summary>
    public string Kind { get; set; } = CredentialKinds.Windows;

    public string User { get; set; } = string.Empty;

    /// <summary>Windows domain, or "." for a local account of the target machine.</summary>
    public string? Domain { get; set; }

    public bool HasSecret { get; set; }

    /// <summary>For <see cref="CredentialKinds.SshKey"/>: the public key, an authorized_keys line ("ssh-ed25519 AAAA… custos").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKey { get; set; }

    /// <summary>For a cloud drive: the client id of the application that issued the token, which only works with it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>DOMAIN\user, or just user.</summary>
    public string DisplayName => string.IsNullOrEmpty(Domain) ? User : Domain + "\\" + User;
}

/// <summary>A credential as kept in <c>credentials.json</c>: with its secret protected by DPAPI (SYSTEM).</summary>
public sealed class StoredCredential
{
    public required Credential Credential { get; set; }

    /// <summary>Base64 of the DPAPI blob; null when there is no secret.</summary>
    public string? SecretDpapi { get; set; }
}

/// <summary>Agent settings (A.9).</summary>
public sealed class AgentSettings
{
    public string Language { get; set; } = "auto";

    public string Theme { get; set; } = "auto";

    public string TrayMode { get; set; } = "full";

    public bool AllowAllUsersEdit { get; set; }

    public string? ProgramPasswordHash { get; set; }

    /// <summary>Days the log files are kept: the journal of each day, the log of each run, the service log.</summary>
    public int LogRetentionDays { get; set; } = CustosInfo.DefaultLogRetentionDays;

    /// <summary>Days the run history is kept.</summary>
    public int HistoryRetentionDays { get; set; } = CustosInfo.DefaultHistoryRetentionDays;

    /// <summary><see cref="LogLevels.Summary"/> or <see cref="LogLevels.Files"/>.</summary>
    public string DefaultLogLevel { get; set; } = LogLevels.Summary;

    /// <summary>"low" or "normal".</summary>
    public string ProcessPriority { get; set; } = "low";

    public bool AllowRemoteEdit { get; set; } = true;

    public string? ServerUrl { get; set; }

    public int HeartbeatIntervalS { get; set; } = 60;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
