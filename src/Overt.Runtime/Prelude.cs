// The runtime Prelude for transpiled Overt programs.
//
// Emitted C# references this via `using static Overt.Runtime.Prelude;`. The
// `Prelude` static class collects everything the compiler wants in scope
// for every compiled program: Ok / Err / Some / None factories, println /
// print / read_line, args, the unqualified collection operations
// (map / filter / fold / par_map / try_map / all / any / size / length),
// and the Trace channel.
//
// Other runtime types (Unit, Result, Option, IoError, List, String, Map,
// Set, Bytes, File, Directory, Path, Process, LogLevel, TraceEvent, Log,
// etc.) live in sibling files in this directory and are referenced by
// fully-qualified namespace name from emitted code. They're split per-
// namespace so the file structure mirrors the stdlib's logical layout.

namespace Overt.Runtime;

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

    // Override channel for the process command-line args. The `overt run`
    // CLI sets this to the user's program-side argv slice before
    // invoking `main`, because the in-process Roslyn-eval'd program
    // shares its host's argv with the runner — `Environment.GetCommandLineArgs()`
    // would return `["overt", "run", "<file.ov>", ...]` rather than just
    // the program's args. When null (the standalone-exe path), `args()`
    // falls back to the OS argv.
    private static System.Collections.Immutable.ImmutableArray<string>? _programArgsOverride;

    /// <summary>
    /// Set the program-args override before invoking <c>main</c>. The
    /// override survives only the current process; clear by passing
    /// <c>null</c>.
    /// </summary>
    public static void _setProgramArgs(System.Collections.Immutable.ImmutableArray<string>? args)
    {
        _programArgsOverride = args;
    }

    // The process command-line arguments, minus the executable path that
    // .NET puts at index 0. Mirrors the contract of `static int Main(
    // string[] args)`. Returned as an Overt List<String>; callers use
    // size(), List.at(), etc.
    //
    // When the program runs under `overt run`, the runner stashes the
    // program-args slice via _setProgramArgs and `args()` returns that.
    // For a standalone-exe deployment (no override set), it falls back
    // to the OS argv.
    public static List<string> args()
    {
        if (_programArgsOverride is { } overridden)
        {
            return new List<string>(overridden);
        }
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
    // dispatches synchronously in registration order. Events carry a level
    // and a message; richer causal-chain wiring (per DESIGN.md §14) layers
    // on top of this base in a follow-up.
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
