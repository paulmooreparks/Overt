using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Overt.Backend.CSharp;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// End-to-end runs for transpiled Overt programs that exercise the real stdlib
/// (map / filter / fold / par_map / Trace). Compiles the emitted C# in-memory via
/// Roslyn, loads the resulting assembly, and invokes <c>Module.main()</c> — so a
/// regression in either the emitter *or* the runtime shows up as a runtime failure
/// here, not a silent compile-only pass.
///
/// Complements <see cref="CSharpCompileCheckTests"/> (compile-only, all 12 examples)
/// and <see cref="StdlibRuntimeTests"/> (direct C# calls into Prelude).
/// </summary>
public class StdlibTranspiledEndToEndTests
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var runtimeAssembly = typeof(Overt.Runtime.Unit).Assembly;
        var refs = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (string.IsNullOrEmpty(asm.Location)) continue;
            refs.Add(MetadataReference.CreateFromFile(asm.Location));
        }
        if (!string.IsNullOrEmpty(runtimeAssembly.Location)
            && !refs.Any(r => r.Display?.Contains("Overt.Runtime") == true))
        {
            refs.Add(MetadataReference.CreateFromFile(runtimeAssembly.Location));
        }
        return refs.ToImmutable();
    }

    /// <summary>
    /// Transpile Overt source → compile the C# in-memory → load the assembly →
    /// invoke <c>Module.main</c>. Returns whatever main returned as a boxed object;
    /// stdout is captured and returned alongside.
    /// </summary>
    private static (object? Result, string Stdout) CompileAndRun(
        string ovSource, string assemblyName)
    {
        var lex = Lexer.Lex(ovSource);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var resolved = Overt.Compiler.Semantics.NameResolver.Resolve(parse.Module);
        Assert.Empty(resolved.Diagnostics);
        var typed = Overt.Compiler.Semantics.TypeChecker.Check(parse.Module, resolved);
        Assert.Empty(typed.Diagnostics);
        var csharp = CSharpEmitter.Emit(parse.Module, typed);

        var tree = CSharpSyntaxTree.ParseText(csharp, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: new[] { tree },
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errs = string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => "  " + d.GetMessage()));
            throw new Xunit.Sdk.XunitException(
                "Emitted C# failed to compile:\n" + errs + "\n\nSource:\n" + csharp);
        }
        ms.Position = 0;
        var asm = Assembly.Load(ms.ToArray());

        var moduleType = asm.GetTypes().FirstOrDefault(t => t.Name == "Module")
            ?? throw new InvalidOperationException("Module type not found in emitted assembly");
        var mainMethod = moduleType.GetMethod("main", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Module.main not found");

        using var sw = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = mainMethod.Invoke(null, null);
            return (result, sw.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
        }
    }

    [Fact]
    public void Transpiled_MapFilterFold_RunsEndToEnd()
    {
        // A small program: sum of squares of the evens in [1..5].
        // 2*2 + 4*4 = 4 + 16 = 20 → printed.
        // Sum of squares of the evens in [1..5]: 2² + 4² = 4 + 16 = 20.
        const string src = """
            module e2e_mff

            fn main() !{io} -> Result<(), IoError> {
                let total: Int =
                    build_list()
                      |> filter(is_even)
                      |> map(square)
                      |> fold(seed = 0, step = add)

                println("total=${total}")?
                Ok(())
            }

            fn build_list() -> List<Int> {
                List.concat_three(
                    first  = List.singleton(1),
                    middle = List.concat_three(
                        first  = List.singleton(2),
                        middle = List.singleton(3),
                        last   = List.singleton(4)),
                    last   = List.singleton(5))
            }

            fn is_even(n: Int) -> Bool { n % 2 == 0 }
            fn square(n: Int) -> Int { n * n }
            fn add(acc: Int, n: Int) -> Int { acc + n }
            """;

        var (result, stdout) = CompileAndRun(src, "e2e_mff");

        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("total=20", stdout);
    }

    [Fact]
    public void Transpiled_ParMap_RunsEndToEnd_PropagatesErr()
    {
        // par_map where one input maps to Err — the runtime should return the first
        // Err (by original index). `main` inspects via classify() and prints.
        //
        // We deliberately avoid `|>?` here because the current emitter lowers `?` to
        // `.Unwrap()` which throws on Err rather than returning. Real control-flow
        // propagation is a follow-up in the emitter. Binding the Result and matching
        // is the faithful shape in the meantime.
        const string src = """
            module e2e_parmap_err

            fn main() !{io, async} -> Result<(), IoError> {
                let outcome: Result<List<Int>, IoError> =
                    par_map(list = build_list(), f = try_double)

                match outcome {
                    Ok(_) => {
                        println("unexpected-ok")?
                    }
                    Err(e) => {
                        println("got-err: ${e.narrative}")?
                    }
                }
                Ok(())
            }

            fn build_list() -> List<Int> {
                List.concat_three(
                    first  = List.singleton(1),
                    middle = List.concat_three(
                        first  = List.singleton(2),
                        middle = List.singleton(3),
                        last   = List.singleton(4)),
                    last   = List.singleton(5))
            }

            fn try_double(n: Int) -> Result<Int, IoError> {
                if n == 3 {
                    Err(IoError { narrative = "three is cursed" })
                } else {
                    Ok(n * 2)
                }
            }
            """;

        var (result, stdout) = CompileAndRun(src, "e2e_parmap_err");

        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("got-err: three is cursed", stdout);
    }

    [Fact]
    public void Transpiled_ParMap_RunsEndToEnd_AllOk()
    {
        const string src = """
            module e2e_parmap_ok

            fn main() !{io, async} -> Result<(), IoError> {
                let total: Int =
                    build_list()
                      |>? par_map(try_double)
                      |>  fold(seed = 0, step = add)

                println("total=${total}")?
                Ok(())
            }

            fn build_list() -> List<Int> {
                List.concat_three(
                    first  = List.singleton(10),
                    middle = List.singleton(20),
                    last   = List.singleton(30))
            }

            fn try_double(n: Int) -> Result<Int, IoError> { Ok(n * 2) }
            fn add(acc: Int, n: Int) -> Int { acc + n }
            """;

        var (result, stdout) = CompileAndRun(src, "e2e_parmap_ok");

        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("total=120", stdout); // 20 + 40 + 60 = 120
    }
}
