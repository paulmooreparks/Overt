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
    public void Transpiled_QuestionInIfArm_PropagatesErrAsValue()
    {
        // `?` inside an if-expression arm that's the value of a let binding —
        // statement-level lowering means the Err flows out as a returned value,
        // not through .Unwrap()-that-throws.
        const string src = """
            module ifq_e2e

            fn choose(which: Bool) -> Result<Int, IoError> {
                if which {
                    Err(IoError { narrative = "left" })
                } else {
                    Ok(99)
                }
            }

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = if true { choose(which = true)? } else { choose(which = false)? }
                println("n=${n}")?
                Ok(())
            }
            """;
        var (result, stdout) = CompileAndRun(src, "ifq_e2e");
        Assert.NotNull(result);
        Assert.Equal("False",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        // Err should NOT have reached the println.
        Assert.DoesNotContain("n=", stdout);
        var err = result.GetType().GetProperty("Error")!.GetValue(result);
        Assert.Equal("left", err!.GetType().GetProperty("narrative")!.GetValue(err));
    }

    [Fact]
    public void Transpiled_QuestionInIfArm_SuccessPath()
    {
        const string src = """
            module ifq_ok_e2e

            fn choose(which: Bool) -> Result<Int, IoError> {
                if which { Err(IoError { narrative = "left" }) } else { Ok(99) }
            }

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = if false { choose(which = true)? } else { choose(which = false)? }
                println("n=${n}")?
                Ok(())
            }
            """;
        var (result, stdout) = CompileAndRun(src, "ifq_ok_e2e");
        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("n=99", stdout);
    }

    [Fact]
    public void Transpiled_ForEachLoopBreakContinue_WorkEndToEnd()
    {
        // Exercises `for each`, `loop` + `break`, `while` + `continue` in one program.
        const string src = """
            module loops_e2e

            fn main() !{io} -> Result<(), IoError> {
                let xs: List<Int> = List.concat_three(
                    first  = List.singleton(10),
                    middle = List.singleton(20),
                    last   = List.singleton(30))

                for each x in xs {
                    println("got ${x}")?
                }

                let mut n = 0
                loop {
                    if n == 3 {
                        break
                    }
                    println("loop ${n}")?
                    n = n + 1
                }

                let mut m = 0
                while m < 5 {
                    if m == 2 {
                        m = m + 1
                        continue
                    }
                    println("while ${m}")?
                    m = m + 1
                }

                Ok(())
            }
            """;
        var (result, stdout) = CompileAndRun(src, "loops_e2e");
        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("got 10", stdout);
        Assert.Contains("got 30", stdout);
        Assert.Contains("loop 2", stdout);
        Assert.DoesNotContain("loop 3", stdout); // break fired
        Assert.Contains("while 1", stdout);
        Assert.DoesNotContain("while 2", stdout); // continue skipped
        Assert.Contains("while 3", stdout);
    }

    [Fact]
    public void Transpiled_LiteralPatterns_MatchIntegerAndBool()
    {
        // Literal patterns on Int (including negative), with a `_` catch-all.
        const string src = """
            module litpat_e2e

            fn describe(n: Int) -> String {
                match n {
                    0  => "zero",
                    1  => "one",
                    -1 => "neg",
                    _  => "other",
                }
            }

            fn main() !{io} -> Result<(), IoError> {
                println(describe(0))?
                println(describe(1))?
                println(describe(-1))?
                println(describe(99))?
                Ok(())
            }
            """;
        var (result, stdout) = CompileAndRun(src, "litpat_e2e");
        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("zero", stdout);
        Assert.Contains("one", stdout);
        Assert.Contains("neg", stdout);
        Assert.Contains("other", stdout);
    }

    [Fact]
    public void Transpiled_ExternCsharp_CallsBcl()
    {
        // Pure BCL static call through extern — should return the same string
        // System.IO.Path.Combine would return when called from C#.
        const string src = """
            module extern_e2e

            extern "csharp" fn path_combine(a: String, b: String) -> String
                binds "System.IO.Path.Combine"

            fn main() !{io} -> Result<(), IoError> {
                let combined: String = path_combine(a = "dir", b = "file.txt")
                println(combined)?
                Ok(())
            }
            """;
        var (result, stdout) = CompileAndRun(src, "extern_e2e_path");
        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        // Path.Combine uses the platform separator; on Windows that's `\`.
        Assert.True(stdout.Contains("dir/file.txt") || stdout.Contains("dir\\file.txt"),
            $"expected a combined path in stdout, got: {stdout}");
    }

    [Fact]
    public void Transpiled_ExternCsharp_ExceptionBecomesErr()
    {
        // Call a BCL method that will throw (reading a nonexistent file);
        // the extern's Result<_, IoError> return means the catch wraps the
        // exception message into Err instead of letting it fly.
        const string src = """
            module extern_err_e2e

            extern "csharp" fn read_all_text(path: String) !{io, fails} -> Result<String, IoError>
                binds "System.IO.File.ReadAllText"

            fn main() !{io} -> Result<(), IoError> {
                match read_all_text(path = "/definitely/does/not/exist.txt") {
                    Ok(s)  => println("unexpected-ok: ${s}")?,
                    Err(e) => println("got-err")?,
                }
                Ok(())
            }
            """;
        var (result, stdout) = CompileAndRun(src, "extern_e2e_err");
        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("got-err", stdout);
        Assert.DoesNotContain("unexpected-ok", stdout);
    }

    [Fact]
    public void Transpiled_ArithEvalDemo_RunsAndPrints()
    {
        // The arithmetic evaluator demo — an interpreter in ~50 lines of Overt.
        // Hardcoded program evaluates to 21; success path must print "= 21".
        var ovSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "examples", "arith_eval.ov"));
        var (result, stdout) = CompileAndRun(ovSource, "arith_eval_demo");
        Assert.NotNull(result);
        Assert.Equal("True",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        Assert.Contains("= 21", stdout);
    }

    [Fact]
    public void Transpiled_QuestionMark_PropagatesErrAsValue()
    {
        // Direct `?` on a Result-returning function: if the callee returns Err,
        // main must return Err *without* throwing. Verifies DESIGN.md §11's
        // "errors as values, no hidden unwinding."
        const string src = """
            module e2e_qmark_err

            fn main() !{io} -> Result<(), IoError> {
                let _: Int = try_something()?
                Ok(())
            }

            fn try_something() -> Result<Int, IoError> {
                Err(IoError { narrative = "nope" })
            }
            """;

        var (result, _) = CompileAndRun(src, "e2e_qmark_err");

        Assert.NotNull(result);
        Assert.Equal("False",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
        // The narrative should have survived through the propagation.
        var errProp = result.GetType().GetProperty("Error");
        Assert.NotNull(errProp);
        var err = errProp!.GetValue(result);
        Assert.Equal("nope", err!.GetType().GetProperty("narrative")!.GetValue(err));
    }

    [Fact]
    public void Transpiled_PipePropagate_PropagatesErrAsValue()
    {
        // `|>?` on a fallible pipe: par_map returns Err, which |>? should early-
        // return from main without throwing. Previously this path threw
        // InvalidOperationException via .Unwrap(); now it returns Err as a value.
        const string src = """
            module e2e_pipeprop_err

            fn main() !{io, async} -> Result<(), IoError> {
                let _: Int =
                    build_list()
                      |>? par_map(try_double)
                      |>  fold(seed = 0, step = add)

                Ok(())
            }

            fn build_list() -> List<Int> {
                List.concat_three(
                    first  = List.singleton(1),
                    middle = List.singleton(2),
                    last   = List.singleton(3))
            }

            fn try_double(n: Int) -> Result<Int, IoError> {
                if n == 2 {
                    Err(IoError { narrative = "no two" })
                } else {
                    Ok(n * 2)
                }
            }

            fn add(acc: Int, n: Int) -> Int { acc + n }
            """;

        var (result, _) = CompileAndRun(src, "e2e_pipeprop_err");

        Assert.NotNull(result);
        Assert.Equal("False",
            result!.GetType().GetProperty("IsOk")!.GetValue(result)!.ToString());
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
