using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Application.Tests.Testing;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tests.Tasks;

/// <summary>
/// Command intent over fake ports (PLAN §16): receipts replay/conflict, normalization,
/// Inbox targeting and history/receipt writes. Real SQLite behavior is proven in
/// Chaos.Infrastructure.Tests.
/// </summary>
public sealed class TaskCommandsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static TaskCommands NewCommands(InMemoryStore store) =>
        new(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));

    private static (string ProfileId, string SpaceId, string InboxId) Seed(InMemoryStore store)
    {
        var profile = new ProfileRecord
        {
            Id = "profile-1",
            DisplayName = "Me",
            ZoneId = "UTC",
            Locale = "en-US",
            DateFormat = "MDY",
            FirstWeekday = DayOfWeek.Sunday,
            DefaultReminderTime = new TimeOnly(9, 0),
            Version = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var space = new SpaceRecord
        {
            Id = "space-1",
            OwnerProfileId = profile.Id,
            Kind = SpaceKind.Personal,
            Name = "Personal",
            Version = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var inbox = new ContainerRecord
        {
            Id = "inbox-1",
            SpaceId = space.Id,
            Kind = ContainerKind.Inbox,
            Name = "Inbox",
            IsInbox = true,
            Position = 0,
            Version = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        store.Profiles.Add(profile);
        store.Spaces.Add(space);
        store.Containers.Add(inbox);
        return (profile.Id, space.Id, inbox.Id);
    }

    [Test]
    public async Task CaptureAsync_EmptyContainerId_TargetsPersonalInbox()
    {
        var store = new InMemoryStore();
        var seeded = Seed(store);
        var commands = NewCommands(store);

        var result = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk", null));

        await Assert.That(result.Replayed).IsFalse();
        await Assert.That(result.Task.ContainerId).IsEqualTo(seeded.InboxId);
        await Assert.That(result.Task.SpaceId).IsEqualTo(seeded.SpaceId);
        await Assert.That(result.Task.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await Assert.That(result.Task.Version).IsEqualTo(1L);
    }

    [Test]
    public async Task CaptureAsync_ExteriorWhitespace_TitleIsTrimmed()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);

        var result = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "  Buy milk\t ", null));

        await Assert.That(result.Task.Title).IsEqualTo("Buy milk");
    }

    [Test]
    public async Task CaptureAsync_UnicodeTitleAndNotes_Preserved()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);
        const string title = "تذكير: تسليم التقرير النهائي 📝✅";
        const string notes = "Line 1\nLine 2 — السبّاك 🛠️";

        var result = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, title, notes));

        await Assert.That(result.Task.Title).IsEqualTo(title);
        await Assert.That(result.Task.Notes).IsEqualTo(notes);
        var fetched = await commands.GetAsync(result.Task.Id);
        await Assert.That(fetched).IsEqualTo(result.Task);
    }

    [Test]
    public async Task CaptureAsync_SameOperationIdSamePayload_ReplaysOriginalResult()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);

        var first = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "  Buy milk ", null));
        var second = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk", null));

        await Assert.That(second.Replayed).IsTrue();
        await Assert.That(second.Task.Id).IsEqualTo(first.Task.Id);
        await Assert.That(store.Tasks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CaptureAsync_PackageOneReceipt_ReplaysAfterUpgrade()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);
        var command = new CaptureTaskCommand("op-legacy", string.Empty, "Buy milk", null);
        var first = await commands.CaptureAsync(command);
        store.Receipts[0] = store.Receipts[0] with { ResultJson = StoredJsonFormats.Snapshot(first.Task) };

        var replay = await commands.CaptureAsync(command);

        await Assert.That(replay.Replayed).IsTrue();
        await Assert.That(replay.Task.Id).IsEqualTo(first.Task.Id);
        await Assert.That(store.Tasks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CaptureAsync_SameOperationIdDifferentPayload_ThrowsOperationConflict()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);
        await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk", null));

        AppException? caught = null;
        try
        {
            await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk instead", null));
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Kind).IsEqualTo(AppErrorKind.OperationConflict);
        await Assert.That(store.Tasks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CaptureAsync_DifferentOperationIdSamePayload_CreatesSecondTask()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);

        var first = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk", null));
        var second = await commands.CaptureAsync(new CaptureTaskCommand("op-2", string.Empty, "Buy milk", null));

        await Assert.That(second.Replayed).IsFalse();
        await Assert.That(second.Task.Id).IsNotEqualTo(first.Task.Id);
        await Assert.That(second.Task.Position).IsGreaterThan(first.Task.Position);
        await Assert.That(store.Tasks.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CaptureAsync_BlankTitle_ThrowsValidationOnTitlePath()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);

        AppException? caught = null;
        try
        {
            await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "   ", null));
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(caught.Fields.Single().FieldPath).IsEqualTo("title");
        await Assert.That(store.Tasks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CaptureAsync_UnknownContainerId_ThrowsNotFound()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);

        AppException? caught = null;
        try
        {
            await commands.CaptureAsync(new CaptureTaskCommand("op-1", "container-gone", "Buy milk", null));
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Kind).IsEqualTo(AppErrorKind.NotFound);
    }

    [Test]
    public async Task CaptureAsync_WithoutFirstRunSeed_ThrowsStorageUnavailable()
    {
        var store = new InMemoryStore();
        var commands = NewCommands(store);

        AppException? caught = null;
        try
        {
            await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk", null));
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Kind).IsEqualTo(AppErrorKind.StorageUnavailable);
    }

    [Test]
    public async Task CaptureAsync_WritesHistorySnapshotAndReceipt()
    {
        var store = new InMemoryStore();
        var seeded = Seed(store);
        var commands = NewCommands(store);

        var result = await commands.CaptureAsync(new CaptureTaskCommand("op-1", string.Empty, "Buy milk", null));

        await Assert.That(store.History.Count).IsEqualTo(1);
        var history = store.History.Single();
        await Assert.That(history.TaskId).IsEqualTo(result.Task.Id);
        await Assert.That(history.EventKind).IsEqualTo("task.created");
        await Assert.That(history.AggregateVersion).IsEqualTo(1L);

        await Assert.That(store.Receipts.Count).IsEqualTo(1);
        var receipt = store.Receipts.Single();
        await Assert.That(receipt.ProfileId).IsEqualTo(seeded.ProfileId);
        await Assert.That(receipt.OperationId).IsEqualTo("op-1");
        await Assert.That(receipt.CommandKind).IsEqualTo("task.capture");
        await Assert.That(receipt.PayloadHash.Length).IsEqualTo(64);
    }

    [Test]
    public async Task GetAsync_UnknownTaskId_ReturnsNull()
    {
        var store = new InMemoryStore();
        Seed(store);
        var commands = NewCommands(store);

        var fetched = await commands.GetAsync("no-such-task");

        await Assert.That(fetched).IsNull();
    }
}
