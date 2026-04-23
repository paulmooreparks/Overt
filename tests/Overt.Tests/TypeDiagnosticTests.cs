using Overt.Compiler.Diagnostics;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Verifies that the type checker emits diagnostics for mismatched types. Each test
/// pairs a small Overt snippet with an expected diagnostic code; tests also confirm
/// each diagnostic carries actionable notes where the error format promises them.
///
/// The type checker's policy: only fire diagnostics when BOTH sides of a comparison
/// are concrete. Type-var-carrying types (generic calls before unification) are
/// tolerated — proper unification is later work and firing here would produce false
/// positives on every `Ok(...)` in every example.
/// </summary>
public class TypeDiagnosticTests
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

    // ---------------------------------------------- OV0300 call argument type

    [Fact]
    public void OV0300_CallArgTypeMismatch_FiresOnMismatch()
    {
        var r = Check(
            "module t\nfn take_int(n: Int) -> Int { n }\nfn f() -> Int { take_int(\"x\") }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0300");
        Assert.Contains("expected `Int`", d.Message);
        Assert.Contains("got `String`", d.Message);
    }

    [Fact]
    public void OV0300_CallArgTypeMatch_NoDiagnostic()
    {
        var r = Check(
            "module t\nfn take_int(n: Int) -> Int { n }\nfn f() -> Int { take_int(42) }");
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void OV0300_GenericArgs_AreNotChecked()
    {
        // `Ok(42)` — Ok has signature fn<T, E>(T) -> Result<T, E>. The arg is concrete
        // (Int), parameter is a type var. Type vars skip the check pending unification.
        var r = Check(
            "module t\nfn f() -> Result<Int, IoError> { Ok(42) }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0300");
    }

    // ---------------------------------------------- OV0302 unknown field

    [Fact]
    public void OV0302_UnknownField_FiresWithFieldList()
    {
        var r = Check(
            "module t\nrecord User { id: Int, name: String }\n"
            + "fn f(u: User) -> Int { u.missing }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0302");
        Assert.Contains("`User`", d.Message);
        Assert.Contains("`missing`", d.Message);
        var help = Assert.Single(d.Notes, n => n.Kind == DiagnosticNoteKind.Help);
        Assert.True(help.Text.Contains("fields:") || help.Text.Contains("did you mean"));
    }

    [Fact]
    public void OV0302_TypoField_SuggestsDidYouMean()
    {
        var r = Check(
            "module t\nrecord User { name: String }\nfn f(u: User) -> String { u.nam }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0302");
        var help = Assert.Single(d.Notes, n => n.Kind == DiagnosticNoteKind.Help);
        Assert.Contains("did you mean", help.Text);
        Assert.Contains("`name`", help.Text);
    }

    [Fact]
    public void OV0302_KnownField_NoDiagnostic()
    {
        var r = Check(
            "module t\nrecord User { id: Int }\nfn f(u: User) -> Int { u.id }");
        Assert.Empty(r.Diagnostics);
    }

    // ---------------------------------------------- OV0303 match arm mismatch

    [Fact]
    public void OV0303_MatchArms_DifferentTypes_Fires()
    {
        // Integer-literal patterns aren't parser-supported yet, so use identifier
        // patterns. Each arm body yields a different concrete type.
        var r = Check(
            "module t\nfn f(x: Int) -> Int { match x { a => a, _ => \"nope\" } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0303");
        Assert.Contains("`String`", d.Message);
        Assert.Contains("`Int`", d.Message);
    }

    [Fact]
    public void OV0303_IfArms_DifferentTypes_Fires()
    {
        var r = Check(
            "module t\nfn f(b: Bool) -> Int { if b { 1 } else { \"nope\" } }");
        Assert.Contains(r.Diagnostics, d => d.Code == "OV0303");
    }

    [Fact]
    public void OV0303_IfWithoutElse_BodyMustBeUnit()
    {
        var r = Check(
            "module t\nfn f(b: Bool) -> Int { if b { 42 } }");
        Assert.Contains(r.Diagnostics, d => d.Code == "OV0303");
    }

    // ---------------------------------------------- OV0304 condition must be Bool

    [Fact]
    public void OV0304_IfCondition_IntNotBool_Fires()
    {
        var r = Check("module t\nfn f(x: Int) -> Int { if x { 1 } else { 2 } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0304");
        Assert.Contains("`if`", d.Message);
        Assert.Contains("`Int`", d.Message);
    }

    [Fact]
    public void OV0304_WhileCondition_IntNotBool_Fires()
    {
        var r = Check("module t\nfn f(x: Int) { while x { } }");
        Assert.Contains(r.Diagnostics, d => d.Code == "OV0304");
    }

    [Fact]
    public void OV0304_BoolCondition_NoDiagnostic()
    {
        var r = Check("module t\nfn f(b: Bool) -> Int { if b { 1 } else { 2 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0304");
    }

    // ---------------------------------------------- OV0306 wrong arity

    [Fact]
    public void OV0306_TooFewArgs_Fires()
    {
        var r = Check(
            "module t\nfn add(a: Int, b: Int) -> Int { a + b }\n"
            + "fn f() -> Int { add(1) }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0306");
        Assert.Contains("2 arguments", d.Message);
        Assert.Contains("got 1", d.Message);
    }

    [Fact]
    public void OV0306_TooManyArgs_Fires()
    {
        // Named args to work around OV0154's multi-arg-positional rule at parser level.
        // `extra` doesn't correspond to any declared parameter, but for arity purposes
        // we're only counting slots.
        var r = Check(
            "module t\nfn one(a: Int) -> Int { a }\n"
            + "fn f() -> Int { one(a = 1, extra = 2) }");
        Assert.Contains(r.Diagnostics, d => d.Code == "OV0306");
    }

    [Fact]
    public void OV0306_RightArity_NoDiagnostic()
    {
        var r = Check(
            "module t\nfn add(a: Int, b: Int) -> Int { a + b }\n"
            + "fn f() -> Int { add(a = 1, b = 2) }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0306");
    }

    // ---------------------------------------------- OV0307 ignored Result

    [Fact]
    public void OV0307_IgnoredResultInStatementPosition_Fires()
    {
        var r = Check(
            "module t\nfn f() !{io} { println(\"hi\") Ok(()) }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0307");
        Assert.Contains("ignored", d.Message);
    }

    [Fact]
    public void OV0307_PropagatedResult_NotIgnored()
    {
        var r = Check(
            "module t\nfn f() !{io} -> Result<(), IoError> { println(\"hi\")? Ok(()) }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0307");
    }

    [Fact]
    public void OV0307_DiscardLetUnderscore_NotIgnored()
    {
        var r = Check(
            "module t\nfn f() !{io} -> Result<(), IoError> "
            + "{ let _ = println(\"hi\") Ok(()) }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0307");
    }

    [Fact]
    public void OV0307_MatchWithResultArms_IgnoredAsStatement_Fires()
    {
        // match arms return Result; match as a whole is Result; in statement position
        // it's ignored.
        var r = Check(
            "module t\nfn consumer() !{io} -> Result<(), IoError> "
            + "{ match true { a => println(\"yes\"), _ => println(\"no\"), } Ok(()) }");
        Assert.Contains(r.Diagnostics, d => d.Code == "OV0307");
    }

    [Fact]
    public void OV0307_NonResultStatement_NotIgnored()
    {
        // Pure-Int expression in statement position is fine (it's discarded silently,
        // no error channel to worry about).
        var r = Check(
            "module t\nfn f() -> Int { 1 + 2 42 }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0307");
    }

    // ---------------------------------------------- OV0308 exhaustiveness

    [Fact]
    public void OV0308_MissingSingleVariant_Fires()
    {
        var r = Check(
            "module t\nenum Color { Red, Green, Blue }\n"
            + "fn f(c: Color) -> Int { match c { Color.Red => 1, Color.Green => 2 } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0308");
        Assert.Contains("`Color.Blue`", d.Message);
    }

    [Fact]
    public void OV0308_MissingMultipleVariants_ListsAll()
    {
        var r = Check(
            "module t\nenum Color { Red, Green, Blue, Yellow }\n"
            + "fn f(c: Color) -> Int { match c { Color.Red => 1 } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0308");
        // Missing list is alphabetical.
        Assert.Contains("`Color.Blue`", d.Message);
        Assert.Contains("`Color.Green`", d.Message);
        Assert.Contains("`Color.Yellow`", d.Message);
        Assert.Contains("variants", d.Message); // plural
    }

    [Fact]
    public void OV0308_Wildcard_SuppressesDiagnostic()
    {
        var r = Check(
            "module t\nenum Color { Red, Green, Blue }\n"
            + "fn f(c: Color) -> Int { match c { Color.Red => 1, _ => 99 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_IdentifierArm_CountsAsCatchAll()
    {
        // Bare identifier in arm position is a binding that matches anything —
        // treated as catch-all for exhaustiveness purposes.
        var r = Check(
            "module t\nenum Color { Red, Green, Blue }\n"
            + "fn f(c: Color) -> Int { match c { Color.Red => 1, anything => 99 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_AllVariantsCovered_NoDiagnostic()
    {
        var r = Check(
            "module t\nenum Color { Red, Green, Blue }\n"
            + "fn f(c: Color) -> Int "
            + "{ match c { Color.Red => 1, Color.Green => 2, Color.Blue => 3 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_DataVariantsSupported_InMatch()
    {
        // Enum with struct-like variant; record-pattern covers it.
        var r = Check(
            "module t\nenum Tree { Leaf, Node { value: Int } }\n"
            + "fn f(t: Tree) -> Int "
            + "{ match t { Tree.Leaf => 0, Tree.Node { value = v } => v } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_TupleScrutinee_NotChecked()
    {
        // Tuple-of-enum exhaustiveness is deferred; v0 only handles single-enum
        // scrutinees. Should produce no diagnostic even when incomplete.
        var r = Check(
            "module t\nenum A { X, Y }\nenum B { P, Q }\n"
            + "fn f(a: A, b: B) -> Int "
            + "{ match (a, b) { (A.X, B.P) => 1, _ => 0 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    // ------------------------------- OV0308 on stdlib enums (Option, Result)

    [Fact]
    public void OV0308_Option_MissingNone_Fires()
    {
        var r = Check(
            "module t\nfn f(o: Option<Int>) -> Int { match o { Some(x) => x } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0308");
        Assert.Contains("`Option.None`", d.Message);
    }

    [Fact]
    public void OV0308_Option_MissingSome_Fires()
    {
        var r = Check(
            "module t\nfn f(o: Option<Int>) -> Int { match o { None => 0 } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0308");
        Assert.Contains("`Option.Some`", d.Message);
    }

    [Fact]
    public void OV0308_Option_BothArms_NoDiagnostic()
    {
        var r = Check(
            "module t\nfn f(o: Option<Int>) -> Int "
            + "{ match o { Some(x) => x, None => 0 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_Result_MissingErr_Fires()
    {
        var r = Check(
            "module t\nfn f(r: Result<Int, String>) -> Int { match r { Ok(x) => x } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0308");
        Assert.Contains("`Result.Err`", d.Message);
    }

    [Fact]
    public void OV0308_Result_BothArms_NoDiagnostic()
    {
        var r = Check(
            "module t\nfn f(r: Result<Int, String>) -> Int "
            + "{ match r { Ok(x) => x, Err(e) => 0 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_Result_WildcardArm_SuppressesDiagnostic()
    {
        var r = Check(
            "module t\nfn f(r: Result<Int, String>) -> Int "
            + "{ match r { Ok(x) => x, _ => 99 } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "OV0308");
    }

    [Fact]
    public void OV0308_Option_NoneIsVariantNotBinding()
    {
        // Critical: `None` as a match arm pattern is a VARIANT REFERENCE on an
        // Option scrutinee, not a catch-all binding. If we got this wrong, adding
        // `None => ...` to an incomplete Option match would spuriously silence
        // the diagnostic for missing Some.
        var r = Check(
            "module t\nfn f(o: Option<Int>) -> Int { match o { None => 0 } }");
        // Should fire because Some is missing — proving None didn't act as a
        // catch-all.
        var d = Assert.Single(r.Diagnostics, x => x.Code == "OV0308");
        Assert.Contains("`Option.Some`", d.Message);
    }

    // ---------------------------------------------- smoke: examples stay clean

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
    public void Examples_ProduceNoTypeDiagnostics(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));
        var result = Check(source);
        var typeErrors = result.Diagnostics
            .Where(d => d.Code.StartsWith("OV030", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(typeErrors);
    }
}
