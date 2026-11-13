using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custos.Core.Destinations;

namespace Custos.Core.Cloud;

/// <summary>Tokens the service gave out: the access token and, with the first sign-in, the refresh token.</summary>
/// <param name="RefreshToken">Null when the service did not give a new one (a refresh at Google and Dropbox).</param>
public sealed record CloudGrant(string AccessToken, string? RefreshToken, DateTime ExpiresUtc);

/// <summary>The access tokens of a cloud account, as the stores need them.</summary>
public interface ICloudTokens
{
    /// <summary>A valid access token.</summary>
    /// <param name="renew">The service refused the last one: get a new one even if it should still be valid.</param>
    /// <exception cref="CloudAccessRevokedException">The account no longer lets CUSTOS in.</exception>
    /// <exception cref="StoreException">The service could not be asked.</exception>
    string AccessToken(bool renew, CancellationToken cancellationToken);
}

/// <summary>An account of a cloud drive (section 6.3): its name for people and its tokens.</summary>
public sealed class CloudAccount(string user, ICloudTokens tokens)
{
    /// <summary>The address or login of the account, only for showing.</summary>
    public string User { get; } = user ?? string.Empty;

    public ICloudTokens Tokens { get; } = tokens ?? throw new ArgumentNullException(nameof(tokens));

    /// <summary>Tests: the HTTP handler that plays the service.</summary>
    internal HttpMessageHandler? Handler { get; init; }

    /// <summary>The first pause before a request is tried again; it doubles with each attempt.</summary>
    internal TimeSpan RetryUnit { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>The account no longer lets CUSTOS in: the access was revoked, the password changed or the token expired.</summary>
public sealed class CloudAccessRevokedException : StoreException
{
    public CloudAccessRevokedException()
    {
    }

    public CloudAccessRevokedException(string message)
        : base(message)
    {
    }

    public CloudAccessRevokedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A sign-in in progress: the browser was sent to the service and the code is awaited (OAuth 2.0 with PKCE).</summary>
public sealed class CloudAuthorization
{
    private readonly string _verifier;

    private CloudAuthorization(CloudService service, CloudApp app, string? redirectUri)
    {
        Service = service;
        App = app;
        RedirectUri = redirectUri;
        _verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        State = Base64Url(RandomNumberGenerator.GetBytes(24));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", app.ClientId),
            new("state", State),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        if (!string.IsNullOrEmpty(redirectUri))
        {
            query.Add(new("redirect_uri", redirectUri));
        }

        if (service.Scope is { } scope)
        {
            query.Add(new("scope", scope));
        }

        query.AddRange(service.Extra);
        Url = service.AuthorizeUri + "?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    public CloudService Service { get; }

    public CloudApp App { get; }

    /// <summary>Where the code comes back; null or empty when the service shows it on its page.</summary>
    public string? RedirectUri { get; }

    /// <summary>Ties the answer to this sign-in.</summary>
    public string State { get; }

    /// <summary>The page to open in the browser.</summary>
    public string Url { get; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Starts a sign-in.</summary>
    /// <param name="redirectUri">The loopback address this computer listens on; null = the code is shown on the
    /// service's page (<see cref="CloudService.CodeOnScreen"/>).</param>
    public static CloudAuthorization Begin(CloudService service, CloudApp app, string? redirectUri)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(app);
        return new CloudAuthorization(service, app, redirectUri ?? service.CodeOnScreen);
    }

    /// <summary>Trades the code of the answer for tokens.</summary>
    /// <exception cref="StoreException">The service refused the code or could not be asked.</exception>
    public CloudGrant Finish(string code, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code.Trim()),
            new("client_id", App.ClientId),
            new("code_verifier", _verifier),
        };
        if (!string.IsNullOrEmpty(RedirectUri))
        {
            form.Add(new("redirect_uri", RedirectUri));
        }

        // A secret goes along whenever the application has one: Google wants it, the others take it with PKCE too.
        if (!string.IsNullOrEmpty(App.ClientSecret))
        {
            form.Add(new("client_secret", App.ClientSecret));
        }

        var grant = OAuthClient.Post(Service, form, handler, refreshing: false, cancellationToken);
        return grant.RefreshToken is null
            ? throw new StoreException(Messages.Format(Messages.CloudNoRefreshToken, Service.Name))
            : grant;
    }

    /// <summary>
    /// The code out of what came back: the query of the loopback request, or what the person pasted (the code
    /// itself, or the whole address from the browser).
    /// </summary>
    /// <exception cref="StoreException">The service said no (the person refused, or the state does not match).</exception>
    public string? ReadCode(string answer)
    {
        string text = (answer ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        int query = text.IndexOf('?', StringComparison.Ordinal);
        if (query < 0 && !text.Contains("code=", StringComparison.Ordinal) && !text.Contains("error=", StringComparison.Ordinal))
        {
            // The code itself, copied from the page of the service.
            return text;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in text[(query + 1)..].Split('#')[0].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            values[key] = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        if (values.TryGetValue("error", out string? error))
        {
            string detail = values.TryGetValue("error_description", out string? description) ? description : error;
            throw new StoreException(error == "access_denied"
                ? Messages.Format(Messages.CloudSignInRefused, Service.Name)
                : Messages.Format(Messages.CloudSignInFailed, Service.Name, detail));
        }

        if (values.TryGetValue("state", out string? state) && !string.Equals(state, State, StringComparison.Ordinal))
        {
            throw new StoreException(Messages.Format(Messages.CloudSignInFailed, Service.Name, "state"));
        }

        return values.TryGetValue("code", out string? code) && code.Length > 0 ? code : null;
    }

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The token endpoint of a service.</summary>
internal static class OAuthClient
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static CloudGrant Refresh(CloudService service, CloudApp app, string refreshToken, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", app.ClientId),
        };
        if (!string.IsNullOrEmpty(app.ClientSecret))
        {
            form.Add(new("client_secret", app.ClientSecret));
        }

        return Post(service, form, handler, refreshing: true, cancellationToken);
    }

    public static CloudGrant Post(CloudService service, List<KeyValuePair<string, string>> form, HttpMessageHandler? handler, bool refreshing, CancellationToken cancellationToken)
    {
        using var own = handler is null ? null : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        var http = own ?? Shared;
        string host = service.TokenUri.Host;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, service.TokenUri) { Content = new FormUrlEncodedContent(form) };
            request.Headers.UserAgent.ParseAdd("CUSTOS/" + CustosInfo.Version);
            HttpResponseMessage response;
            try
            {
                response = http.Send(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt < 3)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(attempt * 2));
                    continue;
                }

                throw new StoreException(Messages.Format(Messages.StoreUnreachable, host, ex.Message), ex);
            }

            using (response)
            {
                string body;
                using (var reader = new StreamReader(response.Content.ReadAsStream(cancellationToken), Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                if ((int)response.StatusCode >= 500 && attempt < 3)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(attempt * 2));
                    continue;
                }

                return Parse(service, response.StatusCode, body, refreshing);
            }
        }
    }

    private static CloudGrant Parse(CloudService service, HttpStatusCode status, string body, bool refreshing)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new StoreException(Messages.Format(Messages.CloudSignInFailed, service.Name, ((int)status).ToString(CultureInfo.InvariantCulture) + " " + Trim(body)));
        }

        using (document)
        {
            var root = document.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (status == HttpStatusCode.OK && Text("access_token") is { Length: > 0 } access)
            {
                long seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out long value) ? value : 3600;
                return new CloudGrant(access, Text("refresh_token"), DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 60, 366L * 24 * 3600)));
            }

            string error = Text("error") ?? ((int)status).ToString(CultureInfo.InvariantCulture);
            string detail = Text("error_description") is { Length: > 0 } description ? error + ": " + description : error;
            if (refreshing && error is "invalid_grant" or "unauthorized_client" or "invalid_token")
            {
                throw new CloudAccessRevokedException(detail);
            }

            if (error == "invalid_client")
            {
                throw new StoreException(Messages.Format(Messages.CloudAppRefused, service.Name, detail));
            }

            throw new StoreException(Messages.Format(Messages.CloudSignInFailed, service.Name, detail));
        }
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>
/// The tokens of a stored account: the access token is kept in the memory of the service only and renewed with the
/// refresh token when it runs out. A refresh token the service replaces (Yandex) is stored at once.
/// </summary>
public sealed class OAuthTokens : ICloudTokens
{
    private static readonly ConcurrentDictionary<string, CachedToken> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lock> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _credentialId;
    private readonly string _user;
    private readonly CloudService _service;
    private readonly CloudApp? _app;
    private readonly Action<string>? _replaced;
    private readonly HttpMessageHandler? _handler;
    private string _refreshToken;

    /// <param name="app">The application that issued the token; null when this computer no longer knows it.</param>
    /// <param name="replaced">Stores a new refresh token the service gave out.</param>
    public OAuthTokens(string credentialId, string user, CloudService service, CloudApp? app, string refreshToken, Action<string>? replaced = null, HttpMessageHandler? handler = null)
    {
        _credentialId = credentialId ?? throw new ArgumentNullException(nameof(credentialId));
        _user = user ?? string.Empty;
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _app = app;
        _refreshToken = refreshToken ?? string.Empty;
        _replaced = replaced;
        _handler = handler;
    }

    /// <summary>Forgets the access token of an account (connected again, or deleted).</summary>
    public static void Forget(string credentialId) => Cache.TryRemove(credentialId, out _);

    public string AccessToken(bool renew, CancellationToken cancellationToken)
    {
        string key = _credentialId;
        lock (Locks.GetOrAdd(key, _ => new Lock()))
        {
            if (!renew && Cache.TryGetValue(key, out var cached) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(2))
            {
                return cached.AccessToken;
            }

            if (_refreshToken.Length == 0)
            {
                throw Revoked(null);
            }

            if (_app is null)
            {
                throw new StoreException(Messages.Format(Messages.CloudAppMissing, _service.Name));
            }

            CloudGrant grant;
            try
            {
                grant = OAuthClient.Refresh(_service, _app, _refreshToken, _handler, cancellationToken);
            }
            catch (CloudAccessRevokedException ex)
            {
                Cache.TryRemove(key, out _);
                throw Revoked(ex);
            }

            if (grant.RefreshToken is { Length: > 0 } replaced && replaced != _refreshToken)
            {
                _refreshToken = replaced;
                _replaced?.Invoke(replaced);
            }

            Cache[key] = new CachedToken(grant.AccessToken, grant.ExpiresUtc);
            return grant.AccessToken;
        }
    }

    private CloudAccessRevokedException Revoked(Exception? inner) =>
        new(Messages.Format(Messages.CloudAccessRevoked, _user, _service.Name), inner);

    private sealed record CachedToken(string AccessToken, DateTime ExpiresUtc);
}

/// <summary>The tokens of a sign-in that has just finished: used to ask the service for the account's name.</summary>
public sealed class GrantTokens(CloudGrant grant) : ICloudTokens
{
    public string AccessToken(bool renew, CancellationToken cancellationToken) =>
        renew ? throw new CloudAccessRevokedException(Messages.Format(Messages.CloudSignInFailed, "OAuth", "401")) : grant.AccessToken;
}
