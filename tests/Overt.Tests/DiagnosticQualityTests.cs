using Overt.Compiler.Diagnostics;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Verifies that diagnostics carry enough context to be actionable, not just
/// locating. Each test both confirms the primary message and checks that a
/// <see cref="DiagnosticNote"/> is attached with either a fix or a cross-reference.
/// </summary>
public class DiagnosticQualityTests
{
    private static Diagnostic ResolveAndFindFirst(string source, string code)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var resolve = NameResolver.Resolve(parse.Module);
        var all = lex.Diagnostics
            .AddRange(parse.Diagnostics)
            .AddRange(resolve.Diagnostics);
        return Assert.Single(all, d => d.Code == code);
    }

    [Fact]
    public void OV0200_UnknownName_OffersDidYouMean()
    {
        var d = ResolveAndFindFirst(
            "module m\nfn main() -> Int { let counter = 0; countr + 1 }",
            "OV0200");

        Assert.Single(d.Notes);
        var note = d.Notes[0];
        Assert.Equal(DiagnosticNoteKind.Help, note.Kind);
        Assert.Contains("counter", note.Text);
    }

    [Fact]
    public void OV0200_UnknownName_NoSuggestion_WhenTooFar()
    {
        var d = ResolveAndFindFirst(
            "module m\nfn main() -> Int { let counter = 0; zzzzzzzz }",
            "OV0200");

        // No suggestion when no name is within the Levenshtein budget.
        Assert.Empty(d.Notes);
    }

    [Fact]
    public void OV0201_DuplicateBinding_PointsAtOriginalDeclaration()
    {
        var d = ResolveAndFindFirst(
            "module m\nfn f() -> Int { 1 }\nfn f() -> Int { 2 }",
            "OV0201");

        Assert.Equal(2, d.Notes.Length);
        var crossRef = Assert.Single(d.Notes, n => n.Kind == DiagnosticNoteKind.Note);
        Assert.NotNull(crossRef.Span);
        // The original declaration is on line 2; the diagnostic itself points at line 3.
        Assert.Equal(2, crossRef.Span!.Value.Start.Line);
        Assert.Equal(3, d.Span.Start.Line);
    }

    [Fact]
    public void OV0155_ExpectedExpression_ListsStartingForms()
    {
        var d = ResolveAndFindFirst(
            "module m\nfn f() -> Int { let x = ; 42 }",
            "OV0155");

        var help = Assert.Single(d.Notes, n => n.Kind == DiagnosticNoteKind.Help);
        Assert.Contains("identifier", help.Text);
        Assert.Contains("literal", help.Text);
    }

    [Fact]
    public void OV0158_ExpectedPattern_ListsPatternForms()
    {
        var d = ResolveAndFindFirst(
            "module m\nfn f() { match x { + => 1 } }",
            "OV0158");

        var help = Assert.Single(d.Notes, n => n.Kind == DiagnosticNoteKind.Help);
        Assert.Contains("tuple", help.Text);
    }

    [Fact]
    public void OV0315_ExternInstance_RequiresSelf()
    {
        ResolveAndFindFirst(
            "module m\n"
            + "extern \"csharp\" type T binds \"System.Object\"\n"
            + "extern \"csharp\" instance fn bad(x: T) -> Int\n"
            + "    binds \"System.Object.GetHashCode\"\n",
            "OV0315");
    }

    [Fact]
    public void OV0316_ExternCtor_RequiresReturnType()
    {
        ResolveAndFindFirst(
            "module m\n"
            + "extern \"csharp\" ctor fn bad()\n"
            + "    binds \"System.Object\"\n",
            "OV0316");
    }
}
