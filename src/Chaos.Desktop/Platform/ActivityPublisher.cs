using Chaos.Application.Activity;
using Chaos.Desktop.Services;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Receives head and job notices from the store after commit and pushes one
/// <c>activity.committed</c> event per coalescing window (about 100 ms), carrying the newest head
/// (PLAN2 §6.4). The event holds no data: the window asks <c>activity.since</c>. Until the RPC host
/// is built the publisher only remembers the head; a window that connects later starts from the head.
/// </summary>
public sealed class ActivityPublisher(TimeProvider clock) : IActivityNotifier, IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private Func<ActivityCommitted, Task>? _publish;
    private ITimer? _timer;
    private long _head;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Called once the RPC host exists; <paramref name="publish"/> sends the event to every subscribed window.</summary>
    public void Attach(Func<ActivityCommitted, Task> publish)
    {
        lock (_gate)
        {
            _publish = publish;
        }
    }

    public void HeadChanged(long seq)
    {
        lock (_gate)
        {
            if (seq > _head)
            {
                _head = seq;
            }

            Arm();
        }
    }

    public void JobsChanged()
    {
        lock (_gate)
        {
            Arm();
        }
    }

    private void Arm()
    {
        _dirty = true;
        if (_disposed || _timer is not null)
        {
            return;
        }

        _timer = clock.CreateTimer(_ => Flush(), null, Window, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        Func<ActivityCommitted, Task>? publish;
        ActivityCommitted payload;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (!_dirty || _disposed)
            {
                return;
            }

            _dirty = false;
            publish = _publish;
            payload = new ActivityCommitted(_head.ToString());
        }

        if (publish is null)
        {
            return;
        }

        try
        {
            _ = publish(payload).ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception)
        {
            // A window that cannot be reached misses a push; it reloads on focus.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
