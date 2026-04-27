# logtally

A small CLI that reads a log file (or stdin), parses each line for a
bracketed level tag like `[INFO] message`, and prints a histogram of
levels seen. Lines that don't match the format are counted as
"unparseable" without aborting.

The shape it expects is exactly what the Overt runtime's default `Log`
consumer emits — `[DEBUG]`, `[INFO]`, `[WARN]`, `[ERROR]` lines (and
`[TRACE]` for programs that extend with their own enum, like the
valconf sample). Read its own output back, in other words.

## What this sample shows

A pure-Overt CLI: argv parsing, file I/O *and* stdin, parsing,
aggregation, sorted output, and split stdout/stderr — all written
in `.ov`, with no C# entry point. The other pure-Overt CLI samples
(`valconf`, `diffconf`, `envcheck`, `pingall`) take this same
shape; `samples/msbuild-smoke/` covers the C#-host-consumes-Overt
path separately.

Three patterns worth lifting:

1. **`main(args: List<String>)` puts the argv dependency in the
   signature.** The runner passes the program-side argv slice in
   directly. The bare `args()` prelude returns the same slice (the
   runner stashes it on the runtime before invoking `main`), so
   parameter-less `main()` plus `args()` reads as well — both forms
   are valid; the parameter form makes the dependency visible at the
   signature.

2. **`Map<String, Int>` aggregation via `fold`.** The histogram is
   the running accumulator. Each `step` call returns a new `Tally`
   record with the right counter bumped — immutable update with
   `Map.insert` returning a new map.

3. **Sum-typed parse result with exhaustive matching.** `LineKind` is
   either `Tagged { level, message }` or `Malformed { raw }`. Every
   `match LineKind` site has to handle both arms; if a future change
   adds a new variant, the compiler refuses to build until every
   consumer is updated.

## Running

This is a script-style sample — `overt run` transpiles, compiles, and
executes in one pass; no `dotnet build` step needed.

```sh
# Argv: read the named file
overt run logtally.ov logs/busy.log
# DEBUG: 2
# INFO: 12
# WARN: 3
# ERROR: 2
# ---
# total: 19

# No argv: read stdin
cat logs/busy.log | overt run logtally.ov

# Malformed lines are counted on stderr without aborting
overt run logtally.ov logs/malformed.log
# INFO: 3
# WARN: 1
# debug: 1
# ---
# total: 7
# warning: 2 unparseable line(s) skipped
```

The histogram goes to stdout in level rank order; the malformed-line
warning goes to stderr so a downstream tool consuming the histogram
isn't polluted. Exit code is 0 unless I/O fails.

## Why the `[debug]` row is separate

`logs/malformed.log` includes `[debug]` (lowercase). The parser
treats it as its own bucket, distinct from `DEBUG`. That's
deliberate: case mismatches usually indicate a misconfigured logger,
and silently merging them would hide the misconfiguration. Unknown
levels sort last in the output (rank 99), so they stand out
visually.

## What the runtime emits

The Overt runtime's `Log` namespace formats events as `[LEVEL]
message` to stderr by default. So if a real Overt program is
emitting log events, redirecting stderr to a file gives input
`logtally` consumes directly:

```sh
my-overt-program 2> run.log
overt run logtally.ov run.log
```

That's the full loop: language emits structured logs → CLI
aggregates them → operator reads the histogram.
