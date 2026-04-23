using System.Collections.Immutable;
using Overt.Compiler.Modules;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Tests;

/// <summary>
/// Multi-file compilation: `use module.{sym1, sym2}` pulls names from a
/// sibling `.ov` file into the importing module's scope. Tests here
/// exercise ModuleGraph, the resolver's import-threading, and the
/// type-checker's imported-symbol-type seeding without going through the
/// full Roslyn compile (that's covered by the transpile-and-run tests).
/// </summary>
public class ModuleImportTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "overt-modtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path);
        }
        public void Write(string file, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, file), content);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void Graph_ResolvesSiblingModule()
    {
        using var tmp = new TempDir();
        tmp.Write("helper.ov", """
            module helper

            fn add_one(x: Int) -> Int { x + 1 }
            """);
        tmp.Write("main.ov", """
            module app

            use helper.{add_one}

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = add_one(11)
                Ok(())
            }
            """);

        var graph = ModuleGraph.Resolve(
            System.IO.Path.Combine(tmp.Path, "main.ov"),
            ImmutableArray<string>.Empty);

        Assert.Empty(graph.Diagnostics);
        Assert.Equal(2, graph.Modules.Length);
        // Topological order: imports first, entry last.
        Assert.Equal("helper", graph.Modules[0].Name);
        Assert.Equal("app", graph.Modules[1].Name);
    }

    [Fact]
    public void Graph_MissingModuleReportsDiagnostic()
    {
        using var tmp = new TempDir();
        tmp.Write("main.ov", """
            module app

            use nonexistent.{something}

            fn main() -> Int { 0 }
            """);

        var graph = ModuleGraph.Resolve(
            System.IO.Path.Combine(tmp.Path, "main.ov"),
            ImmutableArray<string>.Empty);

        Assert.Contains(graph.Diagnostics, d => d.Code == "OV0167");
    }

    [Fact]
    public void Resolver_ImportedSymbolResolvesInUseSite()
    {
        using var tmp = new TempDir();
        tmp.Write("helper.ov", """
            module helper

            fn add_one(x: Int) -> Int { x + 1 }
            """);
        tmp.Write("main.ov", """
            module app

            use helper.{add_one}

            fn main() !{io} -> Result<(), IoError> {
                let n: Int = add_one(11)
                Ok(())
            }
            """);

        var graph = ModuleGraph.Resolve(
            System.IO.Path.Combine(tmp.Path, "main.ov"),
            ImmutableArray<string>.Empty);
        Assert.Empty(graph.Diagnostics);

        // Resolve helper first to collect its exports.
        var helper = graph.Modules.First(m => m.Name == "helper");
        var helperResolved = NameResolver.Resolve(helper.Ast);
        Assert.Empty(helperResolved.Diagnostics);
        var helperTyped = TypeChecker.Check(helper.Ast, helperResolved);
        Assert.Empty(helperTyped.Diagnostics);

        // Build the importable-modules table the way Program.cs does.
        var addOneSym = helper.Ast.Declarations.OfType<FunctionDecl>()
            .First(f => f.Name == "add_one");
        var helperSym = new Symbol(SymbolKind.Function, "add_one", addOneSym.Span, addOneSym);
        var importables = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, Symbol>>(
            StringComparer.Ordinal);
        importables["helper"] = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new[] { KeyValuePair.Create("add_one", helperSym) });

        // Now resolve main with imports available. The `add_one(11)` call in
        // main should not produce OV0200 (unknown name).
        var app = graph.Modules.First(m => m.Name == "app");
        var appResolved = NameResolver.Resolve(app.Ast, importables.ToImmutable());
        Assert.DoesNotContain(appResolved.Diagnostics, d => d.Code == "OV0200");
    }

    [Fact]
    public void Graph_DottedPathWalksDirectories()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "stdlib", "http"));
        tmp.Write(Path.Combine("stdlib", "http", "client.ov"), """
            module stdlib.http.client

            fn get(url: String) -> String { url }
            """);
        tmp.Write("main.ov", """
            module app

            use stdlib.http.client.{get}

            fn main() -> Int { 0 }
            """);

        var graph = ModuleGraph.Resolve(
            Path.Combine(tmp.Path, "main.ov"),
            ImmutableArray<string>.Empty);
        Assert.Empty(graph.Diagnostics);
        Assert.Equal(2, graph.Modules.Length);
        // Dotted module name survives the parse + the file's directory walk.
        Assert.Contains(graph.Modules, m => m.Name == "stdlib.http.client");
    }

    [Fact]
    public void Parser_AliasedUseProducesAlias()
    {
        const string src = """
            module app

            use stdlib.http.client as http

            fn main() -> Int { 0 }
            """;
        var lex = Lexer.Lex(src);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Empty(parse.Diagnostics);
        var use = parse.Module.Declarations.OfType<UseDecl>().Single();
        Assert.Equal("stdlib.http.client", use.ModuleName);
        Assert.Equal("http", use.Alias);
        Assert.Empty(use.ImportedSymbols);
    }

    [Fact]
    public void Parser_WildcardImportReportsOV0163()
    {
        const string src = """
            module app

            use foo

            fn main() -> Int { 0 }
            """;
        var lex = Lexer.Lex(src);
        var parse = Parser.Parse(lex.Tokens);
        Assert.Contains(parse.Diagnostics, d => d.Code == "OV0163");
    }

    [Fact]
    public void Resolver_UnknownImportReportsOV0168()
    {
        using var tmp = new TempDir();
        tmp.Write("helper.ov", """
            module helper

            fn add_one(x: Int) -> Int { x + 1 }
            """);
        tmp.Write("main.ov", """
            module app

            use helper.{nonexistent_symbol}

            fn main() -> Int { 0 }
            """);

        var graph = ModuleGraph.Resolve(
            System.IO.Path.Combine(tmp.Path, "main.ov"),
            ImmutableArray<string>.Empty);

        // Simulate the per-module resolver the CLI runs: resolve helper first.
        var helper = graph.Modules.First(m => m.Name == "helper");
        var helperResolved = NameResolver.Resolve(helper.Ast);

        var addOneSym = helper.Ast.Declarations.OfType<FunctionDecl>()
            .First(f => f.Name == "add_one");
        var sym = new Symbol(SymbolKind.Function, "add_one", addOneSym.Span, addOneSym);
        var importables = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new[] { KeyValuePair.Create(
                "helper",
                ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    new[] { KeyValuePair.Create("add_one", sym) })) });

        var app = graph.Modules.First(m => m.Name == "app");
        var appResolved = NameResolver.Resolve(app.Ast, importables);
        Assert.Contains(appResolved.Diagnostics, d => d.Code == "OV0168");
    }
}
