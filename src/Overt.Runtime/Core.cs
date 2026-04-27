// Foundational runtime types: Unit, Result, Option, IoError, refinement
// errors, FFI placeholders. Every other Prelude file depends on these.

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

// ---------------------------------------------------------------- FFI placeholders

/// <summary>FFI-boundary byte-string type, distinct from Overt <c>String</c>.
/// Placeholder for v1.</summary>
public sealed record CString(byte[] Bytes)
{
    // Lowercase match to Overt source's `CString.from(s)` call style.
    public static CString from(string s) => new(System.Text.Encoding.UTF8.GetBytes(s));
}

/// <summary>C-FFI raw pointer placeholder.</summary>
public readonly record struct Ptr<T>(IntPtr Raw);
