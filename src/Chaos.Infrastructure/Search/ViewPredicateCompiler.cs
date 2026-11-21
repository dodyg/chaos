using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Infrastructure.Search;

/// <summary>
/// Turns a resolved custom-view condition tree into one parameterized SQL predicate over the
/// task query's candidate row (PLAN2 §11.2). Every value is bound through <c>p</c>; the only
/// text in a statement is column names, operators and CTE names chosen here. Free text reaches
/// full-text search only through <see cref="SearchText.ToMatchExpression"/>. The result runs
/// inside the same candidate CTE as built-in views, so a view's count and rows cannot disagree.
/// </summary>
internal sealed class ViewPredicateCompiler(Func<object, string> p, List<string> ctes, string profileId)
{
    private const int MaxTagDepth = 16;
    private int _cte;
    private int _alias;

    public static string Compile(ResolvedViewGroup root, Func<object, string> p, List<string> ctes, string profileId) =>
        new ViewPredicateCompiler(p, ctes, profileId).Group(root);

    /// <summary>
    /// Overdue exactly as the built-in view judges it (PLAN §6): date-only due values pass when their
    /// local date ends, timed values when their instant passes in their own zone, and a recurring
    /// task with no due date is overdue when its open occurrence's effective date has passed.
    /// Shared with <see cref="SqliteTaskQueryStore"/> so the two cannot drift.
    /// </summary>
    public static string Overdue(Func<object, string> p, DateOnly before, IReadOnlyList<ZoneCutoff> cutoffs)
    {
        var timed = cutoffs.Count == 0
            ? "0"
            : string.Join(" OR ", cutoffs.Select(cutoff =>
            {
                var date = p(StorageFormats.Date(cutoff.LocalDate));
                return $"(t.DueZoneId = {p(cutoff.ZoneId)} AND (t.DueDate < {date} OR (t.DueDate = {date} AND t.DueTime < {p(StorageFormats.Time(cutoff.LocalTime))})))";
            }));
        var recurrenceCutoffs = cutoffs.Count == 0 ? "0" : string.Join(" OR ", cutoffs.Select(cutoff =>
        {
            var date = p(StorageFormats.Date(cutoff.LocalDate));
            var time = p(StorageFormats.Time(cutoff.LocalTime));
            var zone = p(cutoff.ZoneId);
            return $"(json_extract(rs.RuleJson, '$.payload.zoneId') = {zone} AND (substr(ro.EffectiveLocal,1,10) < {date} OR (json_extract(rs.RuleJson, '$.payload.anchorTime') IS NOT NULL AND substr(ro.EffectiveLocal,1,10) = {date} AND substr(ro.EffectiveLocal,12,8) < {time})))";
        }));
        var missed = $"(t.DueDate IS NULL AND EXISTS (SELECT 1 FROM RecurrenceOccurrences ro JOIN RecurrenceSeries rs ON rs.Id=ro.SeriesId WHERE ro.TaskId=t.Id AND ro.State=0 AND ro.DeletedAtUtc IS NULL AND ({recurrenceCutoffs})))";
        return $"((t.DueDate IS NOT NULL AND ((t.DueTime IS NULL AND t.DueDate < {p(StorageFormats.Date(before))}) OR (t.DueTime IS NOT NULL AND ({timed})))) OR {missed})";
    }

    private string Group(ResolvedViewGroup group)
    {
        var parts = group.Items.Select(Node).ToList();
        return parts.Count == 0 ? "1" : $"({string.Join(group.All ? " AND " : " OR ", parts)})";
    }

    private string Node(ResolvedViewNode node) => node switch
    {
        ResolvedViewGroup group => Group(group),
        ResolvedViewCondition condition => Condition(condition),
        _ => throw new InvalidOperationException("Unknown view node."),
    };

    private string Condition(ResolvedViewCondition c)
    {
        if (c.Operator == "never")
        {
            return "0";
        }

        return c.Field switch
        {
            "space" => List("t.SpaceId", c, nullable: false),
            "assignee" => Assignee(c),
            "container" => List("t.ContainerId", c, nullable: false),
            "section" => List("t.SectionId", c, nullable: true),
            "status" => List("t.Status", c, nullable: false),
            "priority" => c.Operator switch
            {
                "atLeast" => $"t.Priority >= {p(c.Numbers[0])}",
                "atMost" => $"t.Priority <= {p(c.Numbers[0])}",
                _ => $"t.Priority = {p(c.Numbers[0])}",
            },
            "tag" => Tags(c),
            "category" => $"t.CapacityCategory = {p(c.Numbers[0])}",
            "planned" => Range("t.PlannedDate", c),
            "due" => c.Operator == "overdue" ? Overdue(p, c.OverdueBefore!.Value, c.Cutoffs) : Range("t.DueDate", c),
            "created" => Range("t.CreatedAtUtc", c),
            "changed" => Range("t.UpdatedAtUtc", c),
            "completed" => Range("t.CompletedAtUtc", c),
            "estimate" => c.Operator switch
            {
                "isEmpty" => "t.EstimateMinutes IS NULL",
                "lessThan" => $"(t.EstimateMinutes IS NOT NULL AND t.EstimateMinutes < {p(c.Numbers[0])})",
                _ => $"(t.EstimateMinutes IS NOT NULL AND t.EstimateMinutes > {p(c.Numbers[0])})",
            },
            "text" => Text(c.Text!),
            "waiting" => Waiting(c),
            "source" => Source(c),
            "hasPadLink" => Flag("EXISTS (SELECT 1 FROM ScratchTaskLinks sl WHERE sl.TaskId = t.Id)", c.Numbers[0] == 1),
            "hasPageMention" => Flag("EXISTS (SELECT 1 FROM PageTaskLinks pl WHERE pl.TaskId = t.Id)", c.Numbers[0] == 1),
            "recurring" => Flag("EXISTS (SELECT 1 FROM RecurrenceOccurrences rc WHERE rc.TaskId = t.Id AND rc.DeletedAtUtc IS NULL)", c.Numbers[0] == 1),
            "githubRepository" or "githubState" or "githubLabel" or "githubMilestone" or "githubAssignee" => GitHub(c),
            _ => throw new InvalidOperationException($"No SQL for view field {c.Field}."),
        };
    }

    private string Assignee(ResolvedViewCondition c)
    {
        var personal = $"EXISTS (SELECT 1 FROM Spaces own WHERE own.Id=t.SpaceId AND own.Kind=1 AND own.OwnerProfileId={p(profileId)})";
        var assigned = "SELECT 1 FROM TaskAssignees a JOIN SpaceMembers m ON m.SpaceId=t.SpaceId AND m.MemberId=a.MemberId WHERE a.TaskId=t.Id AND a.DeletedAtUtc IS NULL AND m.RemovedAtUtc IS NULL";
        return c.Operator switch
        {
            "isMe" => $"({personal} OR EXISTS ({assigned} AND a.MemberId={p(profileId)}))",
            "isEmpty" => $"(NOT {personal} AND NOT EXISTS ({assigned}))",
            _ => $"EXISTS ({assigned} AND a.MemberId IN ({string.Join(", ", c.Ids.Select(id => p(id)))}))",
        };
    }

    private static string Source(ResolvedViewCondition c)
    {
        const string github = "EXISTS (SELECT 1 FROM GitHubIssueLinks gl WHERE gl.TaskId = t.Id)";
        const string agent = "EXISTS (SELECT 1 FROM TaskHistory ah WHERE ah.TaskId = t.Id AND ah.EventKind = 'task.created' AND ah.Actor LIKE 'agent:%')";
        return c.Ids[0] switch
        {
            "GitHub" => github,
            "Agent" => agent,
            _ => $"(NOT {github} AND NOT {agent})",
        };
    }

    /// <summary>
    /// The GitHub field group (PLAN2 D7). Every condition reads the task's issue link; the issue's labels,
    /// milestone, state and assignees come from the stored baseline (the last state the sync saw), so the
    /// answer is the same offline. Positive conditions need a link; "is not" on a repository also matches
    /// tasks that are not from GitHub. Label and assignee text is matched as a case-insensitive substring.
    /// </summary>
    private string GitHub(ResolvedViewCondition c)
    {
        var gl = $"gl{_alias++}";
        var link = $"SELECT 1 FROM GitHubIssueLinks {gl} WHERE {gl}.TaskId = t.Id";
        var payload = $"{gl}.BaseJson";
        switch (c.Field)
        {
            case "githubRepository":
            {
                var ids = string.Join(", ", c.Ids.Select(id => p(id)));
                var inside = $"EXISTS ({link} AND {gl}.RepositorySubscriptionId IN ({ids}))";
                return c.Operator == "notIn" ? $"NOT {inside}" : inside;
            }
            case "githubState":
                return $"EXISTS ({link} AND json_extract({payload}, '$.payload.open') = {(c.Ids[0] == "Open" ? 1 : 0)})";
            case "githubLabel":
            {
                var labels = $"SELECT 1 FROM json_each({payload}, '$.payload.labelIds') je JOIN GitHubLabelTags lt ON lt.RepositorySubscriptionId = {gl}.RepositorySubscriptionId AND lt.LabelId = je.value";
                return c.Operator == "isEmpty"
                    ? $"EXISTS ({link} AND NOT EXISTS ({labels}))"
                    : $"EXISTS ({link} AND EXISTS ({labels} WHERE instr(lower(lt.LabelName), {p(c.Text!.ToLowerInvariant())}) > 0))";
            }
            case "githubMilestone":
                // Titles are known only for repositories that map milestones to projects; "is empty" works for every repository.
                return c.Operator == "isEmpty"
                    ? $"EXISTS ({link} AND json_extract({payload}, '$.payload.milestoneNumber') IS NULL)"
                    : $"EXISTS ({link} AND EXISTS (SELECT 1 FROM GitHubMilestoneProjects mp WHERE mp.RepositorySubscriptionId = {gl}.RepositorySubscriptionId AND mp.MilestoneNumber = json_extract({payload}, '$.payload.milestoneNumber') AND instr(lower(mp.BaseTitle), {p(c.Text!.ToLowerInvariant())}) > 0))";
            default: // githubAssignee
                return c.Operator == "isEmpty"
                    ? $"EXISTS ({link} AND json_array_length(COALESCE(json_extract({payload}, '$.payload.assigneeIds'), '[]')) = 0)"
                    : $"EXISTS ({link} AND EXISTS (SELECT 1 FROM json_each({payload}, '$.payload.assigneeLogins') ja WHERE instr(lower(ja.value), {p(c.Text!.ToLowerInvariant())}) > 0))";
        }
    }

    private static string Flag(string exists, bool expected) => expected ? exists : $"NOT {exists}";

    private string List(string column, ResolvedViewCondition c, bool nullable)
    {
        var values = c.Ids.Count > 0 ? c.Ids.Select(id => p(id)) : c.Numbers.Select(number => p(number));
        var list = string.Join(", ", values);
        if (c.Operator == "notIn")
        {
            var not = $"{column} NOT IN ({list})";
            return nullable ? $"({column} IS NULL OR {not})" : not;
        }

        return $"{column} IN ({list})";
    }

    private string Range(string column, ResolvedViewCondition c)
    {
        switch (c.Operator)
        {
            case "isEmpty":
                return $"{column} IS NULL";
            case "isNotEmpty":
                return $"{column} IS NOT NULL";
        }

        var parts = new List<string> { $"{column} IS NOT NULL" };
        if (c.Lower is not null)
        {
            parts.Add($"{column} >= {p(c.Lower)}");
        }

        if (c.Upper is not null)
        {
            parts.Add($"{column} < {p(c.Upper)}");
        }

        return $"({string.Join(" AND ", parts)})";
    }

    private string Tags(ResolvedViewCondition c)
    {
        string Has(IReadOnlyList<string> ids)
        {
            var name = $"ViewTag{_cte++}";
            var seeds = string.Join(", ", ids.Select(id => p(id)));
            var recursion = c.Descendants
                ? $"""

                    UNION ALL
                    SELECT child.Id, scope.Depth + 1 FROM Tags child JOIN {name} scope ON child.ParentTagId = scope.Id
                    WHERE child.DeletedAtUtc IS NULL AND scope.Depth < {MaxTagDepth}
                    """
                : string.Empty;
            ctes.Add($"""
                {name}(Id, Depth) AS (
                    SELECT Id, 0 FROM Tags WHERE Id IN ({seeds}) AND OwnerProfileId = {p(profileId)} AND DeletedAtUtc IS NULL{recursion})
                """);
            // As the built-in tag view does: a tag on the task or on one of its subtasks counts.
            return $"""
                (EXISTS (SELECT 1 FROM TaskTags tt WHERE tt.TaskId = t.Id AND tt.DeletedAtUtc IS NULL AND tt.TagId IN (SELECT Id FROM {name}))
                 OR EXISTS (SELECT 1 FROM Subtasks st JOIN SubtaskTags stt ON stt.SubtaskId = st.Id
                            WHERE st.TaskId = t.Id AND st.DeletedAtUtc IS NULL AND stt.DeletedAtUtc IS NULL AND stt.TagId IN (SELECT Id FROM {name})))
                """;
        }

        return c.Operator switch
        {
            "hasAllOf" => $"({string.Join(" AND ", c.Ids.Select(id => Has([id])))})",
            "hasNone" => $"NOT {Has(c.Ids)}",
            _ => Has(c.Ids),
        };
    }

    private string Text(string text)
    {
        var match = SearchText.ToMatchExpression(text);
        if (match is null)
        {
            return "0";
        }

        // Title and notes of the task itself (search document kind 1), as search matches them.
        var name = $"ViewText{_cte++}";
        ctes.Add($"""
            {name} AS MATERIALIZED (
                SELECT d.TaskId AS TaskId FROM SearchDocuments d
                WHERE d.TargetKind = 1 AND d.TaskId IS NOT NULL
                  AND d.RowId IN (SELECT rowid FROM SearchIndex WHERE SearchIndex MATCH {p(match)}))
            """);
        return $"t.Id IN (SELECT TaskId FROM {name})";
    }

    private string Waiting(ResolvedViewCondition c)
    {
        if (c.Operator == "isEmpty")
        {
            return "w.TaskId IS NULL";
        }

        var terms = SearchText.Terms(SearchText.Normalize(c.Text!).ToLowerInvariant());
        return terms.Count == 0
            ? "0"
            : $"(w.TaskId IS NOT NULL AND {string.Join(" AND ", terms.Select(term => $"instr(w.PersonNormalized, {p(term)}) > 0"))})";
    }
}
