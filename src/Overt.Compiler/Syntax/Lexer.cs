using System.Collections.Immutable;
using System.Text;
using Overt.Compiler.Diagnostics;

namespace Overt.Compiler.Syntax;

/// <summary>
/// Tokenizes a single Overt source file into a flat token stream.
///
/// Not final. The current grammar surface supports enough of §7 to lex the hand-written
/// examples under <c>examples/</c>. Missing: string interpolation segmentation (strings are
/// lexed as one <see cref="TokenKind.StringLiteral"/> today; interpolation parsing happens
/// post-lex), character literals (not in §7), raw strings, numeric type suffixes.
/// </summary>
public sealed class Lexer
{
    private static readonly ImmutableDictionary<string, TokenKind> Keywords =
        new Dictionary<string, TokenKind>(StringComparer.Ordinal)
        {
            ["fn"] = TokenKind.KeywordFn,
            ["let"] = TokenKind.KeywordLet,
            ["mut"] = TokenKind.KeywordMut,
            ["with"] = TokenKind.KeywordWith,
            ["record"] = TokenKind.KeywordRecord,
            ["enum"] = TokenKind.KeywordEnum,
            ["match"] = TokenKind.KeywordMatch,
            ["if"] = TokenKind.KeywordIf,
            ["else"] = TokenKind.KeywordElse,
            ["for"] = TokenKind.KeywordFor,
            ["each"] = TokenKind.KeywordEach,
            ["while"] = TokenKind.KeywordWhile,
            ["loop"] = TokenKind.KeywordLoop,
            ["return"] = TokenKind.KeywordReturn,
            ["use"] = TokenKind.KeywordUse,
            ["module"] = TokenKind.KeywordModule,
            ["pub"] = TokenKind.KeywordPub,
            ["parallel"] = TokenKind.KeywordParallel,
            ["race"] = TokenKind.KeywordRace,
            ["trace"] = TokenKind.KeywordTrace,
            ["true"] = TokenKind.KeywordTrue,
            ["false"] = TokenKind.KeywordFalse,
            ["where"] = TokenKind.KeywordWhere,
            ["extern"] = TokenKind.KeywordExtern,
            ["unsafe"] = TokenKind.KeywordUnsafe,
            ["from"] = TokenKind.KeywordFrom,
            ["as"] = TokenKind.KeywordAs,
            ["in"] = TokenKind.KeywordIn,
        }.ToImmutableDictionary();

    private readonly string _source;
    private readonly List<Diagnostic> _diagnostics = new();

    private int _position;
    private int _line = 1;
    private int _column = 1;

    private Lexer(string source)
    {
        _source = source;
    }

    public static LexResult Lex(string source)
    {
        var lexer = new Lexer(source);
        var tokens = new List<Token>();
        while (true)
        {
            var token = lexer.NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
            {
                break;
            }
        }
        return new LexResult(tokens.ToImmutableArray(), lexer._diagnostics.ToImmutableArray());
    }

    private Token NextToken()
    {
        SkipWhitespace();

        if (IsAtEnd)
        {
            var here = CurrentPosition;
            return new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(here, here));
        }

        var start = CurrentPosition;
        var ch = Peek();

        if (ch == '/' && Peek(1) == '/')
        {
            return LexLineComment(start);
        }

        if (IsIdentifierStart(ch))
        {
            return LexIdentifierOrKeyword(start);
        }

        if (IsDigit(ch))
        {
            return LexNumber(start);
        }

        if (ch == '"')
        {
            return LexString(start);
        }

        return LexPunctuation(start);
    }

    private Token LexLineComment(SourcePosition start)
    {
        var builder = new StringBuilder();
        while (!IsAtEnd && Peek() != '\n')
        {
            builder.Append(Advance());
        }
        return new Token(TokenKind.LineComment, builder.ToString(), new SourceSpan(start, CurrentPosition));
    }

    private Token LexIdentifierOrKeyword(SourcePosition start)
    {
        var builder = new StringBuilder();
        while (!IsAtEnd && IsIdentifierContinue(Peek()))
        {
            builder.Append(Advance());
        }
        var lexeme = builder.ToString();
        var kind = Keywords.TryGetValue(lexeme, out var keyword) ? keyword : TokenKind.Identifier;
        return new Token(kind, lexeme, new SourceSpan(start, CurrentPosition));
    }

    private Token LexNumber(SourcePosition start)
    {
        var builder = new StringBuilder();

        // Hex / binary prefix
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X' || Peek(1) == 'b' || Peek(1) == 'B'))
        {
            builder.Append(Advance()); // 0
            builder.Append(Advance()); // x or b
            while (!IsAtEnd && (IsHexDigit(Peek()) || Peek() == '_'))
            {
                builder.Append(Advance());
            }
            return new Token(TokenKind.IntegerLiteral, builder.ToString(), new SourceSpan(start, CurrentPosition));
        }

        while (!IsAtEnd && (IsDigit(Peek()) || Peek() == '_'))
        {
            builder.Append(Advance());
        }

        var isFloat = false;

        if (!IsAtEnd && Peek() == '.' && IsDigit(Peek(1)))
        {
            isFloat = true;
            builder.Append(Advance()); // .
            while (!IsAtEnd && (IsDigit(Peek()) || Peek() == '_'))
            {
                builder.Append(Advance());
            }
        }

        if (!IsAtEnd && (Peek() == 'e' || Peek() == 'E'))
        {
            isFloat = true;
            builder.Append(Advance());
            if (!IsAtEnd && (Peek() == '+' || Peek() == '-'))
            {
                builder.Append(Advance());
            }
            while (!IsAtEnd && (IsDigit(Peek()) || Peek() == '_'))
            {
                builder.Append(Advance());
            }
        }

        var kind = isFloat ? TokenKind.FloatLiteral : TokenKind.IntegerLiteral;
        return new Token(kind, builder.ToString(), new SourceSpan(start, CurrentPosition));
    }

    private Token LexString(SourcePosition start)
    {
        var builder = new StringBuilder();
        builder.Append(Advance()); // opening "

        while (!IsAtEnd && Peek() != '"')
        {
            var c = Peek();
            if (c == '\n')
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0001",
                    "unterminated string literal: newline before closing quote",
                    new SourceSpan(start, CurrentPosition)));
                break;
            }
            if (c == '\\')
            {
                builder.Append(Advance());
                if (!IsAtEnd)
                {
                    builder.Append(Advance());
                }
                continue;
            }
            builder.Append(Advance());
        }

        if (!IsAtEnd && Peek() == '"')
        {
            builder.Append(Advance()); // closing "
        }
        else
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "OV0001",
                "unterminated string literal: reached end of file",
                new SourceSpan(start, CurrentPosition)));
        }

        return new Token(TokenKind.StringLiteral, builder.ToString(), new SourceSpan(start, CurrentPosition));
    }

    private Token LexPunctuation(SourcePosition start)
    {
        var c = Advance();

        switch (c)
        {
            case '(': return Emit(TokenKind.LeftParen, "(", start);
            case ')': return Emit(TokenKind.RightParen, ")", start);
            case '{': return Emit(TokenKind.LeftBrace, "{", start);
            case '}': return Emit(TokenKind.RightBrace, "}", start);
            case '[': return Emit(TokenKind.LeftBracket, "[", start);
            case ']': return Emit(TokenKind.RightBracket, "]", start);
            case ',': return Emit(TokenKind.Comma, ",", start);
            case ';': return Emit(TokenKind.Semicolon, ";", start);
            case '.': return Emit(TokenKind.Dot, ".", start);
            case '@': return Emit(TokenKind.At, "@", start);
            case '?': return Emit(TokenKind.Question, "?", start);
            case '+': return Emit(TokenKind.Plus, "+", start);
            case '*': return Emit(TokenKind.Star, "*", start);
            case '/': return Emit(TokenKind.Slash, "/", start);
            case '%': return Emit(TokenKind.Percent, "%", start);
            case '^': return Emit(TokenKind.Caret, "^", start);
            case '~': return Emit(TokenKind.Tilde, "~", start);

            case ':':
                if (Match(':')) return Emit(TokenKind.ColonColon, "::", start);
                return Emit(TokenKind.Colon, ":", start);

            case '!':
                if (Match('=')) return Emit(TokenKind.BangEquals, "!=", start);
                return Emit(TokenKind.Bang, "!", start);

            case '=':
                if (Match('=')) return Emit(TokenKind.EqualsEquals, "==", start);
                if (Match('>')) return Emit(TokenKind.FatArrow, "=>", start);
                return Emit(TokenKind.Equals, "=", start);

            case '-':
                if (Match('>')) return Emit(TokenKind.Arrow, "->", start);
                return Emit(TokenKind.Minus, "-", start);

            case '<':
                if (Match('=')) return Emit(TokenKind.LessEquals, "<=", start);
                return Emit(TokenKind.Less, "<", start);

            case '>':
                if (Match('=')) return Emit(TokenKind.GreaterEquals, ">=", start);
                return Emit(TokenKind.Greater, ">", start);

            case '&':
                if (Match('&')) return Emit(TokenKind.AmpersandAmpersand, "&&", start);
                return Emit(TokenKind.Ampersand, "&", start);

            case '|':
                if (Match('|')) return Emit(TokenKind.PipePipe, "||", start);
                if (Match('>'))
                {
                    if (Match('?')) return Emit(TokenKind.PipePropagate, "|>?", start);
                    return Emit(TokenKind.PipeCompose, "|>", start);
                }
                return Emit(TokenKind.Pipe, "|", start);

            default:
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0002",
                    $"unexpected character '{c}'",
                    new SourceSpan(start, CurrentPosition)));
                return Emit(TokenKind.Unknown, c.ToString(), start);
        }
    }

    private Token Emit(TokenKind kind, string lexeme, SourcePosition start)
        => new(kind, lexeme, new SourceSpan(start, CurrentPosition));

    private void SkipWhitespace()
    {
        while (!IsAtEnd)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
                continue;
            }
            break;
        }
    }

    private bool Match(char expected)
    {
        if (IsAtEnd || _source[_position] != expected)
        {
            return false;
        }
        Advance();
        return true;
    }

    private char Advance()
    {
        var c = _source[_position++];
        if (c == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        return c;
    }

    private char Peek(int offset = 0)
        => _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private SourcePosition CurrentPosition => new(_line, _column);

    private bool IsAtEnd => _position >= _source.Length;

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsHexDigit(char c)
        => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsIdentifierStart(char c)
        => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsIdentifierContinue(char c)
        => IsIdentifierStart(c) || IsDigit(c);
}

public sealed record LexResult(
    ImmutableArray<Token> Tokens,
    ImmutableArray<Diagnostic> Diagnostics);
