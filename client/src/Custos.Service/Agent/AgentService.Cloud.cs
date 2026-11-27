using Custos.Core;
using Custos.Core.Cloud;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Service.Agent;

/// <summary>
/// Accounts of cloud drives (section 6.3, «Облачные диски»): the interface sends the browser to the service and gives
/// back what came from there; the service keeps the PKCE secret of the sign-in, trades the code for tokens and keeps
/// the refresh token under DPAPI. The person's password never comes near CUSTOS.
/// </summary>
internal sealed partial class AgentService
{
    /// <summary>How long a sign-in may take in the browser.</summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(20);

    private readonly Dictionary<string, PendingSignIn> _signIns = new(StringComparer.Ordinal);

    public CloudBeginResult BeginCloud(CloudBeginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DestinationKinds.IsCloudDrive(request.Kind))
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.Format(Messages.DestinationKindNotSupported, request.Kind));
        }

        if (request.RedirectUri is { } redirect && !IsLoopback(redirect))
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.CloudBadRedirect);
        }

        if (request.CredentialId is { } id && _config.GetCredential(id)?.Kind != request.Kind)
        {
            // Only an account of the same service is connected again.
            throw NotFound();
        }

        var service = CloudService.Of(request.Kind);
        var app = CloudApps.ForConnect(request.Kind, _config.GetCloudApps());
#if CUSTOS_LINUX
        if (app is null || (service.NeedsSecret && string.IsNullOrWhiteSpace(app.ClientSecret)))
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Custos.Service.Platform.LinuxStrings.Format(
                Custos.Service.Platform.LinuxStrings.CliCloudAppMissing, service.Name, service.Kind));
        }
#else
        if (app is null)
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.Format(Messages.CloudAppMissing, service.Name));
        }
#endif

        var authorization = CloudAuthorization.Begin(service, app, request.RedirectUri);
        lock (_signIns)
        {
            foreach (string old in _signIns.Where(p => DateTime.UtcNow - p.Value.Authorization.StartedUtc > SignInTimeout).Select(p => p.Key).ToList())
            {
                _signIns.Remove(old);
            }

            _signIns[authorization.State] = new PendingSignIn(authorization, request.CredentialId);
        }

        return new CloudBeginResult { State = authorization.State, Url = authorization.Url, RedirectUri = authorization.RedirectUri };
    }

    public Credential FinishCloud(CloudFinishRequest request, string user)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingSignIn? pending;
        lock (_signIns)
        {
            _signIns.Remove(request.State ?? string.Empty, out pending);
        }

        if (pending is null || DateTime.UtcNow - pending.Authorization.StartedUtc > SignInTimeout)
        {
            throw new AgentRequestException(PipeProtocol.Errors.Conflict, Messages.CloudSignInExpired);
        }

        var authorization = pending.Authorization;
        var service = authorization.Service;
        Credential saved;
        try
        {
            string code = authorization.ReadCode(request.Answer)
                ?? throw new StoreException(Messages.Format(Messages.CloudSignInFailed, service.Name, "code"));
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var grant = authorization.Finish(code, null, limit.Token);

            // The name of the account, as the service tells it, with the new token.
            var destination = new TaskDestination { Kind = service.Kind };
            string name;
            using (var store = OpenCloud(destination, new CloudAccount(service.Name, new GrantTokens(grant))))
            {
                store.Cancellation = limit.Token;
                name = store.AccountName();
            }

            saved = _config.SaveCloudAccount(pending.CredentialId, service.Kind, name, grant.RefreshToken!, authorization.App.ClientId);
        }
        catch (Exception ex) when (ex is StoreException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, ex is OperationCanceledException ? Messages.Format(Messages.StoreFailed, service.Name, Messages.StoreNoAnswer) : ex.Message);
        }
        catch (Core.Agent.ConfigException ex)
        {
            throw Refused(ex);
        }

        OAuthTokens.Forget(saved.Id);
        Log(LogLineLevels.Info, Messages.Format(Messages.CloudConnected, saved.User, service.Name, user));
        NoteConfigChange();
        PublishSnapshot();
        return saved;
    }

    public CloudAppsResult GetCloudApps() => new()
    {
        Own = _config.GetCloudApps(),
        BuiltIn = [.. ((string[])[DestinationKinds.GDrive, DestinationKinds.YaDisk, DestinationKinds.Dropbox]).Where(HasBuiltInCloudApp)],
    };

    private static bool HasBuiltInCloudApp(string kind)
    {
#if CUSTOS_LINUX
        return CloudApps.BuiltIn(kind) is { } app
            && (!CloudService.Of(kind).NeedsSecret || !string.IsNullOrWhiteSpace(app.ClientSecret));
#else
        return CloudApps.BuiltIn(kind) is not null;
#endif
    }

    public OkResult SaveCloudApps(CloudAppsSaveRequest request, string user)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            _config.SaveCloudApps(request.Apps, request.Kind);
        }
        catch (Core.Agent.ConfigException ex)
        {
            throw Refused(ex);
        }

        Log(LogLineLevels.Info, Messages.Format(Messages.CloudAppsSaved, user));
        return OkResult.Instance;
    }

    /// <summary>Only an address of this computer: the code must not be sent anywhere else.</summary>
    private static bool IsLoopback(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
        && uri.Host is "127.0.0.1" or "localhost" or "[::1]";

    private static CloudStore OpenCloud(TaskDestination destination, CloudAccount account) => destination.Kind switch
    {
        DestinationKinds.GDrive => new GoogleDriveStore(destination, account),
        DestinationKinds.YaDisk => new YandexDiskStore(destination, account),
        _ => new DropboxStore(destination, account),
    };

    private sealed record PendingSignIn(CloudAuthorization Authorization, string? CredentialId);
}
