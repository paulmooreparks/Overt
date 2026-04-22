using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class LexerTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

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
}
