using Chaos.Sync;

namespace Chaos.Sync.Tests;

/// <summary>PLAN2 P1 item 2: HLC ordering, same-millisecond counters, a clock that goes
/// backwards, a clock from the future (kept, skew beyond 24 h reported) and round-trip format.</summary>
public sealed class HlcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);
    private static readonly long NowMillis = Now.ToUnixTimeMilliseconds();

    private sealed class FixedAdvancingClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static Hlc H(long wall, int counter, string device = "00000000000000000000000000000001") => new(wall, counter, device);

    [Test]
    public async Task Encode_IsFixedWidthAndSortsLikeTheClock()
    {
        var early = H(1, 0);
        var mid = H(1, 1);
        var late = H(10, 0);
        var otherDevice = H(1, 0, "00000000000000000000000000000002");

        await Assert.That(early.Encode().Length).IsEqualTo(52);
        await Assert.That(string.CompareOrdinal(early.Encode(), mid.Encode())).IsLessThan(0);
        await Assert.That(string.CompareOrdinal(mid.Encode(), late.Encode())).IsLessThan(0);
        await Assert.That(string.CompareOrdinal(early.Encode(), otherDevice.Encode())).IsLessThan(0);
    }

    [Test]
    public async Task Ordering_FollowsWallThenCounterThenDevice()
    {
        var a = H(100, 5, "aaaa0000000000000000000000000000");
        var b = H(100, 5, "bbbb0000000000000000000000000000");
        var c = H(100, 6, "aaaa0000000000000000000000000000");
        var d = H(101, 0, "ffff0000000000000000000000000000");

        await Assert.That(a.CompareTo(b)).IsEqualTo(-1);
        await Assert.That(b.CompareTo(a)).IsEqualTo(1);
        await Assert.That(a.CompareTo(a)).IsEqualTo(0);
        await Assert.That(a < c).IsTrue();
        await Assert.That(c < d).IsTrue();
        await Assert.That(d >= c).IsTrue();
    }

    [Test]
    public async Task Parse_RoundTripsEveryField_AndRejectsGarbage()
    {
        var clock = H(NowMillis, 42, "0123456789abcdef0123456789abcdef");
        var encoded = clock.Encode();

        var parsed = Hlc.Parse(encoded);
        await Assert.That(parsed.Encode()).IsEqualTo(encoded);
        await Assert.That(parsed.WallMillis).IsEqualTo(NowMillis);
        await Assert.That(parsed.Counter).IsEqualTo(42);
        await Assert.That(parsed.DeviceId).IsEqualTo("0123456789abcdef0123456789abcdef");

        await Assert.That(Hlc.TryParse("not-a-clock", out _)).IsFalse();
        await Assert.That(Hlc.TryParse(encoded[..^1], out _)).IsFalse();
        await Assert.That(Hlc.TryParse(null, out _)).IsFalse();
        await Assert.That(Hlc.TryParse("0001731656400000-123456-0123456789abcdef0123456789abcdef", out _)).IsFalse();
    }

    [Test]
    public async Task TickLocal_SameMillisecond_IncrementsTheCounter()
    {
        var clock = new HybridClock(Guid.NewGuid(), new FixedTimeProvider(Now));
        var first = clock.TickLocal();
        var second = clock.TickLocal();

        await Assert.That(first.WallMillis).IsEqualTo(NowMillis);
        await Assert.That(first.Counter).IsEqualTo(0);
        await Assert.That(second.WallMillis).IsEqualTo(NowMillis);
        await Assert.That(second.Counter).IsEqualTo(1);
        await Assert.That(second).IsGreaterThan(first);
    }

    [Test]
    public async Task TickLocal_NewMillisecond_ResetsTheCounter()
    {
        var provider = new FixedAdvancingClock(Now);
        var clock = new HybridClock(Guid.NewGuid(), provider);
        clock.TickLocal();
        clock.TickLocal();
        provider.Advance(TimeSpan.FromMilliseconds(1));

        var next = clock.TickLocal();
        await Assert.That(next.WallMillis).IsEqualTo(NowMillis + 1);
        await Assert.That(next.Counter).IsEqualTo(0);
    }

    [Test]
    public async Task TickLocal_ClockGoingBackwards_NeverIssuesAnOlderTimestamp()
    {
        var provider = new FixedAdvancingClock(Now);
        var clock = new HybridClock(Guid.NewGuid(), provider);
        var early = clock.TickLocal();

        provider.Advance(TimeSpan.FromHours(1));
        var late = clock.TickLocal();
        provider.Advance(TimeSpan.FromHours(-2)); // the wall clock was corrected backwards

        var afterBackwards = clock.TickLocal();
        await Assert.That(afterBackwards.WallMillis).IsEqualTo(late.WallMillis);
        await Assert.That(afterBackwards.Counter).IsEqualTo(late.Counter + 1);
        await Assert.That(afterBackwards).IsGreaterThan(late);
        await Assert.That(late).IsGreaterThan(early);
    }

    [Test]
    public async Task Receive_FutureClock_IsKeptAndTicked_ButSkewBeyond24HoursIsReported()
    {
        var provider = new FixedAdvancingClock(Now);
        var clock = new HybridClock(Guid.NewGuid(), provider);
        var local = clock.TickLocal();

        var remote = H(NowMillis + (long)TimeSpan.FromHours(2).TotalMilliseconds, 0, "99999999999999999999999999999999");
        var near = clock.Receive(remote);
        await Assert.That(near.Clock.WallMillis).IsEqualTo(remote.WallMillis);
        await Assert.That(near.Clock.Counter).IsEqualTo(1);
        await Assert.That(near.Clock.DeviceId).IsEqualTo(clock.Last.DeviceId);
        await Assert.That(near.SkewBeyondCap).IsFalse();
        await Assert.That(near.Clock).IsGreaterThan(local);

        var far = H(NowMillis + (long)TimeSpan.FromHours(25).TotalMilliseconds, 0, "99999999999999999999999999999999");
        var reported = clock.Receive(far);
        await Assert.That(reported.Clock.WallMillis).IsEqualTo(far.WallMillis); // kept, never moved back
        await Assert.That(reported.SkewBeyondCap).IsTrue();
    }

    [Test]
    public async Task Receive_OlderRemoteClock_DoesNotMoveTheLocalClockBack()
    {
        var clock = new HybridClock(Guid.NewGuid(), new FixedTimeProvider(Now));
        var first = clock.TickLocal();
        var second = clock.TickLocal();

        var result = clock.Receive(H(NowMillis - 5_000, 3));
        await Assert.That(result.Clock.WallMillis).IsEqualTo(NowMillis);
        await Assert.That(result.Clock.Counter).IsEqualTo(second.Counter + 1);
        await Assert.That(result.Clock).IsGreaterThan(second);
        await Assert.That(first).IsLessThan(second);
    }

    [Test]
    public async Task Receive_SameInstantAsLocal_MaxCounterThenIncrement()
    {
        var clock = new HybridClock(Guid.NewGuid(), new FixedTimeProvider(Now));
        clock.TickLocal();
        clock.TickLocal();

        // A remote event at the same wall millisecond: merged counters must never collide.
        var result = clock.Receive(H(NowMillis, 7));
        await Assert.That(result.Clock.Counter).IsEqualTo(8);
        var next = clock.Receive(H(NowMillis, 2));
        await Assert.That(next.Clock.Counter).IsEqualTo(9);
    }

    [Test]
    public async Task TickLocal_CounterAtItsMaximum_MovesTheWallOnInsteadOfOverflowing()
    {
        var clock = new HybridClock(Guid.NewGuid(), new FixedTimeProvider(Now));
        clock.Restore(new Hlc(NowMillis, Hlc.MaxCounter, clock.Last.DeviceId));

        var next = clock.TickLocal();
        await Assert.That(next.WallMillis).IsEqualTo(NowMillis + 1);
        await Assert.That(next.Counter).IsEqualTo(0);
        await Assert.That(next.Encode().Length).IsEqualTo(52);
        await Assert.That(string.CompareOrdinal(next.Encode(), new Hlc(NowMillis, Hlc.MaxCounter, next.DeviceId).Encode())).IsGreaterThan(0);
    }

    [Test]
    public async Task Create_RejectsValuesTheEncodingCannotHold()
    {
        var device = Guid.NewGuid();
        await Assert.That(Hlc.Create(NowMillis, 3, device).Encode()).IsEqualTo(new Hlc(NowMillis, 3, device.ToString("N")).Encode());
        await Assert.That(() => Hlc.Create(NowMillis, Hlc.MaxCounter + 1, device)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Hlc.Create(NowMillis, -1, device)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Hlc.Create(-1, 0, device)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Hlc.Create(Hlc.MaxWallMillis + 1, 0, device)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Hlc.Create(NowMillis, 0, Guid.Empty)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Restore_ContinuesFromThePersistedClock()
    {
        var provider = new FixedAdvancingClock(Now);
        var first = new HybridClock(Guid.NewGuid(), provider);
        var high = first.TickLocal();
        provider.Advance(TimeSpan.FromHours(1));

        // The persisted clock is an hour behind real time: the next tick uses real time and
        // resets the counter, but stays newer than the persisted value.
        var restarted = new HybridClock(Guid.ParseExact(high.DeviceId, "N"), provider);
        restarted.Restore(high);
        var next = restarted.TickLocal();
        await Assert.That(next.WallMillis).IsEqualTo(NowMillis + 3_600_000);
        await Assert.That(next.Counter).IsEqualTo(0);
        await Assert.That(next).IsGreaterThan(high);

        // The persisted clock is further ahead than real time (a backwards wall correction):
        // ticks continue from it instead of moving back.
        var ahead = new Hlc(NowMillis + 7_200_000, 3, high.DeviceId);
        var restartedAhead = new HybridClock(Guid.ParseExact(high.DeviceId, "N"), provider);
        restartedAhead.Restore(ahead);
        var afterAhead = restartedAhead.TickLocal();
        await Assert.That(afterAhead.WallMillis).IsEqualTo(ahead.WallMillis);
        await Assert.That(afterAhead.Counter).IsEqualTo(ahead.Counter + 1);
        await Assert.That(afterAhead).IsGreaterThan(ahead);
    }
}
