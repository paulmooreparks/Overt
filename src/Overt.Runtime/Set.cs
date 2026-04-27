// Set<T> + the static class Set namespace companion.

namespace Overt.Runtime;

/// <summary>
/// Immutable set of values. Element type's host-default equality is used.
/// </summary>
public sealed record Set<T>(System.Collections.Immutable.ImmutableHashSet<T> Items);

/// <summary>
/// Non-generic namespace companion to <see cref="Set{T}"/>.
/// </summary>
public static class Set
{
    public static Set<T> empty<T>()
        => new(System.Collections.Immutable.ImmutableHashSet<T>.Empty);

    public static bool contains<T>(Set<T> set, T value)
        => set.Items.Contains(value);

    public static Set<T> insert<T>(Set<T> set, T value)
        => new(set.Items.Add(value));

    public static Set<T> remove<T>(Set<T> set, T value)
        => new(set.Items.Remove(value));

    public static int size<T>(Set<T> set)
        => set.Items.Count;

    public static Set<T> union<T>(Set<T> left, Set<T> right)
        => new(left.Items.Union(right.Items));

    public static Set<T> intersect<T>(Set<T> left, Set<T> right)
        => new(left.Items.Intersect(right.Items));

    public static Set<T> difference<T>(Set<T> left, Set<T> right)
        => new(left.Items.Except(right.Items));

    /// <summary>Iteration escape hatch. Returns the set's elements as a
    /// <see cref="List{T}"/> in iteration order (filesystem-/hash-defined,
    /// not stable across hosts — same caveat as <c>Map.keys</c> /
    /// <c>Map.values</c>). Without this, every other Set op produces a
    /// Set with no way to walk it.</summary>
    public static List<T> values<T>(Set<T> set)
        => new(System.Collections.Immutable.ImmutableArray.CreateRange(set.Items));
}
