using Overt.Compiler.Syntax;
using Overt.Compiler.Semantics;

namespace Overt.Tests;

/// <summary>
/// Tests for the `overt bind` reflection-driven facade generator. Focus on
/// two invariants:
/// 1. The generated Overt source parses, name-resolves, and type-checks
///    cleanly — a facade that the parser rejects is useless.
/// 2. The generator's name-mangling keeps overload collisions distinct —
///    same C# name + different arity becomes distinct Overt names.
/// Actual runtime round-trips are covered by StdlibTranspiledEndToEndTests.
/// </summary>
public class BindGeneratorTests
{
    [Fact]
    public void Generate_SystemIoPath_ParsesAndTypeChecks()
    {
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));

        Assert.StartsWith("module path", src);
        Assert.Contains("extern \"csharp\"", src);
        Assert.Contains("binds \"System.IO.Path.", src);

        // Parse + resolve + check — no diagnostics anywhere.
        var lex = Lexer.Lex(src);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var resolved = NameResolver.Resolve(parse.Module);
        Assert.Empty(resolved.Diagnostics);
        var typed = TypeChecker.Check(parse.Module, resolved);
        Assert.Empty(typed.Diagnostics);

        // Sanity: the facade has multiple declarations.
        Assert.True(parse.Module.Declarations.Length > 5,
            $"expected >5 extern decls for System.IO.Path, got {parse.Module.Declarations.Length}");
    }

    [Fact]
    public void Generate_OverloadsGetAritySuffix()
    {
        // System.IO.Path has Combine(2 args), Combine(3 args), Combine(4 args) —
        // all renderable, so each overload gets an arity suffix.
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));
        Assert.Contains("fn combine_2(", src);
        Assert.Contains("fn combine_3(", src);
        Assert.Contains("fn combine_4(", src);
        // The bare name shouldn't clash — no `fn combine(` in the output.
        Assert.DoesNotContain("fn combine(", src);
    }

    [Fact]
    public void Generate_SingleOverloadKeepsBareName()
    {
        // `Path.Exists(string)` is the only renderable Exists overload (the
        // `ReadOnlySpan<char>` one is skipped), so it should keep the bare
        // name without an arity suffix.
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));
        Assert.Contains("fn exists(", src);
    }
}
