// Int — namespace companion for `Int.X` module-qualified calls in Overt
// source. The Int primitive itself is .NET's int; this file collects
// helpers that don't fit as instance methods.

namespace Overt.Runtime;

/// <summary>
/// Companion class for <c>Int.X</c> module-qualified calls. Overt's
/// <c>Int</c> primitive lowers to .NET's <see cref="int"/> (32-bit
/// signed); this class collects integer-shaped helpers that don't fit
/// as instance methods.
/// </summary>
public static class Int
{
    // Half-open integer range [start, end). Materialized eagerly as
    // a List<Int> so callers can use it with `for i in Int.range(0, n)`
    // without an iterator abstraction. For a closed range, pass `end + 1`.
    // A `start >= end` argument yields the empty list (matches Python
    // semantics, avoids a separate null/error channel).
    public static List<int> range(int start, int end)
    {
        if (start >= end)
        {
            return new List<int>(System.Collections.Immutable.ImmutableArray<int>.Empty);
        }
        var n = end - start;
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(n);
        for (int i = start; i < end; i++)
        {
            builder.Add(i);
        }
        return new List<int>(builder.MoveToImmutable());
    }
}
