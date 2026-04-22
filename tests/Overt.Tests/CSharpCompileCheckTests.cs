using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Overt.Backend.CSharp;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// For each example program, verifies that the C# the emitter produces actually compiles
/// against Overt.Runtime. This is the forcing function that keeps the emitter honest and
/// the runtime prelude complete — if a construct emits shape that isn't valid C# or
/// references a runtime name that doesn't exist, this surfaces immediately rather than
/// at integration time.
///
/// Compile is in-process via Roslyn. We don't attempt to execute; just to type-check.
/// Reference assemblies: .NET 9 BCL (System.Runtime, System.Console, System.Collections,
/// System.Linq) plus Overt.Runtime.
/// </summary>
public class CSharpCompileCheckTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    // Runtime assembly and the BCL assemblies the generated code depends on.
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Force Overt.Runtime to load by touching a type from it; without this, it
        // wouldn't appear in AppDomain.CurrentDomain.GetAssemblies() because no test
        // code references it directly.
        var runtimeAssembly = typeof(Overt.Runtime.Unit).Assembly;

        var refs = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (string.IsNullOrEmpty(asm.Location)) continue;
            refs.Add(MetadataReference.CreateFromFile(asm.Location));
        }
        // Explicitly add Overt.Runtime by its known location in case the loop missed it.
        if (!string.IsNullOrEmpty(runtimeAssembly.Location)
            && !refs.Any(r => r.Display?.Contains("Overt.Runtime") == true))
        {
            refs.Add(MetadataReference.CreateFromFile(runtimeAssembly.Location));
        }
        return refs.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> CompileEmittedCSharp(string ovFile)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, ovFile));
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var csharp = CSharpEmitter.Emit(parse.Module);

        var tree = CSharpSyntaxTree.ParseText(csharp, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName: Path.GetFileNameWithoutExtension(ovFile),
            syntaxTrees: new[] { tree },
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    // Examples whose emitted C# compiles cleanly against Overt.Runtime. Each one here
    // is a regression guarantee: the emitter, runtime, and example file are all
    // compatible, end-to-end, shape-wise.
    //
    // The remaining six examples (bst, dashboard, effects, ffi, refinement, trace) do
    // NOT yet compile-check cleanly and are NOT in this theory. Each needs semantic
    // information the untyped emitter can't synthesize:
    //
    //   - bst.ov          pattern lowering of `Tree.Empty` / `Tree.Node { ... }` as
    //                     switch arms (needs type info to resolve enum variants).
    //   - dashboard.ov    tuple-destructure `let (users, orders) = parallel {...}` —
    //                     the parallel placeholder `default!` has no target type.
    //   - effects.ov      generic method inference — `apply_twice<T,E>` called with
    //                     named args cannot infer type parameters.
    //   - ffi.ov          `Some(home) => ...` match arm needs Option-variant pattern
    //                     lowering; `unsafe { call(cs) }` used in expression position
    //                     wants type info to pick statement vs expression form.
    //   - refinement.ov   generic type aliases (`type NonEmpty<T> = ...`) don't lower
    //                     to C# using-directives; need record wrapping.
    //   - trace.ov        same enum-variant widening issue as state_machine but on
    //                     match-arm RHS where the variant lives under `Err(...)`.
    //
    // Each is scheduled for the type-checker arc. As they clear, move them up into
    // the InlineData list.
    [Theory]
    [InlineData("hello.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("race.ov")]
    [InlineData("inference.ov")]
    public void Emit_Example_ProducesCompilableCSharp(string file)
    {
        var errors = CompileEmittedCSharp(file);
        if (errors.Length == 0) return;

        var details = string.Join("\n", errors.Select(e =>
            $"  {e.Id}: {e.GetMessage()}  at {e.Location.GetLineSpan().StartLinePosition}"));
        Assert.Fail($"Emitted C# for {file} has compile errors:\n{details}");
    }
}
