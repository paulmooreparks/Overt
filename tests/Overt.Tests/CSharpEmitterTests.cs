using Overt.Backend.CSharp;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class CSharpEmitterTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static string EmitSource(string source)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        return CSharpEmitter.Emit(parse.Module);
    }

    [Fact]
    public void Emit_Hello_ContainsExpectedShape()
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, "hello.ov"));
        var csharp = EmitSource(source);

        Assert.Contains("namespace Overt.Generated.Hello;", csharp);
        Assert.Contains("public static class Module", csharp);
        Assert.Contains("public static Result<Unit, IoError> main()", csharp);
        Assert.Contains(".Unwrap()", csharp); // ? propagation
        Assert.Contains("Ok(Unit.Value)", csharp);
    }

    [Fact]
    public void Emit_Record_BecomesSealedRecord()
    {
        var csharp = EmitSource("module m\nrecord Point { x: Int, y: Int }");
        Assert.Contains("public sealed record Point(int x, int y);", csharp);
    }

    [Fact]
    public void Emit_Enum_BareVariants_BecomeSealedRecords()
    {
        var csharp = EmitSource("module m\nenum Color { Red, Green, Blue }");
        Assert.Contains("public abstract record Color;", csharp);
        Assert.Contains("public sealed record Color_Red : Color;", csharp);
        Assert.Contains("public sealed record Color_Green : Color;", csharp);
        Assert.Contains("public sealed record Color_Blue : Color;", csharp);
    }

    [Fact]
    public void Emit_EnumDataVariant_CarriesFields()
    {
        var csharp = EmitSource(
            "module m\nenum E { NotFound { id: Int }, Timeout }");
        Assert.Contains("public sealed record E_NotFound(int id) : E;", csharp);
        Assert.Contains("public sealed record E_Timeout : E;", csharp);
    }

    [Fact]
    public void Emit_InterpolatedString_UsesDollarSyntax()
    {
        var csharp = EmitSource(
            "module m\nfn f() { let msg = \"Hello, $name!\" }");
        Assert.Contains("$\"Hello, {name}!\"", csharp);
    }

    [Fact]
    public void Emit_With_LowersToCSharpWithExpression()
    {
        var csharp = EmitSource(
            "module m\nfn f(p: Point) -> Point { p with { x = 1 } }");
        // C# `record with { ... }` syntax passes through directly.
        Assert.Contains("with { x = 1 }", csharp);
    }

    [Fact]
    public void Emit_Pipe_SplicesIntoCallAsFirstArg()
    {
        var csharp = EmitSource("module m\nfn f(x: Int) -> Int { x |> add(1) }");
        // `x |> add(1)` becomes `add(x, 1)`.
        Assert.Contains("add(x, 1)", csharp);
    }

    [Fact]
    public void Emit_PipePropagate_AddsUnwrap()
    {
        var csharp = EmitSource("module m\nfn f(x: Int) -> Int { x |>? validate }");
        Assert.Contains(".Unwrap()", csharp);
    }

    [Fact]
    public void Emit_NamedArgumentCall_UsesCSharpNamedArgs()
    {
        var csharp = EmitSource(
            "module m\nfn f() { fetch(url = \"x\", timeout = \"y\") }");
        Assert.Contains("url: \"x\"", csharp);
        Assert.Contains("timeout: \"y\"", csharp);
    }

    // Smoke-emission: all examples produce some non-empty C# text without crashes.
    [Theory]
    [InlineData("hello.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("bst.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("race.ov")]
    [InlineData("inference.ov")]
    [InlineData("ffi.ov")]
    [InlineData("trace.ov")]
    [InlineData("effects.ov")]
    [InlineData("refinement.ov")]
    public void Emit_Example_ProducesCSharp(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));
        var csharp = EmitSource(source);
        Assert.Contains("namespace Overt.Generated.", csharp);
        Assert.Contains("#nullable enable", csharp);
    }
}
