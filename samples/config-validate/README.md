# config-validate

A small CLI that validates a JSON config file against a refinement-typed
schema. The domain logic (types, validation, error formatting) is a single
Overt module, [Validator.ov](Validator.ov); the C# entry point in
[Program.cs](Program.cs) handles argv, file IO, and JSON deserialization.

## What this sample shows

Overt earns its seat at the table when the code is doing validation, error
handling, or any state check that needs to be trustworthy downstream.
`Validator.ov` demonstrates three patterns that are tedious to get right in
ordinary .NET and become load-bearing here:

1. **Refinement types push domain rules into the type system.**
   `Port = Int where 1 <= self && self <= 65535` is a type; any function
   signature that takes a `Port` is a claim that the argument is already
   in range. No defensive re-checks downstream.

2. **Errors are values, with exhaustive matching.**
   `ValidationError` is a closed enum. `describe` pattern-matches on it,
   and if a future change adds a new variant the compiler refuses to
   build `describe` until its arm is added. No silent fall-through.

3. **`?` makes the happy path linear.**
   `validate` runs five checks, each one either narrowing its field to
   the refined type or short-circuiting to the first `Err`. The final
   `Config` constructor type-checks because every field has been proven
   to satisfy its refinement.

## Running

The build integration picks up `Validator.ov` automatically via the
Overt.Build MSBuild task. Until `Overt.Build` publishes to nuget.org,
this sample imports the task directly from the in-repo build output
of `src/Overt.Build`. Real consumers will replace the `<Import>` in
[ConfigValidate.csproj](ConfigValidate.csproj) with a one-line
`<PackageReference Include="Overt.Build" Version="0.1.0-dev.*" />`.

```sh
dotnet build

dotnet run -- configs/valid.json
#   validated: listening on 0.0.0.0:8080, 4 workers

dotnet run -- configs/invalid-port.json
#   validation failed: port 99999 is out of range; expected 1..65535

dotnet run -- configs/invalid-log-level.json
#   validation failed: log_level 'verbose' is not recognized; expected one of: trace, debug, info, warn, error

dotnet run -- configs/invalid-empty-urls.json
#   validation failed: upstream_urls must not be empty
```

Exit code is `0` on validation success, `1` on any failure.

## Where the Overt / C# split lands

The split here is the one most real .NET projects would arrive at on their
own. C# owns the boundary: argv parsing, `File.ReadAllText`, JSON
deserialization, exception catching. Overt owns the domain core:
refinement types, validation, typed errors, pretty-printing. Overt can do
the boundary work too, via `extern "csharp"` and `!{io}` effect rows, and
a larger program might push more of it across the line. For a sample this
size the C#-boundary-plus-Overt-core shape reads most naturally.
