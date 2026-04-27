# portcheck

A small CLI that takes a hostname and a port specification, probes
each port via async TCP connect, and reports open/closed per port.
Sister sample to [`pingall`](../pingall/) (which uses
`Process.run` + `ping` for ICMP-style reachability); portcheck does
its own TCP connect via async FFI rather than shelling out.

The sixth pure-Overt CLI sample. Pairs with the new void-Task and
async-Result extern wraps shipped in the same commit — those make
binding C#'s throwing async methods (`TcpClient.ConnectAsync` here,
`HttpClient.GetStringAsync` and similar elsewhere) clean enough to
write a real CLI around.

## What this sample shows

1. **`Task<Result<T, E>>` async-extern wrap.** The new emitter
   pattern: declaring an extern as `-> Task<Result<(), IoError>>`
   makes the binds-target's thrown exceptions surface as `Err`
   values. The caller `.await`s the call and pattern-matches on
   the `Result`. No host exceptions escape the FFI boundary.

2. **`.await` composing inside an async fn body.** Standard
   shape: `!{io, async}` row, body uses `.await`, emits as
   `async Task<ReturnType>` on the C# side. The caller `.await`s
   too.

3. **Refinement types validating user input.** `Port = Int where
   1 <= self && self <= 65535`. The port-spec parser converts
   user strings into `List<Port>`; bad input becomes
   `Err(IoError)`.

4. **Named multi-return for per-port results.** `(port: Port, open:
   Bool)` — anonymous record at the result site; no top-level
   decl per ad-hoc shape.

## Running

```sh
overt run portcheck.ov localhost 22
# OPEN   22

overt run portcheck.ov localhost 22,99
# OPEN   22
# CLOSED 99
# (exit 1)

overt run portcheck.ov localhost 8000-8005
# OPEN   8000
# CLOSED 8001
# CLOSED 8002
# CLOSED 8003
# CLOSED 8004
# CLOSED 8005
# (exit 1)

overt run portcheck.ov localhost 80,443,8000-8005,9000
# (mixed; exit 0 if all open, 1 otherwise)
```

In a CI script:

```sh
overt run portcheck.ov localhost 8080,8443,5432 || exit 1
```

## What this sample doesn't do

**Parallel scanning.** The probes run sequentially, awaiting each
in turn. par_map's runtime expects a sync callback (`Func<T,
Result<U, E>>`); an async closure compiles to `Func<T,
Task<Result<U, E>>>`, which the sync signature can't accept.
Adding a `par_map_async` variant would fan out the awaits via
`Task.WhenAll` — separate stdlib op, queued. For small port sets
the total time is dominated by the slowest probe anyway, so the
sequential cost is bearable.

**Per-connection timeout.** `ConnectAsync` against a firewalled
host that drops SYN packets will hang for the OS default (~21s on
Windows). Best for localhost or cooperative hosts where closed
ports return RST immediately. A `Task.WaitAsync(TimeSpan)` wrap
would fix this; out of scope for the v0 sample.

## What shipping this revealed

Two emitter / language-helper bugs that surfaced and got fixed
along the way:

- `BodyContainsAwaitExpr` (the helper that decides whether a fn
  emits as `async`) didn't descend into `for each`, `while`, or
  `loop` bodies. An async fn whose only `.await` site was inside
  a loop emitted as a non-async method, then the body's `await`
  hit CS-error: "the await operator can only be used within an
  async method." Fixed by adding the missing AST cases.
- `EmitExpressionAsStatement` for `AwaitExpr` was emitting `(await
  foo);` (parens-wrapped expression-statement, CS0201) instead of
  `await foo;` (statement-shaped await, valid). Fixed by adding
  the AwaitExpr case to the statement-position switch.
- The formatter didn't have a case for `AwaitExpr` and rendered
  it as `/* ? AwaitExpr */`, breaking round-trip. Fixed.

Each one was a cluster member of "this surface is plumbed for the
common case (async externs returning `Task<T>` directly) but not
for the new wrap shapes." Same shape as the named-tuple plumbing
gaps that surfaced with `pingall` — the pattern is "a new TypeRef
or AST node lands; downstream walkers need parallel updates."
Worth keeping a checklist for the next language addition.
