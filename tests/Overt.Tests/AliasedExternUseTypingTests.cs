using System.Diagnostics;
using Overt.Backend.CSharp;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Semantics;

namespace Overt.Tests;

/// <summary>
/// Regression coverage for the module-graph orchestration that surfaces
/// type information across <c>extern "csharp" use "..." as alias</c>
/// boundaries.
///
/// The failure mode this guards against: prior to the fix, the CLI's
/// CompileGraph collected imported-symbol types only for the explicitly
/// named symbols of a <c>use</c> declaration. Aliased uses carry an empty
/// ImportedSymbols list (the alias namespace is the import surface), so
/// the synthetic module's function types never reached the user module's
/// type checker. Field-access inference fell back to UnknownType, and
/// pattern emission for <c>match call() { Some(x) =&gt; ..., None =&gt; ... }</c>
/// degraded to a literal-name transcription where <c>None</c> got bound
/// as a variable. This test exercises the full pipeline (graph resolution
/// + extern-use expansion + topological type-check) and asserts the typer
/// recognises the expression's <c>Option&lt;T&gt;</c> type so the emitter
/// produces real <c>OptionSome&lt;T&gt;</c> / <c>OptionNone&lt;T&gt;</c>
/// patterns.
/// </summary>
public class AliasedExternUseTypingTests
{
    [Fact]
    public void CompileGraph_AliasedExternUse_PropagatesReturnTypeToMatchPatterns()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "overt-aliased-extern-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var entry = Path.Combine(dir, "main.ov");
            File.WriteAllText(entry, """
                module main

                extern "csharp" use "System.Int32" as int32

                fn parse_or(s: String) !{io, fails} -> Int {
                    match int32.try_parse(s = s) {
                        Some(n) => n,
                        None    => -1,
                    }
                }
                """);

            var graph = Cli.CompileGraph(entry);

            var errors = graph.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.True(errors.Count == 0,
                "compile errors: " + string.Join("; ", errors.Select(e => e.Message)));

            Assert.True(graph.TypeChecks.ContainsKey("main"),
                "expected `main` in TypeChecks; saw: "
                + string.Join(", ", graph.TypeChecks.Keys));
            var typed = graph.TypeChecks["main"];

            // The discriminant expression `int32.try_parse(s = s)` must be
            // typed as Option<Int>. Without the fix, this lands as
            // UnknownType because the synthetic module's function types
            // never reach this module's TypeChecker.
            var hasOptionInt = typed.ExpressionTypes.Values.Any(t => t.Display == "Option<Int>");
            Assert.True(hasOptionInt,
                "expected at least one expression typed as Option<Int>; saw: "
                + string.Join(", ", typed.ExpressionTypes.Values
                    .Select(t => t.Display)
                    .Distinct()));

            // End-to-end: the C# emitter now produces real variant
            // patterns, not the degraded `Some(var n) / var None` shape.
            var csharp = CSharpEmitter.Emit(typed.Module, typed);
            Assert.Contains("OptionSome<int>", csharp);
            Assert.Contains("OptionNone<int>", csharp);
            Assert.DoesNotContain("var None", csharp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
