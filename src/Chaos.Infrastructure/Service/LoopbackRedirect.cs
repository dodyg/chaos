using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Chaos.Infrastructure.Service;

/// <summary>The outcome of the browser coming back to the loopback address.</summary>
internal sealed record LoopbackResult(string? Code, string? Error);

/// <summary>
/// A one-shot HTTP listener on 127.0.0.1 for the OAuth redirect (RFC 8252 section 7.3). It answers only
/// requests that carry the expected <c>state</c>; anything else (a favicon, a probe, a stray link) gets an
/// error page and the wait continues.
/// </summary>
internal sealed class LoopbackRedirect : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackRedirect(string callbackPath)
    {
        _listener.Start();
        Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{callbackPath}");
        CallbackPath = callbackPath;
    }

    public Uri Uri { get; }

    private string CallbackPath { get; }

    public async Task<LoopbackResult> WaitAsync(string expectedState, string successMessage, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var target = (await ReadRequestLineAsync(stream, ct)).Split(' ').ElementAtOrDefault(1) ?? "/";
            var queryAt = target.IndexOf('?', StringComparison.Ordinal);
            var path = queryAt < 0 ? target : target[..queryAt];
            if (path != CallbackPath || queryAt < 0)
            {
                await RespondAsync(stream, 404, "Not found.", ct);
                continue;
            }

            var query = ParseQuery(target[(queryAt + 1)..]);
            if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(expectedState)))
            {
                await RespondAsync(stream, 400, "This sign-in link does not belong to Chaos. You can close this tab.", ct);
                continue;
            }

            if (query.TryGetValue("error", out var error))
            {
                await RespondAsync(stream, 200, "The sign-in did not complete. You can close this tab and return to Chaos.", ct);
                return new LoopbackResult(null, error);
            }

            if (!query.TryGetValue("code", out var code) || code.Length == 0)
            {
                await RespondAsync(stream, 400, "The service sent no sign-in code. You can close this tab.", ct);
                continue;
            }

            await RespondAsync(stream, 200, successMessage, ct);
            return new LoopbackResult(code, null);
        }
    }

    public void Dispose() => _listener.Stop();

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var body = $"<!doctype html><meta charset=\"utf-8\"><title>Chaos</title><body style=\"font-family:sans-serif;margin:3em\"><p>{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            result[name] = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }

        return result;
    }
}
