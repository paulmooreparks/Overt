# Overt

An **agent-first programming language** — written, read, and maintained primarily by LLM agents, with humans in a review and audit role. Transpiles to readable source in host languages (C# primary, Go secondary).

The name is the design philosophy: every effect, error, dispatch, mutation, and piece of state is *overt* — visible at the call or declaration site, never concealed from the reader.

> **Status (April 2026):** v1 design is fully scoped. Compiler implementation has started. No usable toolchain yet.

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

---

## Repository layout

```
DESIGN.md                     Primary design document (source of truth, ~1000 lines)
CARRYOVER.md                  Session handoff notes for the next working session
examples/                     Example programs — living test cases for the design
vscode-extension/             TextMate grammar + language config for .ov files
src/                          Compiler sources (C# / .NET 9)
tests/                        Canonical test suite — both backends must pass
```

See [`DESIGN.md §5`](DESIGN.md) for the map into the detailed design sections.

---

## Building

> The compiler is in early scaffolding. The instructions below will work once the .NET solution lands.

Requires the .NET 9 SDK.

```
dotnet build
dotnet test
```

The canonical test suite runs Overt programs through both the C# and Go backends and asserts identical observable behavior. Go backend emission may be stubbed at any given time — when it is, tests that depend on it are marked skipped, not silently passing.

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
