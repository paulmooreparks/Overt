# chat-relay: a Tela-shape proving ground

This document sketches a small WebSocket chat-relay sample as a design
target for Overt's Go back end and concurrency story. It is not the
implementation; it is the spec the implementation should solve, plus
the explicit feature-gap list each layer forces.

The chat-relay's job is to be the smallest possible program that
exercises every concurrency primitive Tela uses. Tela itself is too
big to drive language decisions against (the WireGuard userspace stack
alone is more code than current Overt can handle). A 500-line stand-in
that hits goroutines, channels, `select`, shared mutable state, and
`context.Context` cancellation is the right size: small enough to
build and rebuild as the language evolves, broad enough that "it
compiles, it runs, it doesn't deadlock" is a meaningful signal.

## 1. The system

A single-binary WebSocket daemon. Clients connect over WS, send a
`join <room>` message, and from that point every message they send is
broadcast to every other connected client in the same room. Clients
can `part` to leave and connect to another room, or just close the
socket.

```
   client A ─┐
   client B ─┼── room "general" ── broadcast pump ── all subscribers
   client C ─┘

   client D ─┐
   client E ─┴── room "design"  ── broadcast pump ── all subscribers
```

Concrete behaviors the implementation must demonstrate:

1. **Concurrent connections.** N clients connected at the same time,
   each with its own reader goroutine. No client can starve another.
2. **Broadcast fan-out.** A message from one client reaches every
   other client in the same room within a bounded time.
3. **Connection lifecycle.** Closing a socket removes the client from
   its room directory. The server doesn't leak goroutines.
4. **Graceful shutdown.** SIGINT closes all sockets and exits within
   a few seconds, not 30.
5. **Shared room directory.** Many goroutines read and write the same
   room → clients map. No data races. No global lock that serializes
   the broadcast hot path.
6. **Cancellation propagation.** When the server is shutting down, in-
   flight broadcasts complete or abort cleanly; a slow client can't
   block the rest.

## 2. The shape

The minimal Go-shaped implementation has four moving parts.

### Hub

A single shared object holding the room directory. Every goroutine
reads from it; some mutate it. Three operations:

- `Join(roomName, client) -> ()` — adds the client to the named room,
  creating the room if it didn't exist.
- `Part(roomName, client) -> ()` — removes the client; deletes the
  room if it was the last subscriber.
- `Broadcast(roomName, msg, except) -> ()` — sends `msg` to every
  client in the room except the originator.

In Go the hub is typically a struct with a `sync.RWMutex` over a
`map[string]*Room`. In Overt, the shape needs a primitive that's
neither in the language nor in the runtime today.

### Room

A struct holding a slice of clients (or a set, indexed by ID) and a
broadcast channel. In Go, sending on the channel and ranging over the
client list both happen under the hub's lock; in a more elaborate
design, each room has its own goroutine pumping the broadcast channel
to subscribers, so the hub lock is held only briefly for membership
mutation.

### Client

One per connected socket. Two goroutines per client:

- **Reader**: reads frames off the WebSocket, parses each as a
  `Command` (an enum: `Join`, `Part`, `Say`, `Quit`), dispatches to
  the hub.
- **Writer**: ranges over the client's per-client outgoing channel,
  writes each message to the WebSocket, blocks on slow consumers.

A `select` multiplexes the writer between the outgoing channel and
the server's shutdown signal so a slow client doesn't strand the
shutdown.

### Server

Accepts WebSocket upgrade requests, spawns the two goroutines per
connection, registers the client with the hub, propagates
`context.Context` cancellation on shutdown.

## 3. Feature-gap mapping

Each row is one Overt or Go-back-end feature the chat-relay needs,
its current status, and the priority in the over-arching road map.

| feature | needed for | status | priority |
| --- | --- | --- | --- |
| **goroutines** (`go { ... }`) | Reader and writer per client; hub broadcast pump | Not in language. `parallel` / `race` are higher-level task groups, not equivalent. | P0 |
| **channels** (`Channel<T>`) | Per-client outgoing queue; broadcast distribution; shutdown signal | Not in language. | P0 |
| **`select`** | Multiplexing writer between outgoing channel and shutdown | Not in language. | P0 |
| **shared mutable map** (Mutex over the room directory) | Hub state | Overt avoids shared mutable state by design. Need a primitive: `Mutex<T>`, an actor, or a transactional cell. | P0 |
| **`extern "go" use net/http`** | Server upgrade handler | Not in Go back end. Mirror of `extern "csharp" use`. | P0 |
| **`extern "go" use gorilla/websocket`** (or std `nhooyr.io/websocket`) | Wire framing | Not in Go back end. Once `extern "go" use` lands, the binding is generated like other third-party Go packages. | P1 |
| **`context.Context`** | Cancellation propagation | Effect rows (`!{io}`, `!{async}`) are loosely analogous but don't carry runtime tokens. Either fold context into an effect-row primitive or expose it as a first-class type via `extern "go"`. | P1 |
| **`Map<K, V>` runtime + iteration** | Room directory; client → outgoing channel maps | Map type exists in the type system; the runtime has no Go-side `Map[K, V]` and no iteration primitives yet. | P1 |
| **`extern "go" use os/signal`** | SIGINT handling for graceful shutdown | Not in Go back end. | P2 |
| **JSON encode/decode** | If the wire format is JSON. Could also be a custom Overt-shaped binary frame. | `extern "go" use encoding/json` once `extern "go" use` lands. | P2 |
| **`defer`** (or equivalent) | Cleanup on connection close (lock release, registry removal) | Not in Overt. Either add `defer` or rely on `Result`-thread cleanup. | P2 |
| **`time` and timers** | Heartbeats, write deadlines | `extern "go" use time`. | P3 |

The P0 items are the language-arc ones. They are all coupled: you
can't ship channels without `select` being usable, you can't ship a
shared-mutable-state primitive without channels (one common
Overt-flavored answer is to make the primitive *be* a channel-backed
actor). Realistic estimate is 3–5 months of focused language work
to ship that block, plus 2–4 weeks of Go-back-end machinery to
emit goroutines + channels + `select` from the new constructs.

## 4. Phasing

The chat-relay can't be built end-to-end in one swoop. Here's a
sequence where each phase is a runnable demo, even if reduced:

### Phase 1 — single-connection echo (today)

A single client connects, sends one message, server echoes it back,
client disconnects. No concurrency. No hub. Just `net/http` upgrade
and one back-and-forth. The point is to surface the smallest set of
`extern "go"` bindings needed (HTTP upgrade, WS read, WS write) and
prove they emit and link.

**Gating items:** `extern "go" use net/http`, websocket binding.
**Language additions:** none.
**Estimate:** 2 weeks of Go-back-end work after `extern "go" use`
lands.

### Phase 2 — many connections, no rooms (after channels land)

Each client connects, every message is broadcast to every other
connected client. No room concept yet. Reader and writer are two
goroutines per client; broadcast is a shared channel that every
writer reads from.

**Gating items:** goroutines, channels (no `select` strictly needed
yet; can be one-shot reads).
**Estimate:** the channel and goroutine constructs themselves;
maybe 6 weeks once language design is decided.

### Phase 3 — rooms (after shared-state primitive lands)

Add the room directory. Hub is a `Mutex<Map<String, Room>>` (or
whatever the language settles on). Per-room broadcast.

**Gating items:** shared-mutable-state primitive.
**Estimate:** depends entirely on the design of that primitive, which
is the highest-stakes language decision in the queue.

### Phase 4 — graceful shutdown (after `select` and `context.Context`)

SIGINT triggers a shutdown signal that propagates through every
goroutine via `select`. Connections close cleanly within a deadline.

**Gating items:** `select`, signal-handling extern, context
representation.

### Phase 5 — production-realistic edges

Heartbeats, write deadlines, slow-consumer disconnect, message-rate
limiting, observability. Each is small individually; collectively
they're what "real" looks like.

## 5. Why this sample, not Tela itself

Tela is the vision: a real-world program that should be writable in
Overt. The chat-relay is the test bench. A few specific reasons it's
a better design driver than Tela:

1. **It fits in a head.** ~500 lines of Go, no domain knowledge
   required, no cryptography, no platform-specific quirks. Anyone
   reviewing the language design can read the chat-relay end-to-end
   in an afternoon.
2. **It exercises every Tela concurrency primitive.** Per-connection
   goroutines, channels for fan-out, `select` for cancellation, mutex
   over a directory map. The patterns scale up without changing
   shape; what works for the chat-relay generally works for Tela.
3. **Failure is fast.** When a language addition is wrong, the chat-
   relay surfaces it within minutes (the program deadlocks, fails to
   shut down, drops messages). Tela would take weeks to surface the
   same defect.
4. **It bounds the dep surface.** Two third-party Go packages (HTTP
   server, WS library), no GUI, no userspace networking. Each `extern
   "go" use` binding can be hand-validated against the chat-relay's
   needs.
5. **It's a usable thing.** A working WebSocket chat relay isn't a toy
   that gets thrown away after the language ships. It can grow into a
   real `samples/` example, then into a Marketplace-ready demo, then
   into the foundation for whatever Tela-shape thing comes next.

## 6. What to build first

Phase 1 is reachable on roughly today's Overt plus an `extern "go"
use` implementation. Phases 2–4 are the language-arc work, gated on
the goroutine / channel / `select` / shared-state primitive design.

The recommended sequence:

1. **Now**: ship `extern "go" use` (the Go analogue of `extern
   "csharp" use`). Mirror BindGenerator for Go's `go/types` reflection
   surface. Estimate: 2–4 weeks. Unblocks Phase 1 and any other
   "talk to Go stdlib" use case.
2. **Phase 1 demo**: single-connection echo. Estimate: 1 week after
   step 1. Ship as `samples/chat-relay/` at a `phase1` branch or tag.
3. **Concurrency design doc**: `docs/concurrency.md`. The companion
   doc to this one, scoping goroutine / channel / `select` / shared-
   state-primitive choices. Pinned-down design, not implementation.
4. **Implement the concurrency arc**: 3–5 months. Phases 2–4 of the
   chat-relay are the acceptance test.

Phase 1 is the immediate next deliverable; everything else gates on
language-design decisions that should be settled before code lands.
