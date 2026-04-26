using System.Collections.Immutable;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Semantics;

/// <summary>
/// Synthetic stdlib declarations: the names and signatures every Overt program sees
/// without an explicit <c>use</c>. Lives here (in the compiler) rather than in
/// <c>Overt.Runtime</c> because the checker needs signature-level visibility before
/// runtime code is involved, and a real <c>prelude.ov</c> file is out of scope until
/// the stdlib milestone.
///
/// Each entry pairs a <see cref="Symbol"/> — with a sentinel <c>0:0</c> declaration
/// span to distinguish synthetic from source — with its <see cref="TypeRef"/>.
/// Consumers:
/// <list type="bullet">
///   <item><see cref="NameResolver"/> seeds the module scope with the symbols so
///     references like <c>println</c> / <c>Ok</c> / <c>Result</c> resolve cleanly
///     instead of falling through an allow-list.</item>
///   <item><see cref="TypeChecker"/> pre-populates its symbol-type map with the
///     signatures so downstream inference has real types to propagate.</item>
/// </list>
/// </summary>
public static class Stdlib
{
    private static readonly SourceSpan Synth = new(new SourcePosition(0, 0), new SourcePosition(0, 0));

    private static readonly List<(Symbol Symbol, TypeRef Type)> Entries = BuildEntries();

    /// <summary>Symbol index by name for resolver seeding.</summary>
    public static ImmutableDictionary<string, Symbol> Symbols { get; } =
        Entries.ToImmutableDictionary(e => e.Symbol.Name, e => e.Symbol, StringComparer.Ordinal);

    /// <summary>Symbol → TypeRef for type-checker seeding.</summary>
    public static ImmutableDictionary<Symbol, TypeRef> Types { get; } =
        Entries.ToImmutableDictionary(e => e.Symbol, e => e.Type);

    /// <summary>
    /// Per-fn parameter names. Populated only for entries that need
    /// names at emit time — currently the namespace fns (`String.X`,
    /// `List.X`) reachable through method-call syntax, where the
    /// emitter must spell the underlying first-arg name when splicing
    /// the receiver. Lookup keyed by the same fn name as
    /// <see cref="Symbols"/>; missing entries fall back to no-name
    /// emission, which is fine because the typer doesn't validate
    /// argument names against parameter names today.
    /// </summary>
    public static ImmutableDictionary<string, ImmutableArray<string>> ParameterNames { get; } =
        BuildParameterNames();

    private static ImmutableDictionary<string, ImmutableArray<string>> BuildParameterNames()
    {
        var b = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        // Names for the stdlib namespace fns whose first arg is the
        // receiver under method-call syntax. Other fns (println,
        // map, etc.) don't need this until method-call routes them.
        b["String.split"] = ImmutableArray.Create("s", "sep");
        b["String.join"] = ImmutableArray.Create("list", "sep");
        b["String.code_at"] = ImmutableArray.Create("s", "index");
        b["String.chars"] = ImmutableArray.Create("s");
        b["String.code_points"] = ImmutableArray.Create("s");
        b["String.starts_with"] = ImmutableArray.Create("s", "prefix");
        b["String.ends_with"] = ImmutableArray.Create("s", "suffix");
        b["String.contains"] = ImmutableArray.Create("s", "needle");
        b["Option.unwrap_or"] = ImmutableArray.Create("opt", "default_value");
        b["Option.unwrap_or_else"] = ImmutableArray.Create("opt", "default_fn");
        b["Result.unwrap_or"] = ImmutableArray.Create("result", "default_value");
        b["Result.unwrap_or_else"] = ImmutableArray.Create("result", "default_fn");
        b["Int.range"] = ImmutableArray.Create("start", "end");
        b["List.at"] = ImmutableArray.Create("list", "index");
        b["all"] = ImmutableArray.Create("list", "predicate");
        b["any"] = ImmutableArray.Create("list", "predicate");
        return b.ToImmutable();
    }

    /// <summary>
    /// Variant names for stdlib enum-shaped types. Consumed by the match-exhaustiveness
    /// check so <c>match opt { Some(x) =&gt; ..., None =&gt; ... }</c> and
    /// <c>match r { Ok(x) =&gt; ..., Err(e) =&gt; ... }</c> get the same treatment as
    /// user-declared enums — the compiler flags any missing arm.
    ///
    /// Each entry's variants are listed in declaration order; the exhaustiveness
    /// reporter sorts alphabetically at diagnostic time for deterministic output.
    /// Arities are not recorded here — a future arity/pattern-shape check can consume
    /// them from the factory signatures in <see cref="Symbols"/> if needed.
    /// </summary>
    public static ImmutableDictionary<string, ImmutableArray<string>> EnumVariants { get; }
        = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["Result"] = ImmutableArray.Create("Ok", "Err"),
            ["Option"] = ImmutableArray.Create("Some", "None"),
        }.ToImmutableDictionary();

    private static List<(Symbol, TypeRef)> BuildEntries()
    {
        var e = new List<(Symbol, TypeRef)>();

        // ---- Primitive types (so NamedType("Int") lookups can resolve) ---------
        // These aren't strictly necessary — the resolver and checker short-circuit
        // primitives — but making them visible as symbols keeps the model uniform.

        // ---- Stdlib types (nominal; arity captured for future generic checks) ---
        e.Add(Type("Result"));
        e.Add(Type("Option"));
        e.Add(Type("List"));
        e.Add(Type("Map"));
        e.Add(Type("Set"));
        e.Add(Type("IoError"));
        e.Add(Type("HttpError"));
        e.Add(Type("TraceEvent"));
        e.Add(Type("RaceAllFailed"));
        e.Add(Type("CString"));
        e.Add(Type("Ptr"));
        e.Add(Type("Trace")); // stdlib namespace shape
        e.Add(Type("Task"));  // async-boundary wrapper; see AGENTS.md §9
        e.Add(Type("String")); // namespace shape for String.split / String.join / etc.
        e.Add(Type("Int"));    // namespace shape for Int.range / etc.

        // ---- Result / Option factory helpers -----------------------------------
        // Ok<T, E>(value: T) -> Result<T, E>
        e.Add(Fn("Ok",
            typeParams: new[] { "T", "E" },
            parameters: new[] { TV("T") },
            ret: Generic("Result", TV("T"), TV("E"))));

        // Err<T, E>(error: E) -> Result<T, E>
        e.Add(Fn("Err",
            typeParams: new[] { "T", "E" },
            parameters: new[] { TV("E") },
            ret: Generic("Result", TV("T"), TV("E"))));

        // Some<T>(value: T) -> Option<T>
        e.Add(Fn("Some",
            typeParams: new[] { "T" },
            parameters: new[] { TV("T") },
            ret: Generic("Option", TV("T"))));

        // None<T>() -> Option<T>
        e.Add(Fn("None",
            typeParams: new[] { "T" },
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("Option", TV("T"))));

        // ---- I/O -----------------------------------------------------------------
        // println(line: String) !{io} -> Result<Unit, IoError>
        e.Add(Fn("println",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        e.Add(Fn("eprintln",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // args() !{io} -> List<String>
        // Process command-line arguments, minus the exe path. `io` because
        // it observes process state; effect-row tracking matters when a
        // library reaches for argv (it has to declare the dependency).
        e.Add(Fn("args",
            typeParams: Array.Empty<string>(),
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("List", PrimitiveType.String),
            effects: new[] { "io" }));

        // ---- Collection operations ----------------------------------------------
        // size<T>(list: List<T>) -> Int
        e.Add(Fn("size",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")) },
            ret: PrimitiveType.Int));

        e.Add(Fn("len",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")) },
            ret: PrimitiveType.Int));

        e.Add(Fn("length",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Int));

        // map<T, U, E>(list: List<T>, f: fn(T) !{E} -> U) !{E} -> List<U>
        e.Add(Fn("map",
            typeParams: new[] { "T", "U", "E" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("T")),
                    TV("U"),
                    ImmutableArray.Create("E")),
            },
            ret: Generic("List", TV("U")),
            effects: new[] { "E" }));

        // filter<T, E>(list: List<T>, pred: fn(T) !{E} -> Bool) !{E} -> List<T>
        e.Add(Fn("filter",
            typeParams: new[] { "T", "E" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("T")),
                    PrimitiveType.Bool,
                    ImmutableArray.Create("E")),
            },
            ret: Generic("List", TV("T")),
            effects: new[] { "E" }));

        // par_map<T, U, E>(list: List<T>, f: fn(T) !{io, async} -> Result<U, E>)
        //     !{io, async} -> Result<List<U>, E>
        // Runs the callback concurrently over each item; any Err short-circuits the
        // whole pipeline, so the return type is a Result wrapping the output list.
        // `|>?` unwraps this — see InferBinary's PipePropagate branch.
        e.Add(Fn("par_map",
            typeParams: new[] { "T", "U", "E" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("T")),
                    Generic("Result", TV("U"), TV("E")),
                    ImmutableArray.Create("io", "async")),
            },
            ret: Generic("Result", Generic("List", TV("U")), TV("E")),
            effects: new[] { "io", "async" }));

        // try_map<T, U, E>(list: List<T>, f: fn(T) !{E} -> Result<U, E>) !{E} -> Result<List<U>, E>
        // Sequential, pure cousin of par_map — same shape, no io/async in the
        // effect row. Short-circuits on the first Err in iteration order.
        e.Add(Fn("try_map",
            typeParams: new[] { "T", "U", "E" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("T")),
                    Generic("Result", TV("U"), TV("E")),
                    ImmutableArray.Create("E")),
            },
            ret: Generic("Result", Generic("List", TV("U")), TV("E")),
            effects: new[] { "E" }));

        // fold<T, U>(list: List<T>, seed: U, step: fn(U, T) -> U) -> U
        e.Add(Fn("fold",
            typeParams: new[] { "T", "U" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                TV("U"),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("U"), TV("T")),
                    TV("U"),
                    ImmutableArray<string>.Empty),
            },
            ret: TV("U")));

        // all<T, E>(list: List<T>, predicate: fn(T) !{E} -> Bool) !{E} -> Bool
        // Universal quantifier. True iff predicate(item) holds for every
        // element; vacuously true on the empty list. Short-circuits on the
        // first false. The predicate's effect row is propagated.
        e.Add(Fn("all",
            typeParams: new[] { "T", "E" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("T")),
                    PrimitiveType.Bool,
                    ImmutableArray.Create("E")),
            },
            ret: PrimitiveType.Bool,
            effects: new[] { "E" }));

        // any<T, E>(list: List<T>, predicate: fn(T) !{E} -> Bool) !{E} -> Bool
        // Existential quantifier. True iff predicate(item) holds for at
        // least one element; vacuously false on the empty list. Short-
        // circuits on the first true. The predicate's effect row is
        // propagated.
        e.Add(Fn("any",
            typeParams: new[] { "T", "E" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("T")),
                    PrimitiveType.Bool,
                    ImmutableArray.Create("E")),
            },
            ret: PrimitiveType.Bool,
            effects: new[] { "E" }));

        // ---- Module-qualified stdlib members --------------------------------
        // These resolve via the name-qualified lookup path the resolver takes for
        // `Module.member` callees. Adding entries here lets the type checker see
        // their signatures (and, via effects, lets OV0310 reach through them).

        // List.empty<T>() -> List<T>
        e.Add(Fn("List.empty",
            typeParams: new[] { "T" },
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("List", TV("T"))));

        // List.singleton<T>(value: T) -> List<T>
        e.Add(Fn("List.singleton",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { TV("T") },
            ret: Generic("List", TV("T"))));

        // List.concat_three<T>(first: List<T>, middle: List<T>, last: List<T>) -> List<T>
        e.Add(Fn("List.concat_three",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                Generic("List", TV("T")),
                Generic("List", TV("T")),
            },
            ret: Generic("List", TV("T"))));

        // List.at<T>(list: List<T>, index: Int) -> T
        // Out-of-range index throws at runtime (programmer error, not a domain
        // condition), so the signature is total — no Result wrap.
        e.Add(Fn("List.at",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                PrimitiveType.Int,
            },
            ret: TV("T")));

        // String.split(s: String, sep: String) -> List<String>
        // Empty separator throws; adjacent separators yield empty segments
        // (StringSplitOptions.None semantics).
        e.Add(Fn("String.split",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: Generic("List", PrimitiveType.String)));

        // String.join(list: List<String>, sep: String) -> String
        e.Add(Fn("String.join",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[]
            {
                Generic("List", PrimitiveType.String),
                PrimitiveType.String,
            },
            ret: PrimitiveType.String));

        // String.code_at(s: String, index: Int) -> Int
        // UTF-16 code unit at the given index. Out-of-range index throws.
        // Useful for predicate-building (digit/alpha checks via arithmetic
        // on the result) without a per-predicate FFI binding.
        e.Add(Fn("String.code_at",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.Int },
            ret: PrimitiveType.Int));

        // String.chars(s: String) -> List<String>
        // Each character as a single-character string. Pairs with the
        // bare-`for` form: `for c in s.chars() { ... }`.
        e.Add(Fn("String.chars",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("List", PrimitiveType.String)));

        // String.code_points(s: String) -> List<Int>
        // Numeric companion to chars() — each character's UTF-16 code
        // unit as an Int, in order. Cheaper than chars() when the
        // caller only needs numeric predicates.
        e.Add(Fn("String.code_points",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("List", PrimitiveType.Int)));

        // String.starts_with(s: String, prefix: String) -> Bool
        // True iff `s` begins with `prefix`. Empty prefix is true.
        e.Add(Fn("String.starts_with",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: PrimitiveType.Bool));

        // String.ends_with(s: String, suffix: String) -> Bool
        // True iff `s` ends with `suffix`. Empty suffix is true.
        e.Add(Fn("String.ends_with",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: PrimitiveType.Bool));

        // String.contains(s: String, needle: String) -> Bool
        // True iff `needle` appears anywhere in `s`. Empty needle is
        // true (matches the .NET / Go convention; "every string contains
        // the empty string").
        e.Add(Fn("String.contains",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: PrimitiveType.Bool));

        // Option.unwrap_or<T>(opt: Option<T>, default_value: T) -> T
        // Returns the inner T on Some, otherwise the default. The
        // default is evaluated eagerly; for a lazily-computed default
        // use unwrap_or_else.
        e.Add(Fn("Option.unwrap_or",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("Option", TV("T")),
                TV("T"),
            },
            ret: TV("T")));

        // Option.unwrap_or_else<T, E>(opt: Option<T>, default_fn: fn() !{E} -> T) !{E} -> T
        // Lazy companion to unwrap_or. The default fn runs only when
        // opt is None; its effect row is propagated.
        e.Add(Fn("Option.unwrap_or_else",
            typeParams: new[] { "T", "E" },
            parameters: new TypeRef[]
            {
                Generic("Option", TV("T")),
                new FunctionTypeRef(
                    ImmutableArray<TypeRef>.Empty,
                    TV("T"),
                    ImmutableArray.Create("E")),
            },
            ret: TV("T"),
            effects: new[] { "E" }));

        // Result.unwrap_or<T, E>(result: Result<T, E>, default_value: T) -> T
        // Returns the inner T on Ok, otherwise the default. As with
        // Option.unwrap_or the default is evaluated eagerly.
        e.Add(Fn("Result.unwrap_or",
            typeParams: new[] { "T", "E" },
            parameters: new TypeRef[]
            {
                Generic("Result", TV("T"), TV("E")),
                TV("T"),
            },
            ret: TV("T")));

        // Result.unwrap_or_else<T, E, F>(result: Result<T, E>,
        //                                default_fn: fn(E) !{F} -> T) !{F} -> T
        // Lazy companion. The default fn receives the Err value so it
        // can react to the failure shape (translate, log, retry, etc.)
        // before producing the fallback. Its effect row is propagated.
        e.Add(Fn("Result.unwrap_or_else",
            typeParams: new[] { "T", "E", "F" },
            parameters: new TypeRef[]
            {
                Generic("Result", TV("T"), TV("E")),
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(TV("E")),
                    TV("T"),
                    ImmutableArray.Create("F")),
            },
            ret: TV("T"),
            effects: new[] { "F" }));

        // Int.range(start: Int, end: Int) -> List<Int>
        // Half-open integer range [start, end). Useful with `for i in
        // Int.range(0, n)` when an index, not the element, is what the
        // body needs. start >= end yields the empty list.
        e.Add(Fn("Int.range",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.Int, PrimitiveType.Int },
            ret: Generic("List", PrimitiveType.Int)));

        // Trace.subscribe(consumer: fn(TraceEvent) !{io} -> ()) !{io} -> ()
        e.Add(Fn("Trace.subscribe",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[]
            {
                new FunctionTypeRef(
                    ImmutableArray.Create<TypeRef>(Named("TraceEvent")),
                    PrimitiveType.Unit,
                    ImmutableArray.Create("io")),
            },
            ret: PrimitiveType.Unit,
            effects: new[] { "io" }));

        // CString.from(s: String) -> CString (C-FFI boundary conversion; no effects)
        e.Add(Fn("CString.from",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Named("CString")));

        return e;
    }

    // ------------------------------------------------------------- helpers

    private static (Symbol, TypeRef) Type(string name)
        => (new Symbol(SymbolKind.Record, name, Synth), new NamedTypeRef(name));

    private static (Symbol, TypeRef) Fn(
        string name,
        string[] typeParams,
        TypeRef[] parameters,
        TypeRef ret,
        string[]? effects = null)
    {
        // Synthetic Symbol uses Function kind for stdlib functions regardless of
        // Overt's internal distinctions; downstream consumers don't care about the
        // declared-ness of stdlib entries.
        var symbol = new Symbol(SymbolKind.Function, name, Synth);
        var type = new FunctionTypeRef(
            parameters.ToImmutableArray(),
            ret,
            (effects ?? Array.Empty<string>()).ToImmutableArray());
        return (symbol, type);
    }

    private static TypeVarRef TV(string name) => new(name);

    private static NamedTypeRef Named(string name) => new(name);

    private static NamedTypeRef Generic(string name, params TypeRef[] args)
        => new(name, args.ToImmutableArray());
}
