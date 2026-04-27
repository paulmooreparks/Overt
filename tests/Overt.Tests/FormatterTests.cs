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

    [Fact]
    public void Format_ElseIfChain_RendersInline()
    {
        // The parser stores `else if X` as `else { if X }` (a block
        // whose only inhabitant is the trailing if-expression). The
        // formatter must collapse that back to `else if` rather than
        // printing each arm as a fresh nested block — without the
        // collapse, an N-arm chain stairsteps to N indentation
        // levels, which makes the source unreadable.
        const string src = """
            module elseif

            fn classify(n: Int) -> String {
                if n < 0 {
                    "negative"
                } else if n == 0 {
                    "zero"
                } else if n < 10 {
                    "small"
                } else {
                    "large"
                }
            }
            """;
        var formatted = FormatSource(src);
        Assert.Contains("} else if n == 0 {", formatted);
        Assert.Contains("} else if n < 10 {", formatted);
        Assert.Contains("} else {", formatted);
        // Negative: no nested re-indentation of `if` after `else`.
        Assert.DoesNotContain("    } else {\n        if ", formatted);
    }

    [Fact]
    public void Format_BlankLineBetweenCommentBlockAndDecl_IsPreserved()
    {
        // A section divider written with a blank line before the next
        // declaration is the author saying "here ends one group; there
        // starts another." The formatter must keep that blank line, or
        // section structure collapses on every fmt --write pass.
        const string src = """
            module sec

            // ---------- types

            record Point { x: Int, y: Int }
            """;
        var formatted = FormatSource(src);
        // The `// ---------- types` line has a blank line between it
        // and the `record Point` line.
        var lines = formatted.Split('\n');
        var sectionIdx = Array.FindIndex(lines, l => l.Contains("---------- types"));
        Assert.True(sectionIdx >= 0, "section divider missing");
        Assert.Equal(string.Empty, lines[sectionIdx + 1].Trim());
    }

    [Fact]
    public void Format_BlankLineBetweenParagraphCommentsInOneBlock_IsPreserved()
    {
        // Multi-paragraph header comment: the blank line between
        // paragraphs is part of the structure, not noise. Without
        // gap-detection inside the comment-flush loop, every blank
        // line within a comment block disappears on fmt.
        const string src = """
            module para

            // First paragraph of the header.
            // Continues on the next line.

            // Second paragraph after a blank line.
            fn main() !{io} -> Result<(), IoError> {
                Ok(())
            }
            """;
        var formatted = FormatSource(src);
        var lines = formatted.Split('\n');
        var firstParaEnd = Array.FindIndex(lines, l => l.Contains("Continues on the next line."));
        Assert.True(firstParaEnd >= 0);
        Assert.Equal(string.Empty, lines[firstParaEnd + 1].Trim());
        Assert.Contains("Second paragraph", lines[firstParaEnd + 2]);
    }

    private static string FormatSource(string source)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        return Formatter.Format(parse.Module, lex.Tokens);
    }
}
