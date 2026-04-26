using Overt.Backend.CSharp;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

public class CSharpEmitterTests
{
    private static readonly string ExamplesDir =
        Path.Combine(AppContext.BaseDirectory, "examples");

    private static string EmitSource(string source)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        return CSharpEmitter.Emit(parse.Module);
    }

    [Fact]
    public void Emit_Hello_ContainsExpectedShape()
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, "hello.ov"));
        var csharp = EmitSource(source);

        Assert.Contains("namespace Overt.Generated.Hello;", csharp);
        Assert.Contains("public static class Module", csharp);
        Assert.Contains("public static Result<Unit, IoError> main()", csharp);
        // `?` propagation: the hoist emits `var __q_N = expr; if (!__q_N.IsOk)
        // return Err<E>(__q_N.UnwrapErr());`. An Unwrap() call only appears
        // at use sites where the Ok value is consumed; hello.ov's `?` is a
        // pure discard, so the Err-check shape is the signal.
        Assert.Contains("var __q_0 = println", csharp);
        Assert.Contains("if (!__q_0.IsOk) return Err<IoError>(__q_0.UnwrapErr());", csharp);
        // With the function-return type known, the emitter pins Ok's type parameter and
        // casts the argument so generic-return helpers target-type correctly in nested
        // positions. Either form is valid Ok; the explicit-T form is what we expect now.
        Assert.Contains("Ok<Unit>((Unit)Unit.Value)", csharp);
    }

    [Fact]
    public void Emit_Record_BecomesSealedRecord()
    {
        var csharp = EmitSource("module m\nrecord Point { x: Int, y: Int }");
        Assert.Contains("public sealed record Point(int x, int y);", csharp);
    }

    [Fact]
    public void Emit_Enum_BareVariants_BecomeSealedRecords()
    {
        var csharp = EmitSource("module m\nenum Color { Red, Green, Blue }");
        Assert.Contains("public abstract record Color;", csharp);
        Assert.Contains("public sealed record Color_Red : Color;", csharp);
        Assert.Contains("public sealed record Color_Green : Color;", csharp);
        Assert.Contains("public sealed record Color_Blue : Color;", csharp);
    }

    [Fact]
    public void Emit_EnumDataVariant_CarriesFields()
    {
        var csharp = EmitSource(
            "module m\nenum E { NotFound { id: Int }, Timeout }");
        Assert.Contains("public sealed record E_NotFound(int id) : E;", csharp);
        Assert.Contains("public sealed record E_Timeout : E;", csharp);
    }

    [Fact]
    public void Emit_InterpolatedString_UsesDollarSyntax()
    {
        var csharp = EmitSource(
            "module m\nfn f() { let msg = \"Hello, $name!\" }");
        // Hole expression is paren-wrapped so named-args / colons /
        // commas inside don't get reinterpreted as alignment / format
        // spec by the C# interpolation parser.
        Assert.Contains("$\"Hello, {(name)}!\"", csharp);
    }

    [Fact]
    public void Emit_With_LowersToCSharpWithExpression()
    {
        var csharp = EmitSource(
            "module m\nfn f(p: Point) -> Point { p with { x = 1 } }");
        // C# `record with { ... }` syntax passes through directly.
        Assert.Contains("with { x = 1 }", csharp);
    }

    [Fact]
    public void Emit_Pipe_SplicesIntoCallAsFirstArg()
    {
        var csharp = EmitSource("module m\nfn f(x: Int) -> Int { x |> add(1) }");
        // `x |> add(1)` becomes `add(x, 1)`.
        Assert.Contains("add(x, 1)", csharp);
    }

    [Fact]
    public void Emit_PipePropagate_AddsUnwrap()
    {
        var csharp = EmitSource("module m\nfn f(x: Int) -> Int { x |>? validate }");
        Assert.Contains(".Unwrap()", csharp);
    }

    [Fact]
    public void Emit_NamedArgumentCall_UsesCSharpNamedArgs()
    {
        var csharp = EmitSource(
            "module m\nfn f() { fetch(url = \"x\", timeout = \"y\") }");
        Assert.Contains("url: \"x\"", csharp);
        Assert.Contains("timeout: \"y\"", csharp);
    }

    // Smoke-emission: all examples produce some non-empty C# text without crashes.
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
    [InlineData("csharp/json.ov")]
    [InlineData("csharp/async.ov")]
    public void Emit_Example_ProducesCSharp(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExamplesDir, file));
        var csharp = EmitSource(source);
        Assert.Contains("namespace Overt.Generated.", csharp);
        Assert.Contains("#nullable enable", csharp);
    }

    [Fact]
    public void Emit_CSharpAttribute_OnFn_EmitsAsAttributeBeforeMethod()
    {
        var source = """
            module m
            @csharp("Fact")
            fn test_something() -> Int { 1 }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("[Fact]", csharp);
        // The attribute must appear before the method signature, not inside it.
        var attrIdx = csharp.IndexOf("[Fact]", StringComparison.Ordinal);
        var methodIdx = csharp.IndexOf("test_something", StringComparison.Ordinal);
        Assert.True(attrIdx >= 0 && methodIdx >= 0, "attribute and method must both appear");
        Assert.True(attrIdx < methodIdx, "attribute must appear before the method signature");
    }

    [Fact]
    public void Emit_MultipleCSharpAttributes_OnFn_EmitsEach()
    {
        var source = """
            module m
            @csharp("Theory")
            @csharp("InlineData(1)")
            @csharp("InlineData(2)")
            fn test_each(n: Int) -> Int { n }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("[Theory]", csharp);
        Assert.Contains("[InlineData(1)]", csharp);
        Assert.Contains("[InlineData(2)]", csharp);
    }

    [Fact]
    public void Emit_CSharpAttribute_WithEscapedQuote_DecodesCorrectly()
    {
        // Escape sequences in the attribute string must reach the emitter literal,
        // so `[JsonPropertyName("name")]` shows up with its quotes intact.
        var source = """
            module m
            @csharp("JsonPropertyName(\"name\")")
            fn getter() -> Int { 1 }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("[JsonPropertyName(\"name\")]", csharp);
    }

    [Fact]
    public void Emit_CSharpAttribute_OnRecord_EmitsBeforeRecordClass()
    {
        var source = """
            module m
            @csharp("Serializable")
            record Point { x: Int, y: Int }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("[Serializable]", csharp);
        var attrIdx = csharp.IndexOf("[Serializable]", StringComparison.Ordinal);
        var classIdx = csharp.IndexOf("public sealed record Point", StringComparison.Ordinal);
        Assert.True(attrIdx < classIdx, "attribute must precede the record class");
    }

    [Fact]
    public void Emit_CSharpAttribute_OnRecordField_EmitsAsPropertyTarget()
    {
        // Record fields lower to positional ctor parameters; the attribute
        // attaches to the synthesized property, so the emitter must wrap it
        // in a `[property: ...]` target specifier.
        var source = """
            module m
            record User {
                @csharp("JsonPropertyName(\"display_name\")")
                name: String,
                age: Int,
            }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("[property: JsonPropertyName(\"display_name\")] string name", csharp);
    }

    [Fact]
    public void Emit_CSharpAttribute_OnEnumVariant_EmitsBeforeVariantClass()
    {
        var source = """
            module m
            enum Status {
                @csharp("Obsolete")
                Pending,
                Shipped,
            }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("[Obsolete]", csharp);
        var attrIdx = csharp.IndexOf("[Obsolete]", StringComparison.Ordinal);
        var pendingIdx = csharp.IndexOf("Status_Pending", StringComparison.Ordinal);
        Assert.True(attrIdx < pendingIdx, "attribute must precede the variant class");
    }

    [Fact]
    public void Emit_DocComment_OnFn_EmitsXmlSummary()
    {
        var source = """
            module m
            @doc("Adds two integers and returns the sum.")
            fn add(a: Int, b: Int) -> Int { a + b }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("/// <summary>", csharp);
        Assert.Contains("/// Adds two integers and returns the sum.", csharp);
        Assert.Contains("/// </summary>", csharp);
    }

    [Fact]
    public void Emit_DocComment_OnRecord_EmitsXmlSummary()
    {
        var source = """
            module m
            @doc("A 2D point in cartesian coordinates.")
            record Point { x: Int, y: Int }
            """;
        var csharp = EmitSource(source);
        Assert.Contains("/// <summary>", csharp);
        Assert.Contains("/// A 2D point in cartesian coordinates.", csharp);
    }

    [Fact]
    public void Emit_DocComment_EscapesXmlSpecials()
    {
        var source = """
            module m
            @doc("Compares a < b in 5 < 10 sense; returns true.")
            fn lt() -> Bool { true }
            """;
        var csharp = EmitSource(source);
        // `<` must be escaped to &lt; so the emitted comment is valid XML.
        Assert.Contains("a &lt; b", csharp);
        Assert.Contains("5 &lt; 10", csharp);
        Assert.DoesNotContain("a < b", csharp);
    }

    [Fact]
    public void Emit_SingleIdentifierModule_PrefixesWithOvertGenerated()
    {
        // The single-identifier module name is treated as a short name
        // needing scoping; emits under `Overt.Generated.` so example code
        // and tests don't claim top-of-tree namespaces.
        var csharp = EmitSource("module greeter\nfn hi() -> Int { 1 }");
        Assert.Contains("namespace Overt.Generated.Greeter;", csharp);
    }

    [Fact]
    public void Emit_DottedModule_EmitsNamespaceVerbatim()
    {
        // Library authors who write a fully-qualified module name (a dotted
        // identifier) get exactly that as the emitted C# namespace, with no
        // `Overt.Generated.` prefix. This is the namespace-emission-control
        // mechanism for libraries that need a clean public API.
        var csharp = EmitSource("module ParksComputing.SemVer\nfn parse() -> Int { 1 }");
        Assert.Contains("namespace ParksComputing.SemVer;", csharp);
        Assert.DoesNotContain("Overt.Generated.ParksComputing", csharp);
    }

    [Fact]
    public void Emit_DottedModule_ImportingDottedModule_EmitsBothVerbatim()
    {
        // When a dotted-name module imports another dotted-name module,
        // the `using static` line targets the imported module's verbatim
        // namespace, not the prefixed one.
        var csharp = EmitSource(
            "module ParksComputing.SemVer.Tests\nuse ParksComputing.SemVer\nfn t() -> Int { 1 }");
        Assert.Contains("namespace ParksComputing.SemVer.Tests;", csharp);
        Assert.Contains("using static ParksComputing.SemVer.Module;", csharp);
        Assert.DoesNotContain("Overt.Generated.ParksComputing", csharp);
    }

    [Fact]
    public void Emit_DottedModule_ImportingSingleIdentifierModule_PrefixesImport()
    {
        // The prefix decision is per-target-module, not per-current-module.
        // A dotted module importing a single-identifier module sees the
        // imported one prefixed; only the importer's own namespace is
        // verbatim.
        var csharp = EmitSource(
            "module ParksComputing.SemVer\nuse greeter\nfn hi() -> Int { 1 }");
        Assert.Contains("namespace ParksComputing.SemVer;", csharp);
        Assert.Contains("using static Overt.Generated.Greeter.Module;", csharp);
    }

    [Fact]
    public void Emit_TryPatternExtern_GeneratesMultiStatementBody()
    {
        // `extern "csharp" try fn ...` lowers to a body that declares an
        // out temp, calls the bind target with `out`, and returns Some/None
        // based on the bool return.
        var source = """
            module m
            extern "csharp" try fn try_parse(s: String) !{io, fails} -> Option<Int>
                binds "System.Int32.TryParse"
            """;
        var csharp = EmitSource(source);
        Assert.Contains("int __overt_tryout = default!;", csharp);
        Assert.Contains("global::System.Int32.TryParse(s, out __overt_tryout)", csharp);
        Assert.Contains("OptionSome<int>(__overt_tryout)", csharp);
        Assert.Contains("OptionNone<int>", csharp);
    }

    [Fact]
    public void Emit_DocComment_OnRecordField_RaisesError()
    {
        // V1 doesn't support @doc on record fields (no clean spot for inline XML
        // comment in the positional ctor parameter list). Verify a clear error
        // surfaces rather than silent failure.
        var source = """
            module m
            record User {
                @doc("the user's display name")
                name: String,
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => EmitSource(source));
        Assert.Contains("@doc", ex.Message);
        Assert.Contains("name", ex.Message);
    }
}
