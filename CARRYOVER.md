# Overt — Session Carryover

For the next Claude Code session working on this project. Read this first; it points at everything else.

**Project location:** `C:\Users\paul\source\repos\Overt`. GitHub: [`paulmooreparks/Overt`](https://github.com/paulmooreparks/Overt) (public, Apache-2.0).

---

## What Overt is

An agent-first programming language — written, read, and maintained primarily by LLM agents, with humans in a review/audit role. Transpiles to readable source in host languages (C# primary via Roslyn, Go secondary). Sits alongside existing code via a "bridge, don't replace" deployment model rather than greenfield replacement.

The name *is* the design philosophy: every effect, error, dispatch, mutation, and piece of state is *overt* — visible at the call or declaration site, never concealed from the reader.

---

## Where to start reading

1. **[`README.md`](README.md)** — status, layout, and a walk through the compiler pipeline. This is the fastest orientation.
2. **[`DESIGN.md`](DESIGN.md)** — authoritative design. 26 sections, ~1100 lines.

Fastest DESIGN.md orientation path (≈15 minutes):

1. **§1 Thesis** — what Overt is and isn't
2. **§2 The central inversion** (the table) — the core design instinct
3. **§4 Token economics and one canonical form** — the primary evaluation lens
4. **§5 Semantic primitives** — pointers to the three axes (concurrency, errors, iteration) + mutation + traces
5. **§7 Surface syntax** — what code actually looks like
6. **§17 FFI** and **§19 Module system and packaging** — how Overt integrates with existing ecosystems
7. **§18 Backend strategy** — includes the debug-mapping subsection that makes runtime errors resolve to `.ov` source

Five-minute version: §1, §2, §5.

Grammar specs (authoritative for the parser/lexer):

- **[`docs/grammar/lexical.md`](docs/grammar/lexical.md)** — token grammar including the string-interpolation mode automaton.
- **[`docs/grammar/precedence.md`](docs/grammar/precedence.md)** — operator precedence and associativity.

---

## Current status

The frontend works end-to-end on C# with real semantic enforcement. **Every example in [`examples/`](examples/) transpiles into C# that compiles cleanly via Roslyn**, pinned by a test for every example. The transpiled `examples/hello.ov` actually runs and prints "Hello, LLM!" — verified by the [`tests/Overt.EndToEnd`](tests/Overt.EndToEnd) harness.

What exists today, pinned by 318 passing tests:

- **Lexer** (mode-stack, full interpolation, token-stream goldens for every example).
- **Parser** (recursive descent, full precedence grammar, all 12 examples parse cleanly).
- **Name resolver** (symbol table, no-shadowing, ambient prelude scope, did-you-mean suggestions, module-qualified stdlib resolution for `List.empty` / `Trace.subscribe` / `CString.from`).
- **Type checker with full semantic enforcement** — 11 diagnostic codes that reject real bugs:
  - OV0300..0306: type / arity / field / arm / condition mismatches
  - OV0307: ignored `Result` (DESIGN.md §11 guarantee)
  - OV0308: non-exhaustive match on user enums, `Option`, and `Result` (DESIGN.md §8's "single most valuable check")
  - OV0310: uncovered effect rows — direct calls, module-qualified calls, and higher-order propagation through effect-variable argument inference
  - OV0311: refinement-predicate decidability at literal boundary crossings (DESIGN.md §8)
  - Non-generic type aliases are transparent for type-equality; the refinement check is the only layer that fires on `let a: Age = 42`.
- **Synthetic stdlib declarations** with real signatures — `Result`, `Option`, `List`, `Ok`/`Err`/`Some`/`None`, `println`, collection operations, `Trace`, `CString`, variant lists for `Option`/`Result`.
- **Real stdlib runtime implementations** ([`Overt.Runtime.Prelude`](src/Overt.Runtime/Prelude.cs)) — `map`, `filter`, `fold` over `ImmutableArray`; `par_map` runs concurrently via `Parallel.For` and returns first-Err by original index; `Trace.subscribe`/`Trace.emit` dispatch to registered subscribers. Transpiled Overt programs that touch collections now run, not just compile — verified by [`StdlibTranspiledEndToEndTests`](tests/Overt.Tests/StdlibTranspiledEndToEndTests.cs), which compiles a small `.ov` program in-memory and invokes `Module.main()`.
- **Faithful `?` / `|>?` propagation — errors are values, not exceptions.** The emitter's `?`-hoisting pass transforms every always-evaluated `?` and `|>?` site into a `var __q_N = ...; if (!__q_N.IsOk) return Err<E>(__q_N.UnwrapErr()); var __qv_N = __q_N.Unwrap();` preamble before the enclosing statement, then substitutes the unwrapped local at the original site. Conditionally-evaluated sites (inside if/match/while arms or block expressions) fall back to `.Unwrap()` and are marked as a follow-up. Verified by new end-to-end tests that catch an Err flowing out of `Module.main()` as a returned value.
- **C# emitter** (type-aware, expected-type threading for generic inference, stdlib variant-pattern lowering, `#line` directives for PDB mapping back to `.ov` source).
- **Runtime prelude** ([`Overt.Runtime`](src/Overt.Runtime)) — `Unit`, `Result<T, E>`, `Option<T>`, `IoError`, `RaceAllFailed<E>`, `List<T>` and friends, target-typed marker structs for `Ok`/`Err`/`Some`.
- **End-to-end harness** that regenerates `Generated.cs` from `hello.ov` on demand (`OVERT_REGEN_HARNESS=1 dotnet test`) and runs the transpiled program.
- **Debug mapping** via portable PDB (§18). Runtime errors, debuggers, and stack traces resolve to `.ov` source, not `.cs`.
- **Compiler Explorer release engineering staged** — [`.github/workflows/release.yml`](.github/workflows/release.yml) builds a Linux x64 self-contained binary on any `v*` tag; the two-PR submission playbook is in [`tooling/godbolt/SUBMISSION.md`](tooling/godbolt/SUBMISSION.md). Waiting on an explicit tag push.

What's notably absent, ordered by impact on "can I write real code in this":

- **Conditionally-evaluated `?` still throws.** The hoisting pass is intentionally stopped at `if` / `match` / `while` arms and block-as-expression boundaries — hoisting both branches eagerly would run code that shouldn't run. `?` inside those contexts still lowers to `.Unwrap()` (throws on Err). Common in practice when a match arm does `foo()?`. Follow-up: per-branch hoisting that threads the early-return through the arm body.
- **Language gaps vs. real programs.** The parser accepts every construct the 12 examples use, but real programs will hit: `for each` / `loop` / `break` / `continue`, integer and float literal patterns in `match`, block comments. None are hard; each is a small parser + checker + emitter increment.
- **Runtime-assertion emission for undecidable refinement predicates.** The checker marks `size(self) > 0`-style predicates as "needs runtime check"; the emitter does not yet generate the check. Works around it today by writing the validation in user code, as refinement.ov does.
- **Formatter.** Not started. Canonical form is enforced by convention in the examples, not mechanically. Blocks the `@review` / `@agent` comment-tooling story.
- **Tuple-of-enum exhaustiveness.** `match (state, event) { ... }` skips the check today; state_machine.ov works because it has a wildcard catch-all.
- **Go backend.** Scaffold only; no emission.
- **Module system / packaging.** Spec'd in §19, not implemented. `use` directive parses but doesn't resolve cross-file imports.
- **MSBuild integration** for consumers. There's no "drop .ov files into a C# project and it compiles" story yet; you have to invoke `overt --emit=csharp` manually.
- **LSP / IDE integration.** Diagnostics available via CLI only. No hover, go-to-definition, etc.

---

## What the next session could reasonably start on

Ordered by "how directly this unblocks someone writing real Overt code":

1. **Language gaps: `for each` / `loop` / integer-literal patterns / `break` / `continue`** (1–2 sessions). Parser + checker + emitter work. Each is self-contained. The union covers what a user writing a CLI tool would hit.
2. **Conditional-context `?` hoisting** (½ session). Extend the hoisting pass to walk into `if` / `match` / `while` arms, hoisting each arm's `?` sites into the arm's body with a proper early-return chain. Closes the last remaining gap in "errors are values, no hidden unwinding."
3. **Runtime-assertion emission for undecidable refinement predicates** (1 session). The emitter's implicit-operator generator on wrapper records should evaluate the predicate and throw on violation. Closes the last gap in the refinement-types guarantee.
4. **`AGENTS.md` — the agent-facing grounding doc** (1 session, best done after #1 so stubs are real). ~400–600 line operational reference: every construct gets one canonical example, effect rows and their meanings, `Result`/`?` idioms, the stdlib surface with real signatures, what each OV diagnostic means and how to fix it. Designed to be loaded verbatim into an agent's context at session start. Not `DESIGN.md` — that's rationale; this is *how to write Overt today*. Paired with a pass over diagnostics to add `note:` pointers into `AGENTS.md` sections so an agent hitting an error learns the rule in situ.
5. **Formatter** (2 sessions). Rules are in §21. Rust's `rustfmt` / Go's `gofmt` is the shape — consumes AST + trivia, emits canonical source. Needed for `@review` / `@agent` comment tooling and for asserting "one canonical form" mechanically.
6. **Tuple-of-enum exhaustiveness** (1 session). Cartesian-product walk over arm patterns in `match (a, b)` against enum types. Additive to OV0308.
7. **Go backend** (2–3 sessions). Fresh emitter against the same AST + TypeCheckResult. Real forcing function for the IR being runtime-neutral per §20. Once a single example emits identically through both, the conformance suite is real.
8. **MSBuild integration** (1–2 sessions). Build task that finds `.ov` files in a csproj, invokes the emitter, wires the generated `.cs` into the compilation. Standard `<Target>` with item groups; the complexity is in the incremental-build story.
9. **LSP server** (multi-session). Reuse the parser and type checker; wire diagnostics to publishDiagnostics, implement hover / go-to-definition via ResolutionResult and TypeCheckResult. Blocks good IDE integration.
10. **Module system** (multi-session). Real `use` imports, a resolver that spans files, a package-discovery story. The largest remaining architectural lift.
11. **Full type inference with unification** (multi-session). Solve generic type arguments at call sites; propagate through argument-driven inference. Would eliminate several emitter workarounds.

---

## Agent-facing documentation strategy

Three surfaces, each doing a different job. All three matter; none substitutes for the others.

1. **`AGENTS.md` at repo root** — the grounding document. ~400–600 lines, terse, example-driven, one canonical form per construct. Loaded verbatim into agent context at session start. Covers: module shape, effect rows, `Result`/`?`, `match` exhaustiveness, refinement types, record updates with `with`, pipe composition, FFI boundaries, stdlib surface with real signatures, what each `OV0xxx` diagnostic means and the canonical fix. Not `DESIGN.md` — that's ~1100 lines of rationale; wrong artifact for a working agent.
2. **[`examples/`](examples/) as a reference corpus.** Already plays this role. Agents grep for idioms (`parallel`, `with`, `FFI`) and read the example. Living test cases — if they stop compiling, CI catches it. Rule: *if an agent can't find a pattern in `examples/`, the language doesn't really have it yet.* As new stdlib/control flow lands, add examples that exercise them.
3. **Compiler diagnostics as in-situ docs.** An agent hitting `OV0310` learns the effect-row rule from the diagnostic itself, in context, at the exact moment it's relevant. Every diagnostic should be self-contained — the primary message, a `help:` with the canonical fix, a `note:` pointing at the relevant `AGENTS.md` section. This is the pedagogically strongest position a doc can occupy and costs almost nothing beyond what we already emit.

The LSP (when it lands) adds hover-for-type and hover-for-effect as a fourth surface, but it's additive, not load-bearing.

---

## Decisions that are locked — do not re-litigate

These are settled by explicit discussion with recorded rationale in `DESIGN.md`. Re-opening them burns tokens and drifts the design. If a new requirement genuinely breaks one, surface it as new data — don't hand-wave it open.

- **Non-generic type aliases are transparent for type-equality.** `type Age = Int where ...` means `Age` and `Int` compare equal structurally; only the refinement predicate distinguishes them. Generic aliases (`type NonEmpty<T> = List<T>`) stay nominal — they emit as wrapper records. (Locked 2026-04-23.)
- **Language name:** Overt. **Extension:** `.ov`. Registry conflicts were checked.
- **Surface syntax:** C-family with ML semantics (§6). Not indentation-significant.
- **Type system:** Static, non-nullable by default, no reflection, no user-defined macros, no full dependent types, no lifetime annotations, no subtyping, invariant generics (§8, §15).
- **No literal integer indexing** at source level (§13). Zero-cost iteration is the workaround; proven-index is the numeric-kernel escape hatch.
- **No method-call syntax.** Pipes (`|>` and `|>?`) for composition; bare calls otherwise (§7). Dots mean record field access or module-qualified call, nothing else.
- **Errors are values,** never exceptions. `Result<T, E>` with `?` propagation (§11). Exceptions convert at FFI boundaries, not in source.
- **No undefined behavior in safe Overt.** Every UB source from C/C++ is designed out structurally (§8 "Defined behavior"). Integer overflow **traps by default**; wrap / saturate / checked are opt-in stdlib functions. Release behavior equals debug behavior.
- **No shadowing** across nested scopes (§3). Every name has one binding. Prelude names are an exception: patterns and locals may reuse them (otherwise `match opt { Some(v) => v, None => 0 }` would conflict with stdlib `None`).
- **Else is optional on `if`** in statement position. `if cond { body }` is sugar for `if cond { body } else { () }`; the then block must have type `()` (§4, resolved 2026-04-22).
- **Comparison and equality are non-associative.** `0 <= x <= 100` is a parse error; use `0 <= x && x <= 100` (resolved 2026-04-22; [`docs/grammar/precedence.md`](docs/grammar/precedence.md) §4).
- **Effect rows are explicit on every function,** row-polymorphic via effect-row type variables. Core effects: `io`, `async`, `inference`, `fails` (§7).
- **Immutable records.** `let mut` for rebinding local names; `with` for modified copies (§10). No shared mutable state, no mutable references.
- **Transpile to source, not IR.** Do not reach for LLVM without a concrete target the current backends cannot reach (§18).
- **Debug mapping via `#line` directives and portable PDB** (§18 debug-mapping subsection). No Overt-specific debug format. Generated `.cs` files are read-only by construction; runtime errors resolve to `.ov` source.
- **Comment tags:** `@review:` (agent → human, resolved by deletion) and `@agent:` (human → agent, persistent). No threading, no status flags, no taxonomy (§21).
- **One canonical form** enforced by formatter. Rules in §21; no per-project or per-developer configuration.
- **Host language for the compiler:** C# on .NET 9 (§20).
- **License:** Apache-2.0.

---

## Author context

The author (paul@smartsam.com) has 26 years of C# experience and concurrent Go experience. This informs the backend choices and the expectation that C# interop should feel native. Communication style is terse and direct — short answers, concrete proposals, tradeoff analysis over abstract philosophy.

---

## Working conventions for agents on this project

- `DESIGN.md` is authoritative. Capture decisions there; do not let them live only in chat.
- When adding new sections, renumber carefully — cross-references are scattered throughout the doc. `grep "^## \d" DESIGN.md` shows the current structure.
- `DESIGN.md §5 Semantic primitives` is the navigation hub — it points at the key detailed sections.
- Examples in `/examples/` are test cases for the design. If committed syntax can't comfortably express a real pattern, that is a signal to revise the design, not the example.
- Keep examples honest: use the same pipe syntax, no method calls, no positional args beyond named-elision where the single-arg rule applies.
- The `.cs` files the emitter produces are not source. They live under `tests/Overt.EndToEnd/Generated.cs` and the test suite regenerates them on `OVERT_REGEN_HARNESS=1`. Never edit generated C# directly — fix the `.ov` source or the emitter instead. Runtime errors already point at `.ov` lines via portable PDB, so this discipline holds naturally.

---

**Semantic-enforcement arc is operationally complete: 11 diagnostic codes now reject real bugs across type correctness, exhaustiveness, effect rows, ignored Results, and refinement predicates. Real stdlib runtime is in and `?` / `|>?` now lower to faithful early-return propagation (DESIGN.md §11's errors-as-values, not throw-based). 318/318 tests. Godbolt release engineering staged and waiting on a tag push. Next most-valuable piece: language gaps (`for each` / `loop` / literal patterns in `match`), then `AGENTS.md` as the grounding doc for agents writing Overt.**
