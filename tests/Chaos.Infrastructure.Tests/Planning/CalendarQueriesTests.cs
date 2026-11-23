using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Application.Views;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Tests.Planning;

/// <summary>The calendar range query against real SQLite (PLAN2 P4): entries, boundaries, repeats and day totals.</summary>
public sealed class CalendarQueriesTests
{
    private static readonly DateOnly Monday = new(2026, 11, 16);
    private static readonly DateOnly Sunday = new(2026, 11, 22);

    private static CalendarQueries QueriesOf(DataHost host) => new(host.Store, new TaskQueries(host.Store, host.Clock), host.Clock);

    private static async Task<TaskRecord> TaskAsync(DataHost host, string title, DateOnly? planned = null, TimeOnly? time = null, DateOnly? due = null,
        int? estimate = null, CapacityCategory category = CapacityCategory.Personal, TimeOnly? dueTime = null, TaskPriority priority = TaskPriority.Normal)
    {
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, title, null))).Task;
        return (await host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, task.Version, title, null, planned, time, time is null ? null : "UTC",
            due, dueTime, dueTime is null ? null : "UTC", priority, estimate, category))).Task;
    }

    [Test]
    public async Task Range_ReturnsPlannedBlocksDueMarkersAndKeepsBoundaries()
    {
        using var host = await DataHost.StartAsync();
        var first = await TaskAsync(host, "Monday", Monday);
        var last = await TaskAsync(host, "Sunday", Sunday, new TimeOnly(9, 0), estimate: 90);
        await TaskAsync(host, "Before", Monday.AddDays(-1));
        await TaskAsync(host, "After", Sunday.AddDays(1));
        var both = await TaskAsync(host, "Both", Monday.AddDays(1), due: Monday.AddDays(1));
        await TaskAsync(host, "Deadline", due: Monday.AddDays(2), dueTime: new TimeOnly(17, 0));

        var result = await QueriesOf(host).RangeAsync(Monday, Sunday, new CalendarFilters());

        await Assert.That(result.Entries.Count(entry => entry.Kind == CalendarEntryKind.Planned)).IsEqualTo(3);
        await Assert.That(result.Entries.Count(entry => entry.Kind == CalendarEntryKind.Due)).IsEqualTo(2);
        await Assert.That(result.Entries.Any(entry => entry.Title is "Before" or "After")).IsFalse();
        var block = result.Entries.Single(entry => entry.TaskId == last.Id);
        await Assert.That(block.IsBlock).IsTrue();
        await Assert.That(block.EndTime).IsEqualTo(new TimeOnly(10, 30));
        await Assert.That(result.Entries.Single(entry => entry.TaskId == first.Id).Time).IsNull();
        await Assert.That(result.Entries.Count(entry => entry.TaskId == both.Id)).IsEqualTo(2);
        await Assert.That(result.Entries.Single(entry => entry.Title == "Deadline").Kind).IsEqualTo(CalendarEntryKind.Due);
        await Assert.That(result.Days.Count).IsEqualTo(7);
        await Assert.That(result.Meetings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TimedTaskWithoutEstimate_IsNotABlock_RunsThirtyMinutes_AndIsClampedToItsDay()
    {
        using var host = await DataHost.StartAsync();
        var plain = await TaskAsync(host, "Call", Monday, new TimeOnly(14, 0));
        var late = await TaskAsync(host, "Late", Monday, new TimeOnly(23, 0), estimate: 240);

        var result = await QueriesOf(host).RangeAsync(Monday, Monday, new CalendarFilters());

        var call = result.Entries.Single(entry => entry.TaskId == plain.Id);
        await Assert.That(call.IsBlock).IsFalse();
        await Assert.That(call.EndTime).IsEqualTo(new TimeOnly(14, 30));
        await Assert.That(result.Entries.Single(entry => entry.TaskId == late.Id).EndTime).IsEqualTo(new TimeOnly(23, 59));
    }

    [Test]
    public async Task Range_RefusesMoreThan62Days_AndReversedRanges()
    {
        using var host = await DataHost.StartAsync();
        var queries = QueriesOf(host);

        await Assert.That(async () => await queries.RangeAsync(Monday, Monday.AddDays(62), new CalendarFilters())).Throws<AppException>();
        var ok = await queries.RangeAsync(Monday, Monday.AddDays(61), new CalendarFilters());
        await Assert.That(ok.Days.Count).IsEqualTo(62);
        await Assert.That(async () => await queries.RangeAsync(Sunday, Monday, new CalendarFilters())).Throws<AppException>();
    }

    [Test]
    public async Task Filters_FocusPlannedDueCompletedAndContainer()
    {
        using var host = await DataHost.StartAsync();
        var work = await TaskAsync(host, "Work", Monday, category: CapacityCategory.Work, due: Monday);
        await TaskAsync(host, "Home", Monday);
        var done = await TaskAsync(host, "Finished", Monday);
        await host.Tasks.SetStatusAsync(new(DataHost.Op(), done.Id, done.Version, TaskItemStatus.Done, null));
        var queries = QueriesOf(host);

        var workOnly = await queries.RangeAsync(Monday, Monday, new CalendarFilters { Category = CapacityCategory.Work });
        await Assert.That(workOnly.Entries.Select(entry => entry.TaskId).Distinct().Single()).IsEqualTo(work.Id);

        var plannedOnly = await queries.RangeAsync(Monday, Monday, new CalendarFilters { ShowDue = false });
        await Assert.That(plannedOnly.Entries.Any(entry => entry.Kind == CalendarEntryKind.Due)).IsFalse();
        await Assert.That(plannedOnly.Entries.Any(entry => entry.Title == "Finished")).IsFalse();

        var withCompleted = await queries.RangeAsync(Monday, Monday, new CalendarFilters { ShowCompleted = true, ShowDue = false });
        await Assert.That(withCompleted.Entries.Any(entry => entry.Title == "Finished")).IsTrue();

        var dueOnly = await queries.RangeAsync(Monday, Monday, new CalendarFilters { ShowPlanned = false });
        await Assert.That(dueOnly.Entries.Select(entry => entry.Kind).Distinct().Single()).IsEqualTo(CalendarEntryKind.Due);

        var otherList = await queries.RangeAsync(Monday, Monday, new CalendarFilters { ContainerId = "no-such-list" });
        await Assert.That(otherList.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Totals_UseCapacityAndAvailability_AndIgnoreFiltersOtherThanFocus()
    {
        using var host = await DataHost.StartAsync();
        // The weekday default is 8 hours, so 5 h and 4 h planned on Tuesday overload it; one task has no estimate.
        var tuesday = Monday.AddDays(1);
        await TaskAsync(host, "A", tuesday, estimate: 300, category: CapacityCategory.Work);
        await TaskAsync(host, "B", tuesday, estimate: 240);
        await TaskAsync(host, "C", tuesday);
        await TaskAsync(host, "Weekend", Sunday, estimate: 30);

        var result = await QueriesOf(host).RangeAsync(Monday, Sunday, new CalendarFilters { ContainerId = "no-such-list" });

        var day = result.Days.Single(item => item.Date == tuesday);
        await Assert.That(day.Load.PlannedMinutes).IsEqualTo(540);
        await Assert.That(day.Load.UnknownEstimates).IsEqualTo(1);
        await Assert.That(day.Load.OverTotal).IsTrue();
        await Assert.That(day.Availability.TotalMinutes).IsEqualTo(480);
        await Assert.That(result.Days.Single(item => item.Date == Sunday).Load.OverTotal).IsTrue();
        await Assert.That(result.Days.Single(item => item.Date == Monday).Load.OverTotal).IsFalse();

        var personal = await QueriesOf(host).RangeAsync(Monday, Sunday, new CalendarFilters { Category = CapacityCategory.Personal });
        await Assert.That(personal.Days.Single(item => item.Date == tuesday).Load.PlannedMinutes).IsEqualTo(240);
    }

    [Test]
    public async Task Repeats_AreProjectedInTheRange_ReadOnly_AndSkipTodayAndEarlier()
    {
        using var host = await DataHost.StartAsync();
        var today = (await QueriesOf(host).RangeAsync(Monday, Monday, new CalendarFilters())).Today;
        var task = await TaskAsync(host, "Water plants", today);
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 2, today, new TimeOnly(8, 0), "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        await new RecurrenceCommands(host.Store, host.Ids, host.Clock).StartAsync(new(DataHost.Op(), task.Id, task.Version, rule));

        var result = await QueriesOf(host).RangeAsync(today, today.AddDays(6), new CalendarFilters());

        var repeats = result.Entries.Where(entry => entry.Kind == CalendarEntryKind.Repeat).ToList();
        await Assert.That(repeats.Select(entry => entry.Date).ToArray()).IsEquivalentTo(new[] { today.AddDays(2), today.AddDays(4), today.AddDays(6) });
        await Assert.That(repeats.All(entry => entry.TaskId == task.Id && entry.Time == new TimeOnly(8, 0))).IsTrue();

        var past = await QueriesOf(host).RangeAsync(today.AddDays(-10), today.AddDays(-1), new CalendarFilters());
        await Assert.That(past.Entries.Any(entry => entry.Kind == CalendarEntryKind.Repeat)).IsFalse();
    }

    [Test]
    public async Task Repeats_ProjectForSelectedSharedSpaceOnlyWhileMembershipIsLive()
    {
        using var host = await DataHost.StartAsync();
        var today = (await QueriesOf(host).RangeAsync(Monday, Monday, new CalendarFilters())).Today;
        var task = await TaskAsync(host, "Shared repeat", today);
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 2, today, null, "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var series = (await new RecurrenceCommands(host.Store, host.Ids, host.Clock).StartAsync(new(DataHost.Op(), task.Id, task.Version, rule))).Series;
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc)
                    SELECT 'team',Id,2,'Team','2026-11-10T09:00:00Z','2026-11-10T09:00:00Z' FROM Profiles LIMIT 1;
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc)
                    SELECT 'member','team',Id,1,'Me',2,'now' FROM Profiles LIMIT 1;
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc)
                    VALUES ('team-list','team',1,'Team','team',0,'2026-11-10T09:00:00Z','2026-11-10T09:00:00Z');
                UPDATE Tasks SET SpaceId='team',ContainerId='team-list' WHERE Id=$task;
                UPDATE RecurrenceSeries SET SpaceId='team' WHERE Id=$series;
                """;
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$series", series.Id);
            await command.ExecuteNonQueryAsync(token);
            return true;
        });
        var queries = QueriesOf(host);
        var team = await queries.RangeAsync(today, today.AddDays(4), new CalendarFilters { SpaceId = "team" });
        await Assert.That(team.Entries.Where(entry => entry.Kind == CalendarEntryKind.Repeat).Select(entry => entry.Date))
            .IsEquivalentTo([today.AddDays(2), today.AddDays(4)]);
        var personal = await queries.RangeAsync(today, today.AddDays(4), new CalendarFilters { SpaceId = task.SpaceId });
        await Assert.That(personal.Entries.Any(entry => entry.TaskId == task.Id)).IsFalse();
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE SpaceMembers SET RemovedAtUtc='2026-11-10T10:00:00Z' WHERE Id='member';";
            await command.ExecuteNonQueryAsync(token);
            return true;
        });
        var removed = await queries.RangeAsync(today, today.AddDays(4), new CalendarFilters { SpaceId = "team" });
        await Assert.That(removed.Entries).IsEmpty();
    }

    [Test]
    public async Task Unscheduled_ListsOpenTasksWithoutAPlannedDate_PagedAndFiltered()
    {
        using var host = await DataHost.StartAsync();
        await TaskAsync(host, "Scheduled", Monday);
        var loose = await TaskAsync(host, "Loose end", due: Sunday);
        var work = await TaskAsync(host, "Work item", category: CapacityCategory.Work);
        var done = await TaskAsync(host, "Done item");
        await host.Tasks.SetStatusAsync(new(DataHost.Op(), done.Id, done.Version, TaskItemStatus.Done, null));
        var queries = QueriesOf(host);

        var all = await queries.UnscheduledAsync(new CalendarFilters(), null, null, 50);
        await Assert.That(all.Rows.Select(row => row.Task.Id).Order().ToArray()).IsEquivalentTo(new[] { loose.Id, work.Id }.Order().ToArray());

        var focus = await queries.UnscheduledAsync(new CalendarFilters { Category = CapacityCategory.Work }, null, null, 50);
        await Assert.That(focus.Rows.Single().Task.Id).IsEqualTo(work.Id);

        var firstPage = await queries.UnscheduledAsync(new CalendarFilters(), null, null, 1);
        await Assert.That(firstPage.Rows.Count).IsEqualTo(1);
        await Assert.That(firstPage.NextCursor).IsNotNull();
        var secondPage = await queries.UnscheduledAsync(new CalendarFilters(), null, firstPage.NextCursor, 1);
        await Assert.That(secondPage.Rows.Single().Task.Id).IsNotEqualTo(firstPage.Rows.Single().Task.Id);
    }

    [Test]
    public async Task ViewLayout_FiltersTheCalendarByTheViewsConditions()
    {
        using var host = await DataHost.StartAsync();
        await TaskAsync(host, "High", Monday, priority: TaskPriority.High);
        await TaskAsync(host, "Normal", Monday);
        var views = new SavedViewCommands(host.Store, host.Ids, host.Clock);
        var view = (await views.SaveAsync(new(DataHost.Op(), null, 0, "High only", "list", null,
            "{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"}]}", "{\"v\":1,\"kind\":\"calendar\"}", true, false))).View;

        var result = await QueriesOf(host).RangeAsync(Monday, Monday, new CalendarFilters { ViewId = view.Id });

        await Assert.That(result.Entries.Select(entry => entry.Title).Distinct().Single()).IsEqualTo("High");
    }
}
