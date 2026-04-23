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
        var resolved = Overt.Compiler.Semantics.NameResolver.Resolve(parse.Module);
        var typed = Overt.Compiler.Semantics.TypeChecker.Check(parse.Module, resolved);
        var csharp = CSharpEmitter.Emit(parse.Module, typed);

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
    // Five examples still have known emitter gaps and are NOT yet in this theory:
    //
    //   - bst.ov          `List.empty()` needs explicit type args emitted from the
    //                     enclosing return-type context; context threading isn't wired.
    //   - dashboard.ov    same `List.empty()` issue, plus tuple-destructure of a
    //                     parallel-block placeholder whose `.Unwrap()` has no receiver.
    //   - effects.ov      generic method inference — `apply_twice<T, E>` called with
    //                     named args blocks C#'s argument-driven inference; needs
    //                     explicit type args emitted from the enclosing-call context.
    //   - refinement.ov   `Ok(List<T>)` doesn't target-type into `Result<NonEmpty<T>, E>`
    //                     without an implicit conversion on the wrapper record (runtime
    //                     work, not emitter).
    //   - trace.ov        `fn print_event(...) -> ()` body is `println(...)` which
    //                     evaluates to Result<Unit,IoError>. Per DESIGN.md §11 an
    //                     ignored Result is a compile error; the example needs updating
    //                     to `let _ = println(...)` or similar.
    //
    // All of these are tractable; most need expected-type threading from the enclosing
    // return/arg context into child expression emission. Separate session.
    [Theory]
    [InlineData("hello.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("race.ov")]
    [InlineData("inference.ov")]
    [InlineData("ffi.ov")]
    [InlineData("bst.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("effects.ov")]
    [InlineData("refinement.ov")]
    [InlineData("trace.ov")]
    public void Emit_Example_ProducesCompilableCSharp(string file)
    {
        var errors = CompileEmittedCSharp(file);
        if (errors.Length == 0) return;

        var details = string.Join("\n", errors.Select(e =>
            $"  {e.Id}: {e.GetMessage()}  at {e.Location.GetLineSpan().StartLinePosition}"));
        Assert.Fail($"Emitted C# for {file} has compile errors:\n{details}");
    }
}
