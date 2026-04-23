# Overt

An **agent-first programming language** — written, read, and maintained primarily by LLM agents, with humans in a review and audit role. Transpiles to readable source in host languages (C# primary, Go secondary).

The name is the design philosophy: every effect, error, dispatch, mutation, and piece of state is *overt* — visible at the call or declaration site, never concealed from the reader.

> **Status (April 2026):** working end-to-end on C#. Every program in [`examples/`](examples/) — 12 so far — lexes, parses, name-resolves, type-checks, emits C# source, and compiles cleanly against the runtime via Roslyn. A transpiled [`examples/hello.ov`](examples/hello.ov) actually runs and prints "Hello, LLM!" — verified by an end-to-end test. The type checker annotates but does not yet reject; diagnostics, effect checking, and refinement enforcement are the next arc. Go backend is scaffolded but not yet emitting. No stdlib beyond a minimal prelude.

---

## Why another language?

Every existing programming language is designed for humans. Short names, implicit effects, positional arguments, exceptions that unwind invisibly, and reflection are all accommodations for *human* cognitive limits — small working memory, strong pattern-matching, strong causal intuition.

LLMs have the inverse profile: **large context, weak causal tracking across calls**. A language optimized for agent authorship should invert the usual tradeoffs — trade brevity for signatures that explain themselves, trade inference for types restated at use sites, trade "idiomatic" for one canonical form.

The target is **optimized for the agent, tolerable for the auditor**. A different point on the curve than any existing language.

For the full argument, see [`DESIGN.md`](DESIGN.md) §1–§2.

---

## What it looks like

```overt
module hello

fn main() !{io} -> Result<(), IoError> {
    println("Hello, LLM!")?
    Ok(())
}
```

Key shape rules on display, even in six lines:

- **`!{io}`** — the effect row on the signature. `main` performs I/O; the caller sees that without reading the body.
- **`-> Result<(), IoError>`** — errors are values. No exceptions.
- **`println("...")?`** — the `?` operator propagates failure explicitly. No hidden unwinding.
- **`Ok(())`** — success is constructed, not implicit.

More examples under [`examples/`](examples/): task groups (`parallel`), fallback (`race`), immutable records with `let mut` rebinding and `with` for modified copies, pipe composition (`|>` / `|>?`), exhaustive pattern matching, refinement types, first-class causal traces, and FFI to C#, Go, and C.

---

## Design highlights

A few of the decisions that define the language. Full rationale in [`DESIGN.md`](DESIGN.md).

- **Static, non-nullable types, no reflection, no user-defined macros.** Predictability over cleverness.
- **Errors as values with `Result<T, E>` and `?` propagation** (§11). Exceptions convert only at FFI boundaries.
- **Effect rows declared on every function**, row-polymorphic via effect-row type variables (§7). Core effects: `io`, `async`, `inference`, `fails`.
- **Immutable records.** `let mut` rebinds local names; `with` produces modified copies (§10). No shared mutable state.
- **No method-call syntax.** Pipes (`|>`, `|>?`) for composition; bare calls otherwise (§7). Dots mean field access or module-qualified call, nothing else.
- **No literal integer indexing at source level** (§13). Zero-cost iteration or proven-index as the numeric-kernel escape hatch.
- **Transpile to source, not IR.** C# via Roslyn (primary); Go as conformance target (§18, §20). LLVM explicitly rejected for v1.
- **One canonical form**, enforced by the formatter. No per-project or per-developer style config (§4, §21).
- **Defined behavior, no UB** (§8). Integer overflow traps by default. Every classical UB source from C/C++ is designed out structurally.
- **Runtime errors point at Overt source.** The C# emitter writes `#line` directives so exceptions, debuggers, and stack traces resolve to the original `.ov` file, not the generated `.cs`. Editing the generated code is structurally discouraged — see §18's debug-mapping subsection.

---

## Repository layout

```
DESIGN.md                           Primary design document (source of truth, ~1100 lines)
CARRYOVER.md                        Session handoff for the next working session
docs/
  grammar/
    lexical.md                      Authoritative lexical grammar (tokens, mode automaton)
    precedence.md                   Operator precedence and associativity
  tooling/
    godbolt.md                      Compiler Explorer integration plan
examples/                           Example programs — living test cases for the design
vscode-extension/                   TextMate grammar + language config for .ov files
tooling/
  godbolt/                          Scaffolding for the Compiler Explorer submission
src/
  Overt.Compiler/
    Syntax/                         Lexer, Parser, AST, Tokens
    Semantics/                      Name resolver, type checker, stdlib declarations
    Diagnostics/                    Diagnostic and DiagnosticNote model
  Overt.Backend.CSharp/             Roslyn-facing C# transpiler
  Overt.Backend.Go/                 Go backend (scaffold; no emission yet)
  Overt.Cli/                        `overt` command-line driver
  Overt.Runtime/                    Runtime prelude: Unit, Result, Option, stdlib stubs
tests/
  Overt.Tests/                      xUnit suite
  Overt.EndToEnd/                   Harness that runs transpiled hello.ov
```

---

## Building and running

Requires the .NET 9 SDK.

```
dotnet build
dotnet test
```

Tests are comprehensive: lexer token-stream goldens, parser AST shape assertions, name-resolver scoping tests, type-checker annotation tests, C# emitter shape tests, Roslyn-based compile-check for every example, a `#line`-mapping verification, and an end-to-end run of `hello.ov` through the full pipeline.

### The compiler CLI

```
overt --emit=<stage> <file.ov>
```

Stages, each writing to stdout, diagnostics to stderr:

- `--emit=tokens` — the lexer's token stream, one per line
- `--emit=ast` — the parsed AST as a readable tree
- `--emit=resolved` — identifier → symbol resolutions
- `--emit=typed` — declaration and expression types
- `--emit=csharp` — transpiled C# source (compiles against [`Overt.Runtime`](src/Overt.Runtime))
- `--emit=go` — not yet implemented

Diagnostics follow the conventional `path:line:col: severity: CODE: message` format with optional `help:` and `note:` follow-up lines. Codes are stable: `OV00xx` for lexer, `OV01xx` for parser, `OV02xx` for resolver, and so on.

### Running transpiled programs

```
overt --emit=csharp examples/hello.ov > tests/Overt.EndToEnd/Generated.cs
dotnet run --project tests/Overt.EndToEnd
# -> Hello, LLM!
```

The end-to-end test automates this and asserts the printed output.

---

## Pipeline

The compiler pipeline, with the test coverage that pins each stage:

1. **Lex** (`Syntax/Lexer.cs`) — mode-stack lexer per [`docs/grammar/lexical.md`](docs/grammar/lexical.md). Token streams for every example are locked in golden files under [`tests/Overt.Tests/fixtures/golden/`](tests/Overt.Tests/fixtures/golden/).
2. **Parse** (`Syntax/Parser.cs`) — recursive-descent, precedence per [`docs/grammar/precedence.md`](docs/grammar/precedence.md). All 12 examples produce zero parser diagnostics.
3. **Name-resolve** (`Semantics/NameResolver.cs`) — builds a symbol table, resolves identifier references, enforces `DESIGN.md §3`'s no-shadowing rule. Prelude symbols ([`Semantics/Stdlib.cs`](src/Overt.Compiler/Semantics/Stdlib.cs)) are ambient and shadowable. Did-you-mean suggestions via Levenshtein.
4. **Type-check** (`Semantics/TypeChecker.cs`) — lowers the AST into a `TypeRef` IR and annotates every expression. Does not yet reject programs; annotations feed later passes.
5. **Emit C#** (`Overt.Backend.CSharp/CSharpEmitter.cs`) — walks the AST with the type-checker's annotations and emits C# source text. Expected-type threading propagates target types into generic calls so constructs like `List.empty()`, `Ok(x)`, and variant-pattern matches lower without a full inference pass. `#line` directives map every statement and declaration back to the `.ov` source so PDBs point runtime errors at Overt.
6. **Compile C#** (Roslyn) — verified by the test suite for every example.

---

## How Overt gets built

From [`DESIGN.md §20`](DESIGN.md):

1. **Semantics spec before either backend exists**, runtime-neutrally.
2. **Single canonical test suite from day one.**
3. **C# emission primary** (Roslyn, emitting C# source text — not IL).
4. **Go emission as conformance target**, in CI only, not a parallel implementation.
5. **Frontend is ~80% of the work.** Backends are comparatively cheap once the IR is right.

The compiler host language is **C#**, chosen for iteration speed given the primary author's background and the fact that the C# backend depends on Roslyn APIs anyway. See [`DESIGN.md §20`](DESIGN.md).

---

## Contributing

Overt is an early-stage solo project. Issues and discussion are welcome, but external PRs are unlikely to be merged until the language stabilizes enough that the bar for changes is clearer than it is today. The living design document is the place to propose a direction; [`examples/`](examples/) is the place to stress-test it.

Locked v1 decisions are enumerated in [`CARRYOVER.md`](CARRYOVER.md). Re-opening them requires new evidence, not new preferences.

---

## License

Apache License 2.0. See [`LICENSE`](LICENSE).
