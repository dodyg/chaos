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
/// </summary>
public sealed class SqliteApplicationStore(SqliteConnectionSource source) : IApplicationStore, IDisposable
{
    private readonly SqliteConnectionSource _source = source;
    private readonly SqliteConnection _writer = source.Open();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _closed;

    public async Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_closed)
            {
                throw new AppException(AppErrorKind.StorageUnavailable, "Chaos is closing, so this change was not saved. Reopen Chaos and try again.");
            }

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
        await using var connection = _source.Open();
        try
        {
            return await query(new SqliteDataSession(connection), ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw SqliteErrorMapper.Map(error);
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
