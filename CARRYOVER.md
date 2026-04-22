# Overt — Session Carryover

For the next Claude Code session working on this project. Read this first; it points at everything else.

**Project location:** `C:\Users\paul\source\repos\Overt` on the author's machine. GitHub repo: `Overt` (URL to be added once created). The directory was previously named `agenticlang`; any stale references to that name can be updated in place.

---

## What Overt is

An agent-first programming language — written, read, and maintained primarily by LLM agents, with humans in a review/audit role. Transpiles to readable source in host languages (C# primary via Roslyn, Go secondary). Sits alongside existing code via a "bridge, don't replace" deployment model rather than greenfield replacement.

The name *is* the design philosophy: every effect, error, dispatch, mutation, and piece of state is *overt* — visible at the call or declaration site, never concealed from the reader.

---

## Where to start reading

**Source of truth:** [`DESIGN.md`](DESIGN.md). 26 sections, ~1000 lines.

**Fastest orientation path** (≈15 minutes):

1. **§1 Thesis** — what Overt is and isn't
2. **§2 The central inversion** (the table) — the core design instinct
3. **§4 Token economics and one canonical form** — the primary evaluation lens
4. **§5 Semantic primitives** — pointers to the three axes (concurrency, errors, iteration) + mutation + traces
5. **§7 Surface syntax** — what code actually looks like
6. **§17 FFI** and **§19 Module system and packaging** — how Overt integrates with existing ecosystems

Five-minute version: §1, §2, §5. Those convey the philosophy and point into detail.

---

## Repo layout

```
/DESIGN.md                         Primary design document (source of truth)
/CARRYOVER.md                      This file
/examples/                         Example programs exercising the language
  hello.ov                         Minimum viable program
  dashboard.ov                     Task groups via `parallel`
  race.ov                          Fallback via `race`
  mutation.ov                      `let mut` and the `with` expression
  pipeline.ov                      `|>` and `|>?` composition
  effects.ov                       Effect-row type variables
  state_machine.ov                 Enum + exhaustive match on tuples
  refinement.ov                    Refinement types + flow narrowing
  trace.ov                         `trace` blocks + `TraceEvent` consumers
  ffi.ov                           All three FFI platforms (csharp, go, c)
  inference.ov                     `inference` effect + `par_map` propagation
  bst.ov                           Self-referential enum + recursion
/vscode-extension/                 Syntax highlighting scaffold
  package.json                     Extension manifest
  language-configuration.json      Brackets, comments, indentation
  syntaxes/overt.tmLanguage.json   TextMate grammar
```

No compiler yet. Everything above is design + examples + tooling scaffold.

---

## Current status

**V1 design is fully scoped.** §24 Open Questions is empty as of this carryover. No unresolved design issues gate v1.

**Next phase is execution: build the compiler.** Per §20 Build discipline, in order:

1. **Frontend** — parser, type checker, IR, diagnostics. Roughly 80% of the work.
2. **C# emission** primary (Roslyn-based, emitting C# source text, not IL).
3. **Go emission** as secondary / conformance target — validates that the IR is runtime-neutral.
4. **Single canonical test suite** from day one, exercised against both backends.

---

## What the next session could reasonably start on

Listed roughly by how unblocked each is:

1. **Compiler skeleton.** Project scaffold (dotnet solution or similar), lexer/parser stubs, AST types for the constructs in §7–§14. High-value; nothing in the design blocks this.
2. **Review the examples with fresh eyes.** Anything in `/examples/` that looks wrong is a design gap worth flagging before code is written against it.
3. **Finish the VS Code extension.** The TextMate grammar exists but hasn't been packaged or tested against the example files. `npm install -g @vscode/vsce && vsce package` in `/vscode-extension/` should produce a `.vsix` that can be installed for a first visual check.
4. **Stdlib type signatures.** §18 gives the stdlib scope; writing the signatures (as stubs) is a concrete way to exercise whether the type system is expressive enough.
5. **A small real program.** Pick something modestly non-trivial (a CLI tool, a tiny web service) and try writing it end-to-end in Overt. Even without a compiler, this surfaces ergonomic issues the example files might not.
6. **Build-time infrastructure.** Per §20: MSBuild integration story for C# backend; `go generate` story for Go.

---

## Decisions that are locked — do not re-litigate

These are settled by explicit discussion with recorded rationale in `DESIGN.md`. Re-opening them burns tokens and drifts the design. If a new requirement genuinely breaks one, surface it as new data — don't hand-wave it open.

- **Language name:** Overt. **Extension:** `.ov`. Registry conflicts were checked.
- **Surface syntax:** C-family with ML semantics (§6). Not indentation-significant.
- **Type system:** Static, non-nullable by default, no reflection, no user-defined macros, no full dependent types, no lifetime annotations, no subtyping, invariant generics (§8, §15).
- **No literal integer indexing** at source level (§13). Zero-cost iteration is the workaround; proven-index is the numeric-kernel escape hatch.
- **No method-call syntax.** Pipes (`|>` and `|>?`) for composition; bare calls otherwise (§7). Dots mean record field access or module-qualified call, nothing else.
- **Errors are values,** never exceptions. `Result<T, E>` with `?` propagation (§11). Exceptions convert at FFI boundaries, not in source.
- **Effect rows are explicit on every function,** row-polymorphic via effect-row type variables. Core effects: `io`, `async`, `inference`, `fails` (§7).
- **Immutable records.** `let mut` for rebinding local names; `with` for modified copies (§10). No shared mutable state, no mutable references.
- **Transpile to source, not IR.** Do not reach for LLVM without a concrete target the current backends cannot reach (§18).
- **Comment tags:** `@review:` (agent → human, resolved by deletion) and `@agent:` (human → agent, persistent). No threading, no status flags, no taxonomy (§21).
- **One canonical form** enforced by formatter. Rules in §21; no per-project or per-developer configuration.

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

---

**Last session ended with v1 fully scoped and all design questions answered. Next phase is implementation.**
