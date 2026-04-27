// Map<K,V> + the static class Map namespace companion.

namespace Overt.Runtime;

/// <summary>
/// Immutable key-value map. Keys must be non-null; equality is the host's
/// default for the key type. Iteration order is insertion-defined per
/// .NET's ImmutableDictionary semantics; programs that need a specific
/// order sort the keys explicitly.
/// </summary>
public sealed record Map<K, V>(System.Collections.Immutable.ImmutableDictionary<K, V> Items)
    where K : notnull;

/// <summary>
/// One key-value pair as a value type. Returned from <see cref="Map.entries{K, V}"/>;
/// constructed where a program needs to thread a single (key, value) through
/// code without a tuple type. Field names are the Overt-canonical lowercase.
/// </summary>
public sealed record MapEntry<K, V>(K key, V value)
    where K : notnull;

/// <summary>
/// Non-generic namespace companion to <see cref="Map{K, V}"/>. Same shape
/// trick as List: record and static class share the name via different
/// arities.
/// </summary>
public static class Map
{
    public static Map<K, V> empty<K, V>() where K : notnull
        => new(System.Collections.Immutable.ImmutableDictionary<K, V>.Empty);

    public static Option<V> get<K, V>(Map<K, V> map, K key) where K : notnull
        => map.Items.TryGetValue(key, out var v) ? new OptionSome<V>(v) : new OptionNone<V>();

    public static bool contains_key<K, V>(Map<K, V> map, K key) where K : notnull
        => map.Items.ContainsKey(key);

    public static Map<K, V> insert<K, V>(Map<K, V> map, K key, V value) where K : notnull
        => new(map.Items.SetItem(key, value));

    public static Map<K, V> remove<K, V>(Map<K, V> map, K key) where K : notnull
        => new(map.Items.Remove(key));

    public static int size<K, V>(Map<K, V> map) where K : notnull
        => map.Items.Count;

    public static List<K> keys<K, V>(Map<K, V> map) where K : notnull
        => new(System.Collections.Immutable.ImmutableArray.CreateRange(map.Items.Keys));

    public static List<V> values<K, V>(Map<K, V> map) where K : notnull
        => new(System.Collections.Immutable.ImmutableArray.CreateRange(map.Items.Values));

    public static List<MapEntry<K, V>> entries<K, V>(Map<K, V> map) where K : notnull
    {
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<MapEntry<K, V>>(map.Items.Count);
        foreach (var kv in map.Items)
        {
            builder.Add(new MapEntry<K, V>(kv.Key, kv.Value));
        }
        return new List<MapEntry<K, V>>(builder.MoveToImmutable());
    }

    /// <summary>Right wins on key collision: <c>merge(a, b)[k] = b[k]</c>
    /// when both contain k. Matches the convention of last-writer-wins
    /// merging that most programs expect.</summary>
    public static Map<K, V> merge<K, V>(Map<K, V> left, Map<K, V> right) where K : notnull
        => new(left.Items.SetItems(right.Items));

    public static Map<K, W> map<K, V, W>(Map<K, V> map, Func<V, W> f) where K : notnull
    {
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<K, W>();
        foreach (var kv in map.Items) builder.Add(kv.Key, f(kv.Value));
        return new Map<K, W>(builder.ToImmutable());
    }

    public static Map<K, V> filter<K, V>(Map<K, V> map, Func<K, V, bool> predicate) where K : notnull
    {
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<K, V>();
        foreach (var kv in map.Items)
        {
            if (predicate(kv.Key, kv.Value)) builder.Add(kv.Key, kv.Value);
        }
        return new Map<K, V>(builder.ToImmutable());
    }
}
