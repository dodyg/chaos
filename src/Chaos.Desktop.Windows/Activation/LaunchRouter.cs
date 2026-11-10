using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Chaos.Desktop.Windows.Activation;

/// <summary>
/// Routes a secondary launch to the running instance (PLAN §5/§13: secondary launches route
/// capture and activation to the running instance and never touch storage). The primary listens
/// on a named pipe that only the current user can open; a secondary connects, sends its
/// arguments and waits for a one-byte acknowledgement.
/// <para>
/// Frame: a 4-byte little-endian length, then UTF-8 arguments separated by NUL. At most
/// <see cref="LaunchIntents.MaxArguments"/> arguments of <see cref="LaunchIntents.MaxArgumentLength"/>
/// characters; anything larger is refused without being read. No environment, paths or
/// permissions travel; the primary re-parses the arguments with <see cref="LaunchIntents"/>.
/// </para>
/// </summary>
public sealed class LaunchRouter : IAsyncDisposable
{
    public const int MaxFrameBytes = 16 * 1024;
    private const byte Accepted = 1;
    private const byte Refused = 0;

    private readonly string _pipeName;
    private readonly Func<IReadOnlyList<string>, bool> _onLaunch;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private LaunchRouter(string pipeName, Func<IReadOnlyList<string>, bool> onLaunch)
    {
        _pipeName = pipeName;
        _onLaunch = onLaunch;
    }

    /// <summary>The pipe name for an instance key, the same key <see cref="SingleInstanceGuard"/> uses.</summary>
    public static string PipeNameFor(string instanceKey) => "Chaos.Launch." + SingleInstanceGuard.Sanitize(instanceKey);

    /// <summary>Starts listening. <paramref name="onLaunch"/> runs on a background thread and returns whether it accepted the launch.</summary>
    public static LaunchRouter Listen(string instanceKey, Func<IReadOnlyList<string>, bool> onLaunch)
    {
        var router = new LaunchRouter(PipeNameFor(instanceKey), onLaunch);
        router._loop = Task.Run(router.ServeAsync);
        return router;
    }

    /// <summary>Sends <paramref name="args"/> to the primary. Returns false when no primary answered or it refused.</summary>
    public static async Task<bool> SendAsync(string instanceKey, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var frame = Encode(args);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeNameFor(instanceKey), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(frame, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            var answer = new byte[1];
            return await pipe.ReadAsync(answer, deadline.Token) == 1 && answer[0] == Accepted;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Encodes the frame; arguments beyond the limits are dropped, not truncated.</summary>
    public static byte[] Encode(IReadOnlyList<string> args)
    {
        var kept = args.Where(arg => arg.Length <= LaunchIntents.MaxArgumentLength && !arg.Contains('\0')).Take(LaunchIntents.MaxArguments);
        var body = Encoding.UTF8.GetBytes(string.Join('\0', kept));
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Decodes a frame body; null when it breaks a limit.</summary>
    public static IReadOnlyList<string>? Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxFrameBytes)
        {
            return null;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var args = text.Length == 0 ? [] : text.Split('\0');
        return args.Length > LaunchIntents.MaxArguments || args.Any(arg => arg.Length > LaunchIntents.MaxArgumentLength) ? null : args;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                exchange.CancelAfter(TimeSpan.FromSeconds(5));
                var accepted = await ReceiveAsync(pipe, exchange.Token);
                await pipe.WriteAsync(new[] { accepted ? Accepted : Refused }, exchange.Token);
                await pipe.FlushAsync(exchange.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // One broken or slow client never stops routing; the next launch gets a fresh pipe.
                Trace.TraceWarning("Launch routing exchange failed: {0}", error.GetType().Name);
            }
        }
    }

    private async Task<bool> ReceiveAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrameBytes)
        {
            return false;
        }

        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        return Decode(body) is { } args && _onLaunch(args);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            // The loop ends by itself once stopping is requested (it handles its own cancellation).
            await _loop;
        }

        _stop.Dispose();
    }
}
