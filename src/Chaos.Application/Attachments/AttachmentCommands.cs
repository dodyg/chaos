using System.Collections.Concurrent;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;

namespace Chaos.Application.Attachments;

public sealed record BeginAttachmentUploadCommand(AttachmentOwnerKind OwnerKind, string OwnerId, string FileName, long Size);

public sealed record AttachmentUploadStarted(string UploadId, int ChunkBytes, long MaxFileBytes);

public sealed record CommitAttachmentCommand(string OperationId, string UploadId);

public sealed record RenameAttachmentCommand(string OperationId, string AttachmentId, long ExpectedVersion, string FileName);

public sealed record AttachmentCommand(string OperationId, string AttachmentId, long ExpectedVersion);

public sealed record EditImageCommand(string OperationId, string AttachmentId, long ExpectedVersion, ImageEdit Edit);

public sealed record AttachmentResult(AttachmentRecord Attachment, bool Replayed);

/// <summary>An attachment with what the UI needs to show it: whether this device has the bytes and how to preview them.</summary>
public sealed record AttachmentView(AttachmentRecord Attachment, AttachmentBytesState Bytes, AttachmentPreview Preview, bool CanOpen, AttachmentTransferInfo? Transfer = null);

/// <summary>One slice of an attachment's bytes, for the renderer to assemble into a Blob.</summary>
public sealed record AttachmentChunk(long Offset, long TotalSize, byte[] Bytes);

/// <summary>
/// Attachments (PLAN2 §9.1). Adding a file is an upload session: <c>Begin</c>, any number of
/// sequential <c>Chunk</c>s of at most 1 MiB, then <c>Commit</c>, which stores the bytes in the
/// <see cref="IAttachmentFileStore"/>, sniffs the type and inserts the row in one transaction,
/// as a recorded operation (receipt, undo). Sessions live in memory with their bytes in a temp
/// folder, expire after an hour, and at most four run at once. No file path ever crosses the
/// renderer boundary. Which provider holds the bytes is invisible here.
/// </summary>
public sealed class AttachmentCommands
{
    private readonly IApplicationStore _store;
    private readonly IAttachmentFileStore _files;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;
    private readonly IUploadStaging _staging;
    private readonly AttachmentLimits _limits;
    private readonly ConcurrentDictionary<string, UploadSession> _uploads = new(StringComparer.Ordinal);
    private readonly IImageProcessor? _images;
    private readonly IThumbnailCache? _thumbnails;

    public const int ThumbnailSide = 640;

    /// <summary>Images at or above this size show a thumbnail inline; the full image opens on click.</summary>
    public const long ThumbnailThresholdBytes = 2L * 1024 * 1024;

    public AttachmentCommands(IApplicationStore store, IAttachmentFileStore files, IIdGenerator ids, TimeProvider clock, IUploadStaging staging, AttachmentLimits? limits = null,
        IImageProcessor? images = null, IThumbnailCache? thumbnails = null, IAttachmentAvailability? availability = null)
    {
        Availability = availability;
        _images = images;
        _thumbnails = thumbnails;
        _store = store;
        _files = files;
        _ids = ids;
        _clock = clock;
        _staging = staging;
        _limits = limits ?? AttachmentLimits.Default;
    }

    public AttachmentLimits Limits => _limits;

    public IAttachmentFileStore Files => _files;

    /// <summary>Set when sync can fetch bytes that are on the service but not here (PLAN2 §9.2); null means this device only has what it holds.</summary>
    public IAttachmentAvailability? Availability { get; }

    /// <summary>
    /// Opens a file's bytes. When they are not here and the online service may have them, fetches them first: opening a large file
    /// is what makes it download ("Downloading… 34%").
    /// </summary>
    private async Task<Stream> OpenBytesAsync(AttachmentRecord row, long offset, CancellationToken ct)
    {
        if (await _files.OpenReadAsync(row.Sha256, offset, ct) is { } here)
        {
            return here;
        }

        if (Availability is { } availability && await availability.EnsureAvailableAsync(row.Sha256, row.ByteSize, ct)
            && await _files.OpenReadAsync(row.Sha256, offset, ct) is { } fetched)
        {
            return fetched;
        }

        var info = Availability is { } describer ? await describer.DescribeAsync(row.Sha256, false, ct) : null;
        throw info is { State: AttachmentSyncState.Blocked }
            ? AppException.NotFound("This file was blocked by the malware check, so it is not available.")
            : info is { Message: { Length: > 0 } message } ? AppException.NotFound($"{message}. This file is not on this computer yet.") : BytesMissing();
    }

    // ---- Reads -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AttachmentView>> ListAsync(AttachmentOwnerKind ownerKind, string ownerId, CancellationToken ct = default)
    {
        var rows = await _store.QueryAsync(async (session, token) => await session.Attachments.ListByOwnerAsync(ownerKind, ownerId, includeDeleted: false, token), ct);
        var views = new List<AttachmentView>(rows.Count);
        foreach (var row in rows)
        {
            views.Add(await DescribeAsync(row, ct));
        }

        return views;
    }

    public async Task<AttachmentView> GetAsync(string attachmentId, CancellationToken ct = default)
    {
        var row = await _store.QueryAsync(async (session, token) => await session.Attachments.GetAsync(attachmentId, token), ct) ?? throw NotFound();
        return await DescribeAsync(row, ct);
    }

    /// <summary>Resolves attachments by ID for rendering in Markdown; unknown or deleted ones are simply absent.</summary>
    public async Task<IReadOnlyList<AttachmentView>> ResolveAsync(IReadOnlyList<string> attachmentIds, CancellationToken ct = default)
    {
        var rows = await _store.QueryAsync(async (session, token) =>
        {
            var found = new List<AttachmentRecord>();
            foreach (var id in attachmentIds.Distinct(StringComparer.Ordinal).Take(200))
            {
                if (await session.Attachments.GetAsync(id, token) is { } row)
                {
                    found.Add(row);
                }
            }

            return (IReadOnlyList<AttachmentRecord>)found;
        }, ct);
        var views = new List<AttachmentView>(rows.Count);
        foreach (var row in rows)
        {
            views.Add(await DescribeAsync(row, ct));
        }

        return views;
    }

    /// <summary>One slice of the bytes (at most one upload chunk), for the renderer to build a Blob from.</summary>
    public async Task<AttachmentChunk> ReadAsync(string attachmentId, long offset, int length, CancellationToken ct = default)
    {
        var row = await _store.QueryAsync(async (session, token) => await session.Attachments.GetAsync(attachmentId, token), ct) ?? throw NotFound();
        if (offset < 0 || offset > row.ByteSize)
        {
            throw AppException.Validation("offset", "That position is outside the file.");
        }

        var count = (int)Math.Min(Math.Clamp(length, 1, AttachmentLimits.UploadChunkBytes), row.ByteSize - offset);
        await using var stream = await OpenBytesAsync(row, offset, ct);
        var buffer = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, count - filled), ct);
            if (read == 0)
            {
                throw BytesMissing();
            }

            filled += read;
        }

        return new AttachmentChunk(offset, row.ByteSize, buffer);
    }

    /// <summary>Opens the stored bytes for a host action (save a copy, open in another app).</summary>
    public async Task<(AttachmentRecord Attachment, Stream Content)> OpenAsync(string attachmentId, CancellationToken ct = default)
    {
        var row = await _store.QueryAsync(async (session, token) => await session.Attachments.GetAsync(attachmentId, token), ct) ?? throw NotFound();
        return (row, await OpenBytesAsync(row, 0, ct));
    }

    /// <summary>The image attachment of a page with this display name, for page backgrounds; null when there is none.</summary>
    public async Task<AttachmentView?> FindPageImageAsync(string pageId, string name, CancellationToken ct = default)
    {
        var row = await _store.QueryAsync(async (session, token) =>
            (await session.Attachments.ListByOwnerAsync(AttachmentOwnerKind.Page, pageId, includeDeleted: false, token))
                .FirstOrDefault(item => string.Equals(item.FileName, name, StringComparison.OrdinalIgnoreCase)
                    && AttachmentSafety.PreviewOf(item.MediaType) == AttachmentPreview.Image), ct);
        return row is null ? null : await DescribeAsync(row, ct);
    }

    // ---- Upload sessions ---------------------------------------------------------------------

    public async Task<AttachmentUploadStarted> BeginAsync(BeginAttachmentUploadCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.Size < 0)
        {
            throw AppException.Validation("size", "The file size is not valid.");
        }

        if (command.Size > _limits.MaxFileBytes)
        {
            throw AppException.Validation("size", $"This file is larger than the {_limits.MaxFileBytes / (1024 * 1024)} MB one file may be.");
        }

        var fileName = AttachmentSafety.SanitizeFileName(command.FileName);
        await _store.QueryAsync(async (session, token) =>
        {
            await RequireOwnerAsync(session, command.OwnerKind, command.OwnerId, token);
            return true;
        }, ct);

        ExpireOld();
        if (_uploads.Count >= AttachmentLimits.MaxConcurrentUploads)
        {
            throw AppException.Validation("upload", $"Only {AttachmentLimits.MaxConcurrentUploads} files can be added at once. Wait for one to finish.");
        }

        var uploadId = _ids.NewId().ToString("N");
        var path = await _staging.CreateAsync(uploadId, ct);

        var upload = new UploadSession(uploadId, command.OwnerKind, command.OwnerId, command.FileName, fileName, command.Size, path, _clock.GetUtcNow());
        if (!_uploads.TryAdd(uploadId, upload) || _uploads.Count > AttachmentLimits.MaxConcurrentUploads)
        {
            Discard(uploadId);
            throw AppException.Validation("upload", $"Only {AttachmentLimits.MaxConcurrentUploads} files can be added at once. Wait for one to finish.");
        }

        return new AttachmentUploadStarted(uploadId, AttachmentLimits.UploadChunkBytes, _limits.MaxFileBytes);
    }

    public async Task ChunkAsync(string uploadId, int index, byte[] bytes, CancellationToken ct = default)
    {
        var upload = Session(uploadId);
        if (bytes.Length > AttachmentLimits.UploadChunkBytes)
        {
            throw AppException.Validation("chunk", "A chunk is at most 1 MiB.");
        }

        await upload.Gate.WaitAsync(ct);
        try
        {
            if (index < upload.NextIndex)
            {
                return; // a retry of a chunk that already arrived
            }

            if (index != upload.NextIndex)
            {
                throw AppException.Validation("index", "Chunks must arrive in order.");
            }

            if (upload.Received + bytes.Length > upload.DeclaredSize)
            {
                throw AppException.Validation("chunk", "More data arrived than the file's size said.");
            }

            await _staging.AppendAsync(upload.TempPath, bytes, ct);
            upload.Received += bytes.Length;
            upload.NextIndex++;
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    public void Cancel(string uploadId) => Discard(uploadId);

    /// <summary>
    /// Stores the bytes, sniffs the type and adds the attachment, replaying a repeat of the same
    /// operation. The row and its undo entry are one transaction; the bytes are stored first, and
    /// removed again when the row cannot be added and nothing else uses them.
    /// </summary>
    public async Task<AttachmentResult> CommitAsync(CommitAttachmentCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _uploads.TryGetValue(command.UploadId, out var upload);
        if (upload is not null && Expired(upload))
        {
            Discard(upload.Id);
            upload = null;
        }

        StoredBlob? stored = null;
        string mediaType = MediaSniffer.Octet;
        ImageSize? size = null;
        if (upload is not null)
        {
            if (upload.Received != upload.DeclaredSize)
            {
                throw AppException.Validation("upload", "The file did not arrive completely. Try adding it again.");
            }

            await using (var input = await _staging.OpenReadAsync(upload.TempPath, ct))
            {
                var head = new byte[(int)Math.Min(input.Length, MediaSniffer.HeadBytes)];
                await input.ReadExactlyAsync(head, ct);
                mediaType = MediaSniffer.Sniff(head, upload.FileName);
                size = MediaSniffer.TryReadSize(mediaType, head);
                input.Position = 0;
                stored = await _files.PutAsync(input, _limits.MaxFileBytes, null, ct);
            }
        }

        try
        {
            var result = await InWriteAsync(async (session, profile, now, token) =>
            {
                var (value, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "attachment.add", command, now, async inner =>
                {
                    if (upload is null || stored is null)
                    {
                        throw AppException.NotFound("This upload expired. Add the file again.");
                    }

                    var spaceId = await RequireOwnerSpaceAsync(session, upload.OwnerKind, upload.OwnerId, inner);
                    var row = new AttachmentRecord
                    {
                        Id = _ids.NewId().ToString(),
                        SpaceId = spaceId,
                        OwnerKind = upload.OwnerKind,
                        OwnerId = upload.OwnerId,
                        FileName = upload.FileName,
                        OriginalFileName = upload.OriginalFileName.Length > 500 ? upload.OriginalFileName[..500] : upload.OriginalFileName,
                        MediaType = mediaType,
                        ByteSize = stored.Size,
                        Sha256 = stored.Sha256,
                        Width = size?.Width,
                        Height = size?.Height,
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Attachments.AddAsync(row, inner);
                    await session.Attachments.SetLocalStateAsync(new AttachmentLocalStateRecord(stored.Sha256, AttachmentBytesState.Present, null, now), inner);
                    return new AttachmentResult(row, Replayed: false);
                }, token);
                return replayed ? value with { Replayed = true } : value;
            }, ct);

            if (upload is not null)
            {
                Discard(upload.Id);
            }

            return result;
        }
        catch when (stored is { AlreadyPresent: false })
        {
            // The row did not go in: do not leave bytes nothing names (a rare race with another upload of the same file aside).
            if (!await _store.QueryAsync(async (session, token) => await session.Attachments.IsHashReferencedAsync(stored.Sha256, token), CancellationToken.None))
            {
                await _files.DeleteAsync(stored.Sha256, CancellationToken.None);
            }

            throw;
        }
    }

    // ---- Changes -----------------------------------------------------------------------------

    public async Task<AttachmentResult> RenameAsync(RenameAttachmentCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = AttachmentSafety.SanitizeFileName(command.FileName, "");
        if (name.Length == 0)
        {
            throw AppException.Validation("fileName", "Give the file a name.");
        }

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (value, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "attachment.rename", command, now, async inner =>
            {
                var row = await session.Attachments.GetAsync(command.AttachmentId, inner) ?? throw NotFound();
                RequireVersion(row, command.ExpectedVersion);
                var updated = await session.Attachments.UpdateAsync(row with { FileName = name, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner) ?? throw Conflict();
                return new AttachmentResult(updated, Replayed: false);
            }, token);
            return replayed ? value with { Replayed = true } : value;
        }, ct);
    }

    /// <summary>Removes an attachment from its owner (restorable). Markdown that referenced it keeps the link, which then shows as a missing file.</summary>
    public async Task<AttachmentResult> DeleteAsync(AttachmentCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (value, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "attachment.delete", command, now, async inner =>
            {
                var row = await session.Attachments.GetIncludingDeletedAsync(command.AttachmentId, inner) ?? throw NotFound();
                RequireVersion(row, command.ExpectedVersion);
                if (row.DeletedAtUtc is not null)
                {
                    return new AttachmentResult(row, Replayed: false);
                }

                var updated = await session.Attachments.UpdateAsync(row with { DeletedAtUtc = now, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner) ?? throw Conflict();
                return new AttachmentResult(updated, Replayed: false);
            }, token);
            return replayed ? value with { Replayed = true } : value;
        }, ct);
    }

    public async Task<AttachmentResult> RestoreAsync(AttachmentCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (value, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "attachment.restore", command, now, async inner =>
            {
                var row = await session.Attachments.GetIncludingDeletedAsync(command.AttachmentId, inner) ?? throw NotFound();
                RequireVersion(row, command.ExpectedVersion);
                if (row.DeletedAtUtc is null)
                {
                    return new AttachmentResult(row, Replayed: false);
                }

                await RequireOwnerAsync(session, row.OwnerKind, row.OwnerId, inner);
                var updated = await session.Attachments.UpdateAsync(row with { DeletedAtUtc = null, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner) ?? throw Conflict();
                return new AttachmentResult(updated, Replayed: false);
            }, token);
            return replayed ? value with { Replayed = true } : value;
        }, ct);
    }

    /// <summary>
    /// Flips, rotates, crops and/or shrinks an image attachment. The edited bytes replace the
    /// attachment file (a new hash, so the original bytes are not touched); undo puts the old file back.
    /// </summary>
    public async Task<AttachmentResult> EditImageAsync(EditImageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var images = _images ?? throw new AppException(AppErrorKind.UnsupportedCapability, "Image editing is not available.");
        var edit = command.Edit;
        if (edit.RotateClockwise is not (0 or 90 or 180 or 270))
        {
            throw AppException.Validation("rotate", "Rotate by 0, 90, 180 or 270 degrees.");
        }

        if (edit.MaxSide is < 16 or > 16384)
        {
            throw AppException.Validation("maxSide", "The longest side must be between 16 and 16384 pixels.");
        }

        if (edit.IsNoOp)
        {
            throw AppException.Validation("edit", "Nothing to change.");
        }

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (value, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "attachment.edit", command, now, async inner =>
            {
                var row = await session.Attachments.GetAsync(command.AttachmentId, inner) ?? throw NotFound();
                RequireVersion(row, command.ExpectedVersion);
                if (row.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                {
                    throw AppException.Validation("image", "Only PNG, JPEG, GIF and WebP images can be edited.");
                }

                await using var source = await OpenBytesAsync(row, 0, inner);
                var edited = await images.EditAsync(source, row.MediaType, edit, inner);
                using var content = new MemoryStream(edited.Bytes, writable: false);
                var stored = await _files.PutAsync(content, _limits.MaxFileBytes, null, inner);
                var name = edited.MediaType == row.MediaType ? row.FileName : Path.ChangeExtension(row.FileName, ".png");
                var updated = await session.Attachments.UpdateAsync(row with
                {
                    FileName = name, MediaType = edited.MediaType, ByteSize = stored.Size, Sha256 = stored.Sha256,
                    Width = edited.Width, Height = edited.Height, Version = row.Version + 1, UpdatedAtUtc = now,
                }, row.Version, inner) ?? throw Conflict();
                await session.Attachments.SetLocalStateAsync(new AttachmentLocalStateRecord(stored.Sha256, AttachmentBytesState.Present, null, now), inner);
                return new AttachmentResult(updated, Replayed: false);
            }, token);
            return replayed ? value with { Replayed = true } : value;
        }, ct);
    }

    /// <summary>A small JPEG of an image attachment, made once and kept as a local file (not synced, rebuilt on demand).</summary>
    public async Task<byte[]> ThumbnailAsync(string attachmentId, CancellationToken ct = default)
    {
        var images = _images ?? throw new AppException(AppErrorKind.UnsupportedCapability, "Thumbnails are not available.");
        var row = await _store.QueryAsync(async (session, token) => await session.Attachments.GetAsync(attachmentId, token), ct) ?? throw NotFound();
        if (AttachmentSafety.PreviewOf(row.MediaType) != AttachmentPreview.Image)
        {
            throw AppException.Validation("image", "Only images have thumbnails.");
        }

        if (_thumbnails is not null && await _thumbnails.TryGetAsync(row.Sha256, ThumbnailSide, ct) is { } cached)
        {
            return cached;
        }

        await using var source = await OpenBytesAsync(row, 0, ct);
        var made = await images.ThumbnailAsync(source, ThumbnailSide, ct);
        if (_thumbnails is not null)
        {
            await _thumbnails.PutAsync(row.Sha256, ThumbnailSide, made.Bytes, ct);
        }

        return made.Bytes;
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>The row with what the UI needs to show it (bytes present here, preview kind, whether it may open).</summary>
    public async Task<AttachmentView> DescribeAsync(AttachmentRecord row, CancellationToken ct = default)
    {
        var present = await _files.ExistsAsync(row.Sha256, ct);
        var transfer = Availability is { } availability ? await availability.DescribeAsync(row.Sha256, present, ct) : null;
        var bytes = present ? AttachmentBytesState.Present
            : transfer?.State == AttachmentSyncState.Downloading ? AttachmentBytesState.Downloading
            : transfer?.State == AttachmentSyncState.DownloadFailed ? AttachmentBytesState.Failed
            : AttachmentBytesState.Missing;
        return new AttachmentView(row, bytes, AttachmentSafety.PreviewOf(row.MediaType, row.FileName), !AttachmentSafety.IsBlocked(row.FileName), transfer);
    }

    private static async Task RequireOwnerAsync(IDataSession session, AttachmentOwnerKind kind, string ownerId, CancellationToken token)
    {
        var exists = kind switch
        {
            AttachmentOwnerKind.Task => await session.Tasks.GetAsync(ownerId, token) is not null,
            AttachmentOwnerKind.Diary => await session.Diary.GetAsync(ownerId, token) is not null,
            AttachmentOwnerKind.Pad => await session.Pad.GetAsync(ownerId, token) is not null,
            AttachmentOwnerKind.Page => await session.Pages.GetAsync(ownerId, token) is not null,
            _ => false,
        };
        if (!exists)
        {
            throw AppException.NotFound("The item this file belongs to no longer exists.");
        }
    }

    private static async Task<string> RequireOwnerSpaceAsync(IDataSession session, AttachmentOwnerKind kind, string ownerId, CancellationToken token)
    {
        string? spaceId;
        switch (kind)
        {
            case AttachmentOwnerKind.Task:
                spaceId = (await session.Tasks.GetAsync(ownerId, token))?.SpaceId;
                break;
            case AttachmentOwnerKind.Diary:
                var diary = await session.Diary.GetAsync(ownerId, token);
                spaceId = diary is null ? null : (await session.Tasks.GetAsync(diary.TaskId, token))?.SpaceId;
                break;
            case AttachmentOwnerKind.Pad:
                spaceId = (await session.Pad.GetAsync(ownerId, token))?.SpaceId;
                break;
            case AttachmentOwnerKind.Page:
                spaceId = (await session.Pages.GetAsync(ownerId, token))?.SpaceId;
                break;
            default:
                spaceId = null;
                break;
        }
        return spaceId ?? throw AppException.NotFound("The item this file belongs to no longer exists.");
    }

    private UploadSession Session(string uploadId)
    {
        ExpireOld();
        return _uploads.TryGetValue(uploadId, out var upload)
            ? upload
            : throw AppException.NotFound("This upload expired. Add the file again.");
    }

    private bool Expired(UploadSession upload) => _clock.GetUtcNow() - upload.StartedAtUtc > AttachmentLimits.UploadLifetime;

    private void ExpireOld()
    {
        foreach (var upload in _uploads.Values.Where(Expired).ToList())
        {
            Discard(upload.Id);
        }
    }

    private void Discard(string uploadId)
    {
        if (_uploads.TryRemove(uploadId, out var upload))
        {
            _staging.Discard(upload.TempPath);
        }
    }

    private static void RequireVersion(AttachmentRecord row, long expected)
    {
        if (row.Version != expected)
        {
            throw Conflict();
        }
    }

    private static AppException NotFound() => AppException.NotFound("That file no longer exists. Refresh and try again.");

    private static AppException Conflict() => AppException.VersionConflict("This file changed while you were working. Refresh and try again.");

    private static AppException BytesMissing() => AppException.NotFound("This file is not on this computer.");

    private Task<T> InWriteAsync<T>(Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body, CancellationToken ct) =>
        TaskMutationSupport.InWriteTransactionAsync(_store, _clock, _ids, body, ct);

    private sealed class UploadSession(string id, AttachmentOwnerKind ownerKind, string ownerId, string originalFileName, string fileName, long declaredSize, string tempPath, DateTimeOffset startedAtUtc)
    {
        public string Id { get; } = id;
        public AttachmentOwnerKind OwnerKind { get; } = ownerKind;
        public string OwnerId { get; } = ownerId;
        public string OriginalFileName { get; } = originalFileName;
        public string FileName { get; } = fileName;
        public long DeclaredSize { get; } = declaredSize;
        public string TempPath { get; } = tempPath;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public int NextIndex { get; set; }
        public long Received { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
