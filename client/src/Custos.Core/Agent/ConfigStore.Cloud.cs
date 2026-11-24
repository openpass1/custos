using Custos.Core.Cloud;
using Custos.Core.Json;
using Custos.Core.Model;

namespace Custos.Core.Agent;

/// <summary>Accounts of cloud drives and the applications they sign in with (section 6.3, «Облачные диски»).</summary>
public sealed partial class ConfigStore
{
    /// <summary>The administrator's own applications at the services: local to this computer, never sent anywhere.</summary>
    public const string CloudAppsFileName = "cloud-apps.json";

    /// <summary>The own applications; empty when none are set.</summary>
    public CloudAppsFile GetCloudApps()
    {
        lock (_lock)
        {
            return Read(CloudAppsFileName, ConfigJsonContext.Default.CloudAppsFile) ?? new CloudAppsFile();
        }
    }

    /// <summary>Stores the own applications. Accounts connected before keep the application that issued their token.</summary>
    public void SaveCloudApps(CloudAppsFile apps, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(apps);
        if (kind is not null && !DestinationKinds.IsCloudDrive(kind))
        {
            throw new ConfigException(Messages.Format(Messages.DestinationKindNotSupported, kind));
        }

        lock (_lock)
        {
            var clean = kind is null ? new CloudAppsFile() : GetCloudApps();
            if (kind is null or DestinationKinds.GDrive)
            {
                clean.Gdrive = Clean(apps.Gdrive);
            }

            if (kind is null or DestinationKinds.YaDisk)
            {
                clean.Yadisk = Clean(apps.Yadisk);
            }

            if (kind is null or DestinationKinds.Dropbox)
            {
                clean.Dropbox = Clean(apps.Dropbox);
            }

            WriteJson(CloudAppsFileName, clean, ConfigJsonContext.Default.CloudAppsFile);
        }

        static CloudApp? Clean(CloudApp? app) => app is null || string.IsNullOrWhiteSpace(app.ClientId) ? null
            : new CloudApp { ClientId = app.ClientId.Trim(), ClientSecret = string.IsNullOrWhiteSpace(app.ClientSecret) ? null : app.ClientSecret.Trim() };
    }

    /// <summary>
    /// Stores an account connected through the browser: a new one, or <paramref name="credentialId"/> with its token
    /// replaced (connected again after the access was revoked; the tasks that use it keep working).
    /// </summary>
    /// <param name="refreshToken">The refresh token, kept under DPAPI like a password.</param>
    /// <param name="clientId">The application that issued it.</param>
    /// <exception cref="ConfigException">No such account, or one of another kind.</exception>
    public Credential SaveCloudAccount(string? credentialId, string kind, string user, string refreshToken, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        if (!CredentialKinds.IsCloud(kind))
        {
            throw new ConfigException(Messages.Format(Messages.DestinationKindNotSupported, kind));
        }

        lock (_lock)
        {
            var list = new List<StoredCredential>(_credentials.Credentials);
            var existing = credentialId is null ? null : FindCredential(credentialId) ?? throw new ConfigException(Messages.CredentialNotFound) { IsNotFound = true };
            if (existing is not null && existing.Credential.Kind != kind)
            {
                throw new ConfigException(Messages.Format(Messages.CloudWrongAccount, existing.Credential.DisplayName));
            }

            var credential = existing is null ? new Credential { Kind = kind } : CopyCredential(existing.Credential);
            credential.User = string.IsNullOrWhiteSpace(user) ? CloudService.NameOf(kind) : user.Trim();
            credential.Domain = null;
            credential.ClientId = clientId;
            var updated = Protect(credential, refreshToken);
            int index = existing is null ? -1 : list.IndexOf(existing);
            if (index >= 0)
            {
                list[index] = updated;
            }
            else
            {
                list.Add(updated);
            }

            WriteCredentials(list);
            return CopyCredential(updated.Credential);
        }
    }

    /// <summary>Stores the refresh token the service gave out in place of the old one (Yandex does so on every refresh).</summary>
    public void UpdateCloudToken(string credentialId, string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        lock (_lock)
        {
            var existing = FindCredential(credentialId);
            if (existing is null || !CredentialKinds.IsCloud(existing.Credential.Kind))
            {
                return;
            }

            var list = new List<StoredCredential>(_credentials.Credentials);
            list[list.IndexOf(existing)] = Protect(CopyCredential(existing.Credential), refreshToken);

            // Only the secret changed: nothing the server sees.
            WriteCredentials(list, bump: false);
        }
    }
}
