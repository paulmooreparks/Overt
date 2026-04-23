using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Module-qualified references (<c>List.empty</c>, <c>Trace.subscribe</c>,
/// <c>CString.from</c>) resolve through the stdlib's qualified-name table rather than
/// the emitter's heuristics. The upshot: their signatures — including effects — are
/// visible to every pass that walks the symbol table.
/// </summary>
public class ModuleQualifiedResolutionTests
{
    private static ResolutionResult Resolve(string source)
    {
        var lex = Lexer.Lex(source);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        return NameResolver.Resolve(parse.Module);
    }

    private static TypeCheckResult Check(string source)
    {
        var lex = Lexer.Lex(source);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var resolved = NameResolver.Resolve(parse.Module);
        return TypeChecker.Check(parse.Module, resolved);
    }

    [Fact]
    public void Resolve_ListEmpty_MapsToStdlibQualifiedSymbol()
    {
        var source = "module t\nfn f() -> List<Int> { List.empty() }";
        var r = Resolve(source);

        // The FieldAccessExpr's span should have a recorded resolution to the qualified
        // `List.empty` stdlib symbol.
        var fn = (FunctionDecl)r.Module.Declarations[0];
        var call = (CallExpr)fn.Body.TrailingExpression!;
        var fa = (FieldAccessExpr)call.Callee;

        Assert.True(r.Resolutions.TryGetValue(fa.Span, out var sym));
        Assert.Equal("List.empty", sym!.Name);
        Assert.Equal(SymbolKind.Function, sym.Kind);
    }

    [Fact]
    public void TypeCheck_ListEmpty_CallReturnsListT()
    {
        var r = Check("module t\nfn f() -> List<Int> { List.empty() }");

        var fn = (FunctionDecl)r.Module.Declarations[0];
        var call = (CallExpr)fn.Body.TrailingExpression!;
        var callType = r.ExpressionTypes[call.Span];

        // Call returns the function's declared return type `List<T>`; T is still a
        // variable until unification, so it shows up in the Display string.
        Assert.Equal("List<T>", callType.Display);
    }

    [Fact]
    public void EffectCheck_TraceSubscribe_CarriesIoThrough()
    {
        // Trace.subscribe declares `!{io}`. A pure caller that calls it should fire
        // OV0310 for the uncovered io — proving effect tracking sees through module-
        // qualified callees.
        var r = Check(
            "module t\nfn pure() { Trace.subscribe(observer) }\n"
            + "fn observer(e: TraceEvent) !{io} -> () { }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0310");
        Assert.Contains("`io`", d.Message);
    }

    [Fact]
    public void EffectCheck_TraceSubscribeInIoFn_NoDiagnostic()
    {
        var r = Check(
            "module t\nfn setup() !{io} { Trace.subscribe(observer) }\n"
            + "fn observer(e: TraceEvent) !{io} -> () { }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0310");
    }

    [Fact]
    public void Resolve_CStringFrom_IsResolvable()
    {
        var r = Resolve("module t\nfn f(s: String) -> CString { CString.from(s) }");
        var fn = (FunctionDecl)r.Module.Declarations[0];
        var call = (CallExpr)fn.Body.TrailingExpression!;
        var fa = (FieldAccessExpr)call.Callee;

        Assert.True(r.Resolutions.TryGetValue(fa.Span, out var sym));
        Assert.Equal("CString.from", sym!.Name);
    }

    [Fact]
    public void Resolve_UnknownModuleMember_LeavesUnresolved()
    {
        // `List.not_a_member()` — List is a stdlib symbol, but there's no qualified
        // `List.not_a_member` entry. The target still resolves to List; the field
        // access just doesn't get a resolution recorded.
        var r = Resolve("module t\nfn f() { List.not_a_member() }");
        var fn = (FunctionDecl)r.Module.Declarations[0];
        var call = (CallExpr)fn.Body.TrailingExpression!;
        var fa = (FieldAccessExpr)call.Callee;

        // The field access span should NOT have a resolution — there's no such member.
        Assert.False(r.Resolutions.ContainsKey(fa.Span));
    }
}
