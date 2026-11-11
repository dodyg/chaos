namespace Chaos.Domain.GitHub;

public readonly record struct FieldMerge<T>(T Value, bool Push, bool Conflict);

public static class SyncMerge
{
    public static FieldMerge<T> Field<T>(T @base, T local, T remote, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        if (comparer.Equals(local, remote)) return new(remote, false, false);
        if (comparer.Equals(local, @base)) return new(remote, false, false);
        if (comparer.Equals(remote, @base)) return new(local, true, false);
        return new(remote, false, true);
    }

    public static HashSet<T> Set<T>(IEnumerable<T> @base, IEnumerable<T> local, IEnumerable<T> remote, IEqualityComparer<T>? comparer = null)
    {
        var original = new HashSet<T>(@base, comparer);
        var current = new HashSet<T>(local, comparer);
        var result = new HashSet<T>(remote, comparer);
        var removed = new HashSet<T>(original, comparer);
        removed.ExceptWith(current);
        current.ExceptWith(original);
        result.UnionWith(current);
        result.ExceptWith(removed);
        return result;
    }
}
