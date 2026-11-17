using System.Globalization;
using System.Text;

namespace Chaos.Sync;

/// <summary>
/// A hybrid logical clock value (PLAN2 §7.1): <c>(wallMillis, counter, deviceId)</c>, encoded as
/// a fixed-width string <c>0001731656400000-00000-&lt;32 hex&gt;</c> so that ordinal string order
/// is clock order. Devices with equal wall and counter are ordered by device ID, which makes the
/// comparison a total order over clocks from different devices.
/// </summary>
public readonly record struct Hlc(long WallMillis, int Counter, string DeviceId) : IComparable<Hlc>
{
    /// <summary>Accepted clock skew: a remote clock further ahead is kept but reported (PLAN2 §P1).</summary>
    public static readonly TimeSpan MaxAcceptedSkew = TimeSpan.FromHours(24);

    public const string DeviceFormat = "N";

    /// <summary>The largest wall time the 13-digit encoding holds.</summary>
    public const long MaxWallMillis = 9_999_999_999_999;

    /// <summary>The largest counter the 5-digit encoding holds; a tick past it moves the wall on by 1 ms.</summary>
    public const int MaxCounter = 99_999;

    private const int EncodedLength = 13 + 1 + 5 + 1 + 32;

    /// <summary>Throws when the device ID is empty, the wall time does not fit 13 digits, or the counter does not fit 5 digits.</summary>
    public static Hlc Create(long wallMillis, int counter, Guid deviceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wallMillis);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wallMillis, MaxWallMillis);
        ArgumentOutOfRangeException.ThrowIfNegative(counter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(counter, MaxCounter);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A clock needs a device ID.", nameof(deviceId));
        }

        return new(wallMillis, counter, ToDevice(deviceId));
    }

    public static string ToDevice(Guid deviceId) => deviceId.ToString(DeviceFormat);

    public static bool TryParse(string? text, out Hlc clock)
    {
        clock = default;
        if (text is null || text.Length != EncodedLength || text[13] != '-' || text[19] != '-')
        {
            return false;
        }

        if (!long.TryParse(text.AsSpan(0, 13), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wall) ||
            !int.TryParse(text.AsSpan(14, 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var counter))
        {
            return false;
        }

        var device = text.AsSpan(20);
        foreach (var character in device)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        clock = new Hlc(wall, counter, device.ToString());
        return true;
    }

    public static Hlc Parse(string text) =>
        TryParse(text, out var clock) ? clock : throw new FormatException($"'{text}' is not a hybrid logical clock.");

    public string Encode() => string.Create(CultureInfo.InvariantCulture, $"{WallMillis:D13}-{Counter:D5}-{DeviceId}");

    public DateTimeOffset WallUtc => DateTimeOffset.FromUnixTimeMilliseconds(WallMillis);

    public int CompareTo(Hlc other)
    {
        var walls = WallMillis.CompareTo(other.WallMillis);
        if (walls != 0)
        {
            return walls;
        }

        var counters = Counter.CompareTo(other.Counter);
        if (counters != 0)
        {
            return counters;
        }

        return string.CompareOrdinal(DeviceId, other.DeviceId);
    }

    public static bool operator <(Hlc left, Hlc right) => left.CompareTo(right) < 0;
    public static bool operator >(Hlc left, Hlc right) => left.CompareTo(right) > 0;
    public static bool operator <=(Hlc left, Hlc right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Hlc left, Hlc right) => left.CompareTo(right) >= 0;

    public override string ToString() => Encode();
}

/// <summary>The merge of a remote clock into the local one: the new clock and whether the remote
/// wall time was further ahead than <see cref="Hlc.MaxAcceptedSkew"/> (kept, but reported).</summary>
public readonly record struct ReceiveResult(Hlc Clock, bool SkewBeyondCap);

/// <summary>
/// The standard hybrid logical clock algorithm (PLAN2 §7.1) over one device's last value, with an
/// injectable <see cref="TimeProvider"/>. <see cref="TickLocal"/> orders local events; 
/// <see cref="Receive"/> merges a remote clock and then ticks, so applied changes are never
/// shadowed by a local change that happened before them.
/// </summary>
public sealed class HybridClock(Guid deviceId, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Guid DeviceId { get; } = deviceId;

    /// <summary>The last issued clock; starts at the epoch, so the first tick is the current time.</summary>
    public Hlc Last { get; private set; } = new(0, 0, Hlc.ToDevice(deviceId));

    public Hlc TickLocal() => Tick(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    public ReceiveResult Receive(Hlc remote)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var skewBeyondCap = remote.WallMillis - now > Hlc.MaxAcceptedSkew.TotalMilliseconds;
        Last = Merge(remote, now);
        return new ReceiveResult(Tick(now), skewBeyondCap);
    }

    /// <summary>Restores a persisted clock (startup reads <c>SyncState.LastHlc</c>); ticks from there.</summary>
    public void Restore(Hlc last) => Last = last;

    private Hlc Tick(long nowMillis)
    {
        var wall = Math.Max(nowMillis, Last.WallMillis);
        var counter = wall == Last.WallMillis ? Last.Counter + 1 : 0;
        if (counter > Hlc.MaxCounter)
        {
            // The counter no longer fits its five digits: borrow the next millisecond instead,
            // which still sorts after every clock issued before.
            wall++;
            counter = 0;
        }

        return Last = new Hlc(wall, counter, Last.DeviceId);
    }

    /// <summary>Merges the remote clock into the last one as the pairwise maximum (same wall
    /// means the larger counter). The caller's <see cref="Tick"/> is what makes the received
    /// event itself newer than everything before it, so the merge itself never increments.</summary>
    private Hlc Merge(Hlc remote, long nowMillis)
    {
        var wall = Math.Max(nowMillis, Math.Max(Last.WallMillis, remote.WallMillis));
        var counter = wall == Last.WallMillis && wall == remote.WallMillis
            ? Math.Max(Last.Counter, remote.Counter)
            : wall == Last.WallMillis ? Last.Counter
            : wall == remote.WallMillis ? remote.Counter
            : 0;
        return new Hlc(wall, counter, Last.DeviceId);
    }
}
