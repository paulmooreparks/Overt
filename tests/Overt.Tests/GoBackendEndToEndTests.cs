using System.Diagnostics;
using Overt.Backend.Go;
using Overt.Compiler.Syntax;
using Xunit;

namespace Overt.Tests;

/// <summary>
/// End-to-end run for the Go back end. Lexes / parses a small Overt module,
/// emits Go source via <see cref="GoEmitter"/>, drops it next to the in-repo
/// runtime under <c>runtime/go</c> via a temp Go module, then invokes
/// <c>go build</c> + the resulting binary and asserts on stdout.
///
/// The test is skipped (via <see cref="SkippableFactAttribute"/>-style guard)
/// when the <c>go</c> toolchain is not on PATH, so a contributor without Go
/// installed sees a passing suite. CI runners that target the Go back end
/// install Go explicitly and this skip path is inert.
///
/// Scope: hello-world. As the GoEmitter grows additional features, parallel
/// tests should go here, each one a tight Overt → Go → run loop. The
/// existing C#-side tests (<c>StdlibTranspiledEndToEndTests</c>) are the
/// reference for which Overt features are expected to lower correctly.
/// </summary>
public class GoBackendEndToEndTests
{
    [Fact]
    public void Transpiled_Hello_PrintsToStdout()
    {
        // Go's fmt.Fprintln always writes a single LF regardless of OS,
        // while .NET's StreamReader on Windows doesn't translate the LF
        // to CRLF when reading from a pipe — so a literal "\n" matches.
        AssertOvertProgramPrints(
            """
            module hello

            fn main() !{io} -> Result<(), IoError> {
                println("hello from Go")?
                Ok(())
            }
            """,
            expectedStdout: "hello from Go\n");
    }

    [Fact]
    public void Transpiled_Arithmetic_LetIfElse()
    {
        // Exercises: integer literals, Int parameters, arithmetic
        // (+, *), comparison (==, <), let with type, statement-position
        // if/else (with an else-if chain), Bool branching. The user fn
        // returns Int and is called with named args; main uses the
        // result in an if-condition. Three branches, three distinct
        // print outputs — the chosen one tells us the whole pipeline
        // routed correctly.
        AssertOvertProgramPrints(
            """
            module arith

            fn classify(n: Int) -> Int {
                if n < 0 {
                    -1
                } else if n == 0 {
                    0
                } else {
                    1
                }
            }

            fn main() !{io} -> Result<(), IoError> {
                let x: Int = 3 + 4 * 2
                let cls: Int = classify(n = x - 11)
                if cls == 0 {
                    println("zero")?
                } else if cls < 0 {
                    println("negative")?
                } else {
                    println("positive")?
                }
                Ok(())
            }
            """,
            // x = 3 + 4*2 = 11; classify(0) = 0; prints "zero".
            expectedStdout: "zero\n");
    }

    [Fact]
    public void Transpiled_Parameters_ReceiveAndUseString()
    {
        // Exercises String parameters, identifier-expression references
        // inside the body, and a user-fn call from main with a named
        // argument. Each `greet` call propagates ? through the user fn
        // back into main's Result<Unit, IoError> return.
        AssertOvertProgramPrints(
            """
            module greet

            fn greet(name: String) !{io} -> Result<(), IoError> {
                println(name)?
                Ok(())
            }

            fn main() !{io} -> Result<(), IoError> {
                greet(name = "alice")?
                greet(name = "bob")?
                Ok(())
            }
            """,
            expectedStdout: "alice\nbob\n");
    }

    /// <summary>
    /// Lex / parse / emit / `go build` / run / assert. Skips the whole
    /// pipeline when `go` is not on PATH; intentionally silent so the
    /// suite stays green for contributors without Go installed.
    /// </summary>
    private static void AssertOvertProgramPrints(string overtSource, string expectedStdout)
    {
        if (!IsGoOnPath())
        {
            return;
        }

        var lex = Lexer.Lex(overtSource);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);

        var goSource = GoEmitter.Emit(parse.Module);

        var workDir = Directory.CreateTempSubdirectory("overt-go-e2e-").FullName;
        try
        {
            // Lay out a Go module that imports the in-repo runtime via a
            // `replace` directive, so the test never depends on a published
            // `overt-runtime` module on a registry.
            var runtimePath = LocateRuntimePath();
            File.WriteAllText(
                Path.Combine(workDir, "go.mod"),
                $$"""
                module overt-app

                go 1.21

                require overt-runtime v0.0.0
                replace overt-runtime => {{runtimePath.Replace("\\", "/")}}
                """);
            File.WriteAllText(Path.Combine(workDir, "main.go"), goSource);

            // `go mod tidy` to populate go.sum (Go 1.21+ refuses to build
            // without one even for a replace-only require).
            RunGo(workDir, "mod", "tidy");

            var binPath = Path.Combine(workDir, IsWindows() ? "app.exe" : "app");
            RunGo(workDir, "build", "-o", binPath, ".");

            var (stdout, stderr, exit) = RunBinary(binPath);
            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.Equal(expectedStdout, stdout);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static bool IsGoOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo("go", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindows()
        => Environment.OSVersion.Platform == PlatformID.Win32NT;

    /// <summary>Walk up from the test bin directory to the repo root,
    /// then down to <c>runtime/go</c>. The runtime is checked into the
    /// repo so the test doesn't need a network round trip.</summary>
    private static string LocateRuntimePath()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Overt.sln")))
        {
            here = here.Parent;
        }
        if (here is null)
        {
            throw new InvalidOperationException(
                "Could not locate Overt.sln walking up from " + AppContext.BaseDirectory);
        }
        var runtime = Path.Combine(here.FullName, "runtime", "go");
        if (!Directory.Exists(runtime))
        {
            throw new InvalidOperationException(
                "Go runtime directory not found at " + runtime);
        }
        return runtime;
    }

    private static void RunGo(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("go")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"`go {string.Join(" ", args)}` failed (exit {p.ExitCode})\n"
                + $"stdout:\n{stdout}\n"
                + $"stderr:\n{stderr}");
        }
    }

    private static (string Stdout, string Stderr, int Exit) RunBinary(string path)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (stdout, stderr, p.ExitCode);
    }
}
