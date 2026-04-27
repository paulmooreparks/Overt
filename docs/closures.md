# Closures: design memo

Drafted because closures are the highest-leverage gap surfaced by
`samples/logtally/` and `samples/diffconf/` (both wanted
`filter(xs, x => predicate_using_outer(x))` and ended up with
`let mut` + `for each` + free helper fns instead). This memo
proposes a concrete design — syntax, capture model, effect rules,
emission strategy — for sign-off before any code lands. Update
in place as decisions settle.

---

## Surface syntax: anonymous `fn` expressions

Reuse the existing `fn` keyword in expression position. The grammar
mirrors named fn declarations, minus the name:

```overt
let pred: fn(Int) -> Bool = fn(x: Int) -> Bool { x > 0 }

filter(list = xs, predicate = fn(x: Int) -> Bool { x > 0 })

let cmp: fn(MapEntry<String, Int>, MapEntry<String, Int>) -> Int =
    fn(a: MapEntry<String, Int>, b: MapEntry<String, Int>) -> Int {
        level_rank(level = a.key) - level_rank(level = b.key)
    }
```

**Why not `|x| ...` (Rust-ish) or `(x) => ...` (TypeScript-ish):**

- Overt's design rule is "every type spelled at the use site." A
  pipe-bar form invites elided parameter types; a fat-arrow form
  invites elided return types. Reusing `fn(x: T) -> U { body }`
  keeps the discipline intact.
- One keyword, one shape: named fns and anonymous fns differ only
  in the missing name. Readers don't learn a second function form.
- Ambiguity-free in expression position. `fn(...)` only appears
  as a *type* in type-annotation position today; in value position
  it can only be an anonymous fn definition. The parser switches
  on the surrounding context.

**Required type annotations.** Parameter types and return type are
required on the anonymous fn, matching named fn declarations and
the `let` rule. No bidirectional inference from the receiving
context. Verbose at write time, unambiguous at read time — Overt's
default trade.

---

## Capture model: by-value at construction

When the anonymous fn references an outer-scope binding (a free
variable), it captures the *current value* of that binding at the
point the closure is constructed. Subsequent rebindings (`let mut`
re-assignment) of the outer name do not affect what the closure
sees.

```overt
let mut threshold: Int = 5
let pred: fn(Int) -> Bool = fn(x: Int) -> Bool { x > threshold }

threshold = 10
let result: List<Int> = filter(list = xs, predicate = pred)
// `pred` filters by > 5, not > 10. Capture happened when `pred`
// was constructed.
```

**Why by-value:**

- Matches the existing rule for everything else in Overt: records
  are immutable, lists are immutable, `let mut` rebinds the local
  name only — nothing observes the rebind from outside the local
  scope. Capture-by-value extends that rule to closures.
- Sidesteps Rust's borrow-and-lifetime story entirely. A closure
  doesn't hold a reference; it holds a snapshot.
- Predictable for readers: "what does this closure see?" is
  answered by reading the surrounding code at construction time,
  not by tracking mutations across the program.

**What about captured `List<T>` / `Map<K, V>` / records?** Overt's
collection types are already immutable values. Capturing a
`List<String>` snapshots the list reference; the underlying
`ImmutableArray<T>` can't be mutated either, so the snapshot is
permanent regardless. Same for records.

**No closures over mutable references.** A closure can't capture a
`let mut` binding *as a reference*, only as a value snapshot. If a
program needs shared mutable state across closure boundaries, it
needs a different mechanism (probably a stdlib `Cell<T>` /
`Atomic<T>`, gated on the rule of three).

---

## Effect-row threading

A closure's body has its own effect row. The closure's *type*
carries that row in its function-type position:

```overt
let log_each: fn(String) !{io} -> () =
    fn(s: String) !{io} -> () {
        let _: Result<(), IoError> = println(s)
        ()
    }
```

The receiving fn parameter declares the row of any closure it
accepts. Stdlib's existing higher-order signatures already work
this way:

```overt
fn par_map<T, U, E>(
    list: List<T>,
    f:    fn(T) !{io, async, E} -> Result<U, E>
) !{io, async, E} -> Result<List<U>, E>
```

When a closure with effect row `R` is passed to a fn parameter
expecting row `R'`, the type checker requires `R ⊆ R'`. Calling a
closure value adds its effect row to the caller's row, same as any
fn call.

**Effect inference inside the closure body.** Today every named fn
must declare its row; the type checker validates it matches the
row implied by the body. For anonymous fns, the same rule applies:
the row is part of the closure-fn's syntax (the `!{...}` between
parameters and `->`). No row inference. Required at write time.

---

## Free-variable capture analysis

The type checker walks the closure body and collects identifier
references that bind to symbols *outside* the closure's parameter
list and local scope. Those are the captured variables. Each
capture's type is whatever the symbol's type is in the enclosing
scope.

**What can be captured:**

- `let` and `let mut` bindings from any enclosing block
- Function parameters from the enclosing fn
- Pattern-binder names from match arms / for-each bodies the
  closure is nested inside

**What cannot be captured (compile error):**

- Names that don't resolve at all — that's already an OV02xx error
  via the existing resolver.
- Symbols whose lifetime is dynamically scoped in a way the static
  capture analysis can't reason about. None of Overt's current
  bindings have this shape, so this constraint is preventative,
  not active.

---

## Lowering

### C# back end

Closures lower to C# lambdas using `Func<T1, ..., Tn, R>` /
`Action<T1, ..., Tn>` for the closure value's type. Captured
variables snapshot to `readonly` locals immediately before the
lambda construction, so capture-by-value semantics survive C#'s
default capture-by-reference.

```csharp
// Source: fn(x: Int) -> Bool { x > threshold }
//         where `threshold` is captured.
{
    var __cap_threshold = threshold;  // snapshot
    Func<int, bool> __closure = (int x) => x > __cap_threshold;
    // ...use __closure...
}
```

`Result<T, E>`-returning closures use the same `Func<...>` shape;
nothing special.

### Go back end

Same shape, with Go closures. Capture-by-value via local
re-binding before the closure literal, since Go closures are also
capture-by-reference by default:

```go
// Source: fn(x: Int) -> Bool { x > threshold }
{
    capThreshold := threshold  // snapshot
    closure := func(x int) bool { return x > capThreshold }
    // ...
}
```

### Effect rows

Erased at lowering on both back ends, same as for named fns.

---

## Diagnostics

Three new error codes (numbered after OV0318):

- **OV0319** — anonymous fn missing parameter type annotation.
  Fires on `fn(x) -> Bool { ... }` (parameter type omitted).
  Help: "annotate every parameter type: `fn(x: Int) -> Bool`."

- **OV0320** — anonymous fn missing return type annotation.
  Fires on `fn(x: Int) { x > 0 }`. Help: "add the return type:
  `fn(x: Int) -> Bool { ... }`."

- **OV0321** — closure body's inferred effect row is broader than
  the declared row. Fires when the body calls a fn with effects
  the closure's signature doesn't list. Help: "add `!{io}` (or
  whichever effect) to the closure's signature."

OV0306 (call-arg-type mismatch), OV0310 (caller's row doesn't
cover callee's row), and OV0314 (every let needs a type) all apply
unchanged when the closure value flows through them — no new codes
needed.

---

## Out of scope (deferred)

- **Closures over mutable references.** No `Cell` / `Atomic` /
  shared-state mechanism in this batch. A closure that needs to
  mutate state across calls needs a non-closure design (a record
  threaded through fold, etc.), same as today.
- **Type inference inside the closure.** No `fn(x) { x > 0 }` —
  full annotations always required.
- **Closure conversion to fn pointer / unboxed forms.** Treat
  closures as boxed by default; if real perf data ever calls for
  unboxing a known-non-capturing form, that's a future
  optimization, not a v0.2.x feature.
- **Self-recursion in anonymous fns.** No name to recurse through.
  Recursive helpers stay named.

---

## Implementation sequence

1. **Parser + AST.** New `AnonymousFunctionExpr` node with
   parameter list, effect row, return type, body block. The
   existing `ParseFunctionType` work is the template — most of the
   parsing scaffolding is reusable. Test: round-trip simple
   closure expressions through lex/parse/format.

2. **Type checker.** Closure typing produces a `FunctionTypeRef`.
   Capture analysis walks the body collecting non-local symbol
   references; the resolver already records every identifier
   reference, so the analysis is a list-comprehension over that
   table filtered by scope. Effect-row check validates the body's
   inferred row is a subset of the declared row (OV0321). Test:
   closure assigned to typed let, passed to fn parameter, called.

3. **C# emitter.** Lower to lambda + capture snapshots. Test:
   round-trip closure call with captured value, captured rebound
   afterward (closure unaffected).

4. **Go emitter.** Same shape. Test: same round-trip on Go side.

5. **Sample rewrites.** Rewrite logtally and diffconf to use
   closures; verify the friction is gone (no more `let mut` +
   for-each + free helper fn for state-aware predicates).

6. **AGENTS.md.** New section under §6 (functions) covering
   anonymous fn syntax, capture-by-value rule, effect-row threading.

Each phase is independently testable. The parser + AST step is the
only one that risks lex/grammar surprises; the rest is mechanical.
