using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.People;
using Chaos.Application.Spaces;
using Chaos.Application.Tasks;
using Chaos.Application.Tests.Tasks;
using Chaos.Application.Tests.Testing;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Tests.Spaces;

/// <summary>
/// Standups over fake ports (PLAN3 9.12): any live member, a viewer included, may post; the author alone edits;
/// the author or an owner deletes; a non-member and the Personal space are refused; replays add nothing.
/// The commands act as the store's primary profile, so "posting as Vera" swaps whose profile is primary.
/// </summary>
public sealed class SpaceUpdateCommandsTests
{
    private const string Me = "profile-1"; // TaskCommandsTests.Seed
    private const string Omar = "omar";
    private const string Viewer = "viewer";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 11, 23);
    private readonly SequentialIdGenerator _ids = new();

    private SpaceUpdateCommands New(InMemoryStore store) => new(store, _ids, new FixedTimeProvider(Now));

    /// <summary>A shared space where Sara (the primary profile) owns, Omar edits and Vera only views.</summary>
    private static InMemoryStore Team()
    {
        var store = new InMemoryStore();
        _ = TaskCommandsTests.Seed(store);
        store.Spaces.Add(new SpaceRecord { Id = "team", OwnerProfileId = Me, Kind = SpaceKind.Shared, Name = "Team", Version = 1, CreatedAtUtc = Now, UpdatedAtUtc = Now });
        store.People.Add(("team", new SpacePerson("person:" + Me, "Sara", 1, false, null)));
        store.People.Add(("team", new SpacePerson("person:" + Omar, "Omar", 2, false, null)));
        store.People.Add(("team", new SpacePerson("person:" + Viewer, "Vera", 3, false, null)));
        return store;
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

    private static async Task<AppException> RefusedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (AppException error) { return error; }
        throw new InvalidOperationException("The command was expected to be refused.");
    }

    [Test]
    public async Task Post_LiveMember_IncludingAViewer_RecordsTheStandupUnderTheirName()
    {
        var store = Team();
        var posted = await New(store).PostAsync(new("post-1", "team", Day, " ## Done\n- Sent the quote "));
        var listed = await New(store).ListAsync("team");

        await Assert.That(posted.Replayed).IsFalse();
        await Assert.That((posted.Item.Update.AuthorMemberId, posted.Item.Update.ForDate, posted.Item.Update.RawMarkdown)).IsEqualTo((Me, Day, "## Done\n- Sent the quote"));
        await Assert.That((posted.Item.AuthorName, posted.Item.IsMine)).IsEqualTo(("Sara", true));
        await Assert.That(listed.Single().Update.Id).IsEqualTo(posted.Item.Update.Id);

        ActAs(store, Viewer, "Vera");
        var veras = await New(store).PostAsync(new("post-2", "team", Day, "Vera's standup"));
        await Assert.That((veras.Item.AuthorName, veras.Item.IsMine)).IsEqualTo(("Vera", true));
        await Assert.That((await New(store).ListAsync("team")).Select(item => item.AuthorName)).IsEquivalentTo(["Vera", "Sara"]);
    }

    [Test]
    public async Task Edit_OnlyTheAuthor_ChangesTheTextAndMarksItEdited()
    {
        var store = Team();
        var posted = (await New(store).PostAsync(new("post-1", "team", Day, "First"))).Item.Update;

        ActAs(store, Omar, "Omar");
        var forged = await RefusedAsync(() => New(store).EditAsync(new("edit-forged", posted.Id, 1, "Forged")));
        await Assert.That(Row(store, posted.Id).RawMarkdown).IsEqualTo("First");

        ActAs(store, Me, "Sara");
        var edited = await New(store).EditAsync(new("edit-1", posted.Id, 1, "Second"));
        await Assert.That(edited.Item.Update.RawMarkdown).IsEqualTo("Second");
        await Assert.That(edited.Item.Update.EditedAtUtc).IsEqualTo(Now);
        await Assert.That(edited.Item.Update.Version).IsEqualTo(2L);
        var stale = await RefusedAsync(() => New(store).EditAsync(new("edit-2", posted.Id, 1, "Stale")));
        await Assert.That(stale.Kind).IsEqualTo(AppErrorKind.VersionConflict);
    }

    [Test]
    public async Task Delete_AuthorOrOwner_LeavesATombstoneTheListHides()
    {
        var store = Team();
        var mine = (await New(store).PostAsync(new("post-1", "team", Day, "Mine"))).Item.Update;

        ActAs(store, Viewer, "Vera");
        var veras = (await New(store).PostAsync(new("post-2", "team", Day, "Vera's"))).Item.Update;
        var notMine = await RefusedAsync(() => New(store).DeleteAsync(new("delete-1", mine.Id, 1)));
        await Assert.That(notMine.Kind).IsEqualTo(AppErrorKind.Validation);

        ActAs(store, Omar, "Omar");
        var notOwner = await RefusedAsync(() => New(store).DeleteAsync(new("delete-2", veras.Id, 1)));
        await Assert.That(notOwner.Kind).IsEqualTo(AppErrorKind.Validation);

        ActAs(store, Me, "Sara");
        var removed = await New(store).DeleteAsync(new("delete-3", veras.Id, 1));
        await Assert.That(removed.Item.Update.DeletedAtUtc).IsEqualTo(Now);
        await Assert.That((await New(store).ListAsync("team")).Select(item => item.Update.Id)).IsEquivalentTo([mine.Id]);
        await Assert.That(store.SpaceUpdates.Count).IsEqualTo(2);
        var again = await New(store).DeleteAsync(new("delete-4", veras.Id, 2));
        await Assert.That(again.Item.Update.DeletedAtUtc).IsEqualTo(Now);
        await Assert.That(again.Item.Update.Version).IsEqualTo(2L);
    }

    [Test]
    public async Task Post_NonMemberPersonalSpaceOrBlankText_IsRefused_AndAReplayAddsNothing()
    {
        var store = Team();
        var personal = store.Spaces.Single(space => space.Kind == SpaceKind.Personal).Id;

        ActAs(store, "stranger", "Stranger");
        var stranger = await RefusedAsync(() => New(store).PostAsync(new("post-1", "team", Day, "Hi")));
        await Assert.That(stranger.Kind).IsEqualTo(AppErrorKind.Validation);

        ActAs(store, Me, "Sara");
        var gone = await RefusedAsync(() => New(store).PostAsync(new("post-2", personal, Day, "Hi")));
        await Assert.That(gone.Kind).IsEqualTo(AppErrorKind.Validation);
        var blank = await RefusedAsync(() => New(store).PostAsync(new("post-3", "team", Day, "  ")));
        await Assert.That(blank.Kind).IsEqualTo(AppErrorKind.Validation);
        var tooLong = await RefusedAsync(() => New(store).PostAsync(new("post-4", "team", Day, new string('x', SpaceUpdates.MaxMarkdownLength + 1))));
        await Assert.That(tooLong.Kind).IsEqualTo(AppErrorKind.Validation);
        await RefusedAsync(() => New(store).ListAsync("nowhere"));
        await Assert.That(store.SpaceUpdates).IsEmpty();

        var first = await New(store).PostAsync(new("post-5", "team", Day, "Once"));
        var again = await New(store).PostAsync(new("post-5", "team", Day, "Once"));
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Item.Update.Id).IsEqualTo(first.Item.Update.Id);
        await Assert.That(store.SpaceUpdates.Count).IsEqualTo(1);
    }

    private static SpaceUpdateRecord Row(InMemoryStore store, string id) => store.SpaceUpdates.Single(row => row.Id == id);
}
