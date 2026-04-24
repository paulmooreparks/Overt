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
}

public sealed record Map<K, V>(System.Collections.Immutable.ImmutableDictionary<K, V> Items)
    where K : notnull;

public sealed record Set<T>(System.Collections.Immutable.ImmutableHashSet<T> Items);

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
