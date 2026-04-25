using Overt.Backend.CSharp;
using Overt.Compiler.Syntax;
using Overt.Compiler.Semantics;

namespace Overt.Tests;

/// <summary>
/// Tests for the `overt bind` reflection-driven facade generator. Focus on
/// two invariants:
/// 1. The generated Overt source parses, name-resolves, and type-checks
///    cleanly — a facade that the parser rejects is useless.
/// 2. The generator's name-mangling keeps overload collisions distinct —
///    same C# name + different arity becomes distinct Overt names.
/// Actual runtime round-trips are covered by StdlibTranspiledEndToEndTests.
/// </summary>
public class BindGeneratorTests
{
    [Fact]
    public void Generate_SystemIoPath_ParsesAndTypeChecks()
    {
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));

        Assert.StartsWith("module path", src);
        Assert.Contains("extern \"csharp\"", src);
        Assert.Contains("binds \"System.IO.Path.", src);

        // Parse + resolve + check — no diagnostics anywhere.
        var lex = Lexer.Lex(src);
        Assert.Empty(lex.Diagnostics);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var resolved = NameResolver.Resolve(parse.Module);
        Assert.Empty(resolved.Diagnostics);
        var typed = TypeChecker.Check(parse.Module, resolved);
        Assert.Empty(typed.Diagnostics);

        // Sanity: the facade has multiple declarations.
        Assert.True(parse.Module.Declarations.Length > 5,
            $"expected >5 extern decls for System.IO.Path, got {parse.Module.Declarations.Length}");
    }

    [Fact]
    public void Generate_OverloadsGetTypeSuffix()
    {
        // System.IO.Path has Combine(string, string), Combine(string, string, string),
        // etc. — all renderable. Overloads disambiguate by C# parameter-type names
        // so arity-alike overloads (Math.Abs(int) vs Math.Abs(double)) stay distinct
        // even when they'd share an arity suffix.
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));
        Assert.Contains("fn combine_string_string(", src);
        Assert.Contains("fn combine_string_string_string(", src);
        Assert.Contains("fn combine_string_string_string_string(", src);
        Assert.DoesNotContain("fn combine(", src);
    }

    [Fact]
    public void Generate_PublicStaticPropertiesBecomeZeroArgExterns()
    {
        // System.Environment exposes several public static properties
        // (MachineName, UserName, ProcessorCount); each should emit as a
        // zero-arg extern. The runtime-side detection (bare member access
        // vs. method call) is handled by CSharpEmitter's reflection check,
        // not by the generator.
        var src = BindGenerator.Generate("env", typeof(System.Environment));
        Assert.Contains("fn machine_name()", src);
        Assert.Contains("fn user_name()", src);
        Assert.Contains("fn processor_count()", src);
    }

    [Fact]
    public void Generate_ParameterCollidingWithTopLevelGetsMangled()
    {
        // System.Environment has both `ExitCode` (property) and
        // `Exit(int exitCode)`. Without mangling, the parameter name
        // `exit_code` collides with the free function `exit_code`, hitting
        // Overt's no-shadowing rule. Parameter gets `_arg` suffix.
        var src = BindGenerator.Generate("env", typeof(System.Environment));
        Assert.Contains("exit_code_arg", src);
    }

    [Fact]
    public void Generate_ValueType_EmitsOpaqueTypeAndInstanceMembers()
    {
        // Struct types (IsValueType) should also emit `extern type` and
        // instance members, not just static ones. DateTime is the motivating
        // case: before struct support, only `DaysInMonth` rendered; with it,
        // `year(self)`, `month(self)`, utc_now, now, etc. all emit.
        var src = BindGenerator.Generate(
            "stdlib.csharp.system.datetime",
            typeof(System.DateTime));
        Assert.Contains("extern \"csharp\" type DateTime binds \"System.DateTime\"", src);
        // Instance property access (zero-arg-besides-self extern).
        Assert.Contains("fn year(self: DateTime)", src);
        Assert.Contains("fn month(self: DateTime)", src);
        // Static property returning the target type itself — needs
        // MapInstanceType-aware field/prop emission.
        Assert.Contains("fn utc_now()", src);
        Assert.Contains("-> Result<DateTime, IoError>", src);
    }

    [Fact]
    public void Generate_CrossTypeOpaqueRefs_EmitUseImportAndSignature()
    {
        // StreamReader has a constructor taking Stream. When we bind
        // StreamReader with Stream registered as an opaque reference
        // (plus an import-from module), the generator should:
        //   1. Emit `use stdlib.csharp.system.io.stream.{Stream}` at top
        //   2. Render the constructor's Stream parameter as the Overt
        //      type `Stream`, not skip the method
        var opaques = new[]
        {
            new BindGenerator.OpaqueTypeRef(
                typeof(System.IO.Stream),
                "Stream",
                "stdlib.csharp.system.io.stream"),
        };
        var src = BindGenerator.Generate(
            "stdlib.csharp.system.io.streamreader",
            typeof(System.IO.StreamReader),
            opaques);

        Assert.Contains("use stdlib.csharp.system.io.stream.{Stream}", src);
        Assert.Contains("stream: Stream", src);
        // The bare constructor (no Stream) should still exist; overloads
        // disambiguate by param type.
        Assert.Contains("new_stream(", src);
    }

    [Fact]
    public void Generate_OpaqueRefWithoutImportModule_StillRendersType()
    {
        // Registering an opaque ref without an import module still lets
        // the generator render its name — the user is responsible for
        // importing the type in their consuming code.
        var opaques = new[]
        {
            new BindGenerator.OpaqueTypeRef(typeof(System.IO.Stream), "Stream", null),
        };
        var src = BindGenerator.Generate(
            "stdlib.csharp.system.io.streamreader",
            typeof(System.IO.StreamReader),
            opaques);

        Assert.DoesNotContain("use stdlib.csharp.system.io.stream", src);
        Assert.Contains("stream: Stream", src);
    }

    [Fact]
    public void Generate_SingleOverloadKeepsBareName()
    {
        // `Path.Exists(string)` is the only renderable Exists overload (the
        // `ReadOnlySpan<char>` one is skipped), so it should keep the bare
        // name without an arity suffix.
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));
        Assert.Contains("fn exists(", src);
    }

    [Fact]
    public void Generate_NullableReferenceReturn_LowersToOption()
    {
        // System.Environment.GetEnvironmentVariable is annotated to return
        // `string?` in modern BCL nullable-annotated builds. The convention
        // layer should lower that to `Option<String>` (then wrapped in
        // Result for impure namespaces).
        var src = BindGenerator.Generate("env", typeof(System.Environment));
        Assert.Contains(
            "fn get_environment_variable(variable: String) !{io} -> Result<Option<String>, IoError>",
            src);
    }

    [Fact]
    public void Generate_NonNullableReturn_StaysUnwrapped()
    {
        // System.IO.Path.Combine(string, string) is annotated as non-nullable;
        // it should land as bare String, not Option<String>. Path is a pure
        // namespace so no Result wrap either. (Other methods on Path —
        // ChangeExtension, GetDirectoryName — DO return string?, so a global
        // "no Option" assertion would over-reach.)
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));
        Assert.Contains(
            "fn combine_string_string(path1: String, path2: String) -> String",
            src);
    }

    [Fact]
    public void Generate_NullableReturn_OnPureNamespace_IsBareOption()
    {
        // Path.ChangeExtension(string?, string?) returns string?. Path is
        // pure (its rule says no Result wrap), so the return should be a
        // bare Option<String>, not Result<Option<String>, ...>.
        var src = BindGenerator.Generate("path", typeof(System.IO.Path));
        Assert.Contains(
            "-> Option<String>",
            src);
        Assert.DoesNotContain("Result<Option<String>", src);
    }

    /// <summary>Synthetic surface for async-lowering tests — three method
    /// shapes the convention layer needs to recognise: `Task&lt;T&gt;` for
    /// primitive T, non-generic `Task` (currently skipped), and a sync
    /// reference for comparison.</summary>
    public static class AsyncFixture
    {
        public static System.Threading.Tasks.Task<int> FetchInt(string source) =>
            System.Threading.Tasks.Task.FromResult(42);

        public static System.Threading.Tasks.Task DoSomething() =>
            System.Threading.Tasks.Task.CompletedTask;

        public static int SyncSibling(int x) => x;
    }

    [Fact]
    public void Generate_TaskOfTReturn_LowersToOvertTaskWithAsyncEffect()
    {
        var src = BindGenerator.Generate("fixture", typeof(AsyncFixture));
        // Task<int> -> Task<Int>. async picks up in the effect row. The
        // async path skips the Result wrap (the task itself carries the
        // failure channel via exceptions caught at .await time).
        Assert.Contains(
            "fn fetch_int(source: String) !{io, fails, async} -> Task<Int>",
            src);
    }

    [Fact]
    public void Generate_NonGenericTaskReturn_IsSkipped()
    {
        // Non-generic Task is intentionally not lowered in v1; the C#
        // emitter does not yet bridge Task -> Task<Unit>. Surfacing it
        // would produce calls that fail to type-check downstream.
        var src = BindGenerator.Generate("fixture", typeof(AsyncFixture));
        Assert.Contains(
            "// skipped " + typeof(AsyncFixture).FullName + ".DoSomething",
            src);
    }

    [Fact]
    public void Generate_TryPattern_LowersToOptionWithTryKeyword()
    {
        // Int32.TryParse(string, out int) is the canonical Try-pattern
        // method. The convention layer drops the trailing out parameter
        // and emits `try` to flag the Try kind for the emitter, which
        // generates the corresponding multi-statement body.
        var src = BindGenerator.Generate("int32", typeof(int));
        Assert.Contains(
            "extern \"csharp\" try fn try_parse(s: String) !{io, fails} -> Option<Int>",
            src);
    }

    [Fact]
    public void Generate_SyncReturn_DoesNotPickUpAsyncEffect()
    {
        var src = BindGenerator.Generate("fixture", typeof(AsyncFixture));
        // Negative control: sync method on the same type doesn't get
        // `async` in its effect row.
        Assert.Contains(
            "fn sync_sibling(x: Int)",
            src);
        // The `async` token must appear only on the FetchInt line.
        var asyncLines = src
            .Split('\n')
            .Where(l => l.Contains("async", StringComparison.Ordinal)
                     && l.TrimStart().StartsWith("extern", StringComparison.Ordinal))
            .ToList();
        Assert.Single(asyncLines);
        Assert.Contains("fetch_int", asyncLines[0]);
    }
}
