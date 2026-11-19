using System.Security.Claims;
using Chaos.Server.Blobs;
using Chaos.Server.Data;

namespace Chaos.Server.Web;

internal static class WebReadEndpoints
{
    public static RouteGroupBuilder MapWebReads(this RouteGroupBuilder web)
    {
        web.MapGet("/today", Today);
        web.MapGet("/appearance", Appearance);
        web.MapGet("/lists", Lists);
        web.MapGet("/pad", Pad);
        web.MapGet("/pages", Pages);
        web.MapGet("/attachments/{id:guid}", Attachment);
        web.MapGet("/search", Search);
        web.MapGet("/lists/{id:guid}/tasks", ListTasks);
        web.MapGet("/tasks/{id:guid}", TaskDetail);
        return web;
    }

    private static async Task<IResult> Appearance(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.AppearanceAsync(account, ct))
            : Results.Unauthorized();

    private static async Task<IResult> Today(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.TodayAsync(account, ct))
            : Results.Unauthorized();

    private static async Task<IResult> Lists(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.ListsAsync(account, ct))
            : Results.Unauthorized();

    private static async Task<IResult> Pad(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.PadAsync(account, ct)) : Results.Unauthorized();

    /// <summary>An image from a note, served inline to the signed-in web session. The session cookie is only sent under /v1/web, so the PWA loads images from here.</summary>
    private static async Task<IResult> Attachment(Guid id, ClaimsPrincipal user, WebReadModel model, BlobService blobs, CancellationToken ct)
    {
        if (await model.AccountAsync(WebCaller.From(user).AccountId, ct) is not { } account)
        {
            return Results.Unauthorized();
        }

        if (await model.ImageAttachmentAsync(account, id, ct) is not { } image
            || await blobs.FindAsync(account.Id, image.Sha256, ct) is not { State: BlobStates.Clean } blob)
        {
            return Results.NotFound();
        }

        return new BlobDownloadResult(blobs, blob, 0, Math.Max(blob.ByteSize - 1, 0), partial: false, image.MediaType, "inline");
    }

    private static async Task<IResult> Pages(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.PagesAsync(account, ct)) : Results.Unauthorized();

    private static async Task<IResult> Search(string? q, ClaimsPrincipal user, WebReadModel model, CancellationToken ct)
    {
        var account = await model.AccountAsync(WebCaller.From(user).AccountId, ct);
        if (account is null) return Results.Unauthorized();
        var query = q?.Trim() ?? "";
        if (query.Length > 200) return Results.BadRequest(new { Detail = "Search must be 200 characters or fewer." });
        return Results.Ok(query.Length == 0 ? new WebSearchResult([], false) : await model.SearchAsync(account, query, ct));
    }

    private static async Task<IResult> ListTasks(Guid id, ClaimsPrincipal user, WebReadModel model, CancellationToken ct)
    {
        var account = await model.AccountAsync(WebCaller.From(user).AccountId, ct);
        if (account is null)
        {
            return Results.Unauthorized();
        }

        return await model.ListAsync(account, id, ct) is { } list
            ? Results.Ok(new { List = list, Tasks = await model.TasksInListAsync(account, id, ct) })
            : Results.NotFound();
    }

    private static async Task<IResult> TaskDetail(Guid id, ClaimsPrincipal user, WebReadModel model, CancellationToken ct)
    {
        var account = await model.AccountAsync(WebCaller.From(user).AccountId, ct);
        if (account is null)
        {
            return Results.Unauthorized();
        }

        return await model.TaskDetailAsync(account, id, ct) is { } task ? Results.Ok(task) : Results.NotFound();
    }
}
