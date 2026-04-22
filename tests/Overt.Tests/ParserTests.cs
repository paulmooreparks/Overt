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
        Assert.Equal("x", let.Name);
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
        Assert.Equal("counter", let.Name);
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
        var lex = Lexer.Lex("module m\nfn f() { let mut x = 0; x = x + 1 }");
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
    public void Parse_IfElse_BothArmsRequired()
    {
        var lex = Lexer.Lex("module m\nfn f() { if cond { 1 } else { 2 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Empty(result.Diagnostics);

        var fn = (FunctionDecl)result.Module.Declarations[0];
        var ife = Assert.IsType<IfExpr>(fn.Body.TrailingExpression);
        Assert.IsType<IdentifierExpr>(ife.Condition);
        Assert.Equal("1", ((IntegerLiteralExpr)ife.Then.TrailingExpression!).Lexeme);
        Assert.Equal("2", ((IntegerLiteralExpr)ife.Else.TrailingExpression!).Lexeme);
    }

    [Fact]
    public void Parse_IfWithoutElse_IsDiagnostic()
    {
        // Per DESIGN.md §4 and precedence.md §6, both arms are required.
        var lex = Lexer.Lex("module m\nfn f() { if cond { 1 } }");
        var result = Parser.Parse(lex.Tokens);
        Assert.Contains(result.Diagnostics, d => d.Code == "OV0150");
    }
}
