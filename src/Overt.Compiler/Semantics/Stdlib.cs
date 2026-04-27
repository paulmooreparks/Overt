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
        b["String.parse_int"] = ImmutableArray.Create("s");
        b["String.parse_float"] = ImmutableArray.Create("s");
        b["String.trim"] = ImmutableArray.Create("s");
        b["String.to_upper"] = ImmutableArray.Create("s");
        b["String.to_lower"] = ImmutableArray.Create("s");
        b["String.replace"] = ImmutableArray.Create("s", "from", "to");
        b["String.substring"] = ImmutableArray.Create("s", "start", "end");
        b["String.index_of"] = ImmutableArray.Create("s", "needle");
        b["String.repeat"] = ImmutableArray.Create("s", "n");
        b["File.read_to_string"] = ImmutableArray.Create("path");
        b["File.write_all_text"] = ImmutableArray.Create("path", "contents");
        b["File.exists"] = ImmutableArray.Create("path");
        b["File.read_lines"] = ImmutableArray.Create("path");
        b["File.append_text"] = ImmutableArray.Create("path", "contents");
        b["File.delete"] = ImmutableArray.Create("path");
        b["File.size"] = ImmutableArray.Create("path");
        b["File.move"] = ImmutableArray.Create("from", "to");
        b["File.copy"] = ImmutableArray.Create("from", "to");
        b["File.read_bytes"] = ImmutableArray.Create("path");
        b["File.write_bytes"] = ImmutableArray.Create("path", "data");
        b["Bytes.empty"] = ImmutableArray<string>.Empty;
        b["Bytes.from_list"] = ImmutableArray.Create("list");
        b["Bytes.size"] = ImmutableArray.Create("b");
        b["Bytes.at"] = ImmutableArray.Create("b", "index");
        b["Bytes.slice"] = ImmutableArray.Create("b", "start", "end");
        b["Bytes.concat"] = ImmutableArray.Create("left", "right");
        b["Bytes.from_utf8"] = ImmutableArray.Create("s");
        b["Bytes.to_utf8"] = ImmutableArray.Create("b");
        b["Log.debug"] = ImmutableArray.Create("message");
        b["Log.info"] = ImmutableArray.Create("message");
        b["Log.warn"] = ImmutableArray.Create("message");
        b["Log.error"] = ImmutableArray.Create("message");
        b["Directory.exists"] = ImmutableArray.Create("path");
        b["Directory.create"] = ImmutableArray.Create("path");
        b["Directory.list"] = ImmutableArray.Create("path");
        b["Directory.delete"] = ImmutableArray.Create("path", "recursive");
        b["Path.join"] = ImmutableArray.Create("parent", "child");
        b["Path.parent"] = ImmutableArray.Create("path");
        b["Path.file_name"] = ImmutableArray.Create("path");
        b["Path.extension"] = ImmutableArray.Create("path");
        b["Path.with_extension"] = ImmutableArray.Create("path", "ext");
        b["Path.is_absolute"] = ImmutableArray.Create("path");
        b["print"] = ImmutableArray.Create("s");
        b["eprint"] = ImmutableArray.Create("s");
        b["read_line"] = ImmutableArray<string>.Empty;
        b["read_to_end"] = ImmutableArray<string>.Empty;
        b["Process.run"] = ImmutableArray.Create("cmd", "args");
        b["Map.empty"] = ImmutableArray<string>.Empty;
        b["Map.get"] = ImmutableArray.Create("map", "key");
        b["Map.contains_key"] = ImmutableArray.Create("map", "key");
        b["Map.insert"] = ImmutableArray.Create("map", "key", "value");
        b["Map.remove"] = ImmutableArray.Create("map", "key");
        b["Map.size"] = ImmutableArray.Create("map");
        b["Map.keys"] = ImmutableArray.Create("map");
        b["Map.values"] = ImmutableArray.Create("map");
        b["Map.entries"] = ImmutableArray.Create("map");
        b["Map.merge"] = ImmutableArray.Create("left", "right");
        b["Map.map"] = ImmutableArray.Create("map", "f");
        b["Map.filter"] = ImmutableArray.Create("map", "predicate");
        b["Set.empty"] = ImmutableArray<string>.Empty;
        b["Set.contains"] = ImmutableArray.Create("set", "value");
        b["Set.insert"] = ImmutableArray.Create("set", "value");
        b["Set.remove"] = ImmutableArray.Create("set", "value");
        b["Set.size"] = ImmutableArray.Create("set");
        b["Set.union"] = ImmutableArray.Create("left", "right");
        b["Set.intersect"] = ImmutableArray.Create("left", "right");
        b["Set.difference"] = ImmutableArray.Create("left", "right");
        b["Option.unwrap_or"] = ImmutableArray.Create("opt", "default_value");
        b["Option.unwrap_or_else"] = ImmutableArray.Create("opt", "default_fn");
        b["Result.unwrap_or"] = ImmutableArray.Create("result", "default_value");
        b["Result.unwrap_or_else"] = ImmutableArray.Create("result", "default_fn");
        b["Int.range"] = ImmutableArray.Create("start", "end");
        b["List.at"] = ImmutableArray.Create("list", "index");
        b["List.concat"] = ImmutableArray.Create("left", "right");
        b["List.head"] = ImmutableArray.Create("list");
        b["List.tail"] = ImmutableArray.Create("list");
        b["List.take"] = ImmutableArray.Create("list", "n");
        b["List.drop"] = ImmutableArray.Create("list", "n");
        b["List.reverse"] = ImmutableArray.Create("list");
        b["List.find"] = ImmutableArray.Create("list", "predicate");
        b["List.find_index"] = ImmutableArray.Create("list", "predicate");
        b["List.contains"] = ImmutableArray.Create("list", "value");
        b["List.flat_map"] = ImmutableArray.Create("list", "f");
        b["List.partition"] = ImmutableArray.Create("list", "predicate");
        b["List.zip"] = ImmutableArray.Create("left", "right");
        b["List.unzip"] = ImmutableArray.Create("pairs");
        b["List.flatten"] = ImmutableArray.Create("lists");
        b["List.sort_by"] = ImmutableArray.Create("list", "cmp");
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
        // RefinementError is the default Err arm of an auto-generated
        // `Alias.try_from` when the refinement type doesn't supply an
        // `else { ... }` clause. Refinements that DO supply one use
        // their own domain type instead.
        e.Add(Type("RefinementError"));
        e.Add(Type("HttpError"));
        e.Add(Type("TraceEvent"));
        e.Add(Type("RaceAllFailed"));
        e.Add(Type("CString"));
        e.Add(Type("Ptr"));
        e.Add(Type("Trace")); // stdlib namespace shape
        e.Add(Type("Task"));  // async-boundary wrapper; see AGENTS.md §9
        e.Add(Type("String")); // namespace shape for String.split / String.join / etc.
        e.Add(Type("Int"));    // namespace shape for Int.range / etc.
        e.Add(Type("File"));   // namespace shape for File.read_to_string / etc.
        e.Add(Type("Directory")); // namespace shape for Directory.list / etc.
        e.Add(Type("Path"));   // namespace shape for Path.join / etc.
        e.Add(Type("Process")); // namespace shape for Process.run
        e.Add(Type("ProcessOutput")); // record returned by Process.run
        e.Add(Type("MapEntry"));  // record returned by Map.entries
        e.Add(Type("ListPartition")); // record returned by List.partition
        e.Add(Type("Pair")); // universal 2-tuple container; used by List.zip / List.unzip
        e.Add(Type("Bytes")); // immutable byte sequence; used by File.read_bytes / write_bytes
        e.Add(Type("LogLevel")); // severity level on TraceEvent
        e.Add(Type("Log")); // namespace shape for Log.debug / info / warn / error

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

        // print(s: String) !{io} -> Result<Unit, IoError>
        // No-trailing-newline sibling of println.
        e.Add(Fn("print",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // eprint(s: String) !{io} -> Result<Unit, IoError>
        e.Add(Fn("eprint",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // read_line() !{io} -> Result<Option<String>, IoError>
        // Some(line) for one line read, None at EOF; trailing newline stripped.
        e.Add(Fn("read_line",
            typeParams: Array.Empty<string>(),
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("Result", Generic("Option", PrimitiveType.String), Named("IoError")),
            effects: new[] { "io" }));

        // read_to_end() !{io} -> Result<String, IoError>
        // Consume all of stdin as a single string. Pipe-consumer pattern.
        e.Add(Fn("read_to_end",
            typeParams: Array.Empty<string>(),
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("Result", PrimitiveType.String, Named("IoError")),
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

        // ---- List foundational ops ---------------------------------------------

        // List.concat<T>(left: List<T>, right: List<T>) -> List<T>
        e.Add(Fn("List.concat",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                Generic("List", TV("T")),
            },
            ret: Generic("List", TV("T"))));

        // List.head<T>(list: List<T>) -> Option<T>
        // None for empty; Some of first element otherwise.
        e.Add(Fn("List.head",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")) },
            ret: Generic("Option", TV("T"))));

        // List.tail<T>(list: List<T>) -> List<T>
        // Empty input yields empty (no panic).
        e.Add(Fn("List.tail",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")) },
            ret: Generic("List", TV("T"))));

        // List.take<T>(list: List<T>, n: Int) -> List<T>
        // Negative n yields empty; n >= length yields the whole list.
        e.Add(Fn("List.take",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")), PrimitiveType.Int },
            ret: Generic("List", TV("T"))));

        // List.drop<T>(list: List<T>, n: Int) -> List<T>
        // Symmetric recovery: negative n yields the whole list, n >= length yields empty.
        e.Add(Fn("List.drop",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")), PrimitiveType.Int },
            ret: Generic("List", TV("T"))));

        // List.reverse<T>(list: List<T>) -> List<T>
        e.Add(Fn("List.reverse",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")) },
            ret: Generic("List", TV("T"))));

        // List.find<T>(list: List<T>, predicate: fn(T) -> Bool) -> Option<T>
        e.Add(Fn("List.find",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                FnType(new TypeRef[] { TV("T") }, PrimitiveType.Bool),
            },
            ret: Generic("Option", TV("T"))));

        // List.find_index<T>(list: List<T>, predicate: fn(T) -> Bool) -> Option<Int>
        e.Add(Fn("List.find_index",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                FnType(new TypeRef[] { TV("T") }, PrimitiveType.Bool),
            },
            ret: Generic("Option", PrimitiveType.Int)));

        // List.contains<T>(list: List<T>, value: T) -> Bool
        // Host-default equality (Go: == ; .NET: EqualityComparer<T>.Default).
        e.Add(Fn("List.contains",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("List", TV("T")), TV("T") },
            ret: PrimitiveType.Bool));

        // List.flat_map<T, U>(list: List<T>, f: fn(T) -> List<U>) -> List<U>
        e.Add(Fn("List.flat_map",
            typeParams: new[] { "T", "U" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                FnType(new TypeRef[] { TV("T") }, Generic("List", TV("U"))),
            },
            ret: Generic("List", TV("U"))));

        // List.partition<T>(list: List<T>, predicate: fn(T) -> Bool) -> ListPartition<T>
        // Two-bucket split — see ListPartition record.
        e.Add(Fn("List.partition",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                FnType(new TypeRef[] { TV("T") }, PrimitiveType.Bool),
            },
            ret: Generic("ListPartition", TV("T"))));

        // List.zip<T, U>(left: List<T>, right: List<U>) -> List<Pair<T, U>>
        // Truncates to the shorter list — Haskell / Rust convention.
        e.Add(Fn("List.zip",
            typeParams: new[] { "T", "U" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                Generic("List", TV("U")),
            },
            ret: Generic("List", Generic("Pair", TV("T"), TV("U")))));

        // List.unzip<T, U>(pairs: List<Pair<T, U>>) -> Pair<List<T>, List<U>>
        // Inverse of zip; returns parallel lefts and rights as a Pair.
        e.Add(Fn("List.unzip",
            typeParams: new[] { "T", "U" },
            parameters: new TypeRef[]
            {
                Generic("List", Generic("Pair", TV("T"), TV("U"))),
            },
            ret: Generic("Pair", Generic("List", TV("T")), Generic("List", TV("U")))));

        // List.flatten<T>(lists: List<List<T>>) -> List<T>
        // Concatenates inner lists in order.
        e.Add(Fn("List.flatten",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", Generic("List", TV("T"))),
            },
            ret: Generic("List", TV("T"))));

        // List.sort_by<T>(list: List<T>, cmp: fn(T, T) -> Int) -> List<T>
        // Stable sort by comparator. cmp returns negative / zero / positive
        // (libc qsort convention); ties retain input order. The plain
        // sort()-without-comparator is gated on a generic-ordering primitive
        // the language doesn't have yet.
        e.Add(Fn("List.sort_by",
            typeParams: new[] { "T" },
            parameters: new TypeRef[]
            {
                Generic("List", TV("T")),
                FnType(new TypeRef[] { TV("T"), TV("T") }, PrimitiveType.Int),
            },
            ret: Generic("List", TV("T"))));

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

        // String.parse_int(s: String) -> Result<Int, IoError>
        // Decimal integer parse with invariant culture. Returns Err with
        // a narrative `could not parse '<s>' as Int` on rejection;
        // typical pairing with refinement try_from for CLI arg / config
        // validation: `parse_int(raw) |>? Port.try_from`.
        e.Add(Fn("String.parse_int",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Int, Named("IoError"))));

        // String.parse_float(s: String) -> Result<Float, IoError>
        // Float-shaped sibling of parse_int. Same invariant-culture
        // posture; same Err narrative shape.
        e.Add(Fn("String.parse_float",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Float, Named("IoError"))));

        // String.trim(s: String) -> String
        // Removes leading and trailing whitespace per Unicode rules.
        e.Add(Fn("String.trim",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.String));

        // String.to_upper / to_lower (s: String) -> String
        // Invariant-culture case conversion.
        e.Add(Fn("String.to_upper",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.String));
        e.Add(Fn("String.to_lower",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.String));

        // String.replace(s: String, from: String, to: String) -> String
        // Empty `from` is a programmer error and panics.
        e.Add(Fn("String.replace",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String, PrimitiveType.String },
            ret: PrimitiveType.String));

        // String.substring(s: String, start: Int, end: Int) -> String
        // Half-open [start, end). Out-of-range or inverted indices panic.
        e.Add(Fn("String.substring",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.Int, PrimitiveType.Int },
            ret: PrimitiveType.String));

        // String.index_of(s: String, needle: String) -> Option<Int>
        // None when absent; empty needle is Some(0).
        e.Add(Fn("String.index_of",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: Generic("Option", PrimitiveType.Int)));

        // String.repeat(s: String, n: Int) -> String
        // n=0 yields ""; negative n panics.
        e.Add(Fn("String.repeat",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.Int },
            ret: PrimitiveType.String));

        // ---- File I/O -----------------------------------------------------------
        // All operations that touch the filesystem carry !{io}; pure
        // path-string helpers on `Path` don't.

        // File.read_to_string(path: String) !{io} -> Result<String, IoError>
        // Read whole file as UTF-8. Errors (not found, permission, encoding)
        // surface as Err with the host's exception message in the narrative.
        e.Add(Fn("File.read_to_string",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.String, Named("IoError")),
            effects: new[] { "io" }));

        // File.write_all_text(path: String, contents: String) !{io} -> Result<(), IoError>
        // Overwrite or create the target file with the given contents,
        // UTF-8 encoded.
        e.Add(Fn("File.write_all_text",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // File.exists(path: String) !{io} -> Bool
        // True iff the path names an existing file (not a directory). The
        // !{io} effect is for observability — a caller's effect row has to
        // declare it watches process state.
        e.Add(Fn("File.exists",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Bool,
            effects: new[] { "io" }));

        // File.read_lines(path: String) !{io} -> Result<List<String>, IoError>
        e.Add(Fn("File.read_lines",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", Generic("List", PrimitiveType.String), Named("IoError")),
            effects: new[] { "io" }));

        // File.append_text(path: String, contents: String) !{io} -> Result<(), IoError>
        e.Add(Fn("File.append_text",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // File.delete(path: String) !{io} -> Result<(), IoError>
        // No-op on missing files (matches .NET / `rm -f` semantics).
        e.Add(Fn("File.delete",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // File.size(path: String) !{io} -> Result<Int, IoError>
        // Files larger than ~2 GB return Err; programs needing them FFI.
        e.Add(Fn("File.size",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Int, Named("IoError")),
            effects: new[] { "io" }));

        // File.move(from: String, to: String) !{io} -> Result<(), IoError>
        // Atomic-where-supported rename.
        e.Add(Fn("File.move",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // File.copy(from: String, to: String) !{io} -> Result<(), IoError>
        // Overwrites existing destination.
        e.Add(Fn("File.copy",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // File.read_bytes(path: String) !{io} -> Result<Bytes, IoError>
        e.Add(Fn("File.read_bytes",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", Named("Bytes"), Named("IoError")),
            effects: new[] { "io" }));

        // File.write_bytes(path: String, data: Bytes) !{io} -> Result<(), IoError>
        e.Add(Fn("File.write_bytes",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, Named("Bytes") },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // ---- Bytes -------------------------------------------------------------
        // Immutable byte sequence; foundational binary-data type.

        e.Add(Fn("Bytes.empty",
            typeParams: Array.Empty<string>(),
            parameters: Array.Empty<TypeRef>(),
            ret: Named("Bytes")));

        e.Add(Fn("Bytes.from_list",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { Generic("List", PrimitiveType.Int) },
            ret: Named("Bytes")));

        e.Add(Fn("Bytes.size",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { Named("Bytes") },
            ret: PrimitiveType.Int));

        e.Add(Fn("Bytes.at",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { Named("Bytes"), PrimitiveType.Int },
            ret: PrimitiveType.Int));

        e.Add(Fn("Bytes.slice",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { Named("Bytes"), PrimitiveType.Int, PrimitiveType.Int },
            ret: Named("Bytes")));

        e.Add(Fn("Bytes.concat",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { Named("Bytes"), Named("Bytes") },
            ret: Named("Bytes")));

        e.Add(Fn("Bytes.from_utf8",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Named("Bytes")));

        e.Add(Fn("Bytes.to_utf8",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { Named("Bytes") },
            ret: Generic("Result", PrimitiveType.String, Named("IoError"))));

        // ---- Directory operations ----------------------------------------------

        // Directory.exists(path: String) !{io} -> Bool
        // Distinct from File.exists; this returns true only for directories.
        e.Add(Fn("Directory.exists",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Bool,
            effects: new[] { "io" }));

        // Directory.create(path: String) !{io} -> Result<(), IoError>
        // Creates intermediate parents. No-op if directory already exists.
        e.Add(Fn("Directory.create",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // Directory.list(path: String) !{io} -> Result<List<String>, IoError>
        // Returns entry names (not full paths). Order is filesystem-defined.
        e.Add(Fn("Directory.list",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Result", Generic("List", PrimitiveType.String), Named("IoError")),
            effects: new[] { "io" }));

        // Directory.delete(path: String, recursive: Bool) !{io} -> Result<(), IoError>
        // recursive=true removes all contents (rm -r); false requires empty dir.
        e.Add(Fn("Directory.delete",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.Bool },
            ret: Generic("Result", PrimitiveType.Unit, Named("IoError")),
            effects: new[] { "io" }));

        // ---- Pure path-string helpers (no effect row) --------------------------

        // Path.join(parent: String, child: String) -> String
        e.Add(Fn("Path.join",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: PrimitiveType.String));

        // Path.parent(path: String) -> Option<String>
        // None when the path has no parent component (bare filename or empty).
        e.Add(Fn("Path.parent",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Option", PrimitiveType.String)));

        // Path.file_name(path: String) -> Option<String>
        // None for the empty string; otherwise the segment after the last separator.
        e.Add(Fn("Path.file_name",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Option", PrimitiveType.String)));

        // Path.extension(path: String) -> Option<String>
        // Includes the leading dot (`.ov`); None when there's no extension.
        e.Add(Fn("Path.extension",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: Generic("Option", PrimitiveType.String)));

        // Path.with_extension(path: String, ext: String) -> String
        // Replace (or add) the extension. `ext` may include or omit the
        // leading dot; both forms produce the same result.
        e.Add(Fn("Path.with_extension",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String, PrimitiveType.String },
            ret: PrimitiveType.String));

        // Path.is_absolute(path: String) -> Bool
        e.Add(Fn("Path.is_absolute",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Bool));

        // ---- Process orchestration ---------------------------------------------

        // Process.run(cmd: String, args: List<String>) !{io} -> Result<ProcessOutput, IoError>
        // Synchronous, blocks until the process completes. Captures stdout
        // and stderr in full. A non-zero exit is Ok with output.exit_code
        // != 0; only launch failures (binary not found, etc.) surface as
        // Err. Call sites destructure via field access on ProcessOutput.
        e.Add(Fn("Process.run",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[]
            {
                PrimitiveType.String,
                Generic("List", PrimitiveType.String),
            },
            ret: Generic("Result", Named("ProcessOutput"), Named("IoError")),
            effects: new[] { "io" }));

        // ---- Map<K, V> -----------------------------------------------------------
        // Immutable key-value map. Foundational. Mutating operations always
        // allocate; the host's default equality is used on keys.

        e.Add(Fn("Map.empty",
            typeParams: new[] { "K", "V" },
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("Map", TV("K"), TV("V"))));

        e.Add(Fn("Map.get",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")), TV("K") },
            ret: Generic("Option", TV("V"))));

        e.Add(Fn("Map.contains_key",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")), TV("K") },
            ret: PrimitiveType.Bool));

        e.Add(Fn("Map.insert",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")), TV("K"), TV("V") },
            ret: Generic("Map", TV("K"), TV("V"))));

        e.Add(Fn("Map.remove",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")), TV("K") },
            ret: Generic("Map", TV("K"), TV("V"))));

        e.Add(Fn("Map.size",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")) },
            ret: PrimitiveType.Int));

        e.Add(Fn("Map.keys",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")) },
            ret: Generic("List", TV("K"))));

        e.Add(Fn("Map.values",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")) },
            ret: Generic("List", TV("V"))));

        // Map.entries returns List<MapEntry<K, V>>. Tuple-shaped type
        // annotations aren't yet expressible in Overt source (no
        // TupleType AST node); a named-field record sidesteps the gap
        // and reads more naturally as `entry.key` / `entry.value`.
        e.Add(Fn("Map.entries",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[] { Generic("Map", TV("K"), TV("V")) },
            ret: Generic("List", Generic("MapEntry", TV("K"), TV("V")))));

        // Map.merge: right wins on key collision (last-writer-wins).
        e.Add(Fn("Map.merge",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[]
            {
                Generic("Map", TV("K"), TV("V")),
                Generic("Map", TV("K"), TV("V")),
            },
            ret: Generic("Map", TV("K"), TV("V"))));

        e.Add(Fn("Map.map",
            typeParams: new[] { "K", "V", "W" },
            parameters: new TypeRef[]
            {
                Generic("Map", TV("K"), TV("V")),
                FnType(new TypeRef[] { TV("V") }, TV("W")),
            },
            ret: Generic("Map", TV("K"), TV("W"))));

        e.Add(Fn("Map.filter",
            typeParams: new[] { "K", "V" },
            parameters: new TypeRef[]
            {
                Generic("Map", TV("K"), TV("V")),
                FnType(new TypeRef[] { TV("K"), TV("V") }, PrimitiveType.Bool),
            },
            ret: Generic("Map", TV("K"), TV("V"))));

        // ---- Set<T> --------------------------------------------------------------
        // Immutable membership. Same shape philosophy as Map.

        e.Add(Fn("Set.empty",
            typeParams: new[] { "T" },
            parameters: Array.Empty<TypeRef>(),
            ret: Generic("Set", TV("T"))));

        e.Add(Fn("Set.contains",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")), TV("T") },
            ret: PrimitiveType.Bool));

        e.Add(Fn("Set.insert",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")), TV("T") },
            ret: Generic("Set", TV("T"))));

        e.Add(Fn("Set.remove",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")), TV("T") },
            ret: Generic("Set", TV("T"))));

        e.Add(Fn("Set.size",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")) },
            ret: PrimitiveType.Int));

        e.Add(Fn("Set.union",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")), Generic("Set", TV("T")) },
            ret: Generic("Set", TV("T"))));

        e.Add(Fn("Set.intersect",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")), Generic("Set", TV("T")) },
            ret: Generic("Set", TV("T"))));

        e.Add(Fn("Set.difference",
            typeParams: new[] { "T" },
            parameters: new TypeRef[] { Generic("Set", TV("T")), Generic("Set", TV("T")) },
            ret: Generic("Set", TV("T"))));

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

        // ---- Log: leveled logging via the Trace channel ------------------------
        // Log.{debug, info, warn, error}(message: String) !{io} -> ()
        // emit a TraceEvent { level, message } into the Trace channel.
        // Default consumer (installed lazily on first Log call) writes
        // [LEVEL] message to stderr; user-registered subscribers run
        // alongside it.

        e.Add(Fn("Log.debug",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Unit,
            effects: new[] { "io" }));
        e.Add(Fn("Log.info",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Unit,
            effects: new[] { "io" }));
        e.Add(Fn("Log.warn",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
            ret: PrimitiveType.Unit,
            effects: new[] { "io" }));
        e.Add(Fn("Log.error",
            typeParams: Array.Empty<string>(),
            parameters: new TypeRef[] { PrimitiveType.String },
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
            (effects ?? Array.Empty<string>()).ToImmutableArray(),
            typeParams.ToImmutableArray());
        return (symbol, type);
    }

    private static TypeVarRef TV(string name) => new(name);

    private static NamedTypeRef Named(string name) => new(name);

    private static NamedTypeRef Generic(string name, params TypeRef[] args)
        => new(name, args.ToImmutableArray());

    /// <summary>Construct a FunctionTypeRef for a fn-typed parameter (no
    /// effect row — the host-side runtime doesn't track effects of
    /// callbacks; the user-side declares the row on the surrounding fn
    /// and the type checker propagates).</summary>
    private static FunctionTypeRef FnType(TypeRef[] parameters, TypeRef ret)
        => new(parameters.ToImmutableArray(), ret, ImmutableArray<string>.Empty);
}
