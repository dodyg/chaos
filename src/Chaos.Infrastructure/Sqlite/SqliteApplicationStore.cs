using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Transaction worker (PLAN §4/§5): one serialized writer connection behind a write gate,
/// short-lived pooled read connections. SQL runs synchronously on the caller's worker
/// thread; the async surface is the application boundary. A failed write rolls the whole
/// transaction back and maps SQLite errors to stable AppErrorKind values; nothing is
/// published until commit.
/// <para>
/// Restore and reset (PLAN §14) need every connection closed while database files are
/// replaced. <see cref="RunExclusiveAsync"/> takes the write gate, holds new reads, waits for
/// reads in flight, closes the writer and clears the pool, runs the file work, then reopens.
/// </para>
/// </summary>
public sealed class SqliteApplicationStore(SqliteConnectionSource source) : IApplicationStore, IDisposable
{
    private readonly SqliteConnectionSource _source = source;
    private SqliteConnection _writer = source.Open();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _readSync = new();
    private int _activeReads;
    private TaskCompletionSource? _readsHeld;
    private TaskCompletionSource? _readsDrained;
    private volatile bool _closed;

    /// <summary>The connection source this store opens; data protection opens its own connections from it.</summary>
    public SqliteConnectionSource Source => _source;

    public async Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfClosed();

            using var transaction = _writer.BeginTransaction();
            try
            {
                var result = await operation(new SqliteDataSession(_writer), ct).ConfigureAwait(false);
                transaction.Commit();
                return result;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception error)
        {
            throw SqliteErrorMapper.Map(error);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<TResult> QueryAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnterReadAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await using var connection = _source.Open();
            return await query(new SqliteDataSession(connection), ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw SqliteErrorMapper.Map(error);
        }
        finally
        {
            ExitRead();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with no connection open on the database: writes wait at
    /// the gate, new reads wait until it finishes, and reads in flight finish first. The
    /// writer is reopened afterwards even when the action fails, so the app keeps working on
    /// whatever database file is then in place.
    /// </summary>
    public async Task<TResult> RunExclusiveAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        Task drained;
        lock (_readSync)
        {
            _readsHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _readsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeReads == 0)
            {
                _readsDrained.TrySetResult();
            }

            drained = _readsDrained.Task;
        }

        try
        {
            ThrowIfClosed();
            await drained.WaitAsync(ct).ConfigureAwait(false);
            _writer.Dispose();
            SqliteConnection.ClearAllPools();
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                _writer = _source.Open();
            }
        }
        finally
        {
            TaskCompletionSource? held;
            lock (_readSync)
            {
                held = _readsHeld;
                _readsHeld = null;
                _readsDrained = null;
            }

            held?.TrySetResult();
            _writeGate.Release();
        }
    }

    private async Task EnterReadAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_readSync)
            {
                if (_readsHeld is null)
                {
                    _activeReads++;
                    return;
                }

                wait = _readsHeld.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void ExitRead()
    {
        lock (_readSync)
        {
            _activeReads--;
            if (_activeReads == 0)
            {
                _readsDrained?.TrySetResult();
            }
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new AppException(AppErrorKind.StorageUnavailable, "Chaos is closing, so this change was not saved. Reopen Chaos and try again.");
        }
    }

    /// <summary>Time a closing application waits for an in-flight write (an autosave) to commit.</summary>
    public static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for the write in progress, if any, to commit or roll back before closing the
    /// writer (PLAN §9: flush outstanding saves before disposing storage). Writes that arrive
    /// afterwards fail as storage-unavailable instead of racing the close.
    /// </summary>
    public void Dispose()
    {
        var drained = _writeGate.Wait(ShutdownDrainTimeout);
        try
        {
            _closed = true;
            _writer.Dispose();
        }
        finally
        {
            if (drained)
            {
                _writeGate.Release();
            }
        }
    }
}
