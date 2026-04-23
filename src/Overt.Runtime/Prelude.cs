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

    /// <summary>Unwrap the <c>Ok</c> value or throw. Mirrors the <c>?</c> operator's
    /// post-lowering shape; a future pass will replace direct calls with proper
    /// propagation at the caller site.</summary>
    public abstract T Unwrap();

    public static implicit operator Result<T, E>(_OkMarker<T> ok) => new ResultOk<T, E>(ok.Value);
    public static implicit operator Result<T, E>(_ErrMarker<E> err) => new ResultErr<T, E>(err.Error);
}

public sealed record ResultOk<T, E>(T Value) : Result<T, E>
{
    public override bool IsOk => true;
    public override T Unwrap() => Value;
}

public sealed record ResultErr<T, E>(E Error) : Result<T, E>
{
    public override bool IsOk => false;
    public override T Unwrap()
        => throw new InvalidOperationException($"Unwrap called on Err({Error})");
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
/// reason/narrative/causal-chain shape from DESIGN.md §11.</summary>
public sealed record IoError(string Narrative)
{
    public override string ToString() => $"IoError: {Narrative}";
}

// ------------------------------------------------------- Collection stubs

/// <summary>Minimal ordered collection placeholder. Real implementation lands with the
/// stdlib milestone; this shape is just enough to let transpiled code type-check.</summary>
public sealed record List<T>(System.Collections.Immutable.ImmutableArray<T> Items);

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

    // ------------------------------- Collection-operation stubs.
    //
    // These exist so transpiled code type-checks. Runtime semantics arrive with the
    // stdlib milestone; until then each stub throws if invoked. The point of having
    // them now is to close the compile-check loop for every example program — a test
    // that the C# emitter produces shape-valid output, not that it produces correct
    // semantics.

    public static int size<T>(List<T> list) => list.Items.Length;
    public static int length(string s) => s.Length;
    public static int len<T>(List<T> list) => list.Items.Length;

    public static List<U> map<T, U>(List<T> list, Func<T, U> f)
        => throw new NotImplementedException("stdlib map not wired up");

    public static List<T> filter<T>(List<T> list, Func<T, bool> predicate)
        => throw new NotImplementedException("stdlib filter not wired up");

    public static Result<List<U>, E> par_map<T, U, E>(List<T> list, Func<T, Result<U, E>> f)
        => throw new NotImplementedException("stdlib par_map not wired up");

    public static U fold<T, U>(List<T> list, U seed, Func<U, T, U> step)
        => throw new NotImplementedException("stdlib fold not wired up");

    // Trace is a stdlib namespace-shaped type so transpiled code can write
    // `Trace.subscribe(...)` etc.
    public static class Trace
    {
        public static void subscribe(Action<TraceEvent> consumer)
            => throw new NotImplementedException("stdlib Trace.subscribe not wired up");
    }
}

/// <summary>Marker carried by all Overt trace events (DESIGN.md §14). Placeholder.</summary>
public abstract record TraceEvent;
