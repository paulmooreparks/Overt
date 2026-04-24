# Overt.Build

![Overt mascot](https://raw.githubusercontent.com/paulmooreparks/Overt/main/assets/artwork/mascot-240.png)

**Overt.Build** is the MSBuild integration for the [Overt](https://www.nuget.org/packages/Overt) language: a task + targets package that compiles `.ov` files alongside `.cs` files during `dotnet build`. Add the package to any SDK-style `.csproj`, drop `.ov` files into the project, and the generated C# is fed to Csc the same way any other `Compile` item is. Diagnostics surface in the IDE's error list as normal Csc errors.

Overt is an agent-first programming language: every effect, error, dispatch, mutation, and piece of state is *overt*, visible at the call or declaration site, never concealed. Transpiles to readable host-language source (C# today, Go planned).

## Install

```xml
<ItemGroup>
  <PackageReference Include="Overt.Build" Version="0.1.0-dev.*" />
</ItemGroup>
```

Pre-release versions require a floating version range or an explicit version pin while Overt is in the `0.x.y` dev channel.

## Usage

Given a project `MyApp.csproj` with `Overt.Build` referenced, create `Greeter.ov`:

```
module greeter

fn greet(name: String) -> String {
    "hello, ${name}"
}
```

And call it from `Program.cs`:

```csharp
using Overt.Generated.Greeter;

Console.WriteLine(Module.greet("world"));
```

`dotnet build` transpiles `Greeter.ov`, compiles the generated C# alongside your own, and links the final assembly. No manual steps, no codegen scripts.

## What gets packed

- `build/Overt.Build.targets` &mdash; auto-imported when you reference this package
- `tasks/net9.0/Overt.Build.dll` &mdash; the MSBuild task
- `lib/net9.0/Overt.Runtime.dll` &mdash; the consumer-side runtime types the generated code references

## Links

- [GitHub repository](https://github.com/paulmooreparks/Overt)
- [Worked sample: config-validate](https://github.com/paulmooreparks/Overt/tree/main/samples/config-validate) &mdash; a CLI that validates JSON against a refinement-typed schema written in Overt
- [Design document](https://github.com/paulmooreparks/Overt/blob/main/DESIGN.md)
- [AGENTS.md](https://github.com/paulmooreparks/Overt/blob/main/AGENTS.md) &mdash; operational reference for agents authoring Overt
