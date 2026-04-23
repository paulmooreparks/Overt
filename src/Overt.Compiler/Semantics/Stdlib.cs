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
