using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class TypeCheckerTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static TypeCheckResult CheckSource(string source)
    {
        var lex = Lexer.Lex(source);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var resolve = NameResolver.Resolve(parse.Module);
        return TypeChecker.Check(parse.Module, resolve);
    }

    // --------------------------------------------- declaration types

    [Fact]
    public void Check_FunctionSignature_CapturesEffectsAndReturn()
    {
        var result = CheckSource(
            "module m\nfn greet(name: String) !{io} -> Result<(), IoError> { }");
        var sym = result.SymbolTypes.Keys.Single(s => s.Name == "greet");
        var ft = Assert.IsType<FunctionTypeRef>(result.SymbolTypes[sym]);
        Assert.Single(ft.Parameters);
        Assert.Equal(PrimitiveType.String, ft.Parameters[0]);
        Assert.Equal(new[] { "io" }, ft.Effects.ToArray());
        var ret = Assert.IsType<NamedTypeRef>(ft.Return);
        Assert.Equal("Result", ret.Name);
    }

    [Fact]
    public void Check_GenericFunction_UsesTypeVarsInSignature()
    {
        var result = CheckSource(
            "module m\nfn apply<T, E>(f: fn(T) !{E} -> T, x: T) !{E} -> T { x }");
        var sym = result.SymbolTypes.Keys.Single(s => s.Name == "apply");
        var ft = Assert.IsType<FunctionTypeRef>(result.SymbolTypes[sym]);
        var param0 = Assert.IsType<FunctionTypeRef>(ft.Parameters[0]);
        Assert.IsType<TypeVarRef>(param0.Parameters[0]);
        Assert.IsType<TypeVarRef>(ft.Return);
    }

    [Fact]
    public void Check_Record_RegistersAsNamedType()
    {
        var result = CheckSource("module m\nrecord Point { x: Int, y: Int }");
        var sym = result.SymbolTypes.Keys.Single(s => s.Name == "Point");
        var nt = Assert.IsType<NamedTypeRef>(result.SymbolTypes[sym]);
        Assert.Equal("Point", nt.Name);
    }

    [Fact]
    public void Check_TypeAlias_UsesTargetType()
    {
        var result = CheckSource("module m\ntype UserId = Int");
        var sym = result.SymbolTypes.Keys.Single(s => s.Name == "UserId");
        Assert.Equal(PrimitiveType.Int, result.SymbolTypes[sym]);
    }

    // --------------------------------------------- expression annotation

    [Fact]
    public void Check_IntegerLiteral_TypedAsInt()
    {
        var result = CheckSource("module m\nfn f() -> Int { 42 }");
        var fn = (FunctionDecl)result.Module.Declarations[0];
        var lit = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Int, result.ExpressionTypes[lit.Span]);
    }

    [Fact]
    public void Check_Identifier_ResolvesToParamType()
    {
        var result = CheckSource("module m\nfn f(n: Int) -> Int { n }");
        var fn = (FunctionDecl)result.Module.Declarations[0];
        var id = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Int, result.ExpressionTypes[id.Span]);
    }

    [Fact]
    public void Check_BinaryArithmetic_PromotesToFloat()
    {
        var result = CheckSource("module m\nfn f(i: Int, d: Float) -> Float { i + d }");
        var fn = (FunctionDecl)result.Module.Declarations[0];
        var add = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Float, result.ExpressionTypes[add.Span]);
    }

    [Fact]
    public void Check_Comparison_IsBool()
    {
        var result = CheckSource("module m\nfn f(a: Int, b: Int) -> Bool { a < b }");
        var fn = (FunctionDecl)result.Module.Declarations[0];
        var cmp = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Bool, result.ExpressionTypes[cmp.Span]);
    }

    [Fact]
    public void Check_FieldAccess_UsesRecordField()
    {
        var result = CheckSource(
            "module m\nrecord Point { x: Int, y: Int }\nfn f(p: Point) -> Int { p.x }");
        var fn = (FunctionDecl)result.Module.Declarations[1];
        var fa = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Int, result.ExpressionTypes[fa.Span]);
    }

    [Fact]
    public void Check_Call_UsesCalleeReturnType()
    {
        var result = CheckSource(
            "module m\nfn helper() -> Int { 1 }\nfn main() -> Int { helper() }");
        var fn = (FunctionDecl)result.Module.Declarations[1];
        var call = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Int, result.ExpressionTypes[call.Span]);
    }

    [Fact]
    public void Check_Tuple_ProducesTupleType()
    {
        var result = CheckSource("module m\nfn f() { let t = (1, 2.0, true) }");
        var fn = (FunctionDecl)result.Module.Declarations[0];
        var letStmt = (LetStmt)fn.Body.Statements[0];
        var tuple = (TupleExpr)letStmt.Initializer;
        var tt = Assert.IsType<TupleTypeRef>(result.ExpressionTypes[tuple.Span]);
        Assert.Equal(3, tt.Elements.Length);
        Assert.Equal(PrimitiveType.Int, tt.Elements[0]);
        Assert.Equal(PrimitiveType.Float, tt.Elements[1]);
        Assert.Equal(PrimitiveType.Bool, tt.Elements[2]);
    }

    [Fact]
    public void Check_InterpolatedString_IsString()
    {
        var result = CheckSource("module m\nfn f(n: Int) -> String { \"n=$n\" }");
        var fn = (FunctionDecl)result.Module.Declarations[0];
        var isx = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.String, result.ExpressionTypes[isx.Span]);
    }

    [Fact]
    public void Check_Propagate_UnwrapsResult()
    {
        var result = CheckSource(
            "module m\nfn get() -> Result<Int, IoError> { Ok(1) }\nfn f() -> Int { get()? }");
        var fn = (FunctionDecl)result.Module.Declarations[1];
        var prop = fn.Body.TrailingExpression!;
        Assert.Equal(PrimitiveType.Int, result.ExpressionTypes[prop.Span]);
    }

    // --------------------------------------------- smoke: examples

    [Theory]
    [InlineData("hello.ov")]
    [InlineData("mutation.ov")]
    [InlineData("pipeline.ov")]
    [InlineData("bst.ov")]
    [InlineData("state_machine.ov")]
    [InlineData("dashboard.ov")]
    [InlineData("race.ov")]
    [InlineData("trace.ov")]
    [InlineData("effects.ov")]
    [InlineData("refinement.ov")]
    [InlineData("csharp/inference.ov")]
    [InlineData("csharp/ffi.ov")]
    public void Check_Example_DoesNotCrash(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));
        var result = CheckSource(source);
        // No type-error diagnostics yet — the checker only annotates. The assertion is
        // that every example's expressions get *some* entry (including UnknownType) and
        // no crash occurs.
        Assert.NotEmpty(result.SymbolTypes);
    }
}
