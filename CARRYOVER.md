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

The frontend works end-to-end on C#. **Every example in [`examples/`](examples/) transpiles into C# that compiles cleanly via Roslyn**, pinned by a test for every example. The transpiled `examples/hello.ov` actually runs and prints "Hello, LLM!" — verified by the [`tests/Overt.EndToEnd`](tests/Overt.EndToEnd) harness.

What exists today, pinned by 212 passing tests:

- **Lexer** (mode-stack, full interpolation, token-stream goldens for every example).
- **Parser** (recursive descent, full precedence grammar, all 12 examples parse cleanly).
- **Name resolver** (symbol table, no-shadowing, ambient prelude scope, did-you-mean).
- **Type checker v0** (TypeRef IR, declaration types, expression annotations — annotates but does not reject yet).
- **Synthetic stdlib declarations** (real signatures for `Result`, `Option`, `List`, `Ok`/`Err`/`Some`/`None`, `println`, collection ops, `Trace`, `CString`; feeds both resolver and checker).
- **C# emitter** (type-aware, expected-type threading for generic inference, stdlib variant-pattern lowering, `#line` directives for PDB mapping back to `.ov` source).
- **Runtime prelude** ([`Overt.Runtime`](src/Overt.Runtime)) — `Unit`, `Result<T, E>`, `Option<T>`, `IoError`, `RaceAllFailed<E>`, `List<T>` and friends, target-typed marker structs for `Ok`/`Err`/`Some`.
- **End-to-end harness** that regenerates `Generated.cs` from `hello.ov` on demand (`OVERT_REGEN_HARNESS=1 dotnet test`) and runs the transpiled program.
- **Debug mapping** via portable PDB (§18). Runtime errors, debuggers, and stack traces resolve to `.ov` source, not `.cs`.

What's notably absent:

- **Type-error diagnostics.** The checker annotates but never emits `OV030x` mismatch codes. Next session's big target.
- **Effect-row enforcement.** Declared, annotated, not yet verified.
- **Refinement predicate checking.** Predicates survive into the AST; the decidable-vs-runtime-assertion split is unimplemented.
- **Real stdlib.** The prelude stubs throw `NotImplementedException` at runtime for `map`, `filter`, `par_map`, `fold`, etc.
- **Go backend.** Scaffold only; no emission.
- **Formatter.** Not started. Canonical form is enforced by convention in the examples, not mechanically.
- **Module system / packaging.** Spec'd in §19, not implemented.

---

## What the next session could reasonably start on

In rough priority order:

1. **Type-error diagnostics (`OV030x`).** Use the checker's annotations to reject programs with mismatched argument types, unknown fields, incompatible match arms, and wrong-arity calls. Biggest downstream unlock: every later pass runs on validated input.
2. **Effect-row checking.** Enforce that a function body's effects are a subset of the declared row. The one mechanism that turns Overt's "effects are visible" claim from annotation into guarantee.
3. **Refinement predicate decidability.** Decide which `where` predicates the checker can prove statically vs. which emit runtime assertions at construction/boundary points. Needs a small decidable-fragment evaluator — range predicates, ADT shape, size bounds of fixed collections.
4. **Module-qualified resolution.** Make `List.empty`, `Trace.subscribe`, `CString.from` real module-qualified calls instead of emitter special-cases. Foundation for the `use` import syntax and for any user-authored stdlib.
5. **Real stdlib implementations.** Start with `map` / `filter` / `fold` backed by `ImmutableArray<T>`; they're called by several examples and currently throw.
6. **Go backend.** Fresh emitter against the same AST + TypeCheckResult. Good forcing function for the IR being runtime-neutral (§20). Once a single example emits identically through both, the conformance suite is a real thing.
7. **Formatter.** Rules are in §21. Rust's `rustfmt` / Go's `gofmt` is the shape — consumes AST + trivia, emits canonical source. Needed before `@review` / `@agent` comment tools mature.

---

## Decisions that are locked — do not re-litigate

These are settled by explicit discussion with recorded rationale in `DESIGN.md`. Re-opening them burns tokens and drifts the design. If a new requirement genuinely breaks one, surface it as new data — don't hand-wave it open.

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

**Last session landed `#line` directives in the emitter so PDBs resolve runtime errors back to Overt source. 12/12 examples compile cleanly via Roslyn; end-to-end hello.ov runs. 212/212 tests. Type-error diagnostics are next.**
