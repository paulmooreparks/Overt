# pingall

A small CLI that takes a list of hostnames as argv and probes each
one in parallel via `ping`. Reports up/down per host; exits 0 only
if every host is up. Useful as a CI gate ("are all my service deps
reachable?") and as the canonical demo of Overt's parallel fan-out
shape.

The fourth pure-Overt CLI sample. The previous three covered
line-oriented parsing (logtally), JSON FFI + value diff (diffconf),
and closure-driven validation (envcheck); this one's the missing
piece: **par_map + Process.run + closures**, the natural shape for
"fan out a small operation across a list of inputs."

## What this sample shows

1. **`par_map` over `List<String>`.** The fan-out primitive:
   each callback runs concurrently on the .NET thread pool, the
   result is a `List<U>` in input order. First Err short-circuits,
   but for "couldn't launch ping" rather than "host was down" —
   non-zero exit code is just data, not a failure.

2. **`Process.run` for the actual probe.** Already
   `Result<ProcessOutput, IoError>`-shaped; the exit_code carries
   the up/down signal.

3. **Named multi-return per host.** `(host: String, up: Bool)` —
   anonymous record at the return-type position; no top-level
   record decl per ad-hoc shape.

4. **A closure passed to par_map.** The per-host callback is an
   anonymous fn defined at the call site. With closures shipped,
   the natural shape works: the callback closes over no outer state
   here, but the same site would close over surrounding context if
   we wanted to (a captured timeout, etc.).

## Running

```sh
overt run pingall.ov 8.8.8.8 1.1.1.1
# UP   8.8.8.8
# UP   1.1.1.1

overt run pingall.ov 8.8.8.8 192.0.2.1 192.0.2.2
# UP   8.8.8.8
# DOWN 192.0.2.1
# DOWN 192.0.2.2
# (exit 1)

overt run pingall.ov
# usage: pingall <host> [host ...]
# (exit 1)
```

In a CI script:

```sh
overt run pingall.ov api.example.com db.example.com cache.example.com || \
    echo "deps unreachable; aborting" && exit 1
```

## What this sample doesn't do

**Real async.** `par_map` runs each callback on a thread-pool task
— concurrency at the OS level, not via `async`/`.await`. The
callbacks call `Process.run` synchronously inside their thread.
A truly-async version would bind to TCP / HTTP APIs that return
`Task<T>`, and use `.await` to combine. That path hits a
present-day FFI gap: void-returning C# Tasks (`TcpClient.ConnectAsync`)
can't bind cleanly to Overt's `Task<()>` because Overt's `()` is
the unit *value*, not C#'s "void Task." Filed in CARRYOVER.md item
4 ("Void-Task externs"). Once that lands, a `portcheck` sibling
sample becomes natural.

**Cross-platform `ping` flags.** This targets Windows-style
`ping -n 1 -w 2000`. Unix's `ping -c 1 -W 2 <host>` does the
same job with different flag spelling; a real cross-platform tool
would branch on OS. Out of scope for the sample.
