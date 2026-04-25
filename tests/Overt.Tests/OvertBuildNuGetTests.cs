using System.Diagnostics;

namespace Overt.Tests;

/// <summary>
/// End-to-end smoke for the NuGet packaging story. Runs
/// `dotnet pack src/Overt.Build` into a scratch feed directory, then
/// synthesizes a minimal consumer project in another scratch directory
/// that declares a single `<PackageReference Include="Overt.Build" />`
/// (against the scratch feed via a local NuGet.config) and an `.ov`
/// file. Builds and runs the consumer; asserts the generated Overt
/// module is callable from C# and prints the expected output.
///
/// Proves the package layout (build/, tasks/net9.0/, lib/net9.0/) is
/// what a real consumer would actually consume — a regression here
/// means the targets file can't find its task DLL, the runtime DLL
/// isn't visible to Csc, or the auto-import didn't fire.
/// </summary>
[Collection("dotnet-cli-serial")]
public class OvertBuildNuGetTests
{
    [Fact]
    public void Packed_Nupkg_IsConsumableViaPackageReference()
    {
        var repoRoot = FindRepoRoot();

        var scratch = Path.Combine(Path.GetTempPath(),
            "overt-nupkg-e2e-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var feedDir = Path.Combine(scratch, "feed");
        var consumerDir = Path.Combine(scratch, "consumer");
        Directory.CreateDirectory(feedDir);
        Directory.CreateDirectory(consumerDir);

        try
        {
            // Pack Overt.Build (and transitively the projects it depends
            // on) into the scratch feed. Use a per-test version suffix so
            // NuGet's global cache doesn't serve a stale 0.1.0 from
            // another run; `--version-suffix` composes with the <Version>
            // into `0.1.0-<suffix>`.
            var versionSuffix = "test" + Guid.NewGuid().ToString("N").Substring(0, 8);
            RunOrThrow("dotnet",
                $"pack {Path.Combine(repoRoot, "src", "Overt.Build", "Overt.Build.csproj")} "
                + $"--output \"{feedDir}\" --version-suffix {versionSuffix}",
                repoRoot);

            var packageVersion = $"0.1.0-{versionSuffix}";

            // Write the consumer project: minimal csproj with a local
            // NuGet.config pointing at the scratch feed, one .ov file,
            // and a Program.cs that calls into the transpiled module.
            WriteAllText(Path.Combine(consumerDir, "NuGet.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feedDir}" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="local">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            WriteAllText(Path.Combine(consumerDir, "Consumer.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <RestoreSources>{feedDir}</RestoreSources>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Overt.Build" Version="{packageVersion}" />
                  </ItemGroup>
                </Project>
                """);

            WriteAllText(Path.Combine(consumerDir, "Lib.ov"), """
                module lib

                fn greet(name: String) -> String {
                    "hello from package, ${name}"
                }
                """);

            WriteAllText(Path.Combine(consumerDir, "Program.cs"), """
                using Overt.Generated.Lib;

                var message = Module.greet("nuget");
                Console.WriteLine(message);
                """);

            // Build + run through the consumer project. `--force` ignores
            // the global package cache so a stale 0.1.0 from a prior
            // run can't bleed in (belt-and-suspenders with the
            // version suffix above).
            RunOrThrow("dotnet", "restore --force", consumerDir);
            RunOrThrow("dotnet", "build --no-restore", consumerDir);

            var (code, stdout, _) = Run("dotnet", "run --no-build", consumerDir);
            Assert.Equal(0, code);
            Assert.Contains("hello from package, nuget", stdout);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static void WriteAllText(string path, string content)
        => File.WriteAllText(path, content);

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
