using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tags;
using Chaos.Application.Tests.Testing;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tests.Tags;

/// <summary>Tag command intent over fake ports: validation before writes, receipt replay and conflict, tree ordering.</summary>
public sealed class TagCommandsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static (InMemoryStore Store, TagCommands Tags) NewCommands()
    {
        var store = new InMemoryStore();
        store.Profiles.Add(new ProfileRecord
        {
            Id = "profile-1", DisplayName = "Me", ZoneId = "UTC", Locale = "en-US", DateFormat = "MDY",
            FirstWeekday = DayOfWeek.Sunday, DefaultReminderTime = new TimeOnly(9, 0), Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now,
        });
        store.Spaces.Add(new SpaceRecord { Id = "space-1", OwnerProfileId = "profile-1", Kind = SpaceKind.Personal, Name = "Personal", Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now });
        return (store, new TagCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now)));
    }

    private static async Task<AppException> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AppException error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected an AppException.");
    }

    [Test]
    [Arguments("magenta")]
    [Arguments("#ff0000")]
    public async Task CreateAsync_ColorOutsidePalette_IsRejectedBeforeWriting(string color)
    {
        var (store, tags) = NewCommands();

        var error = await ThrowsAsync(() => tags.CreateAsync(new("op", "work", null, color)));

        await Assert.That(error.Fields.Single().FieldPath).IsEqualTo("colorKey");
        await Assert.That(store.Tags.Count).IsEqualTo(0);
        await Assert.That(store.Receipts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CreateAsync_SameOperationReplayed_ReturnsOriginalAndDifferentPayloadConflicts()
    {
        var (store, tags) = NewCommands();

        var first = await tags.CreateAsync(new("op", "home/garden", null, null));
        var replay = await tags.CreateAsync(new("op", "home/garden", null, null));
        var conflict = await ThrowsAsync(() => tags.CreateAsync(new("op", "home/kitchen", null, null)));

        await Assert.That(replay.Replayed).IsTrue();
        await Assert.That(replay.Tag.Id).IsEqualTo(first.Tag.Id);
        await Assert.That(store.Tags.Count).IsEqualTo(2);
        await Assert.That(conflict.Kind).IsEqualTo(AppErrorKind.OperationConflict);
    }

    [Test]
    public async Task LinkAsync_NeitherOrBothTagAndPath_IsRejected()
    {
        var (_, tags) = NewCommands();

        var neither = await ThrowsAsync(() => tags.LinkAsync(new("a", TagTargetKind.Task, "task", null, null)));
        var both = await ThrowsAsync(() => tags.LinkAsync(new("b", TagTargetKind.Task, "task", "tag", "path")));

        await Assert.That(neither.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(both.Kind).IsEqualTo(AppErrorKind.Validation);
    }

    [Test]
    public async Task LinkAsync_UnknownTask_IsNotFoundAndCreatesNoTag()
    {
        var (store, tags) = NewCommands();

        var error = await ThrowsAsync(() => tags.LinkAsync(new("a", TagTargetKind.Task, "missing", null, "errands")));

        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.NotFound);
        await Assert.That(store.Tags.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TreeOrder_ListsParentsBeforeChildrenWithPaths()
    {
        TagRecord Tag(string id, string name, string? parent, long position) => new()
        {
            Id = id, OwnerProfileId = "p", DisplayName = name, NormalizedName = name.ToLowerInvariant(), ParentTagId = parent,
            Position = position, Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };

        var ordered = TagCommands.TreeOrder([Tag("k", "Kitchen", "h", 1024), Tag("w", "Work", null, 1024), Tag("g", "Garden", "h", 0), Tag("h", "Home", null, 0)]);

        await Assert.That(string.Join(",", ordered.Select(row => row.Path))).IsEqualTo("Home,Home/Garden,Home/Kitchen,Work");
    }
}
