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

3. **Set-style list diff in pure Overt.** Without closures, `filter`
   with a captured-context predicate isn't an option, so the diff
   builds with `let mut` + a for-each loop + `List.contains` lookups
   on the other side. Verbose, but the pattern is direct and the
   call site reads as intent.

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
- `1` — configs differ (hunks on stdout, diagnostic line on stderr)
- `1` — I/O or JSON parse failure (error on stderr; same exit because
  `overt run` only distinguishes Ok / Err)

## Things the sample reveals

Two pieces of present-day friction worth flagging — both are
workarounds in source today, and both are candidates for a future
language pass:

**No closures means filter can't capture context.** The natural way
to compute "elements of `right` not in `left`" is
`filter(list = right, predicate = (item) => !contains(left, item))`.
Overt has no anonymous fns, so the predicate would have to be a
top-level fn, which can't reference `left`. The workaround is
explicit iteration with a free helper fn (`append_if_missing`); see
`list_added` / `list_removed`. Same shape applies to `Set<T>`
operations — the missing `Set.to_list` op has prevented using the
nicer set-difference path.

**Non-numeric exit codes from `overt run`.** The runner exits 0 on
`Ok`, 1 on `Err`, and prints `overt run: main returned Err: <err>`
on stderr. For a CLI that *uses* the differ-vs-match split as its
contract (`diffconf` does, like `cmp`), the trailing stderr line is
noise. A standalone-exe build via `Overt.Build` skips the runner
and gets clean exit codes natively.
