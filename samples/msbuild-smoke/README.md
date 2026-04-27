# msbuild-smoke

The minimal worked example of consuming Overt from a C# `.csproj`
via the [`Overt.Build`](../../src/Overt.Build/) MSBuild integration.
Two files of substance: a one-line `.ov` module and a Program.cs
that calls into it. Builds with plain `dotnet build`; no manual
transpile step.

If you're integrating Overt into an existing C# project, this is
the shape to start from.

## What's here

- **`Greeter.ov`** — one Overt fn (`greet(name) -> String`).
- **`Program.cs`** — calls `Module.greet("world")` from C#, prints
  the result, exits non-zero on mismatch.
- **`MsbuildSmoke.csproj`** — references `Overt.Build` (in-repo
  during dev; `<PackageReference Include="Overt.Build" Version="0.2.0-dev.*" />`
  for consumers off nuget.org).

## How the integration works

`Overt.Build` injects a build target that runs the Overt compiler
on every `.ov` file in the project (`<OvertCompile>` items, or all
`.ov` by default), generating `.g.cs` files into
`$(IntermediateOutputPath)overt/`. These get added to the C#
compile group automatically, so `Module.greet` is in scope by the
time `csc` runs.

```sh
cd samples/msbuild-smoke
dotnet build
dotnet run
# hello, world
```

The generated namespace is `Overt.Generated.<module-name>` — here
`Overt.Generated.Greeter`. The fn lives on the static `Module`
class within that namespace.

## When to use this shape vs pure-Overt CLI

- **This (Overt.Build hybrid)** — when you have an existing C#
  codebase and want to use Overt for some specific component
  (validation, parsing, domain logic). C# owns the boundary
  layers; Overt owns the typed-domain piece.
- **Pure-Overt CLI** (see [`samples/valconf/`](../valconf/),
  [`samples/diffconf/`](../diffconf/), etc.) — when the whole
  program can be written in Overt and run via `overt run` or
  built as a standalone tool. No C# entry point.

The Overt-can-do-it-all-now pattern is what most of the other
samples demonstrate; `msbuild-smoke` exists for the cases where
a C# host is the right shape.
