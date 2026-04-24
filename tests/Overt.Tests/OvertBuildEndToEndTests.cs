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
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
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
