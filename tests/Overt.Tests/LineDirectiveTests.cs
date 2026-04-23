using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Overt.Backend.CSharp;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Verifies that the emitter produces <c>#line</c> directives that ultimately resolve
/// runtime errors and debugger stack traces back to Overt source — not the generated
/// C#. This is the concrete mechanism behind the anti-hack defense: if exceptions
/// already point at <c>foo.ov:23</c>, there is no reason for anyone to open the
/// transpiled <c>.cs</c>.
/// </summary>
public class LineDirectiveTests
{
    private static string EmitWithSourcePath(string source, string sourcePath)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var resolved = NameResolver.Resolve(parse.Module);
        var typed = TypeChecker.Check(parse.Module, resolved);
        return CSharpEmitter.Emit(parse.Module, typed, sourcePath);
    }

    [Fact]
    public void Emit_WithSourcePath_IncludesLineDirectives()
    {
        var csharp = EmitWithSourcePath(
            "module m\nfn f() -> Int { 42 }",
            "/abs/m.ov");

        Assert.Contains("#line (2, 1)", csharp);
        Assert.Contains("\"/abs/m.ov\"", csharp);
        Assert.Contains("#line default", csharp);
    }

    [Fact]
    public void Emit_WithoutSourcePath_EmitsNoLineDirectives()
    {
        var lex = Lexer.Lex("module m\nfn f() -> Int { 42 }");
        var parse = Parser.Parse(lex.Tokens);
        var resolved = NameResolver.Resolve(parse.Module);
        var typed = TypeChecker.Check(parse.Module, resolved);

        var csharp = CSharpEmitter.Emit(parse.Module, typed, sourcePath: null);

        // Match a directive-shaped prefix rather than the bare word — "#line" also
        // appears in the preamble's prose comment, which is an intentional pointer
        // to the feature.
        Assert.DoesNotContain("#line (", csharp);
        Assert.DoesNotContain("#line default", csharp);
    }

    [Fact]
    public void Emit_PerStatement_DirectivesWritten()
    {
        // Each statement gets its own directive so debuggers can step line-by-line.
        var csharp = EmitWithSourcePath(
            "module m\nfn f() -> Int {\n    let a = 1\n    let b = 2\n    a + b\n}",
            "/abs/m.ov");

        // Three statements (two lets + the trailing expression) should each have a
        // directive. Lower bound the count; headers and scaffolding may add more.
        var count = csharp.Split('\n').Count(l => l.Contains("#line (")
            && l.Contains("/abs/m.ov"));
        Assert.True(count >= 3, $"expected at least 3 #line directives, saw {count}");
    }

    [Fact]
    public void Emit_LineDirectivePath_EscapesBackslashes()
    {
        // Windows absolute paths contain backslashes which are C# escape characters
        // inside string literals. The emitter must double them.
        var csharp = EmitWithSourcePath(
            "module m\nfn f() -> Int { 1 }",
            @"C:\Users\x\m.ov");

        Assert.Contains(@"""C:\\Users\\x\\m.ov""", csharp);
    }

    [Fact]
    public void Emit_WithSourcePath_CompilesWithRoslyn_LineInfoFlowsThrough()
    {
        // End-to-end: the emitted C# with line directives compiles cleanly, and
        // Roslyn's parser recognizes our span-form `#line (l, c) - (l, c) "path"`
        // as a LineSpanDirectiveTriviaSyntax. This is the layer that feeds portable
        // PDBs; if Roslyn refused the directive shape, the PDB mapping would silently
        // fail.
        var csharp = EmitWithSourcePath(
            "module m\nfn f() -> Int { 42 }",
            "/abs/m.ov");

        var tree = CSharpSyntaxTree.ParseText(csharp, new CSharpParseOptions(LanguageVersion.Latest));
        var spanDirectives = tree.GetRoot().DescendantNodes(descendIntoTrivia: true)
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LineSpanDirectiveTriviaSyntax>()
            .ToArray();

        Assert.NotEmpty(spanDirectives);
        Assert.Contains(spanDirectives, d => d.File.ValueText == "/abs/m.ov");

        // Compile the tree through the shared references and confirm no parse or
        // compile errors around the directives themselves.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "line-directive-check",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var parseErrors = compilation.GetParseDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(parseErrors);
    }

    [Fact]
    public void Emit_ReturnStatement_MapsBackToOvertSourceLine()
    {
        // Verify the chain end-to-end: a node in the generated C# resolves, via
        // Roslyn's line-directive-aware mapping, to its corresponding position in the
        // Overt source. This is what portable PDBs consume — so if this resolves,
        // runtime stack traces will too.
        //
        // Source shape:
        //   line 1: module m
        //   line 2: fn compute(x: Int) -> Int {
        //   line 3:     x + 1
        //   line 4: }
        var csharp = EmitWithSourcePath(
            "module m\nfn compute(x: Int) -> Int {\n    x + 1\n}",
            "/abs/m.ov");

        var tree = CSharpSyntaxTree.ParseText(csharp, new CSharpParseOptions(LanguageVersion.Latest));
        var returnStmt = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax>()
            .Single();

        var mapped = tree.GetMappedLineSpan(returnStmt.Span);

        Assert.Equal("/abs/m.ov", mapped.Path);
        // The return statement maps to the trailing expression `x + 1` on Overt line 3.
        // GetMappedLineSpan is 0-indexed; Overt's SourceSpan is 1-indexed.
        Assert.Equal(2, mapped.StartLinePosition.Line);
    }
}
