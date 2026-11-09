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

    public async Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
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

    public void Dispose()
    {
        _writer.Dispose();
        _writeGate.Dispose();
    }
}
