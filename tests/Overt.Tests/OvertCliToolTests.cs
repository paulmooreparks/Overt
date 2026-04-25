using System.Diagnostics;

namespace Overt.Tests;

/// <summary>
/// End-to-end smoke for the .NET global-tool packaging of
/// <c>Overt.Cli</c>. Packs the CLI into a scratch feed, installs it
/// to a scratch <c>--tool-path</c> (so the user's global tool store
/// stays untouched), runs the installed `overt` against a temp .ov
/// file, and asserts output. Also verifies the bundled stdlib resolves
/// from the tool-install location via the ancestor-walk in
/// `DiscoverSearchDirs`.
///
/// If this regresses, the two usual causes are: the
/// `tools/net9.0/any/stdlib/` bundling fell out of the csproj, or a
/// new ProjectReference added a DLL that isn't landing in the
/// tool-pack output.
/// </summary>
[Collection("dotnet-cli-serial")]
public class OvertCliToolTests
{
    [Fact]
    public void Packed_CliTool_InstallsAndRunsOvertSource()
    {
        var repoRoot = FindRepoRoot();

        var scratch = Path.Combine(Path.GetTempPath(),
            "overt-tool-e2e-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var feedDir = Path.Combine(scratch, "feed");
        var toolPath = Path.Combine(scratch, "tools");
        Directory.CreateDirectory(feedDir);
        Directory.CreateDirectory(toolPath);

        try
        {
            var versionSuffix = "test" + Guid.NewGuid().ToString("N").Substring(0, 8);
            RunOrThrow("dotnet",
                $"pack {Path.Combine(repoRoot, "src", "Overt.Cli", "Overt.Cli.csproj")} "
                + $"--output \"{feedDir}\" --version-suffix {versionSuffix}",
                repoRoot);

            var packageVersion = $"0.1.0-{versionSuffix}";

            // Scoped NuGet.config that whitelists the scratch feed. Written
            // next to the tool path; `dotnet tool install` picks up the
            // config via --configfile.
            var configPath = Path.Combine(scratch, "nuget.config");
            File.WriteAllText(configPath, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feedDir}" />
                  </packageSources>
                  <packageSourceMapping>
                    <packageSource key="local">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            // --tool-path keeps the install local; no user-global
            // side-effects. The `overt` binary lands directly in
            // <toolPath>/overt(.exe).
            RunOrThrow("dotnet",
                $"tool install Overt --version {packageVersion} "
                + $"--tool-path \"{toolPath}\" --configfile \"{configPath}\"",
                scratch);

            // Write a sample .ov that exercises FFI bulk-import. Verifies
            // the installed tool can resolve `extern "csharp" use "..."`
            // against the BCL types loaded into its own AppDomain.
            var sampleDir = Path.Combine(scratch, "sample");
            Directory.CreateDirectory(sampleDir);
            var samplePath = Path.Combine(sampleDir, "hello.ov");
            File.WriteAllText(samplePath, """
                module hello_tool

                extern "csharp" use "System.Math" as math

                fn main() !{io} -> Result<(), IoError> {
                    let root: Float = math.sqrt(d = 25.0)
                    println("sqrt(25) = ${root}")?
                    Ok(())
                }
                """);

            // Invoke the installed tool directly. Windows: overt.exe;
            // Unix: overt.
            var overtExe = OperatingSystem.IsWindows()
                ? Path.Combine(toolPath, "overt.exe")
                : Path.Combine(toolPath, "overt");
            Assert.True(File.Exists(overtExe), $"expected overt binary at {overtExe}");

            var (code, stdout, stderr) = Run(overtExe, $"run \"{samplePath}\"", sampleDir);
            Assert.True(code == 0, $"exit={code}\nstdout:{stdout}\nstderr:{stderr}");
            Assert.Contains("sqrt(25) = 5", stdout);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
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
