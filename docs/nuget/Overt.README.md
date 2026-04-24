<img src="https://raw.githubusercontent.com/paulmooreparks/Overt/main/assets/artwork/mascot.png" alt="Overt mascot" align="right" width="180">

# Overt

**Overt** is an agent-first programming language, written and maintained primarily by LLM agents with humans in a review and audit role. Every effect, error, dispatch, mutation, and piece of state is *overt*: visible at the call or declaration site, never concealed. Overt transpiles to readable source in a host language (C# today, Go planned).

This package is the Overt CLI as a .NET global tool. After install, the `overt` command transpiles, compiles, and runs `.ov` source files; emits any intermediate compiler stage for inspection; formats source; and generates `extern "csharp"` facades for .NET types.

## Install

```
dotnet tool install --global Overt --prerelease
```

The `--prerelease` flag is required while Overt is in the `0.x.y` dev channel. Install a specific version with `--version 0.1.0-dev.3`.

## Try it

```
cat > hello.ov <<'EOF'
module hello

fn main() !{io} -> Result<(), IoError> {
    println("Hello, LLM!")?
    Ok(())
}
EOF

overt run hello.ov
```

Outputs:

```
Hello, LLM!
```

## Using Overt inside a C# project

Add the [`Overt.Build`](https://www.nuget.org/packages/Overt.Build) package to any `.csproj`; drop `.ov` files next to your `.cs` files; `dotnet build` compiles them all together. See [samples/config-validate/](https://github.com/paulmooreparks/Overt/tree/main/samples/config-validate) for a worked example.

## Links

- [GitHub repository](https://github.com/paulmooreparks/Overt) &mdash; full source, examples, tests
- [Design document](https://github.com/paulmooreparks/Overt/blob/main/DESIGN.md) &mdash; rationale and reference
- [Why Overt](https://github.com/paulmooreparks/Overt/blob/main/docs/why-overt.md) &mdash; the thesis essay
- [AGENTS.md](https://github.com/paulmooreparks/Overt/blob/main/AGENTS.md) &mdash; operational reference for agents authoring Overt
