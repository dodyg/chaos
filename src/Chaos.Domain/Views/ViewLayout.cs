using System.Text.Json.Nodes;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Views;

public enum ViewLayoutKind { List, Table, Board, Calendar }

/// <summary>
/// How a custom view is shown (PLAN2 §11.2): the layout, the list grouping, the sort and the
/// table's columns. The calendar layout (P4) shows the view's tasks on the calendar. Version 1.
/// </summary>
public sealed record ViewLayout(
    ViewLayoutKind Kind,
    string Group,
    string? GroupTagId,
    string SortField,
    bool SortDescending,
    IReadOnlyList<string> Columns)
{
    public const int Version = 1;
    public const int MaxColumns = 12;

    public static readonly IReadOnlyList<string> Groups = ["none", "container", "status", "priority", "tag", "due"];
    public static readonly IReadOnlyList<string> SortFields = ["due", "planned", "priority", "created", "changed", "title", "manual"];
    public static readonly IReadOnlyList<string> ColumnNames =
        ["title", "status", "priority", "container", "planned", "due", "estimate", "tags", "created", "changed", "completed"];

    public static ViewLayout Default { get; } = new(
        ViewLayoutKind.List, "none", null, "due", false, ["title", "status", "priority", "due"]);

    /// <exception cref="DomainValidationException">The text is not a valid version-1 layout.</exception>
    public static ViewLayout Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new DomainValidationException("The view layout is not valid JSON.");
        }

        if (node is not JsonObject root || !root.TryGetPropertyValue("v", out var v) || v?.ToString() != Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new DomainValidationException("This view layout was made by a newer version of Chaos and cannot be opened.");
        }

        var kind = Text(root, "kind") switch
        {
            null or "list" => ViewLayoutKind.List,
            "table" => ViewLayoutKind.Table,
            "board" => ViewLayoutKind.Board,
            "calendar" => ViewLayoutKind.Calendar,
            _ => throw new DomainValidationException("Choose the list, table, board or calendar layout."),
        };

        var group = Text(root, "group") ?? "none";
        if (!Groups.Contains(group))
        {
            throw new DomainValidationException($"Group by one of: {string.Join(", ", Groups)}.");
        }

        var groupTag = Text(root, "groupTagId");
        if (group == "tag" && groupTag is null)
        {
            throw new DomainValidationException("Choose the tag whose children the list groups by.");
        }

        var field = "due";
        var descending = false;
        if (root.TryGetPropertyValue("sort", out var sortNode) && sortNode is JsonObject sort)
        {
            field = Text(sort, "field") ?? field;
            descending = (Text(sort, "dir") ?? "asc") switch
            {
                "asc" => false,
                "desc" => true,
                _ => throw new DomainValidationException("Sort ascending or descending."),
            };
        }

        if (!SortFields.Contains(field))
        {
            throw new DomainValidationException($"Sort by one of: {string.Join(", ", SortFields)}.");
        }

        var columns = new List<string>();
        if (root.TryGetPropertyValue("columns", out var columnsNode) && columnsNode is JsonArray array)
        {
            foreach (var item in array)
            {
                var name = item?.ToString();
                if (name is null || !ColumnNames.Contains(name))
                {
                    throw new DomainValidationException($"\"{name}\" is not a table column.");
                }

                if (!columns.Contains(name))
                {
                    columns.Add(name);
                }
            }
        }
        else
        {
            columns.AddRange(Default.Columns);
        }

        if (columns.Count is 0 or > MaxColumns || !columns.Contains("title"))
        {
            throw new DomainValidationException($"Choose from 1 to {MaxColumns} columns, including the title.");
        }

        return new ViewLayout(kind, group, groupTag, field, descending, columns);
    }

    public string ToJson()
    {
        var sort = new JsonObject { ["field"] = SortField, ["dir"] = SortDescending ? "desc" : "asc" };
        var root = new JsonObject
        {
            ["v"] = Version,
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["group"] = Group,
            ["sort"] = sort,
            ["columns"] = new JsonArray([.. Columns.Select(column => (JsonNode?)JsonValue.Create(column))]),
        };
        if (GroupTagId is not null)
        {
            root["groupTagId"] = GroupTagId;
        }

        return root.ToJsonString();
    }

    private static string? Text(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
