using Chaos.Application.Abstractions;
using Chaos.Application.Reminders;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Windows.Activation;
using Chaos.Domain.Reminders;

namespace Chaos.Desktop.Tests.Activation;

public sealed class ShellActivationTests
{
    private const string Job = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string TaskId = "00000001-0000-0000-0000-000000000000";

    private static LaunchIntent Button(ReminderActivation action) => new(LaunchIntentKind.Reminder, JobId: Job, Generation: 7, Action: action);

    [Test]
    public async Task Capture_IsQueuedForTheWindowAndBringsItForward()
    {
        var shell = new ShellActivations((_, _) => throw new InvalidOperationException("not a reminder"));
        var forward = new List<bool>();
        shell.Handled += forward.Add;

        await shell.HandleAsync(LaunchIntent.Capture, CancellationToken.None);
        var taken = shell.Take();

        await Assert.That(taken.Single().Kind).IsEqualTo(ShellActivationKind.Capture);
        await Assert.That(forward).IsEquivalentTo(new[] { true });
        await Assert.That(shell.Take()).IsEmpty();
    }

    [Test]
    public async Task DoneButton_ActsWithoutTheWindow_AndRepeatsReuseTheOperationId()
    {
        var commands = new List<NotificationActionCommand>();
        var shell = new ShellActivations((command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(new NotificationActionResult(NotificationActionOutcome.Completed, TaskId, null, Replayed: commands.Count > 1));
        });
        var forward = new List<bool>();
        shell.Handled += forward.Add;

        await shell.HandleAsync(Button(ReminderActivation.Done), CancellationToken.None);
        await shell.HandleAsync(Button(ReminderActivation.Done), CancellationToken.None);

        await Assert.That(commands.Select(command => command.Action)).IsEquivalentTo(new[] { NotificationAction.Done, NotificationAction.Done });
        await Assert.That(commands[0].OperationId).IsEqualTo(commands[1].OperationId);
        await Assert.That(commands[0].Generation).IsEqualTo(7L);
        await Assert.That(forward).IsEquivalentTo(new[] { false, false });
        await Assert.That(shell.Take().All(item => item.Kind == ShellActivationKind.Notice)).IsTrue();
    }

    [Test]
    public async Task SnoozeButton_MapsToTheSnoozeChoice()
    {
        NotificationActionCommand? seen = null;
        var shell = new ShellActivations((command, _) =>
        {
            seen = command;
            return Task.FromResult(new NotificationActionResult(NotificationActionOutcome.Snoozed, TaskId, DateTimeOffset.UnixEpoch, false));
        });

        await shell.HandleAsync(Button(ReminderActivation.SnoozeTenMinutes), CancellationToken.None);

        await Assert.That(seen!.Action).IsEqualTo(NotificationAction.Snooze);
        await Assert.That(seen.Snooze).IsEqualTo(SnoozeChoice.TenMinutes);
    }

    [Test]
    public async Task DoneButton_OnTaskWithOpenSubtasks_OpensTheTaskInsteadOfDeciding()
    {
        var commands = new List<NotificationActionCommand>();
        var shell = new ShellActivations((command, _) =>
        {
            commands.Add(command);
            return command.Action == NotificationAction.Done
                ? throw AppException.Validation("openSubtaskChoice", "This task has open subtasks.")
                : Task.FromResult(new NotificationActionResult(NotificationActionOutcome.Opened, TaskId, null, false));
        });
        var forward = new List<bool>();
        shell.Handled += forward.Add;

        await shell.HandleAsync(Button(ReminderActivation.Done), CancellationToken.None);
        var taken = shell.Take().Single();

        await Assert.That(commands.Select(command => command.Action)).IsEquivalentTo(new[] { NotificationAction.Done, NotificationAction.Open });
        await Assert.That(commands[1].OperationId).IsNotEqualTo(commands[0].OperationId);
        await Assert.That(taken.Kind).IsEqualTo(ShellActivationKind.OpenTask);
        await Assert.That(taken.TaskId).IsEqualTo(TaskId);
        await Assert.That(forward).IsEquivalentTo(new[] { true });
    }

    [Test]
    public async Task StaleButton_ExplainsThatNothingWasDone()
    {
        var shell = new ShellActivations((_, _) => Task.FromResult(new NotificationActionResult(NotificationActionOutcome.Stale, TaskId, null, false)));

        await shell.HandleAsync(Button(ReminderActivation.Done), CancellationToken.None);

        await Assert.That(shell.Take().Single().Message!).Contains("Nothing was done");
    }

    [Test]
    public async Task Queue_KeepsOnlyTheNewestActivations()
    {
        var shell = new ShellActivations((_, _) => throw new InvalidOperationException("not a reminder"));

        for (var index = 0; index < ShellActivations.Capacity + 10; index++)
        {
            await shell.HandleAsync(LaunchIntent.Pad, CancellationToken.None);
        }

        var taken = shell.Take();
        await Assert.That(taken.Count).IsEqualTo(ShellActivations.Capacity);
        await Assert.That(taken[^1].Sequence).IsEqualTo((long)ShellActivations.Capacity + 10);
    }
}
