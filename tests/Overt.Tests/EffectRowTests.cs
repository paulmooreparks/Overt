using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Verifies that the type checker's effect-row enforcement (OV0310) diagnoses when a
/// function's body performs a concrete effect (io / async / inference) that the
/// signature doesn't declare. Effect-row type variables (E, F, ...) and the implicit
/// <c>fails</c> are deliberately not part of this pass — they wait on unification.
/// </summary>
public class EffectRowTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static TypeCheckResult Check(string source)
    {
        var lex = Lexer.Lex(source);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var resolve = NameResolver.Resolve(parse.Module);
        return TypeChecker.Check(parse.Module, resolve);
    }

    [Fact]
    public void OV0310_UncoveredIo_FiresWhenPrintlnInPureFunction()
    {
        var r = Check(
            "module t\nfn f() -> Result<(), IoError> { println(\"hi\") }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0310");
        Assert.Contains("`io`", d.Message);
        Assert.Contains("empty", d.Message);
    }

    [Fact]
    public void OV0310_PrintlnInEffectfulFunction_NoDiagnostic()
    {
        var r = Check(
            "module t\nfn f() !{io} -> Result<(), IoError> { println(\"hi\") }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0310");
    }

    [Fact]
    public void OV0310_HelpLine_SuggestsCompleteRow()
    {
        var r = Check(
            "module t\nfn f() !{async} -> Result<(), IoError> { println(\"hi\") }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0310");
        var help = Assert.Single(d.Notes, n => n.Kind == Overt.Compiler.Diagnostics.DiagnosticNoteKind.Help);
        Assert.Contains("!{async, io}", help.Text);
    }

    [Fact]
    public void OV0310_ParallelAddsAsync()
    {
        var r = Check(
            "module t\nfn f() -> Int { let p = parallel { 1, 2 } 42 }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0310");
        Assert.Contains("`async`", d.Message);
    }

    [Fact]
    public void OV0310_RaceAddsAsync()
    {
        var r = Check(
            "module t\nfn f() -> Int { race { 1, 2 } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0310");
        Assert.Contains("`async`", d.Message);
    }

    [Fact]
    public void OV0310_EffectVarInBody_IsNotChecked()
    {
        // Calling a higher-order fn whose callback carries an effect-row variable does
        // NOT surface concrete effects until the variable is solved at the call site.
        // This pass doesn't solve — it tolerates.
        var r = Check(
            "module t\nfn apply<T, E>(f: fn(T) !{E} -> T, x: T) !{E} -> T { f(x) }\n"
            + "fn pure_looking() -> Int { apply(f = id_int, x = 0) }\n"
            + "fn id_int(n: Int) -> Int { n }");
        // apply's `E` is a type variable, not a concrete effect. pure_looking's row
        // is empty; the call to apply doesn't add any CONCRETE effect. So no
        // OV0310 diagnostic — even though a future unification pass would catch
        // more subtle cases.
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0310");
    }

    [Fact]
    public void OV0310_DeclaringMoreEffectsThanUsed_IsFine()
    {
        // Declaring `!{io}` but not actually calling anything with io is not an error
        // in v1. The check is one-directional: body ⊆ declared, not equality.
        var r = Check(
            "module t\nfn f(n: Int) !{io} -> Int { n + 1 }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0310");
    }

    [Fact]
    public void OV0310_TraceBlock_NoOwnEffect()
    {
        // `trace { body }` is a pass-through for effect purposes — its own semantics
        // (subscribing to TraceEvent consumers) don't manifest as `io` / `async`.
        // The body's effects still flow through, so a println inside trace still
        // requires io.
        var clean = Check(
            "module t\nfn f() -> Int { trace { 42 } }");
        Assert.DoesNotContain(clean.Diagnostics, d => d.Code == "OV0310");

        var dirty = Check(
            "module t\nfn f() -> Result<(), IoError> { trace { println(\"in trace\") } }");
        Assert.Contains(dirty.Diagnostics, d => d.Code == "OV0310");
    }

    // --------------------------------------------- smoke: examples stay clean

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
    public void Examples_ProduceNoEffectRowDiagnostics(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));
        var result = Check(source);
        var effectErrors = result.Diagnostics
            .Where(d => d.Code == "OV0310")
            .ToArray();
        Assert.Empty(effectErrors);
    }
}
