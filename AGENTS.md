# AGENTS.md — writing Overt

This file tells an LLM how to write working Overt code *today*. Load it into
context at the start of any session that will author or modify `.ov` files.

It is not rationale — that lives in `DESIGN.md`. Each section here shows the
**one canonical form** for a construct (no alternatives, no history) and the
diagnostic you'll see if you miswrite it.

Status as of commit tip: 359 tests; `overt run` executes transpiled programs
via in-memory Roslyn. The C# backend is primary; Go is scaffolded only.

---

## 0. Overt is for you, the agent

Overt's design target is **you**. Every tradeoff in the language prefers
agent read/write/reason-about clarity over human aesthetic conventions. When
you're writing Overt code, drop the human-oriented habits you carry from
other languages and apply the following, which are optimized for how you
actually work:

- **Verbosity that eliminates implicit behavior is a feature, not a smell.**
  A longer form with zero implicit transformations per line beats a terser
  form with two. You have a strong pattern-matcher and weak causal-chain
  simulation; explicit step-by-step plays to that asymmetry.

- **Named intermediate `let` bindings are the default for sequencing.** Don't
  reach for pipe composition just because it looks cleaner. Three
  consecutive `let` statements with visible inputs and outputs are easier
  for you to modify correctly than one pipe chain where each arrow hides a
  splice and an unwrap. Pipes exist in Overt but are an expert idiom, not
  the canonical form — see §10's guidance.

- **Redundant type annotations are valuable.** `let n: Int = ...` beats
  `let n = ...` even when the compiler could infer. The annotation is a
  check you didn't silently produce something of a different type, and a
  cue to your future self reading the line out of context.

- **When two forms are equivalent, pick the one that reads as a sequence of
  atomic steps.** That's the form you'll modify correctly weeks later, with
  or without the surrounding file in context.

The human maintainer reading your code is secondary audience. They will be
fine. Don't optimize for their pattern-matching at the expense of yours.
Details and rationale: `DESIGN.md` §2 and §4 ("canonical-form tie-breaker").

---

## 1. A complete program

```overt
module hello

fn main() !{io} -> Result<(), IoError> {
    println("Hello, LLM!")?
    Ok(())
}
```

Rules on display:
- `module <name>` is required as the first declaration.
- `main` takes no args and returns `Result<(), IoError>`. The process exits 0
  on `Ok`, 1 on `Err`.
- `!{io}` declares the effect row; any function performing I/O must list `io`.
- `println(...)` returns `Result<(), IoError>` — errors are values, not
  exceptions. The `?` propagates `Err` as an early return.
- `Ok(())` constructs the success value explicitly. There is no implicit
  return; the last expression of a block is the value if it isn't a statement.

To build and run:
```
overt run hello.ov    # compiles via Roslyn, executes, prints, exits 0
overt --emit=csharp hello.ov    # dumps the transpiled C# to stdout
```

---

## 2. Modules and declarations

A module is one `.ov` file. Cross-file imports come in two shapes:

```overt
// Selective — imported names are in scope unqualified.
use pathutil.{path_combine}

// Aliased — module's symbols accessed via `alias.name`.
use stdlib.http.client as http
```

- Module names can be dotted; segments map to directories. `use stdlib.http.client`
  resolves to `stdlib/http/client.ov` relative to the entry file's directory.
- The `.ov` file's own `module` declaration must match the import name:
  `stdlib/http/client.ov` declares `module stdlib.http.client`.
- Wildcard imports are forbidden (DESIGN.md §19); name the symbols you want
  (selective form) or alias the module (`as`).
- Selective + alias together isn't supported — pick one.
- `overt run` resolves the full module graph. Other emit modes
  (`--emit=csharp`, etc.) operate on a single file only and will fail on
  files with `use` declarations.

Top-level declarations:
- `fn <name>(...) ...` — function
- `record <Name> { field: Type, ... }` — product type, immutable
- `enum <Name> { Variant, Variant { field: Type }, ... }` — sum type
- `type <Name> = <Type> where <pred>` — type alias, optionally refined
- `extern "c" fn <name>(...) -> <Type>` — FFI declaration (body throws at
  runtime; binding is unimplemented)

Declaration order does not matter — forward references within a file are
fine.

---

## 3. Types

Primitives: `Int` (32-bit, `int`), `Int64` (64-bit, `long`), `Float` (64-bit,
`double`), `Bool`, `String`, `()` (unit).

Generic stdlib types:
- `Result<T, E>` — success or failure
- `Option<T>` — present or absent
- `List<T>` — ordered immutable collection

Records — immutable, constructed with `{ field = value, ... }`:

```overt
record User {
    id:     Int,
    name:   String,
    active: Bool,
}

let u = User { id = 1, name = "ada", active = true }
```

Enums — closed sums, fully-qualified variant references:

```overt
enum Status {
    Pending,
    Shipped,
    Delivered,
}

@derive(Debug)
enum OrderError {
    OutOfStock   { sku: String },
    InvalidPrice { cents: Int },
}

let s = Status.Pending
let e = OrderError.OutOfStock { sku = "A-1" }
```

Tuples:

```overt
let pair: (Int, String) = (1, "hi")
```

**Every `let` requires an explicit type annotation.** Missing annotations
fire **OV0314**. The rule serves two purposes: it prevents silent
re-inference when an upstream function's return type changes, and it
keeps each line self-describing so an agent reading a fragment out of
context still knows what each binding is.

```overt
let x: Int = 42                          // correct
let mut counter: Int = 0                 // correct
let _: Result<(), IoError> = println(s)  // correct — annotated discard
let x = 42                               // OV0314
```

**Exemption: destructuring patterns.** `let (a, b) = expr` and other
pattern bindings are exempt from OV0314 — there's no tuple-type annotation
syntax yet, and the individual bindings each carry their type at the use
site. Plain-identifier `let` always requires the annotation.

---

## 4. Type aliases and refinements

Non-generic aliases are transparent for equality — `Age` and `Int` compare
equal, distinguished only by a refinement predicate:

```overt
type Age = Int where 0 <= self && self <= 150
```

Literal crossings into a refined type are checked at compile time in the
decidable fragment (numeric / boolean / string comparisons, `&&`, `||`, `!`,
`self` references) via **OV0311**.

**Don't write chained comparisons.** `0 <= self <= 150` is a parse error
(non-associative comparison). Spell it `0 <= self && self <= 150`.

Generic aliases are nominal, not transparent:

```overt
type NonEmpty<T> = List<T> where size(self) > 0
```

**Runtime enforcement.** Every value crossing into a refinement runs
the predicate; failure throws `Overt.Runtime.RefinementViolation`
carrying the alias name, predicate source, and offending value. Generic
refinements check in the wrapper's implicit operator; non-generic
refinements check through a synthesized `__Refinements.{Alias}__Check`
helper the emitter wraps around boundary expressions (call args,
record-field inits, let initializers). Statically-proven-safe literal
values (decidable fragment → `true`) skip the runtime check.

---

## 5. Effect rows

Every function declares its effects as part of its signature:

```overt
fn pure_calc(n: Int) -> Int { n * 2 }
fn log_calc(n: Int) !{io} -> Int { println("n=${n}")?; n * 2 }
fn maybe_fetch(id: Int) !{io, async} -> Result<User, IoError> { ... }
```

Core effects: `io`, `async`, `inference`, `fails`.

Effect rows are **covering, not minimal** — if a function performs effect `X`,
`X` must appear in its row. Missing effects are **OV0310**. There is no
effect polymorphism notation in source today; the compiler approximates
effect-variable propagation through function-typed arguments.

---

## 6. Expressions

### if

```overt
let x: Int = if cond { 1 } else { 2 }

// else is optional when the body evaluates to ()
if cond { println("ok")? }
```

Both arms must produce the same type when there's an else. If else is absent,
the body must be `()`.

### match — exhaustive

```overt
match status {
    Status.Pending   => "waiting",
    Status.Shipped   => "moving",
    Status.Delivered => "done",
}

// Literal patterns on Int / Float / Bool / String — need a `_` arm.
match n {
    0  => "zero",
    1  => "one",
    -1 => "neg",
    _  => "other",
}

// Record destructure
match order {
    OrderError.OutOfStock { sku = s }   => s,
    OrderError.InvalidPrice { cents = c } => "bad price",
}

// Tuple match
match (state, event) {
    (State.Closed, Event.Open) => State.Listening,
    _                          => state,
}
```

Missing arms fire **OV0308**. Literal patterns don't contribute to
exhaustiveness (infinite domain); they require `_`.

### with — record update

```overt
let updated = u with { active = false }
```

Produces a new record; `u` is unchanged. There is no in-place mutation of
record fields.

### Block as expression

```overt
let v = {
    let a = compute()
    let b = other()
    a + b
}
```

The last expression is the value. Statements before it must end with the
previous newline or `;` (both accepted).

---

## 7. Statements

```overt
let x: Int = 42               // immutable binding
let mut counter = 0           // mutable rebinding of a single name
counter = counter + 1         // assignment — only valid on `let mut`
break                         // only inside a loop body
continue                      // only inside a loop body
```

`let mut` rebinds the **local name**, not the record's fields. Field-level
mutation doesn't exist; `with` copies instead.

Shadowing across nested scopes is rejected (**OV0201**). Patterns and locals
may reuse a prelude name (so `Some(v) => v` binds `v` without colliding with
the `None` stdlib symbol).

---

## 8. Control flow

```overt
// Collection iteration — must be a List<T>
for each x in xs {
    println("got ${x}")?
}

// Infinite loop — exits via break
let mut n = 0
loop {
    if n == 3 { break }
    n = n + 1
}

// Condition loop
while m < 10 {
    if m == 5 { m = m + 1; continue }
    m = m + 1
}
```

`break`/`continue` outside a loop body fire **OV0312**. `for each` on a
non-`List` iterable fires **OV0313**.

---

## 9. Errors as values

### Result and `?`

```overt
fn try_parse(s: String) -> Result<Int, ParseError> { ... }

fn doubled(s: String) -> Result<Int, ParseError> {
    let n: Int = try_parse(s)?   // propagates Err as early return
    Ok(n * 2)
}
```

The `?` operator works only when the enclosing function returns
`Result<_, E>` with a matching `E`. An ignored `Result<_, _>` in statement
position is **OV0307** — every `Result` must be consumed via `?`,
`let _ = ...`, or `match`.

### Pipe-propagate

```overt
ids |>? par_map(fetch_user) |> filter(is_active) |> map(get_name) |> Ok
```

`|>?` unwraps `Result<T, E>` to `T` along the pipe, propagating `Err` to the
enclosing function's return. `|>` is the infallible form.

### `?` in if-expression arms works

```overt
let n: Int = if cond { choose(true)? } else { choose(false)? }
```

This lowers to statement-level C# (`int n; if (cond) { ... } else { ... }`)
so the `?`-hoist's early-return reaches the enclosing function directly.
Requires the `let` to have a type annotation and a single identifier target
(not tuple destructuring); both arms have the hoist applied independently
so only the chosen branch's operand evaluates.

`?` in other conditionally-evaluated positions — nested deep inside a call
argument within an if-arm, say — may still fall back to `.Unwrap()`. If
you're in doubt, bind the `?` to a let first, then use that in the larger
expression.

---

## 10. Calls and pipes

**All multi-argument calls must use named arguments.** Positional is only
legal on single-argument calls. Mixing is not allowed.

```overt
concat_three(first = a, middle = b, last = c)    // correct
concat_three(a, b, c)                            // OV0154
insert(tree = t, value = 5)                      // correct
foo(42)                                          // correct (single arg)
```

Pipe composition splices the piped value as the **first** argument:

```overt
xs |> filter(is_even)            // filter(xs, is_even)
xs |> fold(seed = 0, step = add) // fold(xs, seed=0, step=add)
```

`|>?` is the propagating form: it splices as first arg AND unwraps the
`Result`, early-returning `Err` from the enclosing function:

```overt
ids |>? par_map(fetch_user) |> filter(is_active) |> map(get_name) |> Ok
```

**Pipes are an expert idiom, not the canonical form for sequencing.** Each
arrow carries two implicit transformations (positional splice + optional
unwrap) that you must mentally simulate at each step. For the common
"do step A, then step B, then step C" shape, prefer named `let` bindings:

```overt
// Canonical for agent RWRA — each step explicit, no implicit operations.
let filtered: List<Int> = filter(xs, is_even)
let squared:  List<Int> = map(filtered, square)
let total:    Int       = fold(squared, seed = 0, step = add)
```

Reach for pipes only when the intent is genuinely pipeline-shaped (a single
value flowing through uniform transformations), and when the readability
win of seeing the flow as a single expression outweighs the cost of the
implicit splice/unwrap mechanics. For anything with meaningful intermediate
values worth naming, the three-let form is what to produce.

There is **no method-call syntax**. Dots mean record field access or
module-qualified stdlib lookup (`List.empty`, `Trace.subscribe`), nothing
else.

---

## 11. Stdlib surface (runnable subset)

### Result / Option

```overt
Ok(value)            // -> Result<T, E>
Err(error)           // -> Result<T, E>
Some(value)          // -> Option<T>
None                 // -> Option<T>
```

### I/O

```overt
println(line: String) !{io} -> Result<(), IoError>
eprintln(line: String) !{io} -> Result<(), IoError>
```

### Lists

```overt
List.empty() -> List<T>                              // needs context for T
List.singleton(value: T) -> List<T>
List.concat_three(first: List<T>, middle: List<T>, last: List<T>) -> List<T>

size(list: List<T>) -> Int
len(list: List<T>) -> Int
length(s: String) -> Int

map(list: List<T>, f: fn(T) -> U) -> List<U>
filter(list: List<T>, pred: fn(T) -> Bool) -> List<T>
fold(list: List<T>, seed: U, step: fn(U, T) -> U) -> U

par_map(list: List<T>, f: fn(T) !{io, async} -> Result<U, E>)
    !{io, async} -> Result<List<U>, E>
```

`par_map` runs the callback concurrently (TPL) and returns the first `Err`
by original index on failure; order of the output list matches the input.

### Trace

```overt
Trace.subscribe(consumer: fn(TraceEvent) !{io} -> ()) !{io} -> ()
```

A `trace { ... }` block is a pass-through today — no events are actually
emitted. Subscribe works, but you won't see anything.

### Blessed stdlib (auto-discovered)

A set of facades for commonly-used BCL types ships with the compiler at
`stdlib/`. The CLI auto-discovers the stdlib directory (walking up from
the running `overt.exe`, or via `$OVERT_STDLIB` override), so
`use stdlib.<something>` just works out of the box.

```overt
use stdlib.csharp.system.io.path as path

fn main() !{io} -> Result<(), IoError> {
    let combined: String = path.combine_string_string(path1 = "dir", path2 = "file.txt")
    println(combined)?
    Ok(())
}
```

**Per-backend, not portable.** Stdlib lives under `stdlib/<backend>/*`.
Today only `stdlib/csharp/*` exists (the only backend emitting code).
When Go and TypeScript backends arrive, `stdlib/go/*` and `stdlib/ts/*`
sit beside it — each with its own facades bound to that backend's native
ecosystem. There is no portable-across-backends stdlib. Agents
retargeting a program to another backend rewrite it; humans shouldn't
be trying to write cross-backend Overt by hand. The same split applies
to tooling: `overt bind`, `overt run`, debug mapping, and host-source
inspection are all per-backend. `overt fmt` and the Overt-level emit
stages (`tokens`, `ast`, `resolved`, `typed`) are shared. See
DESIGN.md §19 and §20 for the rationale.

Currently shipped (all under `stdlib.csharp.system.*`, mirroring .NET's
own `System.*` namespaces):
- `stdlib.csharp.system.io.path` — pure string manipulation
- `stdlib.csharp.system.io.file` — file I/O (Result-wrapped)
- `stdlib.csharp.system.math` — pure math
- `stdlib.csharp.system.environment` — env vars, system info
- `stdlib.csharp.system.guid` — GUID utilities
- `stdlib.csharp.system.convert` — type conversions
- `stdlib.csharp.system.console` — console I/O

These are regenerated by running `overt bind --type <CLR type>
--module <module path> --output stdlib/<path>.ov`. See `overt bind --help`.

### FFI — C# extern bindings

`extern "csharp" [kind] fn` binds a host-language member. The optional
kind keyword — `instance` or `ctor` — selects the call shape; without one,
the extern is static. The binds target is always a dotted path; it never
encodes the shape. Grammar:

```
extern "csharp"               fn <name>(...) -> T    binds "Ns.Type.Member"   // static (default)
extern "csharp" instance      fn <name>(self: T, …) binds "Ns.Type.Member"   // instance
extern "csharp" ctor          fn <name>(…) -> T     binds "Ns.Type"           // constructor
```

```overt
// Static method / property / field. Dotted path, last segment is the member.
extern "csharp" fn path_combine(a: String, b: String) -> String
    binds "System.IO.Path.Combine"

extern "csharp" fn machine_name() !{io} -> String
    binds "System.Environment.MachineName"

// Effectful — Result return causes the extern runtime to wrap exceptions
// as Err(IoError { narrative = <exception message> }).
extern "csharp" fn read_all_text(path: String) !{io, fails} -> Result<String, IoError>
    binds "System.IO.File.ReadAllText"
```

Static properties and fields emit as bare member access (no `()`); the
runtime detects this via reflection against the binds target. Methods
emit with arguments.

**Opaque types, instance methods, constructors.** For stateful BCL types
(`StringBuilder`, `HttpClient`, etc.), declare an `extern type` first, then
use `ctor` for constructors and `instance` for methods/properties. The
binds target stays a plain dotted path in both cases:

```overt
extern "csharp" type StringBuilder binds "System.Text.StringBuilder"

extern "csharp" ctor fn sb_new() -> StringBuilder
    binds "System.Text.StringBuilder"

extern "csharp" instance fn sb_append(self: StringBuilder, s: String) -> StringBuilder
    binds "System.Text.StringBuilder.Append"

extern "csharp" instance fn sb_to_string(self: StringBuilder) -> String
    binds "System.Text.StringBuilder.ToString"
```

`instance fn` requires `self` as the first parameter (OV0315 otherwise);
the emitter drops it from the argument list and uses it as the C# call
receiver: `self.Append(s)`. `ctor fn` requires a return type — the
constructed type (OV0316 otherwise) — and emits `new T(args)`.

`overt bind` generates all of the above automatically for public types —
constructors, static members, and instance methods get emitted in one pass
with the correct kind keywords.

**Cross-type references.** For methods that take or return other opaque
types, register them with `--with-opaque`:

```
overt bind --type System.IO.StreamReader \
           --module stdlib.csharp.system.io.streamreader \
           --with-opaque System.IO.Stream=stdlib.csharp.system.io.stream \
           --output stdlib/csharp/system/io/streamreader.ov
```

Each `--with-opaque <FullName>[=<module>]` tells the generator: "this
other type is available as an opaque reference." If a module path is
provided, the generated facade emits `use <module>.{<Name>}` at the top
so consumers don't need to import the cross-type by hand. Without a
module path, the type is registered for rendering but the consumer
supplies its own import. Repeatable.

Auto-exception conversion works for error types with a single `narrative`
string constructor (currently only `IoError`). Other error types rethrow;
map them to `IoError` via a wrapper fn until richer mappings land.

**Generate facades with `overt bind`:**

```
overt bind --type System.IO.Path --module path --output facades/path.ov
```

This reflects on the .NET type and emits extern declarations for its public
static methods. Effect rows come from a curated namespace table (pure for
`System.Math`, `System.String`, `System.IO.Path`; `!{io, fails}` for
`System.IO.*`, `System.Console.*`, etc.; `!{io, async, fails}` for
`System.Net.*`). Overload collisions become `name_<arity>` (e.g.
`combine_2`, `combine_3`, `combine_4`). Parameters or returns the
generator can't map cleanly (spans, arrays, custom types) emit as
`// skipped` comments.

### FFI — C extern bindings

`extern "c" fn` parses but is **not wired at runtime yet** — P/Invoke
integration is a later milestone. For now, use a C# extern as an
intermediary (declare a C# static method that calls the C function, bind to
that).

```overt
extern "c" fn c_strlen(s: CString) -> Int    // parses but throws at runtime

fn strlen(s: String) -> Int {
    let cs: CString = CString.from(s)
    unsafe { c_strlen(cs) }
}
```

---

## 12. Formatting and naming

- Two-space indent. Not indentation-significant (C-family braces).
- Identifiers: `snake_case` for values and functions, `PascalCase` for types,
  `SCREAMING_SNAKE_CASE` nowhere in particular — Overt doesn't have
  constants distinct from `let`.
- Record and variant field names are lowercase: `IoError { narrative = "..." }`,
  not `Narrative`.
- One canonical form is enforced by `overt fmt <file>`. Pass `--write` to
  update in place. The formatter is idempotent and comment-preserving.

---

## 13. What doesn't work yet

**If you try these, you will get an error or a runtime failure. Don't.**

- **`overt fmt` is still single-file.** It will format a file that has
  `use` declarations locally but won't touch the imported modules. For
  multi-file work, run `overt fmt` per file.
- **FFI calls at runtime.** `extern` compiles; invocation throws.
- **`trace { ... }` emission.** The block is pass-through; no events fire.
- **`?` nested deep inside a call argument within an if/match arm used as a value.**
  Direct `if { foo()? }` in a let initializer works (stmt-level lowering).
  Buried in a call like `foo(if cond { bar()? } else { baz })` it may fall
  back to `.Unwrap()` which throws. Lift the `?` into a let if you need
  guaranteed propagation.
- **Return-value refinement checks.** Boundary-wrapping fires at call
  args, let initializers, and record field inits. A refinement violation
  in a function's return expression is not currently detected at runtime;
  add a call-site binding if you need the guarantee (`let r: Refined = f(x)`
  triggers the let-boundary check).
- **`f64` literal patterns in `match`.** Parse OK but don't fire — float
  equality isn't a well-defined match.
- **Block comments (`/* ... */`).** Only line comments (`//`) work.
- **Module-system-aware package management.** No `import`, no `cargo`-style
  manifest.

---

## 14. Diagnostic codes

Every error comes with `help:` text naming the fix; this table is a quick
reference. Codes are stable.

| Code | Stage | Meaning | Fix |
|------|-------|---------|-----|
| OV0001–0003 | lex | malformed literal, unterminated string, unknown char | follow lexical.md |
| OV0102–0103 | lex | interpolation errors | balance `${...}` |
| OV0150 | parse | unexpected token at top level | check declaration shape |
| OV0151 | parse | expected effect name | spell the effect word |
| OV0152 | parse | expected type | use `Int`/`String`/`Result<...>` etc. |
| OV0153 | parse | tuple types unsupported in declarations | use a record instead |
| OV0154 | parse | positional arg in multi-arg call | use `name = value` |
| OV0155 | parse | malformed pattern | see OV0158 guidance |
| OV0156 | parse | malformed interpolation | balance `${...}` / `$ident` |
| OV0157 | parse | generic structural errors | consult the reported span |
| OV0158 | parse | expected pattern | `_`, identifier, path, `Name(..)`, `Name {..}`, tuple, literal |
| OV0159 | parse | unit pattern `()` unsupported | bind or ignore with `_` |
| OV0160 | parse | duplicate field / variant / parameter | rename |
| OV0161 | parse | `let mut` with non-identifier pattern | mutable bindings take a single name |
| OV0162 | parse | missing comma between match arms | add `,` |
| OV0169 | parse | file missing its `module <name>` header | add `module <name>` as the first line |
| OV0170 | parse | stray `;` | remove it; newlines separate statements |
| OV0200 | resolve | unknown name | check spelling; did-you-mean suggested |
| OV0201 | resolve | shadowed name | rename; no shadowing across nested scopes |
| OV0300 | type | argument type mismatch | match the parameter type |
| OV0301 | type | return type mismatch | match the declared return |
| OV0302 | type | field type mismatch | match the record's field type |
| OV0303 | type | arm / branch type mismatch | both arms must produce the same type |
| OV0304 | type | condition must be Bool | wrap in comparison or Bool-returning call |
| OV0306 | type | argument count mismatch | match the signature's arity |
| OV0307 | type | ignored `Result<_, _>` | use `?`, `let _ = expr`, or `match` |
| OV0308 | type | non-exhaustive match | add missing arms or a `_` |
| OV0310 | type | function performs an undeclared effect | add it to the effect row |
| OV0311 | type | refinement violated at literal boundary | change the literal or widen the predicate |
| OV0312 | type | `break`/`continue` outside loop | only valid in while / for each / loop |
| OV0313 | type | `for each` on non-`List` | convert to a `List<T>` first |
| OV0314 | type | `let` without type annotation | add `: <Type>` after the binder |
| OV0315 | resolve | `extern instance fn` without `self` first parameter | add `self: <Type>` as the first parameter |
| OV0316 | resolve | `extern ctor fn` without return type | add `-> <Type>` — the constructed type |

---

## 15. Canonical templates

**Fallible function with I/O:**
```overt
fn read_and_double(s: String) !{io} -> Result<Int, IoError> {
    let raw: Int = try_parse(s)?
    println("parsed ${raw}")?
    Ok(raw * 2)
}
```

**Tree-walking interpreter shape** (see `examples/arith_eval.ov`):
```overt
enum Expr {
    Lit { value: Int },
    Bin { op: BinOp, left: Expr, right: Expr },
}

fn eval(expr: Expr) -> Result<Int, EvalError> {
    match expr {
        Expr.Lit { value = v } => Ok(v),
        Expr.Bin { op = op, left = l, right = r } => {
            let lv: Int = eval(l)?
            let rv: Int = eval(r)?
            apply(op = op, left = lv, right = rv)
        }
    }
}
```

**Collection pipeline:**
```overt
fn summarize(xs: List<Int>) -> Int {
    xs
      |> filter(is_positive)
      |> map(square)
      |> fold(seed = 0, step = add)
}
```

**Parallel failing pipeline:**
```overt
fn all_users(ids: List<UserId>) !{io, async} -> Result<List<User>, LoadError> {
    ids |>? par_map(fetch_user) |> Ok
}
```

**State machine:**
```overt
fn transition(state: State, event: Event) -> Result<State, TransitionError> {
    match (state, event) {
        (State.Closed, Event.Open)    => Ok(State.Listening),
        (State.Listening, Event.Data) => Ok(State.Receiving),
        _                             => Err(TransitionError.Invalid { from = state, event = event }),
    }
}
```

---

**When in doubt, prefer the shape in `examples/`.** Every example there
compiles; if a pattern isn't shown in an example, it may not be supported
yet.
