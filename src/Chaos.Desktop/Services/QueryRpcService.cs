using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>A built-in view plus temporary filters; dates are yyyy-MM-dd local dates. Day picks My Day's date (null = today).</summary>
public sealed record TaskQueryRequest(
    string View,
    string? ContainerId,
    string? TagId,
    string? Text,
    IReadOnlyList<string>? Statuses,
    IReadOnlyList<string>? Priorities,
    string? PlannedFrom,
    string? PlannedTo,
    string? DueFrom,
    string? DueTo,
    string? CompletedFrom,
    string? CompletedTo,
    bool IncludeCompleted,
    bool IncludeArchived,
    int? UpcomingDays,
    int? Limit,
    string? Cursor,
    string? Day = null);

public sealed record QueryRowResponse(TaskResponse Task, string ContainerName, bool ContainerArchived, IReadOnlyList<string> TagIds, IReadOnlyList<string> Matches, string? GroupDate,
    string? WaitingPerson, string? NextFollowUpAtUtc);
public sealed record TaskQueryResponse(bool Ok, IReadOnlyList<QueryRowResponse> Rows, int TotalCount, string? NextCursor, TaskErrorResponse? Error);
public sealed record ViewCountsRequest();
public sealed record ViewCountResponse(string View, int Count);
public sealed record TagCountResponse(string TagId, int Count);
public sealed record ViewCountsResponse(bool Ok, IReadOnlyList<ViewCountResponse> Views, IReadOnlyList<TagCountResponse> Tags, TaskErrorResponse? Error);
public sealed record RebuildSearchRequest();
public sealed record RebuildSearchResponse(bool Ok, int Documents, TaskErrorResponse? Error);

/// <summary>Built-in views, search and sidebar counts (PLAN §10, §15 WP3).</summary>
[NeoRpcService("queries", Version = 1)]
public sealed class QueryRpcService(TaskQueries queries)
{
    [NeoRpcMethod("run")]
    public async ValueTask<TaskQueryResponse> RunAsync(TaskQueryRequest request, CancellationToken ct)
    {
        try
        {
            var page = await queries.RunAsync(ToSpec(request), ct);
            return new(true, [.. page.Rows.Select(Map)], TagRpcService.Count(page.TotalCount), page.NextCursor, null);
        }
        catch (AppException error) { return new(false, [], 0, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("counts")]
    public async ValueTask<ViewCountsResponse> CountsAsync(ViewCountsRequest request, CancellationToken ct)
    {
        try
        {
            var counts = await queries.CountsAsync(ct);
            return new(true,
                [.. counts.Views.Select(entry => new ViewCountResponse(entry.Key.ToString(), TagRpcService.Count(entry.Value)))],
                [.. counts.Tags.Select(entry => new TagCountResponse(entry.Key, TagRpcService.Count(entry.Value)))],
                null);
        }
        catch (AppException error) { return new(false, [], [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("rebuildSearch")]
    public async ValueTask<RebuildSearchResponse> RebuildSearchAsync(RebuildSearchRequest request, CancellationToken ct)
    {
        try { return new(true, TagRpcService.Count(await queries.RebuildSearchIndexAsync(ct)), null); }
        catch (AppException error) { return new(false, 0, TaskRpcService.ToErrorResponse(error)); }
    }

    internal static TaskQuerySpec ToSpec(TaskQueryRequest request) => new()
    {
        View = ParseEnum<TaskView>(request.View, "view"),
        ContainerId = request.ContainerId,
        TagId = request.TagId,
        Text = request.Text,
        Statuses = [.. (request.Statuses ?? []).Select(value => ParseEnum<TaskItemStatus>(value, "statuses"))],
        Priorities = [.. (request.Priorities ?? []).Select(value => ParseEnum<TaskPriority>(value, "priorities"))],
        Planned = Range(request.PlannedFrom, request.PlannedTo, "planned"),
        Due = Range(request.DueFrom, request.DueTo, "due"),
        Completed = Range(request.CompletedFrom, request.CompletedTo, "completed"),
        IncludeCompleted = request.IncludeCompleted,
        IncludeArchived = request.IncludeArchived,
        UpcomingDays = request.UpcomingDays ?? 14,
        Day = Date(request.Day, "day"),
        Limit = request.Limit ?? TaskQueries.MaxLimit,
        Cursor = request.Cursor,
    };

    private static QueryRowResponse Map(TaskQueryRow row) => new(
        TaskRpcService.ToResponse(row.Task),
        row.ContainerName,
        row.ContainerArchived,
        row.TagIds,
        [.. row.Matches.Select(match => match.ToString())],
        row.GroupDate is { } date ? StorageFormats.Date(date) : null,
        row.WaitingPerson,
        row.NextFollowUpAtUtc is { } followUp ? StorageFormats.Instant(followUp) : null);

    private static LocalDateRange? Range(string? from, string? to, string field)
    {
        var start = Date(from, field);
        var end = Date(to, field);
        return start is null && end is null ? null : new LocalDateRange(start, end);
    }

    private static DateOnly? Date(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date
        : throw AppException.Validation(field, "Enter a valid date.");

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw AppException.Validation(field, "Choose a valid value.");
}
