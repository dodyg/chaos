using Chaos.Domain.Validation;

namespace Chaos.Domain.Recurrence;

/// <summary>
/// The hosts a repeating task rotates through (PLAN3 9.10). <paramref name="Start"/> is the index of the first host.
/// Stored as <c>RotationJson</c> on the series; only shared spaces may rotate.
/// </summary>
public sealed record Rotation(IReadOnlyList<string> Members, int Start)
{
    public const int MaxMembers = 50;

    public void Validate()
    {
        if (Members is null || Members.Count is 0 or > MaxMembers)
            throw new DomainValidationException($"Choose between 1 and {MaxMembers} members to rotate through.");
        if (Members.Any(string.IsNullOrWhiteSpace) || Members.Distinct(StringComparer.Ordinal).Count() != Members.Count)
            throw new DomainValidationException("Each member can appear in the rotation only once.");
        if (Start < 0 || Start >= Members.Count)
            throw new DomainValidationException("Choose a starting member from the rotation.");
    }

    /// <summary>The index of the occurrence after one with <paramref name="previousIndex"/>; the first uses <see cref="Start"/>.</summary>
    public int NextIndex(int? previousIndex) => previousIndex is { } previous ? previous + 1 : Start;

    /// <summary>
    /// The host for <paramref name="index"/>: <c>Members[index % count]</c>, moving on to the next live member when that one
    /// has left. Null when nobody in the rotation is live. A pure function of its inputs, so every device agrees.
    /// </summary>
    public string? HostFor(int index, Func<string, bool> isLive)
    {
        if (Members.Count == 0 || index < 0) return null;
        for (var step = 0; step < Members.Count; step++)
        {
            var member = Members[(int)(((long)index + step) % Members.Count)];
            if (isLive(member)) return member;
        }
        return null;
    }

    /// <summary>The next <paramref name="count"/> hosts after <paramref name="previousIndex"/>, for "Next: Omar, then Sara".</summary>
    public IReadOnlyList<string> Upcoming(int? previousIndex, int count, Func<string, bool> isLive)
    {
        var hosts = new List<string>(count);
        var index = NextIndex(previousIndex);
        for (var i = 0; i < count; i++, index++)
            if (HostFor(index, isLive) is { } host) hosts.Add(host);
        return hosts;
    }
}
