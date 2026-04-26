using System.Diagnostics;
using Overt.Backend.Go;
using Overt.Compiler.Syntax;
using Xunit;

namespace Overt.Tests;

/// <summary>
/// Per-example sweep that verifies the Go emitter produces source the
/// `go` toolchain accepts. Mirrors <see cref="CSharpCompileCheckTests"/>
/// in shape: one test invocation per example, in-process pipeline
/// through to emit, then shell out to `go build` against the in-repo
/// runtime under <c>runtime/go</c>. We only verify compile (not
/// execution); the e2e tests in <see cref="GoBackendEndToEndTests"/>
/// cover end-to-end runs for the smaller curated programs.
///
/// The test theory enumerates only the portable examples that the Go
/// emitter currently handles cleanly. As features land in the emitter
/// (method-call syntax, generic user fns, pipe operators, async,
/// refinement runtime checks, etc.), more examples become eligible
/// and migrate from the "out of scope" comment block below into the
/// theory.
///
/// All tests skip silently when `go` is not on PATH so the suite
/// stays green for contributors without Go installed.
/// </summary>
public class GoCompileCheckTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    // Lazy because the runtime path is resolved at first use, and
    // because tests that skip on missing-Go shouldn't pay for it.
    private static readonly Lazy<string> RuntimePath = new(LocateRuntimePath);

    // Examples currently in scope for the Go target. Each one passes
    // `go build` against the in-repo runtime end-to-end.
    //
    // Out-of-scope examples (with the concrete emitter / runtime gap
    // each one hits, observed by running this sweep against every
    // portable example):
    //
    //   - effects.ov          Higher-order user fns with effect-row
    //                         polymorphism. Needs the emitter to
    //                         thread `func(...) T` types through
    //                         user-fn signatures.
    //   - dashboard.ov        `parallel { ... }` task groups. Would
    //                         lower to goroutines + channels; no
    //                         lowering yet (and a real one wants the
    //                         language-level concurrency design that's
    //                         currently scoped only on paper).
    //   - race.ov             `race { ... }` first-success. Same
    //                         family as parallel; same gating design.
    //   - refinement.ov       Refinement runtime checks. The C#
    //                         emitter injects `where`-predicate
    //                         validations at every boundary; the Go
    //                         emitter doesn't.
    //   - trace.ov            `trace { ... }` blocks. Not yet wired
    //                         in the Go emitter.
    //   - csharp/*            Reach into `extern "csharp" use "..."` —
    //                         the Go target has no equivalent FFI, by
    //                         design. These will never enter this
    //                         theory; they live in `examples/csharp/`
    //                         for that reason.
    //
    // Each entry above gains a row in the theory below the moment its
    // feature lands, and the comment shrinks accordingly.
    [Theory]
    [InlineData("hello.ov")]
    [InlineData("greeter.ov")]
    [InlineData("arith_eval.ov")]
    [InlineData("bst.ov")]
    [InlineData("mutation.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("pipeline.ov")]
    public void Emit_Example_ProducesCompilableGo(string file)
    {
        if (!IsGoOnPath())
        {
            return;
        }

        var error = EmitAndGoBuild(file);
        Assert.True(string.IsNullOrEmpty(error),
            $"Go build failed for {file}:\n{error}");
    }

    /// <summary>
    /// Drives an example through lex → parse → resolve → typecheck →
    /// GoEmitter, lays out a Go module that imports the in-repo
    /// runtime via a `replace` directive, and runs `go build`. Returns
    /// an empty string on success or the captured stderr on failure.
    /// </summary>
    private static string EmitAndGoBuild(string ovFile)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, ovFile));
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        if (parse.Diagnostics.Length > 0)
        {
            return "Parse diagnostics:\n  " + string.Join("\n  ",
                parse.Diagnostics.Select(d => d.ToString()));
        }
        var resolved = Overt.Compiler.Semantics.NameResolver.Resolve(parse.Module);
        if (resolved.Diagnostics.Length > 0)
        {
            return "Resolve diagnostics:\n  " + string.Join("\n  ",
                resolved.Diagnostics.Select(d => d.ToString()));
        }
        var typed = Overt.Compiler.Semantics.TypeChecker.Check(parse.Module, resolved);
        if (typed.Diagnostics.Length > 0)
        {
            return "Type-check diagnostics:\n  " + string.Join("\n  ",
                typed.Diagnostics.Select(d => d.ToString()));
        }

        string goSource;
        try
        {
            goSource = GoEmitter.Emit(parse.Module);
        }
        catch (NotSupportedException ex)
        {
            return "GoEmitter NotSupportedException: " + ex.Message;
        }

        var workDir = Directory.CreateTempSubdirectory("overt-go-compile-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "go.mod"),
                $$"""
                module overt-app

                go 1.21

                require overt-runtime v0.0.0
                replace overt-runtime => {{RuntimePath.Value.Replace("\\", "/")}}
                """);
            File.WriteAllText(Path.Combine(workDir, "main.go"), goSource);

            // `go mod tidy` writes go.sum even for a replace-only require;
            // newer Go versions refuse `go build` without it.
            var (tidyExit, _, tidyStderr) = RunGo(workDir, "mod", "tidy");
            if (tidyExit != 0)
            {
                return $"`go mod tidy` failed:\n{tidyStderr}\n\nEmitted Go:\n{goSource}";
            }

            var (buildExit, _, buildStderr) = RunGo(workDir, "build", "./...");
            if (buildExit != 0)
            {
                return $"`go build` failed:\n{buildStderr}\n\nEmitted Go:\n{goSource}";
            }
            return "";
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static (int Exit, string Stdout, string Stderr) RunGo(string workDir, params string[] args)
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
        return (p.ExitCode, stdout, stderr);
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
}
