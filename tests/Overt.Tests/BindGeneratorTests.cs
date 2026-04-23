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
}
