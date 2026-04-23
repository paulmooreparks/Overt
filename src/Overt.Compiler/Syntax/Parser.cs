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

    /// <summary>
    /// When false, <see cref="ParsePrimary"/> will not interpret an identifier followed by
    /// <c>{</c> as a record literal. Set in "condition-like" positions (the head of
    /// <c>if</c>, <c>while</c>, <c>match</c>) so the following <c>{</c> can open the
    /// construct's body instead. Rust's struct-expression-disambiguation trick.
    /// </summary>
    private bool _allowRecordLiteral = true;

    private Parser(ImmutableArray<Token> tokens)
    {
        _tokens = tokens;
    }

    public static ParseResult Parse(ImmutableArray<Token> tokens)
    {
        // Filter out comment tokens before parsing. Comments are preserved in the
        // full token stream for the formatter; the parser's AST has no home for
        // them today, and skipping them at navigation time would complicate every
        // Peek() call. The formatter reads the original (unfiltered) token array
        // from LexResult.Tokens and re-associates comments with AST spans.
        var filtered = tokens.Length > 0 && tokens.Any(t => t.Kind == TokenKind.LineComment)
            ? tokens.Where(t => t.Kind != TokenKind.LineComment).ToImmutableArray()
            : tokens;
        var parser = new Parser(filtered);
        var module = parser.ParseModule();
        return new ParseResult(module, parser._diagnostics.ToImmutableArray());
    }

    // ---------------------------------------------------------------- module

    private ModuleDecl ParseModule()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordModule, "module declaration");
        var nameToken = Expect(TokenKind.Identifier, "module name");
        var nameBuilder = new System.Text.StringBuilder(nameToken.Lexeme);
        // Dotted module names (module stdlib.http.client). Each segment must
        // match the sibling .ov file's location under stdlib/http/client.ov.
        while (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance(); // .
            nameBuilder.Append('.');
            nameBuilder.Append(Advance().Lexeme);
        }
        var moduleName = nameBuilder.ToString();

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
            moduleName,
            declarations.ToImmutable(),
            new SourceSpan(startPos, endPos));
    }

    // ------------------------------------------------------- declarations

    private Declaration? ParseDeclaration()
    {
        var attributes = ImmutableArray<Annotation>.Empty;
        if (Check(TokenKind.At))
        {
            attributes = ParseAnnotationList();
        }

        if (Check(TokenKind.KeywordFn))
        {
            if (attributes.Length > 0)
            {
                ReportError("OV0157",
                    "attributes on `fn` declarations are not supported in v1",
                    attributes[0].Span);
            }
            return ParseFunctionDecl();
        }
        if (Check(TokenKind.KeywordRecord))
        {
            return ParseRecordDecl(attributes);
        }
        if (Check(TokenKind.KeywordEnum))
        {
            return ParseEnumDecl(attributes);
        }
        if (Check(TokenKind.KeywordExtern)
            || (Check(TokenKind.KeywordUnsafe) && Peek(1).Kind == TokenKind.KeywordExtern))
        {
            if (attributes.Length > 0)
            {
                ReportError("OV0157",
                    "attributes on `extern` declarations are not supported in v1",
                    attributes[0].Span);
            }
            return ParseExternDecl();
        }
        if (Check(TokenKind.KeywordType))
        {
            if (attributes.Length > 0)
            {
                ReportError("OV0157",
                    "attributes on `type` aliases are not supported in v1",
                    attributes[0].Span);
            }
            return ParseTypeAliasDecl();
        }
        if (Check(TokenKind.KeywordUse))
        {
            if (attributes.Length > 0)
            {
                ReportError("OV0157",
                    "attributes on `use` declarations are not supported",
                    attributes[0].Span);
            }
            return ParseUseDecl();
        }

        if (attributes.Length > 0)
        {
            ReportError("OV0157",
                "attributes must precede a declaration",
                attributes[0].Span);
        }
        return null;
    }

    private Declaration ParseExternDecl()
    {
        var startPos = Current.Span.Start;
        var isUnsafe = Match(TokenKind.KeywordUnsafe);
        Expect(TokenKind.KeywordExtern, "extern declaration");

        var platformToken = Expect(TokenKind.StringLiteral, "extern platform string");
        var platform = StripQuotes(platformToken.Lexeme);

        // `extern "platform" type Name binds "..."` — opaque type import.
        if (Check(TokenKind.KeywordType))
        {
            if (isUnsafe)
            {
                ReportError("OV0157",
                    "`unsafe` is not applicable to extern type declarations",
                    new SourceSpan(startPos, Current.Span.End));
            }
            return ParseExternTypeDeclRest(platform, startPos);
        }

        Expect(TokenKind.KeywordFn, "extern function signature");
        var nameToken = Expect(TokenKind.Identifier, "extern function name");
        Expect(TokenKind.LeftParen, "extern parameter list");
        var parameters = ParseParameterList();
        Expect(TokenKind.RightParen, "extern parameter list");

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

        // `binds "..."` — mandatory.
        ExpectContextualKeyword("binds", "extern declaration");
        var bindsToken = Expect(TokenKind.StringLiteral, "binds target string");
        var bindsTarget = StripQuotes(bindsToken.Lexeme);
        var endPos = bindsToken.Span.End;

        // `from "..."` — C FFI only; the parser is permissive and the later pass checks.
        string? fromLibrary = null;
        if (Check(TokenKind.Identifier) && Current.Lexeme == "from")
        {
            Advance();
            var fromToken = Expect(TokenKind.StringLiteral, "from library string");
            fromLibrary = StripQuotes(fromToken.Lexeme);
            endPos = fromToken.Span.End;
        }

        return new ExternDecl(
            platform,
            isUnsafe,
            nameToken.Lexeme,
            parameters,
            effects,
            returnType,
            bindsTarget,
            fromLibrary,
            new SourceSpan(startPos, endPos));
    }

    private ExternTypeDecl ParseExternTypeDeclRest(string platform, SourcePosition startPos)
    {
        Expect(TokenKind.KeywordType, "extern type declaration");
        var nameToken = Expect(TokenKind.Identifier, "extern type name");
        ExpectContextualKeyword("binds", "extern type declaration");
        var bindsToken = Expect(TokenKind.StringLiteral, "binds target string");
        var bindsTarget = StripQuotes(bindsToken.Lexeme);
        return new ExternTypeDecl(
            platform,
            nameToken.Lexeme,
            bindsTarget,
            new SourceSpan(startPos, bindsToken.Span.End));
    }

    private void ExpectContextualKeyword(string word, string context)
    {
        if (Check(TokenKind.Identifier) && Current.Lexeme == word)
        {
            Advance();
            return;
        }
        ReportError("OV0160",
            $"expected `{word}` in {context}, got {Current.Kind}",
            Current.Span);
    }

    private static string StripQuotes(string lexeme)
    {
        if (lexeme.Length >= 2 && lexeme[0] == '"' && lexeme[^1] == '"')
        {
            return lexeme[1..^1];
        }
        return lexeme;
    }

    private ImmutableArray<Annotation> ParseAnnotationList()
    {
        var attributes = ImmutableArray.CreateBuilder<Annotation>();
        while (Check(TokenKind.At))
        {
            attributes.Add(ParseAnnotation());
        }
        return attributes.ToImmutable();
    }

    private Annotation ParseAnnotation()
    {
        var at = Expect(TokenKind.At, "attribute");
        var nameToken = Expect(TokenKind.Identifier, "attribute name");

        var arguments = ImmutableArray<string>.Empty;
        var endPos = nameToken.Span.End;
        if (Match(TokenKind.LeftParen))
        {
            var args = ImmutableArray.CreateBuilder<string>();
            if (!Check(TokenKind.RightParen))
            {
                args.Add(Expect(TokenKind.Identifier, "attribute argument").Lexeme);
                while (Match(TokenKind.Comma))
                {
                    if (Check(TokenKind.RightParen))
                    {
                        break;
                    }
                    args.Add(Expect(TokenKind.Identifier, "attribute argument").Lexeme);
                }
            }
            var closing = Expect(TokenKind.RightParen, "attribute arguments");
            arguments = args.ToImmutable();
            endPos = closing.Span.End;
        }

        return new Annotation(nameToken.Lexeme, arguments, new SourceSpan(at.Span.Start, endPos));
    }

    private RecordDecl ParseRecordDecl(ImmutableArray<Annotation> attributes)
    {
        var startPos = attributes.Length > 0 ? attributes[0].Span.Start : Current.Span.Start;
        Expect(TokenKind.KeywordRecord, "record declaration");
        var nameToken = Expect(TokenKind.Identifier, "record name");
        Expect(TokenKind.LeftBrace, "record body");

        var fields = ImmutableArray.CreateBuilder<RecordField>();
        if (!Check(TokenKind.RightBrace))
        {
            fields.Add(ParseRecordFieldDecl());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightBrace))
                {
                    break;
                }
                fields.Add(ParseRecordFieldDecl());
            }
        }

        var closing = Expect(TokenKind.RightBrace, "record body");
        return new RecordDecl(
            nameToken.Lexeme,
            attributes,
            fields.ToImmutable(),
            new SourceSpan(startPos, closing.Span.End));
    }

    private EnumDecl ParseEnumDecl(ImmutableArray<Annotation> attributes)
    {
        var startPos = attributes.Length > 0 ? attributes[0].Span.Start : Current.Span.Start;
        Expect(TokenKind.KeywordEnum, "enum declaration");
        var nameToken = Expect(TokenKind.Identifier, "enum name");
        Expect(TokenKind.LeftBrace, "enum body");

        var variants = ImmutableArray.CreateBuilder<EnumVariant>();
        if (!Check(TokenKind.RightBrace))
        {
            variants.Add(ParseEnumVariant());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightBrace))
                {
                    break;
                }
                variants.Add(ParseEnumVariant());
            }
        }

        var closing = Expect(TokenKind.RightBrace, "enum body");
        return new EnumDecl(
            nameToken.Lexeme,
            attributes,
            variants.ToImmutable(),
            new SourceSpan(startPos, closing.Span.End));
    }

    private EnumVariant ParseEnumVariant()
    {
        var nameToken = Expect(TokenKind.Identifier, "enum variant name");
        var endPos = nameToken.Span.End;
        var fields = ImmutableArray<RecordField>.Empty;

        if (Check(TokenKind.LeftBrace))
        {
            Advance(); // {
            var builder = ImmutableArray.CreateBuilder<RecordField>();
            if (!Check(TokenKind.RightBrace))
            {
                builder.Add(ParseRecordFieldDecl());
                while (Match(TokenKind.Comma))
                {
                    if (Check(TokenKind.RightBrace))
                    {
                        break;
                    }
                    builder.Add(ParseRecordFieldDecl());
                }
            }
            var closing = Expect(TokenKind.RightBrace, "enum variant fields");
            fields = builder.ToImmutable();
            endPos = closing.Span.End;
        }

        return new EnumVariant(
            nameToken.Lexeme,
            fields,
            new SourceSpan(nameToken.Span.Start, endPos));
    }

    private RecordField ParseRecordFieldDecl()
    {
        var nameToken = Expect(TokenKind.Identifier, "field name");
        Expect(TokenKind.Colon, "field type annotation");
        var type = ParseTypeExpr();
        return new RecordField(
            nameToken.Lexeme,
            type,
            new SourceSpan(nameToken.Span.Start, type.Span.End));
    }

    private FunctionDecl ParseFunctionDecl()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordFn, "function declaration");
        var nameToken = Expect(TokenKind.Identifier, "function name");

        var typeParams = Check(TokenKind.Less)
            ? ParseTypeParameterList()
            : ImmutableArray<string>.Empty;

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
            typeParams,
            parameters,
            effects,
            returnType,
            body,
            new SourceSpan(startPos, body.Span.End));
    }

    private TypeAliasDecl ParseTypeAliasDecl()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordType, "type alias");
        var nameToken = Expect(TokenKind.Identifier, "type alias name");

        var typeParams = Check(TokenKind.Less)
            ? ParseTypeParameterList()
            : ImmutableArray<string>.Empty;

        Expect(TokenKind.Equals, "type alias initializer");
        var target = ParseTypeExpr();

        Expression? predicate = null;
        var endPos = target.Span.End;
        if (Match(TokenKind.KeywordWhere))
        {
            predicate = ParseExpression();
            endPos = predicate.Span.End;
        }

        return new TypeAliasDecl(
            nameToken.Lexeme,
            typeParams,
            target,
            predicate,
            new SourceSpan(startPos, endPos));
    }

    private ImmutableArray<string> ParseTypeParameterList()
    {
        Expect(TokenKind.Less, "type parameters");
        var builder = ImmutableArray.CreateBuilder<string>();
        if (!Check(TokenKind.Greater))
        {
            builder.Add(Expect(TokenKind.Identifier, "type parameter name").Lexeme);
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.Greater))
                {
                    break;
                }
                builder.Add(Expect(TokenKind.Identifier, "type parameter name").Lexeme);
            }
        }
        Expect(TokenKind.Greater, "type parameters");
        return builder.ToImmutable();
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

        if (Check(TokenKind.KeywordFn))
        {
            return ParseFunctionType();
        }

        var spanAtError = Current.Span;
        ReportError("OV0152", $"expected type, got {Current.Kind}", spanAtError);
        // Synthesize a unit type so callers have a non-null TypeExpr.
        return new UnitType(spanAtError);
    }

    private FunctionType ParseFunctionType()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordFn, "function type");
        Expect(TokenKind.LeftParen, "function type parameters");

        var parameters = ImmutableArray.CreateBuilder<TypeExpr>();
        if (!Check(TokenKind.RightParen))
        {
            parameters.Add(ParseTypeExpr());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightParen))
                {
                    break;
                }
                parameters.Add(ParseTypeExpr());
            }
        }
        Expect(TokenKind.RightParen, "function type parameters");

        EffectRow? effects = null;
        if (Check(TokenKind.Bang))
        {
            effects = ParseEffectRow();
        }

        Expect(TokenKind.Arrow, "function type return");
        var returnType = ParseTypeExpr();
        return new FunctionType(
            parameters.ToImmutable(),
            effects,
            returnType,
            new SourceSpan(startPos, returnType.Span.End));
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
            // A let-binding is always a statement, never a trailing expression.
            if (Check(TokenKind.KeywordLet))
            {
                statements.Add(ParseLetStmt());
                Match(TokenKind.Semicolon); // optional trailing `;`
                continue;
            }

            // break / continue — always statements, never values. The type checker
            // rejects them outside a loop body (OV0312).
            if (Check(TokenKind.KeywordBreak))
            {
                var tok = Advance();
                statements.Add(new BreakStmt(tok.Span));
                Match(TokenKind.Semicolon);
                continue;
            }
            if (Check(TokenKind.KeywordContinue))
            {
                var tok = Advance();
                statements.Add(new ContinueStmt(tok.Span));
                Match(TokenKind.Semicolon);
                continue;
            }

            // `ident = expr` at statement position is rebinding assignment (only valid for
            // `let mut` bindings; the type checker enforces that later). Named-argument
            // syntax `name = expr` never reaches here because it lives inside a call's
            // argument list.
            if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Equals)
            {
                statements.Add(ParseAssignmentStmt());
                Match(TokenKind.Semicolon);
                continue;
            }

            var expr = ParseExpression();

            if (Match(TokenKind.Semicolon))
            {
                statements.Add(new ExpressionStmt(expr, expr.Span));
                continue;
            }

            if (Check(TokenKind.RightBrace))
            {
                trailingExpression = expr;
                break;
            }

            // Bare expression without semicolon before another statement — the expression's
            // value is discarded.
            statements.Add(new ExpressionStmt(expr, expr.Span));
        }

        var closing = Expect(TokenKind.RightBrace, "block");
        return new BlockExpr(
            statements.ToImmutable(),
            trailingExpression,
            new SourceSpan(startPos, closing.Span.End));
    }

    private UseDecl ParseUseDecl()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordUse, "use declaration");

        // Module path: one or more dot-separated identifiers. Selective/alias
        // forms use a following token to mark the end: `.{` starts a selector,
        // bare `.` followed by `{` means selector on the multi-segment path.
        // We disambiguate by peeking: if after the identifier we see `.` then
        // an identifier, treat it as another path segment. If we see `.` then
        // `{`, stop and let the selector parse below.
        var pathBuilder = ImmutableArray.CreateBuilder<string>();
        var firstTok = Expect(TokenKind.Identifier, "use declaration (module path)");
        pathBuilder.Add(firstTok.Lexeme);
        var endPos = firstTok.Span.End;
        while (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance(); // .
            var next = Advance();
            pathBuilder.Add(next.Lexeme);
            endPos = next.Span.End;
        }

        var path = pathBuilder.ToImmutable();

        // After the path, we expect either:
        //   .{ ... }   — selective import
        //   as Ident   — aliased import
        // Anything else is a wildcard, which DESIGN.md §19 forbids.
        if (Match(TokenKind.KeywordAs))
        {
            var aliasTok = Expect(TokenKind.Identifier, "use ... as <alias>");
            Match(TokenKind.Semicolon);
            return new UseDecl(
                path,
                ImmutableArray<string>.Empty,
                aliasTok.Lexeme,
                new SourceSpan(startPos, aliasTok.Span.End));
        }

        if (Match(TokenKind.Dot))
        {
            Expect(TokenKind.LeftBrace, "use selector");
            var symbols = ImmutableArray.CreateBuilder<string>();
            if (!Check(TokenKind.RightBrace))
            {
                var first = Expect(TokenKind.Identifier, "use selector symbol");
                symbols.Add(first.Lexeme);
                while (Match(TokenKind.Comma))
                {
                    if (Check(TokenKind.RightBrace)) break;
                    var sym = Expect(TokenKind.Identifier, "use selector symbol");
                    symbols.Add(sym.Lexeme);
                }
            }
            var closing = Expect(TokenKind.RightBrace, "use selector");
            Match(TokenKind.Semicolon);
            return new UseDecl(
                path,
                symbols.ToImmutable(),
                Alias: null,
                new SourceSpan(startPos, closing.Span.End));
        }

        ReportErrorWithHelp("OV0163",
            "use declaration requires either selective imports or an alias",
            new SourceSpan(startPos, endPos),
            "spell out the symbols you want (`use " + string.Join(".", path) + ".{name1, name2}`), "
                + "or alias the module (`use " + string.Join(".", path) + " as name`). "
                + "Wildcard imports are disallowed (DESIGN.md §19).");
        return new UseDecl(
            path,
            ImmutableArray<string>.Empty,
            Alias: null,
            new SourceSpan(startPos, endPos));
    }

    private LetStmt ParseLetStmt()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordLet, "let binding");
        var isMutable = Match(TokenKind.KeywordMut);

        // Accept any pattern on the LHS. The common case is a single identifier
        // pattern; tuple destructuring (`let (users, orders) = ...`) lands here too.
        var target = ParsePattern();

        if (isMutable && target is not IdentifierPattern)
        {
            ReportError("OV0161",
                "`let mut` requires a single identifier on the left; pattern destructuring is only valid for immutable let",
                target.Span);
        }

        TypeExpr? type = null;
        if (Match(TokenKind.Colon))
        {
            type = ParseTypeExpr();
        }

        Expect(TokenKind.Equals, "let binding initializer");
        var initializer = ParseExpression();
        return new LetStmt(
            target,
            isMutable,
            type,
            initializer,
            new SourceSpan(startPos, initializer.Span.End));
    }

    private AssignmentStmt ParseAssignmentStmt()
    {
        var nameToken = Expect(TokenKind.Identifier, "assignment target");
        Expect(TokenKind.Equals, "assignment");
        var value = ParseExpression();
        return new AssignmentStmt(
            nameToken.Lexeme,
            value,
            new SourceSpan(nameToken.Span.Start, value.Span.End));
    }

    // ---------------------------------------------------------- expressions

    private Expression ParseExpression() => ParsePipe();

    // Grammar per docs/grammar/precedence.md §8. Each method handles its level's operators
    // by parsing a higher-precedence subexpression, then looping on matching operator tokens.

    private Expression ParsePipe()
    {
        var left = ParseLogicalOr();
        while (Check(TokenKind.PipeCompose) || Check(TokenKind.PipePropagate))
        {
            var op = Advance().Kind == TokenKind.PipeCompose
                ? BinaryOp.PipeCompose
                : BinaryOp.PipePropagate;
            var right = ParseLogicalOr();
            left = new BinaryExpr(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Match(TokenKind.PipePipe))
        {
            var right = ParseLogicalAnd();
            left = new BinaryExpr(BinaryOp.LogicalOr, left, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseLogicalAnd()
    {
        var left = ParseEquality();
        while (Match(TokenKind.AmpersandAmpersand))
        {
            var right = ParseEquality();
            left = new BinaryExpr(BinaryOp.LogicalAnd, left, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    // Equality and comparison are NON-associative (precedence.md §4). At most one operator
    // may appear at each level; a second emits OV0102.
    private Expression ParseEquality()
    {
        var left = ParseComparison();
        if (!TryMatchBinaryOp(EqualityOp, out var op))
        {
            return left;
        }
        var right = ParseComparison();
        var result = new BinaryExpr(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
        if (TryMatchBinaryOp(EqualityOp, out _))
        {
            ReportError("OV0102",
                "equality operators are non-associative; use `&&` to combine comparisons",
                Current.Span);
            // Consume the right operand to avoid cascading; ignore the chained result.
            ParseComparison();
        }
        return result;
    }

    private Expression ParseComparison()
    {
        var left = ParseAdditive();
        if (!TryMatchBinaryOp(ComparisonOp, out var op))
        {
            return left;
        }
        var right = ParseAdditive();
        var result = new BinaryExpr(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
        if (TryMatchBinaryOp(ComparisonOp, out _))
        {
            ReportError("OV0102",
                "comparison operators are non-associative; use `&&` to combine comparisons",
                Current.Span);
            ParseAdditive();
        }
        return result;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (TryMatchBinaryOp(AdditiveOp, out var op))
        {
            var right = ParseMultiplicative();
            left = new BinaryExpr(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParseUnaryPrefix();
        while (TryMatchBinaryOp(MultiplicativeOp, out var op))
        {
            var right = ParseUnaryPrefix();
            left = new BinaryExpr(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    // Unary prefix is non-chainable: `!!x` and `--x` are rejected (precedence.md §3).
    private Expression ParseUnaryPrefix()
    {
        if (Check(TokenKind.Minus) || Check(TokenKind.Bang))
        {
            var opToken = Advance();
            var op = opToken.Kind == TokenKind.Minus ? UnaryOp.Negate : UnaryOp.LogicalNot;

            if (Check(TokenKind.Minus) || Check(TokenKind.Bang))
            {
                ReportError("OV0103",
                    "unary prefix operators do not chain; parenthesize explicitly if required",
                    Current.Span);
            }

            var operand = ParsePostfix();
            return new UnaryExpr(op, operand, new SourceSpan(opToken.Span.Start, operand.Span.End));
        }
        return ParsePostfix();
    }

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

            if (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
            {
                Advance(); // .
                var fieldToken = Advance();
                expr = new FieldAccessExpr(
                    expr,
                    fieldToken.Lexeme,
                    new SourceSpan(expr.Span.Start, fieldToken.Span.End));
                continue;
            }

            if (Check(TokenKind.KeywordWith))
            {
                expr = ParseWithTail(expr);
                continue;
            }

            // Record literal on an identifier/path chain (e.g. `Tree.Node { ... }`).
            // The restricted-context flag disables this in `if` / `while` / `match` heads.
            if (_allowRecordLiteral
                && IsIdentifierChain(expr)
                && LooksLikeRecordLiteralHead())
            {
                expr = ParseRecordLiteralTail(expr);
                continue;
            }

            break;
        }

        return expr;
    }

    private static bool IsIdentifierChain(Expression expr) =>
        expr is IdentifierExpr
        || (expr is FieldAccessExpr fa && IsIdentifierChain(fa.Target));

    private WithExpr ParseWithTail(Expression target)
    {
        Expect(TokenKind.KeywordWith, "with expression");
        Expect(TokenKind.LeftBrace, "with expression");

        var updates = ImmutableArray.CreateBuilder<FieldInit>();
        if (!Check(TokenKind.RightBrace))
        {
            updates.Add(ParseFieldInit());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightBrace))
                {
                    break;
                }
                updates.Add(ParseFieldInit());
            }
        }

        var closing = Expect(TokenKind.RightBrace, "with expression");
        return new WithExpr(
            target,
            updates.ToImmutable(),
            new SourceSpan(target.Span.Start, closing.Span.End));
    }

    // Operator-classifier helpers. Each returns the matching BinaryOp if the current
    // token is in the set, otherwise returns false without advancing.
    private delegate bool OpClassifier(TokenKind kind, out BinaryOp op);

    private static readonly OpClassifier EqualityOp = (TokenKind kind, out BinaryOp op) =>
    {
        op = kind switch
        {
            TokenKind.EqualsEquals => BinaryOp.Equal,
            TokenKind.BangEquals => BinaryOp.NotEqual,
            _ => default,
        };
        return kind is TokenKind.EqualsEquals or TokenKind.BangEquals;
    };

    private static readonly OpClassifier ComparisonOp = (TokenKind kind, out BinaryOp op) =>
    {
        op = kind switch
        {
            TokenKind.Less => BinaryOp.Less,
            TokenKind.LessEquals => BinaryOp.LessEqual,
            TokenKind.Greater => BinaryOp.Greater,
            TokenKind.GreaterEquals => BinaryOp.GreaterEqual,
            _ => default,
        };
        return kind is TokenKind.Less or TokenKind.LessEquals
            or TokenKind.Greater or TokenKind.GreaterEquals;
    };

    private static readonly OpClassifier AdditiveOp = (TokenKind kind, out BinaryOp op) =>
    {
        op = kind switch
        {
            TokenKind.Plus => BinaryOp.Add,
            TokenKind.Minus => BinaryOp.Subtract,
            _ => default,
        };
        return kind is TokenKind.Plus or TokenKind.Minus;
    };

    private static readonly OpClassifier MultiplicativeOp = (TokenKind kind, out BinaryOp op) =>
    {
        op = kind switch
        {
            TokenKind.Star => BinaryOp.Multiply,
            TokenKind.Slash => BinaryOp.Divide,
            TokenKind.Percent => BinaryOp.Modulo,
            _ => default,
        };
        return kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent;
    };

    private bool TryMatchBinaryOp(OpClassifier classifier, out BinaryOp op)
    {
        if (classifier(Current.Kind, out op))
        {
            Advance();
            return true;
        }
        return false;
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

            case TokenKind.StringHead:
                return ParseInterpolatedString();

            case TokenKind.IntegerLiteral:
                Advance();
                return new IntegerLiteralExpr(token.Lexeme, token.Span);

            case TokenKind.FloatLiteral:
                Advance();
                return new FloatLiteralExpr(token.Lexeme, token.Span);

            case TokenKind.KeywordTrue:
                Advance();
                return new BooleanLiteralExpr(true, token.Span);

            case TokenKind.KeywordFalse:
                Advance();
                return new BooleanLiteralExpr(false, token.Span);

            case TokenKind.LeftParen:
                return ParseUnitOrParenthesizedExpression();

            case TokenKind.LeftBrace:
                return ParseBlock();

            case TokenKind.KeywordIf:
                return ParseIfExpr();

            case TokenKind.KeywordWhile:
                return ParseWhileExpr();

            case TokenKind.KeywordFor:
                return ParseForEachExpr();

            case TokenKind.KeywordLoop:
                return ParseLoopExpr();

            case TokenKind.KeywordMatch:
                return ParseMatchExpr();

            case TokenKind.KeywordParallel:
                return ParseTaskGroup(parallel: true);

            case TokenKind.KeywordRace:
                return ParseTaskGroup(parallel: false);

            case TokenKind.KeywordUnsafe:
                return ParseUnsafeExpr();

            case TokenKind.KeywordTrace:
                return ParseTraceExpr();

            // TODO: list literals.
        }

        ReportErrorWithHelp("OV0155",
            $"expected expression, got {TokenDisplay(token)}",
            token.Span,
            "expressions begin with an identifier, literal, `(`, `{`, `if`, `match`, `while`, `parallel`, `race`, `trace`, `unsafe`, `-`, or `!`");
        Advance(); // skip offending token so the parser can make progress
        return new UnitExpr(token.Span);
    }

    private InterpolatedStringExpr ParseInterpolatedString()
    {
        var head = Expect(TokenKind.StringHead, "interpolated string");
        var startPos = head.Span.Start;
        var endPos = head.Span.End;

        var parts = ImmutableArray.CreateBuilder<StringPart>();
        parts.Add(new StringLiteralPart(head.Lexeme, head.Span));

        while (true)
        {
            // One interpolation.
            if (Check(TokenKind.Dollar))
            {
                var dollar = Advance();
                var ident = Expect(TokenKind.Identifier, "interpolation identifier");
                var interpSpan = new SourceSpan(dollar.Span.Start, ident.Span.End);
                parts.Add(new StringInterpolationPart(
                    new IdentifierExpr(ident.Lexeme, ident.Span),
                    interpSpan));
            }
            else if (Check(TokenKind.InterpolationStart))
            {
                var start = Advance();

                // Inside ${...} we are effectively a fresh expression context —
                // re-enable record literals regardless of what the outer state is.
                var savedAllowRecord = _allowRecordLiteral;
                _allowRecordLiteral = true;
                Expression inner;
                try
                {
                    inner = ParseExpression();
                }
                finally
                {
                    _allowRecordLiteral = savedAllowRecord;
                }

                var end = Expect(TokenKind.InterpolationEnd, "interpolation");
                parts.Add(new StringInterpolationPart(
                    inner,
                    new SourceSpan(start.Span.Start, end.Span.End)));
            }
            else
            {
                ReportError("OV0156", "expected interpolation in string", Current.Span);
                endPos = Current.Span.Start;
                break;
            }

            // Literal continuation.
            if (Check(TokenKind.StringMiddle))
            {
                var mid = Advance();
                parts.Add(new StringLiteralPart(mid.Lexeme, mid.Span));
                continue;
            }
            if (Check(TokenKind.StringTail))
            {
                var tail = Advance();
                parts.Add(new StringLiteralPart(tail.Lexeme, tail.Span));
                endPos = tail.Span.End;
                break;
            }

            ReportError("OV0156",
                "expected StringMiddle or StringTail after interpolation",
                Current.Span);
            endPos = Current.Span.Start;
            break;
        }

        return new InterpolatedStringExpr(parts.ToImmutable(), new SourceSpan(startPos, endPos));
    }

    private IfExpr ParseIfExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordIf, "if expression");
        var condition = ParseExpressionRestricted();
        var thenBlock = ParseBlock();

        BlockExpr? elseBlock = null;
        if (Match(TokenKind.KeywordElse))
        {
            if (Check(TokenKind.KeywordIf))
            {
                // `else if` — parse a nested if and wrap it in a synthetic block so the
                // AST's Else slot stays a BlockExpr. No runtime cost; the wrap is just a
                // shape adapter.
                var nestedIf = ParseIfExpr();
                elseBlock = new BlockExpr(
                    ImmutableArray<Statement>.Empty,
                    nestedIf,
                    nestedIf.Span);
            }
            else
            {
                elseBlock = ParseBlock();
            }
        }

        var endPos = elseBlock?.Span.End ?? thenBlock.Span.End;
        return new IfExpr(condition, thenBlock, elseBlock, new SourceSpan(startPos, endPos));
    }

    private WhileExpr ParseWhileExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordWhile, "while loop");
        var condition = ParseExpressionRestricted();
        var body = ParseBlock();
        return new WhileExpr(condition, body, new SourceSpan(startPos, body.Span.End));
    }

    private ForEachExpr ParseForEachExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordFor, "for each");
        Expect(TokenKind.KeywordEach, "for each (the `each` keyword is required — Overt has no bare `for` form)");
        var binder = ParsePattern();
        Expect(TokenKind.KeywordIn, "for each");
        var iterable = ParseExpressionRestricted();
        var body = ParseBlock();
        return new ForEachExpr(binder, iterable, body, new SourceSpan(startPos, body.Span.End));
    }

    private LoopExpr ParseLoopExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordLoop, "loop");
        var body = ParseBlock();
        return new LoopExpr(body, new SourceSpan(startPos, body.Span.End));
    }

    private UnsafeExpr ParseUnsafeExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordUnsafe, "unsafe block");
        var body = ParseBlock();
        return new UnsafeExpr(body, new SourceSpan(startPos, body.Span.End));
    }

    private TraceExpr ParseTraceExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordTrace, "trace block");
        var body = ParseBlock();
        return new TraceExpr(body, new SourceSpan(startPos, body.Span.End));
    }

    /// <summary>
    /// Parses <c>parallel { expr, expr, ... }</c> and <c>race { expr, expr, ... }</c>
    /// task groups (DESIGN.md §12). Unlike a block expression, the braces delimit a
    /// comma-separated list of expressions — not statements followed by a trailing
    /// expression. Trailing commas are accepted.
    /// </summary>
    private Expression ParseTaskGroup(bool parallel)
    {
        var startPos = Current.Span.Start;
        Advance(); // parallel or race
        Expect(TokenKind.LeftBrace, parallel ? "parallel block" : "race block");

        var tasks = ImmutableArray.CreateBuilder<Expression>();
        if (!Check(TokenKind.RightBrace))
        {
            tasks.Add(ParseExpression());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightBrace))
                {
                    break;
                }
                tasks.Add(ParseExpression());
            }
        }

        var closing = Expect(TokenKind.RightBrace, parallel ? "parallel block" : "race block");
        var span = new SourceSpan(startPos, closing.Span.End);
        return parallel
            ? new ParallelExpr(tasks.ToImmutable(), span)
            : new RaceExpr(tasks.ToImmutable(), span);
    }

    // ------------------------------------------------------------- match

    private MatchExpr ParseMatchExpr()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.KeywordMatch, "match expression");
        var scrutinee = ParseExpressionRestricted();
        Expect(TokenKind.LeftBrace, "match body");

        var arms = ImmutableArray.CreateBuilder<MatchArm>();
        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
        {
            var arm = ParseMatchArm();
            arms.Add(arm);

            if (Match(TokenKind.Comma))
            {
                continue;
            }

            // No comma. Allowed iff the arm body ended with `}` — i.e. the last consumed
            // token was a right brace (block, if-else, match, with, record literal, etc).
            if (Check(TokenKind.RightBrace))
            {
                break;
            }
            if (_cursor > 0 && _tokens[_cursor - 1].Kind == TokenKind.RightBrace)
            {
                continue;
            }

            ReportError("OV0162", "expected `,` between match arms", Current.Span);
            break;
        }

        var closing = Expect(TokenKind.RightBrace, "match body");
        return new MatchExpr(
            scrutinee,
            arms.ToImmutable(),
            new SourceSpan(startPos, closing.Span.End));
    }

    private MatchArm ParseMatchArm()
    {
        var pattern = ParsePattern();
        Expect(TokenKind.FatArrow, "match arm");
        var body = ParseExpression();
        return new MatchArm(pattern, body, new SourceSpan(pattern.Span.Start, body.Span.End));
    }

    // ----------------------------------------------------------- patterns

    private Pattern ParsePattern()
    {
        if (Check(TokenKind.Identifier) && Current.Lexeme == "_")
        {
            var tok = Advance();
            return new WildcardPattern(tok.Span);
        }

        if (Check(TokenKind.LeftParen))
        {
            return ParseParenOrTuplePattern();
        }

        if (Check(TokenKind.Identifier))
        {
            return ParseIdentifierOrConstructorPattern();
        }

        // Literal patterns: integer (possibly negated), float, string, bool.
        // These are equality-matches; they never contribute to exhaustiveness
        // over an infinite domain, so a match using them still needs `_`.
        if (Check(TokenKind.IntegerLiteral) || Check(TokenKind.FloatLiteral)
            || Check(TokenKind.StringLiteral)
            || Check(TokenKind.KeywordTrue) || Check(TokenKind.KeywordFalse))
        {
            var start = Current.Span.Start;
            var literalTok = Advance();
            Expression literal = literalTok.Kind switch
            {
                TokenKind.IntegerLiteral => new IntegerLiteralExpr(literalTok.Lexeme, literalTok.Span),
                TokenKind.FloatLiteral => new FloatLiteralExpr(literalTok.Lexeme, literalTok.Span),
                TokenKind.StringLiteral => new StringLiteralExpr(literalTok.Lexeme, literalTok.Span),
                TokenKind.KeywordTrue => new BooleanLiteralExpr(true, literalTok.Span),
                TokenKind.KeywordFalse => new BooleanLiteralExpr(false, literalTok.Span),
                _ => throw new InvalidOperationException("unreachable"),
            };
            return new LiteralPattern(literal, new SourceSpan(start, literalTok.Span.End));
        }
        if (Check(TokenKind.Minus) && Peek(1).Kind == TokenKind.IntegerLiteral)
        {
            var start = Current.Span.Start;
            Advance(); // -
            var intTok = Advance();
            var inner = new IntegerLiteralExpr(intTok.Lexeme, intTok.Span);
            var span = new SourceSpan(start, intTok.Span.End);
            return new LiteralPattern(new UnaryExpr(UnaryOp.Negate, inner, span), span);
        }

        ReportErrorWithHelp("OV0158",
            $"expected pattern, got {TokenDisplay(Current)}",
            Current.Span,
            "patterns are `_`, an identifier, a dotted path, a constructor call `Name(pat, ...)`, a record destructure `Name { field = pat, ... }`, a tuple `(pat, ...)`, or a literal (`0`, `true`, `\"exit\"`)");
        var skipped = Advance();
        return new WildcardPattern(skipped.Span);
    }

    private Pattern ParseParenOrTuplePattern()
    {
        var startPos = Current.Span.Start;
        Expect(TokenKind.LeftParen, "pattern");

        if (Check(TokenKind.RightParen))
        {
            // Unit pattern not supported yet; match the unit value via a different idiom.
            var closing = Advance();
            ReportError("OV0159", "unit pattern `()` is not yet supported", new SourceSpan(startPos, closing.Span.End));
            return new WildcardPattern(new SourceSpan(startPos, closing.Span.End));
        }

        var first = ParsePattern();
        if (!Match(TokenKind.Comma))
        {
            var close = Expect(TokenKind.RightParen, "pattern");
            return first with { Span = new SourceSpan(startPos, close.Span.End) };
        }

        var elements = ImmutableArray.CreateBuilder<Pattern>();
        elements.Add(first);
        if (!Check(TokenKind.RightParen))
        {
            elements.Add(ParsePattern());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightParen))
                {
                    break;
                }
                elements.Add(ParsePattern());
            }
        }
        var closeTup = Expect(TokenKind.RightParen, "tuple pattern");
        return new TuplePattern(
            elements.ToImmutable(),
            new SourceSpan(startPos, closeTup.Span.End));
    }

    private Pattern ParseIdentifierOrConstructorPattern()
    {
        var firstToken = Advance();
        var startPos = firstToken.Span.Start;
        var endPos = firstToken.Span.End;
        var pathBuilder = ImmutableArray.CreateBuilder<string>();
        pathBuilder.Add(firstToken.Lexeme);

        while (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance(); // .
            var next = Advance();
            pathBuilder.Add(next.Lexeme);
            endPos = next.Span.End;
        }

        if (Check(TokenKind.LeftParen))
        {
            Advance();
            var args = ImmutableArray.CreateBuilder<Pattern>();
            if (!Check(TokenKind.RightParen))
            {
                args.Add(ParsePattern());
                while (Match(TokenKind.Comma))
                {
                    if (Check(TokenKind.RightParen))
                    {
                        break;
                    }
                    args.Add(ParsePattern());
                }
            }
            var closeParen = Expect(TokenKind.RightParen, "constructor pattern arguments");
            return new ConstructorPattern(
                pathBuilder.ToImmutable(),
                args.ToImmutable(),
                new SourceSpan(startPos, closeParen.Span.End));
        }

        if (Check(TokenKind.LeftBrace))
        {
            Advance();
            var fields = ImmutableArray.CreateBuilder<FieldPattern>();
            if (!Check(TokenKind.RightBrace))
            {
                fields.Add(ParseFieldPattern());
                while (Match(TokenKind.Comma))
                {
                    if (Check(TokenKind.RightBrace))
                    {
                        break;
                    }
                    fields.Add(ParseFieldPattern());
                }
            }
            var closeBrace = Expect(TokenKind.RightBrace, "record pattern");
            return new RecordPattern(
                pathBuilder.ToImmutable(),
                fields.ToImmutable(),
                new SourceSpan(startPos, closeBrace.Span.End));
        }

        var path = pathBuilder.ToImmutable();
        if (path.Length == 1)
        {
            return new IdentifierPattern(path[0], new SourceSpan(startPos, endPos));
        }
        return new PathPattern(path, new SourceSpan(startPos, endPos));
    }

    private FieldPattern ParseFieldPattern()
    {
        var nameToken = Expect(TokenKind.Identifier, "field pattern name");
        Expect(TokenKind.Equals, "field pattern");
        var sub = ParsePattern();
        return new FieldPattern(
            nameToken.Lexeme,
            sub,
            new SourceSpan(nameToken.Span.Start, sub.Span.End));
    }

    /// <summary>
    /// Returns true if the current state is <c>{ Ident = </c> or <c>{ }</c> — the two token
    /// shapes that unambiguously indicate a record literal body following a preceding
    /// identifier. All other uses of <c>{</c> after an identifier leave the brace to the
    /// caller (if-expression body, trailing block, etc).
    /// </summary>
    private bool LooksLikeRecordLiteralHead()
    {
        if (!Check(TokenKind.LeftBrace))
        {
            return false;
        }
        var after = Peek(1).Kind;
        if (after == TokenKind.RightBrace)
        {
            return true;
        }
        return after == TokenKind.Identifier && Peek(2).Kind == TokenKind.Equals;
    }

    private RecordLiteralExpr ParseRecordLiteralTail(Expression typeTarget)
    {
        Expect(TokenKind.LeftBrace, "record literal");

        var fields = ImmutableArray.CreateBuilder<FieldInit>();
        if (!Check(TokenKind.RightBrace))
        {
            fields.Add(ParseFieldInit());
            while (Match(TokenKind.Comma))
            {
                if (Check(TokenKind.RightBrace))
                {
                    break;
                }
                fields.Add(ParseFieldInit());
            }
        }

        var closing = Expect(TokenKind.RightBrace, "record literal");
        return new RecordLiteralExpr(
            typeTarget,
            fields.ToImmutable(),
            new SourceSpan(typeTarget.Span.Start, closing.Span.End));
    }

    private FieldInit ParseFieldInit()
    {
        var nameToken = Expect(TokenKind.Identifier, "field initializer name");
        Expect(TokenKind.Equals, "field initializer");
        var value = ParseExpression();
        return new FieldInit(
            nameToken.Lexeme,
            value,
            new SourceSpan(nameToken.Span.Start, value.Span.End));
    }

    /// <summary>
    /// Parse an expression with record-literal primaries disabled. Used in the condition
    /// position of <c>if</c>, <c>while</c>, and <c>match</c> so the following <c>{</c>
    /// can open the construct's body instead of being mis-parsed as a record literal.
    /// </summary>
    private Expression ParseExpressionRestricted()
    {
        var saved = _allowRecordLiteral;
        _allowRecordLiteral = false;
        try
        {
            return ParseExpression();
        }
        finally
        {
            _allowRecordLiteral = saved;
        }
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

        // Parens unambiguate by themselves, so re-enable record-literal parsing inside
        // regardless of the enclosing restricted context.
        var saved = _allowRecordLiteral;
        _allowRecordLiteral = true;
        try
        {
            var first = ParseExpression();

            // Parenthesized single expression.
            if (!Match(TokenKind.Comma))
            {
                var close = Expect(TokenKind.RightParen, "expression");
                return first with { Span = new SourceSpan(startPos, close.Span.End) };
            }

            // Tuple expression: two or more elements. Trailing comma allowed.
            var elements = ImmutableArray.CreateBuilder<Expression>();
            elements.Add(first);
            if (!Check(TokenKind.RightParen))
            {
                elements.Add(ParseExpression());
                while (Match(TokenKind.Comma))
                {
                    if (Check(TokenKind.RightParen))
                    {
                        break;
                    }
                    elements.Add(ParseExpression());
                }
            }
            var tupleClose = Expect(TokenKind.RightParen, "tuple expression");
            return new TupleExpr(
                elements.ToImmutable(),
                new SourceSpan(startPos, tupleClose.Span.End));
        }
        finally
        {
            _allowRecordLiteral = saved;
        }
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

    private void ReportErrorWithHelp(string code, string message, SourceSpan span, string help)
    {
        _diagnostics.Add(
            new Diagnostic(DiagnosticSeverity.Error, code, message, span).WithHelp(help));
    }

    private static string TokenDisplay(Token token) => token.Kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.Identifier => $"identifier `{token.Lexeme}`",
        TokenKind.StringLiteral or TokenKind.StringHead => "string literal",
        TokenKind.IntegerLiteral => $"integer `{token.Lexeme}`",
        TokenKind.FloatLiteral => $"float `{token.Lexeme}`",
        _ => $"`{token.Lexeme}`",
    };
}

public sealed record ParseResult(
    ModuleDecl Module,
    ImmutableArray<Diagnostic> Diagnostics);
