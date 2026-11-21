using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;

namespace Chaos.Application.Queries;

/// <summary>
/// The field registry's resolver (PLAN2 §11.2). It turns a validated condition tree into
/// <see cref="ResolvedViewGroup"/>: names become stored integers, relative dates become the dates
/// or UTC instants they cover in the profile's zone and first weekday, and "overdue" gets the
/// same per-zone cutoffs the built-in Overdue view uses. The store compiles the result to
/// parameterized SQL, so no value is ever concatenated into a statement.
/// </summary>
internal static class ViewFields
{
    public static async Task<ResolvedViewGroup> ResolveAsync(
        IDataSession session, TaskQueries.QueryContext context, ViewDefinition definition, CancellationToken token)
    {
        IReadOnlyList<ZoneCutoff>? cutoffs = null;

        async Task<IReadOnlyList<ZoneCutoff>> CutoffsAsync()
        {
            if (cutoffs is not null)
            {
                return cutoffs;
            }

            var list = new List<ZoneCutoff>();
            foreach (var zoneId in await session.Queries.ListOpenDueZoneIdsAsync(context.Space.Id, token))
            {
                // An unknown stored zone falls back to the profile zone rather than hiding the task.
                var local = TimeZoneInfo.ConvertTime(context.Now, TaskQueries.FindZone(zoneId) ?? context.Zone);
                list.Add(new ZoneCutoff(zoneId, DateOnly.FromDateTime(local.DateTime), new TimeOnly(local.Hour, local.Minute, local.Second)));
            }

            return cutoffs = list;
        }

        async Task<ResolvedViewGroup> GroupAsync(ViewGroup group)
        {
            var items = new List<ResolvedViewNode>();
            foreach (var item in group.Items)
            {
                items.Add(item switch
                {
                    ViewGroup child => await GroupAsync(child),
                    ViewCondition condition => await ConditionAsync(condition),
                    _ => throw new InvalidOperationException("Unknown view node."),
                });
            }

            return new ResolvedViewGroup(group.Match == ViewMatch.All, items);
        }

        async Task<ResolvedViewNode> ConditionAsync(ViewCondition condition)
        {
            var field = ViewFieldCatalog.Find(condition.Field) ?? throw AppException.Validation("definition", $"\"{condition.Field}\" is not a field a view can use.");
            var op = condition.Operator;
            if (op is "isEmpty" or "isNotEmpty" or "isMe")
            {
                return new ResolvedViewCondition(field.Name, op);
            }

            return field.Type switch
            {
                ViewFieldType.Id => new ResolvedViewCondition(field.Name, op == "isNot" ? "notIn" : "in") { Ids = condition.Values },
                ViewFieldType.StatusSet => new ResolvedViewCondition(field.Name, op)
                {
                    Numbers = [.. condition.Values.Select(name => (long)Enum.Parse<TaskItemStatus>(name))],
                },
                ViewFieldType.Ordinal => new ResolvedViewCondition(field.Name, op) { Numbers = [(long)Enum.Parse<TaskPriority>(condition.Values[0])] },
                ViewFieldType.TagSet => new ResolvedViewCondition(field.Name, op) { Ids = condition.Values, Descendants = condition.Descendants },
                ViewFieldType.Category => new ResolvedViewCondition(field.Name, "is") { Numbers = [(long)Enum.Parse<CapacityCategory>(condition.Values[0])] },
                ViewFieldType.Source or ViewFieldType.Choice => new ResolvedViewCondition(field.Name, "is") { Ids = condition.Values },
                ViewFieldType.Minutes => new ResolvedViewCondition(field.Name, op) { Numbers = [condition.Number!.Value] },
                ViewFieldType.Text => new ResolvedViewCondition(field.Name, "contains") { Text = condition.Text },
                ViewFieldType.Flag => new ResolvedViewCondition(field.Name, "is") { Numbers = [condition.Flag == true ? 1 : 0] },
                ViewFieldType.Date or ViewFieldType.Instant => await DateAsync(field, condition),
                _ => throw AppException.Validation("definition", $"\"{condition.Field}\" is not a field a view can use."),
            };
        }

        async Task<ResolvedViewNode> DateAsync(ViewFieldInfo field, ViewCondition condition)
        {
            var value = condition.Date!;
            if (value.Relative == ViewRelative.Overdue)
            {
                return new ResolvedViewCondition(field.Name, "overdue") { OverdueBefore = context.Today, Cutoffs = await CutoffsAsync() };
            }

            var (from, to) = ViewDates.Resolve(value, context.Today, context.Profile.FirstWeekday);
            DateOnly? lower = null;
            DateOnly? upper = null; // exclusive
            switch (condition.Operator)
            {
                case "before":
                    if (from is null)
                    {
                        return new ResolvedViewCondition(field.Name, "never");
                    }

                    upper = from;
                    break;
                case "after":
                    if (to is null)
                    {
                        return new ResolvedViewCondition(field.Name, "never");
                    }

                    lower = to.Value.AddDays(1);
                    break;
                default: // on, inRange
                    lower = from;
                    upper = to?.AddDays(1);
                    break;
            }

            return new ResolvedViewCondition(field.Name, "range")
            {
                Lower = Bound(field, lower),
                Upper = Bound(field, upper),
            };
        }

        string? Bound(ViewFieldInfo field, DateOnly? date) => date is not { } day
            ? null
            : field.Type == ViewFieldType.Date
                ? StorageFormats.Date(day)
                : StorageFormats.Instant(TaskQueries.StartOfLocalDayUtc(context.Zone, day));

        return await GroupAsync(definition.Root);
    }

    /// <summary>
    /// The fields a view's quick add may pre-fill: only conditions directly under a top-level
    /// "all" group that fix a field to a value (PLAN2 §11.2). Conditions under "any" or nested
    /// groups are never used.
    /// </summary>
    public static ViewContextDefaults ContextDefaults(ViewDefinition definition, DateOnly today, DayOfWeek firstWeekday)
    {
        if (definition.Root.Match != ViewMatch.All)
        {
            return new ViewContextDefaults();
        }

        string? container = null;
        TaskItemStatus? status = null;
        TaskPriority? priority = null;
        CapacityCategory? category = null;
        var tags = new List<string>();
        DateOnly? planned = null;
        DateOnly? due = null;
        foreach (var condition in definition.Root.Items.OfType<ViewCondition>())
        {
            switch (condition.Field, condition.Operator)
            {
                case ("container", "is" or "in") when condition.Values.Count == 1:
                    container = condition.Values[0];
                    break;
                case ("status", "in") when condition.Values.Count == 1:
                    status = Enum.Parse<TaskItemStatus>(condition.Values[0]);
                    break;
                case ("priority", "is"):
                    priority = Enum.Parse<TaskPriority>(condition.Values[0]);
                    break;
                case ("category", "is"):
                    category = Enum.Parse<CapacityCategory>(condition.Values[0]);
                    break;
                case ("tag", "hasAnyOf" or "hasAllOf"):
                    foreach (var id in condition.Values.Where(id => !tags.Contains(id)))
                    {
                        tags.Add(id);
                    }

                    break;
                case ("planned" or "due", "on") when condition.Date is { } date
                    && ViewDates.Resolve(date, today, firstWeekday) is { From: { } from, To: { } to } && from == to:
                    if (condition.Field == "planned")
                    {
                        planned = from;
                    }
                    else
                    {
                        due = from;
                    }

                    break;
            }
        }

        return new ViewContextDefaults
        {
            ContainerId = container,
            Status = status,
            Priority = priority,
            Category = category,
            TagIds = tags,
            PlannedDate = planned,
            DueDate = due,
        };
    }
}
