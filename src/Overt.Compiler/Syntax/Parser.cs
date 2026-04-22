using System.Collections.Immutable;
using Overt.Compiler.Diagnostics;

namespace Overt.Compiler.Syntax;

/// <summary>
/// Recursive-descent parser from token stream to AST. The grammar implemented here
/// matches <c>docs/grammar/precedence.md §8</c> — each precedence level is a method,
/// higher-precedence methods sit below lower ones in the call graph.
///
/// The parser records diagnostics but does not throw. On error it synthesizes a
/// best-effort AST node and continues; downstream code may encounter holes but must
/// not crash.
/// </summary>
public sealed class Parser
{
    private readonly ImmutableArray<Token> _tokens;
    private readonly List<Diagnostic> _diagnostics = new();
    private int _cursor;

    private Parser(ImmutableArray<Token> tokens)
    {
        _tokens = tokens;
    }

    public static ParseResult Parse(ImmutableArray<Token> tokens)
    {
        var parser = new Parser(tokens);
        var module = parser.ParseModule();
        return new ParseResult(module, parser._diagnostics.ToImmutableArray());
    }

    // ---------------------------------------------------------------- module

    private ModuleDecl ParseModule()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordModule, "module declaration");
        var nameToken = Expect(TokenKind.Identifier, "module name");

        var declarations = ImmutableArray.CreateBuilder<Declaration>();
        while (!Check(TokenKind.EndOfFile))
        {
            var before = _cursor;
            var decl = ParseDeclaration();
            if (decl is not null)
            {
                declarations.Add(decl);
            }
            if (_cursor == before)
            {
                // Could not make progress — emit a diagnostic and skip one token to break the loop.
                ReportError("OV0150", $"unexpected token {Current.Kind} at top level", Current.Span);
                Advance();
            }
        }

        var endPos = Current.Span.End;
        return new ModuleDecl(
            nameToken.Lexeme,
            declarations.ToImmutable(),
            new SourceSpan(startPos, endPos));
    }

    // ------------------------------------------------------- declarations

    private Declaration? ParseDeclaration()
    {
        if (Check(TokenKind.KeywordFn))
        {
            return ParseFunctionDecl();
        }

        return null;
    }

    private FunctionDecl ParseFunctionDecl()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordFn, "function declaration");
        var nameToken = Expect(TokenKind.Identifier, "function name");
        Expect(TokenKind.LeftParen, "function parameter list");

        var parameters = ParseParameterList();
        Expect(TokenKind.RightParen, "function parameter list");

        EffectRow? effects = null;
        if (Check(TokenKind.Bang))
        {
            effects = ParseEffectRow();
        }

        TypeExpr? returnType = null;
        if (Check(TokenKind.Arrow))
        {
            Advance();
            returnType = ParseTypeExpr();
        }

        var body = ParseBlock();
        return new FunctionDecl(
            nameToken.Lexeme,
            parameters,
            effects,
            returnType,
            body,
            new SourceSpan(startPos, body.Span.End));
    }

    private ImmutableArray<Parameter> ParseParameterList()
    {
        if (Check(TokenKind.RightParen))
        {
            return ImmutableArray<Parameter>.Empty;
        }

        var parameters = ImmutableArray.CreateBuilder<Parameter>();
        parameters.Add(ParseParameter());
        while (Match(TokenKind.Comma))
        {
            if (Check(TokenKind.RightParen))
            {
                break; // trailing comma allowed
            }
            parameters.Add(ParseParameter());
        }
        return parameters.ToImmutable();
    }

    private Parameter ParseParameter()
    {
        var nameToken = Expect(TokenKind.Identifier, "parameter name");
        Expect(TokenKind.Colon, "parameter type annotation");
        var type = ParseTypeExpr();
        return new Parameter(
            nameToken.Lexeme,
            type,
            new SourceSpan(nameToken.Span.Start, type.Span.End));
    }

    private EffectRow ParseEffectRow()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.Bang, "effect row");
        Expect(TokenKind.LeftBrace, "effect row");

        var effects = ImmutableArray.CreateBuilder<string>();
        if (!Check(TokenKind.RightBrace))
        {
            effects.Add(ExpectEffectName());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightBrace))
                {
                    break;
                }
                effects.Add(ExpectEffectName());
            }
        }

        var closing = Expect(TokenKind.RightBrace, "effect row");
        return new EffectRow(
            effects.ToImmutable(),
            new SourceSpan(startPos, closing.Span.End));
    }

    private string ExpectEffectName()
    {
        // Effect names (`io`, `async`, `inference`) and effect-row variables (any identifier)
        // all lex as Identifier. The distinction is semantic, not syntactic.
        if (Check(TokenKind.Identifier))
        {
            return Advance().Lexeme;
        }
        ReportError("OV0151", $"expected effect name, got {Current.Kind}", Current.Span);
        return Advance().Lexeme;
    }

    // ----------------------------------------------------- type expressions

    private TypeExpr ParseTypeExpr()
    {
        if (Check(TokenKind.LeftParen))
        {
            return ParseUnitOrTupleType();
        }

        if (Check(TokenKind.Identifier))
        {
            return ParseNamedType();
        }

        var spanAtError = Current.Span;
        ReportError("OV0152", $"expected type, got {Current.Kind}", spanAtError);
        // Synthesize a unit type so callers have a non-null TypeExpr.
        return new UnitType(spanAtError);
    }

    private TypeExpr ParseUnitOrTupleType()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.LeftParen, "type");
        if (Check(TokenKind.RightParen))
        {
            var closing = Advance();
            return new UnitType(new SourceSpan(startPos, closing.Span.End));
        }

        // For now, tuple types are not separately modeled — fall back to parsing a single
        // type and emitting a diagnostic. The parser grammar for tuples lands with §9.
        ReportError("OV0153", "tuple types are not yet supported; use a record", Current.Span);
        var inner = ParseTypeExpr();
        var close = Expect(TokenKind.RightParen, "type");
        return inner with { Span = new SourceSpan(startPos, close.Span.End) };
    }

    private NamedType ParseNamedType()
    {
        var nameToken = Expect(TokenKind.Identifier, "type name");
        var endPos = nameToken.Span.End;

        var args = ImmutableArray<TypeExpr>.Empty;
        if (Check(TokenKind.Less))
        {
            args = ParseTypeArgumentList();
            endPos = _tokens[_cursor - 1].Span.End;
        }

        return new NamedType(
            nameToken.Lexeme,
            args,
            new SourceSpan(nameToken.Span.Start, endPos));
    }

    private ImmutableArray<TypeExpr> ParseTypeArgumentList()
    {
        Expect(TokenKind.Less, "type arguments");
        var args = ImmutableArray.CreateBuilder<TypeExpr>();
        if (!Check(TokenKind.Greater))
        {
            args.Add(ParseTypeExpr());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.Greater))
                {
                    break;
                }
                args.Add(ParseTypeExpr());
            }
        }
        Expect(TokenKind.Greater, "type arguments");
        return args.ToImmutable();
    }

    // ---------------------------------------------------------- statements

    private BlockExpr ParseBlock()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.LeftBrace, "block");

        var statements = ImmutableArray.CreateBuilder<Statement>();
        Expression? trailingExpression = null;

        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
        {
            var expr = ParseExpression();

            if (Check(TokenKind.Semicolon))
            {
                var semi = Advance();
                statements.Add(new ExpressionStmt(
                    expr,
                    new SourceSpan(expr.Span.Start, semi.Span.End)));
                continue;
            }

            // No trailing semicolon. This is either the block's result expression or —
            // if there's more to parse before the closing brace — a bare expression statement.
            if (Check(TokenKind.RightBrace))
            {
                trailingExpression = expr;
                break;
            }

            // The expression serves as a statement; its value is discarded.
            statements.Add(new ExpressionStmt(expr, expr.Span));
        }

        var closing = Expect(TokenKind.RightBrace, "block");
        return new BlockExpr(
            statements.ToImmutable(),
            trailingExpression,
            new SourceSpan(startPos, closing.Span.End));
    }

    // ---------------------------------------------------------- expressions

    private Expression ParseExpression() => ParsePipe();

    // Grammar per precedence.md §8. Each method descends to the next precedence level.
    // Today only a subset of operators is parsed; the scaffolding is in place so adding
    // them is mechanical.

    private Expression ParsePipe() => ParseLogicalOr();        // TODO: |> |>?
    private Expression ParseLogicalOr() => ParseLogicalAnd();  // TODO: ||
    private Expression ParseLogicalAnd() => ParseEquality();   // TODO: &&
    private Expression ParseEquality() => ParseComparison();   // TODO: == != (non-assoc)
    private Expression ParseComparison() => ParseAdditive();   // TODO: < <= > >= (non-assoc)
    private Expression ParseAdditive() => ParseMultiplicative(); // TODO: + -
    private Expression ParseMultiplicative() => ParseUnaryPrefix(); // TODO: * / %
    private Expression ParseUnaryPrefix() => ParsePostfix();   // TODO: - ! (non-chainable)

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Check(TokenKind.LeftParen))
            {
                expr = ParseCallTail(expr);
                continue;
            }

            if (Check(TokenKind.Question))
            {
                var q = Advance();
                expr = new PropagateExpr(expr, new SourceSpan(expr.Span.Start, q.Span.End));
                continue;
            }

            // TODO: field access `.ident`

            break;
        }

        return expr;
    }

    private CallExpr ParseCallTail(Expression callee)
    {
        Expect(TokenKind.LeftParen, "call arguments");
        var args = ImmutableArray.CreateBuilder<Argument>();

        if (!Check(TokenKind.RightParen))
        {
            args.Add(ParseArgument());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightParen))
                {
                    break;
                }
                args.Add(ParseArgument());
            }
        }

        var closing = Expect(TokenKind.RightParen, "call arguments");

        // §7: named-arg rule. Exactly one argument may be positional; otherwise every
        // argument must be named. Enforced here at parse time.
        var frozen = args.ToImmutable();
        if (frozen.Length > 1)
        {
            foreach (var arg in frozen)
            {
                if (arg.Name is null)
                {
                    ReportError(
                        "OV0154",
                        "multi-argument calls require every argument to be named (e.g. `name = value`)",
                        arg.Span);
                    break;
                }
            }
        }

        return new CallExpr(callee, frozen, new SourceSpan(callee.Span.Start, closing.Span.End));
    }

    private Argument ParseArgument()
    {
        // If the next token is Identifier followed by `=`, parse as a named argument.
        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Equals)
        {
            var nameToken = Advance();
            Advance(); // =
            var value = ParseExpression();
            return new Argument(
                nameToken.Lexeme,
                value,
                new SourceSpan(nameToken.Span.Start, value.Span.End));
        }

        var expr = ParseExpression();
        return new Argument(Name: null, expr, expr.Span);
    }

    private Expression ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Identifier:
                Advance();
                return new IdentifierExpr(token.Lexeme, token.Span);

            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpr(token.Lexeme, token.Span);

            case TokenKind.LeftParen:
                return ParseUnitOrParenthesizedExpression();

            case TokenKind.LeftBrace:
                return ParseBlock();

            // TODO: integer/float literals, if/else, match, with, trace, record/list literals,
            // and interpolated strings (StringHead / StringMiddle / StringTail).
        }

        ReportError("OV0155", $"expected expression, got {token.Kind}", token.Span);
        Advance(); // skip offending token so the parser can make progress
        return new UnitExpr(token.Span);
    }

    private Expression ParseUnitOrParenthesizedExpression()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.LeftParen, "expression");
        if (Check(TokenKind.RightParen))
        {
            var closing = Advance();
            return new UnitExpr(new SourceSpan(startPos, closing.Span.End));
        }

        var inner = ParseExpression();
        var close = Expect(TokenKind.RightParen, "expression");
        return inner with { Span = new SourceSpan(startPos, close.Span.End) };
    }

    // ---------------------------------------------------------- primitives

    private Token Current => _tokens[_cursor];

    private Token Peek(int offset)
        => _cursor + offset < _tokens.Length ? _tokens[_cursor + offset] : _tokens[^1];

    private Token Advance() => _tokens[_cursor++];

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (!Check(kind))
        {
            return false;
        }
        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string context)
    {
        if (Check(kind))
        {
            return Advance();
        }
        ReportError(
            "OV0150",
            $"expected {kind} in {context}, got {Current.Kind}",
            Current.Span);
        // Synthesize: do not consume the offending token. Caller continues with the
        // current token, which may unwind further until progress is possible.
        return new Token(kind, string.Empty, Current.Span);
    }

    private void ReportError(string code, string message, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span));
    }
}

public sealed record ParseResult(
    ModuleDecl Module,
    ImmutableArray<Diagnostic> Diagnostics);
