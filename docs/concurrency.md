# Concurrency design

This document scopes Overt's concurrency model: goroutines, channels,
`select`, shared mutable state, and cancellation. It is the design
brief for the next ~6 months of language work, gated on the
chat-relay sample (`docs/samples/chat-relay.md`) as an acceptance
test.

The goal is to settle the language-level shape *before* implementing
any of it. Implementation will span multiple sessions and probably
multiple back ends; getting the surface wrong now means rewriting it
later.

## 1. Goals

In rough priority order:

1. **Express the concurrency patterns Tela uses today.** Per-
   connection goroutines, channels for request/response handoff,
   `select` for multiplexed cancellation, mutex-guarded directories.
   If those four patterns can't be written cleanly, the design has
   failed.
2. **Honor Overt's "overt" stance.** Every effect, error, and
   dispatch should be visible at the call or declaration site. A
   goroutine spawn must be unmissable; a channel send/receive must
   not look like a method call.
3. **Compose with effect rows.** Concurrency effects (`async`, a
   future `concurrent`, cancellation) should integrate with the
   existing row-polymorphism story, not bolt on as a separate
   parallel system.
4. **Lower cleanly to Go and reasonably to C#.** The two reference
   back ends already exist. Go is the obvious fit for goroutines and
   channels; C# would lower to `Task` + `Channel<T>` + `CancellationToken`
   shapes. The design should not pretend the back ends are
   isomorphic, but it shouldn't require radically different idioms
   either.
5. **Don't repeat Go's footguns.** Loop-variable closures, nil
   channel receives, deadlocks from unbuffered channels with no
   ready receiver, and `defer` ordering bugs are recurring Go
   defects. Where the language can structurally prevent them
   without ergonomic cost, it should.

## 2. The four pieces

Concurrency in Tela-shape programs is four coupled features:

- **Goroutine spawn**: start a concurrent task that runs to
  completion, possibly forever.
- **Channels**: typed message passing between goroutines.
- **`select`**: choose one of several pending channel operations.
- **Shared mutable state**: data updated by multiple goroutines.
- **Cancellation**: bounded shutdown that propagates through a tree
  of goroutines.

Each gets its own subsection below: what it has to do, the design
options, and a leading proposal.

## 3. Goroutine spawn

### Requirement

Start a fresh task that runs alongside the caller. Returns
immediately. The spawned task may produce a value (often it doesn't),
may run forever, may be cancelled.

### Options

- **A. `go expr`** (Go-style). Brief, recognizable. Reads as
  "this expression runs concurrently."
- **B. `spawn { ... }` block**. Block-shaped, fits Overt's
  `parallel`/`race` aesthetic. Encourages multi-statement bodies.
- **C. `task fn() ...`** as a fn modifier. Declares a fn that always
  runs concurrently when called. Doesn't generalize to
  inline blocks.
- **D. `Task.spawn(fn)` as an stdlib call**. Most explicit; least
  ergonomic.

### Leading proposal

`spawn { ... }` block-form, returning `Task<T>` where T is the
block's value type. Reads as "evaluate this block in a new task,
hand me a Task handle." Aligns with the existing `parallel` and
`race` block-forms.

```overt
let writer: Task<()> = spawn {
    for msg in outgoing.iter() {
        ws.write(msg)?
    }
}
```

**Rationale.** Block form gives a natural place for multi-statement
work. The returned `Task<T>` lets callers `.await` for completion or
ignore it. Reusing `Task` keeps the type system consistent with
existing async code. Reading `spawn` as a keyword is structurally
unmissable, satisfying goal 2.

**Open**: should `spawn` carry an effect? A bare `spawn { fn() }`
where `fn` has effects must propagate them somehow. Probably
`spawn { ... }` produces `Task<T> !{spawn}`, where `spawn` is a new
effect. Callers acknowledge that the surrounding fn launches tasks.

## 4. Channels

### Requirement

Typed, possibly buffered, message passing. Operations: send, receive,
close. Receive should distinguish "value received" from "channel
closed."

### Options

- **A. Operator syntax** (Go-style): `ch <- value` (send), `<-ch`
  (receive). Compact; idiomatic Go; novel as Overt syntax.
- **B. Method syntax**: `ch.send(value)`, `ch.recv()`. Familiar,
  consistent with method-call dispatch elsewhere in Overt.
- **C. Pipe operator overload**: `value |> ch` for send, `ch |> ...`
  for receive into a binding.

### Leading proposal

Method syntax with `Channel<T>` as a stdlib type:

```overt
let ch: Channel<Message> = Channel.new(capacity = 16)
ch.send(msg)?
let received: Option<Message> = ch.recv()
```

`recv()` returning `Option<T>` (None on close) prevents the "received
zero value, was that a real message or a closed-channel default?"
ambiguity Go has.

**Why method syntax over operators**:

- Method calls already exist; `<-` would be one more thing to learn.
- Method calls thread effect rows naturally (`send` is `!{async}`,
  `try_send` is non-blocking, etc.).
- The language already has `?` and `|>`/`|>?` as precedence-sensitive
  operators; adding `<-` increases parsing complexity for low gain.

**Send semantics**: `send` blocks when the buffer is full;
`try_send(msg) -> Result<(), ChannelFull>` is the non-blocking
alternative. Same pattern for receive: blocking `recv()` and
non-blocking `try_recv()`.

**Close semantics**: `ch.close()` causes subsequent `recv()` calls to
drain remaining buffered values, then return `None`. Sending on a
closed channel is a programmer error (panic / fatal).

### Iteration

`for msg in ch.iter() { ... }` desugars to a loop over `recv()` that
exits when `recv()` returns `None`. The `iter()` is a virtual call
that doesn't actually allocate a List; it's a language idiom the
emitter recognizes.

## 5. `select`

### Requirement

Wait on multiple pending channel operations; act on whichever is
ready first. Required for "wait on a message OR cancellation OR
shutdown."

### Options

- **A. Block form mirroring Go's `select` statement.** Each arm is a
  channel operation plus a body.
- **B. Pattern-match on a virtual "next event."** Channels become
  event sources, `select` becomes a pattern match on the next
  event from any source. More Overt-flavored.
- **C. Combinator syntax**: `select(ch1.recv(), ch2.recv(), ...)`
  returning a tagged union. Most general, least readable for the
  common cases.

### Leading proposal

A `select` block with channel-operation arms, lowered to Go's `select`
on the Go target and to `Task.WhenAny` patterns on C#.

```overt
select {
    msg = incoming.recv() => {
        handle_inbound(msg)
    }
    outgoing.send(reply) => {
        // sent successfully
    }
    _ = shutdown.recv() => {
        return Ok(())
    }
}
```

**Rationale.** This shape maps cleanly to Go's `select` (zero
ceremony), and to C#'s `Task.WhenAny` over `ChannelReader<T>` (more
ceremony but tractable). It reuses the `=>` arm syntax from `match`,
so the visual rhythm of "branch on something, run a body" is the
same construct readers already know.

**Open**: should `select` have a `default` arm for non-blocking
behavior? Go does. The right answer is probably yes; the syntax could
be a literal `default => { ... }` arm.

**Open**: how does `select` interact with `?`-propagation when an arm
body uses it? Probably the same way `match` arms do today.

## 6. Shared mutable state

### Requirement

Multiple goroutines need to read and write a shared data structure
(the room directory in the chat-relay; the session map in Tela). The
language has to provide some way to do this.

This is the highest-stakes decision in the doc. Overt's existing
stance is "no shared mutable state at the language level"; that has
to change to handle Tela-shape programs.

### Options

- **A. `Mutex<T>` type.** Like Rust. Acquire a lock, read/write the
  protected value, release. Familiar to Go developers (essentially
  `sync.Mutex` + `sync.RWMutex` packaged with the value they
  protect).
- **B. Actor model.** Every shared-state holder is a goroutine that
  receives commands and replies on a channel. Clean composition with
  channels; no locks. But every state access is a round-trip
  through a channel, which is fine for low-rate state but expensive
  for hot reads (the chat-relay's room lookup is once-per-broadcast,
  which could be lots).
- **C. Software-transactional memory (STM).** Every read/write is in
  a transaction that retries on conflict. Elegant; not a Go-native
  pattern; expensive to implement well.
- **D. "Atomic cell" + immutable snapshots.** `Cell<T>` with
  compare-and-swap; consumers always see a coherent snapshot but
  may have to retry. Ergonomic for some patterns (single-writer with
  many readers) and awkward for others.

### Leading proposal

`Mutex<T>` with a guard pattern inspired by Rust's `MutexGuard`. The
guard auto-releases on scope exit (which Overt would need to express
via a `defer`-equivalent or explicit `Result`-thread cleanup).

```overt
let hub: Mutex<RoomDirectory> = Mutex.new(RoomDirectory.empty())

fn join(hub: Mutex<RoomDirectory>, room: String, client: Client) -> Result<(), JoinError> {
    let guard: MutexGuard<RoomDirectory> = hub.lock()?
    guard.add(room = room, client = client)
    // guard releases here implicitly, or via explicit `guard.release()`
    Ok(())
}
```

**Rationale.** Mutex matches what Tela actually does. Actor-model
purity is conceptually cleaner but pays a per-message cost on the hot
broadcast path. STM and atomic cells are both interesting research
directions but neither is a natural fit for the patterns Tela uses.

**Big open question**: how does `MutexGuard` express its
auto-release? Three sub-options:

- **B1.** Add `defer` to the language. The guard is just a value;
  `defer guard.release()` runs at scope exit.
- **B2.** Make `Mutex<T>` use a closure-passing API: `hub.with(|d| {
  d.add(...) })`. The lock is held for the closure's duration. No
  `defer` needed but requires inline closures, which Overt also
  doesn't have today.
- **B3.** Tie release to the guard's drop: when the variable goes
  out of scope, the runtime releases. Requires destructor semantics
  (which Overt doesn't have).

**Recommended**: B2 (closure-based). It avoids adding both `defer`
and destructors; it requires inline closures, which the language
needs anyway for `select` arm bodies and other constructs.

## 7. Cancellation

### Requirement

When a server shuts down, every in-flight goroutine should learn of
it and exit cleanly within a deadline. Tela uses `context.Context`
for this; the chat-relay's Phase 4 needs the same.

### Options

- **A. `context.Context` as a first-class type via `extern "go"
  use`.** Cheapest implementation; Tela-native. Overt is just a
  pass-through.
- **B. New language effect `!{cancellable}`.** Propagation through
  the type system; the runtime carries a hidden cancel-token. More
  Overt-native; harder to lower.
- **C. Explicit `Cancel` parameter.** Every fn that supports
  cancellation takes a `Cancel` arg. Most explicit; most ceremonial.
- **D. Channel-based cancellation pattern.** A `shutdown: Channel<()>`
  that's read in `select` arms. Composes with channels naturally;
  doesn't address "fn deep in the call tree wants to know we're
  shutting down."

### Leading proposal

Combine D and a sugar layer. The primitive is a channel, but the
language provides an idiom:

```overt
fn handler(conn: WebSocket, shutdown: Channel<()>) -> Result<(), Error> {
    select {
        msg = conn.recv() => { ... }
        _ = shutdown.recv() => {
            return Ok(())
        }
    }
}
```

A future sugar layer could add a `cancellable` effect that lowers to
the same channel pattern, but the primitive is plain enough that the
sugar isn't gating the chat-relay's Phase 4.

**Rationale.** Channels already exist in this design; cancellation
falls out of `select` for free. `context.Context` interop happens
when integrating with Go libraries; that's an `extern "go"` concern,
not a language concern.

## 8. Effect rows

The existing rows are `io`, `async`, `inference`, `fails`. Concurrency
adds at least one and possibly more:

- **`spawn`** (or `concurrent`): present on any fn that calls
  `spawn { ... }`. Caller acknowledges they may launch tasks.
- **`async`**: already exists, used today for fns that `.await`.
  Channels' send/recv are `!{async}` since they may suspend.

`select` blocks are `!{async}` because they suspend. `Mutex.lock()`
is `!{async}` for the same reason. A purely synchronous fn cannot
call into channels or mutex.

**Open**: do we need `concurrent` and `async` as distinct effects, or
should they collapse to one? Go conflates them (every blocking
operation is just "may yield"); C# distinguishes them (Task vs.
Channel). Overt's choice has emit consequences.

**Recommended**: collapse. `async` covers any "may suspend" effect.
Two rows is one too many for a cognitive budget.

## 9. Acceptance criteria

The design is ready when the chat-relay's phases compile and run as
expected:

| phase | what it proves |
| --- | --- |
| 1 | `extern "go" use` works for `net/http` and a websocket library. No language additions needed. |
| 2 | Goroutines and channels work end-to-end. Per-client reader spawns, broadcast channel fans out. |
| 3 | Shared mutable state primitive works. Hub state mutates safely from many tasks. |
| 4 | `select` and cancellation propagate correctly. SIGINT shuts down within deadline. |
| 5 | (Realistic edges.) Heartbeats, slow-consumer disconnect, write deadlines. |

Any deadlock, race, or "obvious" rewrite needed at any phase is a
design defect to fix before moving on.

## 10. What this doc deliberately doesn't cover

- **Implementation details** of the Go and C# back-end lowerings.
  Those go in per-back-end docs once the language surface is
  settled.
- **Memory model.** Goroutines see writes in some order; the precise
  semantics matter for advanced users but not for the chat-relay.
  Borrow Go's memory model for now (back ends inherit it cheaply on
  Go; C# back end gets it via `volatile` / `Interlocked`).
- **Structured concurrency.** Whether to enforce that every spawned
  task has a defined parent and is awaited or cancelled when the
  parent exits. This is a productive future direction (cf. Trio,
  Swift's `TaskGroup`), but not gating for the chat-relay.
- **Async cancellation primitives.** `CancellationToken`, future-
  cancellation, etc. The channel-based shutdown pattern in §7 is
  enough for now; a richer story can layer on top later.

## 11. Suggested next move

The doc lays out the design space; the next move is to settle the
big open questions in actual review. Specifically:

1. Is `spawn { ... }` block-form the right syntax, or do we want
   `go expr`?
2. Channels: method-syntax (current proposal) vs. operator-syntax?
3. Shared state: `Mutex<T>` with closure-passing access, or
   something else?
4. Cancellation: channel-based primitive (current proposal), or a
   first-class effect / type?
5. Effect rows: collapse `async` and a hypothetical `concurrent`
   into one, or keep them distinct?

A separate session reviewing each of these and pinning down a
"locked" design is the gating step before any code lands. After
that, implementation can proceed in parallel with chat-relay
phasing as the acceptance test.
