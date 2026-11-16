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
            "source" => Flag("EXISTS (SELECT 1 FROM GitHubIssueLinks gl WHERE gl.TaskId = t.Id)", c.Ids[0] == "GitHub"),
            "hasPadLink" => Flag("EXISTS (SELECT 1 FROM ScratchTaskLinks sl WHERE sl.TaskId = t.Id)", c.Numbers[0] == 1),
            "hasPageMention" => Flag("EXISTS (SELECT 1 FROM PageTaskLinks pl WHERE pl.TaskId = t.Id)", c.Numbers[0] == 1),
            "recurring" => Flag("EXISTS (SELECT 1 FROM RecurrenceOccurrences rc WHERE rc.TaskId = t.Id AND rc.DeletedAtUtc IS NULL)", c.Numbers[0] == 1),
            _ => throw new InvalidOperationException($"No SQL for view field {c.Field}."),
        };
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
