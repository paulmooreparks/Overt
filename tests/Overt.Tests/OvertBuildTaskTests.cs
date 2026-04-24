using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Overt.Build;

namespace Overt.Tests;

/// <summary>
/// Direct-invocation tests for <see cref="OvertTranspileTask"/>. Don't spin up
/// a real MSBuild; stub out <see cref="IBuildEngine"/> and observe outputs.
/// Separate from the end-to-end dotnet-build smoke (<see cref="OvertBuildEndToEndTests"/>)
/// so a task regression is isolated from target-wiring regressions.
/// </summary>
public class OvertBuildTaskTests
{
    [Fact]
    public void Transpile_ValidSource_EmitsGeneratedCsAndReportsNoErrors()
    {
        var tmp = Path.Combine(Path.GetTempPath(),
            "overt-build-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            var srcPath = Path.Combine(tmp, "sample.ov");
            File.WriteAllText(srcPath, "module sample\nfn answer() -> Int { 42 }\n");

            var outDir = Path.Combine(tmp, "out");
            var engine = new RecordingBuildEngine();
            var task = new OvertTranspileTask
            {
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem(srcPath) },
                OutputDirectory = outDir,
            };

            var ok = task.Execute();

            Assert.True(ok, "task should succeed on valid source");
            Assert.Empty(engine.Errors);
            var generated = Assert.Single(task.GeneratedFiles);
            var genPath = generated.ItemSpec;
            Assert.True(File.Exists(genPath), $"expected generated file at {genPath}");
            var contents = File.ReadAllText(genPath);
            Assert.Contains("namespace Overt.Generated.Sample", contents);
            Assert.Contains("answer", contents);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Transpile_SourceWithTypeError_FailsAndReportsDiagnostic()
    {
        var tmp = Path.Combine(Path.GetTempPath(),
            "overt-build-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            var srcPath = Path.Combine(tmp, "bad.ov");
            // `let` without type annotation — OV0314
            File.WriteAllText(srcPath,
                "module bad\nfn f() -> Int { let x = 1\n x }\n");

            var engine = new RecordingBuildEngine();
            var task = new OvertTranspileTask
            {
                BuildEngine = engine,
                SourceFiles = new ITaskItem[] { new TaskItem(srcPath) },
                OutputDirectory = Path.Combine(tmp, "out"),
            };

            var ok = task.Execute();

            Assert.False(ok, "task should fail when the module has errors");
            Assert.Contains(engine.Errors, e => e.Code == "OV0314");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = new();
        public List<BuildWarningEventArgs> Warnings { get; } = new();

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test.proj";

        public bool BuildProjectFile(string projectFileName, string[] targetNames,
            System.Collections.IDictionary globalProperties,
            System.Collections.IDictionary targetOutputs) => true;

        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
    }
}
