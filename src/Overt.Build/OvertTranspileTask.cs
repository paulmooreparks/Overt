using System.Collections.Immutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Overt.Backend.CSharp;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Build;

/// <summary>
/// MSBuild task that transpiles <c>.ov</c> source files to C# and exposes
/// the generated paths back to the build so they can be fed to Csc as
/// additional <c>Compile</c> items.
///
/// Each source is lexed, parsed, name-resolved, type-checked, and emitted
/// via <see cref="CSharpEmitter"/>. Compile-time diagnostics surface as
/// MSBuild errors/warnings so they appear in the normal build log with
/// file/line information. One source file maps to one output file under
/// <see cref="OutputDirectory"/>, named <c>&lt;input&gt;.g.cs</c>.
///
/// Cross-file module resolution isn't wired yet — each .ov is checked in
/// isolation. That matches the CLI's non-<c>run</c> modes today and keeps
/// the task focused on the emission boundary.
/// </summary>
public sealed class OvertTranspileTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string OutputDirectory { get; set; } = "";

    [Output]
    public ITaskItem[] GeneratedFiles { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            Directory.CreateDirectory(OutputDirectory);
        }

        var generated = new List<ITaskItem>(SourceFiles.Length);
        var anyErrors = false;

        foreach (var item in SourceFiles)
        {
            var sourcePath = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(sourcePath)) sourcePath = item.ItemSpec;

            var source = File.ReadAllText(sourcePath);
            var lex = Lexer.Lex(source);
            var parse = Parser.Parse(lex.Tokens);
            var resolved = NameResolver.Resolve(parse.Module);
            var typed = TypeChecker.Check(parse.Module, resolved);

            var allDiagnostics = lex.Diagnostics
                .AddRange(parse.Diagnostics)
                .AddRange(resolved.Diagnostics)
                .AddRange(typed.Diagnostics);

            foreach (var d in allDiagnostics)
            {
                ReportDiagnostic(d, sourcePath);
                if (d.Severity == DiagnosticSeverity.Error) anyErrors = true;
            }

            if (anyErrors) continue; // skip emission; errors are the user's fix

            var csharp = CSharpEmitter.Emit(parse.Module, typed, sourcePath);

            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var outputPath = Path.Combine(OutputDirectory, baseName + ".g.cs");
            File.WriteAllText(outputPath, csharp);

            generated.Add(new TaskItem(outputPath));
        }

        GeneratedFiles = generated.ToArray();
        return !anyErrors;
    }

    private void ReportDiagnostic(Diagnostic d, string sourcePath)
    {
        var line = d.Span.Start.Line;
        var col = d.Span.Start.Column;
        var endLine = d.Span.End.Line;
        var endCol = d.Span.End.Column;

        if (d.Severity == DiagnosticSeverity.Error)
        {
            Log.LogError(
                subcategory: null,
                errorCode: d.Code,
                helpKeyword: null,
                file: sourcePath,
                lineNumber: line,
                columnNumber: col,
                endLineNumber: endLine,
                endColumnNumber: endCol,
                message: d.Message);
        }
        else
        {
            Log.LogWarning(
                subcategory: null,
                warningCode: d.Code,
                helpKeyword: null,
                file: sourcePath,
                lineNumber: line,
                columnNumber: col,
                endLineNumber: endLine,
                endColumnNumber: endCol,
                message: d.Message);
        }
    }
}
