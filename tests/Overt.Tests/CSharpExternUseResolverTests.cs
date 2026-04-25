using Overt.Backend.CSharp;

namespace Overt.Tests;

/// <summary>
/// Tests the C# back end's bridge between <c>ExternUseExpander</c>'s
/// platform-agnostic resolver delegate and <c>BindGenerator</c>. These
/// exercise the resolver against actual BCL types rather than stubs;
/// without them, the expander unit tests pass but the wiring through
/// the real backend could silently break.
/// </summary>
public class CSharpExternUseResolverTests
{
    [Fact]
    public void Resolve_KnownBclType_ReturnsOvertSourceWithExternDeclarations()
    {
        var source = CSharpExternUseResolver.Resolve("csharp", "System.Math");
        Assert.NotNull(source);

        // The resolver wraps BindGenerator output; the synthetic module name
        // should be derivable from the target.
        Assert.Contains("module __overt_extern_csharp_System_Math", source);

        // System.Math is purely static methods on primitives, so it
        // should produce extern fn declarations the parser can read.
        Assert.Contains("extern \"csharp\" fn", source);
    }

    [Fact]
    public void Resolve_UnknownTarget_ReturnsNull()
    {
        var source = CSharpExternUseResolver.Resolve("csharp", "Definitely.Not.A.Real.Type.Anywhere");
        Assert.Null(source);
    }

    [Fact]
    public void Resolve_NonCsharpPlatform_ReturnsNull()
    {
        // Other platforms (go, rust, c) are not this resolver's concern.
        // It returns null so the expander reports an unresolved-target
        // diagnostic upstream.
        var source = CSharpExternUseResolver.Resolve("go", "System.Math");
        Assert.Null(source);
    }
}
