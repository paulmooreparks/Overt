# envcheck

A small CLI that reads a `.env`-style file and validates each key
against a schema declared as Overt code. Reports missing required
keys, invalid values, and unknown keys; exits 1 on any failure.

The third pure-Overt CLI sample (after [`logtally`](../logtally/) and
[`diffconf`](../diffconf/)). Each sample picks a different shape:
logtally is line-oriented parsing, diffconf is JSON FFI + value diff,
envcheck is **closure-driven validation with a schema-as-data
dispatch table**.

## What this sample shows

1. **Closures stored as Map values.** The schema is a
   `Map<String, SchemaEntry>` where `SchemaEntry` carries a
   `fn(String) -> Result<(), String>` validator. Adding a new
   variable to the schema is one `Map.insert` call. The validation
   loop pulls the right validator per key by lookup, then invokes
   the closure.

2. **Validators built by closure-returning factory fns.** `non_empty()`,
   `int_in_range(low, high)`, and `one_of(allowed)` each return an
   anonymous fn with the parameters captured. The schema lines stay
   readable: `validator = int_in_range(low = 1, high = 65535)`
   instead of a wall of inline closure literals.

3. **Sum-typed `Failure` with three variants** (`MissingRequired`,
   `Invalid`, `Unknown`). Each render-site exhaustively matches; if a
   future variant lands, the compiler refuses to build until every
   consumer handles it. The actual signal lives in the *count* of
   failures returned; an empty list means OK.

4. **Pure-Overt CLI built on the 0.2 stdlib.** `args()`, `File.read_lines`,
   `Map.entries`, `String.parse_int`, `String.split` (none in this
   sample, but readily available) — argv to output, no C# entry
   point. The runner forwards exit code; stdout is the report;
   stderr stays clean.

## Running

```sh
overt run envcheck.ov envs/prod.env
# OK: 5 key(s) validated against 5 schema entry(ies)

overt run envcheck.ov envs/bad.env
# INVALID DATABASE_URL (""): must be non-empty
# INVALID PORT ("99999"): must be in 1..65535; got 99999
# UNKNOWN TYPO_KEY: not declared in schema
# INVALID LOG_LEVEL ("verbose"): must be one of: debug, info, warn, error
# (exit 1)

overt run envcheck.ov envs/missing.env
# MISSING WORKER_COUNT: required key not set
# MISSING DATABASE_URL: required key not set
# MISSING PORT: required key not set
# MISSING LOG_LEVEL: required key not set
# (exit 1)
```

In a CI script:

```sh
overt run envcheck.ov ./prod.env || exit 1
```

## Schema-as-code, briefly

```overt
fn build_schema() -> Map<String, SchemaEntry> {
    // ...
    s = Map<String, SchemaEntry>.insert(
        map = s,
        key = "PORT",
        value = SchemaEntry {
            validator = int_in_range(low = 1, high = 65535),
            required = true,
        },
    )
    // ...
}
```

`int_in_range` is a closure-returning factory:

```overt
fn int_in_range(low: Int, high: Int) -> fn(String) -> Result<(), String> {
    fn(value: String) -> Result<(), String> {
        match String.parse_int(s = value) {
            Err(_) => Err("must be an integer in ${low}..${high}"),
            Ok(n) => {
                if n >= low && n <= high { Ok(()) }
                else { Err("must be in ${low}..${high}; got ${n}") }
            },
        }
    }
}
```

The `low` and `high` parameters are captured by the inner closure;
each `int_in_range(low = 1, high = 256)` call returns a fresh
validator with that specific range baked in.

## Earlier friction, now resolved

The first version of this sample carried three workaround comments
for emitter quirks (side-effect-only `if`/`match` inside for-each
emitting as expression-statement, `Result` patterns inside
IIFE-wrapped match arms missing their `ResultOk`/`ResultErr` rewrite,
and generic record-literal type args lost in IIFE contexts). The
underlying issues — block-trailing-expression-as-statement routing
and pattern-binding type inference for `Some`/`Ok`/`Err` against
`Option<T>`/`Result<T, E>` — have been fixed. The workarounds are
gone and the sample reads naturally.

The `EnvLine` record stayed (rather than reverting to `Pair<String,
String>`) because a named field per piece reads better at the use
site than `pair.left` / `pair.right` — independent design choice,
not a workaround.
