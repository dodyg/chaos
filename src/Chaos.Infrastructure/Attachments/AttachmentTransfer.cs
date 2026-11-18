using System.Collections.Concurrent;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Attachments;

/// <summary>Attachment transfer settings (PLAN2 §9.2).</summary>
public sealed class AttachmentTransferOptions
{
    /// <summary>Files up to this size download as soon as their metadata arrives; larger ones wait to be opened (5 MiB).</summary>
    public long AutoDownloadBytes { get; init; } = 5L * 1024 * 1024;

    /// <summary>How often a pass runs when nothing wakes it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long a refused upload (over quota) waits before it asks again.</summary>
    public TimeSpan QuotaRetry { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>What one pass did, for tests and diagnostics.</summary>
public sealed record AttachmentTransferResult(int Uploaded, int Downloaded, int Failed);

/// <summary>
/// Moves attachment bytes between this computer and the online service (PLAN2 §9.2). Uploads follow the metadata, one file at a
/// time, resuming from the parts the service holds. Downloads follow the rules: files up to 5 MiB at once, larger ones when opened,
/// pinned lists and projects whatever their size (the pad and pages are always pinned). It reads and writes only device-local state
/// (<c>AttachmentLocalState</c>, <c>OfflineContainers</c>), so nothing here ever travels to another device.
/// </summary>
public sealed class AttachmentTransfer(SqliteApplicationStore store, IAttachmentFileStore files, IBlobTransport transport, TimeProvider clock, AttachmentTransferOptions? options = null) : IAttachmentAvailability, IDisposable
{
    private const int Unknown = 0;
    private const int OnService = 1;
    private const int Blocked = 2;
    private const int UploadFailed = 3;
    private const int OverQuota = 4;

    private readonly AttachmentTransferOptions _options = options ?? new AttachmentTransferOptions();
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<bool>> _downloads = new();
    private readonly ConcurrentDictionary<string, Progress> _progress = new();
    private readonly Lock _wakeGate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Progress(long total)
    {
        public long Total { get; } = total;
        public long Done;
        public bool Upload;
    }

    /// <summary>Raised when any attachment's transfer state changed, so the UI can refresh.</summary>
    public event Action? Changed;

    // ---- Loop ----------------------------------------------------------------------------------------

    /// <summary>Starts the background loop; a pass runs on every wake and every poll interval. Calling it twice does nothing.</summary>
    public void Start()
    {
        lock (_wakeGate)
        {
            if (_loop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>Something changed (a sync cycle finished, a file was attached, a list was pinned): run a pass soon.</summary>
    public void Wake()
    {
        lock (_wakeGate)
        {
            _wake.TrySetResult();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                Task woken;
                lock (_wakeGate)
                {
                    woken = _wake.Task;
                }

                await Task.WhenAny(woken, Task.Delay(_options.PollInterval, clock, ct));
                ct.ThrowIfCancellationRequested();
                lock (_wakeGate)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A pass must never end the loop; the next wake or poll tries again.
                System.Diagnostics.Trace.TraceWarning("Attachment transfer pass failed: {0}", error.GetType().Name);
                await Task.Delay(_options.MinBackoff, clock, ct);
            }
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_wakeGate)
        {
            loop = _loop;
            _stop?.Cancel();
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends by cancellation.
        }

        _stop?.Dispose();
        _passGate.Dispose();
    }

    // ---- One pass ------------------------------------------------------------------------------------

    /// <summary>Uploads what the service lacks, then downloads what the rules call for. Offline or signed-out problems end the pass quietly.</summary>
    public async Task<AttachmentTransferResult> RunOnceAsync(CancellationToken ct)
    {
        await _passGate.WaitAsync(ct);
        try
        {
            if (!await IsEnabledAsync(ct))
            {
                return new(0, 0, 0);
            }

            int uploaded = 0, downloaded = 0, failed = 0;
            try
            {
                foreach (var (sha, size) in await UploadCandidatesAsync(ct))
                {
                    switch (await TryUploadAsync(sha, size, ct))
                    {
                        case true:
                            uploaded++;
                            break;
                        case false:
                            failed++;
                            break;
                    }
                }

                foreach (var (sha, size) in await DownloadCandidatesAsync(ct))
                {
                    switch (await EnsureAsync(sha, size, automatic: true, ct))
                    {
                        case true:
                            downloaded++;
                            break;
                        case false:
                            failed++;
                            break;
                    }
                }
            }
            catch (ChaosServiceException error) when (error.Kind is ServiceFailure.Offline or ServiceFailure.SignInAgain or ServiceFailure.DeviceRevoked or ServiceFailure.AccountDeleted)
            {
                // Not reachable or not signed in: the sync status already says so, and the next pass tries again.
            }

            return new(uploaded, downloaded, failed);
        }
        finally
        {
            _passGate.Release();
        }
    }

    private Task<bool> IsEnabledAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(SyncSql.Scalar(((SqliteDataSession)session).Connection, "SELECT Enabled FROM SyncState WHERE Id = 1;") == 1), ct);

    private Task<List<(string Sha, long Size)>> UploadCandidatesAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(ReadHashes(((SqliteDataSession)session).Connection, """
            SELECT a.Sha256, MAX(a.ByteSize) FROM Attachments a
            LEFT JOIN AttachmentLocalState s ON s.Sha256 = a.Sha256
            WHERE a.DeletedAtUtc IS NULL AND COALESCE(s.Remote, 0) IN (0, 3, 4) AND (s.NextTryUtc IS NULL OR s.NextTryUtc <= $now)
            GROUP BY a.Sha256 ORDER BY MIN(a.CreatedAtUtc), a.Sha256;
            """, ("$now", StorageFormats.Instant(clock.GetUtcNow())))), ct);

    /// <summary>
    /// Files that are missing here and should come now: small ones, and any size in a pinned container. The pad and pages are always
    /// pinned (PLAN2 §9.2). A file known to be blocked, or backing off after a failure, is left alone.
    /// </summary>
    private Task<List<(string Sha, long Size)>> DownloadCandidatesAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(ReadHashes(((SqliteDataSession)session).Connection, """
            SELECT a.Sha256, MAX(a.ByteSize) FROM Attachments a
            LEFT JOIN AttachmentLocalState s ON s.Sha256 = a.Sha256
            WHERE a.DeletedAtUtc IS NULL AND COALESCE(s.Remote, 0) <> 2 AND (s.NextTryUtc IS NULL OR s.NextTryUtc <= $now)
              AND (a.ByteSize <= $auto OR a.OwnerKind IN (3, 4)
                   OR (a.OwnerKind = 1 AND EXISTS (SELECT 1 FROM Tasks t JOIN OfflineContainers o ON o.ContainerId = t.ContainerId WHERE t.Id = a.OwnerId))
                   OR (a.OwnerKind = 2 AND EXISTS (SELECT 1 FROM DiaryEntries d JOIN Tasks t ON t.Id = d.TaskId JOIN OfflineContainers o ON o.ContainerId = t.ContainerId WHERE d.Id = a.OwnerId)))
            GROUP BY a.Sha256 ORDER BY MIN(a.CreatedAtUtc), a.Sha256;
            """, ("$now", StorageFormats.Instant(clock.GetUtcNow())), ("$auto", _options.AutoDownloadBytes))), ct);

    private static List<(string Sha, long Size)> ReadHashes(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<(string, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    // ---- Upload --------------------------------------------------------------------------------------

    /// <summary>Returns true when the bytes are now on the service (or were), false on a failure, null when there was nothing to send from here.</summary>
    private async Task<bool?> TryUploadAsync(string sha, long size, CancellationToken ct)
    {
        if (!await files.ExistsAsync(sha, ct))
        {
            return null;
        }

        var progress = _progress[sha] = new Progress(size) { Upload = true };
        Changed?.Invoke();
        try
        {
            var existing = await transport.HeadAsync(sha, ct);
            if (existing is not null)
            {
                await SetRemoteAsync(sha, existing.IsBlocked ? Blocked : OnService, ct);
                return !existing.IsBlocked;
            }

            var upload = await transport.StartUploadAsync(sha, size, ct);
            if (upload.BlobState is { } already)
            {
                await SetRemoteAsync(sha, already == "Blocked" ? Blocked : OnService, ct);
                return already != "Blocked";
            }

            var held = upload.Received.ToHashSet();
            progress.Done = Math.Min(size, (long)held.Count * upload.PartSize);
            var buffer = new byte[upload.PartSize];
            for (var index = 0; index < upload.PartCount; index++)
            {
                if (held.Contains(index))
                {
                    continue;
                }

                var length = (int)Math.Min(upload.PartSize, size - ((long)index * upload.PartSize));
                await using var source = await files.OpenReadAsync(sha, (long)index * upload.PartSize, ct) ?? throw new IOException("The file is no longer here.");
                var filled = 0;
                while (filled < length)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(filled, length - filled), ct);
                    if (read == 0)
                    {
                        throw new IOException("The file is shorter than it should be.");
                    }

                    filled += read;
                }

                await transport.PutPartAsync(sha, index, buffer.AsMemory(0, length), ct);
                progress.Done += length;
                Changed?.Invoke();
            }

            var done = await transport.CompleteUploadAsync(sha, ct);
            if (done.IsBlocked)
            {
                await SetRemoteAsync(sha, Blocked, ct);
                await RaiseAttentionAsync("attachment.blocked", sha, "A file you attached was blocked by the online service's malware check. It stays on this computer but is not shared.", ct);
                return false;
            }

            await SetRemoteAsync(sha, OnService, ct);
            return true;
        }
        catch (BlobTransferException error) when (error.Kind == BlobFailureKind.Quota)
        {
            await SetRemoteAsync(sha, OverQuota, ct, retryAfter: _options.QuotaRetry);
            await RaiseAttentionAsync("attachment.quota", sha, "The online service has no room for a file you attached. It stays on this computer; free some space or remove files to upload it.", ct);
            return false;
        }
        catch (BlobTransferException error)
        {
            // Too large, a hash mismatch, a scanner that is down: the file stays here and the upload is tried again later.
            await SetRemoteAsync(sha, UploadFailed, ct, backoff: true);
            System.Diagnostics.Trace.TraceWarning("Attachment upload refused: {0}", error.Kind);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await SetRemoteAsync(sha, UploadFailed, ct, backoff: true);
            return false;
        }
        finally
        {
            _progress.TryRemove(sha, out _);
            Changed?.Invoke();
        }
    }

    // ---- Download ------------------------------------------------------------------------------------

    public Task<bool> EnsureAvailableAsync(string sha256, long size, CancellationToken ct) => EnsureAsync(sha256, size, automatic: false, ct).ContinueWith(task => task.Result ?? false, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    /// <summary>
    /// Downloads the file unless it is here. Several callers for one file share one download. Returns true when the bytes arrived,
    /// null when they were already here, false when they could not be fetched.
    /// </summary>
    private async Task<bool?> EnsureAsync(string sha, long size, bool automatic, CancellationToken ct)
    {
        if (await files.ExistsAsync(sha, ct))
        {
            return null;
        }

        if (!await IsEnabledAsync(ct))
        {
            return false;
        }

        var current = await store.QueryAsync((session, _) => Task.FromResult(SyncSql.Scalar(((SqliteDataSession)session).Connection,
            "SELECT COALESCE((SELECT Remote FROM AttachmentLocalState WHERE Sha256 = '" + sha + "'), 0);")), ct);
        if (current == Blocked)
        {
            return false;
        }

        var task = _downloads.GetOrAdd(sha, _ => Task.Run(() => DownloadAsync(sha, size, ct), CancellationToken.None));
        try
        {
            return await task;
        }
        finally
        {
            _downloads.TryRemove(new KeyValuePair<string, Task<bool>>(sha, task));
        }
    }

    private async Task<bool> DownloadAsync(string sha, long size, CancellationToken ct)
    {
        var progress = _progress[sha] = new Progress(size);
        await SetBytesStateAsync(sha, AttachmentBytesState.Downloading, ct);
        Changed?.Invoke();
        try
        {
            await using var body = await transport.OpenDownloadAsync(sha, 0, ct);
            await using var counted = new CountingStream(body, progress);
            await files.PutAsync(counted, Math.Max(size, 1) + 1024, sha, ct);
            await SetBytesStateAsync(sha, AttachmentBytesState.Present, ct, remote: OnService);
            return true;
        }
        catch (BlobTransferException error) when (error.Kind == BlobFailureKind.Blocked)
        {
            await SetBytesStateAsync(sha, AttachmentBytesState.Missing, ct, remote: Blocked);
            return false;
        }
        catch (BlobTransferException error) when (error.Kind == BlobFailureKind.NotFound)
        {
            // The device that attached it has not uploaded it yet; look again later.
            await SetBytesStateAsync(sha, AttachmentBytesState.Missing, ct, backoff: true);
            return false;
        }
        catch (Exception error) when (error is BlobTransferException or ChaosServiceException or IOException or UnauthorizedAccessException or AppException)
        {
            await SetBytesStateAsync(sha, AttachmentBytesState.Failed, ct, backoff: true);
            if (error is ChaosServiceException { Kind: ServiceFailure.Offline or ServiceFailure.SignInAgain or ServiceFailure.DeviceRevoked or ServiceFailure.AccountDeleted })
            {
                throw;
            }

            return false;
        }
        finally
        {
            _progress.TryRemove(sha, out _);
            Changed?.Invoke();
        }
    }

    private sealed class CountingStream(Stream inner, Progress progress) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref progress.Done, read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- State ---------------------------------------------------------------------------------------

    public async Task<AttachmentTransferInfo> DescribeAsync(string sha256, bool bytesPresent, CancellationToken ct)
    {
        var (enabled, remote, bytes, failures) = await store.QueryAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            var on = SyncSql.Scalar(connection, "SELECT Enabled FROM SyncState WHERE Id = 1;") == 1;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Remote, State, Failures FROM AttachmentLocalState WHERE Sha256 = $sha;";
            command.Parameters.AddWithValue("$sha", sha256);
            using var reader = command.ExecuteReader();
            return Task.FromResult(reader.Read() ? (on, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)) : (on, 0, 0, 0));
        }, ct);

        if (!enabled)
        {
            return AttachmentTransferInfo.Idle;
        }

        _progress.TryGetValue(sha256, out var live);
        int? Percent() => live is { Total: > 0 } p ? (int)Math.Clamp(Interlocked.Read(ref p.Done) * 100 / p.Total, 0, 100) : null;

        if (remote == Blocked)
        {
            return new(AttachmentSyncState.Blocked, null, "Blocked by malware check");
        }

        if (!bytesPresent)
        {
            if (live is { Upload: false })
            {
                return new(AttachmentSyncState.Downloading, Percent(), $"Downloading {Percent() ?? 0}%");
            }

            return bytes == (int)AttachmentBytesState.Failed
                ? new(AttachmentSyncState.DownloadFailed, null, "Download failed — retry")
                : new(AttachmentSyncState.NotDownloaded, null, "Not downloaded yet");
        }

        if (live is { Upload: true })
        {
            return new(AttachmentSyncState.Uploading, Percent(), $"Uploading {Percent() ?? 0}%");
        }

        return remote switch
        {
            OverQuota => new(AttachmentSyncState.OverQuota, null, "Not uploaded — the online storage is full"),
            UploadFailed => new(AttachmentSyncState.UploadFailed, null, "Upload failed — retry"),
            OnService => AttachmentTransferInfo.Idle,
            _ => new(AttachmentSyncState.UploadPending, null, "Waiting to upload"),
        };
    }

    /// <summary>"Retry": forgets the back-off and failure for a file and runs a pass soon.</summary>
    public async Task RetryAsync(string sha256, CancellationToken ct)
    {
        await store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE AttachmentLocalState SET NextTryUtc = NULL, Failures = 0,
                    Remote = CASE WHEN Remote IN (3, 4) THEN 0 ELSE Remote END,
                    State = CASE WHEN State = 3 THEN 1 ELSE State END
                WHERE Sha256 = $sha;
                """;
            command.Parameters.AddWithValue("$sha", sha256);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
        Wake();
    }

    private Task SetRemoteAsync(string sha, int remote, CancellationToken ct, bool backoff = false, TimeSpan? retryAfter = null) =>
        UpsertAsync(sha, state: null, remote, backoff, retryAfter, ct);

    private Task SetBytesStateAsync(string sha, AttachmentBytesState state, CancellationToken ct, int? remote = null, bool backoff = false) =>
        UpsertAsync(sha, state, remote, backoff, null, ct);

    /// <summary>
    /// Writes the device-local row for a hash. A success clears the back-off; a failure sets one that doubles with each failure.
    /// </summary>
    private Task UpsertAsync(string sha, AttachmentBytesState? state, int? remote, bool backoff, TimeSpan? retryAfter, CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            var now = clock.GetUtcNow();
            long failures = 0;
            string? next = null;
            if (backoff || retryAfter is not null)
            {
                failures = SyncSql.Scalar(connection, "SELECT COALESCE((SELECT Failures FROM AttachmentLocalState WHERE Sha256 = '" + sha + "'), 0);") + 1;
                var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Min(_options.MaxBackoff.TotalSeconds, _options.MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 20))));
                next = StorageFormats.Instant(now + wait);
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AttachmentLocalState (Sha256, State, PinnedReason, UpdatedAtUtc, Remote, NextTryUtc, Failures)
                VALUES ($sha, COALESCE($state, 1), NULL, $now, COALESCE($remote, 0), $next, $failures)
                ON CONFLICT (Sha256) DO UPDATE SET
                    State = COALESCE($state, State),
                    Remote = COALESCE($remote, Remote),
                    NextTryUtc = $next,
                    Failures = $failures,
                    UpdatedAtUtc = $now;
                """;
            command.Parameters.AddWithValue("$sha", sha);
            command.Parameters.AddWithValue("$state", state is { } s ? (long)(int)s : DBNull.Value);
            command.Parameters.AddWithValue("$remote", remote is { } r ? (long)r : DBNull.Value);
            command.Parameters.AddWithValue("$next", (object?)next ?? DBNull.Value);
            command.Parameters.AddWithValue("$failures", failures);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);

    private Task RaiseAttentionAsync(string category, string key, string message, CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM SyncAttention WHERE Category = $category AND EntityKey = $key AND ResolvedAtUtc IS NULL LIMIT 1;";
                exists.Parameters.AddWithValue("$category", category);
                exists.Parameters.AddWithValue("$key", key);
                if (exists.ExecuteScalar() is not null)
                {
                    return Task.FromResult(0);
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SyncAttention (Id, Category, Kind, EntityKey, Message, DetailJson, CreatedAtUtc)
                VALUES ($id, $category, 'attachment', $key, $message, NULL, $now);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);

    // ---- Keep available offline (device-local, per container) -----------------------------------------

    public Task<IReadOnlyList<string>> OfflineContainersAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = "SELECT ContainerId FROM OfflineContainers ORDER BY ContainerId;";
            var ids = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }

            return Task.FromResult((IReadOnlyList<string>)ids);
        }, ct);

    /// <summary>Marks or unmarks a list or project as kept offline on this computer, and starts fetching what it holds.</summary>
    public async Task SetContainerOfflineAsync(string containerId, bool offline, CancellationToken ct)
    {
        await store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            using var command = connection.CreateCommand();
            command.CommandText = offline
                ? "INSERT INTO OfflineContainers (ContainerId, UpdatedAtUtc) SELECT Id, $now FROM Containers WHERE Id = $id ON CONFLICT (ContainerId) DO NOTHING;"
                : "DELETE FROM OfflineContainers WHERE ContainerId = $id;";
            command.Parameters.AddWithValue("$id", containerId);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
        Wake();
    }
}
