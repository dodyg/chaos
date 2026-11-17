using Chaos.Application.Activity;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record ActivityEntityDto(string Kind, string Key, string Op, string? Version);
public sealed record ActivityEntryDto(string Seq, string OperationId, string OccurredAtUtc, string Source, string Action, string Outcome, string? JobRunId, IReadOnlyList<ActivityEntityDto> Entities);
public sealed record ActivitySinceRequest(string After, int Limit);
/// <summary>Reset means the position was pruned (or never valid): reload everything, then continue from Head.</summary>
public sealed record ActivitySinceResponse(bool Ok, IReadOnlyList<ActivityEntryDto> Entries, string Head, bool Reset);
public sealed record ActivityHeadRequest();
public sealed record ActivityHeadResponse(bool Ok, string Head);
public sealed record ActivityJobDto(string Job, bool Running, string? RunningSinceUtc, string? LastRunId, string? LastStartedUtc, string? LastFinishedUtc, string? LastOutcome, string? LastError);
public sealed record ActivityJobsRequest();
public sealed record ActivityJobsResponse(bool Ok, IReadOnlyList<ActivityJobDto> Jobs);

/// <summary>Pushed after commit, coalesced to about 100 ms: the window then calls <c>activity.since</c> (PLAN2 §6.4).</summary>
public sealed record ActivityCommitted(string Seq);

/// <summary>Declares the push event so the generator emits a typed publisher and a frontend subscription.</summary>
public sealed class ActivityEvents
{
    [NeoRpcEvent("activity.committed")]
    public ActivityCommitted Committed => throw new NotSupportedException("Declaration only; publish through the generated event.");
}

/// <summary>The system event stream for the window: ordered reads of the activity log and job state (PLAN2 §6.4).</summary>
[NeoRpcService("activity", Version = 1)]
public sealed class ActivityRpcService(IActivityFeed feed)
{
    [NeoRpcMethod("since")]
    public async ValueTask<ActivitySinceResponse> SinceAsync(ActivitySinceRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("activity.since");
        var after = long.TryParse(request.After, out var parsed) && parsed >= 0 ? parsed : 0;
        var page = await feed.SinceAsync(after, request.Limit <= 0 ? 200 : request.Limit, ct);
        return new(true, [.. page.Entries.Select(entry => new ActivityEntryDto(entry.Seq.ToString(), entry.OperationId, entry.OccurredAtUtc, entry.Source, entry.Action,
            entry.Outcome, entry.JobRunId, [.. entry.Entities.Select(entity => new ActivityEntityDto(entity.Kind, entity.Key, entity.Op, entity.Version?.ToString()))]))],
            page.Head.ToString(), page.Reset);
    }

    [NeoRpcMethod("head")]
    public async ValueTask<ActivityHeadResponse> HeadAsync(ActivityHeadRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("activity.head");
        return new(true, (await feed.HeadAsync(ct)).ToString());
    }

    [NeoRpcMethod("jobs")]
    public async ValueTask<ActivityJobsResponse> JobsAsync(ActivityJobsRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("activity.jobs");
        var jobs = await feed.JobsAsync(ct);
        return new(true, [.. jobs.Select(job => new ActivityJobDto(job.Job, job.RunningSinceUtc is not null, job.RunningSinceUtc, job.LastRunId, job.LastStartedUtc, job.LastFinishedUtc, job.LastOutcome, job.LastError))]);
    }
}
