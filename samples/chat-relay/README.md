# chat-relay (Phase 1)

A single-connection WebSocket echo server. The smallest meaningful
Tela-shape program written in Overt: it exercises every FFI path
the Go back end ships today, and it's the acceptance test for the
FFI design memo at [`docs/ffi.md`](../../docs/ffi.md).

The full design (Phases 1–5) is in
[`docs/samples/chat-relay.md`](../../docs/samples/chat-relay.md).
This sample implements only Phase 1.

## What it demonstrates

- **`extern "go" type`**: opaque host types for
  `http.ResponseWriter`, `*http.Request`, `*websocket.Conn`,
  `websocket.Upgrader`. Each binds-string is the verbatim Go-side
  type expression including pointer markers and package paths.
- **`extern "go" fn`**: static functions like `http.HandleFunc`
  and `http.ListenAndServe`. The package selector resolves to
  the import path (or via the `from` clause for non-stdlib
  paths).
- **Function-typed extern parameters**: `http_handle` takes
  `handler: fn(ResponseWriter, Request) -> ()`; the Overt-side
  `echo_handler` named fn passes through verbatim.
- **`extern "go" instance fn`**: `conn.read_message()` and
  `conn.write_message(msg = ...)` route through the type-checker's
  method-call resolution, then through the generated shim.
- **`Result<T, IoError>` wrap**: every fallible Go call surfaces
  as `Result<T, IoError>` via the emitter's automatic
  (T, error) → Result conversion.

## Files

- `echo.ov` — the Overt source. ~80 lines, contains all the
  application logic.
- `helpers.go` — three hand-written Go shims that adapt
  rough-edged gorilla/websocket APIs to the (T, error) and
  zero-arg-constructor shapes the FFI binds against. ~30 lines.
- `go.mod` — Go module declaration with the in-repo runtime
  replaced via the `replace` directive.

## Build and run

The `overt` CLI's `--emit=go` mode is the transpilation step;
`go build` is the rest.

```
# From samples/chat-relay/
dotnet run --project ../../src/Overt.Cli -- --emit=go echo.ov > echo.ov.go
go build -o chat-relay .
./chat-relay
```

Once running, the server listens on `:8080`. Connect with any
WebSocket client to test:

```
# Using websocat: https://github.com/vi/websocat
websocat ws://localhost:8080/echo

# Type any message; the server echoes it back. Ctrl+C to exit.
```

## Why a hand-written `helpers.go`

Three of the websocket APIs we need don't fit the FFI's
default shim conventions:

- `Upgrader{...}` initialization needs struct-literal access,
  which the Overt FFI doesn't expose.
- `ReadMessage` returns `(messageType int, p []byte, error)` —
  three values, where the FFI's Result wrap expects two.
- `WriteMessage` takes a `messageType int` argument that an echo
  server fixes at `TextMessage`.

Each helper is five lines of Go. When `extern "go" use
"<package>"` (the bulk-import work scoped in
[`docs/ffi.md`](../../docs/ffi.md)) ships, the generator
will produce equivalent facades automatically and this file
can shrink.

## What's next

The sample stops at Phase 1 because Phase 2 (many connections
broadcasting to a room) needs language-level concurrency
primitives that don't exist yet:

- `goroutine`-equivalent for the per-connection reader/writer
  pumps,
- `channel`s for the broadcast fan-out,
- `select` for multiplexing reads and shutdown signals,
- a shared-mutable-state primitive for the room directory.

The design for those is in
[`docs/concurrency.md`](../../docs/concurrency.md). Phase 2 lands
once the language ships them.
