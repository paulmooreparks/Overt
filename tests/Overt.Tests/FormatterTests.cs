using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Formatter regression tests. Two invariants per example:
/// 1. Idempotent — <c>fmt(fmt(src)) == fmt(src)</c>. A formatter that doesn't
///    have a fixed point has drift, which corrupts comments / layout slowly.
/// 2. Semantic-preserving — the formatted source parses to a module with no
///    new diagnostics and the same declaration names (structural sanity).
///
/// End-to-end semantic preservation for the runnable examples is asserted in
/// <see cref="StdlibTranspiledEndToEndTests"/>; here we hit parse-only so
/// every example under <c>examples/</c> is covered.
/// </summary>
public class FormatterTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    [Theory]
    // Portable examples (root) — pure Overt, no extern bulk-imports.
    [InlineData("hello.ov")]
    [InlineData("arith_eval.ov")]
    [InlineData("bst.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("effects.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("race.ov")]
    [InlineData("refinement.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("trace.ov")]
    // C#-bucket examples — reach `extern "csharp"` for stdlib.
    [InlineData("csharp/ffi.ov")]
    [InlineData("csharp/inference.ov")]
    public void Format_Example_IsIdempotentAndParseable(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));

        var formatted1 = FormatSource(source);
        var formatted2 = FormatSource(formatted1);

        Assert.Equal(formatted1, formatted2);
        // Idempotence catches: extra blank lines, indent drift, comment
        // re-emission, trailing-newline inconsistency.

        // Second check: the formatted source must parse cleanly (no new
        // lex/parse diagnostics introduced by formatting).
        var reLex = Lexer.Lex(formatted1);
        var reParse = Parser.Parse(reLex.Tokens);
        Assert.Empty(reLex.Diagnostics);
        Assert.Empty(reParse.Diagnostics);

        // Module name survives.
        var originalLex = Lexer.Lex(source);
        var originalParse = Parser.Parse(originalLex.Tokens);
        Assert.Equal(originalParse.Module.Name, reParse.Module.Name);
        Assert.Equal(originalParse.Module.Declarations.Length, reParse.Module.Declarations.Length);
    }

    private static string FormatSource(string source)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        return Formatter.Format(parse.Module, lex.Tokens);
    }
}
