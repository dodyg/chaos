using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaos.Domain.Planning;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Views;

/// <summary>The value type a field holds; it decides which operators and value shapes are legal (PLAN2 §11.2).</summary>
public enum ViewFieldType { Id, StatusSet, Ordinal, TagSet, Category, Date, Instant, Minutes, Text, Source, Flag, Choice }

/// <summary>One searchable attribute: its stable name, type and the operators it accepts.</summary>
public sealed record ViewFieldInfo(string Name, ViewFieldType Type, IReadOnlyList<string> Operators, IReadOnlyList<string>? Choices = null);

/// <summary>
/// The field registry's type table (PLAN2 §11.2). Validation needs it in the domain; the
/// application resolves each field to criteria and the store turns those into parameterized SQL.
/// Fields that depend on later packages (attachments in P9, GitHub in the D7 group) are added
/// with their package, never listed before they can be answered.
/// </summary>
public static class ViewFieldCatalog
{
    private static readonly string[] DateOps = ["before", "after", "on", "inRange", "isEmpty", "isNotEmpty"];

    public static readonly IReadOnlyList<ViewFieldInfo> All =
    [
        new("space", ViewFieldType.Id, ["is", "isNot", "in"]),
        new("assignee", ViewFieldType.Id, ["isMe", "is", "in", "isEmpty"]),
        new("container", ViewFieldType.Id, ["is", "isNot", "in"]),
        new("section", ViewFieldType.Id, ["is", "isNot", "in"]),
        new("status", ViewFieldType.StatusSet, ["in", "notIn"]),
        new("customState", ViewFieldType.Id, ["is", "isNot", "in"]),
        new("priority", ViewFieldType.Ordinal, ["is", "atLeast", "atMost"]),
        new("tag", ViewFieldType.TagSet, ["hasAnyOf", "hasAllOf", "hasNone"]),
        new("category", ViewFieldType.Category, ["is"]),
        new("planned", ViewFieldType.Date, DateOps),
        new("due", ViewFieldType.Date, DateOps),
        new("created", ViewFieldType.Instant, DateOps),
        new("changed", ViewFieldType.Instant, DateOps),
        new("completed", ViewFieldType.Instant, DateOps),
        new("estimate", ViewFieldType.Minutes, ["lessThan", "moreThan", "isEmpty"]),
        new("text", ViewFieldType.Text, ["contains"]),
        new("waiting", ViewFieldType.Text, ["contains", "isEmpty"]),
        new("source", ViewFieldType.Source, ["is"]),
        new("hasPadLink", ViewFieldType.Flag, ["is"]),
        new("hasPageMention", ViewFieldType.Flag, ["is"]),
        new("recurring", ViewFieldType.Flag, ["is"]),
        // GitHub group (D7, personal views): answered from the stored issue baseline and the label and
        // milestone mappings, so only what the app already keeps. Author and linked pull requests are
        // not stored by the sync and are therefore not offered.
        new("githubRepository", ViewFieldType.Id, ["is", "isNot", "in"]),
        new("githubState", ViewFieldType.Choice, ["is"], ["Open", "Closed"]),
        new("githubLabel", ViewFieldType.Text, ["contains", "isEmpty"]),
        new("githubMilestone", ViewFieldType.Text, ["contains", "isEmpty"]),
        new("githubAssignee", ViewFieldType.Text, ["contains", "isEmpty"]),
    ];

    public static ViewFieldInfo? Find(string name) => All.FirstOrDefault(field => field.Name == name);
}

public enum ViewMatch { All, Any }

/// <summary>The relative date phrases of PLAN2 §11.2. <c>Overdue</c> is valid only for the due field.</summary>
public enum ViewRelative
{
    Today, Tomorrow, Yesterday,
    ThisWeek, NextWeek, LastWeek,
    ThisMonth, NextMonth, LastMonth,
    NextDays, LastDays, Overdue,
}

/// <summary>An absolute single day, an absolute range, or a relative phrase (with a day count for next/last N days).</summary>
public sealed record ViewDateValue(DateOnly? From, DateOnly? To, ViewRelative? Relative, int Days)
{
    public bool IsRelative => Relative is not null;
}

public abstract record ViewNode;

public sealed record ViewGroup(ViewMatch Match, IReadOnlyList<ViewNode> Items) : ViewNode;

/// <summary>
/// One condition. Which value member is set depends on the field's type: <see cref="Values"/> for
/// ids, statuses, priorities, categories and sources; <see cref="Date"/> for date fields;
/// <see cref="Number"/> for minutes; <see cref="Text"/> for text; <see cref="Flag"/> for booleans.
/// </summary>
public sealed record ViewCondition(
    string Field,
    string Operator,
    IReadOnlyList<string> Values,
    ViewDateValue? Date = null,
    long? Number = null,
    string? Text = null,
    bool? Flag = null,
    bool Descendants = false) : ViewNode;

/// <summary>
/// A custom view's condition tree, version 1 (PLAN2 §11.2). Parsing is strict: an unknown field,
/// an operator the field does not accept, a value of the wrong shape, more than four levels of
/// groups or more than fifty conditions all fail with a message the editor can show.
/// </summary>
public sealed record ViewDefinition(ViewGroup Root)
{
    public const int Version = 1;
    public const int MaxDepth = 4;
    public const int MaxConditions = 50;
    public const int MaxTextLength = 200;
    public const int MaxValues = 100;
    public const int MaxRelativeDays = 3660;

    public static ViewDefinition Empty { get; } = new(new ViewGroup(ViewMatch.All, []));

    public int ConditionCount => Count(Root);

    /// <summary>Parses and validates the stored JSON text.</summary>
    /// <exception cref="DomainValidationException">The text is not a valid version-1 definition.</exception>
    public static ViewDefinition Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainValidationException("The view has no definition.");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw new DomainValidationException("The view definition is not valid JSON.");
        }

        if (node is not JsonObject root)
        {
            throw new DomainValidationException("The view definition must be an object.");
        }

        if (!root.TryGetPropertyValue("v", out var version) || version is not JsonValue v || !v.TryGetValue<int>(out var versionNumber) || versionNumber != Version)
        {
            throw new DomainValidationException("This view was made by a newer version of Chaos and cannot be opened.");
        }

        var count = 0;
        var group = ParseGroup(root, 1, ref count);
        return new ViewDefinition(group);
    }

    /// <summary>The canonical JSON text of the definition (stable key order), for storing and comparing.</summary>
    public string ToJson()
    {
        var root = GroupToNode(Root);
        var ordered = new JsonObject { ["v"] = Version };
        foreach (var (key, value) in root.ToList())
        {
            root.Remove(key);
            ordered[key] = value;
        }

        return ordered.ToJsonString();
    }

    private static int Count(ViewNode node) => node switch
    {
        ViewGroup group => group.Items.Sum(Count),
        _ => 1,
    };

    private static ViewGroup ParseGroup(JsonObject obj, int depth, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw new DomainValidationException($"Groups can be nested at most {MaxDepth} levels deep.");
        }

        var match = ViewMatch.All;
        if (obj.TryGetPropertyValue("match", out var matchNode) && matchNode is not null)
        {
            match = matchNode.ToString() switch
            {
                "all" => ViewMatch.All,
                "any" => ViewMatch.Any,
                _ => throw new DomainValidationException("A group matches \"all\" or \"any\" of its conditions."),
            };
        }

        if (!obj.TryGetPropertyValue("items", out var itemsNode) || itemsNode is not JsonArray array)
        {
            throw new DomainValidationException("A group needs a list of conditions.");
        }

        var items = new List<ViewNode>();
        foreach (var item in array)
        {
            if (item is not JsonObject child)
            {
                throw new DomainValidationException("Each condition must be an object.");
            }

            if (child.ContainsKey("items"))
            {
                items.Add(ParseGroup(child, depth + 1, ref count));
            }
            else
            {
                count++;
                if (count > MaxConditions)
                {
                    throw new DomainValidationException($"A view can have at most {MaxConditions} conditions.");
                }

                items.Add(ParseCondition(child));
            }
        }

        return new ViewGroup(match, items);
    }

    private static ViewCondition ParseCondition(JsonObject obj)
    {
        var fieldName = StringProperty(obj, "field") ?? throw new DomainValidationException("A condition needs a field.");
        var field = ViewFieldCatalog.Find(fieldName) ?? throw new DomainValidationException($"\"{fieldName}\" is not a field a view can use.");
        var op = StringProperty(obj, "op") ?? throw new DomainValidationException($"A condition on {fieldName} needs an operator.");
        if (!field.Operators.Contains(op))
        {
            throw new DomainValidationException($"\"{op}\" cannot be used with {fieldName}. Choose one of: {string.Join(", ", field.Operators)}.");
        }

        obj.TryGetPropertyValue("value", out var value);
        var descendants = obj.TryGetPropertyValue("descendants", out var d) && d is JsonValue dv && dv.TryGetValue<bool>(out var flag) && flag;
        if (descendants && field.Type != ViewFieldType.TagSet)
        {
            throw new DomainValidationException("Only tag conditions can include descendants.");
        }

        var emptyOp = op is "isEmpty" or "isNotEmpty" or "isMe";
        if (emptyOp)
        {
            return new ViewCondition(fieldName, op, []);
        }

        return field.Type switch
        {
            ViewFieldType.Id => new ViewCondition(fieldName, op, Strings(fieldName, value, op == "in", ids: true)),
            ViewFieldType.StatusSet => new ViewCondition(fieldName, op, Names<TaskItemStatus>(fieldName, value, many: true)),
            ViewFieldType.Ordinal => new ViewCondition(fieldName, op, Names<TaskPriority>(fieldName, value, many: false)),
            ViewFieldType.TagSet => new ViewCondition(fieldName, op, Strings(fieldName, value, many: true, ids: true), Descendants: descendants),
            ViewFieldType.Category => new ViewCondition(fieldName, op, Names<CapacityCategory>(fieldName, value, many: false)),
            ViewFieldType.Source => new ViewCondition(fieldName, op, OneOf(fieldName, value, "App", "GitHub", "Agent")),
            ViewFieldType.Choice => new ViewCondition(fieldName, op, OneOf(fieldName, value, [.. field.Choices!])),
            ViewFieldType.Date or ViewFieldType.Instant => new ViewCondition(fieldName, op, [], Date: ParseDate(field, op, value)),
            ViewFieldType.Minutes => new ViewCondition(fieldName, op, [], Number: ParseMinutes(fieldName, value)),
            ViewFieldType.Text => new ViewCondition(fieldName, op, [], Text: ParseText(fieldName, value)),
            ViewFieldType.Flag => new ViewCondition(fieldName, op, [], Flag: value is JsonValue fv && fv.TryGetValue<bool>(out var b)
                ? b
                : throw new DomainValidationException($"{fieldName} is either yes or no.")),
            _ => throw new DomainValidationException($"\"{fieldName}\" is not a field a view can use."),
        };
    }

    private static string? StringProperty(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static IReadOnlyList<string> Strings(string field, JsonNode? value, bool many, bool ids)
    {
        var list = new List<string>();
        switch (value)
        {
            case JsonValue single when single.TryGetValue<string>(out var text):
                list.Add(text);
                break;
            case JsonArray array when many:
                foreach (var element in array)
                {
                    list.Add(element is JsonValue ev && ev.TryGetValue<string>(out var t)
                        ? t
                        : throw new DomainValidationException($"Choose values for {field}."));
                }

                break;
            default:
                throw new DomainValidationException($"Choose {(many ? "one or more values" : "a value")} for {field}.");
        }

        if (list.Count == 0 || list.Count > MaxValues || list.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 64))
        {
            throw new DomainValidationException($"Choose between 1 and {MaxValues} values for {field}.");
        }

        _ = ids;
        return [.. list.Select(item => item.Trim()).Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> Names<TEnum>(string field, JsonNode? value, bool many) where TEnum : struct, Enum
    {
        var raw = Strings(field, value, many, ids: false);
        var names = new List<string>();
        foreach (var item in raw)
        {
            if (!Enum.TryParse<TEnum>(item, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(item, out _))
            {
                throw new DomainValidationException($"\"{item}\" is not a valid value for {field}.");
            }

            names.Add(parsed.ToString());
        }

        return names;
    }

    private static IReadOnlyList<string> OneOf(string field, JsonNode? value, params string[] allowed)
    {
        var raw = Strings(field, value, many: false, ids: false);
        return allowed.Contains(raw[0]) ? raw : throw new DomainValidationException($"{field} is one of: {string.Join(", ", allowed)}.");
    }

    private static long ParseMinutes(string field, JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<long>(out var minutes) && minutes is >= 0 and <= EstimateMinutes.MaxMinutes
            ? minutes
            : throw new DomainValidationException($"{field} is a number of minutes from 0 to {EstimateMinutes.MaxMinutes}.");

    private static string ParseText(string field, JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.Trim();
            return trimmed.Length <= MaxTextLength
                ? trimmed
                : throw new DomainValidationException($"The text for {field} can be at most {MaxTextLength} characters.");
        }

        throw new DomainValidationException($"Enter some text for {field}.");
    }

    private static ViewDateValue ParseDate(ViewFieldInfo field, string op, JsonNode? value)
    {
        var parsed = value switch
        {
            JsonValue single when single.TryGetValue<string>(out var text) => SingleDay(field.Name, text),
            JsonObject obj when obj.ContainsKey("relative") => ParseRelative(field, obj),
            JsonObject obj => ParseRange(field.Name, obj),
            _ => throw new DomainValidationException($"Choose a date for {field.Name}."),
        };

        if (parsed.Relative == ViewRelative.Overdue && (field.Name != "due" || op != "inRange"))
        {
            throw new DomainValidationException("\"Overdue\" is only available as a range on the due date.");
        }

        if (parsed is { From: { } from, To: { } to } && from > to)
        {
            throw new DomainValidationException($"The start date of {field.Name} must be on or before its end date.");
        }

        return parsed;
    }

    private static ViewDateValue SingleDay(string field, string text)
    {
        var day = Day(field, text);
        return new ViewDateValue(day, day, null, 0);
    }

    private static ViewDateValue ParseRange(string field, JsonObject obj)
    {
        DateOnly? from = obj.TryGetPropertyValue("from", out var f) && f is JsonValue fv && fv.TryGetValue<string>(out var ft) ? Day(field, ft) : null;
        DateOnly? to = obj.TryGetPropertyValue("to", out var t) && t is JsonValue tv && tv.TryGetValue<string>(out var tt) ? Day(field, tt) : null;
        return from is null && to is null
            ? throw new DomainValidationException($"Give {field} a start date, an end date, or both.")
            : new ViewDateValue(from, to, null, 0);
    }

    private static ViewDateValue ParseRelative(ViewFieldInfo field, JsonObject obj)
    {
        var name = StringProperty(obj, "relative") ?? throw new DomainValidationException($"Choose a relative date for {field.Name}.");
        var days = 0;
        ViewRelative relative;
        switch (name)
        {
            case "today": relative = ViewRelative.Today; break;
            case "tomorrow": relative = ViewRelative.Tomorrow; break;
            case "yesterday": relative = ViewRelative.Yesterday; break;
            case "thisWeek": relative = ViewRelative.ThisWeek; break;
            case "nextWeek": relative = ViewRelative.NextWeek; break;
            case "lastWeek": relative = ViewRelative.LastWeek; break;
            case "thisMonth": relative = ViewRelative.ThisMonth; break;
            case "nextMonth": relative = ViewRelative.NextMonth; break;
            case "lastMonth": relative = ViewRelative.LastMonth; break;
            case "overdue": relative = ViewRelative.Overdue; break;
            case "next":
            case "last":
                relative = name == "next" ? ViewRelative.NextDays : ViewRelative.LastDays;
                if (!obj.TryGetPropertyValue("days", out var daysNode) || daysNode is not JsonValue dv || !dv.TryGetValue<int>(out days) || days is < 1 or > MaxRelativeDays)
                {
                    throw new DomainValidationException($"\"{name}\" needs a number of days from 1 to {MaxRelativeDays}.");
                }

                break;
            default:
                throw new DomainValidationException($"\"{name}\" is not a relative date.");
        }

        return new ViewDateValue(null, null, relative, days);
    }

    private static DateOnly Day(string field, string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new DomainValidationException($"\"{text}\" is not a date for {field}. Use year-month-day.");

    private static JsonObject GroupToNode(ViewGroup group)
    {
        var items = new JsonArray();
        foreach (var item in group.Items)
        {
            items.Add(item switch
            {
                ViewGroup child => GroupToNode(child),
                ViewCondition condition => ConditionToNode(condition),
                _ => throw new InvalidOperationException("Unknown view node."),
            });
        }

        return new JsonObject { ["match"] = group.Match == ViewMatch.All ? "all" : "any", ["items"] = items };
    }

    private static JsonObject ConditionToNode(ViewCondition condition)
    {
        var node = new JsonObject { ["field"] = condition.Field, ["op"] = condition.Operator };
        var info = ViewFieldCatalog.Find(condition.Field)!;
        JsonNode? value = condition.Operator is "isEmpty" or "isNotEmpty" or "isMe"
            ? null
            : info.Type switch
            {
                ViewFieldType.Id when condition.Operator is "is" or "isNot" => JsonValue.Create(condition.Values[0]),
                ViewFieldType.Id or ViewFieldType.StatusSet or ViewFieldType.TagSet =>
                    new JsonArray([.. condition.Values.Select(v => (JsonNode?)JsonValue.Create(v))]),
                ViewFieldType.Ordinal or ViewFieldType.Category or ViewFieldType.Source or ViewFieldType.Choice => JsonValue.Create(condition.Values[0]),
                ViewFieldType.Date or ViewFieldType.Instant => DateToNode(condition.Date!),
                ViewFieldType.Minutes => JsonValue.Create(condition.Number),
                ViewFieldType.Text => JsonValue.Create(condition.Text),
                ViewFieldType.Flag => JsonValue.Create(condition.Flag),
                _ => null,
            };
        if (value is not null)
        {
            node["value"] = value;
        }

        if (condition.Descendants)
        {
            node["descendants"] = true;
        }

        return node;
    }

    private static JsonNode DateToNode(ViewDateValue date)
    {
        if (date.Relative is { } relative)
        {
            var name = relative switch
            {
                ViewRelative.NextDays => "next",
                ViewRelative.LastDays => "last",
                _ => char.ToLowerInvariant(relative.ToString()[0]) + relative.ToString()[1..],
            };
            var obj = new JsonObject { ["relative"] = name };
            if (relative is ViewRelative.NextDays or ViewRelative.LastDays)
            {
                obj["days"] = date.Days;
            }

            return obj;
        }

        if (date.From is { } from && date.To is { } to && from == to)
        {
            return JsonValue.Create(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))!;
        }

        var range = new JsonObject();
        if (date.From is { } f)
        {
            range["from"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (date.To is { } t)
        {
            range["to"] = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return range;
    }
}

/// <summary>
/// Resolves a date value to concrete local dates (PLAN2 §11.2): the range a relative phrase
/// covers on a given day. "Next N days" runs from today through N-1 days ahead, "last N days"
/// from N-1 days back through today; weeks start on the profile's first weekday. A null end is open.
/// </summary>
public static class ViewDates
{
    public static (DateOnly? From, DateOnly? To) Resolve(ViewDateValue value, DateOnly today, DayOfWeek firstWeekday)
    {
        if (value.Relative is not { } relative)
        {
            return (value.From, value.To);
        }

        var weekStart = WeekCalendar.StartOf(today, firstWeekday);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return relative switch
        {
            ViewRelative.Today => (today, today),
            ViewRelative.Tomorrow => (today.AddDays(1), today.AddDays(1)),
            ViewRelative.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            ViewRelative.ThisWeek => (weekStart, WeekCalendar.EndOf(weekStart)),
            ViewRelative.NextWeek => (weekStart.AddDays(7), WeekCalendar.EndOf(weekStart.AddDays(7))),
            ViewRelative.LastWeek => (weekStart.AddDays(-7), WeekCalendar.EndOf(weekStart.AddDays(-7))),
            ViewRelative.ThisMonth => (monthStart, monthStart.AddMonths(1).AddDays(-1)),
            ViewRelative.NextMonth => (monthStart.AddMonths(1), monthStart.AddMonths(2).AddDays(-1)),
            ViewRelative.LastMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            ViewRelative.NextDays => (today, today.AddDays(value.Days - 1)),
            ViewRelative.LastDays => (today.AddDays(-(value.Days - 1)), today),
            // Overdue is a status-aware predicate the store compiles itself; as dates it is "before today".
            ViewRelative.Overdue => (null, today.AddDays(-1)),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }
}
