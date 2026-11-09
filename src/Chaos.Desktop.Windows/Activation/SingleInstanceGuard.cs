namespace Chaos.Desktop.Windows.Activation;

/// <summary>
/// Owns the per-instance application lock. PLAN §5 (migrations/bootstrap): "The app lock
/// prevents a second process migrating or restoring the same root; secondary launches
/// route capture/activation to the running instance." A secondary instance must detect
/// that a primary owns the lock and forward activation instead of touching storage.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _owned;

    public bool IsPrimaryInstance => _owned;

    /// <param name="instanceName">
    /// Stable, user-visible instance key. Data-root specific names allow several
    /// independent roots on one machine without stealing each other's lock.
    /// </param>
    public SingleInstanceGuard(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        var sanitized = Sanitize(instanceName);
        _mutex = new Mutex(initiallyOwned: false, name: @"Local\Chaos." + sanitized, out _);

        try
        {
            _owned = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A crashed primary left the mutex abandoned: the lock is free and we may take it.
            _owned = true;
        }
    }

    private static string Sanitize(string instanceName)
    {
        // Mutex names exclude a few characters; fold everything outside letters,
        // digits, dot, dash and underscore to '-' so any data-root path maps stably.
        var chars = instanceName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray();
        var name = new string(chars);
        return name.Length switch
        {
            0 => "default",
            > 200 => name[..200],
            _ => name,
        };
    }

    public void Dispose()
    {
        if (_owned)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Release throws when the owning thread differs; disposal from a foreign
                // thread must not mask the original failure being handled by the caller.
            }
        }

        _mutex?.Dispose();
    }
}
