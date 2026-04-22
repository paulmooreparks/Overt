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
}
