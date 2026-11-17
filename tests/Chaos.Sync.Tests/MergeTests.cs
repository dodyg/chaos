using Chaos.Sync;

namespace Chaos.Sync.Tests;

/// <summary>PLAN2 P1 item 4: field-group merge — newer wins, older loses, equal clocks are
/// idempotent, unknown groups are kept, and a purge carries no groups at all.</summary>
public sealed class MergeTests
{
    private static readonly string A = "00000000000000000000000000000001";
    private static readonly string B = "00000000000000000000000000000002";

    private static Hlc C(long wall, int counter, string device = "00000000000000000000000000000001") => new(wall, counter, device);

    private static SyncGroupState Group(long wall, int counter, string device, params (string Column, object? Value)[] values) =>
        new(C(wall, counter, device), new SyncGroupValues(values.ToDictionary(v => v.Column, v => v.Value)));

    private static IReadOnlyDictionary<string, SyncGroupState> Map(params (string Name, SyncGroupState State)[] groups) =>
        groups.ToDictionary(g => g.Name, g => g.State, StringComparer.Ordinal);

    [Test]
    public async Task Apply_NewerIncomingGroup_WinsAndReplacesTheStoredValue()
    {
        var stored = Map(("title", Group(1000, 0, A, ("Title", "old"))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(("title", Group(2000, 0, B, ("Title", "new")))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Purge).IsFalse();
        await Assert.That(result.Winners.ContainsKey("title")).IsTrue();
        await Assert.That(result.Merged["title"].Values.Values["Title"]).IsEqualTo("new");
        await Assert.That(result.Replaced["title"].Values["Title"]).IsEqualTo("old");
        await Assert.That(result.Changed).IsTrue();
    }

    [Test]
    public async Task Apply_OlderIncomingGroup_LosesAndKeepsTheStoredValue()
    {
        var stored = Map(("title", Group(2000, 0, A, ("Title", "mine"))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(("title", Group(1000, 0, B, ("Title", "theirs")))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Winners.Count).IsEqualTo(0);
        await Assert.That(result.Merged["title"].Values.Values["Title"]).IsEqualTo("mine");
        await Assert.That(result.Replaced.Count).IsEqualTo(0);
        await Assert.That(result.Changed).IsFalse();
    }

    [Test]
    public async Task Apply_EqualClocksSameDevice_IsIdempotent()
    {
        // The device's own change coming back from the server: no win, no replaced value.
        var stored = Map(("title", Group(1000, 0, A, ("Title", "same"))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(("title", Group(1000, 0, A, ("Title", "same")))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Winners.Count).IsEqualTo(0);
        await Assert.That(result.Changed).IsFalse();
    }

    [Test]
    public async Task Apply_EqualClocksDifferentDevice_HigherDeviceIdWins()
    {
        // Total order over clocks: same wall and counter resolves by device, deterministically
        // on every machine.
        var stored = Map(("title", Group(1000, 5, A, ("Title", "mine"))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(("title", Group(1000, 5, B, ("Title", "theirs")))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Winners.ContainsKey("title")).IsTrue();
        await Assert.That(result.Merged["title"].Values.Values["Title"]).IsEqualTo("theirs");
    }

    [Test]
    public async Task Apply_GroupsMergeIndependently()
    {
        // A title change on one device and a status change on another both survive.
        var stored = Map(
            ("title", Group(1000, 0, A, ("Title", "local title"))),
            ("status", Group(500, 0, A, ("Status", 0L))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(
            ("title", Group(900, 0, B, ("Title", "remote title"))),
            ("status", Group(1500, 0, B, ("Status", 4L)))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Merged["title"].Values.Values["Title"]).IsEqualTo("local title");
        await Assert.That(result.Merged["status"].Values.Values["Status"]).IsEqualTo(4L);
        await Assert.That(result.Winners.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_UnknownGroups_AreKeptOnBothSides()
    {
        var stored = Map(("title", Group(1000, 0, A, ("Title", "t"))), ("future-group", Group(500, 0, A, ("X", 1L))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(
            ("title", Group(2000, 0, B, ("Title", "new"))),
            ("newer-group", Group(2000, 0, B, ("Y", 2L)))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Merged.ContainsKey("future-group")).IsTrue();
        await Assert.That(result.Merged.ContainsKey("newer-group")).IsTrue();
        await Assert.That(result.Merged["newer-group"].Values.Values["Y"]).IsEqualTo(2L);
    }

    [Test]
    public async Task Apply_NoStoredState_EveryIncomingGroupWins()
    {
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(
            ("create", Group(1000, 0, B, ("SpaceId", "s"))),
            ("title", Group(1000, 0, B, ("Title", "new task")))));

        var result = Merge.Apply(null, incoming);

        await Assert.That(result.Merged.Count).IsEqualTo(2);
        await Assert.That(result.Winners.Count).IsEqualTo(2);
        await Assert.That(result.Replaced.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_PurgeOp_TombstonesWithoutGroups()
    {
        var stored = Map(("title", Group(1000, 0, A, ("Title", "t"))));
        var incoming = SyncChange.Purge("c1", "task", "t1");

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Purge).IsTrue();
        await Assert.That(result.Merged.Count).IsEqualTo(0);
        await Assert.That(result.Changed).IsTrue();
    }

    [Test]
    public async Task Apply_SameValueAgain_RecordsNoReplacement()
    {
        // A win that changes nothing (for example a column rewritten with the same content)
        // must not put a "replaced value" into history.
        var stored = Map(("title", Group(1000, 0, A, ("Title", "same"))));
        var incoming = SyncChange.Upsert("c1", "task", "t1", Map(("title", Group(2000, 0, B, ("Title", "same")))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Winners.ContainsKey("title")).IsTrue();
        await Assert.That(result.Replaced.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_BoolAndIntValues_CompareEqualWithTheirStoredLongForms()
    {
        // SQLite reads booleans back as long, and C# writes ints: the same logical value.
        var stored = Map(("flags", Group(1000, 0, A, ("QuietHoursEnabled", 1L))));
        var incoming = SyncChange.Upsert("c1", "profile", "p1", Map(("flags", Group(2000, 0, B, ("QuietHoursEnabled", true)))));

        var result = Merge.Apply(stored, incoming);

        await Assert.That(result.Replaced.Count).IsEqualTo(0);
    }
}
