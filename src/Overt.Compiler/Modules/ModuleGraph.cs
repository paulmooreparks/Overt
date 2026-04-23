using System.Collections.Immutable;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Modules;

/// <summary>
/// Resolves a multi-file Overt compilation unit.
///
/// Starting from an entry <c>.ov</c> file, walks its <c>use</c> declarations,
/// locates each imported module on disk by looking for <c>&lt;name&gt;.ov</c>
/// in a configured search path, pre-lexes and pre-parses it, and recurses.
/// The result is a topologically-ordered list of modules (imports before
/// dependents) plus any lex/parse diagnostics encountered along the way.
///
/// Cycle detection: a strict acyclic graph is required per DESIGN.md §19.
/// Encountering a cycle adds an error diagnostic but doesn't throw.
///
/// MVP constraints:
/// <list type="bullet">
///   <item>Module names are single-segment (dotted paths like
///     <c>stdlib.http.client</c> are future work).</item>
///   <item>Search path is a list of directories; first match wins.</item>
///   <item>Missing or unreadable files produce a diagnostic, not an
///     exception.</item>
/// </list>
/// </summary>
public static class ModuleGraph
{
    public sealed record LoadedModule(
        string Name,
        string SourcePath,
        string Source,
        ImmutableArray<Token> Tokens,
        ModuleDecl Ast);

    public sealed record Result(
        ImmutableArray<LoadedModule> Modules,
        ImmutableArray<Diagnostic> Diagnostics);

    /// <summary>Resolve a module graph starting from <paramref name="entryPath"/>.
    /// <paramref name="searchDirs"/> holds extra directories to check for
    /// imported modules; the entry file's own directory is searched first.
    /// </summary>
    public static Result Resolve(string entryPath, ImmutableArray<string> searchDirs)
    {
        var diagnostics = new List<Diagnostic>();
        var loaded = new Dictionary<string, LoadedModule>(StringComparer.Ordinal);
        var order = new List<LoadedModule>();
        var inProgress = new HashSet<string>(StringComparer.Ordinal);

        var entryDir = Path.GetDirectoryName(Path.GetFullPath(entryPath)) ?? ".";
        var effectiveDirs = ImmutableArray.Create(entryDir).AddRange(searchDirs);

        Load(entryPath, isEntry: true);

        return new Result(order.ToImmutableArray(), diagnostics.ToImmutableArray());

        LoadedModule? Load(string path, bool isEntry)
        {
            var absolute = Path.GetFullPath(path);
            if (loaded.TryGetValue(absolute, out var already)) return already;
            if (!inProgress.Add(absolute))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0164",
                    $"circular import detected at {path}",
                    new SourceSpan(new SourcePosition(0, 0), new SourcePosition(0, 0)),
                    ImmutableArray<DiagnosticNote>.Empty));
                return null;
            }

            string source;
            try
            {
                source = File.ReadAllText(absolute);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    isEntry ? "OV0165" : "OV0166",
                    isEntry
                        ? $"cannot read entry file '{path}': {ex.Message}"
                        : $"cannot read imported module '{path}': {ex.Message}",
                    new SourceSpan(new SourcePosition(0, 0), new SourcePosition(0, 0)),
                    ImmutableArray<DiagnosticNote>.Empty));
                inProgress.Remove(absolute);
                return null;
            }

            var lex = Lexer.Lex(source);
            diagnostics.AddRange(lex.Diagnostics);
            var parse = Parser.Parse(lex.Tokens);
            diagnostics.AddRange(parse.Diagnostics);

            // Walk this module's `use` declarations before marking us as loaded,
            // so our dependencies sit earlier in the topological order.
            foreach (var decl in parse.Module.Declarations.OfType<UseDecl>())
            {
                var resolvedPath = ResolveModulePath(decl.ModuleName, effectiveDirs);
                if (resolvedPath is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "OV0167",
                        $"cannot find module '{decl.ModuleName}' in search path",
                        decl.Span,
                        ImmutableArray.Create(new DiagnosticNote(
                            DiagnosticNoteKind.Help,
                            "place " + decl.ModuleName + ".ov beside the importing file "
                                + "or add its directory to the search path",
                            null))));
                    continue;
                }
                Load(resolvedPath, isEntry: false);
            }

            var entry = new LoadedModule(
                parse.Module.Name,
                absolute,
                source,
                lex.Tokens,
                parse.Module);
            loaded[absolute] = entry;
            inProgress.Remove(absolute);
            order.Add(entry);
            return entry;
        }
    }

    private static string? ResolveModulePath(string moduleName, ImmutableArray<string> searchDirs)
    {
        var fileName = moduleName + ".ov";
        foreach (var dir in searchDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
