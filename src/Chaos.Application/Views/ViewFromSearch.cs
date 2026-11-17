using System.Text.Json.Nodes;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;

namespace Chaos.Application.Views;

/// <summary>
/// "Save as view" on the search page (PLAN2 §11.2): the current text and temporary filters become a
/// definition. The result is parsed and re-serialized like any saved definition, so it is valid by
/// construction; without an explicit status filter, finished tasks stay out, as they do in search.
/// </summary>
public static class ViewFromSearch
{
    public static string Build(TaskQuerySpec spec)
    {
        var items = new JsonArray();

        void Add(string field, string op, JsonNode? value, bool descendants = false)
        {
            var node = new JsonObject { ["field"] = field, ["op"] = op };
            if (value is not null)
            {
                node["value"] = value;
            }

            if (descendants)
            {
                node["descendants"] = true;
            }

            items.Add(node);
        }

        static JsonNode Names<T>(IEnumerable<T> values) => new JsonArray([.. values.Distinct().Select(value => (JsonNode?)JsonValue.Create(value!.ToString()))]);

        if (!string.IsNullOrWhiteSpace(spec.Text))
        {
            Add("text", "contains", JsonValue.Create(spec.Text.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(spec.ContainerId))
        {
            Add("container", "is", JsonValue.Create(spec.ContainerId));
        }

        if (!string.IsNullOrWhiteSpace(spec.TagId))
        {
            Add("tag", "hasAnyOf", new JsonArray(JsonValue.Create(spec.TagId)), descendants: true);
        }

        if (spec.Statuses.Count > 0)
        {
            Add("status", "in", Names(spec.Statuses));
        }
        else if (!spec.IncludeCompleted)
        {
            Add("status", "notIn", Names([TaskItemStatus.Done, TaskItemStatus.Cancelled]));
        }

        if (spec.Priorities.Count == 1)
        {
            Add("priority", "is", JsonValue.Create(spec.Priorities[0].ToString()));
        }
        else if (spec.Priorities.Count > 1)
        {
            // The search filter means "any of these priorities".
            var any = new JsonArray();
            foreach (var priority in spec.Priorities.Distinct())
            {
                any.Add(new JsonObject { ["field"] = "priority", ["op"] = "is", ["value"] = priority.ToString() });
            }

            items.Add(new JsonObject { ["match"] = "any", ["items"] = any });
        }

        if (spec.Category is { } category)
        {
            Add("category", "is", JsonValue.Create(category.ToString()));
        }

        AddRange("planned", spec.Planned);
        AddRange("due", spec.Due);
        AddRange("completed", spec.Completed);

        var root = new JsonObject { ["v"] = ViewDefinition.Version, ["match"] = "all", ["items"] = items };
        return ViewDefinition.Parse(root.ToJsonString()).ToJson();

        void AddRange(string field, LocalDateRange? range)
        {
            if (range is null || (range.From is null && range.To is null))
            {
                return;
            }

            var value = new JsonObject();
            if (range.From is { } from)
            {
                value["from"] = StorageFormats.Date(from);
            }

            if (range.To is { } to)
            {
                value["to"] = StorageFormats.Date(to);
            }

            Add(field, "inRange", value);
        }
    }
}
