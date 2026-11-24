using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Custos.Core.Cloud;

/// <summary>
/// Waits on a loopback address of this computer for the browser coming back from the sign-in page of a cloud drive
/// (section 6.3, «Подключение аккаунта»): the service sends the browser to <c>http://127.0.0.1:port/?code=…&amp;state=…</c>,
/// this answers with a short page and hands over the address. Plain sockets, not HttpListener: HttpListener wants a URL
/// reservation that a user without administrator rights may not have.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    /// <summary>A request of a browser fits into this many bytes; anything longer is not the answer.</summary>
    private const int MaxRequest = 16 * 1024;

    private readonly List<TcpListener> _listeners;
    private readonly CancellationTokenSource _stop = new();

    private LoopbackReceiver(List<TcpListener> listeners, int port)
    {
        _listeners = listeners;
        Port = port;
    }

    /// <summary>The port listened on.</summary>
    public int Port { get; }

    /// <summary>
    /// Starts listening on 127.0.0.1 and, where the computer has it, on [::1] (a browser may take «localhost» for
    /// either). Port 0 takes any free port.
    /// </summary>
    /// <returns>Null when the port is taken by another program: then the code is entered by hand.</returns>
    public static LoopbackReceiver? TryStart(int port)
    {
        var listeners = new List<TcpListener>();
        try
        {
            var first = new TcpListener(IPAddress.Loopback, port);
            first.Start();
            listeners.Add(first);
            int chosen = ((IPEndPoint)first.LocalEndpoint).Port;
            if (Socket.OSSupportsIPv6)
            {
                var second = new TcpListener(IPAddress.IPv6Loopback, chosen);
                try
                {
                    second.Start();
                    listeners.Add(second);
                }
                catch (SocketException)
                {
                    // No IPv6 on this computer, or the port is taken there: 127.0.0.1 alone will do.
                    second.Stop();
                }
            }

            return new LoopbackReceiver(listeners, chosen);
        }
        catch (SocketException)
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }

            return null;
        }
    }

    /// <summary>
    /// Waits for the request that carries the answer of the service (a <c>code</c> or an <c>error</c> in the query),
    /// shows <paramref name="page"/> to the browser and returns the path with the query (<c>/?code=…</c>). Other
    /// requests (the icon of the page, a stray visit) get "not found" and the wait goes on.
    /// </summary>
    /// <param name="expectedState">When set, accept only callbacks carrying this OAuth state.</param>
    public async Task<string> WaitAsync(string page, CancellationToken cancellationToken, string? expectedState = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var pending = new List<Task<TcpClient>>();
        var listeners = new Dictionary<Task<TcpClient>, TcpListener>();
        foreach (var listener in _listeners)
        {
            var accept = listener.AcceptTcpClientAsync(linked.Token).AsTask();
            pending.Add(accept);
            listeners[accept] = listener;
        }

        while (true)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            var listener = listeners[done];
            listeners.Remove(done);
            using (var client = await done.ConfigureAwait(false))
            {
                string? target = await HandleAsync(client, page, expectedState, linked.Token).ConfigureAwait(false);
                if (target is not null)
                {
                    return target;
                }
            }

            var next = listener.AcceptTcpClientAsync(linked.Token).AsTask();
            pending.Add(next);
            listeners[next] = listener;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        _stop.Dispose();
    }

    /// <summary>Whether a request target is the answer of the service: the root with a code or an error.</summary>
    internal static bool IsAnswer(string target, string? expectedState = null)
    {
        int query = target.IndexOf('?', StringComparison.Ordinal);
        if (query < 0 || target[..query] != "/")
        {
            return false;
        }

        string[] parts = target[(query + 1)..].Split('&');
        if (!parts.Any(p => p.StartsWith("code=", StringComparison.Ordinal) || p.StartsWith("error=", StringComparison.Ordinal)))
        {
            return false;
        }

        // Reject missing and duplicate state parameters, including percent-encoded names.
        return expectedState is null || parts.Select(part => part.Split('=', 2))
            .Where(pair => WebUtility.UrlDecode(pair[0]) == "state")
            .Select(pair => pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : string.Empty)
            .SequenceEqual([expectedState], StringComparer.Ordinal);
    }

    private static async Task<string?> HandleAsync(TcpClient client, string page, string? expectedState, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        var stream = client.GetStream();
        string? target;
        try
        {
            target = await ReadTargetAsync(stream, limit.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A connection that said nothing in time (a browser opens spare ones).
            return null;
        }

        bool answer = target is not null && IsAnswer(target, expectedState);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(answer
                ? "<!doctype html><html><head><meta charset=\"utf-8\"><title>CUSTOS</title></head><body style=\"font-family:sans-serif;margin:3em\"><p>"
                    + WebUtility.HtmlEncode(page) + "</p></body></html>"
                : "Not found");
            string head = (answer ? "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" : "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\n")
                + "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\nContent-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), limit.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, limit.Token).ConfigureAwait(false);
            await stream.FlushAsync(limit.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser went away; the address it brought is what counts.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return answer ? target : null;
    }

    /// <summary>The target of a GET request (<c>/?code=…</c>), read up to the end of its headers; null for anything else.</summary>
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequest];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string text = Encoding.ASCII.GetString(buffer, 0, length);
        int end = text.IndexOf("\r\n", StringComparison.Ordinal);
        string[] line = (end < 0 ? text : text[..end]).Split(' ');
        return line.Length >= 3 && line[0] == "GET" && line[1].StartsWith('/') ? line[1] : null;
    }
}
