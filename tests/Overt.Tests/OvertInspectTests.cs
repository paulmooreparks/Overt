namespace Overt.Tests;

/// <summary>
/// Lightweight verification of the <c>overt inspect</c> subcommand. Calls
/// the program's static entry point directly with redirected stdout/stderr,
/// so the test runs in-process and doesn't depend on the global-tool
/// packaging path. Heavier coverage of the same flow lives in
/// <see cref="OvertCliToolTests"/>.
/// </summary>
public class OvertInspectTests
{
    private static int InvokeInspect(string[] args, out string stdout, out string stderr)
    {
        // Reflect into Cli.InspectProgram. The Cli class is `static` and
        // file-internal to Program.cs; reach it via its emitted type name.
        var asm = typeof(Overt.Backend.CSharp.BindGenerator).Assembly
            .GetReferencedAssemblies()
            .Select(System.Reflection.Assembly.Load)
            .FirstOrDefault(a => a.GetName().Name == "overt")
            ?? System.Reflection.Assembly.Load("overt");
        var cli = asm.GetType("Cli");
        Assert.NotNull(cli);
        var inspect = cli!.GetMethod("InspectProgram",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(inspect);

        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            var code = (int)inspect!.Invoke(null, new object[] { args })!;
            stdout = outWriter.ToString();
            stderr = errWriter.ToString();
            return code;
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void Inspect_KnownBclType_PrintsSynthesizedFacade()
    {
        var code = InvokeInspect(new[] { "System.Math" }, out var stdout, out var stderr);
        Assert.Equal(0, code);
        Assert.Contains("module __overt_extern_csharp_System_Math", stdout);
        Assert.Contains("extern \"csharp\" fn", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Inspect_UnknownTarget_ExitsNonZeroWithDiagnostic()
    {
        var code = InvokeInspect(new[] { "Definitely.Not.A.Type" }, out var stdout, out var stderr);
        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("cannot resolve", stderr);
    }

    [Fact]
    public void Inspect_NoArguments_PrintsUsageAndExits2()
    {
        var code = InvokeInspect(Array.Empty<string>(), out _, out var stderr);
        Assert.Equal(2, code);
        Assert.Contains("a target type is required", stderr);
    }

    [Fact]
    public void Inspect_UnknownPlatform_ReportsResolverGap()
    {
        var code = InvokeInspect(new[] { "anything", "--platform", "klingon" }, out _, out var stderr);
        Assert.Equal(1, code);
        Assert.Contains("klingon", stderr);
    }

    [Fact]
    public void Inspect_HelpFlag_PrintsUsageAndExits0()
    {
        var code = InvokeInspect(new[] { "--help" }, out var stdout, out _);
        Assert.Equal(0, code);
        Assert.Contains("usage: overt inspect", stdout);
    }
}
