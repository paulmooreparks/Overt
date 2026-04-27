# diffconf

A small CLI that diffs two JSON config files. Reads both, deserializes
each into a `Config` record, walks the fields, and prints what
changed. Pairs visually with [`config-validate`](../config-validate/):
*validate* checks one config; *diffconf* compares two.

The intended use case is CI drift checks — `diffconf prod.json
staging.json || echo "config drift detected"` slots into a pipeline
step exactly the way Unix `cmp` and `diff` do.

## What this sample shows

The second pure-Overt CLI sample (after [`logtally`](../logtally/)),
this one leaning on JSON FFI rather than line-oriented parsing:

1. **JSON deserialization via `extern "csharp"`.** One line of FFI
   (`Deserialize<Config>`) pulls a typed record straight out of a
   JSON file. Same path
   [`examples/csharp/json.ov`](../../examples/csharp/json.ov) takes
   for round-trip — proven and idiomatic.

2. **Sum-typed diff result.** `DiffEntry` is either `Changed` (scalar
   fields) or `ListChanged` (carries `added` / `removed` lists). The
   render fn pattern-matches on it; if a future field type added a
   `MapChanged` variant, the compiler would refuse to build until
   render handled it.

3. **Set-style list diff with captured-state closures.** `list_added`
   and `list_removed` use `filter` with an anonymous fn that captures
   the other-side list to test membership. Two functions, four lines
   each — the natural shape for "elements of A not in B."

## Running

```sh
# Diff two configs — exits 1, prints hunks to stdout
overt run diffconf.ov configs/baseline.json configs/staging.json
# host: "0.0.0.0" → "127.0.0.1"
# port: 8080 → 9090
# log_level: "info" → "debug"
# upstream_urls:
#   + https://staging-1.example.com
#   + https://staging-2.example.com
#   - https://api.example.com

# Same config compared to itself — exits 0
overt run diffconf.ov configs/baseline.json configs/baseline.json
# configs match
```

Exit codes follow Unix `diff` / `cmp`:

- `0` — configs match
- `1` — configs differ (hunks on stdout)
- `1` — I/O or JSON parse failure (error on stderr; same exit because
  `overt run` only distinguishes Ok / Err)

The diff output goes to stdout, the exit code carries the
match-vs-differ signal, and stderr stays clean. A CI step can branch
on `$?` directly without grepping anything.

## What this sample used to flag, but doesn't anymore

Two friction notes lived here before:

- *"No closures means `filter` can't capture context."* Closures
  shipped; `list_added` and `list_removed` are now four-line
  filter calls.
- *"`overt run` prints a trailing stderr line on Err returns,
  polluting diffconf's CI output."* Trailer removed; the runner now
  forwards exit code without commentary, matching `dotnet run` /
  `go run` behavior. Programs that want diagnostic output write it
  themselves with `eprintln` before returning Err — see the usage
  branch in `main` for the pattern.
