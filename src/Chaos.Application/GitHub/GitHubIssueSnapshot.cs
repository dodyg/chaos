using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Domain.GitHub;

namespace Chaos.Application.GitHub;

/// <summary>Versioned remote baseline. Arrays make the persisted representation independent of set implementations.</summary>
public sealed record GitHubIssueSnapshot(string Title, string Body, bool Open, string? Reason, long[] LabelIds, long[] AssigneeIds, long? MilestoneNumber, DateOnly? MilestoneDue)
{
    public IssueProjection Projection() => new(Title, Body, new(Open, Reason), LabelIds.ToHashSet(), AssigneeIds.ToHashSet());
    public static GitHubIssueSnapshot From(GitHubIssue issue) => new(issue.Title, issue.Body ?? "", issue.State == "open", issue.State == "closed" ? issue.StateReason ?? "completed" : issue.StateReason,
        issue.Labels.Select(l => l.Id).Order().ToArray(), issue.Assignees.Select(a => a.Id).Order().ToArray(), issue.Milestone?.Number,
        issue.Milestone?.DueOn is { } due ? DateOnly.FromDateTime(due.UtcDateTime) : null);
}

public static class GitHubIssueScope
{
    public static GitHubFilter Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("v", out var version) || version.GetInt32() != 1) throw AppException.Validation("filter", "Unsupported GitHub filter version.");
            var milestone = r.GetProperty("milestone");
            var result = new GitHubFilter(r.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).ToArray(), r.GetProperty("labelMode").GetString()!, r.GetProperty("assignee").GetString()!,
                milestone.ValueKind == JsonValueKind.Number ? milestone.GetInt64() : null, milestone.ValueKind == JsonValueKind.String && milestone.GetString() == "none", r.GetProperty("includeClosedDays").GetInt32());
            if (milestone.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null) && !(milestone.ValueKind == JsonValueKind.String && result.NoMilestone)) throw AppException.Validation("filter", "Invalid milestone filter.");
            result.ToJson();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw AppException.Validation("filter", "The GitHub subscription filter could not be read. Edit its settings."); }
    }

    public static bool Matches(GitHubFilter filter, GitHubIssue issue, long accountId, DateTimeOffset now, bool alreadyLinked)
    {
        if (issue.PullRequest is not null) return false;
        if (!alreadyLinked && issue.State != "open" && (filter.IncludeClosedDays == 0 || issue.ClosedAt is null || issue.ClosedAt < now.AddDays(-filter.IncludeClosedDays))) return false;
        if (filter.Labels.Count > 0)
        {
            var names = issue.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (filter.LabelMode == "all" ? filter.Labels.Any(l => !names.Contains(l)) : !filter.Labels.Any(names.Contains)) return false;
        }
        if (filter.Assignee == "me" && !issue.Assignees.Any(a => a.Id == accountId)) return false;
        if (filter.Assignee == "none" && issue.Assignees.Count != 0) return false;
        return (!filter.NoMilestone || issue.Milestone is null) && (filter.Milestone is null || issue.Milestone?.Number == filter.Milestone);
    }
}
