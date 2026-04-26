# Foreign function interface design

This document scopes Overt's FFI surface for the Go back end past the
per-method `extern "go" fn` baseline that already ships. It is the
companion to `docs/concurrency.md`: where that doc lays the design
space for goroutines, channels, and `select`, this one covers
**opaque host types, pointers, function-typed extern parameters, and
method calls on host values** — the four design points that surfaced
when scoping Phase 1 of the chat-relay sample.

The C# back end already has answers for most of these (the BCL is
deeply integrated, with `extern "csharp" use` doing reflection-driven
bulk-import and `extern "csharp" instance fn` for receiver methods).
The Go target needs equivalent capabilities, but Go's type system is
different enough that direct copying isn't always right.

The structure mirrors `docs/concurrency.md`: each subsection states
the requirement, surveys the realistic options, picks a leading
proposal, and lists open questions. No code yet; design first.

## 1. Goals

In rough priority order:

1. **Allow Phase 1 of the chat-relay sample to be written in Overt.**
   Single-connection echo server using `net/http` and a websocket
   library. No language-arc concurrency features; just FFI.
2. **Make the FFI honest.** A reader who sees `extern "go" fn ...`
   should know which calls reach into Go and which stay in Overt.
   Effect rows must propagate; nil-pointer / panic risks must be
   visible.
3. **Stay close to Go's idiom on the host side.** A Go developer
   reading the emitted shim should recognize it as ordinary Go.
   Surprising adapter patterns are a maintenance tax forever.
4. **Don't lock in design decisions for the bigger problems.** This
   doc covers the per-method extern story (and the `extern "go" use`
   bulk-import that follows from it). It does not commit to how
   generics, traits, channels-across-FFI, or async/await across the
   boundary should work; those have their own design arcs.

## 2. Opaque host types

### Requirement

`http.HandleFunc(pattern, handler)` is a method that takes
`http.ResponseWriter` (a Go interface) and `*http.Request` (a pointer
to a Go struct). To call it from Overt, both types have to exist on
the Overt side as named entities the type checker accepts.

The Overt side cannot inspect Go's interface contract or struct
fields without `go/types` reflection work that this doc deliberately
defers (it's the bulk-import question, §8). What's needed first is
the ability to **name a Go type opaquely**, pass values of it
through Overt, and hand them back into Go.

### Options

- **A. Extend the existing `extern "go" type` form.** The parser
  already recognizes `extern "<platform>" type Name binds "..."`. The
  GoEmitter needs `LowerType` to handle a `NamedType` that resolves
  to such a declaration. Today that case throws.
- **B. Inline binds-strings everywhere.** Skip `extern "go" type`;
  let users write the Go-side type directly in fn signatures via
  some new syntax. Doesn't compose with the rest of the language.
- **C. Synthetic Overt records that mirror Go's struct shape.** Read
  Go's struct definitions and generate Overt records. Only works for
  exported-field structs; doesn't handle interfaces or pointers.

### Leading proposal

Approach **A**. Source-level shape:

```overt
extern "go" type Request binds "*net/http.Request"
extern "go" type ResponseWriter binds "net/http.ResponseWriter"
```

The binds-string IS the Go-side spelling, including pointer markers
(`*`) and any qualifiers. The Overt side gets a name to pass around;
the GoEmitter's `LowerType` returns the binds-string verbatim
whenever it sees `NamedType("Request")` and that name resolves to an
extern type declaration.

This means the binds-string is the source of truth; Overt makes no
attempt to model "is this a pointer," "is this an interface," or
"does this implement that." The host's type system handles
correctness at `go build` time.

**Implementation cost:** small. The parser already produces
`ExternTypeDecl`. The Go back end needs:

1. A pass that collects all `extern "go" type` declarations into a
   dictionary at module emit start.
2. `LowerType` for `NamedType` falls back to that dictionary before
   throwing.
3. The Overt-side type checker treats these as opaque named types
   (no field access, no method dispatch — those go through the
   per-method extern declarations from §5).

### Open questions

- **Imports.** A binds-string like `"*net/http.Request"` mentions the
  `net/http` package. The GoEmitter has to add `net/http` to its
  import set. Parsing the binds-string for a package path is
  doable: strip the leading `*`, take everything before the last
  `.`. Same convention as `extern "go" fn binds "package.Member"`.
- **Type-equivalent comparisons.** Two Overt types declared with the
  same binds-string are not type-equal in Overt's name-based system.
  Probably fine for v1; surfaces as a "you have to spell the type
  name once and reuse it" rule.
- **Generics.** `[]string` and `map[string]int` aren't Go type names
  per se; they're type expressions. The leading proposal handles
  them by allowing arbitrary binds-strings (`"[]string"`,
  `"map[string]int"`), but composition with Overt-side generics
  isn't possible. Workaround: declare the specific instantiation as
  its own opaque type. Real fix is the bulk-import work (§8).

## 3. Pointers

### Requirement

Half of Go's API surface uses pointers. `*http.Request`, `*sql.DB`,
`*websocket.Conn`. The Overt side has to round-trip values of these
types through fn calls without misinterpreting them.

Today Overt has `Ptr<T>` in the runtime as a placeholder; no
language code uses it.

### Options

- **A. Pointers are encoded in binds-strings.** As proposed in §2:
  `extern "go" type Request binds "*net/http.Request"`. Overt sees
  one opaque type; Go sees the pointer. No language-level pointer
  feature.
- **B. Pointers as a first-class type.** `Ptr<T>` becomes a real
  language feature. `extern "go" type Request binds "net/http.Request"`
  declares the value type; users wrap it as `Ptr<Request>` for
  pointer values. Distinguishes pass-by-value from pass-by-reference
  at the Overt level.
- **C. Pointers as a refinement / annotation.** A `&` prefix in type
  expressions, à la Rust references. Same idea as B but in syntax.

### Leading proposal

Approach **A**. Pointers are part of the binds-string, not the
language.

**Rationale.** Overt's design rejects pointer-arithmetic-shaped
features at the language level (DESIGN.md §13: no literal integer
indexing, zero-cost iteration). Adding `Ptr<T>` would put pointers
back into the abstract interface and immediately raise questions
(can you dereference? can you compare? null-checking?) that don't
have good answers given the rest of the language's stance.

The pragmatic alternative is "the host says what shape the type is;
Overt just passes it around." That keeps the language clean and
delegates pointer-ness to the FFI boundary, where pointers are real
and nullable and need careful handling regardless.

**Cost of being wrong:** if Overt later wants to express pointer-
specific operations (deref, equality, arithmetic), `Ptr<T>` can
graduate. The opt-in path is non-breaking. The opt-out path (decide
later that we don't want pointers as a language feature) is breaking,
so leaving them out of the language is the strictly more reversible
choice.

### Open questions

- **Nil handling.** A Go function that returns `*http.Request` can
  return nil. Overt's type system has no nullable concept by default;
  every type is non-null. Should Overt-side declarations of nullable
  Go returns be `Option<Request>`? The C# back end already has the
  "Nullable → Option" convention for annotated types (AGENTS.md §11.7);
  the Go target should match. **Recommended**: yes; opt-in via an
  attribute on the extern declaration or by manual signature
  authorship. Auto-detection comes with bulk-import (§8).
- **Equality.** Two `*http.Request` pointers are equal iff they
  point at the same struct. Overt has structural equality on records
  but no pointer-equality concept. For v1, no operator-level equality
  on extern types; force the user to call into a Go-side comparator
  if they care. Most production code doesn't.

## 4. Function-typed extern parameters

### Requirement

`http.HandleFunc("/echo", handler)` takes a Go function value as its
second argument. To express that in Overt, the extern fn's parameter
needs to be a function type, and the GoEmitter has to lower the
Overt FunctionType to a Go `func(...)` signature in the shim.

The Overt type system already has `FunctionTypeRef`. The C# back
end uses it for stdlib things like `Trace.subscribe(consumer:
fn(TraceEvent) !{io} -> ())`.

### Options

- **A. Direct lowering.** Overt FunctionType → Go `func(...) T`.
  Effect rows are erased at the boundary (Go has no effect concept).
  Overt-side named fns pass by name; the Go shim receives a Go
  function value.
- **B. Wrapped lowering.** Overt FunctionType lowers to a struct
  `OvertFn[Args, Ret]` carrying the Go function pointer plus
  bookkeeping (effect tags, stack metadata). More expressive; more
  expensive.
- **C. Forbid function-typed externs entirely.** Force users to
  write a Go-side shim that exposes a flat-arg surface. Simpler;
  pushes adapter code outside the language.

### Leading proposal

Approach **A**. Concrete shape:

```overt
extern "go" fn http_handle(
    pattern: String,
    handler: fn(ResponseWriter, Request) -> ()
) !{io} -> () binds "http.HandleFunc"
```

GoEmitter lowering:

```go
func http_handle(pattern string, handler func(http.ResponseWriter, *http.Request)) {
    http.HandleFunc(pattern, handler)
}
```

The shim forwards every parameter directly. The function-typed
parameter becomes a Go function-type parameter in the shim signature;
calling it inside a `extern "go" fn` body just emits the parameter
name verbatim followed by an argument list, which Go accepts as
ordinary function-value invocation.

When the user passes an Overt-side named fn `my_handler` as the
argument, the existing IdentifierExpr emit produces `my_handler` in
Go. The Overt fn already emits as a Go fn at the same name (the
GoEmitter writes `func my_handler(...)` for the Overt declaration),
so `http_handle("/echo", my_handler)` works as long as the
signatures match.

**Effect-row erasure at the boundary.** An Overt fn `fn handler(...)
!{io} -> ()` lowers to a Go `func(...)` with no effect surface
(panics propagate as Go panics; I/O is unmarked). The Overt
type-checker still validates that calling such a fn from a context
without `!{io}` is an error, so the type-level safety holds; the
runtime guarantee is the host's.

### Open questions

- **Closures.** Overt has no inline-lambda syntax today (§11 of
  AGENTS.md). All function values are named-fn references.
  Function-typed externs work fine for that; chat-relay Phase 1 has
  one named handler, so no closures needed. The day Overt grows
  closures, the captured environment becomes an emission concern
  (Go closures over local vars work natively; this is mostly an
  Overt-side question of how captures resolve).
- **Multiple-return-value Go fns.** Many Go fns return `(T, error)`.
  When the Overt-side function-type parameter passes through to a Go
  shim, can it return that pair? Probably needs to be modeled as
  `Result<T, E>` with an explicit conversion. Out of scope for
  function-typed *extern parameters*; relevant for the broader return-
  type story.

## 5. Method calls on host values

### Requirement

`conn.ReadMessage()`, `r.URL.Path`, `w.Header().Set(...)`. The
Overt-side caller wants to invoke a method on a value of an opaque
extern type, just like they'd invoke a method on an Overt record's
type-checker-routed namespace fn.

### Options

- **A. `extern "go" instance fn`.** Mirror the existing C#
  `extern "csharp" instance fn` form. Each method has its own
  declaration; the first parameter is `self`. Method-call syntax
  routes through it via the type checker's `MethodCallResolutions`.
- **B. Member access via FieldAccessExpr lowering.** The GoEmitter
  recognizes `value.Member()` on an extern type and emits it
  verbatim as Go's dot-access. No declaration needed; the type
  checker has to allow open-ended member access on extern types.
- **C. `extern "go" use "package.Type"`.** Bulk-import the type's
  full method surface. Reflection-driven; produces the entire
  facade. Larger feature.

### Leading proposal

Approach **A** for v1, with **C** as the natural follow-up.

Concrete shape:

```overt
extern "go" type Conn binds "*github.com/gorilla/websocket.Conn"

extern "go" instance fn read_message(self: Conn) !{io} -> Result<String, IoError>
    binds "ReadMessage"

extern "go" instance fn write_message(self: Conn, msg: String) !{io} -> Result<(), IoError>
    binds "WriteMessage"
```

Note `binds "ReadMessage"` is just the Go method name, not the
package-qualified form, because `instance fn` already pins the
receiver type.

GoEmitter lowering:

```go
func read_message(self *websocket.Conn) (string, error) {
    return self.ReadMessage()
}

func write_message(self *websocket.Conn, msg string) error {
    return self.WriteMessage(...)
}
```

The Overt-side caller writes `conn.read_message()`; the type checker
routes that to `read_message(self = conn)` via the existing
method-call mechanism (already used for `String.chars()`,
`xs.map(f)`, etc.).

**Why not B.** The type checker doesn't have access to Go's method
table. Allowing open-ended member access on extern types means the
emitter would emit *whatever the user wrote* and let `go build`
catch the typos. That's worse for diagnostics (you get a Go-side
error far from the Overt source) and worse for Overt's "errors
discoverable in Overt vocabulary" stance.

**Why not C yet.** Bulk-import requires `go/types` reflection in a
Go-side helper that the Overt compiler shells out to. Substantial
infrastructure work. The per-method form here gets us to chat-relay
Phase 1 without it.

### Open questions

- **Embedded methods / promotions.** Go's struct embedding promotes
  embedded methods to the outer type. The per-method form here
  doesn't auto-discover them; users would write the binding for
  whichever method they call, regardless of where it's defined. Fine
  for v1.
- **Method values.** `conn.ReadMessage` (without parens) is a Go
  method value — a function-typed reference. Out of scope for v1;
  rare in practice, awkward to model.
- **Generic methods.** Go 1.18+ supports type parameters on methods.
  Out of scope; rare on the surface area chat-relay Phase 1 needs.

## 6. Boundary semantics

### Requirement

When Go code panics, what happens to the Overt caller? When a Go fn
returns `(T, error)`, how does that map to `Result<T, E>`?

### Panics

Go panics propagate through the call stack. From Overt's view, a
panic in a Go-side `extern fn` looks like an unrecoverable runtime
error. The C# back end's policy is "uncaught exceptions become Go
panics on the C# back end" — which is to say, no automatic
conversion in either direction.

**Leading proposal**: same. No `defer recover()` is injected
automatically. An extern declared `!{fails}` is a hint to the
human (and the type checker) that the call may fail; the Overt
runtime makes no guarantee. Users who want panic-safety wrap the
call in a hand-written Go shim that uses `defer recover()` and
returns a `Result`.

**Rationale.** Auto-injection would penalize every extern call
with a `defer` overhead. The shim pattern is explicit, opt-in, and
matches how Go developers handle panic boundaries in real code.

### `(T, error)` return

Go's idiomatic dual return is `(T, error)`. Overt's `Result<T, E>`
is the natural target. The conversion has to happen *somewhere*.

**Leading proposal.** When an `extern "go" fn`'s declared Overt
return type is `Result<T, E>` and the Go-side bound function
returns `(T, error)`, the GoEmitter shim does the conversion:

```go
func read_message(self *websocket.Conn) overt.Result[string, overt.IoError] {
    msg, err := self.ReadMessage()
    if err != nil {
        return overt.Err[string, overt.IoError](overt.IoError{Narrative: err.Error()})
    }
    return overt.Ok[string, overt.IoError](msg)
}
```

The shim recognizes the Go signature shape (last return is `error`)
and inserts the err-check / wrap pattern. If the Overt return type
isn't `Result<T, E>`, the shim returns the pair as-is (which fails
to compile, surfacing as a clear error).

**Open question.** What `E` type does the err-wrap target? Most Go
functions return `error` as a generic interface; the natural Overt-
side mapping is `IoError`. Custom error types would need a manual
shim. **Recommended for v1**: hardcode the IoError target; users
who want richer error types write their own shim.

### Nil pointer

A Go function that returns a nil `*T` produces a Go value Overt
can't safely dereference. The C# back end's nullable-→-Option
convention (AGENTS.md §11.7) is the right shape; the Go target
needs the same.

**Leading proposal**: opt-in via the Overt-side declaration. If the
declared return is `Option<T>` and the Go return is `*T`, the shim
checks for nil:

```go
func find_handler(name string) overt.Option[*Handler] {
    h := lookup(name)
    if h == nil {
        return overt.None[*Handler]()
    }
    return overt.Some[*Handler](h)
}
```

### Effect rows

`extern "go" fn ... !{io}` declares the fn performs I/O. This is a
type-checker constraint (callers must be in `!{io}` context); it
has no runtime effect on the Go side. Same for `async`, `inference`,
`fails`. Effect rows are erased at the FFI boundary.

## 7. Summary of leading proposals

| feature | shape | implementation cost |
| --- | --- | --- |
| Opaque host types | `extern "go" type Name binds "package.Type"`; binds-string is verbatim Go type expression including pointers | 3–4 days |
| Pointers | encoded in binds-strings; no first-class `Ptr<T>` | (covered above) |
| Function-typed extern params | Overt FunctionType lowers to Go `func(...)`; Overt named fns pass by name | 3–4 days |
| Method calls on host values | `extern "go" instance fn` mirroring the C# pattern | 3–4 days |
| Panics | propagate as Go panics; no auto-recover; `!{fails}` is documentation | 0 (status quo) |
| `(T, error)` → `Result<T, E>` | shim does the conversion when the Overt return is `Result<T, IoError>` | 1–2 days |
| Nil pointer → Option | shim checks nil when the Overt return is `Option<T>` and Go return is `*T` | 1–2 days |
| Bulk-import | `extern "go" use "package.Type"` via `go/types` reflection helper | 2–3 weeks (separate doc, separate session) |

Total for v1 (chat-relay Phase 1 reachable): **~2 weeks of focused
work**, in roughly the order above. Each row is a self-contained
commit-or-three.

## 8. Acceptance criteria

The design is ready when chat-relay Phase 1 can be written end-to-
end in Overt. Concretely:

```overt
module chat_relay

extern "go" type ResponseWriter binds "net/http.ResponseWriter"
extern "go" type Request binds "*net/http.Request"
extern "go" type Conn binds "*github.com/gorilla/websocket.Conn"

extern "go" fn http_handle(pattern: String, handler: fn(ResponseWriter, Request) -> ())
    !{io} -> () binds "http.HandleFunc"

extern "go" fn http_listen_and_serve(addr: String) !{io} -> Result<(), IoError>
    binds "http.ListenAndServe"

extern "go" fn upgrade(w: ResponseWriter, r: Request) !{io} -> Result<Conn, IoError>
    binds "upgrader.Upgrade" from "..."

extern "go" instance fn read_message(self: Conn) !{io} -> Result<String, IoError>
    binds "ReadMessage"
extern "go" instance fn write_message(self: Conn, msg: String) !{io} -> Result<(), IoError>
    binds "WriteMessage"

fn echo_handler(w: ResponseWriter, r: Request) -> () {
    let conn: Conn = upgrade(w = w, r = r) ?? return ()
    loop {
        let msg: String = conn.read_message() ?? return ()
        let _ = conn.write_message(msg = msg)
    }
}

fn main() !{io} -> Result<(), IoError> {
    http_handle(pattern = "/echo", handler = echo_handler)
    http_listen_and_serve(addr = ":8080")
}
```

(The `??` operator is illustrative; the actual error-handling
shape uses match or `?` with `Result`-returning fns.)

The acceptance test is: this program emits, builds with `go build`,
and runs as a single-connection echo websocket server. A
`websocat ws://localhost:8080/echo` round-trip confirms the
pipeline.

## 9. What this doc deliberately doesn't cover

- **Bulk-import (`extern "go" use "package"`).** Mentioned as a
  follow-up; gets its own design pass once the per-method form is
  stable.
- **Channel types crossing the FFI boundary.** Whether Overt's future
  `Channel<T>` (concurrency design) can be passed to a Go fn
  expecting a `chan T`. Defer until both halves of the design exist.
- **Generic method dispatch.** Go 1.18+ generics on methods. Rare
  on the chat-relay surface; defer.
- **Build-tag / per-platform externs.** `extern "go" fn ...` selected
  by GOOS. Defer; the existing `extern "<platform>"` form covers the
  current need.
- **C FFI from Go.** Overt's `extern "c"` already exists for the C#
  back end; the Go target's relationship to cgo is its own question
  with its own constraints.

## 10. Suggested next move

Implement the leading proposals in roughly this order, each as one
focused commit:

1. **`extern "go" type` lowering** (§2). LowerType handles named
   extern types via a binds-string registry; imports are extracted
   from the binds-string.
2. **Function-typed extern parameter lowering** (§4). LowerType for
   FunctionTypeRef; emit shape for shim signature; pass-by-name
   for Overt fn args.
3. **Result wrap for `(T, error)` returns** (§6). Shim recognizes
   the Go return shape and inserts the err-check.
4. **`extern "go" instance fn`** (§5). Parser may already accept
   it; emitter lowers to `func name(self T, args...) Ret` shim that
   calls `self.Method(args...)`.
5. **Nil-pointer → Option** (§6). Shim recognizes pointer return +
   Option declared, inserts nil-check.

After all five, write chat-relay Phase 1 as the acceptance test.
Each step also gets a small e2e test in
`GoBackendEndToEndTests` so the sweep tracks them.

The `extern "go" use` bulk-import is its own multi-week arc that
gets a follow-up design doc once the per-method foundation is
stable. That's the right time to also think about reusing the C#
back end's BindGenerator architecture for Go.
