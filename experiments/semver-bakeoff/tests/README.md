# SemVer bake-off acceptance tests

Language-agnostic black-box test suite for the SemVer task defined
in [`../preregistration.md`](../preregistration.md). Each test case
specifies an argv invocation, optional stdin, expected stdout, and
expected exit code. The runner drives any executable that conforms
to the CLI surface in §2.1 of the pre-reg.

## Files

- `cases.jsonl` — one test case per line. Source of truth.
- `run.py` — invokes the candidate binary against every case and
  produces a per-case pass/fail report.

## Test case schema

```json
{
  "id":          "parse-001",                    // sortable, unique
  "category":    "parse",                        // parse | compare | match | sort | error
  "description": "basic version, no pre-release", // human-readable
  "argv":        ["parse", "1.2.3"],             // args after the binary
  "stdin":       "",                              // optional, default empty
  "stdout":      "1.2.3\n",                       // expected stdout, exact match
  "exit":        0,                               // expected exit code
  "source":      "SemVer 2.0.0 §2"                // where this case comes from
}
```

`stdout` is matched exactly (byte-for-byte). The runner does not
inspect stderr beyond reporting it on failure for diagnostics —
the success criterion is `stdout` plus `exit` only.

## Running

```
python run.py /path/to/semver-binary [--out results.json]
```

The runner:
- Reads `cases.jsonl`.
- For each case, invokes the binary with `argv` and `stdin` piped in.
- Captures stdout, stderr, exit code with a 10-second per-test timeout.
- Compares stdout (exact) and exit (exact) against expected.
- Writes per-case results to `--out` (default `results.json`).
- Prints a summary table to stdout.

## Adding cases

Append a single JSON object on its own line to `cases.jsonl`. Keep
`id` unique and sortable (zero-padded numeric suffix). Run `run.py
--validate` to confirm the file parses and IDs are unique.
