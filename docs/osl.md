# Overt Standard Library (OSL)

> *"Library design is language design."* — Bjarne Stroustrup
>
> *"Within C++, there is a much smaller and cleaner language struggling to get out."* — Bjarne Stroustrup

## What this document is

A living spec for the Overt Standard Library. Authoritative for the surface,
the design rules, the candidate queue, and the audit trail of what got in,
what got rejected, and why.

Each operation lives in exactly one place in this doc — either a module
section (with status), the `## Candidates` queue, or the `## Rejected`
section. Promotions and rejections are PRs against this file.

---

## Design principles

### Generic-namespaced calls use Form 3.

Static / namespace-qualified calls on a generic type spell the type
arguments **on the type, before the dot**:

```overt
List<Int>.empty()
Map<String, Int>.empty()
Set<String>.empty()
NonEmpty<Int>.try_from(xs)
```

Form 3 (`Type<Args>.method(...)`) is the canonical and only-accepted
form for generic-typed namespace calls whose return type can't be
inferred from value-typed arguments. Earlier shorthand forms
(`List.empty()` with target-type inference; `List.empty<Int>()` with
method-level type args) were considered and rejected: Form 3 is
type-theoretically defensible (`List` alone is a type *constructor*,
not a type; `List<Int>` is the type the static method belongs to),
context-independent (works the same in let-init, call-arg, or any
expression position — no inference-where-does-it-fire mental model),
and grep-friendly (every construction has the type literally present
at the call site).

Where the type args can be inferred from value arguments, the call
stays unqualified — `List.singleton(value = 42)` infers `T = Int`
from the arg and doesn't need explicit type args. The Form-3 rule
applies only when the *namespace identifier is a generic type* and
the type args can't be reconstructed from the call's value args.

Unqualified prelude factories (`Ok`, `Err`, `Some`, `None`,
`println`, etc.) stay unqualified by design — they're explicitly
in the unqualified-prelude scope and the type info comes from the
call's value arg or surrounding context.

### Vocabulary is English; grammar is Latin.

The OSL is **promiscuous about what enters** and **conservative about how
each thing is shaped**. Borrow names and shapes from wherever they read
well — Rust, Haskell, Python, Scala, .NET BCL, Go's stdlib, whatever fits.
Don't invent in a vacuum. But every borrowed thing gets reshaped on the way
in to fit Overt's discipline:

- **`Result<T, E>`-wrapped fallibility.** Operations that can fail return
  `Result`; the host's exceptions convert at the boundary. No throwing into
  Overt code from stdlib.
- **Lowercase fields, named arguments.** `IoError { narrative = "..." }`,
  `File.read_to_string(path = "...")`. The host's casing convention doesn't
  leak.
- **Effect rows declared.** Every operation that touches the world carries
  the effect on its signature (`!{io}`, `!{async}`). Pure operations carry
  no row.
- **Immutable values.** No in-place mutation. `Map.insert(m, k, v)` returns
  a new map; the original is unchanged.
- **One canonical name.** When multiple host-language conventions disagree,
  pick one and stay with it. Synonyms are a v1 mistake we won't repeat
  (see *Cleanup* below).

### Foundational vs. rule of three.

Two paths into the OSL:

- **Foundational.** Operations on the basic data types (String, List, Map,
  Set, Int, Float) that any program will eventually need *and* that carry no
  domain assumption. These ship without waiting for evidence — every
  program eventually slices a string and sorts a list, and not having those
  in stdlib makes the language feel half-built. The full foundational set
  is enumerated under each module section below.
- **Rule of three.** Anything domain-specific (timing, randomness, env vars,
  math beyond arithmetic, network, JSON, regex). A candidate has to surface
  in **three independent programs** before it earns an OSL slot. Until
  then, programs hand-roll or use FFI; the cost is real but bounded.

The cut between the two is *general-purpose data manipulation* vs.
*carries a domain assumption*. `String.substring`, `List.sort`,
`List.zip` are general — every domain needs them. `Random.next`,
`Float.sqrt`, `Clock.now` carry a domain (sims, geometry, timing); they
wait for repeated demand.

### What stays out, by design.

Some categories don't enter the OSL regardless of how often programs reach
for them:

- **HTTP, JSON, regex, crypto, sockets, db drivers, image / audio / video
  codecs.** These are domain-specific *and* the host ecosystems are huge,
  well-maintained, and irreducibly different across backends. Programs use
  `extern "csharp" use "..."` / `extern "go" use "..."` and accept that
  they're portable only across hosts they have bindings for.
- **Reflection.** Rejected at the language level (DESIGN.md §4); no
  stdlib counterpart. Programs that need it rely on Overt's annotation /
  derive system (`@derive(Debug, Display)`) or hand-written code.
- **Macros / metaprogramming.** Same.

### When in doubt, leave it out.

Operations that read fine but have no motivating program don't enter the
OSL. They sit in `## Candidates` until at least three programs cite them.
The asymmetry is deliberate: it's much easier to add a missing operation
later than to remove a regretted one.

---

## How to propose a new operation

The spec doc is the proposal mechanism. No committee, no scheduled
meetings, no RFC process — just structured editing of this file with PRs
as the conversation.

1. Append a candidate block to `## Candidates` below. The format:
   ```
   ### List.zip<T, U>

   - **Signature:** `List.zip(left: List<T>, right: List<U>) -> List<(T, U)>`
   - **Semantics:** Truncate to the shorter list; no error path.
   - **Motivating programs:**
     - examples/foo.ov:42 — pairing flag names with values
     - samples/bar/main.ov:91 — pairing line numbers with content
   - **Status:** 🔮 speculative (2 of 3)
   - **Notes:** A zip-with-error variant is not in scope.
   ```
2. Cite at least one motivating program (file + line). Speculative
   candidates with no citation get rejected unless they're plainly
   foundational.
3. When the citation count hits 3 and the four shape rules pass
   (`Result`, lowercase fields, effect row, immutable), status flips to
   ⏳ Planned. The next implementation session picks it up.
4. Promotion is a PR: move the block from `## Candidates` to its module
   section, change status to ⏳ → 🚧 → ✅ as it lands.
5. Rejection is also legitimate. A candidate that hits citations but
   violates the shape rules, or duplicates an existing operation, moves
   to `## Rejected` with a one-line reason.

External proposals: open a GitHub issue or a PR against this file. The
same threshold (3 citations + shape rules) applies.

---

## Status legend

| Symbol | Meaning |
| --- | --- |
| ✅ | Shipped on both backends with E2E coverage |
| 🚧 | One backend, partial, or proposed-but-unverified |
| ⏳ | Planned, in scope, not yet started |
| 🔮 | Speculative; under consideration |
| 🚫 | Out of scope (rejected or by-design) |

---

## Module index

Foundational types and the modules they live in:

- [`Unit`](#unit) — the no-information value
- [`Result<T, E>`](#result) — fallible computation
- [`Option<T>`](#option) — possibly-absent value
- [`IoError`](#ioerror) — narrative-carrying I/O failure
- [`RefinementError`](#refinementerror) — refinement try_from default Err
- [`List<T>`](#list) — immutable sequence
- [`Map<K, V>`](#map) — immutable key→value map
- [`Set<T>`](#set) — immutable membership
- [`Bytes`](#bytes) — immutable byte sequence
- [`String`](#string) — text helpers
- [`Int`](#int) / [`Float`](#float) — numeric companions
- [`Console`](#console) — stdin / stdout / stderr
- [`File`](#file) / [`Directory`](#directory) / [`Path`](#path) — filesystem
- [`Process`](#process) — subprocess execution
- [`Trace`](#trace) / [`Log`](#log) — causal tracing and leveled logging

Out-of-scope categories: HTTP, JSON, regex, crypto, sockets, db drivers.
See *What stays out, by design* above. Date / Time also stays out for
v1 — see [`Time`](#time-deferred) below.

The shape of foundational File and Console I/O was sanity-checked against
C's `stdio.h` / `unistd.h` / `sys/stat.h`. C's compactness (~30 fns covers
nearly everything outside formatted-I/O variants) is a useful upper bound:
where C ships a primitive we don't, that's a flag to look at the gap.

---

## `Unit`

Type. The zero-information return value, used for fns that exist for their
effects (`println`, `File.write_all_text`). Single canonical instance.

| Status | Item |
| --- | --- |
| ✅ | `Unit` type |

---

## `Result`

Type plus its constructors. Sum type with an `Ok` and `Err` arm.

| Status | Item |
| --- | --- |
| ✅ | `Result<T, E>` type |
| ✅ | `Ok(value: T) -> Result<T, E>` |
| ✅ | `Err(error: E) -> Result<T, E>` |
| ✅ | `Result.unwrap_or<T, E>(r: Result<T, E>, default_value: T) -> T` |
| ✅ | `Result.unwrap_or_else<T, E>(r: Result<T, E>, fallback: fn(E) -> T) -> T` |
| ⏳ | `Result.is_ok<T, E>(r: Result<T, E>) -> Bool` |
| ⏳ | `Result.is_err<T, E>(r: Result<T, E>) -> Bool` |
| ⏳ | `Result.map<T, U, E>(r: Result<T, E>, f: fn(T) -> U) -> Result<U, E>` |
| ⏳ | `Result.map_err<T, E, F>(r: Result<T, E>, f: fn(E) -> F) -> Result<T, F>` |
| ⏳ | `Result.and_then<T, U, E>(r: Result<T, E>, f: fn(T) -> Result<U, E>) -> Result<U, E>` |
| ⏳ | `Result.ok<T, E>(r: Result<T, E>) -> Option<T>` |
| ⏳ | `Result.err<T, E>(r: Result<T, E>) -> Option<E>` |

The `?` operator is language-level; it short-circuits on Err and does not
require a stdlib companion.

---

## `Option`

| Status | Item |
| --- | --- |
| ✅ | `Option<T>` type |
| ✅ | `Some(value: T) -> Option<T>` |
| ✅ | `None() -> Option<T>` |
| ✅ | `Option.unwrap_or<T>(opt: Option<T>, default_value: T) -> T` |
| ✅ | `Option.unwrap_or_else<T>(opt: Option<T>, fallback: fn() -> T) -> T` |
| ⏳ | `Option.is_some<T>(opt: Option<T>) -> Bool` |
| ⏳ | `Option.is_none<T>(opt: Option<T>) -> Bool` |
| ⏳ | `Option.map<T, U>(opt: Option<T>, f: fn(T) -> U) -> Option<U>` |
| ⏳ | `Option.and_then<T, U>(opt: Option<T>, f: fn(T) -> Option<U>) -> Option<U>` |
| ⏳ | `Option.ok_or<T, E>(opt: Option<T>, err: E) -> Result<T, E>` |
| ⏳ | `Option.ok_or_else<T, E>(opt: Option<T>, err: fn() -> E) -> Result<T, E>` |

---

## `IoError`

| Status | Item |
| --- | --- |
| ✅ | `record IoError { narrative: String }` |

The standard error type for `!{io}`-effected operations. Error narratives
match across backends — a program reading the same broken state sees the
same string on .NET and Go (hence `String.parse_int`'s "could not parse
'<input>' as Int"). Future fields (errno, wrapped cause) can land
additively.

---

## `RefinementError`

| Status | Item |
| --- | --- |
| ✅ | `record RefinementError { alias_name: String, predicate_text: String, offending_value: Any }` |

The default Err arm of an auto-generated `Alias.try_from(raw)` when the
refinement type does not supply an `else { ... }` clause. Refinements that
DO supply one use their own domain type instead.

(`RefinementViolation` is the panic-time companion raised by the always-on
boundary check helper. It's intentionally not a value type — refinements
that cross a boundary the type checker can't decide statically panic; the
TryFrom path is for value-error semantics.)

---

## `List`

Immutable sequence. The backbone of every Overt program that handles
collections.

### Foundational set

| Status | Item |
| --- | --- |
| ✅ | `List<T>` type |
| ✅ | `List.empty<T>() -> List<T>` |
| ✅ | `List.singleton<T>(value: T) -> List<T>` |
| ✅ | `List.at<T>(list: List<T>, index: Int) -> T` (panics on out-of-range) |
| ✅ | `size<T>(list: List<T>) -> Int` |
| ✅ | `map<T, U>(list: List<T>, f: fn(T) -> U) -> List<U>` |
| ✅ | `filter<T>(list: List<T>, pred: fn(T) -> Bool) -> List<T>` |
| ✅ | `fold<T, U>(list: List<T>, seed: U, step: fn(U, T) -> U) -> U` |
| ✅ | `all<T>(list: List<T>, pred: fn(T) -> Bool) -> Bool` |
| ✅ | `any<T>(list: List<T>, pred: fn(T) -> Bool) -> Bool` |
| ✅ | `par_map<T, U, E>(list: List<T>, f: fn(T) -> Result<U, E>) -> Result<List<U>, E>` |
| ✅ | `try_map<T, U, E>(list: List<T>, f: fn(T) -> Result<U, E>) -> Result<List<U>, E>` |
| ✅ | `List.concat_three<T>(first: List<T>, middle: List<T>, last: List<T>) -> List<T>` |
| ✅ | `List.contains<T>(list: List<T>, value: T) -> Bool` |
| ✅ | `List.concat<T>(left: List<T>, right: List<T>) -> List<T>` |
| ✅ | `List.head<T>(list: List<T>) -> Option<T>` |
| ✅ | `List.tail<T>(list: List<T>) -> List<T>` |
| ✅ | `List.take<T>(list: List<T>, n: Int) -> List<T>` |
| ✅ | `List.drop<T>(list: List<T>, n: Int) -> List<T>` |
| ✅ | `List.reverse<T>(list: List<T>) -> List<T>` |
| ✅ | `List.find<T>(list: List<T>, predicate: fn(T) -> Bool) -> Option<T>` |
| ✅ | `List.find_index<T>(list: List<T>, predicate: fn(T) -> Bool) -> Option<Int>` |
| ✅ | `List.flat_map<T, U>(list: List<T>, f: fn(T) -> List<U>) -> List<U>` |
| ✅ | `List.partition<T>(list: List<T>, predicate: fn(T) -> Bool) -> ListPartition<T>` |
| ✅ | `record ListPartition<T> { matched: List<T>, unmatched: List<T> }` (returned by `List.partition`) |
| ✅ | `record Pair<T, U> { left: T, right: U }` (universal 2-tuple container) |
| ✅ | `List.zip<T, U>(left: List<T>, right: List<U>) -> List<Pair<T, U>>` (truncates to shorter) |
| ✅ | `List.unzip<T, U>(pairs: List<Pair<T, U>>) -> Pair<List<T>, List<U>>` |
| ✅ | `List.flatten<T>(lists: List<List<T>>) -> List<T>` |
| ✅ | `List.sort_by<T>(list: List<T>, cmp: fn(T, T) -> Int) -> List<T>` (stable; libc cmp convention) |
| ⏳ | `List.sort<T>(list: List<T>) -> List<T>` (requires generic ordering primitive) |

### Cleanup

| Status | Item | Note |
| --- | --- | --- |
| 🚫 | `len<T>(list) -> Int` synonym | Duplicate of `size`. Remove pre-1.0; programs use `size`. |

`size` is the canonical name. `len` is a v0 shipping mistake. The Overt
formatter will rewrite `len(xs)` to `size(xs)` once we land the cleanup.

`length(s: String) -> Int` is *not* a `size` synonym — it's the String
operation; see [`String`](#string).

---

## `Map`

Immutable key→value map. Hash-based on .NET (ImmutableDictionary) and Go
(map[K]V copy-on-write); iteration order is insertion-defined on .NET and
unspecified on Go (consistent with the host's native semantics — programs
that need deterministic order sort the keys explicitly).

### Foundational set

| Status | Item |
| --- | --- |
| ✅ | `Map<K, V>` type |
| ✅ | `record MapEntry<K, V> { key: K, value: V }` (returned by `Map.entries`) |
| ✅ | `Map.empty<K, V>() -> Map<K, V>` |
| ✅ | `Map.get<K, V>(map: Map<K, V>, key: K) -> Option<V>` |
| ✅ | `Map.contains_key<K, V>(map: Map<K, V>, key: K) -> Bool` |
| ✅ | `Map.insert<K, V>(map: Map<K, V>, key: K, value: V) -> Map<K, V>` |
| ✅ | `Map.remove<K, V>(map: Map<K, V>, key: K) -> Map<K, V>` |
| ✅ | `Map.size<K, V>(map: Map<K, V>) -> Int` |
| ✅ | `Map.keys<K, V>(map: Map<K, V>) -> List<K>` |
| ✅ | `Map.values<K, V>(map: Map<K, V>) -> List<V>` |
| ✅ | `Map.entries<K, V>(map: Map<K, V>) -> List<MapEntry<K, V>>` |
| ✅ | `Map.merge<K, V>(left: Map<K, V>, right: Map<K, V>) -> Map<K, V>` (right wins) |
| ✅ | `Map.map<K, V, W>(map: Map<K, V>, f: fn(V) -> W) -> Map<K, W>` |
| ✅ | `Map.filter<K, V>(map: Map<K, V>, predicate: fn(K, V) -> Bool) -> Map<K, V>` |

### Design notes

**`Map.entries` returns `List<MapEntry<K, V>>` rather than `List<(K, V)>`.**
Tuple-shaped type annotations aren't yet expressible in Overt source —
the AST has no `TupleType` node. The `MapEntry` record sidesteps the gap
and reads more naturally at the field-access site:

```overt
for entry in Map.entries(map = m) {
    println("${entry.key} = ${entry.value}")?
}
```

If tuple-type annotations land later, `Map.entries` could return
`List<(K, V)>` instead, and `MapEntry` would be deprecated in favor of
that. Until then, the named-field form is the canonical shape.

**`Map.merge` is right-wins.** When both maps contain a key, the right
side's value survives. Matches the convention of last-writer-wins
merging that most programs expect (and what `dict.update()` /
`Dictionary.SetItems` do).

**Key constraint.** Both runtimes require `K` to be hashable / comparable
(`notnull` on .NET, `comparable` on Go). The Overt-side type-checker
doesn't yet enforce this — programs that pass a structurally-keyed type
(e.g. a record) get a host-language error rather than a clean Overt
diagnostic. A future pass would surface the constraint as a refinement
or trait.

---

## `Set`

Immutable membership. ImmutableHashSet on .NET; `map[T]struct{}` (the
idiomatic Go set pattern) on Go. Same hashable/comparable element-type
constraint as `Map`.

| Status | Item |
| --- | --- |
| ✅ | `Set<T>` type |
| ✅ | `Set.empty<T>() -> Set<T>` |
| ✅ | `Set.contains<T>(set: Set<T>, value: T) -> Bool` |
| ✅ | `Set.insert<T>(set: Set<T>, value: T) -> Set<T>` |
| ✅ | `Set.remove<T>(set: Set<T>, value: T) -> Set<T>` |
| ✅ | `Set.size<T>(set: Set<T>) -> Int` |
| ✅ | `Set.union<T>(left: Set<T>, right: Set<T>) -> Set<T>` |
| ✅ | `Set.intersect<T>(left: Set<T>, right: Set<T>) -> Set<T>` |
| ✅ | `Set.difference<T>(left: Set<T>, right: Set<T>) -> Set<T>` |

---

## `String`

Text helpers. `String` is a primitive type; the namespace collects the
operations.

### Foundational set

| Status | Item |
| --- | --- |
| ✅ | `length(s: String) -> Int` |
| ✅ | `String.split(s: String, sep: String) -> List<String>` |
| ✅ | `String.join(list: List<String>, sep: String) -> String` |
| ✅ | `String.contains(s: String, needle: String) -> Bool` |
| ✅ | `String.starts_with(s: String, prefix: String) -> Bool` |
| ✅ | `String.ends_with(s: String, suffix: String) -> Bool` |
| ✅ | `String.parse_int(s: String) -> Result<Int, IoError>` |
| ✅ | `String.parse_float(s: String) -> Result<Float, IoError>` |
| ✅ | `String.code_at(s: String, index: Int) -> Int` |
| ✅ | `String.chars(s: String) -> List<String>` |
| ✅ | `String.code_points(s: String) -> List<Int>` |
| ✅ | `String.trim(s: String) -> String` |
| ✅ | `String.to_upper(s: String) -> String` |
| ✅ | `String.to_lower(s: String) -> String` |
| ✅ | `String.replace(s: String, from: String, to: String) -> String` |
| ✅ | `String.substring(s: String, start: Int, end: Int) -> String` |
| ✅ | `String.index_of(s: String, needle: String) -> Option<Int>` |
| ✅ | `String.repeat(s: String, n: Int) -> String` |

`length(s)` is the only String operation that escapes its namespace
(unqualified prelude form). Historical from when `size` and `length`
overlapped; the cleanup keeps `length` for strings, `size` for collections.

---

## `Int`

| Status | Item |
| --- | --- |
| ✅ | `Int` primitive type |
| ✅ | `Int.range(start: Int, end: Int) -> List<Int>` |
| ⏳ | `Int.abs(n: Int) -> Int` |
| ⏳ | `Int.min(a: Int, b: Int) -> Int` |
| ⏳ | `Int.max(a: Int, b: Int) -> Int` |

Arithmetic operators (`+`, `-`, `*`, `/`, `%`) are language-level, not
stdlib. Integer overflow traps by default per DESIGN.md §8.

---

## `Float`

| Status | Item |
| --- | --- |
| ✅ | `Float` primitive type |
| ⏳ | `Float.abs(x: Float) -> Float` |
| ⏳ | `Float.min(a: Float, b: Float) -> Float` |
| ⏳ | `Float.max(a: Float, b: Float) -> Float` |

Domain-specific ops (`sqrt`, `floor`, `ceil`, `round`, `pow`) are
rule-of-three; they'll land when three programs reach for them. Most
programs don't.

---

## `Console`

Process-level stdin / stdout / stderr. The unqualified prelude forms
(`println`, `eprintln`, `args`) are kept for ergonomics, not folded into
this namespace; programs reach for them constantly enough that the
qualification feels like noise.

| Status | Item |
| --- | --- |
| ✅ | `println(line: String) !{io} -> Result<Unit, IoError>` (writes line + `\n` to stdout) |
| ✅ | `eprintln(line: String) !{io} -> Result<Unit, IoError>` (writes line + `\n` to stderr) |
| ✅ | `args() !{io} -> List<String>` |
| ✅ | `print(s: String) !{io} -> Result<Unit, IoError>` (no trailing newline) |
| ✅ | `eprint(s: String) !{io} -> Result<Unit, IoError>` (no trailing newline) |
| ✅ | `read_line() !{io} -> Result<Option<String>, IoError>` (None at EOF) |
| ✅ | `read_to_end() !{io} -> Result<String, IoError>` (consume all of stdin) |

What stays out of foundational: ANSI color codes, terminal-size detection,
raw-mode keyboard input, cursor positioning. These are domain-specific
(interactive TUIs, fancy CLIs) and the platform conventions diverge.
Programs that want color emit raw escape strings, which work on modern
Win/macOS/Linux terminals.

---

## `Bytes`

Immutable sequence of octets. Foundational because programs that read
binary files, hash data, or interoperate with byte-shaped protocols can't
operate at the text-only ceiling.

| Status | Item |
| --- | --- |
| ✅ | `Bytes` type (immutable sequence of u8) |
| ✅ | `Bytes.empty() -> Bytes` |
| ✅ | `Bytes.from_list(list: List<Int>) -> Bytes` (each Int must be 0..255) |
| ✅ | `Bytes.size(b: Bytes) -> Int` |
| ✅ | `Bytes.at(b: Bytes, index: Int) -> Int` (panics out-of-range) |
| ✅ | `Bytes.slice(b: Bytes, start: Int, end: Int) -> Bytes` |
| ✅ | `Bytes.concat(left: Bytes, right: Bytes) -> Bytes` |
| ✅ | `Bytes.from_utf8(s: String) -> Bytes` |
| ✅ | `Bytes.to_utf8(b: Bytes) -> Result<String, IoError>` (Err on invalid UTF-8) |

`Bytes.at` returns `Int` rather than a separate `Byte` primitive — every
operation that wants a single byte uses Int 0..255, and programs that
care can spell that as a refinement type:

```overt
type Byte = Int where 0 <= self && self <= 255
```

Lowers to `byte[]` / `ImmutableArray<byte>` on .NET and `[]byte` on Go.
The "intermediate Byte primitive" alternative was rejected because it
duplicates a refinement type the language already supports.

---

## `File`

Filesystem operations. All operations carry `!{io}`.

The shape borrows from C's `stdio.h` (compact verb set, path-based) plus
the universally-shipped extensions (read_lines, append, copy, move) that
every modern stdlib has because the C-style "open / loop / close" is too
verbose for the common cases.

| Status | Item |
| --- | --- |
| ✅ | `File.read_to_string(path: String) !{io} -> Result<String, IoError>` |
| ✅ | `File.write_all_text(path: String, contents: String) !{io} -> Result<Unit, IoError>` |
| ✅ | `File.exists(path: String) !{io} -> Bool` |
| ✅ | `File.read_lines(path: String) !{io} -> Result<List<String>, IoError>` |
| ✅ | `File.append_text(path: String, contents: String) !{io} -> Result<Unit, IoError>` |
| ✅ | `File.delete(path: String) !{io} -> Result<Unit, IoError>` (= C `remove`; no-op on missing) |
| ✅ | `File.size(path: String) !{io} -> Result<Int, IoError>` |
| ✅ | `File.move(from: String, to: String) !{io} -> Result<Unit, IoError>` (= C `rename`; atomic where the host supports it) |
| ✅ | `File.copy(from: String, to: String) !{io} -> Result<Unit, IoError>` (overwrites destination) |
| ✅ | `File.read_bytes(path: String) !{io} -> Result<Bytes, IoError>` |
| ✅ | `File.write_bytes(path: String, data: Bytes) !{io} -> Result<Unit, IoError>` (overwrites) |
| ⏳ | `File.read_bytes(path: String) !{io} -> Result<Bytes, IoError>` |
| ⏳ | `File.write_bytes(path: String, data: Bytes) !{io} -> Result<Unit, IoError>` |

UTF-8 throughout for text operations. Default file mode for writes is
0644 (matching .NET WriteAllText default).

What stays out of foundational, with C-stdlib parallels for context:
- **Streams** (`fopen`/`fread`/`fclose`-style opaque handles). Everything
  in the table above is path-based one-shot. Streaming I/O is queued in
  `## Candidates` because real programs will need it (see *Streams*
  there) and we want the design pass settled before the implementation.
- **`stat`-shape `FileInfo`** (size + mtime + permissions + is_dir as one
  record). Useful, but pulls Time in. Foundational `File.size` covers the
  common case; the rest waits for Time.
- **File watching, atomic-write helpers, file locks, symlinks, mmap.**
  Real domains in their own right.

---

## `Directory`

Filesystem directory operations. All carry `!{io}`.

| Status | Item |
| --- | --- |
| ✅ | `Directory.exists(path: String) !{io} -> Bool` |
| ✅ | `Directory.create(path: String) !{io} -> Result<Unit, IoError>` (creates parents as needed) |
| ✅ | `Directory.list(path: String) !{io} -> Result<List<String>, IoError>` (entry names, not full paths) |
| ✅ | `Directory.delete(path: String, recursive: Bool) !{io} -> Result<Unit, IoError>` |

The recursive-flag form of `delete` is a deliberate single-method choice
over Rust's `remove_dir` / `remove_dir_all` split; one fn with the
predicate is more Overt-shaped (one canonical name, predicate as a
named arg).

`list` returns entry *names*, not full paths. Callers that want full
paths use `Path.join(parent = dir, child = name)` per entry. This avoids
a second variant fn (`list_paths` etc.) at the cost of a one-line loop.

---

## `Path`

Pure path-string manipulation. None of these touch the filesystem; no
effect row.

| Status | Item |
| --- | --- |
| ✅ | `Path.join(parent: String, child: String) -> String` |
| ✅ | `Path.parent(path: String) -> Option<String>` |
| ✅ | `Path.file_name(path: String) -> Option<String>` |
| ✅ | `Path.extension(path: String) -> Option<String>` |
| ✅ | `Path.with_extension(path: String, ext: String) -> String` (replace extension; `.cs` ↔ `.ov`) |
| ✅ | `Path.is_absolute(path: String) -> Bool` |

Platform-aware separator (`/` on Unix, `\` on Windows) per the host's
native conventions.

---

## `Process`

Subprocess execution.

| Status | Item |
| --- | --- |
| ✅ | `record ProcessOutput { exit_code: Int, stdout: String, stderr: String }` |
| ✅ | `Process.run(cmd: String, args: List<String>) !{io} -> Result<ProcessOutput, IoError>` |

A non-zero exit is `Ok` with `output.exit_code != 0`. Only launch failures
(binary not found, permission denied, etc.) surface as `Err`. Streaming
I/O, process groups, signals, and timeouts are not in scope for v1; they
land via rule of three when programs need them.

---

## `Trace`

The trace block (`trace { ... }`) is language-level; the runtime is
minimal. `Trace.subscribe` registers a consumer for events emitted by
trace blocks *and* by [`Log`](#log) calls — they share the channel.

| Status | Item |
| --- | --- |
| 🚧 | `enum LogLevel { Debug, Info, Warn, Error }` (planned, replaces description-only TraceEvent) |
| 🚧 | `record TraceEvent { level: LogLevel, message: String }` (currently `{ description: String }`; planned reshape) |
| ✅ | `Trace.subscribe(consumer: fn(TraceEvent) !{io} -> ())` |

**No timestamp field.** Time deliberately stays out of foundational (see
[Time (deferred)](#time-deferred) below). Consumers that need timestamps
either capture them at receive-time or wait until `Time` graduates.

When the program-default consumer runs (no `Trace.subscribe` called),
output is `[LEVEL] message` to stderr — no timestamps. Programs that
want richer formatting register a consumer.

---

## `Log`

Leveled logging. Folds into the Trace channel: `Log.info(msg)` emits a
`TraceEvent { level: LogLevel.Info, message: msg }`. Subscribers see
both `trace { ... }` block events and explicit log calls through one
mechanism.

| Status | Item |
| --- | --- |
| ⏳ | `Log.debug(message: String) !{io} -> ()` |
| ⏳ | `Log.info(message: String) !{io} -> ()` |
| ⏳ | `Log.warn(message: String) !{io} -> ()` |
| ⏳ | `Log.error(message: String) !{io} -> ()` |

Why folded into Trace rather than independent: programs gain one
consumer registry, one filtering model, one way to silence output.
Causal tracing is a real Overt distinctive (see README); making logging
its consumer-friendly view is what makes the distinctive earn its keep.

What stays out of foundational:
- **Structured key-value attributes** (Serilog's LogContext, OpenTelemetry
  attributes). May happen via `attrs: Map<String, String>` on TraceEvent
  once Map operations are foundational; speculative.
- **Sinks for syslog, journald, OTLP, Stackdriver.** Domain. FFI per host.
- **Filtering / routing rules.** The subscriber implements whatever it
  wants; we don't ship filtering machinery.

Failure mode to watch: a buggy subscriber that throws / panics drops
events. The default-fallback consumer should write to stderr with a
warning when a subscriber fails, so logs don't silently disappear.

---

## Time (deferred)

Time and Date are *intentionally* not in v1's foundational set.

The pragmatic version of getting Time right is to refuse to do Date.
Calendars, timezones, DST transitions, leap seconds, calendar arithmetic
— every one is a tar pit, and the languages that try to solve them
produce surfaces with 30+ types and decades of regret (Java pre-
`java.time`, Python's `datetime` + `pytz` + `zoneinfo`, JavaScript's
`Date`). Until a sample app convinces us we need it, programs that want
date / time semantics use FFI (`extern "csharp" use "System.DateTime..."`
or `extern "go" use "time"`). The host ecosystems handle it well.

When (if) Time graduates via rule of three, the constrained surface I'd
propose:

- **Monotonic Instant only.** Opaque type. No serialization, no parsing,
  no calendar interpretation. Suitable for timing, ordering, durations.
- **Duration.** Diff of two Instants. Plus / minus / as-millis / as-seconds
  / as-micros readouts.
- **One escape hatch for human-readable timestamps.** `Clock.now_utc() ->
  String` returns ISO 8601 UTC. No locale, no timezone conversion, no
  format customization. That's the entire wall-clock surface.

Anything beyond that — local time, calendar arithmetic, parsing user-
entered dates, scheduling — stays FFI per host. We don't try to be a
calendar library. Ever.

---

## Async / concurrency primitives

| Status | Item |
| --- | --- |
| ✅ | `Task<T>` type (async-boundary wrapper) |

`parallel { ... }`, `race { ... }`, and `.await` are language-level
constructs, not stdlib. They lower per backend (sequential on Go today,
structured on C#). Genuine goroutines + channels on Go is a separate arc;
when it lands, channels may surface as a stdlib type, but that's
speculative.

---

## FFI primitives

| Status | Item |
| --- | --- |
| ✅ | `CString.from(s: String) -> CString` |
| ✅ | `Ptr<T>` raw pointer placeholder (C FFI) |

These exist for `extern "c"` boundaries. They're documented but not
aspirational — programs that don't reach for C FFI never see them.

---

## Candidates

Operations that haven't earned their slot yet. Each block lists the
motivating programs. When citation count hits **3** and the four shape
rules pass, status flips to ⏳ Planned and the next implementation
session picks it up.

### Streams (`Reader` / `Writer` opaque handles)

**Status:** 🔮 deferred-but-anticipated (no program citations yet; design
sketched here so the implementation can start from a refined draft when a
program demands it)

**Why this is an anticipated candidate, not just speculative:** the
foundational `File.read_to_string` / `File.write_all_text` shape is
fine for files that fit in memory. It breaks for the workloads Overt is
actually intended to enable: log filters on multi-GB files, network
servers handling unbounded input, build tools streaming through large
artifacts. *"Read all into memory" is the teaching idiom; "stream from
source to sink" is the working idiom.* When chat-relay Phase 2+
or any pipeline-shaped sample app lands, this candidate gets cited.

**Sketch:**

```overt
type FileReader  // opaque, stateful host handle
type FileWriter  // opaque, stateful host handle

// Open / close — callback form auto-closes on body return / error.
File.with_open_read<R>(path: String, body: fn(FileReader) -> R) !{io} -> Result<R, IoError>
File.with_open_write<R>(path: String, body: fn(FileWriter) -> R) !{io} -> Result<R, IoError>
File.with_open_append<R>(path: String, body: fn(FileWriter) -> R) !{io} -> Result<R, IoError>

// Read primitives.
Reader.read_line(reader: FileReader) !{io} -> Result<Option<String>, IoError>  // None at EOF
Reader.read_block(reader: FileReader, max_bytes: Int) !{io} -> Result<Bytes, IoError>  // empty Bytes at EOF

// Write primitives.
Writer.write(writer: FileWriter, data: Bytes) !{io} -> Result<Int, IoError>  // bytes actually written
Writer.write_string(writer: FileWriter, s: String) !{io} -> Result<Unit, IoError>
Writer.flush(writer: FileWriter) !{io} -> Result<Unit, IoError>
```

**Open design questions:**

1. **State without mutation.** `FileReader` is stateful by nature
   (position, error state, possibly a buffer). Overt's "no shared
   mutable state" rule needs a principled exception. The pragmatic
   answer is (a) make the stream an opaque host-managed type that the
   language doesn't promise immutability for. We already do this for
   `ProcessOutput`'s underlying buffer.
2. **Close without RAII.** Overt has no destructors. The `with_open_*`
   callback form auto-closes on body return *or* error. Considered
   alternative: explicit `Reader.close` users must remember to call.
   Rejected: error-prone, leaks on early return.
3. **Iteration shape.** `for line in reader { ... }` reads naturally
   but requires Reader to participate in whatever iteration protocol
   Overt eventually has. Without that protocol, callers loop manually:
   `loop { match Reader.read_line(reader) { Some(line) => ..., None
   => break } }`. Acceptable v1; iteration polish lands later.
4. **Bytes-buffer vs. fill-caller-buffer.** C's `read(buf, n)` mutates
   a caller-owned buffer. Overt-shape says read returns a *new* `Bytes`.
   Less efficient for huge transfers; matches the language's semantics.
   Most users won't notice; perf-sensitive users use FFI. Ship return-
   new and revisit if benchmarks demand otherwise.
5. **`Network.with_open_*` parallel.** Server / client sockets eventually
   need the same shape. The design here should generalize so a future
   `Network` module reuses `Reader` / `Writer` rather than introducing
   its own.
6. **Async streams.** `Reader.read_line_async(reader) !{io, async} ->
   Task<Result<...>>` for non-blocking I/O. Out of scope for the v1
   sketch; revisit when chat-relay Phase 2 demands it.

**Notes:**
- Three-citation threshold still applies. This block exists so when
  citations come, we don't start from a blank page.
- The candidate pre-resolves the open questions enough that an
  implementation could start; the citation list documents which
  programs justified each design decision when the time comes.

### Template

```
### {ModuleOrFreeForm}.{name}

- **Signature:** `...`
- **Semantics:** ...
- **Motivating programs:**
  - path/to/program1.ov:line — what it would replace
  - path/to/program2.ov:line — same
- **Status:** 🔮 speculative (N of 3)
- **Notes:** edge cases, related candidates, etc.
```

---

## Rejected

Operations that hit citation count but don't pass the shape rules, or
that duplicate existing operations. One-line rationale per entry.

*(No rejections currently logged.)*

---

## Out-of-scope policy

Categories that don't enter the OSL regardless of demand:

| Category | Reason | Path |
| --- | --- | --- |
| HTTP client | Domain-specific, host ecosystems differ irreducibly | `extern "csharp" use "System.Net.Http"` / `extern "go" use "net/http"` |
| JSON ser/de | Same | `extern "csharp" use "System.Text.Json"` / `extern "go" use "encoding/json"` |
| Regex | Same | Per-host extern |
| Crypto | Same; correctness too sensitive to reinvent | Per-host extern |
| Sockets / raw network | Same | Per-host extern |
| Database drivers | Same | Per-host extern |
| Image / audio / video | Same | Per-host extern |
| Reflection | Rejected at language level (DESIGN.md §4) | Use `@derive`, hand-roll |
| Macros / metaprogramming | Same | Same |

The language is allowed to grow; this list is allowed to shrink, but only
when *all backends* mature parallel implementations of the relevant
primitive. None of these are imminent.

---

## Cleanup queue

Pre-1.0 corrections to the shipped surface. Each item has a clear
disposition; the queue exists so we don't ship the regrets.

| Item | Disposition | Note |
| --- | --- | --- |
| `len<T>(list)` | Remove | Duplicate of `size`. Formatter rewrites at fix time. |
| Effect row on `File.exists` | Keep `!{io}` | The filesystem state can change between calls; `!{io}` declares the dependency. Confirmed correct after review. |
| `TraceEvent { description }` shape | Reshape to `{ level, message }` | Folds Log into the same channel; ships with `LogLevel` enum and the `Log` namespace. |

---

## Versioning

The OSL is versioned independently of the Overt language version. Tag
this doc `osl-1.0` when the foundational set is fully shipped. `1.x`
adds operations as the candidate queue matures. `2.0` allows removals
with a `1.x` deprecation runway.

Programs pin to a version via `// osl: 1.0` at the top of the entry
module *(speculative; pinning machinery doesn't exist yet)*.
