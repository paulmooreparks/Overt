using System.Collections.Immutable;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class ParserTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static ParseResult ParseFile(string exampleFile)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, exampleFile));
        var lex = Lexer.Lex(source);
        Assert.Empty(lex.Diagnostics);
        return Parser.Parse(lex.Tokens);
    }

    [Fact]
    public void Parse_HelloOv_ProducesExpectedModuleShape()
    {
        var result = ParseFile("hello.ov");

        Assert.Empty(result.Diagnostics);

        var module = result.Module;
        Assert.Equal("hello", module.Name);
        Assert.Single(module.Declarations);

        var fn = Assert.IsType<FunctionDecl>(module.Declarations[0]);
        Assert.Equal("main", fn.Name);
        Assert.Empty(fn.Parameters);

        Assert.NotNull(fn.Effects);
        Assert.Equal(new[] { "io" }, fn.Effects!.Effects.ToArray());

        var returnType = Assert.IsType<NamedType>(fn.ReturnType);
        Assert.Equal("Result", returnType.Name);
        Assert.Equal(2, returnType.TypeArguments.Length);
        Assert.IsType<UnitType>(returnType.TypeArguments[0]);
        var errorType = Assert.IsType<NamedType>(returnType.TypeArguments[1]);
        Assert.Equal("IoError", errorType.Name);
        Assert.Empty(errorType.TypeArguments);

        // Body: { println("Hello, LLM!")?  Ok(()) }
        var body = fn.Body;
        Assert.Single(body.Statements);
        Assert.NotNull(body.TrailingExpression);

        var stmt = Assert.IsType<ExpressionStmt>(body.Statements[0]);
        var propagate = Assert.IsType<PropagateExpr>(stmt.Expression);
        var call = Assert.IsType<CallExpr>(propagate.Operand);
        var callee = Assert.IsType<IdentifierExpr>(call.Callee);
        Assert.Equal("println", callee.Name);
        Assert.Single(call.Arguments);
        Assert.Null(call.Arguments[0].Name);
        var strArg = Assert.IsType<StringLiteralExpr>(call.Arguments[0].Value);
        Assert.Equal("\"Hello, LLM!\"", strArg.Value);

        var trailing = Assert.IsType<CallExpr>(body.TrailingExpression);
        var okCallee = Assert.IsType<IdentifierExpr>(trailing.Callee);
        Assert.Equal("Ok", okCallee.Name);
        Assert.Single(trailing.Arguments);
        Assert.IsType<UnitExpr>(trailing.Arguments[0].Value);
    }

    [Fact]
    public void Parse_EmptyEffectRowIsAbsent_NotPresent()
    {
        // A function with no effects has no `!{...}`; the AST's Effects field should be null.
        var lex = Lexer.Lex("module m\nfn pure() -> Int { 42 }");
        var result = Parser.Parse(lex.Tokens);
        // 42 is not yet a supported primary; we expect a diagnostic but still AST recovery.
        // This test asserts only that the Effects field is null regardless.
        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
        Assert.Null(fn.Effects);
    }

    [Fact]
    public void Parse_MultipleEffects_ParsesAllInOrder()
    {
        var lex = Lexer.Lex("module m\nfn f() !{io, async, inference} { }");
        var result = Parser.Parse(lex.Tokens);

        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
        Assert.NotNull(fn.Effects);
        Assert.Equal(
            new[] { "io", "async", "inference" },
            fn.Effects!.Effects.ToArray());
    }

    [Fact]
    public void Parse_GenericReturnType_NestsTypeArguments()
    {
        var lex = Lexer.Lex("module m\nfn f() -> Result<Option<Int>, Error> { }");
        var result = Parser.Parse(lex.Tokens);

        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
        var outer = Assert.IsType<NamedType>(fn.ReturnType);
        Assert.Equal("Result", outer.Name);
        Assert.Equal(2, outer.TypeArguments.Length);

        var option = Assert.IsType<NamedType>(outer.TypeArguments[0]);
        Assert.Equal("Option", option.Name);
        Assert.Single(option.TypeArguments);
        var innerInt = Assert.IsType<NamedType>(option.TypeArguments[0]);
        Assert.Equal("Int", innerInt.Name);

        var error = Assert.IsType<NamedType>(outer.TypeArguments[1]);
        Assert.Equal("Error", error.Name);
    }

    [Fact]
    public void Parse_NamedArgumentCall_RecognizesName()
    {
        var lex = Lexer.Lex(
            "module m\nfn main() { fetch(url = \"x\", timeout = \"y\") }");
        var result = Parser.Parse(lex.Tokens);

        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
        var call = Assert.IsType<CallExpr>(fn.Body.TrailingExpression);
        Assert.Equal(2, call.Arguments.Length);
        Assert.Equal("url", call.Arguments[0].Name);
        Assert.Equal("timeout", call.Arguments[1].Name);
    }

    [Fact]
    public void Parse_MultiArgCallWithPositional_EmitsOV0154()
    {
        var lex = Lexer.Lex("module m\nfn main() { fetch(\"x\", \"y\") }");
        var result = Parser.Parse(lex.Tokens);

        Assert.Contains(result.Diagnostics, d => d.Code == "OV0154");
    }

    [Fact]
    public void Parse_ParameterList_ParsesAnnotatedNames()
    {
        var lex = Lexer.Lex("module m\nfn f(url: String, retries: Int) { }");
        var result = Parser.Parse(lex.Tokens);

        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
        Assert.Equal(2, fn.Parameters.Length);
        Assert.Equal("url", fn.Parameters[0].Name);
        Assert.Equal("retries", fn.Parameters[1].Name);

        var urlType = Assert.IsType<NamedType>(fn.Parameters[0].Type);
        Assert.Equal("String", urlType.Name);
    }

    [Fact]
    public void Parse_TrailingCommaInEffectRow_IsAccepted()
    {
        var lex = Lexer.Lex("module m\nfn f() !{io, async,} { }");
        var result = Parser.Parse(lex.Tokens);

        // No diagnostics about the trailing comma.
        Assert.DoesNotContain(result.Diagnostics, d => d.Code.StartsWith("OV015"));
    }

    // ------------------------------------------------------------- literals

    private static Expression ParseBodyExpression(string source)
    {
        var lex = Lexer.Lex($"module m\nfn f() {{ {source} }}");
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var fn = (FunctionDecl)parse.Module.Declarations[0];
        var expr = fn.Body.TrailingExpression;
        Assert.NotNull(expr);
        return expr!;
    }

    [Fact]
    public void Parse_IntegerLiteral()
    {
        var expr = ParseBodyExpression("42");
        var lit = Assert.IsType<IntegerLiteralExpr>(expr);
        Assert.Equal("42", lit.Lexeme);
    }

    [Fact]
    public void Parse_FloatLiteral()
    {
        var expr = ParseBodyExpression("3.14");
        var lit = Assert.IsType<FloatLiteralExpr>(expr);
        Assert.Equal("3.14", lit.Lexeme);
    }

    [Fact]
    public void Parse_BooleanLiterals()
    {
        var t = Assert.IsType<BooleanLiteralExpr>(ParseBodyExpression("true"));
        var f = Assert.IsType<BooleanLiteralExpr>(ParseBodyExpression("false"));
        Assert.True(t.Value);
        Assert.False(f.Value);
    }

    [Fact]
    public void Parse_UnderscoreSeparatedInteger_PreservesLexeme()
    {
        var expr = ParseBodyExpression("1_000_000");
        Assert.Equal("1_000_000", Assert.IsType<IntegerLiteralExpr>(expr).Lexeme);
    }

    // ------------------------------------------------------ binary operators

    [Fact]
    public void Parse_AdditionAndMultiplication_RespectPrecedence()
    {
        // a + b * c  =>  a + (b * c)
        var expr = ParseBodyExpression("a + b * c");
        var add = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.IsType<IdentifierExpr>(add.Left);
        var mul = Assert.IsType<BinaryExpr>(add.Right);
        Assert.Equal(BinaryOp.Multiply, mul.Op);
    }

    [Fact]
    public void Parse_AdditionIsLeftAssociative()
    {
        // a + b + c  =>  (a + b) + c
        var expr = ParseBodyExpression("a + b + c");
        var outer = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.Add, outer.Op);
        var inner = Assert.IsType<BinaryExpr>(outer.Left);
        Assert.Equal(BinaryOp.Add, inner.Op);
        Assert.IsType<IdentifierExpr>(inner.Left);
        Assert.IsType<IdentifierExpr>(inner.Right);
        Assert.IsType<IdentifierExpr>(outer.Right);
    }

    [Fact]
    public void Parse_LogicalAndOrComparison_PrecedenceOrder()
    {
        // a < b && c > d  =>  (a < b) && (c > d)
        var expr = ParseBodyExpression("a < b && c > d");
        var and = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.LogicalAnd, and.Op);
        Assert.Equal(BinaryOp.Less, Assert.IsType<BinaryExpr>(and.Left).Op);
        Assert.Equal(BinaryOp.Greater, Assert.IsType<BinaryExpr>(and.Right).Op);
    }

    [Fact]
    public void Parse_PipeIsLooserThanArithmetic()
    {
        // a + b |> f  =>  (a + b) |> f
        var expr = ParseBodyExpression("a + b |> f");
        var pipe = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.PipeCompose, pipe.Op);
        Assert.Equal(BinaryOp.Add, Assert.IsType<BinaryExpr>(pipe.Left).Op);
        Assert.IsType<IdentifierExpr>(pipe.Right);
    }

    [Fact]
    public void Parse_PipeChain_LeftAssociative()
    {
        // x |> f |> g  =>  (x |> f) |> g
        var expr = ParseBodyExpression("x |> f |> g");
        var outer = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.PipeCompose, outer.Op);
        var inner = Assert.IsType<BinaryExpr>(outer.Left);
        Assert.Equal(BinaryOp.PipeCompose, inner.Op);
    }

    [Fact]
    public void Parse_PipeMixedComposeAndPropagate()
    {
        var expr = ParseBodyExpression("x |>? f |> g");
        var outer = Assert.IsType<BinaryExpr>(expr);
        Assert.Equal(BinaryOp.PipeCompose, outer.Op);
        var inner = Assert.IsType<BinaryExpr>(outer.Left);
        Assert.Equal(BinaryOp.PipePropagate, inner.Op);
    }

    [Fact]
    public void Parse_ChainedComparison_EmitsOV0102()
    {
        var lex = Lexer.Lex("module m\nfn f() { a < b < c }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0102");
    }

    [Fact]
    public void Parse_ChainedEquality_EmitsOV0102()
    {
        var lex = Lexer.Lex("module m\nfn f() { a == b == c }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0102");
    }

    // ---------------------------------------------------------- unary prefix

    [Fact]
    public void Parse_UnaryNegation()
    {
        var expr = ParseBodyExpression("-42");
        var neg = Assert.IsType<UnaryExpr>(expr);
        Assert.Equal(UnaryOp.Negate, neg.Op);
        Assert.IsType<IntegerLiteralExpr>(neg.Operand);
    }

    [Fact]
    public void Parse_LogicalNot()
    {
        var expr = ParseBodyExpression("!flag");
        var not = Assert.IsType<UnaryExpr>(expr);
        Assert.Equal(UnaryOp.LogicalNot, not.Op);
    }

    [Fact]
    public void Parse_DoubleUnary_EmitsOV0103()
    {
        var lex = Lexer.Lex("module m\nfn f() { !!x }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0103");
    }

    [Fact]
    public void Parse_UnaryBindsLooserThanPostfix()
    {
        // -x?  =>  -(x?)   postfix binds tighter than unary prefix
        var expr = ParseBodyExpression("-x?");
        var neg = Assert.IsType<UnaryExpr>(expr);
        Assert.IsType<PropagateExpr>(neg.Operand);
    }

    // -------------------------------------------------------- field access

    [Fact]
    public void Parse_FieldAccess_Simple()
    {
        var expr = ParseBodyExpression("user.name");
        var fa = Assert.IsType<FieldAccessExpr>(expr);
        Assert.Equal("name", fa.FieldName);
        Assert.Equal("user", Assert.IsType<IdentifierExpr>(fa.Target).Name);
    }

    [Fact]
    public void Parse_FieldAccess_ChainsLeft()
    {
        var expr = ParseBodyExpression("a.b.c");
        var outer = Assert.IsType<FieldAccessExpr>(expr);
        Assert.Equal("c", outer.FieldName);
        var inner = Assert.IsType<FieldAccessExpr>(outer.Target);
        Assert.Equal("b", inner.FieldName);
    }

    [Fact]
    public void Parse_FieldAccessOnCallResult()
    {
        var expr = ParseBodyExpression("fetch().name");
        var fa = Assert.IsType<FieldAccessExpr>(expr);
        Assert.IsType<CallExpr>(fa.Target);
    }

    // ---------------------------------------------------------- let / assign

    [Fact]
    public void Parse_LetBinding_Simple()
    {
        var lex = Lexer.Lex("module m\nfn f() { let x = 42 }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        Assert.Null(fn.Body.TrailingExpression);
        var let = Assert.IsType<LetStmt>(fn.Body.Statements[0]);
        Assert.Equal("x", ((IdentifierPattern)let.Target).Name);
        Assert.False(let.IsMutable);
        Assert.Null(let.Type);
        Assert.IsType<IntegerLiteralExpr>(let.Initializer);
    }

    [Fact]
    public void Parse_LetMutBinding_FlagsMutable()
    {
        var lex = Lexer.Lex("module m\nfn f() { let mut counter = 0 }");
        var result = Parser.Parse(lex.Tokens);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var let = Assert.IsType<LetStmt>(fn.Body.Statements[0]);
        Assert.True(let.IsMutable);
        Assert.Equal("counter", ((IdentifierPattern)let.Target).Name);
    }

    [Fact]
    public void Parse_LetWithTypeAnnotation()
    {
        var lex = Lexer.Lex("module m\nfn f() { let x: Int = 42 }");
        var result = Parser.Parse(lex.Tokens);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var let = Assert.IsType<LetStmt>(fn.Body.Statements[0]);
        var ty = Assert.IsType<NamedType>(let.Type);
        Assert.Equal("Int", ty.Name);
    }

    [Fact]
    public void Parse_AssignmentStatement()
    {
        var lex = Lexer.Lex("module m\nfn f() { let mut x = 0\n x = x + 1 }");
        var result = Parser.Parse(lex.Tokens);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        Assert.Equal(2, fn.Body.Statements.Length);
        var asn = Assert.IsType<AssignmentStmt>(fn.Body.Statements[1]);
        Assert.Equal("x", asn.Name);
        var add = Assert.IsType<BinaryExpr>(asn.Value);
        Assert.Equal(BinaryOp.Add, add.Op);
    }

    // ------------------------------------------------------------------ if

    [Fact]
    public void Parse_IfElse_BothArmsPresent()
    {
        var lex = Lexer.Lex("module m\nfn f() { if cond { 1 } else { 2 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var ife = Assert.IsType<IfExpr>(fn.Body.TrailingExpression);
        Assert.IsType<IdentifierExpr>(ife.Condition);
        Assert.Equal("1", ((IntegerLiteralExpr)ife.Then.TrailingExpression!).Lexeme);
        Assert.NotNull(ife.Else);
        Assert.Equal("2", ((IntegerLiteralExpr)ife.Else!.TrailingExpression!).Lexeme);
    }

    [Fact]
    public void Parse_IfWithoutElse_IsAccepted_WithNullElseArm()
    {
        // DESIGN.md §4: `if cond { body }` is sugar for `if cond { body } else { () }`.
        // The type checker enforces that the then block's type is `()` when else is absent.
        var lex = Lexer.Lex("module m\nfn f() { if cond { body } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var ife = Assert.IsType<IfExpr>(fn.Body.TrailingExpression);
        Assert.Null(ife.Else);
    }

    // -------------------------------------------------------------- records

    [Fact]
    public void Parse_RecordDecl_SingleField()
    {
        var lex = Lexer.Lex("module m\nrecord UserId { value: String }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var rec = Assert.IsType<RecordDecl>(result.Module.Declarations[0]);
        Assert.Equal("UserId", rec.Name);
        Assert.Single(rec.Fields);
        Assert.Equal("value", rec.Fields[0].Name);
        Assert.Equal("String", ((NamedType)rec.Fields[0].Type).Name);
    }

    [Fact]
    public void Parse_RecordDecl_MultipleFields_TrailingCommaAllowed()
    {
        var lex = Lexer.Lex(
            "module m\nrecord User { id: Int, name: String, is_active: Bool, }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var rec = Assert.IsType<RecordDecl>(result.Module.Declarations[0]);
        Assert.Equal(new[] { "id", "name", "is_active" }, rec.Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void Parse_RecordDecl_EmptyBody()
    {
        var lex = Lexer.Lex("module m\nrecord Empty { }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var rec = Assert.IsType<RecordDecl>(result.Module.Declarations[0]);
        Assert.Empty(rec.Fields);
    }

    [Fact]
    public void Parse_RecordLiteral_InExpressionPosition()
    {
        var expr = ParseBodyExpression("Point { x = 1, y = 2 }");
        var lit = Assert.IsType<RecordLiteralExpr>(expr);
        Assert.Equal("Point", ((IdentifierExpr)lit.TypeTarget).Name);
        Assert.Equal(2, lit.Fields.Length);
        Assert.Equal("x", lit.Fields[0].Name);
        Assert.Equal("y", lit.Fields[1].Name);
        Assert.Equal("1", ((IntegerLiteralExpr)lit.Fields[0].Value).Lexeme);
    }

    [Fact]
    public void Parse_RecordLiteral_EmptyFields()
    {
        var expr = ParseBodyExpression("Marker { }");
        var lit = Assert.IsType<RecordLiteralExpr>(expr);
        Assert.Equal("Marker", ((IdentifierExpr)lit.TypeTarget).Name);
        Assert.Empty(lit.Fields);
    }

    [Fact]
    public void Parse_RecordLiteral_NestedValue()
    {
        var expr = ParseBodyExpression("User { id = UserId { value = \"x\" }, active = true }");
        var outer = Assert.IsType<RecordLiteralExpr>(expr);
        Assert.Equal("User", ((IdentifierExpr)outer.TypeTarget).Name);
        var idInit = outer.Fields[0];
        Assert.Equal("id", idInit.Name);
        var inner = Assert.IsType<RecordLiteralExpr>(idInit.Value);
        Assert.Equal("UserId", ((IdentifierExpr)inner.TypeTarget).Name);
    }

    [Fact]
    public void Parse_RecordLiteral_DottedPath()
    {
        // Tree.Node { ... } is parsed as a record literal whose type-target is a
        // FieldAccessExpr chain of identifiers. Required for enum variant records.
        var expr = ParseBodyExpression("Tree.Node { value = 1 }");
        var lit = Assert.IsType<RecordLiteralExpr>(expr);
        var fa = Assert.IsType<FieldAccessExpr>(lit.TypeTarget);
        Assert.Equal("Node", fa.FieldName);
        Assert.Equal("Tree", ((IdentifierExpr)fa.Target).Name);
    }

    [Fact]
    public void Parse_IfCondition_DoesNotConsumeFollowingBraceAsRecordLiteral()
    {
        // Without the restricted-context machinery, the parser would greedily consume
        // `flag { ... }` as a record literal and leave the if with no body.
        var lex = Lexer.Lex("module m\nfn f() { if flag { body_expr } else { other } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var ife = Assert.IsType<IfExpr>(fn.Body.TrailingExpression);
        Assert.Equal("flag", ((IdentifierExpr)ife.Condition).Name);
    }

    [Fact]
    public void Parse_RecordLiteralInsideIfCondition_RequiresParens()
    {
        // Parenthesizing re-enables record-literal parsing inside the restricted context.
        var lex = Lexer.Lex(
            "module m\nfn f() { if (Point { x = 1 }) == p { a } else { b } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var ife = Assert.IsType<IfExpr>(fn.Body.TrailingExpression);
        var eq = Assert.IsType<BinaryExpr>(ife.Condition);
        Assert.Equal(BinaryOp.Equal, eq.Op);
        Assert.IsType<RecordLiteralExpr>(eq.Left);
    }

    [Fact]
    public void Parse_RecordLiteral_FieldAccessAfter()
    {
        var expr = ParseBodyExpression("Point { x = 1, y = 2 }.x");
        var fa = Assert.IsType<FieldAccessExpr>(expr);
        Assert.Equal("x", fa.FieldName);
        Assert.IsType<RecordLiteralExpr>(fa.Target);
    }

    // ----------------------------------------------- interpolated strings

    [Fact]
    public void Parse_InterpolatedString_DollarIdent()
    {
        var expr = ParseBodyExpression("\"Hello, $name!\"");
        var isx = Assert.IsType<InterpolatedStringExpr>(expr);
        Assert.Equal(3, isx.Parts.Length);
        var head = Assert.IsType<StringLiteralPart>(isx.Parts[0]);
        var interp = Assert.IsType<StringInterpolationPart>(isx.Parts[1]);
        var tail = Assert.IsType<StringLiteralPart>(isx.Parts[2]);
        Assert.Equal("\"Hello, ", head.Text);
        Assert.Equal("name", Assert.IsType<IdentifierExpr>(interp.Expression).Name);
        Assert.Equal("!\"", tail.Text);
    }

    [Fact]
    public void Parse_InterpolatedString_BracedExpression()
    {
        var expr = ParseBodyExpression("\"${price * quantity}\"");
        var isx = Assert.IsType<InterpolatedStringExpr>(expr);
        Assert.Equal(3, isx.Parts.Length);
        var interp = Assert.IsType<StringInterpolationPart>(isx.Parts[1]);
        var mul = Assert.IsType<BinaryExpr>(interp.Expression);
        Assert.Equal(BinaryOp.Multiply, mul.Op);
    }

    [Fact]
    public void Parse_InterpolatedString_Multiple()
    {
        var expr = ParseBodyExpression("\"a${x}b${y}c\"");
        var isx = Assert.IsType<InterpolatedStringExpr>(expr);
        Assert.Equal(5, isx.Parts.Length);
        Assert.IsType<StringLiteralPart>(isx.Parts[0]);
        Assert.IsType<StringInterpolationPart>(isx.Parts[1]);
        Assert.IsType<StringLiteralPart>(isx.Parts[2]);
        Assert.IsType<StringInterpolationPart>(isx.Parts[3]);
        Assert.IsType<StringLiteralPart>(isx.Parts[4]);
    }

    [Fact]
    public void Parse_InterpolatedString_NestedStringInsideInterpolation()
    {
        var expr = ParseBodyExpression("\"outer ${format(\"inner $who\")}\"");
        var outer = Assert.IsType<InterpolatedStringExpr>(expr);
        var interp = Assert.IsType<StringInterpolationPart>(outer.Parts[1]);
        var call = Assert.IsType<CallExpr>(interp.Expression);
        Assert.IsType<InterpolatedStringExpr>(call.Arguments[0].Value);
    }

    [Fact]
    public void Parse_InterpolatedString_InsideIfCondition_Works()
    {
        // Interpolated strings start with `"`, not an identifier, so the restricted
        // context that disables record literals does not affect them.
        var lex = Lexer.Lex(
            "module m\nfn f() !{io} { if \"$x\" == \"$y\" { a } else { b } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);
    }

    // ----------------------------------------------------- with expression

    [Fact]
    public void Parse_WithExpr_SingleFieldUpdate()
    {
        var expr = ParseBodyExpression("config with { enable_a = true }");
        var with = Assert.IsType<WithExpr>(expr);
        Assert.Equal("config", ((IdentifierExpr)with.Target).Name);
        Assert.Single(with.Updates);
        Assert.Equal("enable_a", with.Updates[0].Name);
        Assert.True(((BooleanLiteralExpr)with.Updates[0].Value).Value);
    }

    [Fact]
    public void Parse_WithExpr_ChainsOnFieldAccess()
    {
        // user.address with { city = "X" } groups as (user.address) with { city = "X" }.
        var expr = ParseBodyExpression("user.address with { city = \"X\" }");
        var with = Assert.IsType<WithExpr>(expr);
        Assert.IsType<FieldAccessExpr>(with.Target);
    }

    [Fact]
    public void Parse_WithExpr_NestedWithInValue()
    {
        var expr = ParseBodyExpression(
            "user with { address = user.address with { city = \"London\" } }");
        var outer = Assert.IsType<WithExpr>(expr);
        Assert.Equal("user", ((IdentifierExpr)outer.Target).Name);
        var inner = Assert.IsType<WithExpr>(outer.Updates[0].Value);
        Assert.IsType<FieldAccessExpr>(inner.Target);
    }

    // --------------------------------------------------------------- while

    [Fact]
    public void Parse_While_SimpleBody()
    {
        // The while expression lands on the block's trailing-expression slot (it has no
        // following expression to demote it to a statement). That's fine — the type
        // checker will require while's value `()` to match the enclosing block's type.
        var lex = Lexer.Lex(
            "module m\nfn f() { let mut i = 0\n while i <= 10 { i = i + 1 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        Assert.Single(fn.Body.Statements); // the let
        var wh = Assert.IsType<WhileExpr>(fn.Body.TrailingExpression);
        var cmp = Assert.IsType<BinaryExpr>(wh.Condition);
        Assert.Equal(BinaryOp.LessEqual, cmp.Op);
        Assert.Single(wh.Body.Statements);
    }

    [Fact]
    public void Parse_While_ThenTrailingExpression()
    {
        // Matches mutation.ov shape: while { ... } followed by a trailing value.
        var lex = Lexer.Lex(
            "module m\nfn f() -> Int { let mut t = 0\n while t < 10 { t = t + 1 }\n t }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        Assert.Equal(2, fn.Body.Statements.Length);
        Assert.IsType<WhileExpr>(((ExpressionStmt)fn.Body.Statements[1]).Expression);
        Assert.Equal("t", ((IdentifierExpr)fn.Body.TrailingExpression!).Name);
    }

    [Fact]
    public void Parse_While_ConditionRespectsRestrictedContext()
    {
        // `while flag { body }` — `flag { body }` must NOT be parsed as a record literal.
        var lex = Lexer.Lex("module m\nfn f() { while flag { body_call() } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var whe = Assert.IsType<WhileExpr>(fn.Body.TrailingExpression);
        Assert.Equal("flag", ((IdentifierExpr)whe.Condition).Name);
    }

    // ------------------------------------------------------------ else-if

    [Fact]
    public void Parse_ElseIfChain_WrapsNestedIfInSyntheticBlock()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { if a { 1 } else if b { 2 } else { 3 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var outer = Assert.IsType<IfExpr>(fn.Body.TrailingExpression);
        Assert.NotNull(outer.Else);
        // Synthetic wrapper block: no statements, trailing expression is the nested if.
        Assert.Empty(outer.Else!.Statements);
        var nested = Assert.IsType<IfExpr>(outer.Else.TrailingExpression);
        Assert.Equal("b", ((IdentifierExpr)nested.Condition).Name);
    }

    // -------------------------------------------------------------- enums

    [Fact]
    public void Parse_EnumDecl_BareVariants()
    {
        var lex = Lexer.Lex("module m\nenum Status { Pending, Shipped, Delivered }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var e = Assert.IsType<EnumDecl>(result.Module.Declarations[0]);
        Assert.Equal("Status", e.Name);
        Assert.Equal(new[] { "Pending", "Shipped", "Delivered" }, e.Variants.Select(v => v.Name).ToArray());
        Assert.All(e.Variants, v => Assert.Empty(v.Fields));
    }

    [Fact]
    public void Parse_EnumDecl_StructLikeVariants()
    {
        var lex = Lexer.Lex(
            "module m\nenum LoadError { NotFound { id: Int }, NetworkFailed { cause: String }, Timeout }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var e = Assert.IsType<EnumDecl>(result.Module.Declarations[0]);
        Assert.Equal(3, e.Variants.Length);
        Assert.Single(e.Variants[0].Fields);
        Assert.Equal("id", e.Variants[0].Fields[0].Name);
        Assert.Single(e.Variants[1].Fields);
        Assert.Empty(e.Variants[2].Fields); // bare after struct-like
    }

    // --------------------------------------------------------- annotations

    [Fact]
    public void Parse_DeriveAnnotation_OnRecord()
    {
        var lex = Lexer.Lex("module m\n@derive(Debug, Clone)\nrecord User { id: Int }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var rec = Assert.IsType<RecordDecl>(result.Module.Declarations[0]);
        Assert.Single(rec.Annotations);
        Assert.Equal("derive", rec.Annotations[0].Name);
        Assert.Equal(new[] { "Debug", "Clone" }, rec.Annotations[0].Arguments.ToArray());
    }

    [Fact]
    public void Parse_DeriveAnnotation_OnEnum()
    {
        var lex = Lexer.Lex("module m\n@derive(Debug)\nenum E { A, B }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var e = Assert.IsType<EnumDecl>(result.Module.Declarations[0]);
        Assert.Single(e.Annotations);
    }

    [Fact]
    public void Parse_Annotation_OnFunction_EmitsOV0157()
    {
        var lex = Lexer.Lex("module m\n@derive(Debug)\nfn f() { }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0157");
    }

    // ---------------------------------------------------------- tuples

    [Fact]
    public void Parse_TupleExpr_TwoElements()
    {
        var expr = ParseBodyExpression("(a, b)");
        var tup = Assert.IsType<TupleExpr>(expr);
        Assert.Equal(2, tup.Elements.Length);
    }

    [Fact]
    public void Parse_TupleExpr_ThreeElements_TrailingComma()
    {
        var expr = ParseBodyExpression("(1, 2, 3,)");
        var tup = Assert.IsType<TupleExpr>(expr);
        Assert.Equal(3, tup.Elements.Length);
    }

    [Fact]
    public void Parse_SingleParenExpression_NotATuple()
    {
        var expr = ParseBodyExpression("(42)");
        Assert.IsType<IntegerLiteralExpr>(expr);
    }

    // ---------------------------------------------------------- match

    [Fact]
    public void Parse_Match_BareVariantArms()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { match status { Pending => 1, Shipped => 2, Delivered => 3 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var m = Assert.IsType<MatchExpr>(fn.Body.TrailingExpression);
        Assert.Equal(3, m.Arms.Length);
        Assert.All(m.Arms, arm => Assert.IsType<IdentifierPattern>(arm.Pattern));
    }

    [Fact]
    public void Parse_Match_PathAndConstructorPatterns()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { match r { Ok(value) => value, Err(e) => 0 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var m = Assert.IsType<MatchExpr>(fn.Body.TrailingExpression);
        var okArm = Assert.IsType<ConstructorPattern>(m.Arms[0].Pattern);
        Assert.Equal(new[] { "Ok" }, okArm.Path.ToArray());
        Assert.Single(okArm.Arguments);
        Assert.IsType<IdentifierPattern>(okArm.Arguments[0]);
    }

    [Fact]
    public void Parse_Match_RecordDestructurePattern()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { match t { Tree.Node { value = v, left = l } => v } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var m = Assert.IsType<MatchExpr>(fn.Body.TrailingExpression);
        var rp = Assert.IsType<RecordPattern>(m.Arms[0].Pattern);
        Assert.Equal(new[] { "Tree", "Node" }, rp.Path.ToArray());
        Assert.Equal(2, rp.Fields.Length);
    }

    [Fact]
    public void Parse_Match_TupleScrutineeAndTuplePattern()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { match (state, event) { (A, B) => 1, (from, event) => 2 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var m = Assert.IsType<MatchExpr>(fn.Body.TrailingExpression);
        Assert.IsType<TupleExpr>(m.Scrutinee);
        Assert.IsType<TuplePattern>(m.Arms[0].Pattern);
        var secondArm = Assert.IsType<TuplePattern>(m.Arms[1].Pattern);
        Assert.Equal(2, secondArm.Elements.Length);
        Assert.IsType<IdentifierPattern>(secondArm.Elements[0]);
    }

    [Fact]
    public void Parse_Match_WildcardPattern()
    {
        var lex = Lexer.Lex("module m\nfn f() { match x { _ => 0 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var m = Assert.IsType<MatchExpr>(fn.Body.TrailingExpression);
        Assert.IsType<WildcardPattern>(m.Arms[0].Pattern);
    }

    [Fact]
    public void Parse_Match_ScrutineeRespectsRestrictedContext()
    {
        // `match flag { body }` — `flag { ... }` must NOT be a record literal.
        var lex = Lexer.Lex("module m\nfn f() { match x { A => 1, B => 2 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parse_Match_BlockBodiedArm_NeedsNoComma()
    {
        // Rust-style: match arms whose body ends with `}` can omit the trailing comma.
        var lex = Lexer.Lex(
            "module m\nfn f() { match x { A => { 1 } B => 2 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var m = Assert.IsType<MatchExpr>(fn.Body.TrailingExpression);
        Assert.Equal(2, m.Arms.Length);
    }

    // --------------------------------------------------- let with patterns

    [Fact]
    public void Parse_Let_TupleDestructure()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { let (a, b) = pair }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var let = Assert.IsType<LetStmt>(fn.Body.Statements[0]);
        var tup = Assert.IsType<TuplePattern>(let.Target);
        Assert.Equal(2, tup.Elements.Length);
        Assert.Equal("a", ((IdentifierPattern)tup.Elements[0]).Name);
        Assert.Equal("b", ((IdentifierPattern)tup.Elements[1]).Name);
    }

    [Fact]
    public void Parse_LetMut_WithTuplePattern_EmitsOV0161()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { let mut (a, b) = pair }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0161");
    }

    // --------------------------------------------------- task groups + trace + unsafe

    [Fact]
    public void Parse_ParallelBlock()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { let x = parallel { a(), b(), c(), } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var let = Assert.IsType<LetStmt>(fn.Body.Statements[0]);
        var p = Assert.IsType<ParallelExpr>(let.Initializer);
        Assert.Equal(3, p.Tasks.Length);
    }

    [Fact]
    public void Parse_RaceBlock()
    {
        var lex = Lexer.Lex(
            "module m\nfn f() { race { primary(), backup() } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var r = Assert.IsType<RaceExpr>(fn.Body.TrailingExpression);
        Assert.Equal(2, r.Tasks.Length);
    }

    [Fact]
    public void Parse_UnsafeBlock()
    {
        var lex = Lexer.Lex("module m\nfn f() { unsafe { raw_call() } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var u = Assert.IsType<UnsafeExpr>(fn.Body.TrailingExpression);
        Assert.IsType<CallExpr>(u.Body.TrailingExpression);
    }

    [Fact]
    public void Parse_TraceBlock()
    {
        var lex = Lexer.Lex("module m\nfn f() { trace { body() } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var t = Assert.IsType<TraceExpr>(fn.Body.TrailingExpression);
        Assert.IsType<CallExpr>(t.Body.TrailingExpression);
    }

    // ---------------------------------------------------- extern

    [Fact]
    public void Parse_Extern_Csharp()
    {
        var lex = Lexer.Lex(
            "module m\nextern \"csharp\" fn write(s: String) !{io} -> ()\n    binds \"System.Console.Write\"");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var ext = Assert.IsType<ExternDecl>(result.Module.Declarations[0]);
        Assert.Equal("csharp", ext.Platform);
        Assert.Equal("write", ext.Name);
        Assert.False(ext.IsUnsafe);
        Assert.Equal("System.Console.Write", ext.BindsTarget);
        Assert.Null(ext.FromLibrary);
    }

    [Fact]
    public void Parse_Extern_UnsafeC_WithFromLibrary()
    {
        var lex = Lexer.Lex(
            "module m\nunsafe extern \"c\" fn strlen(s: CString) -> Int\n    binds \"strlen\"\n    from \"libc\"");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var ext = Assert.IsType<ExternDecl>(result.Module.Declarations[0]);
        Assert.Equal("c", ext.Platform);
        Assert.True(ext.IsUnsafe);
        Assert.Equal("strlen", ext.BindsTarget);
        Assert.Equal("libc", ext.FromLibrary);
    }

    // -------------------------------------------------- smoke-parse goldens

    [Theory]
    [InlineData("hello.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("bst.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("race.ov")]
    [InlineData("inference.ov")]
    [InlineData("ffi.ov")]
    [InlineData("trace.ov")]
    [InlineData("effects.ov")]
    [InlineData("refinement.ov")]
    public void Parse_Example_HasNoDiagnostics(string file)
    {
        var result = ParseFile(file);
        Assert.Empty(result.Diagnostics);
        Assert.NotEmpty(result.Module.Declarations);
    }

    // --------------------------------------------------- generics / fn types

    [Fact]
    public void Parse_FunctionDecl_WithTypeParameters()
    {
        var lex = Lexer.Lex("module m\nfn map<T, U>(list: List<T>, f: fn(T) -> U) -> List<U> { }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
        Assert.Equal(new[] { "T", "U" }, fn.TypeParameters.ToArray());
    }

    [Fact]
    public void Parse_FunctionType_WithEffectRow()
    {
        var lex = Lexer.Lex(
            "module m\nfn apply<T, E>(f: fn(T) !{E} -> T, x: T) !{E} -> T { f(x) }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var fType = Assert.IsType<FunctionType>(fn.Parameters[0].Type);
        Assert.Single(fType.Parameters);
        Assert.NotNull(fType.Effects);
        Assert.Equal(new[] { "E" }, fType.Effects!.Effects.ToArray());
    }

    // ---------------------------------------------------------- type alias

    [Fact]
    public void Parse_TypeAlias_Simple()
    {
        var lex = Lexer.Lex("module m\ntype UserId = Int");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var t = Assert.IsType<TypeAliasDecl>(result.Module.Declarations[0]);
        Assert.Equal("UserId", t.Name);
        Assert.Equal("Int", ((NamedType)t.Target).Name);
        Assert.Null(t.Predicate);
    }

    [Fact]
    public void Parse_TypeAlias_WithRefinement()
    {
        var lex = Lexer.Lex(
            "module m\ntype Age = Int where 0 <= self && self <= 150");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var t = Assert.IsType<TypeAliasDecl>(result.Module.Declarations[0]);
        Assert.Equal("Age", t.Name);
        Assert.NotNull(t.Predicate);
        var and = Assert.IsType<BinaryExpr>(t.Predicate);
        Assert.Equal(BinaryOp.LogicalAnd, and.Op);
    }

    [Fact]
    public void Parse_TypeAlias_WithGenericParams()
    {
        var lex = Lexer.Lex(
            "module m\ntype NonEmpty<T> = List<T> where size(self) > 0");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var t = Assert.IsType<TypeAliasDecl>(result.Module.Declarations[0]);
        Assert.Equal(new[] { "T" }, t.TypeParameters.ToArray());
    }
}
