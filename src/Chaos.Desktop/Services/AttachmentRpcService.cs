using System.Diagnostics;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Attachments;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Attachments;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record AttachmentDto(string Id, string OwnerKind, string OwnerId, string FileName, string MediaType, string ByteSize, string Sha256,
    int? Width, int? Height, string Version, string CreatedAtUtc, string Bytes, string Preview, bool CanOpen,
    string Sync, int? Percent, string? Note);
public sealed record AttachmentListRequest(string OwnerKind, string OwnerId);
public sealed record AttachmentResolveRequest(IReadOnlyList<string> Ids);
public sealed record AttachmentListResponse(bool Ok, IReadOnlyList<AttachmentDto> Attachments, TaskErrorResponse? Error);
public sealed record AttachmentBeginRequest(string OwnerKind, string OwnerId, string FileName, string Size);
public sealed record AttachmentBeginResponse(bool Ok, string UploadId, int ChunkBytes, string MaxFileBytes, TaskErrorResponse? Error);
public sealed record AttachmentChunkRequest(string UploadId, int Index, string Base64);
public sealed record AttachmentCommitRequest(string OperationId, string UploadId);
public sealed record AttachmentCancelRequest(string UploadId);
public sealed record AttachmentOperationResponse(bool Ok, AttachmentDto? Attachment, TaskErrorResponse? Error);
public sealed record AttachmentSimpleResponse(bool Ok, TaskErrorResponse? Error);
public sealed record AttachmentRenameRequest(string OperationId, string AttachmentId, string ExpectedVersion, string FileName);
public sealed record AttachmentItemRequest(string OperationId, string AttachmentId, string ExpectedVersion);
public sealed record AttachmentReadRequest(string AttachmentId, string Offset, int Length);
public sealed record AttachmentReadResponse(bool Ok, string Base64, string TotalSize, TaskErrorResponse? Error);
public sealed record AttachmentActionRequest(string AttachmentId);
public sealed record AttachmentActionResponse(bool Ok, string? Path, TaskErrorResponse? Error);
public sealed record AttachmentPageImageRequest(string PageId, string Name);
public sealed record AttachmentCropDto(int X, int Y, int Width, int Height);
public sealed record AttachmentEditImageRequest(string OperationId, string AttachmentId, string ExpectedVersion, bool FlipHorizontal, bool FlipVertical,
    int RotateClockwise, AttachmentCropDto? Crop, int? MaxSide);
public sealed record AttachmentRetryRequest(string AttachmentId);
public sealed record AttachmentOfflineRequest(string ContainerId, bool Offline);
public sealed record AttachmentOfflineListRequest();
public sealed record AttachmentOfflineResponse(bool Ok, IReadOnlyList<string> ContainerIds, TaskErrorResponse? Error);
public sealed record AttachmentThumbnailRequest(string AttachmentId);

/// <summary>
/// Attachments (PLAN2 §9.1). Files go in as chunks of at most 1 MiB and come out the same way;
/// no filesystem path ever crosses RPC, in either direction (the paths the actions below return
/// are for showing the person where a copy was saved, never for the renderer to read). "Open"
/// copies the file to a per-attachment folder and opens it through the shell only when its name
/// is not on the blocked list; "Show in folder" and "Save a copy" work for every file.
/// </summary>
[NeoRpcService("attachment", Version = 1)]
public sealed class AttachmentRpcService(AttachmentCommands attachments, string openedDirectory, Func<string> exportDirectory, Func<string, bool>? shellOpen = null, Func<string, bool>? shellSelect = null, AttachmentTransfer? transfer = null)
{
    public static string DefaultOpenedDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chaos", "opened");

    [NeoRpcMethod("list")]
    public async ValueTask<AttachmentListResponse> ListAsync(AttachmentListRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.list");
        try { return new(true, [.. (await attachments.ListAsync(Kind(request.OwnerKind), request.OwnerId, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>"Retry" on a file that failed to upload or download (PLAN2 §9.2).</summary>
    [NeoRpcMethod("retry")]
    public async ValueTask<AttachmentSimpleResponse> RetryAsync(AttachmentRetryRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.retry");
        try
        {
            var view = await attachments.GetAsync(request.AttachmentId, ct);
            if (transfer is not null)
            {
                await transfer.RetryAsync(view.Attachment.Sha256, ct);
            }

            return new(true, null);
        }
        catch (AppException error) { return new(false, TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>"Keep available offline" for a list or project on this computer; answers with every container kept offline.</summary>
    [NeoRpcMethod("setOffline")]
    public async ValueTask<AttachmentOfflineResponse> SetOfflineAsync(AttachmentOfflineRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.setOffline");
        if (transfer is null)
        {
            return new(true, [], null);
        }

        await transfer.SetContainerOfflineAsync(request.ContainerId, request.Offline, ct);
        return new(true, await transfer.OfflineContainersAsync(ct), null);
    }

    [NeoRpcMethod("offlineContainers")]
    public async ValueTask<AttachmentOfflineResponse> OfflineContainersAsync(AttachmentOfflineListRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.offlineContainers");
        return new(true, transfer is null ? [] : await transfer.OfflineContainersAsync(ct), null);
    }

    [NeoRpcMethod("resolve")]
    public async ValueTask<AttachmentListResponse> ResolveAsync(AttachmentResolveRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.resolve");
        try { return new(true, [.. (await attachments.ResolveAsync(request.Ids, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("pageImage")]
    public async ValueTask<AttachmentOperationResponse> PageImageAsync(AttachmentPageImageRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.pageImage");
        try { return new(true, await attachments.FindPageImageAsync(request.PageId, request.Name, ct) is { } found ? Map(found) : null, null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("begin")]
    public async ValueTask<AttachmentBeginResponse> BeginAsync(AttachmentBeginRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.begin");
        try
        {
            var started = await attachments.BeginAsync(new BeginAttachmentUploadCommand(Kind(request.OwnerKind), request.OwnerId, request.FileName, Long(request.Size, "size")), ct);
            return new(true, started.UploadId, started.ChunkBytes, started.MaxFileBytes.ToString(CultureInfo.InvariantCulture), null);
        }
        catch (AppException error) { return new(false, "", 0, "0", TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("chunk")]
    public async ValueTask<AttachmentSimpleResponse> ChunkAsync(AttachmentChunkRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.chunk");
        try
        {
            await attachments.ChunkAsync(request.UploadId, request.Index, Decode(request.Base64), ct);
            return new(true, null);
        }
        catch (AppException error) { return new(false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("commit")]
    public async ValueTask<AttachmentOperationResponse> CommitAsync(AttachmentCommitRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.commit");
        try
        {
            var result = await attachments.CommitAsync(new CommitAttachmentCommand(request.OperationId, request.UploadId), ct);
            return new(true, Map(await attachments.GetAsync(result.Attachment.Id, ct)), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("cancel")]
    public ValueTask<AttachmentSimpleResponse> CancelAsync(AttachmentCancelRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.cancel");
        attachments.Cancel(request.UploadId);
        return ValueTask.FromResult(new AttachmentSimpleResponse(true, null));
    }

    [NeoRpcMethod("rename")]
    public async ValueTask<AttachmentOperationResponse> RenameAsync(AttachmentRenameRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.rename");
        return await ChangeAsync(async () => await attachments.RenameAsync(new RenameAttachmentCommand(request.OperationId, request.AttachmentId, HistoryRpc.Version(request.ExpectedVersion), request.FileName), ct), ct);
    }

    [NeoRpcMethod("delete")]
    public async ValueTask<AttachmentOperationResponse> DeleteAsync(AttachmentItemRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.delete");
        return await ChangeAsync(async () => await attachments.DeleteAsync(new AttachmentCommand(request.OperationId, request.AttachmentId, HistoryRpc.Version(request.ExpectedVersion)), ct), ct);
    }

    [NeoRpcMethod("restore")]
    public async ValueTask<AttachmentOperationResponse> RestoreAsync(AttachmentItemRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.restore");
        return await ChangeAsync(async () => await attachments.RestoreAsync(new AttachmentCommand(request.OperationId, request.AttachmentId, HistoryRpc.Version(request.ExpectedVersion)), ct), ct);
    }

    /// <summary>Flips, rotates, crops and/or shrinks an image attachment; the edited image replaces it and Undo brings the old one back.</summary>
    [NeoRpcMethod("editImage")]
    public async ValueTask<AttachmentOperationResponse> EditImageAsync(AttachmentEditImageRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.editImage");
        var edit = new ImageEdit(request.FlipHorizontal, request.FlipVertical, request.RotateClockwise,
            request.Crop is { } crop ? new ImageCrop(crop.X, crop.Y, crop.Width, crop.Height) : null, request.MaxSide);
        return await ChangeAsync(async () => await attachments.EditImageAsync(new EditImageCommand(request.OperationId, request.AttachmentId, HistoryRpc.Version(request.ExpectedVersion), edit), ct), ct);
    }

    /// <summary>A small JPEG (base64) of an image, for showing large images inline without reading the whole file.</summary>
    [NeoRpcMethod("thumbnail")]
    public async ValueTask<AttachmentReadResponse> ThumbnailAsync(AttachmentThumbnailRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.thumbnail");
        try
        {
            var bytes = await attachments.ThumbnailAsync(request.AttachmentId, ct);
            return new(true, Convert.ToBase64String(bytes), bytes.Length.ToString(CultureInfo.InvariantCulture), null);
        }
        catch (AppException error) { return new(false, "", "0", TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>One slice of the bytes as base64; the renderer assembles them into a Blob and an object URL.</summary>
    [NeoRpcMethod("read")]
    public async ValueTask<AttachmentReadResponse> ReadAsync(AttachmentReadRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.read");
        try
        {
            var chunk = await attachments.ReadAsync(request.AttachmentId, Long(request.Offset, "offset"), request.Length, ct);
            return new(true, Convert.ToBase64String(chunk.Bytes), chunk.TotalSize.ToString(CultureInfo.InvariantCulture), null);
        }
        catch (AppException error) { return new(false, "", "0", TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>Opens the file in its own app, after a click. Files that can run code are refused.</summary>
    [NeoRpcMethod("open")]
    public async ValueTask<AttachmentActionResponse> OpenAsync(AttachmentActionRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.open");
        try
        {
            var item = (await attachments.GetAsync(request.AttachmentId, ct)).Attachment;
            if (AttachmentSafety.IsBlocked(item.FileName))
            {
                throw AppException.Validation("fileName", "Chaos does not open files that can run code. Use Show in folder or Save a copy instead.");
            }

            var path = await CopyToOpenedAsync(item, ct);
            return (shellOpen ?? OpenWithShell)(path)
                ? new(true, null, null)
                : new(false, null, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.UnsupportedCapability, "No app on this computer could open this file.")));
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("showInFolder")]
    public async ValueTask<AttachmentActionResponse> ShowInFolderAsync(AttachmentActionRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.showInFolder");
        try
        {
            var path = await CopyToOpenedAsync((await attachments.GetAsync(request.AttachmentId, ct)).Attachment, ct);
            return (shellSelect ?? SelectWithExplorer)(path)
                ? new(true, null, null)
                : new(false, null, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.UnsupportedCapability, "The folder could not be opened.")));
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>Writes a copy to the export folder under a name that does not replace anything there.</summary>
    [NeoRpcMethod("saveCopy")]
    public async ValueTask<AttachmentActionResponse> SaveCopyAsync(AttachmentActionRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("attachment.saveCopy");
        try
        {
            var (item, content) = await attachments.OpenAsync(request.AttachmentId, ct);
            await using (content)
            {
                var folder = exportDirectory();
                Directory.CreateDirectory(folder);
                var target = UniquePath(folder, AttachmentSafety.SanitizeFileName(item.FileName));
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await content.CopyToAsync(output, ct);
                return new(true, target, null);
            }
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, null, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.StorageUnavailable, $"The copy could not be saved: {error.Message}", innerException: error)));
        }
    }

    private async Task<string> CopyToOpenedAsync(AttachmentRecord item, CancellationToken ct)
    {
        var (_, content) = await attachments.OpenAsync(item.Id, ct);
        await using (content)
        {
            var folder = Path.Combine(openedDirectory, item.Id);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, AttachmentSafety.SanitizeFileName(item.FileName));
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await content.CopyToAsync(output, ct);
            return path;
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        }

        return path;
    }

    private static bool OpenWithShell(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool SelectWithExplorer(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private async ValueTask<AttachmentOperationResponse> ChangeAsync(Func<Task<AttachmentResult>> action, CancellationToken ct)
    {
        try
        {
            var result = await action();
            return new(true, Map(await attachments.DescribeAsync(result.Attachment, ct)), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    private static AttachmentDto Map(AttachmentView view) => new(
        view.Attachment.Id,
        view.Attachment.OwnerKind.ToString().ToLowerInvariant(),
        view.Attachment.OwnerId,
        view.Attachment.FileName,
        view.Attachment.MediaType,
        view.Attachment.ByteSize.ToString(CultureInfo.InvariantCulture),
        view.Attachment.Sha256,
        view.Attachment.Width,
        view.Attachment.Height,
        view.Attachment.Version.ToString(CultureInfo.InvariantCulture),
        StorageFormats.Instant(view.Attachment.CreatedAtUtc),
        view.Bytes.ToString().ToLowerInvariant(),
        view.Preview.ToString().ToLowerInvariant(),
        view.CanOpen,
        (view.Transfer?.State ?? AttachmentSyncState.None).ToString().ToLowerInvariant(),
        view.Transfer?.Percent,
        view.Transfer?.Message);

    private static AttachmentOwnerKind Kind(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "task" => AttachmentOwnerKind.Task,
        "diary" => AttachmentOwnerKind.Diary,
        "pad" => AttachmentOwnerKind.Pad,
        "page" => AttachmentOwnerKind.Page,
        _ => throw AppException.Validation("ownerKind", "Attachments belong to a task, diary entry, pad note or page."),
    };

    private static long Long(string? text, string field) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw AppException.Validation(field, "That number is not valid.");

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw AppException.Validation("chunk", "That chunk is not valid.");
        }
    }
}
