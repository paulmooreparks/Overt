using System.Collections.Immutable;
using Overt.Compiler.Modules;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Unit tests for <see cref="ExternUseExpander"/>. The expander is the seam
/// between Overt-side `extern "csharp" use "..."` declarations and the
/// per-backend resolver that turns target metadata into Overt source. These
/// tests exercise the expander against a stub resolver so the contract is
/// verified without dragging .NET reflection into the test path.
/// </summary>
public class ExternUseExpanderTests
{
    private static ModuleDecl Parse(string source)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        return parse.Module;
    }

    [Fact]
    public void Expand_ModuleWithoutExternUse_PassesThroughUnchanged()
    {
        var module = Parse("module m\nfn hello() -> Int { 1 }");
        var result = ExternUseExpander.Expand(module, (_, _) => null);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(module.Declarations.Length, result.Module.Declarations.Length);
        Assert.IsType<FunctionDecl>(result.Module.Declarations[0]);
    }

    [Fact]
    public void Expand_SingleExternUse_SplicesGeneratedDecls()
    {
        var module = Parse(
            "module m\nextern \"csharp\" use \"System.IO.File\"\nfn other() -> Int { 1 }");

        // Stub resolver: when asked for System.IO.File, returns a tiny
        // synthetic module with one extern fn declaration.
        ExternUseExpander.Resolver resolver = (platform, target) =>
        {
            Assert.Equal("csharp", platform);
            Assert.Equal("System.IO.File", target);
            return """
                module __synthetic
                extern "csharp" fn read_all_text(path: String) !{io, fails} -> Result<String, IoError>
                    binds "System.IO.File.ReadAllText"
                """;
        };

        var result = ExternUseExpander.Expand(module, resolver);
        Assert.Empty(result.Diagnostics);

        // The expanded module has the extern fn (from the resolver) plus
        // the user's `fn other`, in source order.
        Assert.Equal(2, result.Module.Declarations.Length);
        var extern0 = Assert.IsType<ExternDecl>(result.Module.Declarations[0]);
        Assert.Equal("read_all_text", extern0.Name);
        Assert.Equal("System.IO.File.ReadAllText", extern0.BindsTarget);
        var fn1 = Assert.IsType<FunctionDecl>(result.Module.Declarations[1]);
        Assert.Equal("other", fn1.Name);

        // The original ExternUseDecl is gone from the expanded module.
        Assert.DoesNotContain(result.Module.Declarations, d => d is ExternUseDecl);
    }

    [Fact]
    public void Expand_MultipleExternUses_PreservesSourceOrder()
    {
        var module = Parse(
            "module m\nextern \"csharp\" use \"X\"\nfn middle() -> Int { 1 }\nextern \"csharp\" use \"Y\"\nfn last() -> Int { 2 }");

        ExternUseExpander.Resolver resolver = (_, target) =>
            $"""
            module __synthetic
            extern "csharp" fn from_{target.ToLowerInvariant()}() -> Int
                binds "{target}.M"
            """;

        var result = ExternUseExpander.Expand(module, resolver);
        Assert.Empty(result.Diagnostics);

        // Expected order: extern fn from X, fn middle, extern fn from Y, fn last.
        Assert.Equal(4, result.Module.Declarations.Length);
        Assert.Equal("from_x", Assert.IsType<ExternDecl>(result.Module.Declarations[0]).Name);
        Assert.Equal("middle", Assert.IsType<FunctionDecl>(result.Module.Declarations[1]).Name);
        Assert.Equal("from_y", Assert.IsType<ExternDecl>(result.Module.Declarations[2]).Name);
        Assert.Equal("last", Assert.IsType<FunctionDecl>(result.Module.Declarations[3]).Name);
    }

    [Fact]
    public void Expand_ResolverReturnsNull_EmitsOV0170AndDropsDeclaration()
    {
        var module = Parse("module m\nextern \"csharp\" use \"DoesNotExist\"\nfn other() -> Int { 1 }");

        ExternUseExpander.Resolver resolver = (_, _) => null;

        var result = ExternUseExpander.Expand(module, resolver);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal("OV0170", diag.Code);

        // The use declaration is dropped from the expansion; only the user's
        // own fn remains.
        var fn = Assert.IsType<FunctionDecl>(Assert.Single(result.Module.Declarations));
        Assert.Equal("other", fn.Name);
    }

    [Fact]
    public void Expand_ResolverThrows_EmitsOV0171AndDropsDeclaration()
    {
        var module = Parse("module m\nextern \"csharp\" use \"Boom\"\nfn other() -> Int { 1 }");

        ExternUseExpander.Resolver resolver = (_, _) =>
            throw new InvalidOperationException("simulated failure");

        var result = ExternUseExpander.Expand(module, resolver);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal("OV0171", diag.Code);
        Assert.Contains("simulated failure", diag.Message);
        Assert.Single(result.Module.Declarations);
    }

    [Fact]
    public void Expand_AliasedUse_ProducesSyntheticModuleAndUseDecl()
    {
        var module = Parse(
            "module m\nextern \"csharp\" use \"System.Math\" as math\nfn other() -> Int { 1 }");

        ExternUseExpander.Resolver resolver = (_, _) => """
            module __synth_math
            extern "csharp" fn pi() -> Float
                binds "System.Math.PI"
            """;

        var result = ExternUseExpander.Expand(module, resolver);
        Assert.Empty(result.Diagnostics);

        // Aliased path should NOT splice declarations into the user's
        // module; it should produce one synthetic module and one UseDecl.
        Assert.Single(result.SyntheticModules);
        Assert.Equal("__synth_math", result.SyntheticModules[0].Name);
        Assert.Contains(result.SyntheticModules[0].Ast.Declarations, d => d is ExternDecl);

        // The user's module now contains the UseDecl + the user's own fn,
        // and no extern declarations of its own.
        Assert.Equal(2, result.Module.Declarations.Length);
        var useDecl = Assert.IsType<UseDecl>(result.Module.Declarations[0]);
        Assert.Equal("__synth_math", useDecl.ModuleName);
        Assert.Equal("math", useDecl.Alias);
        var fn = Assert.IsType<FunctionDecl>(result.Module.Declarations[1]);
        Assert.Equal("other", fn.Name);

        Assert.DoesNotContain(result.Module.Declarations, d => d is ExternDecl);
    }

    [Fact]
    public void Expand_UnaliasedUse_DoesNotProduceSyntheticModules()
    {
        var module = Parse("module m\nextern \"csharp\" use \"System.Math\"");

        ExternUseExpander.Resolver resolver = (_, _) => """
            module __synth
            extern "csharp" fn pi() -> Float
                binds "System.Math.PI"
            """;

        var result = ExternUseExpander.Expand(module, resolver);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.SyntheticModules);
    }

    [Fact]
    public void Expand_ResolverReturnsMalformedSource_EmitsOV0173()
    {
        var module = Parse("module m\nextern \"csharp\" use \"Bad\"");

        ExternUseExpander.Resolver resolver = (_, _) => "this is not Overt source";

        var result = ExternUseExpander.Expand(module, resolver);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d => Assert.True(
            d.Code is "OV0172" or "OV0173",
            $"expected lex (OV0172) or parse (OV0173) error code, got {d.Code}"));
        Assert.Empty(result.Module.Declarations);
    }
}
