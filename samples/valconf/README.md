# valconf

A small CLI that reads a JSON config file, validates each field
against refinement-typed boundaries, and reports either a one-line
success summary or the first validation failure. Pure-Overt CLI;
parallel in spirit with [`diffconf`](../diffconf/) — *valconf*
checks one config, *diffconf* compares two.

This is the refresh of the original `samples/config-validate/`,
which had a separate C# `Program.cs` handling argv + file I/O +
JSON deserialize because, when written, Overt couldn't do those
steps cleanly. The 0.2 stdlib has all the pieces; this version is
end-to-end Overt.

## What this sample shows

1. **Refinement types push domain rules into the type system.**
   `Port = Int where 1 <= self && self <= 65535` is a real type;
   any function signature that takes a `Port` is a claim that the
   argument is already in range. No defensive re-checks downstream.

2. **Errors are values, with exhaustive matching.** `ValidationError`
   is a closed enum. `describe` pattern-matches on it; if a future
   change adds a new variant, the compiler refuses to build until
   `describe` covers it (OV0308). No silent fall-through.

3. **`?` makes the happy path linear.** `validate` runs five checks,
   each one either narrowing its field to the refined type or
   short-circuiting to the first `Err`. The final `Config`
   constructor type-checks because every field has been proven to
   satisfy its refinement.

## Running

```sh
overt run valconf.ov configs/valid.json
# validated: listening on 0.0.0.0:8080, 4 workers

overt run valconf.ov configs/invalid-port.json
# validation failed: port 99999 is out of range; expected 1..65535
# (exit 1)

overt run valconf.ov configs/invalid-log-level.json
# validation failed: log_level 'verbose' is not recognized; expected one of: trace, debug, info, warn, error
# (exit 1)

overt run valconf.ov configs/invalid-empty-urls.json
# validation failed: upstream_urls must not be empty
# (exit 1)
```

In a CI script:

```sh
overt run valconf.ov ./prod-config.json || exit 1
```

## What changed in the refresh

The original sample carried a hybrid C#/Overt structure: the
validator core was Overt, the boundary plumbing was C#. Reading
through it required jumping between two languages. The refresh
collapses everything into one `.ov` file:

| | Original (`config-validate/`) | Refresh (`valconf/`) |
|---|---|---|
| Entry point | `Program.cs` (75 lines C#) | `main(args)` in `valconf.ov` |
| File read | `File.ReadAllText` (C# stdlib) | `File.read_to_string` (Overt stdlib) |
| JSON deserialize | `JsonSerializer.Deserialize<>` with options | One `extern "csharp" fn` binding |
| Result handling | C# `switch` over `ResultOk` / `ResultErr` | Overt `match` on Result |
| Build | `dotnet build` via Overt.Build | `overt run valconf.ov` directly |

The validator core itself didn't change much:
- `check_log_level` is now a `match raw { "trace" => ..., ... _ => ... }` instead of an if-else-if chain. Smaller and reads as a dispatch table.
- All else identical to the original Validator.ov logic.

## What this refresh revealed

**Nothing surfaced new emitter quirks.** The sample slid through
parser, type checker, and emitter without working around anything
— a quiet sign that the IIFE-cluster fixes from earlier this
session and the named-tuple plumbing held up.

The match-on-Result with multi-statement arms doing `?` propagation
in `main` (previously the canonical IIFE-with-? pattern that
needed workarounds) **just worked**. Same for the other patterns
this sample uses. That's the lesson: Overt has matured enough that
a sample of this complexity ports from C#-bordered to pure-Overt
without language fights.
