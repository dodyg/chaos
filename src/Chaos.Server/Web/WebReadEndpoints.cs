using System.Security.Claims;

namespace Chaos.Server.Web;

internal static class WebReadEndpoints
{
    public static RouteGroupBuilder MapWebReads(this RouteGroupBuilder web)
    {
        web.MapGet("/today", Today);
        web.MapGet("/lists", Lists);
        web.MapGet("/lists/{id:guid}/tasks", ListTasks);
        web.MapGet("/tasks/{id:guid}", TaskDetail);
        return web;
    }

    private static async Task<IResult> Today(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.TodayAsync(account, ct))
            : Results.Unauthorized();

    private static async Task<IResult> Lists(ClaimsPrincipal user, WebReadModel model, CancellationToken ct) =>
        await model.AccountAsync(WebCaller.From(user).AccountId, ct) is { } account
            ? Results.Ok(await model.ListsAsync(account, ct))
            : Results.Unauthorized();

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
