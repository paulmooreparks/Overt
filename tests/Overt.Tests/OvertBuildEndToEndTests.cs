using System.Diagnostics;

namespace Overt.Tests;

/// <summary>
/// End-to-end smoke: shells out to <c>dotnet build</c> / <c>dotnet run</c> on
/// the in-repo MSBuild sample (`samples/msbuild-smoke/`). That sample imports
/// <c>Overt.Build.targets</c> from the built Overt.Build output and calls
/// into an Overt-transpiled module from a C# <c>Program.cs</c>. Passing
/// proves the targets file, the task DLL, the generated-file wiring, and
/// the Csc-picks-up-Compile-item flow all compose.
///
/// This runs outside the xUnit process because MSBuild / Csc involvement
/// dwarfs whatever `OvertBuildTaskTests` exercises directly; treat this as
/// the coarse-grained integration seal.
/// </summary>
[Collection("dotnet-cli-serial")]
public class OvertBuildEndToEndTests
{
    [Fact]
    public void MsbuildSmoke_Runs_AndPrintsExpectedOutput()
    {
        var repoRoot = FindRepoRoot();
        var sampleDir = Path.Combine(repoRoot, "samples", "msbuild-smoke");
        Assert.True(Directory.Exists(sampleDir),
            $"expected sample project at {sampleDir}");

        // Force-build the task project first so Overt.Build.dll exists at the
        // bin path the sample imports from. Tests that run from a clean clone
        // would otherwise race the sample's build against the task's build.
        RunOrThrow("dotnet",
            "build " + Path.Combine(repoRoot, "src", "Overt.Build", "Overt.Build.csproj"),
            repoRoot);

        // `dotnet run` does a build too, but we invoke build separately so
        // a pure-build regression surfaces without being conflated with
        // runtime issues.
        RunOrThrow("dotnet", "build MsbuildSmoke.csproj", sampleDir);

        var (code, stdout, _) = Run("dotnet", "run --project MsbuildSmoke.csproj", sampleDir);
        Assert.Equal(0, code);
        Assert.Contains("hello, world", stdout);
    }

    /// <summary>
    /// Exercises the config-validate sample end-to-end across each fixture.
    /// The sample is the Phase 1 gate: it's the first real consumer of the
    /// Overt.Build integration that does something worth publishing a
    /// package for (refinement-typed validation, typed errors, exhaustive
    /// match). A regression here means the public story broke.
    /// </summary>
    [Fact]
    public void ConfigValidate_AllFixturesProduceExpectedOutputAndExitCode()
    {
        var repoRoot = FindRepoRoot();
        var sampleDir = Path.Combine(repoRoot, "samples", "config-validate");
        Assert.True(Directory.Exists(sampleDir),
            $"expected sample project at {sampleDir}");

        RunOrThrow("dotnet",
            "build " + Path.Combine(repoRoot, "src", "Overt.Build", "Overt.Build.csproj"),
            repoRoot);
        RunOrThrow("dotnet", "build ConfigValidate.csproj", sampleDir);

        // Happy path.
        var (code, stdout, _) = Run("dotnet",
            "run --no-build --project ConfigValidate.csproj -- configs/valid.json",
            sampleDir);
        Assert.Equal(0, code);
        Assert.Contains("validated: listening on 0.0.0.0:8080, 4 workers", stdout);

        // Each failure fixture hits a distinct ValidationError variant,
        // so asserting on the describe() text catches both a regression
        // in `describe` and a regression in which variant `validate` returns.
        AssertFailure("invalid-port.json",
            "port 99999 is out of range; expected 1..65535", sampleDir);
        AssertFailure("invalid-log-level.json",
            "log_level 'verbose' is not recognized", sampleDir);
        AssertFailure("invalid-empty-urls.json",
            "upstream_urls must not be empty", sampleDir);
    }

    private static void AssertFailure(string fixtureName, string expectedInStderr, string sampleDir)
    {
        var (code, _, stderr) = Run("dotnet",
            $"run --no-build --project ConfigValidate.csproj -- configs/{fixtureName}",
            sampleDir);
        Assert.Equal(1, code);
        Assert.Contains(expectedInStderr, stderr);
    }

    // Cap any single shell-out at two minutes. These tests drive dotnet
    // build/run, so a couple of seconds is normal; anything past a minute
    // means something is wedged. The timeout keeps a broken test from
    // burning a CI hour and gives a clear failure message instead of a
    // hang.
    private static readonly TimeSpan s_processTimeout = TimeSpan.FromMinutes(2);

    private static (int Code, string Stdout, string Stderr) Run(
        string fileName, string args, string workingDir)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;

        // Drain stdout and stderr concurrently. Reading one to EOF before
        // the other starts deadlocks when the unread pipe fills: child
        // blocks writing, the read we're waiting on never sees EOF,
        // everyone stays stuck. MsbuildSmoke's output is tiny so it never
        // hit this; ConfigValidate's four-fixture run plus dotnet build
        // chatter is big enough to fill a pipe buffer and freeze CI.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        // Wait for both streams to reach EOF before WaitForExit, per the
        // documented pattern: EOF means the child has no more output, so
        // exit-on-WaitForExit is safe to follow.
        if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, s_processTimeout))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new Xunit.Sdk.XunitException(
                $"`{fileName} {args}` (cwd={workingDir}) did not produce EOF on both streams within {s_processTimeout.TotalSeconds:0}s; killed.");
        }

        if (!p.WaitForExit((int)s_processTimeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new Xunit.Sdk.XunitException(
                $"`{fileName} {args}` (cwd={workingDir}) exited-waited past {s_processTimeout.TotalSeconds:0}s; killed.");
        }

        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void RunOrThrow(string fileName, string args, string workingDir)
    {
        var (code, stdout, stderr) = Run(fileName, args, workingDir);
        if (code != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"`{fileName} {args}` (cwd={workingDir}) exited {code}\n"
                + $"stdout:\n{stdout}\nstderr:\n{stderr}");
        }
    }

    /// <summary>Walk up from the test binary location to find the repo
    /// root — marked by the .sln file. Tests running from `bin/Debug/net9.0/`
    /// need a few hops up.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Overt.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"could not find Overt.sln walking up from {AppContext.BaseDirectory}");
    }
}
