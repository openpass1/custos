using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Custos.Core.Monitor;

/// <summary>An answer of the server as the agent sees it. Only a signed answer may be acted upon (A.2, A.11).</summary>
public sealed class MonitorAnswer
{
    /// <summary>The HTTP status; 0 when there was no answer at all.</summary>
    public int Status { get; init; }

    public byte[] Body { get; init; } = [];

    /// <summary>The signature of the pinned server key is right.</summary>
    public bool Authentic { get; init; }

    /// <summary>Why there was no answer (network, timeout).</summary>
    public string? TransportError { get; init; }

    /// <summary><c>error</c> of an error body (A.11), or null.</summary>
    public string? Error => ErrorBody?.Error;

    public ErrorAnswer? ErrorBody { get; init; }

    public bool Ok => Authentic && Status == 200;

    public T? Read<T>(JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(Body, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The answer in words, for the log and the program window.</summary>
    public string Describe()
    {
        if (Status == 0)
        {
            return Messages.Format(Messages.MonitorNoConnection, TransportError);
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"HTTP {Status}");
        if (ErrorBody?.Error is { } error)
        {
            text += $" {error}: {ErrorBody.Message}";
        }

        return Authentic ? text : Messages.Format(Messages.MonitorUnsigned, text);
    }

    internal static ErrorAnswer? ParseError(byte[] body)
    {
        if (body.Length == 0 || body[0] != (byte)'{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, MonitorJsonContext.Default.ErrorAnswer);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What became of a registration (A.3).</summary>
public sealed class RegistrationOutcome
{
    public bool Accepted { get; init; }

    public RegisterAnswer? Answer { get; init; }

    /// <summary>The answer was 403 bad_registration_key: the key typed is not the one of the server.</summary>
    public bool KeyRejected { get; init; }

    /// <summary>There was no usable answer: retry like any other unavailability (A.11).</summary>
    public bool Unreachable { get; init; }

    /// <summary>Why it was not accepted, in words.</summary>
    public string? Reason { get; init; }

    public required MonitorAnswer Raw { get; init; }
}

/// <summary>
/// HTTP to the monitoring server: every request signed with the agent key, every answer checked against the pinned
/// server key (A.2). Thread-safe.
/// </summary>
public sealed class MonitorClient : IDisposable
{
    public const string ProtocolVersion = "1";
    public const string ApiPrefix = "/api/v1/";

    /// <summary>Largest answer read into memory (commands, errors). Packages are streamed to a file instead.</summary>
    private const int MaxAnswerBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;

    /// <param name="handler">Replaces the network in tests; null = a normal handler that unpacks gzip answers.</param>
    public MonitorClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        handler ??= new SocketsHttpHandler
        {
            // The signature covers the unpacked body (A.2).
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CUSTOS/" + CustosInfo.Version);
        RequestTimeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    public TimeSpan RequestTimeout { get; }

    /// <summary>The address of a path under the API: <c>{server_url}/api/v1/{path}</c>.</summary>
    public static Uri ApiUri(string serverUrl, string path)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        return new Uri(serverUrl.TrimEnd('/') + ApiPrefix + path, UriKind.Absolute);
    }

    /// <summary>Whether this is an address the agent can use: http or https, absolute, no query or fragment.</summary>
    public static bool IsValidServerUrl(string? url, out string normalized)
    {
        normalized = (url ?? string.Empty).Trim().TrimEnd('/');
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>A signed request; the answer is checked against <paramref name="serverKey"/>.</summary>
    public async Task<MonitorAnswer> CallAsync(string serverUrl, string method, string path, byte[]? body, string agentId, RSA agentKey, RSA? serverKey, CancellationToken cancellationToken)
    {
        string nonce = MonitorCrypto.NewNonce();
        using var request = BuildRequest(serverUrl, method, path, body ?? [], nonce, agentKey);
        request.Headers.Add("X-Custos-Agent", agentId);
        return await SendAsync(request, nonce, serverKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registration (A.3): proves the registration key by HMAC without sending it, and accepts the answer only if its
    /// HMAC is right, it is signed by the server key it carries, that key matches the fingerprint of the code (if
    /// any) and the key pinned before (if any).
    /// </summary>
    public async Task<RegistrationOutcome> RegisterAsync(string serverUrl, RegistrationCode code, RegisterBody body, RSA agentKey, string? pinnedServerKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        const string Path = "agent/register";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body, MonitorJsonContext.Default.RegisterBody);
        string nonce = MonitorCrypto.NewNonce();
        using var request = BuildRequest(serverUrl, "POST", Path, json, nonce, agentKey);
        string time = request.Headers.GetValues("X-Custos-Time").First();
        request.Headers.Add("X-Custos-Registration-Mac", MonitorCrypto.Hmac(code.Key, MonitorCrypto.RequestString("POST", Path, time, nonce, json)));

        var (status, headers, answerBody, transportError) = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (status == 0)
        {
            var none = new MonitorAnswer { Status = 0, TransportError = transportError };
            return new RegistrationOutcome { Unreachable = true, Reason = none.Describe(), Raw = none };
        }

        string responseString = MonitorCrypto.ResponseString(status, Header(headers, "X-Custos-Time"), nonce, answerBody);
        bool macValid = MonitorCrypto.HmacValid(code.Key, responseString, Header(headers, "X-Custos-Registration-Mac"));
        var error = MonitorAnswer.ParseError(answerBody);

        if (status != 200)
        {
            // Without a pinned key an error can be checked only by its HMAC; with one, by the signature too.
            bool signed = macValid;
            if (signed && pinnedServerKey is not null)
            {
                using var pinned = MonitorCrypto.LoadPublicPem(pinnedServerKey);
                signed = MonitorCrypto.Verify(pinned, responseString, Header(headers, "X-Custos-Signature"));
            }

            var raw = new MonitorAnswer { Status = status, Body = answerBody, Authentic = signed, ErrorBody = error };
            if (status == 403 && error?.Error == "bad_registration_key")
            {
                // Signed only with the server key, which the agent cannot know yet (A.3).
                return new RegistrationOutcome { KeyRejected = true, Reason = Messages.MonitorKeyRejected, Raw = raw };
            }

            return new RegistrationOutcome { Unreachable = !signed, Reason = raw.Describe(), Raw = raw };
        }

        RegisterAnswer? answer;
        try
        {
            answer = JsonSerializer.Deserialize(answerBody, MonitorJsonContext.Default.RegisterAnswer);
        }
        catch (JsonException)
        {
            answer = null;
        }

        using var serverKey = MonitorCrypto.LoadPublicPem(answer?.ServerPublicKey);
        bool signatureValid = MonitorCrypto.Verify(serverKey, responseString, Header(headers, "X-Custos-Signature"));
        var result = new MonitorAnswer { Status = status, Body = answerBody, Authentic = signatureValid };
        string? failed = !macValid ? Messages.MonitorCheckMac
            : answer is null || serverKey is null || string.IsNullOrEmpty(answer.AgentId) ? Messages.MonitorCheckBody
            : !signatureValid ? Messages.MonitorCheckSignature
            : code.Fingerprint is { } expected && MonitorCrypto.NormalizeFingerprint(MonitorCrypto.Fingerprint(serverKey)) != expected
                ? Messages.Format(Messages.MonitorCheckFingerprint, MonitorCrypto.Fingerprint(serverKey))
            : pinnedServerKey is not null && !MonitorCrypto.SameKey(pinnedServerKey, answer.ServerPublicKey) ? Messages.MonitorCheckPinned
            : null;
        if (failed is not null)
        {
            return new RegistrationOutcome { Reason = Messages.Format(Messages.MonitorRegistrationRefused, failed), Raw = result };
        }

        return new RegistrationOutcome { Accepted = true, Answer = answer, Raw = result };
    }

    /// <summary>
    /// Downloads a package (A.10) into <paramref name="file"/>: a signed GET whose 200 body is the file itself and is
    /// not signed; the caller checks it by the SHA-256 and the signature of the command. Errors are signed as usual.
    /// </summary>
    /// <returns>The answer; on success its body is empty and the file holds the download.</returns>
    public async Task<MonitorAnswer> DownloadAsync(string serverUrl, string path, string file, long maxBytes, string agentId, RSA agentKey, RSA? serverKey, CancellationToken cancellationToken)
    {
        string nonce = MonitorCrypto.NewNonce();
        using var request = BuildRequest(serverUrl, "GET", path, [], nonce, agentKey);
        request.Headers.Add("X-Custos-Agent", agentId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                byte[] body = await ReadLimitedAsync(response.Content, MaxAnswerBytes, timeout.Token).ConfigureAwait(false);
                return Checked((int)response.StatusCode, Headers(response), body, nonce, serverKey);
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    byte[] buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes)
                        {
                            throw new IOException(Messages.Format(Messages.MonitorDownloadTooBig, maxBytes / (1024 * 1024)));
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }
            }

            return new MonitorAnswer { Status = 200, Authentic = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new MonitorAnswer { Status = 0, TransportError = ex is OperationCanceledException ? Messages.MonitorTimeout : ex.Message };
        }
    }

    public void Dispose() => _http.Dispose();

    private static HttpRequestMessage BuildRequest(string serverUrl, string method, string path, byte[] body, string nonce, RSA agentKey)
    {
        string time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(new HttpMethod(method), ApiUri(serverUrl, path));
        if (method != "GET")
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        request.Headers.Add("X-Custos-Protocol", ProtocolVersion);
        request.Headers.Add("X-Custos-Time", time);
        request.Headers.Add("X-Custos-Nonce", nonce);
        request.Headers.Add("X-Custos-Signature", MonitorCrypto.Sign(agentKey, MonitorCrypto.RequestString(method, path, time, nonce, body)));
        return request;
    }

    private async Task<MonitorAnswer> SendAsync(HttpRequestMessage request, string nonce, RSA? serverKey, CancellationToken cancellationToken)
    {
        var (status, headers, body, transportError) = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        return status == 0 ? new MonitorAnswer { Status = 0, TransportError = transportError } : Checked(status, headers, body, nonce, serverKey);
    }

    private static MonitorAnswer Checked(int status, Dictionary<string, string> headers, byte[] body, string nonce, RSA? serverKey)
    {
        bool signed = MonitorCrypto.Verify(serverKey, MonitorCrypto.ResponseString(status, Header(headers, "X-Custos-Time"), nonce, body), Header(headers, "X-Custos-Signature"));
        return new MonitorAnswer { Status = status, Body = body, Authentic = signed, ErrorBody = status == 200 ? null : MonitorAnswer.ParseError(body) };
    }

    private async Task<(int Status, Dictionary<string, string> Headers, byte[] Body, string? Error)> ExchangeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            byte[] body = await ReadLimitedAsync(response.Content, MaxAnswerBytes, timeout.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, Headers(response), body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (0, [], [], ex is OperationCanceledException ? Messages.MonitorTimeout : Innermost(ex));
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > limit)
                {
                    throw new IOException(Messages.Format(Messages.MonitorAnswerTooBig, limit / (1024 * 1024)));
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }
    }

    private static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return headers;
    }

    private static string Header(Dictionary<string, string> headers, string name) => headers.TryGetValue(name, out string? value) ? value : string.Empty;
}
