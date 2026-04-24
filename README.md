<img src="assets/artwork/mascot.png" alt="Overt mascot" align="right" width="180">

# Overt

[![CI](https://img.shields.io/github/actions/workflow/status/paulmooreparks/Overt/ci.yml?branch=main&label=CI&logo=github)](https://github.com/paulmooreparks/Overt/actions/workflows/ci.yml)
[![Overt on NuGet](https://img.shields.io/nuget/vpre/Overt?label=Overt&logo=nuget)](https://www.nuget.org/packages/Overt)
[![Overt.Build on NuGet](https://img.shields.io/nuget/vpre/Overt.Build?label=Overt.Build&logo=nuget)](https://www.nuget.org/packages/Overt.Build)
[![License](https://img.shields.io/github/license/paulmooreparks/Overt)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-512bd4?logo=dotnet)](https://dotnet.microsoft.com/)

<img src="https://raw.githubusercontent.com/paulmooreparks/Overt/main/assets/artwork/mascot.png" align="right" width="200" alt="Overt mascot">

An **agent-first programming language**: written, read, and maintained primarily by LLM agents, with humans in a review and audit role. Transpiles to readable source in host languages (C# primary, Go secondary).

The name is the design philosophy: every effect, error, dispatch, mutation, and piece of state is *overt*, visible at the call or declaration site, never concealed from the reader.

---

## Why agent-first?

Every existing programming language is designed for humans. Short names, implicit effects, positional arguments, exceptions that unwind invisibly, and reflection are all accommodations for *human* cognitive limits: small working memory, strong pattern-matching, and strong causal intuition.

LLMs have the inverse profile: **large context, weak causal tracking across calls**. A language optimized for agent authorship should invert the usual tradeoffs: trade brevity for signatures that explain themselves, trade inference for types restated at use sites, trade "idiomatic" for one canonical form.

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

- **`!{io}`.** The effect row on the signature. `main` performs I/O; the caller sees that without reading the body.
- **`-> Result<(), IoError>`.** Errors are values. No exceptions.
- **`println("...")?`.** The `?` operator propagates failure explicitly. No hidden unwinding.
- **`Ok(())`.** Success is constructed, not implicit.

More examples under [`examples/`](examples/): task groups (`parallel`), fallback (`race`), immutable records with `let mut` rebinding and `with` for modified copies, pipe composition (`|>` / `|>?`), exhaustive pattern matching, refinement types, first-class causal traces, async I/O with `.await`, typed JSON roundtrip, and FFI to C#, Go, and C.

---

## Quick try

Requires the .NET 9 SDK. Today, from a clone of this repo:

```
git clone https://github.com/paulmooreparks/Overt
cd Overt
dotnet run --project src/Overt.Cli -- run examples/hello.ov
```

That transpiles, compiles, and executes `hello.ov` in one pass, printing `Hello, LLM!`.

A .NET global tool (`dotnet tool install --global Overt`) is packaged and tested but not yet published to nuget.org; see [`ROLLOUT.md`](ROLLOUT.md) for when that ships.

Using Overt from an existing C# project is a `<PackageReference>` away; see [AGENTS.md §11](AGENTS.md#11-building-with-msbuild-c-back-end) and the working sample at [`samples/msbuild-smoke/`](samples/msbuild-smoke/).

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
- **Runtime errors point at Overt source.** The C# emitter writes `#line` directives so exceptions, debuggers, and stack traces resolve to the original `.ov` file, not the generated `.cs`. Editing the generated code is structurally discouraged; see §18's debug-mapping subsection.
- **Explicit async.** `Task<T>`-returning externs bind directly; postfix `.await` extracts the value, mirroring `?`. Fns that await emit as `async Task<T>` in C#; callers see `Task<T>` and unwrap at the site. The `async` effect in the row is the declaration; `.await` is the line-level marker.
- **MSBuild integration.** `.ov` files compile alongside `.cs` in any csproj via a `<PackageReference>` to `Overt.Build`, with no manual transpile step. Compile-time diagnostics surface in the IDE's error list like normal Csc errors.

---

## Architecture

Two-tier split: language-level work is shared across all back ends; anything that touches host artifacts is per-back-end.

```mermaid
flowchart TB
    src["<b>Overt source</b> (.ov)"]

    subgraph tier1 ["<code>Overt.Compiler</code>"]
        direction TB
        pipe["Lex · Parse · Resolve · Type-check"]
        shared["Formatter · Module graph<br/>Diagnostics · LSP protocol<br/>--emit=tokens/ast/resolved/typed"]
    end

    subgraph tier2 ["<code>Overt.Backend.*</code>"]
        direction LR
        cs["<b>C#</b> (today)<br/>Emitter · Runtime<br/>BindGen · Runner<br/>#line + PDB · NuGet"]
        go["<b>Go</b> (scaffold)"]
        future["<b>Future: TypeScript, Rust, C++, etc.</b>"]
    end

    src --> tier1
    tier1 -->|AST| tier2
```

See [`DESIGN.md`](DESIGN.md) §19 (stdlib is per-back-end, not portable) and §20 (tooling-tier split) for rationale.

## Repository layout

```
DESIGN.md                           Primary design document (source of truth)
AGENTS.md                           Operational reference for agents writing Overt
CARRYOVER.md                        Session handoff: next-session queue and locked decisions
ROLLOUT.md                          Phased plan for taking Overt public
docs/
  grammar/                          Authoritative lexical + precedence grammars
examples/                           Example programs (living test cases)
samples/
  msbuild-smoke/                    C# project consuming .ov files via Overt.Build
stdlib/
  csharp/                           Blessed BCL facades (auto-discovered by CLI)
    system/                           Mirrors .NET's System.* namespace structure
tooling/
  install.ps1                       Publish-and-install script for the `overt` CLI (dev workflow)
  ov.ps1                            Dev-mode wrapper that targets the Debug build dir
vscode-extension/                   TextMate grammar + language config for .ov files
src/
  Overt.Compiler/                   Tier 1: lexer, parser, resolver, type-checker, formatter
    Modules/                          Module-graph resolution for cross-file `use`
  Overt.Backend.CSharp/             Tier 2: C# emitter, BindGenerator, extern runtime wiring
  Overt.Backend.Go/                 Tier 2: Go back end (scaffold; no emission yet)
  Overt.Build/                      MSBuild integration: OvertTranspileTask + targets + NuGet packaging
  Overt.Cli/                        Thin dispatcher: `run`, `fmt`, `bind`, `--emit=<stage>`
  Overt.Runtime/                    Runtime prelude for transpiled programs (C# back end)
tests/
  Overt.Tests/                      xUnit suite (lexer goldens, emitter compile-checks, e2e tool install)
  Overt.EndToEnd/                   Roslyn compile + exec harness for hello.ov
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
overt run <file.ov>              transpile, compile in memory, execute
overt fmt [--write] <file.ov>    format to canonical form (idempotent)
overt bind --type <FullName>     generate an Overt facade for a .NET type
overt --emit=<stage> <file.ov>   dump a pipeline stage for inspection
```

Emit stages, each writing to stdout with diagnostics on stderr:

- `--emit=tokens`: the lexer's token stream, one per line
- `--emit=ast`: the parsed AST as a readable tree
- `--emit=resolved`: identifier → symbol resolutions
- `--emit=typed`: declaration and expression types
- `--emit=csharp`: transpiled C# source (compiles against [`Overt.Runtime`](src/Overt.Runtime))
- `--emit=go`: not yet implemented

All emit stages (plus `run`) walk the full module graph, so a file with `use` declarations compiles correctly even in stage-dump mode.

Diagnostics follow the `path:line:col: severity: CODE: message` format with `help:` follow-ups (actionable fix) and `note:` follow-ups (pointer into [`AGENTS.md`](AGENTS.md)). Codes are stable: `OV00xx` lex, `OV01xx` parse, `OV02xx` resolve, `OV03xx` type-check.

### Running transpiled programs

```
overt run examples/hello.ov
# -> Hello, LLM!
```

The end-to-end Roslyn compile + exec happens in-process; there is no intermediate file.

### Using blessed stdlib facades

The CLI auto-discovers [`stdlib/csharp/`](stdlib/csharp/) next to the compiler. Overt programs `use` the facades directly:

```overt
module app

use stdlib.csharp.system.environment as env
use stdlib.csharp.system.math as math

fn main() !{io} -> Result<(), IoError> {
    let cpus: Int = env.processor_count()?
    println("cpus=${cpus} sqrt9=${math.sqrt(d = 9.0)}")?
    Ok(())
}
```

To generate a new facade:

```
overt bind --type System.DateTime --module stdlib.csharp.system.datetime \
           --output stdlib/csharp/system/datetime.ov
```

### Installing on PATH

`tooling/install.ps1` publishes the CLI into `$HOME\bin` (or any `-Bin <path>`) and copies the blessed `stdlib/` alongside it. Re-run whenever you want the on-PATH copy to reflect new changes.

---

## Pipeline

The compiler pipeline, with the test coverage that pins each stage:

1. **Lex** (`Syntax/Lexer.cs`): mode-stack lexer per [`docs/grammar/lexical.md`](docs/grammar/lexical.md). Token streams for every example are locked in golden files under [`tests/Overt.Tests/fixtures/golden/`](tests/Overt.Tests/fixtures/golden/).
2. **Parse** (`Syntax/Parser.cs`): recursive-descent, precedence per [`docs/grammar/precedence.md`](docs/grammar/precedence.md). Every example parses clean.
3. **Name-resolve** (`Semantics/NameResolver.cs`): builds a symbol table, resolves identifier references (including module-qualified names like `List.empty` / `Trace.subscribe`), and enforces `DESIGN.md §3`'s no-shadowing rule. Prelude symbols ([`Semantics/Stdlib.cs`](src/Overt.Compiler/Semantics/Stdlib.cs)) are ambient and shadowable. Did-you-mean suggestions via Levenshtein.
4. **Type-check** (`Semantics/TypeChecker.cs`): lowers the AST into a `TypeRef` IR, annotates every expression, and *validates* contracts. Enforces argument / return / field / arm / condition / arity correctness (OV0300–0306), ignored `Result` (OV0307), match exhaustiveness on user enums, stdlib `Option` / `Result`, and tuples of enums (OV0308), effect-row coverage including higher-order propagation (OV0310), refinement-predicate violations at literal boundaries (OV0311), required `let` type annotations (OV0314), extern-kind shape (OV0315/16), and `.await` on a `Task<T>` (OV0317). Refinement predicates that are undecidable at compile time emit runtime checks at every boundary (call args, let initializers, record-field inits, return expressions).
5. **Emit C#** (`Overt.Backend.CSharp/CSharpEmitter.cs`): walks the annotated AST and emits C# source text. Expected-type threading propagates target types into generic calls so `List.empty()`, `Ok(x)`, and variant-pattern matches lower without a full inference pass. `#line` directives map every statement back to the `.ov` source; runtime errors resolve to Overt, not the generated C#.
6. **Compile C#** (Roslyn): verified by the test suite for every example.

---

## How Overt gets built

From [`DESIGN.md §20`](DESIGN.md):

1. **Back-end-independent front end.** Lex / parse / resolve / type-check / format / module graph / diagnostics live in `Overt.Compiler` and never learn which back end will consume the AST.
2. **Per-back-end everything else.** Each `Overt.Backend.<Host>` owns its emitter, runtime, binding generator (`overt bind`), runner (`overt run`), debug mapping, and package-ecosystem interop.
3. **C# emission primary** (Roslyn, emitting C# source text, not IL).
4. **Go emission as conformance target** to keep the split honest; CI only, not a parallel implementation effort.
5. **Portability, if ever needed, is its own back end.** A purpose-designed portable stdlib and emitter, opted into explicitly. See §19.

The compiler host language is **C#**, chosen for iteration speed given the primary author's background and the fact that the C# back end depends on Roslyn APIs anyway.

---

## Status

Working end-to-end on C#:

- **Language.** Records, enums (including struct-like variants), pattern matching with cartesian-product exhaustiveness on tuples of enums, effect rows, refinement types with runtime-checked boundaries, immutable records with `with`-updates, `let mut` rebinding, full imperative control flow (`for each`, `while`, `loop`, `break`, `continue`, literal patterns), `?` and `|>?` propagation (including inside nested `if`/`match` arms), `.await` on `Task<T>` with async-effect fns emitting as `async Task<T>`.
- **FFI.** `extern "csharp"` with three explicit kinds (static, `instance`, and `ctor`), plus generic methods via angle-bracket binds targets (`Deserialize<MyType>`). Go and C placeholders parse and diagnose clearly; only C# executes today.
- **Stdlib.** Facades under `stdlib/csharp/system.*` for path, file, console, environment, math, convert, DateTime, TimeSpan, Guid, StringBuilder, and Uri, generated from reflection via `overt bind`, auto-discovered by the CLI. JSON roundtrip via `JsonSerializer.Deserialize<T>` demonstrated in [`examples/json.ov`](examples/json.ov).
- **Tooling.** `overt run` (in-memory Roslyn compile + execute), `overt fmt` (canonical form, idempotent, comment-preserving), `overt bind` (reflection-based facade generation), `overt --emit=<stage>` (tokens, ast, resolved, typed, csharp). Compile-time diagnostics carry stable OV-codes plus `help:` fixes and `note: see AGENTS.md §N` pointers.
- **Packaging.** `<PackageReference Include="Overt.Build" />` compiles `.ov` files alongside `.cs` in any csproj. `overt` packaged as a .NET global tool. Both nupkgs are produced and tested; neither is published to nuget.org yet ([`ROLLOUT.md`](ROLLOUT.md) Phase 2).
- **Not yet.** Go back-end emission, LSP server, cross-file module system beyond the current in-repo graph, and self-hosted compiler, all on the roadmap in [`CARRYOVER.md`](CARRYOVER.md).

---

## Contributing

Overt is an early-stage solo project. Issues and discussion are welcome, but external PRs are unlikely to be merged until the language stabilizes enough that the bar for changes is clearer than it is today. The living design document is the place to propose a direction; [`examples/`](examples/) is the place to stress-test it.

Locked v1 decisions are enumerated in [`CARRYOVER.md`](CARRYOVER.md). Re-opening them requires new evidence, not new preferences.

---

## License

Apache License 2.0. See [`LICENSE`](LICENSE).
