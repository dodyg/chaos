using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.People;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Application.Tests.Testing;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Tests.Tasks;

/// <summary>
/// Delegation intent over fake ports (PLAN3 9.11): the commands obey the transition table,
/// each move writes its diary entry, declining returns the task, review is explicit, nudges
/// are limited, and the delegator's follow-up ends with the delegation. The commands act as
/// the store's primary profile, so "answering as Omar" swaps whose profile is primary.
/// </summary>
public sealed class DelegationCommandsTests
{
    private const string Me = "profile-1"; // TaskCommandsTests.Seed
    private const string Omar = "omar";
    private const string Viewer = "viewer";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private readonly SequentialIdGenerator _ids = new();

    private DelegationCommands New(InMemoryStore store) => new(store, _ids, new FixedTimeProvider(Now));

    private TaskCommands Tasks(InMemoryStore store) => new(store, _ids, new FixedTimeProvider(Now));

    /// <summary>A shared space where Sara (the primary profile) owns and Omar edits; the task sits in its list.</summary>
    private async Task<TaskRecord> SharedTaskAsync(InMemoryStore store, string operationId = "capture-shared")
    {
        var seeded = TaskCommandsTests.Seed(store);
        store.Spaces.Add(new SpaceRecord { Id = "team", OwnerProfileId = seeded.ProfileId, Kind = SpaceKind.Shared, Name = "Team", Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now });
        store.Containers.Add(new ContainerRecord { Id = "team-list", SpaceId = "team", Kind = ContainerKind.List, Name = "Work", Position = 0, Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now });
        store.People.Add(("team", new SpacePerson("person:" + seeded.ProfileId, "Sara", 1, false, null)));
        store.People.Add(("team", new SpacePerson("person:" + Omar, "Omar", 2, false, null)));
        store.People.Add(("team", new SpacePerson("person:" + Viewer, "Vera", 3, false, null)));
        return (await Tasks(store).CaptureAsync(new(operationId, "team-list", "Send the quote", null))).Task;
    }

    private async Task<TaskDelegationRecord> DelegatedAsync(InMemoryStore store, bool review = false, FollowUpSpec? followUp = null)
    {
        var task = await SharedTaskAsync(store, "capture-" + Guid.NewGuid().ToString("N"));
        await New(store).DelegateAsync(new DelegateCommand("delegate", task.Id, task.Version, Omar, "Please handle the venue", "Quote attached and sent", new DateOnly(2026, 11, 30), review, followUp));
        return store.Delegations.Single();
    }

    private static async Task<AppException> RefusedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (AppException error) { return error; }
        throw new InvalidOperationException("The command was expected to be refused.");
    }

    private static void ActAs(InMemoryStore store, string id, string name)
    {
        store.Profiles.Clear();
        store.Profiles.Add(new ProfileRecord
        {
            Id = id, DisplayName = name, ZoneId = "UTC", Locale = "en-US", DateFormat = "MDY", FirstWeekday = DayOfWeek.Sunday,
            DefaultReminderTime = new TimeOnly(9, 0), Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now,
        });
    }

    [Test]
    public async Task Delegate_SharedTask_MakesRecipientSoleAssigneeRecordsBriefDiaryFollowUpAndHistory()
    {
        var store = new InMemoryStore();
        var task = await SharedTaskAsync(store);
        await store.ExecuteAsync(async (session, token) =>
        {
            await session.Tasks.SaveAssigneeAsync(new TaskAssigneeRecord(task.Id, Viewer, Now, null, null), token);
            return 0;
        });
        task = store.Tasks.Single(t => t.Id == task.Id);
        var command = new DelegateCommand("delegate", task.Id, task.Version, Omar, " Please handle the venue ", "Quote attached", new DateOnly(2026, 11, 30), true,
            new FollowUpSpec(FollowUpKind.Fixed, Now.AddDays(2), null, null));
        var result = await New(store).DelegateAsync(command);

        var delegation = result.Delegation;
        await Assert.That(delegation.State).IsEqualTo(DelegationState.Pending);
        await Assert.That((delegation.FromMemberId, delegation.ToMemberId)).IsEqualTo((Me, Omar));
        await Assert.That((delegation.Message, delegation.DoneDefinition, delegation.ReviewRequired, delegation.DueDate)).IsEqualTo(("Please handle the venue", "Quote attached", true, new DateOnly(2026, 11, 30)));
        await Assert.That(store.TaskAssignees.Where(a => a.TaskId == task.Id && a.DeletedAtUtc is null).Select(a => a.MemberId)).IsEquivalentTo([Omar]);
        var entry = store.Diary.Single(e => e.TaskId == task.Id);
        await Assert.That(entry.RawMarkdown).IsEqualTo("Delegated to Omar · due 2026-11-30 · review needed\n\nPlease handle the venue\n\nDone looks like: Quote attached");
        await Assert.That(entry.AuthorProfileId).IsEqualTo(Me);
        var followUp = store.FollowUps.Single();
        await Assert.That((followUp.DelegationId, followUp.Kind, followUp.FixedAtUtc)).IsEqualTo((delegation.Id, FollowUpKind.Fixed, Now.AddDays(2)));
        await Assert.That(store.History.Single(h => h.EventKind == "task.delegated").ChangedFields).Contains("Assignees");
        await Assert.That(store.Receipts.Single(r => r.CommandKind == "delegation.delegate").OperationId).IsEqualTo("delegate");
        var replay = await New(store).DelegateAsync(command);
        await Assert.That(replay.Replayed).IsTrue();
        await Assert.That(store.Delegations).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Delegate_AfterWaitingFollowUp_UsesDelegationStart()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store, followUp: new FollowUpSpec(FollowUpKind.AfterWaiting, null, 2, DurationUnit.Days));

        var view = await New(store).GetForTaskAsync(delegation.TaskId);

        await Assert.That(view.Live.Single().FollowUpDueAtUtc).IsEqualTo(Now.AddDays(2));
    }

    [Test]
    public async Task Delegate_PersonalTask_ViewerRecipientOrSelf_IsRefused()
    {
        var store = new InMemoryStore();
        var seeded = TaskCommandsTests.Seed(store);
        var personal = (await Tasks(store).CaptureAsync(new("capture-personal", "", "Mine", null))).Task;
        var error = await RefusedAsync(() => New(store).DelegateAsync(new("d1", personal.Id, personal.Version, Omar, null, null, null, false, null)));
        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(error.Fields.Single().FieldPath).IsEqualTo("taskId");

        var shared = await SharedTaskAsync(store);
        await Assert.That((await RefusedAsync(() => New(store).DelegateAsync(new("d2", shared.Id, shared.Version, Viewer, null, null, null, false, null)))).Fields.Single().FieldPath).IsEqualTo("toMemberId");
        await Assert.That((await RefusedAsync(() => New(store).DelegateAsync(new("d3", shared.Id, shared.Version, seeded.ProfileId, null, null, null, false, null)))).Fields.Single().FieldPath).IsEqualTo("toMemberId");
        await Assert.That((await RefusedAsync(() => New(store).DelegateAsync(new("d4", shared.Id, shared.Version, Omar, null, null, null, false, new FollowUpSpec(FollowUpKind.BeforeDue, null, 1, DurationUnit.Days))))).Fields.Single().FieldPath).IsEqualTo("followUp");
        await Assert.That(store.Delegations).IsEmpty();
    }

    [Test]
    public async Task Delegate_TaskAlreadyDelegated_EndsTheEarlierDelegationAndItsFollowUp()
    {
        var store = new InMemoryStore();
        var first = await DelegatedAsync(store, followUp: new FollowUpSpec(FollowUpKind.Fixed, Now.AddDays(1), null, null));
        store.People.Add(("team", new SpacePerson("person:ada", "Ada", 2, false, null)));
        var task = store.Tasks.Single(t => t.Id == first.TaskId);
        await New(store).DelegateAsync(new("again", task.Id, task.Version, "ada", null, null, null, false, null));
        await Assert.That(store.Delegations.Single(d => d.Id == first.Id).State).IsEqualTo(DelegationState.Withdrawn);
        await Assert.That(store.FollowUps.Single().ResolvedAtUtc).IsEqualTo(Now);
        await Assert.That(store.TaskAssignees.Where(a => a.DeletedAtUtc is null).Select(a => a.MemberId)).IsEquivalentTo(["ada"]);
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Delegated to Ada");
        await Assert.That(store.Undo.Select(u => u.OperationId)).Contains("again");
    }

    [Test]
    public async Task Respond_Decline_ReturnsTaskToDelegatorWithTheReason()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store, followUp: new FollowUpSpec(FollowUpKind.Fixed, Now.AddDays(1), null, null));
        ActAs(store, Omar, "Omar");
        var result = await New(store).RespondAsync(new("decline", delegation.Id, delegation.Version, DelegationResponse.Decline, "Too busy this week"));

        await Assert.That(result.Delegation.State).IsEqualTo(DelegationState.Declined);
        await Assert.That(result.Delegation.Response).IsEqualTo("Too busy this week");
        await Assert.That(store.TaskAssignees.Where(a => a.DeletedAtUtc is null).Select(a => a.MemberId)).IsEquivalentTo([Me]);
        await Assert.That(store.TaskAssignees.Single(a => a.MemberId == Omar).DeletedAtUtc).IsEqualTo(Now);
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Declined the delegation from Sara: Too busy this week");
        await Assert.That(store.FollowUps.Single().ResolvedAtUtc).IsEqualTo(Now);
        await Assert.That(store.History.Last().EventKind).IsEqualTo("task.delegation-decline");
        var view = await New(store).ListAsync(DelegationSide.From);
        await Assert.That(view).IsEmpty();
    }

    [Test]
    public async Task Respond_OnlyTheRecipientAnswers_AndMovesFollowTheTable()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store);
        var asSara = await RefusedAsync(() => New(store).RespondAsync(new("x", delegation.Id, delegation.Version, DelegationResponse.Accept, null)));
        await Assert.That(asSara.Fields.Single().FieldPath).IsEqualTo("delegationId");

        ActAs(store, Omar, "Omar");
        var commands = New(store);
        var blank = await RefusedAsync(() => commands.RespondAsync(new("q", delegation.Id, delegation.Version, DelegationResponse.Question, " ")));
        await Assert.That(blank.Fields.Single().FieldPath).IsEqualTo("text");
        var accepted = await commands.RespondAsync(new("accept", delegation.Id, delegation.Version, DelegationResponse.Accept, null));
        await Assert.That(accepted.Delegation.State).IsEqualTo(DelegationState.Accepted);
        await Assert.That((await RefusedAsync(() => commands.RespondAsync(new("again", delegation.Id, accepted.Delegation.Version, DelegationResponse.Accept, null)))).Fields.Single().FieldPath).IsEqualTo("delegationId");
        var question = await commands.RespondAsync(new("question", delegation.Id, accepted.Delegation.Version, DelegationResponse.Question, "Which venue?"));
        await Assert.That(question.Delegation.State).IsEqualTo(DelegationState.Question);
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Asked about the task: Which venue?");
        var declined = await commands.RespondAsync(new("decline", delegation.Id, question.Delegation.Version, DelegationResponse.Decline, "Never mind"));
        await Assert.That(declined.Delegation.State).IsEqualTo(DelegationState.Declined);
        await Assert.That(store.TaskAssignees.Where(a => a.DeletedAtUtc is null).Select(a => a.MemberId)).IsEquivalentTo([Me]);
        await Assert.That((await RefusedAsync(() => commands.RespondAsync(new("after", delegation.Id, declined.Delegation.Version, DelegationResponse.Accept, null)))).Fields.Single().FieldPath).IsEqualTo("delegationId");
        await Assert.That(await commands.ListAsync(DelegationSide.To)).IsEmpty();
    }

    [Test]
    public async Task Done_WithReview_SetsNeedsReview_ThenOnlyTheDelegatorCloses()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store, review: true);
        ActAs(store, Omar, "Omar");
        var commands = New(store);
        await Assert.That((await RefusedAsync(() => commands.DoneAsync(new("early", delegation.Id, delegation.Version, null, null)))).Fields.Single().FieldPath).IsEqualTo("delegationId");
        await commands.RespondAsync(new("accept", delegation.Id, delegation.Version, DelegationResponse.Accept, null));
        delegation = store.Delegations.Single();
        var done = await commands.DoneAsync(new("done", delegation.Id, delegation.Version, "Quote sent", null));
        await Assert.That(done.Delegation.State).IsEqualTo(DelegationState.Done);
        await Assert.That(done.Task.Status).IsEqualTo(TaskItemStatus.NeedsReview);
        await Assert.That(done.Task.CompletedAtUtc).IsNull();
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Reported done, ready for review: Quote sent");
        await Assert.That((await RefusedAsync(() => commands.CloseAsync(new("x", delegation.Id, done.Delegation.Version)))).Fields.Single().FieldPath).IsEqualTo("delegationId");
        await Assert.That((await RefusedAsync(() => commands.ReopenAsync(new("y", delegation.Id, done.Delegation.Version)))).Fields.Single().FieldPath).IsEqualTo("delegationId");

        ActAs(store, Me, "Sara");
        var closed = await New(store).CloseAsync(new("close", delegation.Id, done.Delegation.Version));
        await Assert.That(closed.Delegation.State).IsEqualTo(DelegationState.Closed);
        await Assert.That(closed.Task.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(closed.Task.CompletedAtUtc).IsEqualTo(Now);
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Accepted the result and closed the delegation.");
    }

    [Test]
    public async Task Done_WithoutReview_CompletesTheTask_AndTheDelegatorCanSendItBack()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store, review: false, followUp: new FollowUpSpec(FollowUpKind.Fixed, Now.AddDays(1), null, null));
        ActAs(store, Omar, "Omar");
        var commands = New(store);
        await commands.RespondAsync(new("accept", delegation.Id, delegation.Version, DelegationResponse.Accept, null));
        delegation = store.Delegations.Single();
        var done = await commands.DoneAsync(new("done", delegation.Id, delegation.Version, null, null));
        await Assert.That(done.Task.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(store.FollowUps.Single().ResolvedAtUtc).IsEqualTo(Now);
        await Assert.That((await RefusedAsync(() => commands.DoneAsync(new("twice", delegation.Id, done.Delegation.Version, null, null)))).Fields.Single().FieldPath).IsEqualTo("delegationId");

        ActAs(store, Me, "Sara");
        var reopened = await New(store).ReopenAsync(new("reopen", delegation.Id, done.Delegation.Version));
        await Assert.That(reopened.Delegation.State).IsEqualTo(DelegationState.Accepted);
        await Assert.That(TaskItemStatusRules.IsTerminal(reopened.Task.Status)).IsFalse();
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Sent the result back for more work.");
    }

    [Test]
    public async Task Withdraw_ByDelegator_TakesTheTaskBackAndEndsTheFollowUp()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store, followUp: new FollowUpSpec(FollowUpKind.Fixed, Now.AddDays(1), null, null));
        var result = await New(store).WithdrawAsync(new("withdraw", delegation.Id, delegation.Version));
        await Assert.That(result.Delegation.State).IsEqualTo(DelegationState.Withdrawn);
        await Assert.That(store.TaskAssignees.Where(a => a.DeletedAtUtc is null).Select(a => a.MemberId)).IsEquivalentTo([Me]);
        await Assert.That(store.FollowUps.Single().ResolvedAtUtc).IsEqualTo(Now);
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Took the task back from Omar.");
        var view = await New(store).GetForTaskAsync(result.Task.Id);
        await Assert.That(view.Live).IsEmpty();
        await Assert.That(view.History.Single().Delegation.Id).IsEqualTo(delegation.Id);
        await Assert.That(view.History.Single().ToName).IsEqualTo("Omar");
    }

    [Test]
    public async Task Nudge_UsesTheTemplate_IsLimitedToThreeADay_AndWritesNoUndoEntry()
    {
        var store = new InMemoryStore();
        var delegation = await DelegatedAsync(store);
        var undoEntries = store.Undo.Count;
        var commands = New(store);
        var template = await commands.NudgeTemplateAsync(delegation.Id);
        await Assert.That(template).IsEqualTo("Hi Omar, any news on \"Send the quote\"?");
        var first = await commands.NudgeAsync(new("n1", delegation.Id, delegation.Version, null));
        await Assert.That(first.Delegation.State).IsEqualTo(DelegationState.Pending);
        await Assert.That(store.Nudges.Single().Text).IsEqualTo(template);
        await Assert.That(store.Nudges.Single().ToMemberId).IsEqualTo(Omar);
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Nudged Omar.");
        await commands.NudgeAsync(new("n2", delegation.Id, delegation.Version, "Any news?"));
        await Assert.That(store.Diary.Last().RawMarkdown).IsEqualTo("Nudged Omar. Any news?");
        await commands.NudgeAsync(new("n3", delegation.Id, delegation.Version, null));
        var error = await RefusedAsync(() => commands.NudgeAsync(new("n4", delegation.Id, delegation.Version, null)));
        await Assert.That(error.Message).Contains("3 times in the last 24 hours");
        await Assert.That(store.Nudges).Count().IsEqualTo(3);
        await Assert.That(store.Undo).Count().IsEqualTo(undoEntries);
        await Assert.That(store.History.Last().EventKind).IsEqualTo("task.nudged");

        ActAs(store, Omar, "Omar");
        await Assert.That((await RefusedAsync(() => New(store).NudgeAsync(new("n5", delegation.Id, delegation.Version, null)))).Fields.Single().FieldPath).IsEqualTo("delegationId");
    }

    [Test]
    public async Task NudgeTemplates_FollowTheLocale_AndFallBackToEnglish()
    {
        await Assert.That(NudgeTemplates.For("fr-FR", "Omar", "Devis")).IsEqualTo("Bonjour Omar, des nouvelles de « Devis » ?");
        await Assert.That(NudgeTemplates.For("ar", "Omar", "Quote")).StartsWith("مرحبًا Omar");
        await Assert.That(NudgeTemplates.For("de-DE", "Omar", "Quote")).IsEqualTo("Hi Omar, any news on \"Quote\"?");
        await Assert.That(NudgeTemplates.For(null, "Omar", "Quote")).IsEqualTo("Hi Omar, any news on \"Quote\"?");
    }

    [Test]
    public async Task Undo_Delegate_TombstonesTheDelegationAndRestoresTheAssignee()
    {
        var store = new InMemoryStore();
        var task = await SharedTaskAsync(store);
        await store.ExecuteAsync(async (session, token) =>
        {
            await session.Tasks.SaveAssigneeAsync(new TaskAssigneeRecord(task.Id, Me, Now, null, null), token);
            return 0;
        });
        task = store.Tasks.Single(t => t.Id == task.Id);
        await New(store).DelegateAsync(new("delegate", task.Id, task.Version, Omar, null, null, null, false, null));
        var entry = store.Undo.Single(e => e.OperationId == "delegate");
        await Assert.That(entry.Label).IsEqualTo("Delegate “Send the quote”");
        await new UndoCommands(store, _ids, new FixedTimeProvider(Now)).UndoAsync(new("undo", "delegate"));
        await Assert.That(store.Delegations.Single().DeletedAtUtc).IsEqualTo(Now);
        await Assert.That(store.TaskAssignees.Where(a => a.DeletedAtUtc is null).Select(a => a.MemberId)).IsEquivalentTo([Me]);
    }
}
