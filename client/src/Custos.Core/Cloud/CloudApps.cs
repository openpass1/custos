using System.Reflection;
using System.Text.Json.Serialization;
using Custos.Core.Model;

namespace Custos.Core.Cloud;

/// <summary>
/// The application CUSTOS signs in as at a cloud drive (section 6.3, «Приложения CUSTOS у сервисов»): its client id
/// and, where the service wants one, its client secret. A desktop program cannot keep a secret; PKCE protects the
/// sign-in instead.
/// </summary>
public sealed class CloudApp
{
    public string ClientId { get; set; } = string.Empty;

    public string? ClientSecret { get; set; }

    [JsonIgnore]
    public bool IsSet => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// <c>config\cloud-apps.json</c>: the applications an administrator registered at the services himself (settings,
/// «Облачные диски»). Local to this computer: never in the configuration snapshot, never changed by the server.
/// </summary>
public sealed class CloudAppsFile
{
    public CloudApp? Gdrive { get; set; }

    public CloudApp? Yadisk { get; set; }

    public CloudApp? Dropbox { get; set; }

    /// <summary>The own application of a service; null when none is set.</summary>
    public CloudApp? Of(string kind) => (kind switch
    {
        DestinationKinds.GDrive => Gdrive,
        DestinationKinds.YaDisk => Yadisk,
        DestinationKinds.Dropbox => Dropbox,
        _ => null,
    }) is { IsSet: true } app ? app : null;
}

/// <summary>Where the application of a service comes from: the one built into this version, or the administrator's own.</summary>
public static class CloudApps
{
    /// <summary>
    /// The application the build put in (GitHub Actions variables, see <c>client/README.md</c>); null for a build
    /// without one. The ids are never in the repository.
    /// </summary>
    public static CloudApp? BuiltIn(string kind)
    {
        string prefix = kind switch
        {
            DestinationKinds.GDrive => "CustosGDrive",
            DestinationKinds.YaDisk => "CustosYaDisk",
            DestinationKinds.Dropbox => "CustosDropbox",
            _ => string.Empty,
        };
        if (prefix.Length == 0)
        {
            return null;
        }

        var metadata = typeof(CloudApps).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Value(string key) => metadata.FirstOrDefault(a => a.Key == prefix + key)?.Value is { Length: > 0 } value ? value.Trim() : null;
        string? id = Value("ClientId");
        return id is null ? null : new CloudApp { ClientId = id, ClientSecret = Value("ClientSecret") };
    }

    /// <summary>The application to connect a new account with: the administrator's own if set, else the built-in one.</summary>
    public static CloudApp? ForConnect(string kind, CloudAppsFile own) => own?.Of(kind) ?? BuiltIn(kind);

    /// <summary>
    /// The application that issued a token: a refresh token only works with the client id it was given to. Its
    /// secret is the one known for that id here; null when this computer knows no such application any more.
    /// </summary>
    public static CloudApp? ForToken(string kind, string? clientId, CloudAppsFile own)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return ForConnect(kind, own);
        }

        foreach (var app in new[] { own?.Of(kind), BuiltIn(kind) })
        {
            if (app is not null && string.Equals(app.ClientId, clientId, StringComparison.Ordinal))
            {
                return app;
            }
        }

        // Dropbox and Yandex need no secret with PKCE: the id alone still refreshes.
        return kind == DestinationKinds.GDrive ? null : new CloudApp { ClientId = clientId };
    }
}
