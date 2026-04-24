using System.Runtime.CompilerServices;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class LexerTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static string GoldenDir([CallerFilePath] string callerFilePath = "")
        => Path.Combine(Path.GetDirectoryName(callerFilePath)!, "fixtures", "golden");

    [Fact]
    public void Lex_HelloOv_ProducesExpectedTokenStream()
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, "hello.ov"));

        var result = Lexer.Lex(source);

        Assert.Empty(result.Diagnostics);

        // Drop EOF for comparison; the trailing EOF is tested separately.
        var actual = result.Tokens
            .Where(t => t.Kind != TokenKind.EndOfFile)
            .Select(t => (t.Kind, t.Lexeme))
            .ToArray();

        var expected = new (TokenKind, string)[]
        {
            (TokenKind.KeywordModule, "module"),
            (TokenKind.Identifier, "hello"),

            (TokenKind.KeywordFn, "fn"),
            (TokenKind.Identifier, "main"),
            (TokenKind.LeftParen, "("),
            (TokenKind.RightParen, ")"),
            (TokenKind.Bang, "!"),
            (TokenKind.LeftBrace, "{"),
            (TokenKind.Identifier, "io"),
            (TokenKind.RightBrace, "}"),
            (TokenKind.Arrow, "->"),
            (TokenKind.Identifier, "Result"),
            (TokenKind.Less, "<"),
            (TokenKind.LeftParen, "("),
            (TokenKind.RightParen, ")"),
            (TokenKind.Comma, ","),
            (TokenKind.Identifier, "IoError"),
            (TokenKind.Greater, ">"),
            (TokenKind.LeftBrace, "{"),

            (TokenKind.Identifier, "println"),
            (TokenKind.LeftParen, "("),
            (TokenKind.StringLiteral, "\"Hello, LLM!\""),
            (TokenKind.RightParen, ")"),
            (TokenKind.Question, "?"),

            (TokenKind.Identifier, "Ok"),
            (TokenKind.LeftParen, "("),
            (TokenKind.LeftParen, "("),
            (TokenKind.RightParen, ")"),
            (TokenKind.RightParen, ")"),

            (TokenKind.RightBrace, "}"),
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Lex_HelloOv_EndsWithEof()
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, "hello.ov"));

        var result = Lexer.Lex(source);

        Assert.NotEmpty(result.Tokens);
        Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
    }

    [Fact]
    public void Lex_PipeOperators_DistinguishComposeAndPropagate()
    {
        var result = Lexer.Lex("x |> f |>? g");

        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.Identifier,
                TokenKind.PipeCompose,
                TokenKind.Identifier,
                TokenKind.PipePropagate,
                TokenKind.Identifier,
                TokenKind.EndOfFile,
            },
            kinds);
    }

    [Fact]
    public void Lex_UnterminatedString_ReportsDiagnostic()
    {
        var result = Lexer.Lex("let x = \"unterminated");

        Assert.Contains(result.Diagnostics, d => d.Code == "OV0001");
    }

    [Fact]
    public void Lex_StringWithDollarIdent_SegmentsCorrectly()
    {
        var result = Lexer.Lex("\"Hello, $name!\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.StringHead,
                TokenKind.Dollar,
                TokenKind.Identifier,
                TokenKind.StringTail,
                TokenKind.EndOfFile,
            },
            kinds);

        Assert.Equal("\"Hello, ", result.Tokens[0].Lexeme);
        Assert.Equal("name", result.Tokens[2].Lexeme);
        Assert.Equal("!\"", result.Tokens[3].Lexeme);
    }

    [Fact]
    public void Lex_StringWithBraceInterp_SegmentsCorrectly()
    {
        var result = Lexer.Lex("\"${price * 1.08}\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.StringHead,
                TokenKind.InterpolationStart,
                TokenKind.Identifier,
                TokenKind.Star,
                TokenKind.FloatLiteral,
                TokenKind.InterpolationEnd,
                TokenKind.StringTail,
                TokenKind.EndOfFile,
            },
            kinds);
    }

    [Fact]
    public void Lex_MultipleInterpolations_ProducesMiddleSegments()
    {
        var result = Lexer.Lex("\"a${x}b${y}c\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.StringHead,
                TokenKind.InterpolationStart,
                TokenKind.Identifier,
                TokenKind.InterpolationEnd,
                TokenKind.StringMiddle,
                TokenKind.InterpolationStart,
                TokenKind.Identifier,
                TokenKind.InterpolationEnd,
                TokenKind.StringTail,
                TokenKind.EndOfFile,
            },
            kinds);

        Assert.Equal("\"a", result.Tokens[0].Lexeme);
        Assert.Equal("b", result.Tokens[4].Lexeme);
        Assert.Equal("c\"", result.Tokens[8].Lexeme);
    }

    [Fact]
    public void Lex_NestedStringInInterpolation_WorksRecursively()
    {
        var result = Lexer.Lex("\"outer: ${format(\"inner $who\")}\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.StringHead,        // "outer:
                TokenKind.InterpolationStart,
                TokenKind.Identifier,        // fn
                TokenKind.LeftParen,
                TokenKind.StringHead,        // "inner
                TokenKind.Dollar,
                TokenKind.Identifier,        // who
                TokenKind.StringTail,        // "
                TokenKind.RightParen,
                TokenKind.InterpolationEnd,
                TokenKind.StringTail,        // "
                TokenKind.EndOfFile,
            },
            kinds);
    }

    [Fact]
    public void Lex_InterpolationWithNestedBraces_TracksBraceDepth()
    {
        // The inner { } pair inside the interpolation must not close the interpolation.
        var result = Lexer.Lex("\"${ if x { 1 } else { 2 } }\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.StringHead,
                TokenKind.InterpolationStart,
                TokenKind.KeywordIf,
                TokenKind.Identifier,
                TokenKind.LeftBrace,
                TokenKind.IntegerLiteral,
                TokenKind.RightBrace,
                TokenKind.KeywordElse,
                TokenKind.LeftBrace,
                TokenKind.IntegerLiteral,
                TokenKind.RightBrace,
                TokenKind.InterpolationEnd,
                TokenKind.StringTail,
                TokenKind.EndOfFile,
            },
            kinds);
    }

    [Fact]
    public void Lex_DollarFollowedByDot_OnlyConsumesIdentifier()
    {
        // Per lexical.md §6.4: bare $ident is an identifier only — the dotted path is
        // literal text. To interpolate a dotted path, use ${user.name}.
        var result = Lexer.Lex("\"$user.name\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.StringHead,
                TokenKind.Dollar,
                TokenKind.Identifier,
                TokenKind.StringTail,
                TokenKind.EndOfFile,
            },
            kinds);

        Assert.Equal(".name\"", result.Tokens[3].Lexeme);
    }

    [Fact]
    public void Lex_BareDollarInStringBody_ReportsOV0003()
    {
        var result = Lexer.Lex("\"price is $ cheap\"");

        Assert.Contains(result.Diagnostics, d => d.Code == "OV0003");
    }

    [Fact]
    public void Lex_EscapedDollar_IsLiteralInStringBody()
    {
        var result = Lexer.Lex("\"cost: \\$5\"");

        Assert.Empty(result.Diagnostics);
        var kinds = result.Tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[] { TokenKind.StringLiteral, TokenKind.EndOfFile },
            kinds);
    }

    /// <summary>
    /// Locks down the token stream for every example. Set <c>OVERT_UPDATE_GOLDEN=1</c>
    /// in the environment to regenerate the golden files after an intentional change.
    /// </summary>
    [Theory]
    [InlineData("bst.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("effects.ov")]
    [InlineData("ffi.ov")]
    [InlineData("hello.ov")]
    [InlineData("inference.ov")]
    [InlineData("json.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("race.ov")]
    [InlineData("refinement.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("trace.ov")]
    public void Lex_Example_MatchesGoldenTokenStream(string exampleFile)
    {
        var sourcePath = Path.Combine(ExamplesDir, exampleFile);
        var source = File.ReadAllText(sourcePath);

        var result = Lexer.Lex(source);

        Assert.Empty(result.Diagnostics);

        var actual = string.Join("\n", result.Tokens.Select(t => t.ToString())) + "\n";
        var goldenPath = Path.Combine(GoldenDir(), exampleFile + ".tokens");

        if (Environment.GetEnvironmentVariable("OVERT_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(goldenPath, actual);
            return;
        }

        Assert.True(
            File.Exists(goldenPath),
            $"Golden file not found: {goldenPath}. Run tests with OVERT_UPDATE_GOLDEN=1 to create.");

        var expected = File.ReadAllText(goldenPath);
        Assert.Equal(expected, actual);
    }
}
