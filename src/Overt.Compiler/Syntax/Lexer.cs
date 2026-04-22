using System.Collections.Immutable;
using System.Text;
using Overt.Compiler.Diagnostics;

namespace Overt.Compiler.Syntax;

/// <summary>
/// Tokenizes a single Overt source file. Authoritative spec:
/// <c>docs/grammar/lexical.md</c>. Divergence between this implementation and the spec
/// is a bug in one or the other; the test suite pins them together.
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
            ["type"] = TokenKind.KeywordType,
        }.ToImmutableDictionary();

    // Mode stack. The top frame determines how the next token is scanned.
    // See docs/grammar/lexical.md §6.2 for the automaton.
    private abstract class Frame { }
    private sealed class DefaultFrame : Frame { }
    private sealed class StringBodyFrame : Frame { public bool SeenSegment; }
    private sealed class InterpolationFrame : Frame { public int BraceDepth; }

    private readonly string _source;
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly Stack<Frame> _modes = new();
    private readonly Queue<Token> _pending = new();

    private int _position;
    private int _line = 1;
    private int _column = 1;

    private Lexer(string source)
    {
        _source = source;
        _modes.Push(new DefaultFrame());
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
        if (_pending.TryDequeue(out var queued))
        {
            return queued;
        }

        var top = _modes.Peek();
        if (top is StringBodyFrame body)
        {
            return LexStringBody(body, openStart: null);
        }

        SkipTrivia();

        if (IsAtEnd)
        {
            var here = CurrentPosition;
            return new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(here, here));
        }

        var start = CurrentPosition;
        var ch = Peek();

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
            return OpenStringBody(start);
        }

        if (top is InterpolationFrame interp)
        {
            if (ch == '{')
            {
                interp.BraceDepth++;
                return LexPunctuation(start);
            }
            if (ch == '}' && interp.BraceDepth == 0)
            {
                Advance();
                _modes.Pop();
                return new Token(TokenKind.InterpolationEnd, "}", new SourceSpan(start, CurrentPosition));
            }
            if (ch == '}')
            {
                interp.BraceDepth--;
                return LexPunctuation(start);
            }
        }

        return LexPunctuation(start);
    }

    private Token OpenStringBody(SourcePosition start)
    {
        var frame = new StringBodyFrame();
        _modes.Push(frame);
        Advance(); // consume opening "
        return LexStringBody(frame, openStart: start);
    }

    /// <summary>
    /// Scans a run of literal text in string-body mode. Emits one of
    /// <see cref="TokenKind.StringLiteral"/> (whole string, no interpolation),
    /// <see cref="TokenKind.StringHead"/> (first segment of an interpolated string),
    /// <see cref="TokenKind.StringMiddle"/> (between two interpolations), or
    /// <see cref="TokenKind.StringTail"/> (final segment).
    /// Queues any cross-mode transition tokens (<c>Dollar + Identifier</c> or
    /// <c>InterpolationStart</c>) into <see cref="_pending"/>.
    /// </summary>
    private Token LexStringBody(StringBodyFrame frame, SourcePosition? openStart)
    {
        var segmentStart = openStart ?? CurrentPosition;
        var lexeme = new StringBuilder();
        if (openStart.HasValue)
        {
            lexeme.Append('"');
        }

        while (true)
        {
            if (IsAtEnd)
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0001",
                    "unterminated string literal: reached end of file",
                    new SourceSpan(segmentStart, CurrentPosition)));
                _modes.Pop();
                return new Token(
                    frame.SeenSegment ? TokenKind.StringTail : TokenKind.StringLiteral,
                    lexeme.ToString(),
                    new SourceSpan(segmentStart, CurrentPosition));
            }

            var c = Peek();

            if (c == '\n')
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0001",
                    "unterminated string literal: newline before closing quote",
                    new SourceSpan(segmentStart, CurrentPosition)));
                _modes.Pop();
                return new Token(
                    frame.SeenSegment ? TokenKind.StringTail : TokenKind.StringLiteral,
                    lexeme.ToString(),
                    new SourceSpan(segmentStart, CurrentPosition));
            }

            if (c == '\\')
            {
                lexeme.Append(Advance());
                if (!IsAtEnd)
                {
                    lexeme.Append(Advance());
                }
                continue;
            }

            if (c == '"')
            {
                lexeme.Append(Advance());
                _modes.Pop();
                var kind = frame.SeenSegment ? TokenKind.StringTail : TokenKind.StringLiteral;
                return new Token(kind, lexeme.ToString(), new SourceSpan(segmentStart, CurrentPosition));
            }

            if (c == '$')
            {
                var segmentEnd = CurrentPosition;
                var dollarStart = CurrentPosition;
                Advance(); // consume $

                if (!IsAtEnd && Peek() == '{')
                {
                    Advance(); // consume {
                    _pending.Enqueue(new Token(
                        TokenKind.InterpolationStart,
                        "${",
                        new SourceSpan(dollarStart, CurrentPosition)));
                    _modes.Push(new InterpolationFrame());
                }
                else if (!IsAtEnd && IsIdentifierStart(Peek()))
                {
                    _pending.Enqueue(new Token(
                        TokenKind.Dollar,
                        "$",
                        new SourceSpan(dollarStart, CurrentPosition)));

                    var identStart = CurrentPosition;
                    var identBuilder = new StringBuilder();
                    while (!IsAtEnd && IsIdentifierContinue(Peek()))
                    {
                        identBuilder.Append(Advance());
                    }
                    _pending.Enqueue(new Token(
                        TokenKind.Identifier,
                        identBuilder.ToString(),
                        new SourceSpan(identStart, CurrentPosition)));
                }
                else
                {
                    _diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "OV0003",
                        "bare '$' in string body must be followed by an identifier or '{'; use '\\$' for a literal '$'",
                        new SourceSpan(dollarStart, CurrentPosition)));
                    _pending.Enqueue(new Token(
                        TokenKind.Dollar,
                        "$",
                        new SourceSpan(dollarStart, CurrentPosition)));
                }

                var segmentKind = frame.SeenSegment ? TokenKind.StringMiddle : TokenKind.StringHead;
                frame.SeenSegment = true;
                return new Token(segmentKind, lexeme.ToString(), new SourceSpan(segmentStart, segmentEnd));
            }

            lexeme.Append(Advance());
        }
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

        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X' || Peek(1) == 'b' || Peek(1) == 'B'))
        {
            builder.Append(Advance());
            builder.Append(Advance());
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
            builder.Append(Advance());
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

    /// <summary>
    /// Skips whitespace, line terminators, and line comments. Called in default and
    /// interpolation modes; string-body mode has its own scanning rules.
    /// </summary>
    private void SkipTrivia()
    {
        while (!IsAtEnd)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
                continue;
            }
            if (c == '/' && Peek(1) == '/')
            {
                while (!IsAtEnd && Peek() != '\n')
                {
                    Advance();
                }
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
