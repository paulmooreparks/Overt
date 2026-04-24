# Overt — Session Carryover

For the next Claude Code session working on this project. Read this first; it points at everything else.

**Project location:** `C:\Users\paul\source\repos\Overt`. GitHub: [`paulmooreparks/Overt`](https://github.com/paulmooreparks/Overt) (public, Apache-2.0).

---

## What Overt is

An agent-first programming language — written, read, and maintained primarily by LLM agents, with humans in a review/audit role. Transpiles to readable source in host languages (C# primary via Roslyn, Go secondary). Sits alongside existing code via a "bridge, don't replace" deployment model rather than greenfield replacement.

The name *is* the design philosophy: every effect, error, dispatch, mutation, and piece of state is *overt* — visible at the call or declaration site, never concealed from the reader.

---

## Where to start reading

1. **[`AGENTS.md`](AGENTS.md)** — the operational doc for agents writing Overt. Every construct with one canonical example, every diagnostic code with its fix, the stdlib surface with real signatures, known gaps called out. Load this into context at session start whenever you'll be authoring `.ov` code. Not `DESIGN.md` — that's rationale.
2. **[`README.md`](README.md)** — status, layout, and a walk through the compiler pipeline. Fastest orientation for working on the compiler itself.
3. **[`DESIGN.md`](DESIGN.md)** — authoritative design. 26 sections, ~1100 lines.

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

What exists today, pinned by 359 passing tests:

- **Lexer** (mode-stack, full interpolation, token-stream goldens for every example).
- **Parser** (recursive descent, full precedence grammar, all 12 examples parse cleanly).
- **Name resolver** (symbol table, no-shadowing, ambient prelude scope, did-you-mean suggestions, module-qualified stdlib resolution for `List.empty` / `Trace.subscribe` / `CString.from`).
- **Type checker with full semantic enforcement** — 11 diagnostic codes that reject real bugs:
  - OV0300..0306: type / arity / field / arm / condition mismatches
  - OV0307: ignored `Result` (DESIGN.md §11 guarantee)
  - OV0308: non-exhaustive match on user enums, `Option`, and `Result` (DESIGN.md §8's "single most valuable check")
  - OV0310: uncovered effect rows — direct calls, module-qualified calls, and higher-order propagation through effect-variable argument inference
  - OV0311: refinement-predicate decidability at literal boundary crossings (DESIGN.md §8)
  - OV0312: `break` / `continue` outside a loop body
  - OV0313: `for each` iterable must be a `List<T>`
  - Non-generic type aliases are transparent for type-equality; the refinement check is the only layer that fires on `let a: Age = 42`.
- **Synthetic stdlib declarations** with real signatures — `Result`, `Option`, `List`, `Ok`/`Err`/`Some`/`None`, `println`, collection operations, `Trace`, `CString`, variant lists for `Option`/`Result`.
- **Real stdlib runtime implementations** ([`Overt.Runtime.Prelude`](src/Overt.Runtime/Prelude.cs)) — `map`, `filter`, `fold` over `ImmutableArray`; `par_map` runs concurrently via `Parallel.For` and returns first-Err by original index; `Trace.subscribe`/`Trace.emit` dispatch to registered subscribers. Transpiled Overt programs that touch collections now run, not just compile — verified by [`StdlibTranspiledEndToEndTests`](tests/Overt.Tests/StdlibTranspiledEndToEndTests.cs), which compiles a small `.ov` program in-memory and invokes `Module.main()`.
- **Int64 primitive type.** Overt now has `Int` (32-bit, lowers to C# `int`) and `Int64` (64-bit, lowers to C# `long`) as distinct primitives. Previously `long`-returning BCL methods were `// skipped`; now they're part of the binding surface. Unlocks `Environment.TickCount64`, `TimeSpan.Ticks*`, `DateTime.Ticks`, and all fixed-constant fields typed as `long` (e.g., `TimeSpan.TicksPerSecond`). BindGenerator maps `typeof(long) -> Int64` directly. Overloads that differ by int-vs-long (e.g., Math overloads) stay distinguishable via the type-suffix machinery.
- **Value-type (struct) extern support.** Structs like `DateTime`, `TimeSpan`, `Guid` now emit `extern type` and their full instance/static surface. DateTime went from 2 to 51 externs; TimeSpan from ~9 to 58. Instance properties emit as zero-arg-besides-self externs; the emitter's `::` handler detects properties via reflection and emits bare member access (`self.Year`) instead of a method call.
- **Cross-type opaque extern references.** `overt bind --with-opaque <FullName>[=<module>]` lets a generated facade reference OTHER opaque types the user has declared elsewhere. The generator renders the type under its Overt short name, and (if a module path was provided) emits a `use <module>.{<Name>}` at the top of the facade. `StreamReader(Stream)` and `HttpClient` methods taking `Uri` both work now. Registry is repeatable at the CLI.
- **CLI force-loads common BCL assemblies.** `System.Net.Http`, `System.Text.Json`, `System.Text.RegularExpressions`, etc. aren't loaded by default in an AppDomain that only touches our own code; `overt bind` now explicitly touches them so reflection can reach them.
- **Tier-1 audit passed (Go-backend corner check).** Confirmed zero hardcoded `"csharp"` strings in `Overt.Compiler`; `ExternDecl.Platform` and `ExternDecl.BindsTarget` are opaque data handled only in `Overt.Backend.CSharp`. `Overt.Compiler.csproj` has zero project/package references. `Stdlib.cs` defines language-level prelude names (Result/Option/List/map/filter/par_map/println) — per-backend runtimes must each provide them but the names are shared.
- **Opaque-type extern bindings: instance methods and constructors.** `extern "csharp" type StringBuilder binds "System.Text.StringBuilder"` declares an opaque Overt type whose host representation is a full type name. The C# emitter turns this into a `using StringBuilder = global::System.Text.StringBuilder;` type alias at the top of the generated file. Extern functions binding to `Ns.Type..ctor` emit `new global::Ns.Type(args)`; extern functions binding to `Ns.Type::Method` require a first param named `self: T` and emit `self.Method(args)`. BindGenerator auto-emits all three shapes (static members + constructors + instance methods) for reference types, so `overt bind --type System.Text.StringBuilder` produces a usable facade in one command. Aliased module imports (`use foo as bar`) bring the imported module's types into scope unqualified alongside the alias, matching C#/Rust's using-alias ergonomics.
- **Property + field access in externs, type-based overload disambiguation.** The C# emitter now consults reflection at emit time to decide whether a binds target is a property (bare member access) or method (parenthesized call). `System.Environment.MachineName` emits as `System.Environment.MachineName` with no parens; `System.Environment.Exit(code)` keeps its call shape. BindGenerator emits public static properties and fields as zero-arg externs alongside methods; overloaded methods disambiguate by C# parameter type names so `Math.Abs(int)` becomes `abs_int` and `Math.Abs(double)` becomes `abs_double` (both would have been `abs_1` under arity-only). Parameters that would shadow top-level facade names (e.g., `Environment.Exit(exitCode)` + `Environment.ExitCode`) get an `_arg` suffix so Overt's no-shadow rule doesn't reject the generated facade.
- **Blessed stdlib with auto-discovery, per-backend structure.** Facades live under `stdlib/<backend>/*`. Today only `stdlib/csharp/system/*` exists (the only backend emitting code): `stdlib.csharp.system.io.path`, `io.file`, `math`, `environment`, `guid`, `convert`, `console`. The CLI's `DiscoverSearchDirs` walks up from `overt.exe` looking for any ancestor containing `stdlib/`, so `use stdlib.csharp.system.io.path as path` just works. `$OVERT_STDLIB` override accepted. `install.ps1` copies `stdlib/` alongside the published binary. Generated facades use `global::<binds-target>` in emitted calls so they don't collide with Overt's own `System.*` namespace under `Overt.Generated.Stdlib.Csharp.System.*`.
- **Cross-file modules via `use` — both shapes.** Two import forms now work end-to-end: `use a.b.{sym1, sym2}` (selective, symbols in scope unqualified) and `use a.b as alias` (aliased, access via `alias.sym`). Dotted paths walk directories: `use stdlib.http.client` resolves to `stdlib/http/client.ov` in the search-path directories (entry file's dir by default). ModuleGraph discovers sibling and nested `.ov` files, topologically orders imports, detects cycles. NameResolver threads exports through; TypeChecker sees their types via `importedSymbolTypes`. Emitter emits `using static Overt.Generated.<Path>.Module;` for selective and `using Alias = Overt.Generated.<Path>.Module;` for aliased. `overt run main.ov` handles the full graph; other emit modes are still single-file. Wildcard imports forbidden (DESIGN.md §19) — OV0163 if you try.
- **C# extern runtime + facade generator — the BCL is reachable.** `extern "csharp" fn` declarations now lower to real calls into the bound C# method. A `Result<_, IoError>` return automatically wraps the call in a try/catch that converts exceptions to `Err(IoError { narrative })`. `overt bind --type System.IO.Path` reflects on a .NET type and emits an Overt facade with effect rows inferred from a curated namespace table (pure for `Math`/`String`/`IO.Path`; `io,fails` for most I/O; `io,async,fails` for `Net.*`). Overloads are disambiguated by arity suffix (`combine_2`, `combine_3`). Parameters/returns the generator can't map cleanly emit as `// skipped` comments. An Overt program can now call `System.IO.Path.Combine`, `System.IO.File.ReadAllText`, etc. end-to-end, verified by `Transpiled_ExternCsharp_*` tests.
- **Formatter.** `overt fmt <file>` emits one canonical form: four-space indent, trailing commas on multi-line lists, one statement per line, match arms one per line, named-arg calls for multi-arg. `--write` updates in place. Comment-preserving — line comments survive a round-trip. Backed by 13 idempotence tests (every example formats to a fixed point and the result re-parses cleanly). Prerequisite: the lexer now emits `LineComment` tokens; the parser filters them out of its token stream on entry; the formatter reads the unfiltered stream to re-interleave comments at their source positions.
- **Imperative control flow: `for each`, `loop`, `break`, `continue`, plus literal patterns in `match`.** The parser, checker, and emitter all handle them end-to-end. `for each x in xs { ... }` lowers to C# `foreach (var x in xs.Items)`, `loop { }` to `while (true)`, `break`/`continue` to their C# equivalents. OV0312 rejects `break`/`continue` outside a loop body; OV0313 requires `for each`'s iterable to be `List<T>`. Literal patterns (`0`, `1`, `-1`, `true`, `"exit"`) match the scrutinee for equality and don't contribute to exhaustiveness — a match using them still needs `_`.
- **Faithful `?` / `|>?` propagation — errors are values, not exceptions.** The emitter's `?`-hoisting pass transforms every always-evaluated `?` and `|>?` site into a `var __q_N = ...; if (!__q_N.IsOk) return Err<E>(__q_N.UnwrapErr()); var __qv_N = __q_N.Unwrap();` preamble before the enclosing statement, then substitutes the unwrapped local at the original site. Conditionally-evaluated sites (inside if/match/while arms or block expressions) fall back to `.Unwrap()` and are marked as a follow-up. Verified by new end-to-end tests that catch an Err flowing out of `Module.main()` as a returned value.
- **C# emitter** (type-aware, expected-type threading for generic inference, stdlib variant-pattern lowering, `#line` directives for PDB mapping back to `.ov` source).
- **Runtime prelude** ([`Overt.Runtime`](src/Overt.Runtime)) — `Unit`, `Result<T, E>`, `Option<T>`, `IoError`, `RaceAllFailed<E>`, `List<T>` and friends, target-typed marker structs for `Ok`/`Err`/`Some`.
- **End-to-end harness** that regenerates `Generated.cs` from `hello.ov` on demand (`OVERT_REGEN_HARNESS=1 dotnet test`) and runs the transpiled program.
- **Debug mapping** via portable PDB (§18). Runtime errors, debuggers, and stack traces resolve to `.ov` source, not `.cs`.
- **Compiler Explorer release engineering staged** — [`.github/workflows/release.yml`](.github/workflows/release.yml) builds a Linux x64 self-contained binary on any `v*` tag; the two-PR submission playbook is in [`tooling/godbolt/SUBMISSION.md`](tooling/godbolt/SUBMISSION.md). Waiting on an explicit tag push.

What's notably absent, ordered by impact on "can I write real code in this":

- **`?` deep inside a call argument within an if/match arm.** Direct `let x = if cond { foo()? } else { bar }` now lowers to stmt-level C# if/else with proper early-return. But `foo(if cond { bar()? } else { baz })` — the `?` is buried inside an argument inside an arm — may still fall back to `.Unwrap()`-that-throws. Workaround: lift the `?` to a preceding let.
- **Block comments.** `// line` works; `/* ... */` block comments don't parse. Unblock trivial, low priority.
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

1. **Generic-method opaque support** (1 session). `HttpClient.GetFromJsonAsync<T>(Uri)` and similar BCL methods that take type parameters are skipped by BindGenerator today. Adding a convention for declaring generic externs (either explicit type args in the binds target, or a separate facade per specialization) unlocks typed JSON interop.
4. **Task/async interop at the extern boundary** (1–2 sessions). Methods returning `Task<T>` are currently unmapped. Overt has an `async` effect but no machinery to `.Result`-wait, `await`, or propagate a Task into the Overt calling convention. Needs a design decision: does Overt use `fn`s that appear synchronous but run on a TPL scheduler? Or emit explicit `Task<T>` as an opaque return type with an Overt-side await construct?
3. **Extern grammar extensions** (1 session). Today extern handles static methods. For BCL we also need: instance methods (with a `self` parameter convention), constructors (binds to `..ctor`), properties (zero-arg binds to a static or instance property getter). The binding runtime already handles static-method-shaped bindings — the changes are in the parser and emitter.
4. **MSBuild integration** (1–2 sessions). Lets a `.csproj` with `<OvertCompile Include="*.ov"/>` transpile, reference Overt.Runtime, and link against NuGet.
5. **Conditional-context `?` — remaining deep-nesting case** (½ session). Stmt-level lowering is now in for `let x: T = if cond { foo()? } else { bar }` and match equivalents. Still falls back to `.Unwrap()` when `?` is buried deep inside a call argument within an if/match arm (e.g., `foo(if cond { bar()? } else { baz })`). Fix: extend `NeedsStmtLowering` detection to walk into call arguments, or transform such cases to lift the `?` into a prior let. Low practical urgency — the common shape now works.
3. **Runtime-assertion emission for undecidable refinement predicates** (1 session). The emitter's implicit-operator generator on wrapper records should evaluate the predicate and throw on violation. Closes the last gap in the refinement-types guarantee.
4. **Diagnostic upgrade: `note:` pointers into `AGENTS.md`** (½ session). `AGENTS.md` exists now. Next is plumbing diagnostics to include `note: see AGENTS.md §<N>` so an agent hitting an OV code learns the rule from the error message, in context, without needing to go look. Touches every diagnostic site in the compiler but each touch is one line.
5. **Formatter** (2 sessions). Rules are in §21. Rust's `rustfmt` / Go's `gofmt` is the shape — consumes AST + trivia, emits canonical source. Needed for `@review` / `@agent` comment tooling and for asserting "one canonical form" mechanically.
6. **Tuple-of-enum exhaustiveness** (1 session). Cartesian-product walk over arm patterns in `match (a, b)` against enum types. Additive to OV0308.
7. **Go backend** (2–3 sessions). Fresh emitter against the same AST + TypeCheckResult. Real forcing function for the IR being runtime-neutral per §20. Once a single example emits identically through both, the conformance suite is real.
8. **MSBuild integration** (1–2 sessions). Build task that finds `.ov` files in a csproj, invokes the emitter, wires the generated `.cs` into the compilation. Standard `<Target>` with item groups; the complexity is in the incremental-build story.
9. **LSP server** (multi-session). Reuse the parser and type checker; wire diagnostics to publishDiagnostics, implement hover / go-to-definition via ResolutionResult and TypeCheckResult. Blocks good IDE integration.
10. **Module system** (multi-session). Real `use` imports, a resolver that spans files, a package-discovery story. The largest remaining architectural lift.
11. **Full type inference with unification** (multi-session). Solve generic type arguments at call sites; propagate through argument-driven inference. Would eliminate several emitter workarounds.

---

## Agent-RWRA findings and hypotheses

These are observations from agent use of Overt that aren't yet validated at
scale but are concrete enough to capture. They're about which design
decisions make reading/writing/reasoning easier for LLM agents — the
primary audience.

### H1 (2026-04-24). The three-let form likely beats pipe composition for agent RWRA.

**Observation.** Same computation expressed three ways in `examples/app.ov`-style programs:

```overt
// Three lets — verbose, zero implicit transformations per line.
let b1: StringBuilder = sb.new_()
let b2: StringBuilder = sb.append_string(self = b1, value = "hello ")?
let b3: StringBuilder = sb.append_string(self = b2, value = "from Overt")?
let result: String = sb.to_string(b3)?

// let mut — single accumulator, explicit mutation.
let mut b: StringBuilder = sb.new_()
b = sb.append_string(self = b, value = "hello ")?
b = sb.append_string(self = b, value = "from Overt")?
let result: String = sb.to_string(b)?

// Pipe chain — terse, two implicit transformations per arrow.
let result: String =
    sb.new_()
      |>? sb.append_string(value = "hello ")
      |>? sb.append_string(value = "from Overt")
      |>? sb.to_string
```

**The agent self-report (Claude Opus 4.7, 2026-04-24):** the three-let form
is easiest to RWRA. No implicit semantics anywhere; every line tells its
inputs and outputs directly; edits are local. Pipes cost two implicit
operations per arrow (positional splice + `|>?` unwrap) that the agent must
simulate mentally at each step. `let mut` sits in the middle — the mutation
tracking is a smaller tax than pipe mechanics but bigger than no tax at all.

**Why this matters.** Pipe syntax is optimized for human visual-flow
recognition. The three-let form is optimized for agent step-by-step
reasoning. Overt's target is agent RWRA primary, human RWRA secondary —
which suggests pipes are a human-optimized feature that may not earn their
seat at the table.

**Hypothesis to validate.** As real agent-authored Overt programs
accumulate, measure: when agents are free to pick between equivalent pipe
and three-let forms, which do they pick, and which yields fewer bugs on
modification? If the finding holds:

- AGENTS.md should steer agents toward the three-let (or `let mut`) form as
  canonical, and explicitly mark pipes as an expert idiom.
- The formatter could consider de-pipelining when it can prove equivalence.
- Pipes might get reclassified from "core" to "v1 optional" — if the
  empirical data says they hurt more than they help, removing them from
  the language is on the table.

Do not pre-emptively strip pipes; the finding is a single-agent
self-report, not data yet. Validate first.

### H2 (2026-04-24). Conditional-context `?` inconsistency is the same class of problem.

`?` in a direct if-expression arm now lowers to early-return (stmt-level
restructuring). `?` buried deep inside a call argument within an arm falls
back to `.Unwrap()`-that-throws — same `?` character, different semantics,
no visible marker. This is exactly the "implicit transformation per site"
problem that pipes have, except worse because the inconsistency is hidden.
Treat the fix (extend `NeedsStmtLowering` into call args) as higher
priority than its "½ session, low urgency" estimate suggests. An agent
cannot safely rely on `?` unless its behavior is uniform.

### H3–H9 (2026-04-24). Language-surface audit findings.

A pass over the language looking for implicit-per-line decisions an agent
must carry. Full analysis is in the session transcript; the short list:

- **H3. Type alias transparency vs nominality.** Non-generic aliases are
  transparent (`Age == Int`); generic aliases are nominal. Same keyword,
  context-dependent semantics. *Needs validation before changing.*
- **H4. Extern binds-target punctuation** encoded three call shapes in an
  opaque string (`.` / `::` / `..ctor`). **Resolved 2026-04-23.** First-class
  `extern "csharp" instance fn` / `extern "csharp" ctor fn` keywords now
  select shape; binds target is always a dotted path. Diagnostics OV0315
  (missing `self`) and OV0316 (missing return type on ctor) catch misuse.
  All stdlib facades regenerated.
- **H5. Optional type annotation on `let`.** **Resolved 2026-04-23.**
  Required via OV0314; all examples and tests updated.
- **H6. Optional trailing `;` after statements.** **Resolved 2026-04-23.**
  Stray `;` now rejected with OV0170; newlines separate statements.
- **H7. `|>?` vs `|>`** — same prefix, different semantics. Moot if H1
  reclassifies pipes as expert idiom; no standalone action.
- **H8. Refinement-predicate silent deferral** when undecidable.
  **Resolved 2026-04-24.** Generic refinements throw `RefinementViolation`
  at the wrapper's implicit operator. Non-generic refinements route
  through synthesized `__Refinements.{Alias}__Check` helpers that the
  emitter wraps around boundary expressions — call args, let
  initializers, record field inits, and function return expressions.
  Statically-proven-safe literal crossings skip the wrap; undecidable
  and non-literal crossings always run the predicate.
- **H9. Block-as-expression trailing value** is implicit per-block. The
  Rust/ML tradition thinks this earns its keep; hold for validation.

**Strengths confirmed in the same pass** (keep as-is): named args on
multi-arg calls; explicit `self` for instance methods; effect rows on every
fn; exhaustive match; no shadowing; no method-call syntax; errors as Result
values; single-arg positional as ambiguity-free zone.

**What the three "actionable now" items share:** none of them required
empirical agent data because the cost is measurable at the language-design
level — each eliminates an implicit decision rule the agent must carry. All
three (H4, H5, H6) shipped together 2026-04-23.

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
- **Per-backend stdlib is primary; portability is a separate backend, not a language feature** (§19). `stdlib/csharp/*` binds to .NET idioms; future `stdlib/go/*` would bind to Go idioms. No platform-neutral abstraction layer in the core. Programs that need portability use a future *portable backend* with its own stdlib engineered for that purpose — users opt in explicitly. Rationale: agent-driven retargeting is cheap; portable abstractions are expensive forever. (Locked 2026-04.)
- **Tooling is tiered: backend-independent vs. per-backend** (§20). Lex / parse / resolve / type-check / format / module graph / OV diagnostics / LSP protocol live in `Overt.Compiler` (one implementation, shared). Emission, runtime library, binding generator (`overt bind`), runner (`overt run`), debug mapping, host-source inspection, and package-system interop live in each `Overt.Backend.<Host>` project. The CLI is a thin dispatcher. `BindGenerator` moved from `Overt.Cli` into `Overt.Backend.CSharp` to make the split real. (Locked 2026-04.)
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

**The language is usable: 13 diagnostic codes reject real bugs; real stdlib runtime; faithful `?` propagation (including inside if/match arms); full imperative control flow; literal match patterns; canonical `overt fmt`; every diagnostic points at the relevant AGENTS.md section; and — new this session — `extern "csharp"` actually calls the BCL, with `overt bind` generating typed, effect-annotated facades via reflection. 341/341 tests. Godbolt release engineering staged and waiting on a tag push. The architectural tiers still outstanding: Go backend, module system (so facades can be `use`-imported across files), LSP, MSBuild integration (for `.csproj` + NuGet).**
