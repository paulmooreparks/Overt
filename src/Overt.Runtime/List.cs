// List<T> + the static class List namespace companion (List.empty, head,
// tail, take, drop, find, partition, etc.). Also Pair<T,U> and the
// JsonConverter wiring that lets List values round-trip through
// System.Text.Json.

namespace Overt.Runtime;

/// <summary>Minimal ordered collection placeholder. Real implementation lands with the
/// stdlib milestone; this shape is just enough to let transpiled code type-check.
/// The JsonConverter attribute wires System.Text.Json (de)serialization: a JSON
/// array maps to / from the wrapped ImmutableArray, so Overt records with List
/// fields round-trip through JsonSerializer without any per-consumer setup.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ListJsonConverterFactory))]
public sealed record List<T>(System.Collections.Immutable.ImmutableArray<T> Items);

/// <summary>
/// Binds the generic List&lt;T&gt; to a per-T JsonConverter. The per-T converter
/// defers element (de)serialization to the runtime's configured converters, so
/// nested Overt types and user-defined converters both flow through.
/// </summary>
internal sealed class ListJsonConverterFactory : System.Text.Json.Serialization.JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType
           && typeToConvert.GetGenericTypeDefinition() == typeof(List<>);

    public override System.Text.Json.Serialization.JsonConverter CreateConverter(
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ListJsonConverter<>).MakeGenericType(elementType);
        return (System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class ListJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<List<T>>
{
    public override List<T>? Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null)
        {
            return null;
        }
        var arr = System.Text.Json.JsonSerializer.Deserialize<T[]>(ref reader, options);
        return new List<T>(arr is null
            ? System.Collections.Immutable.ImmutableArray<T>.Empty
            : System.Collections.Immutable.ImmutableArray.Create(arr));
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        List<T> value,
        System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value.Items)
        {
            System.Text.Json.JsonSerializer.Serialize(writer, item, options);
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Non-generic namespace companion to <see cref="List{T}"/>. Overt source calls
/// module-qualified stdlib functions as <c>List.empty()</c>, <c>List.singleton(x)</c>,
/// etc; those resolve to members of this class. C# permits a non-generic class and a
/// generic class/record to share a name.
/// </summary>
public static class List
{
    public static List<T> empty<T>() => new(System.Collections.Immutable.ImmutableArray<T>.Empty);
    public static List<T> singleton<T>(T value)
        => new(System.Collections.Immutable.ImmutableArray.Create(value));
    public static List<T> concat_three<T>(List<T> first, List<T> middle, List<T> last)
        => new(first.Items.AddRange(middle.Items).AddRange(last.Items));

    // Indexed access. Out-of-range index is a programmer error (callers
    // should size()-check first), so it surfaces as
    // ArgumentOutOfRangeException rather than as a domain error.
    public static T at<T>(List<T> list, int index)
    {
        if ((uint)index >= (uint)list.Items.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"List.at: index out of range [0, {list.Items.Length})");
        }
        return list.Items[index];
    }

    // Two-list concat. Use concat_three for the three-arity form when an
    // unrolled chain is convenient.
    public static List<T> concat<T>(List<T> left, List<T> right)
        => new(left.Items.AddRange(right.Items));

    // Head / tail. Head returns Option to avoid the empty-list panic;
    // tail returns the empty list when the input is empty (matches
    // Haskell-flavored "tail of empty is empty" rather than panicking).
    public static Option<T> head<T>(List<T> list)
        => list.Items.IsEmpty ? new OptionNone<T>() : new OptionSome<T>(list.Items[0]);
    public static List<T> tail<T>(List<T> list)
        => list.Items.IsEmpty
            ? list
            : new(list.Items.RemoveAt(0));

    // Take / drop. Out-of-range counts are clamped — `take` of more than
    // the list has returns the whole list; negative counts return the
    // empty list. Total programmer-input recovery. Symmetric on `drop`.
    public static List<T> take<T>(List<T> list, int n)
    {
        if (n <= 0) return new(System.Collections.Immutable.ImmutableArray<T>.Empty);
        if (n >= list.Items.Length) return list;
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(n);
        for (var i = 0; i < n; i++) builder.Add(list.Items[i]);
        return new(builder.MoveToImmutable());
    }
    public static List<T> drop<T>(List<T> list, int n)
    {
        if (n <= 0) return list;
        if (n >= list.Items.Length) return new(System.Collections.Immutable.ImmutableArray<T>.Empty);
        var remaining = list.Items.Length - n;
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(remaining);
        for (var i = n; i < list.Items.Length; i++) builder.Add(list.Items[i]);
        return new(builder.MoveToImmutable());
    }

    public static List<T> reverse<T>(List<T> list)
    {
        if (list.Items.Length <= 1) return list;
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(list.Items.Length);
        for (var i = list.Items.Length - 1; i >= 0; i--) builder.Add(list.Items[i]);
        return new(builder.MoveToImmutable());
    }

    // First element matching pred, or None.
    public static Option<T> find<T>(List<T> list, Func<T, bool> predicate)
    {
        foreach (var v in list.Items)
        {
            if (predicate(v)) return new OptionSome<T>(v);
        }
        return new OptionNone<T>();
    }

    // First index whose element matches pred, or None.
    public static Option<int> find_index<T>(List<T> list, Func<T, bool> predicate)
    {
        for (var i = 0; i < list.Items.Length; i++)
        {
            if (predicate(list.Items[i])) return new OptionSome<int>(i);
        }
        return new OptionNone<int>();
    }

    // Membership via host equality. Uses default EqualityComparer<T>; for
    // user records that means structural equality (record ==), for
    // primitives the obvious thing.
    public static bool contains<T>(List<T> list, T value)
    {
        var cmp = System.Collections.Generic.EqualityComparer<T>.Default;
        foreach (var v in list.Items)
        {
            if (cmp.Equals(v, value)) return true;
        }
        return false;
    }

    // Map each element to a list, then concat the results. The functional
    // bind operation; useful for "expand each element into N elements."
    public static List<U> flat_map<T, U>(List<T> list, Func<T, List<U>> f)
    {
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<U>();
        foreach (var v in list.Items)
        {
            builder.AddRange(f(v).Items);
        }
        return new List<U>(builder.ToImmutable());
    }

    // Pair corresponding elements from two lists; truncate to the shorter
    // when lengths disagree (Haskell / Rust convention; no error). Returns
    // List<Pair<T, U>>.
    public static List<Pair<T, U>> zip<T, U>(List<T> left, List<U> right)
    {
        var n = System.Math.Min(left.Items.Length, right.Items.Length);
        if (n == 0) return new List<Pair<T, U>>(System.Collections.Immutable.ImmutableArray<Pair<T, U>>.Empty);
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<Pair<T, U>>(n);
        for (var i = 0; i < n; i++)
        {
            builder.Add(new Pair<T, U>(left.Items[i], right.Items[i]));
        }
        return new List<Pair<T, U>>(builder.MoveToImmutable());
    }

    // Inverse of zip: takes a list of pairs and returns parallel lefts /
    // rights as a Pair<List<T>, List<U>>.
    public static Pair<List<T>, List<U>> unzip<T, U>(List<Pair<T, U>> pairs)
    {
        var n = pairs.Items.Length;
        if (n == 0)
        {
            return new Pair<List<T>, List<U>>(
                new List<T>(System.Collections.Immutable.ImmutableArray<T>.Empty),
                new List<U>(System.Collections.Immutable.ImmutableArray<U>.Empty));
        }
        var lefts = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(n);
        var rights = System.Collections.Immutable.ImmutableArray.CreateBuilder<U>(n);
        foreach (var p in pairs.Items)
        {
            lefts.Add(p.left);
            rights.Add(p.right);
        }
        return new Pair<List<T>, List<U>>(
            new List<T>(lefts.MoveToImmutable()),
            new List<U>(rights.MoveToImmutable()));
    }

    // Flatten a list of lists into a single list, preserving inner-list
    // order. Equivalent to fold-with-concat but with a tighter loop.
    public static List<T> flatten<T>(List<List<T>> lists)
    {
        var total = 0;
        foreach (var inner in lists.Items) total += inner.Items.Length;
        if (total == 0) return new List<T>(System.Collections.Immutable.ImmutableArray<T>.Empty);
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(total);
        foreach (var inner in lists.Items) builder.AddRange(inner.Items);
        return new List<T>(builder.MoveToImmutable());
    }

    // Stable sort by a comparator. cmp returns negative / zero / positive
    // (libc qsort convention); ties retain input order. The underlying
    // .NET sort is OrderBy, which is stable.
    public static List<T> sort_by<T>(List<T> list, Func<T, T, int> cmp)
    {
        if (list.Items.Length <= 1) return list;
        var arr = list.Items.ToArray();
        // Stable sort: use a tagged-with-index OrderBy.
        var tagged = arr
            .Select((v, i) => (v, i))
            .OrderBy(t => t, System.Collections.Generic.Comparer<(T v, int i)>.Create((a, b) =>
            {
                var c = cmp(a.v, b.v);
                return c != 0 ? c : a.i.CompareTo(b.i);
            }))
            .Select(t => t.v)
            .ToArray();
        return new List<T>(System.Collections.Immutable.ImmutableArray.Create(tagged));
    }

    // Split into (matching, non-matching) — preserves order within each
    // partition. Returns a Pair record; tuple-shaped return waits on
    // tuple-type annotations.
    public static ListPartition<T> partition<T>(List<T> list, Func<T, bool> predicate)
    {
        var yes = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>();
        var no = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>();
        foreach (var v in list.Items)
        {
            (predicate(v) ? yes : no).Add(v);
        }
        return new ListPartition<T>(
            new List<T>(yes.ToImmutable()),
            new List<T>(no.ToImmutable()));
    }
}

/// <summary>
/// Two-bucket result of <see cref="List.partition{T}"/>. Field names use
/// Overt's lowercase convention; programs read as
/// <c>let split = List.partition(list = xs, predicate = pred); split.matched</c>.
/// Until tuple-type annotations land, named-field record sidesteps the gap
/// (same shape as <see cref="MapEntry{K, V}"/>).
/// </summary>
public sealed record ListPartition<T>(List<T> matched, List<T> unmatched);

/// <summary>
/// Universal "two things" container — the working form of a 2-tuple
/// until tuple-type annotations land in Overt source. Used as the
/// element type for <see cref="List.zip{T, U}"/> and as the return
/// shape for <see cref="List.unzip{T, U}"/>. Field names use the
/// Overt-canonical lowercase.
/// </summary>
public sealed record Pair<T, U>(T left, U right);
