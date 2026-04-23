using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class NameResolverTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static ResolutionResult ResolveSource(string source)
    {
        var lex = Lexer.Lex(source);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        return NameResolver.Resolve(parse.Module);
    }

    [Fact]
    public void Resolve_SimpleLocal()
    {
        var result = ResolveSource("module m\nfn f(x: Int) -> Int { x }");
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var body = fn.Body;
        var xRef = (IdentifierExpr)body.TrailingExpression!;
        Assert.True(result.Resolutions.ContainsKey(xRef.Span));
        Assert.Equal(SymbolKind.Parameter, result.Resolutions[xRef.Span].Kind);
    }

    [Fact]
    public void Resolve_LetBinding()
    {
        var result = ResolveSource(
            "module m\nfn f() -> Int { let x = 42\n x }");
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var xRef = (IdentifierExpr)fn.Body.TrailingExpression!;
        Assert.Equal(SymbolKind.LetBinding, result.Resolutions[xRef.Span].Kind);
    }

    [Fact]
    public void Resolve_TopLevelFunctionReference()
    {
        var result = ResolveSource(
            "module m\nfn helper() -> Int { 0 }\nfn main() -> Int { helper() }");
        Assert.Empty(result.Diagnostics);

        var main = (FunctionDecl)result.Module.Declarations[1];
        var call = (CallExpr)main.Body.TrailingExpression!;
        var callee = (IdentifierExpr)call.Callee;
        Assert.Equal(SymbolKind.Function, result.Resolutions[callee.Span].Kind);
    }

    [Fact]
    public void Resolve_UnknownName_EmitsOV0200()
    {
        var result = ResolveSource("module m\nfn f() -> Int { nonexistent }");
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0200");
    }

    [Fact]
    public void Resolve_DuplicateTopLevel_EmitsOV0201()
    {
        var result = ResolveSource("module m\nfn dup() -> Int { 1 }\nfn dup() -> Int { 2 }");
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0201");
    }

    [Fact]
    public void Resolve_DuplicateLet_EmitsOV0201()
    {
        var result = ResolveSource(
            "module m\nfn f() -> Int { let x = 1\n let x = 2\n x }");
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0201");
    }

    [Fact]
    public void Resolve_NoShadowing_InnerScopeCannotReusePlainVar()
    {
        // DESIGN.md §3: every name has one binding. Inner scope cannot redefine.
        var result = ResolveSource(
            "module m\nfn f(x: Int) -> Int { let x = 2\n x }");
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0201");
    }

    [Fact]
    public void Resolve_StdlibNames_AreTolerated()
    {
        // Ok, Err, Some, None, println, List, etc. aren't in the module scope yet
        // (no stdlib), but shouldn't emit unknown-name errors.
        var result = ResolveSource(
            "module m\nfn f() { println(\"hi\") }");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Resolve_MatchArmPatternBindings_ScopedToArm()
    {
        // Pattern bindings in one arm should not leak into the next arm.
        var result = ResolveSource(
            "module m\nfn f(r: Result<Int, String>) -> Int { match r { Ok(value) => value, Err(message) => 0 } }");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Resolve_RefinementSelf_IsBound()
    {
        var result = ResolveSource(
            "module m\ntype Age = Int where 0 <= self && self <= 150");
        Assert.Empty(result.Diagnostics);

        var alias = (TypeAliasDecl)result.Module.Declarations[0];
        var pred = (BinaryExpr)alias.Predicate!;
        var selfRef = (IdentifierExpr)((BinaryExpr)pred.Left).Right;
        Assert.Equal("self", selfRef.Name);
        Assert.Equal(SymbolKind.PatternBinding, result.Resolutions[selfRef.Span].Kind);
    }

    // ------------------------------------------------- smoke: examples

    [Theory]
    [InlineData("hello.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("bst.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("race.ov")]
    [InlineData("inference.ov")]
    [InlineData("ffi.ov")]
    [InlineData("trace.ov")]
    [InlineData("effects.ov")]
    [InlineData("refinement.ov")]
    [InlineData("state_machine.ov")]
    public void Resolve_Example_HasNoDiagnostics(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));
        var result = ResolveSource(source);
        Assert.Empty(result.Diagnostics);
    }
}
