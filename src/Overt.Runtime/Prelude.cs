// The runtime Prelude for transpiled Overt programs.
//
// Emitted C# references this via `using static Overt.Runtime.Prelude;`. Anything
// the compiler wants to be in scope for every compiled program — Unit, Result,
// Option, Ok/Err factories, println — lives here.
//
// This is a minimal first cut, matched to what the C# emitter produces today.
// Each entry is keyed to emission patterns in CSharpEmitter.cs; if emission
// changes shape, this file updates in lockstep.

namespace Overt.Runtime;

// ---------------------------------------------------------------- Unit

/// <summary>
/// The unit type. Overt's <c>()</c> type and <c>()</c> value both map to this.
/// Singleton; every instance compares equal.
/// </summary>
public sealed record Unit
{
    public static readonly Unit Value = new();
    private Unit() { }
    public override string ToString() => "()";
}

// ---------------------------------------------------------------- Result

/// <summary>
/// <c>Result&lt;T, E&gt;</c> — the v1 error model's only error-carrying type
/// (DESIGN.md §11). Abstract base; the only two inhabitants are <see cref="ResultOk{T,E}"/>
/// and <see cref="ResultErr{T,E}"/>. Implicit conversions from <c>_OkMarker</c> and
/// <c>_ErrMarker</c> let <c>Prelude.Ok(x)</c> / <c>Prelude.Err(e)</c> target-type cleanly
/// without the caller having to spell out both type arguments.
/// </summary>
public abstract record Result<T, E>
{
    public abstract bool IsOk { get; }
    public bool IsErr => !IsOk;

    /// <summary>Extract the <c>Ok</c> value or throw. Used by the C# emitter on the
    /// Ok branch after a <c>?</c>-hoist has already early-returned on Err, and as a
    /// fallback inside conditionally-evaluated expressions where hoisting isn't
    /// applied.</summary>
    public abstract T Unwrap();

    /// <summary>Extract the <c>Err</c> value or throw. Used by the C# emitter's
    /// <c>?</c>-hoist on the Err branch to construct the propagated error without
    /// having to spell out generic arguments at the pattern-match site.</summary>
    public abstract E UnwrapErr();

    public static implicit operator Result<T, E>(_OkMarker<T> ok) => new ResultOk<T, E>(ok.Value);
    public static implicit operator Result<T, E>(_ErrMarker<E> err) => new ResultErr<T, E>(err.Error);
}

public sealed record ResultOk<T, E>(T Value) : Result<T, E>
{
    public override bool IsOk => true;
    public override T Unwrap() => Value;
    public override E UnwrapErr()
        => throw new InvalidOperationException($"UnwrapErr called on Ok({Value})");
}

public sealed record ResultErr<T, E>(E Error) : Result<T, E>
{
    public override bool IsOk => false;
    public override T Unwrap()
        => throw new InvalidOperationException($"Unwrap called on Err({Error})");
    public override E UnwrapErr() => Error;
}

// Markers carry just enough information for Result<T, E>'s implicit conversions to
// construct the right variant. They exist because C# can't infer both T and E from
// a bare call like `Ok(42)` — target-typing supplies the missing piece.
public readonly record struct _OkMarker<T>(T Value);
public readonly record struct _ErrMarker<E>(E Error);

// ---------------------------------------------------------------- Option

public abstract record Option<T>
{
    public abstract bool IsSome { get; }
    public bool IsNone => !IsSome;

    public static implicit operator Option<T>(_SomeMarker<T> s) => new OptionSome<T>(s.Value);
    public static implicit operator Option<T>(_NoneMarker _) => new OptionNone<T>();
}

public sealed record OptionSome<T>(T Value) : Option<T>
{
    public override bool IsSome => true;
}

public sealed record OptionNone<T> : Option<T>
{
    public override bool IsSome => false;
}

public readonly record struct _SomeMarker<T>(T Value);
public readonly record struct _NoneMarker;

/// <summary>
/// Non-generic namespace companion for <c>Option.X</c> module-qualified
/// calls. Distinct from <c>Option&lt;T&gt;</c> (different arity), so the
/// two coexist without naming conflict. Method-call syntax routes
/// <c>opt.unwrap_or(d)</c> through here.
/// </summary>
public static class Option
{
    /// <summary>Returns the inner T on Some, otherwise <paramref name="default_value"/>.
    /// The default is evaluated eagerly; pair with <see cref="unwrap_or_else"/>
    /// when the default is expensive or has effects.</summary>
    public static T unwrap_or<T>(Option<T> opt, T default_value)
        => opt is OptionSome<T> some ? some.Value : default_value;

    /// <summary>Lazy companion to <see cref="unwrap_or"/>. The default fn
    /// runs only when <paramref name="opt"/> is None.</summary>
    public static T unwrap_or_else<T>(Option<T> opt, Func<T> default_fn)
        => opt is OptionSome<T> some ? some.Value : default_fn();
}

/// <summary>
/// Non-generic namespace companion for <c>Result.X</c> module-qualified
/// calls. Pairs with <c>Result&lt;T, E&gt;</c> the same way <see cref="Option"/>
/// pairs with <c>Option&lt;T&gt;</c>.
/// </summary>
public static class Result
{
    /// <summary>Returns the inner T on Ok, otherwise <paramref name="default_value"/>.
    /// Default evaluated eagerly.</summary>
    public static T unwrap_or<T, E>(Result<T, E> result, T default_value)
        => result is ResultOk<T, E> ok ? ok.Value : default_value;

    /// <summary>Lazy companion. The default fn receives the Err value so it can
    /// translate, log, or otherwise react to the failure shape before producing
    /// the fallback.</summary>
    public static T unwrap_or_else<T, E>(Result<T, E> result, Func<E, T> default_fn)
        => result is ResultOk<T, E> ok ? ok.Value : default_fn(((ResultErr<T, E>)result).Error);
}

// ---------------------------------------------------------------- Error types

/// <summary>Minimal stand-in for Overt's <c>IoError</c>. Will grow to carry the
/// reason/narrative/causal-chain shape from DESIGN.md §11. Field name matches
/// Overt's lowercase-field convention so <c>IoError { narrative = "..." }</c>
/// round-trips through the emitter.</summary>
public sealed record IoError(string narrative)
{
    public override string ToString() => $"IoError: {narrative}";
}

/// <summary>
/// Thrown when a value flowing into a refinement-typed boundary fails the
/// refinement's predicate at runtime. Compile-time checks (OV0311) catch
/// literal violations; this covers the cases that compile-time evaluation
/// can't decide — typically non-literal values or predicates that call
/// functions (<c>size(self) &gt; 0</c>, etc.). See AGENTS.md §4.
/// </summary>
public sealed class RefinementViolation(string aliasName, string predicateText, object? offendingValue)
    : Exception($"value {Repr(offendingValue)} does not satisfy refinement `{aliasName}` predicate: {predicateText}")
{
    public string AliasName { get; } = aliasName;
    public string PredicateText { get; } = predicateText;
    public object? OffendingValue { get; } = offendingValue;

    private static string Repr(object? v) => v switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "?",
    };
}

/// <summary>
/// Domain-error value returned from an auto-generated <c>Alias.try_from(raw)</c>
/// when its refinement type does not supply an <c>else { ... }</c> clause.
/// A refinement that DOES supply one uses the user's own domain type
/// instead, so this is the fallback "no custom error declared" shape.
/// Round-trips through the emitter as a record value, not an exception:
/// the auto-gen lowers `try_from` failures into <c>Err(RefinementError { ... })</c>
/// in the Result return type, not a throw. Field names match Overt's
/// lowercase-field convention.
/// </summary>
public sealed record RefinementError(
    string alias_name,
    string predicate_text,
    object? offending_value)
{
    public override string ToString()
        => $"value {Repr(offending_value)} does not satisfy refinement `{alias_name}` predicate: {predicate_text}";

    private static string Repr(object? v) => v switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "?",
    };
}

/// <summary>
/// Marker thrown by the emitted stub for an <c>extern</c> whose platform
/// isn't wired up in the current runtime (e.g. <c>extern "go" fn ...</c>
/// under the C# backend). The CLI recognizes this type specifically and
/// reports a toolchain-limitation message in Overt vocabulary, rather
/// than letting it surface as an "unhandled exception" — Overt programs
/// don't have exceptions and the reader shouldn't see that word.
/// </summary>
public sealed class ExternPlatformNotImplemented(string platform, string externName)
    : Exception($"extern platform '{platform}' is not wired up in this runtime (at extern `{externName}`)")
{
    public string Platform { get; } = platform;
    public string ExternName { get; } = externName;
}

/// <summary>
/// Error variant returned by <c>race { ... }</c> when every branch fails. Carries the
/// per-branch errors in source order (DESIGN.md §12). Placeholder — proper causal-chain
/// wiring lands with the error-model milestone.
/// </summary>
public sealed record RaceAllFailed<E>(System.Collections.Immutable.ImmutableArray<E> Errors);

// ------------------------------------------------------- Collection stubs

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
/// Non-generic namespace companion for <c>String.X</c> module-qualified
/// calls in Overt source. Overt's <c>String</c> primitive lowers to .NET's
/// <see cref="string"/>; this class collects the operations the prelude
/// signature table exposes under that namespace.
/// </summary>
public static class String
{
    // Splits on the literal separator (no regex). Empty separator is a
    // programmer error and throws. Adjacent separators yield empty
    // segments (StringSplitOptions.None semantics) — callers that want
    // empties collapsed can filter() the result.
    public static List<string> split(string s, string sep)
    {
        if (sep.Length == 0)
        {
            throw new ArgumentException(
                "String.split: separator must be non-empty",
                nameof(sep));
        }
        var parts = s.Split(sep, StringSplitOptions.None);
        return new List<string>(System.Collections.Immutable.ImmutableArray.Create(parts));
    }

    // Inverse of split. Empty list yields empty string.
    public static string join(List<string> list, string sep)
        => string.Join(sep, list.Items);

    // UTF-16 code unit at the given index, as an Int. Useful for
    // building character-class predicates (digit / alpha / etc.) by
    // arithmetic on the result, without per-predicate FFI bindings.
    // Out-of-range index throws.
    public static int code_at(string s, int index)
    {
        if ((uint)index >= (uint)s.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"String.code_at: index out of range [0, {s.Length})");
        }
        return s[index];
    }

    // Each character of the input string as a single-character string.
    // Returned as an Overt List<String> so callers can iterate with
    // `for c in s.chars()` and pattern-match against literal strings.
    // For pure ASCII / character-class work that just needs the code
    // point as a number, prefer `code_points` to avoid the per-char
    // string allocation.
    public static List<string> chars(string s)
    {
        if (s.Length == 0)
        {
            return new List<string>(System.Collections.Immutable.ImmutableArray<string>.Empty);
        }
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            builder.Add(s[i].ToString());
        }
        return new List<string>(builder.MoveToImmutable());
    }

    // Each character's UTF-16 code unit as an Int, in order. The
    // numeric companion to `chars()` — same iteration shape but
    // avoids string boxing per character. Surrogate pairs surface as
    // two ints (matching .NET semantics); SemVer-style ASCII work
    // doesn't care, and Unicode-aware code can post-process.
    public static List<int> code_points(string s)
    {
        if (s.Length == 0)
        {
            return new List<int>(System.Collections.Immutable.ImmutableArray<int>.Empty);
        }
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            builder.Add(s[i]);
        }
        return new List<int>(builder.MoveToImmutable());
    }

    // Three predicate-shape helpers built on .NET's String methods.
    // Empty argument is true in every case (matches StringComparison.Ordinal
    // semantics); callers that want "non-empty prefix" can guard with
    // `length(prefix) > 0 && s.starts_with(prefix)`.
    public static bool starts_with(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);
    public static bool ends_with(string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal);
    public static bool contains(string s, string needle) => s.Contains(needle, StringComparison.Ordinal);

    // Trim removes leading and trailing whitespace (Unicode-aware via .NET's
    // Char.IsWhiteSpace). The narrative "trim" matches Rust / Python; .NET's
    // String.Trim() does the same.
    public static string trim(string s) => s.Trim();

    // Case conversion. Invariant culture so locale doesn't affect the result;
    // a Turkish-locale "i" → "I" surprise that bit Java for years isn't a
    // shape Overt programs should inherit. Programs that want locale-aware
    // case use FFI to System.Globalization.
    public static string to_upper(string s) => s.ToUpperInvariant();
    public static string to_lower(string s) => s.ToLowerInvariant();

    // Replace every occurrence of `from` with `to`. Empty `from` is a
    // programmer error and throws (matches .NET's String.Replace contract).
    public static string replace(string s, string from, string to)
    {
        if (from.Length == 0)
        {
            throw new ArgumentException(
                "String.replace: 'from' must be non-empty",
                nameof(from));
        }
        return s.Replace(from, to, StringComparison.Ordinal);
    }

    // UTF-16 code-unit-indexed substring; half-open [start, end). Out-of-range
    // indices throw (programmer error; callers guard with length() check).
    public static string substring(string s, int start, int end)
    {
        if ((uint)start > (uint)s.Length || (uint)end > (uint)s.Length || start > end)
        {
            throw new ArgumentOutOfRangeException(
                $"String.substring: indices out of range or inverted "
                + $"(start={start}, end={end}, length={s.Length})");
        }
        return s.Substring(start, end - start);
    }

    // Find the index of `needle` in `s`, or None when absent. Empty needle is
    // 0 (matches .NET / Python convention).
    public static Option<int> index_of(string s, string needle)
    {
        var i = s.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? new OptionNone<int>() : new OptionSome<int>(i);
    }

    // Repeat the string n times. n=0 yields the empty string; negative n is
    // a programmer error and throws.
    public static string repeat(string s, int n)
    {
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(n), n,
                "String.repeat: count must be non-negative");
        }
        if (n == 0 || s.Length == 0) return string.Empty;
        return string.Concat(System.Linq.Enumerable.Repeat(s, n));
    }

    // Parse helpers. CLI arg parsing and config readers are the typical
    // callers; both paths want a Result to thread into refinement
    // try_from. Invariant culture so locale doesn't affect semantics —
    // Overt programs that need locale-aware parsing build atop these.
    public static Result<int, IoError> parse_int(string s)
    {
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return new ResultOk<int, IoError>(n);
        }
        return new ResultErr<int, IoError>(
            new IoError($"could not parse '{s}' as Int"));
    }

    public static Result<double, IoError> parse_float(string s)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return new ResultOk<double, IoError>(d);
        }
        return new ResultErr<double, IoError>(
            new IoError($"could not parse '{s}' as Float"));
    }
}

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

/// <summary>
/// File I/O companion. Mirrors the small set of file operations that Overt
/// programs need without pulling in an extern binding per call. All
/// fallible operations return <c>Result&lt;T, IoError&gt;</c>; the
/// host-side exceptions are converted to <c>IoError</c> at the boundary
/// per DESIGN.md §17. Pure path-string helpers live on <see cref="Path"/>.
/// </summary>
public static class File
{
    /// <summary>Read the file at <paramref name="path"/> as UTF-8 and
    /// return its contents. Errors (file not found, permission denied,
    /// encoding failure) surface as <c>Err(IoError)</c>.</summary>
    public static Result<string, IoError> read_to_string(string path)
    {
        try
        {
            return new ResultOk<string, IoError>(global::System.IO.File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new ResultErr<string, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Write <paramref name="contents"/> to <paramref name="path"/>
    /// as UTF-8, overwriting any existing file. Returns
    /// <c>Ok(())</c> on success, <c>Err(IoError)</c> on failure.</summary>
    public static Result<Unit, IoError> write_all_text(string path, string contents)
    {
        try
        {
            global::System.IO.File.WriteAllText(path, contents);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>True iff <paramref name="path"/> names an existing file
    /// (not a directory). Predicates don't return Result; pair with
    /// <see cref="read_to_string"/> when you actually want the contents.</summary>
    public static bool exists(string path) => global::System.IO.File.Exists(path);

    /// <summary>Read the file as UTF-8, splitting on newlines. Each line
    /// excludes the trailing `\n` (and `\r\n` on Windows). The final line
    /// is included even if it lacks a trailing newline. Empty file → empty
    /// list.</summary>
    public static Result<List<string>, IoError> read_lines(string path)
    {
        try
        {
            var lines = global::System.IO.File.ReadAllLines(path);
            return new ResultOk<List<string>, IoError>(
                new List<string>(System.Collections.Immutable.ImmutableArray.Create(lines)));
        }
        catch (Exception ex)
        {
            return new ResultErr<List<string>, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Append <paramref name="contents"/> to <paramref name="path"/>
    /// (UTF-8). Creates the file if it doesn't exist.</summary>
    public static Result<Unit, IoError> append_text(string path, string contents)
    {
        try
        {
            global::System.IO.File.AppendAllText(path, contents);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Delete the file at <paramref name="path"/>. Deleting a
    /// non-existent file is a no-op (matches .NET File.Delete and POSIX
    /// `rm -f`-ish semantics — programs that want a "missing" diagnostic
    /// guard with <see cref="exists"/> first).</summary>
    public static Result<Unit, IoError> delete(string path)
    {
        try
        {
            global::System.IO.File.Delete(path);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Size of the file in bytes. Errors (not found, permission
    /// denied, etc.) surface as Err.</summary>
    public static Result<int, IoError> size(string path)
    {
        try
        {
            var info = new global::System.IO.FileInfo(path);
            // FileInfo.Length is long; clamp to int. Files larger than
            // 2 GB are vanishingly rare for the v1 stdlib's audience and
            // can FFI to FileInfo directly when they matter.
            var len = info.Length;
            if (len > int.MaxValue)
            {
                return new ResultErr<int, IoError>(new IoError(
                    $"File.size: file '{path}' exceeds Int range ({len} bytes); use FFI for large files"));
            }
            return new ResultOk<int, IoError>((int)len);
        }
        catch (Exception ex)
        {
            return new ResultErr<int, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Atomic-where-supported rename. On the same filesystem this
    /// is the rename(2) primitive; across filesystems .NET falls back to
    /// copy + delete. Programs that need strict-atomic semantics across
    /// filesystem boundaries handle that themselves.</summary>
    public static Result<Unit, IoError> move(string from, string to)
    {
        try
        {
            global::System.IO.File.Move(from, to);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Copy the file. Existing destination is overwritten —
    /// matches the conventional "cp -f" default. Programs that want a
    /// "fail if exists" check guard with <see cref="exists"/> first.</summary>
    public static Result<Unit, IoError> copy(string from, string to)
    {
        try
        {
            global::System.IO.File.Copy(from, to, overwrite: true);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }
}

/// <summary>
/// Filesystem directory operations. All carry !{io}. Directory listing,
/// creation (with parents-as-needed), and removal (with optional
/// recursive flag).
/// </summary>
public static class Directory
{
    public static bool exists(string path) => global::System.IO.Directory.Exists(path);

    /// <summary>Create the directory, including any missing parents.
    /// No-op if it already exists.</summary>
    public static Result<Unit, IoError> create(string path)
    {
        try
        {
            global::System.IO.Directory.CreateDirectory(path);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>List the entry names in the directory (file and
    /// subdirectory names; not full paths). Programs that want full
    /// paths join with <see cref="Path.join"/> per entry. The list
    /// order is filesystem-dependent and not promised stable across
    /// hosts.</summary>
    public static Result<List<string>, IoError> list(string path)
    {
        try
        {
            var entries = global::System.IO.Directory.GetFileSystemEntries(path);
            var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>(entries.Length);
            foreach (var e in entries)
            {
                builder.Add(global::System.IO.Path.GetFileName(e) ?? e);
            }
            return new ResultOk<List<string>, IoError>(new List<string>(builder.MoveToImmutable()));
        }
        catch (Exception ex)
        {
            return new ResultErr<List<string>, IoError>(new IoError(ex.Message));
        }
    }

    /// <summary>Delete the directory. With <paramref name="recursive"/>
    /// = true, removes all contents; with false, requires the directory
    /// to be empty (matches POSIX rmdir / rm -r split).</summary>
    public static Result<Unit, IoError> delete(string path, bool recursive)
    {
        try
        {
            global::System.IO.Directory.Delete(path, recursive);
            return new ResultOk<Unit, IoError>(Unit.Value);
        }
        catch (Exception ex)
        {
            return new ResultErr<Unit, IoError>(new IoError(ex.Message));
        }
    }
}

/// <summary>
/// Pure path-string helpers. None of these touch the filesystem — they
/// operate on the path string itself. For real file existence checks /
/// reads / writes, see <see cref="File"/>.
/// </summary>
public static class Path
{
    /// <summary>Join two path segments with the platform-appropriate
    /// separator. <c>Path.join("dir", "file.txt")</c> yields
    /// <c>"dir/file.txt"</c> on Unix or <c>"dir\\file.txt"</c> on
    /// Windows. The Go runtime does the same, so output round-trips
    /// across back ends on each platform.</summary>
    public static string join(string parent, string child)
        => global::System.IO.Path.Combine(parent, child);

    /// <summary>Directory portion of <paramref name="path"/>. Returns
    /// <c>None</c> when the path has no parent (a bare filename or
    /// the empty string).</summary>
    public static Option<string> parent(string path)
    {
        var dir = global::System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return new OptionNone<string>();
        return new OptionSome<string>(dir);
    }

    /// <summary>Final component of <paramref name="path"/>. Returns
    /// <c>None</c> for the empty string; otherwise the segment after
    /// the last separator.</summary>
    public static Option<string> file_name(string path)
    {
        var name = global::System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return new OptionNone<string>();
        return new OptionSome<string>(name);
    }

    /// <summary>File extension including the leading dot, e.g. <c>".ov"</c>.
    /// Returns <c>None</c> when the path has no extension.</summary>
    public static Option<string> extension(string path)
    {
        var ext = global::System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return new OptionNone<string>();
        return new OptionSome<string>(ext);
    }

    /// <summary>Replace (or add) the extension on <paramref name="path"/>.
    /// <paramref name="ext"/> may include or omit the leading dot;
    /// empty <paramref name="ext"/> strips any existing extension.</summary>
    public static string with_extension(string path, string ext)
    {
        var stripped = global::System.IO.Path.ChangeExtension(path, null) ?? path;
        if (string.IsNullOrEmpty(ext)) return stripped;
        return ext.StartsWith('.') ? stripped + ext : stripped + "." + ext;
    }

    /// <summary>True iff <paramref name="path"/> is rooted (absolute) per
    /// the host's path conventions.</summary>
    public static bool is_absolute(string path)
        => global::System.IO.Path.IsPathRooted(path);
}

/// <summary>
/// The captured result of a synchronous Process.run invocation:
/// exit code plus stdout and stderr as strings. Field names match
/// Overt's lowercase-field convention so destructuring on the Overt
/// side reads naturally (`output.exit_code`, etc.).
/// </summary>
public sealed record ProcessOutput(int exit_code, string stdout, string stderr);

/// <summary>
/// Process companion. The v1 surface is one synchronous `run` operation
/// that captures stdout / stderr / exit code in full. Streaming I/O,
/// process groups, signals, and timeouts are deferred until a real
/// orchestration program needs them. Pairs with File / Path for the
/// minimum stdlib surface a CLI tool / build script / orchestrator
/// needs.
/// </summary>
public static class Process
{
    /// <summary>Run <paramref name="cmd"/> with the given <paramref name="args"/>,
    /// wait for it to complete, and return the captured stdout, stderr,
    /// and exit code. Failures to launch the process surface as
    /// <c>Err(IoError)</c>; a process that ran but exited non-zero is
    /// still <c>Ok</c> — callers branch on <c>output.exit_code</c>.</summary>
    public static Result<ProcessOutput, IoError> run(string cmd, List<string> args)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(cmd)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args.Items) psi.ArgumentList.Add(a);
            using var p = global::System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return new ResultErr<ProcessOutput, IoError>(
                    new IoError($"failed to start process: {cmd}"));
            }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new ResultOk<ProcessOutput, IoError>(
                new ProcessOutput(p.ExitCode, stdout, stderr));
        }
        catch (Exception ex)
        {
            return new ResultErr<ProcessOutput, IoError>(new IoError(ex.Message));
        }
    }
}

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
}

/// <summary>FFI-boundary byte-string type, distinct from Overt <c>String</c>.
/// Placeholder for v1.</summary>
public sealed record CString(byte[] Bytes)
{
    // Lowercase match to Overt source's `CString.from(s)` call style.
    public static CString from(string s) => new(System.Text.Encoding.UTF8.GetBytes(s));
}

/// <summary>C-FFI raw pointer placeholder.</summary>
public readonly record struct Ptr<T>(IntPtr Raw);

// ---------------------------------------------------------------- Prelude

/// <summary>
/// Functions available unqualified in every transpiled Overt file via
/// <c>using static Overt.Runtime.Prelude;</c>.
/// </summary>
public static class Prelude
{
    // Result / Option factory helpers — return markers that target-type into the right
    // Result<T, E> or Option<T> at the call site.
    public static _OkMarker<T> Ok<T>(T value) => new(value);
    public static _ErrMarker<E> Err<E>(E error) => new(error);
    public static _SomeMarker<T> Some<T>(T value) => new(value);
    public static readonly _NoneMarker None = default;

    // I/O. Returns Result so callers can use `?` / `|>?`. Errors from Console.WriteLine
    // convert into IoError; v1 conforms to DESIGN.md §17's "exceptions → Result at
    // the boundary" rule.
    public static Result<Unit, IoError> println(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
            return Ok(Unit.Value);
        }
        catch (IOException ex)
        {
            return Err(new IoError(ex.Message));
        }
    }

    // The process command-line arguments, minus the executable path that
    // .NET puts at index 0. Mirrors the contract of `static int Main(
    // string[] args)`. Returned as an Overt List<String>; callers use
    // size(), List.at(), etc. The list is computed once per process by
    // the runtime; repeated calls are cheap.
    public static List<string> args()
    {
        var raw = Environment.GetCommandLineArgs();
        if (raw.Length <= 1)
        {
            return new List<string>(System.Collections.Immutable.ImmutableArray<string>.Empty);
        }
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>(raw.Length - 1);
        for (var i = 1; i < raw.Length; i++)
        {
            builder.Add(raw[i]);
        }
        return new List<string>(builder.MoveToImmutable());
    }

    public static Result<Unit, IoError> eprintln(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
            return Ok(Unit.Value);
        }
        catch (IOException ex)
        {
            return Err(new IoError(ex.Message));
        }
    }

    // No-trailing-newline siblings of println / eprintln. Common shape for
    // progress indicators, prompts, "running test... done." style output.
    public static Result<Unit, IoError> print(string s)
    {
        try
        {
            Console.Out.Write(s);
            return Ok(Unit.Value);
        }
        catch (IOException ex)
        {
            return Err(new IoError(ex.Message));
        }
    }

    public static Result<Unit, IoError> eprint(string s)
    {
        try
        {
            Console.Error.Write(s);
            return Ok(Unit.Value);
        }
        catch (IOException ex)
        {
            return Err(new IoError(ex.Message));
        }
    }

    // Read one line from stdin. Returns Some(line) when a line was read,
    // None at EOF. The trailing newline is stripped; an empty line returns
    // Some(""). I/O errors surface as Err(IoError).
    public static Result<Option<string>, IoError> read_line()
    {
        try
        {
            var line = Console.In.ReadLine();
            return line is null
                ? Ok((Option<string>)new OptionNone<string>())
                : Ok((Option<string>)new OptionSome<string>(line));
        }
        catch (IOException ex)
        {
            return Err(new IoError(ex.Message));
        }
    }

    // Consume all of stdin as a single string. Standard `cat file | tool`
    // pipe-consumer pattern. Returns the empty string when stdin is at EOF
    // immediately.
    public static Result<string, IoError> read_to_end()
    {
        try
        {
            return Ok(Console.In.ReadToEnd());
        }
        catch (IOException ex)
        {
            return Err(new IoError(ex.Message));
        }
    }

    // ------------------------------- Collection operations.

    public static int size<T>(List<T> list) => list.Items.Length;
    public static int length(string s) => s.Length;
    public static int len<T>(List<T> list) => list.Items.Length;

    public static List<U> map<T, U>(List<T> list, Func<T, U> f)
    {
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<U>(list.Items.Length);
        foreach (var item in list.Items) builder.Add(f(item));
        return new List<U>(builder.MoveToImmutable());
    }

    public static List<T> filter<T>(List<T> list, Func<T, bool> predicate)
    {
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>();
        foreach (var item in list.Items)
            if (predicate(item)) builder.Add(item);
        return new List<T>(builder.ToImmutable());
    }

    // par_map: runs f concurrently over all items, preserves input order, and
    // returns the first Err (by original index) if any element fails. On empty
    // input returns Ok of the empty list. The Overt signature declares
    // !{io, async, E} — TPL satisfies async; io is over-approximated.
    //
    // Implementation uses Task.Run per item rather than Parallel.For. The
    // parallel-loop scheduler's heuristics can elect to run every iteration
    // inline on the calling thread when the work per item is small, which
    // silently violates par_map's "genuinely concurrent" contract. Task-per-
    // item forces enqueue onto the thread pool, so callers always observe
    // the concurrency they asked for. Per-task overhead is cheap for the
    // list sizes Overt programs use in practice.
    public static Result<List<U>, E> par_map<T, U, E>(List<T> list, Func<T, Result<U, E>> f)
    {
        var items = list.Items;
        if (items.Length == 0)
            return Ok(new List<U>(System.Collections.Immutable.ImmutableArray<U>.Empty));

        var results = new Result<U, E>[items.Length];
        var tasks = new System.Threading.Tasks.Task[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            int idx = i;
            tasks[idx] = System.Threading.Tasks.Task.Run(() => results[idx] = f(items[idx]));
        }
        System.Threading.Tasks.Task.WaitAll(tasks);

        var okBuilder = System.Collections.Immutable.ImmutableArray.CreateBuilder<U>(items.Length);
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i] is ResultErr<U, E> err) return Err<E>(err.Error);
            okBuilder.Add(((ResultOk<U, E>)results[i]).Value);
        }
        return Ok(new List<U>(okBuilder.MoveToImmutable()));
    }

    public static U fold<T, U>(List<T> list, U seed, Func<U, T, U> step)
    {
        var acc = seed;
        foreach (var item in list.Items) acc = step(acc, item);
        return acc;
    }

    // Universal / existential predicate combinators. Vacuous all on an
    // empty list returns true (the universal-quantification convention);
    // vacuous any returns false. Both short-circuit, so callers can pass
    // expensive predicates without paying for the whole list when the
    // answer is decidable from a prefix.
    public static bool all<T>(List<T> list, Func<T, bool> predicate)
    {
        foreach (var item in list.Items)
        {
            if (!predicate(item)) return false;
        }
        return true;
    }

    public static bool any<T>(List<T> list, Func<T, bool> predicate)
    {
        foreach (var item in list.Items)
        {
            if (predicate(item)) return true;
        }
        return false;
    }

    // try_map: the sequential, pure cousin of par_map. Walks the list in order
    // and short-circuits on the first Err. Carries no io/async effect — use
    // when the callback is a pure validator and the parallelism in par_map
    // would force unwanted effects into the caller's row.
    public static Result<List<U>, E> try_map<T, U, E>(List<T> list, Func<T, Result<U, E>> f)
    {
        var items = list.Items;
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<U>(items.Length);
        foreach (var item in items)
        {
            var r = f(item);
            if (r is ResultErr<U, E> err)
            {
                return Err<E>(err.Error);
            }
            builder.Add(((ResultOk<U, E>)r).Value);
        }
        return Ok(new List<U>(builder.MoveToImmutable()));
    }

    // Trace is a stdlib namespace-shaped type so transpiled code can write
    // `Trace.subscribe(...)`. Subscribers live in a process-wide list; emit()
    // dispatches synchronously in registration order. The richer causal-chain
    // wiring from DESIGN.md §14 lands with the traces milestone.
    public static class Trace
    {
        private static readonly System.Collections.Generic.List<Func<TraceEvent, Unit>> _subscribers = new();
        private static readonly object _lock = new();

        // Consumer matches the emitted shape of `fn f(e: TraceEvent) !{io} -> ()` which
        // returns Unit, not void, so Func<TraceEvent, Unit> — not Action<TraceEvent>.
        public static void subscribe(Func<TraceEvent, Unit> consumer)
        {
            lock (_lock) _subscribers.Add(consumer);
        }

        public static void emit(TraceEvent evt)
        {
            Func<TraceEvent, Unit>[] snapshot;
            lock (_lock) snapshot = _subscribers.ToArray();
            foreach (var s in snapshot) s(evt);
        }

        // For tests: reset the subscriber list to a known state.
        public static void _reset()
        {
            lock (_lock) _subscribers.Clear();
        }
    }
}

/// <summary>Marker carried by all Overt trace events (DESIGN.md §14). Placeholder.</summary>
public abstract record TraceEvent;
