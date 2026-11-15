using Chaos.Sync;

namespace Chaos.Sync.Tests;

/// <summary>PLAN2 P1 item 3: UUIDv5 against the RFC 9562 test vector, plus the invariants the
/// natural-key scheme relies on (determinism, version/variant bits, distinct names).</summary>
public sealed class NameBasedIdTests
{
    [Test]
    public async Task Create_Rfc9562Vector_MatchesThePublishedUuidV5()
    {
        // RFC 9562 Appendix B: UUIDv5 of "python.org" in the DNS namespace.
        var uuid = NameBasedId.Create(NameBasedId.DnsNamespace, "python.org");

        await Assert.That(uuid.ToString().ToLowerInvariant()).IsEqualTo("886313e1-3b8a-5372-9b90-0c9aee199e5d");
    }

    [Test]
    public async Task Create_SameNamespaceAndName_AlwaysProducesTheSameId()
    {
        var first = NameBasedId.Create(NameBasedId.ChaosNamespace, "dayplan:profile:2026-11-15");
        var second = NameBasedId.Create(NameBasedId.ChaosNamespace, "dayplan:profile:2026-11-15");

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Create_MarksVersion5AndRfcVariantBits()
    {
        var uuid = NameBasedId.Create(NameBasedId.ChaosNamespace, "anything");
        var text = uuid.ToString("N");

        await Assert.That(text[12]).IsEqualTo('5'); // version nibble
        await Assert.That("89ab".Contains(text[16])).IsTrue(); // variant 10xx
    }

    [Test]
    public async Task Create_DifferentNamesOrNamespaces_ProduceDifferentIds()
    {
        var a = NameBasedId.Create(NameBasedId.ChaosNamespace, "dailypage:space:2026-11-15");
        var b = NameBasedId.Create(NameBasedId.ChaosNamespace, "dailypage:space:2026-11-16");
        var dns = NameBasedId.Create(NameBasedId.DnsNamespace, "dailypage:space:2026-11-15");

        await Assert.That(b).IsNotEqualTo(a);
        await Assert.That(dns).IsNotEqualTo(a);
    }

    [Test]
    public async Task Names_DifferForEveryNaturalKeyKind()
    {
        var profile = Guid.NewGuid().ToString();
        var date = new DateOnly(2026, 11, 15);
        var plan = NameBasedId.Create(NameBasedId.Names.DayPlan(profile, date));
        var page = NameBasedId.Create(NameBasedId.Names.DailyPage("space", date));
        var week = NameBasedId.Create(NameBasedId.Names.WeekPlan(profile, date));

        var distinct = new HashSet<Guid> { plan, page, week };
        await Assert.That(distinct.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Names_DeriveTheSameOccurrenceAndSuccessorTaskOnTwoDevices()
    {
        // The P1 exit criterion for deterministic IDs, in miniature: the same real thing gets
        // the same ID on two fresh data roots that never talked to each other.
        var here = NameBasedId.Create(NameBasedId.Names.OccurrenceTask("series", "2026-11-16"));
        var there = NameBasedId.Create(NameBasedId.Names.OccurrenceTask("series", "2026-11-16"));

        await Assert.That(there).IsEqualTo(here);
    }
}
