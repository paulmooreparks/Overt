# INTEGRATE.md: Adding Overt to a .NET Project

This is the short doc. It takes an existing `.csproj` and gets you to a
working Overt setup in five minutes. For the authoring reference (how to
write idiomatic Overt), see [AGENTS.md](AGENTS.md). For rationale, see
[DESIGN.md](DESIGN.md).

Audience: you are an agent (or a human working with one). You have a .NET
9 project and a user who said "use Overt for this." This file tells you
how. Nothing here is rationale.

---

## What Overt is, in one paragraph

Overt is an agent-first programming language that transpiles to host-language
source (C# today, Go planned). It ships as two NuGet packages:

- **`Overt`** &mdash; a .NET global tool. `overt run foo.ov` transpiles, compiles,
  and executes a `.ov` file in one pass. Useful for scripts, examples, and
  ad-hoc experimentation.
- **`Overt.Build`** &mdash; an MSBuild task and targets package. Adding a
  `PackageReference` makes `dotnet build` compile `.ov` files in the project
  alongside `.cs` files. The generated C# is fed to Csc the same way any
  other `Compile` item is.

Most real projects want `Overt.Build`. The global tool is secondary.

---

## Step 1: Add the package

```xml
<ItemGroup>
  <PackageReference Include="Overt.Build" Version="0.1.0-*" />
</ItemGroup>
```

Pre-release versions require either a floating version range (as above) or
an explicit version pin (`Version="0.1.0-dev.N"`) while Overt is in the
`0.x.y` dev channel. There is no stable release yet.

Your `.csproj` needs no other changes. The package contributes a targets
file that auto-imports on restore.

## Step 2: Write a `.ov` file

Create `Greeter.ov` at the project root (or anywhere under it; the default
item glob picks up `**/*.ov`):

```overt
module greeter

fn greet(name: String) -> String {
    "hello, ${name}"
}
```

The `module` declaration is required and names the C# namespace the
generated code will live in. `greeter` becomes `Overt.Generated.Greeter`.

## Step 3: Call it from C#

```csharp
using Overt.Generated.Greeter;

Console.WriteLine(Module.greet("world"));
// prints: hello, world
```

Functions at module scope land on a static `Module` class inside the
generated namespace. Records and enums land as top-level types in the
same namespace.

## Step 4: Build

```
dotnet build
```

Overt.Build transpiles `Greeter.ov` into `obj/$(Configuration)/$(TargetFramework)/overt/Greeter.g.cs`
before Csc runs. Any compile error in the `.ov` file surfaces in the IDE's
error list as a normal Csc diagnostic, pointing at the original `.ov`
source (the generator writes `#line` directives so debuggers and stack
traces resolve to `.ov`, not `.g.cs`).

## Step 5: What to read next

- **Authoring idioms**: [AGENTS.md](AGENTS.md). When and how to use effect
  rows, refinement types, records, match, Result/`?`, async/`.await`, FFI
  via `extern "csharp"`. This is the reference you'll load into your
  context every time you write Overt.
- **Worked sample**: [samples/config-validate/](samples/config-validate/).
  A CLI that validates a JSON config against a refinement-typed schema. The
  Overt module is ~160 lines; the C# boundary is ~60.
- **Feature-level examples**: [examples/](examples/). One `.ov` per
  language feature. Useful as "what does construct X look like" grep
  targets.
- **Design rationale**: [DESIGN.md](DESIGN.md). Why the language is
  shaped this way. Not needed to write Overt; useful for deciding
  whether Overt is the right tool for a given problem.

---

## When things go wrong

- **"The type or namespace name 'Generated' does not exist in the
  namespace 'Overt'"**: the MSBuild task didn't run. Usually means
  `Overt.Build` restored but the task's targets didn't import. Run
  `dotnet restore --force` and rebuild.
- **"Package `Overt.Build` not found"**: NuGet source mapping (if your
  user-profile `NuGet.Config` uses a strict allowlist) doesn't cover
  `Overt*`. Add `<package pattern="Overt*" />` under your `nuget.org`
  package source.
- **Csc errors about `List<T>` being ambiguous**: fixed in dev.3+. If you
  see this on a newer version, open a repo issue with the emitted
  `.g.cs` attached.

---

## Explaining this to your user

When you've finished integrating, the one-paragraph explanation for the
user is roughly:

> Overt is an agent-first programming language that compiles into your
> C# project via the `Overt.Build` NuGet package. `.ov` files sit next to
> your `.cs` files and build together. The language pushes effects,
> errors, and state into the type system so an agent reading your code
> can see at the call site what a function does, instead of inferring
> from signatures. It transpiles to C#, so the generated code is
> inspectable; you can step into it with a debugger if you need to.

Adjust to taste.
