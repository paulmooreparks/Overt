using System.Runtime.CompilerServices;
using Overt.Backend.CSharp;
using Overt.Compiler.Syntax;
using Overt.EndToEnd;

namespace Overt.Tests;

/// <summary>
/// End-to-end verification: emitted C# for hello.ov compiles against Overt.Runtime,
/// actually runs, and prints "Hello, LLM!" to stdout. This closes the full
/// source → AST → C# → compiled assembly → execution → observed output loop.
///
/// The harness lives in the Overt.EndToEnd project. Its Generated.cs is the checked-in
/// transpiled form of examples/hello.ov; <see cref="Emit_HelloOv_MatchesCheckedInHarness"/>
/// asserts it is in sync with what the current emitter would produce, so drift fails
/// the build loudly rather than silently.
/// </summary>
public class HelloEndToEndTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static string HarnessDir([CallerFilePath] string callerFilePath = "")
        => Path.Combine(Path.GetDirectoryName(callerFilePath)!, "..", "Overt.EndToEnd");

    [Fact]
    public void Run_HelloOvHarness_PrintsHelloLLM()
    {
        using var stdout = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exitCode = Harness.Main();
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
        Assert.Contains("Hello, LLM!", stdout.ToString());
    }

    [Fact]
    public void Emit_HelloOv_MatchesCheckedInHarness()
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, "hello.ov"));
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var expected = CSharpEmitter.Emit(parse.Module);

        var generatedPath = Path.Combine(HarnessDir(), "Generated.cs");

        if (Environment.GetEnvironmentVariable("OVERT_REGEN_HARNESS") == "1")
        {
            File.WriteAllText(generatedPath, expected);
            return;
        }

        Assert.True(
            File.Exists(generatedPath),
            $"Harness Generated.cs missing at {generatedPath}. Run with OVERT_REGEN_HARNESS=1 to create.");

        var actual = File.ReadAllText(generatedPath);
        if (actual != expected)
        {
            Assert.Fail(
                "tests/Overt.EndToEnd/Generated.cs is out of sync with the current C# emitter. "
                + "Rerun with OVERT_REGEN_HARNESS=1 to regenerate.");
        }
    }
}
