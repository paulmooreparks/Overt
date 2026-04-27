// Process — synchronous subprocess execution. The v1 surface is one
// `run` operation that captures stdout / stderr / exit code.

namespace Overt.Runtime;

/// <summary>
/// The captured result of a synchronous Process.run invocation:
/// exit code plus stdout and stderr as strings. Field names match
/// Overt's lowercase-field convention so destructuring on the Overt
/// side reads naturally (`output.exit_code`, etc.).
/// </summary>
public sealed record ProcessOutput(int exit_code, string stdout, string stderr);

/// <summary>
/// Process companion. The v1 surface is one synchronous `run` operation
/// that captures stdout / stderr / exit code in full. Streaming I/O,
/// process groups, signals, and timeouts are deferred until a real
/// orchestration program needs them. Pairs with File / Path for the
/// minimum stdlib surface a CLI tool / build script / orchestrator
/// needs.
/// </summary>
public static class Process
{
    /// <summary>Run <paramref name="cmd"/> with the given <paramref name="args"/>,
    /// wait for it to complete, and return the captured stdout, stderr,
    /// and exit code. Failures to launch the process surface as
    /// <c>Err(IoError)</c>; a process that ran but exited non-zero is
    /// still <c>Ok</c> — callers branch on <c>output.exit_code</c>.</summary>
    public static Result<ProcessOutput, IoError> run(string cmd, List<string> args)
    {
        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(cmd)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args.Items) psi.ArgumentList.Add(a);
            using var p = global::System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return new ResultErr<ProcessOutput, IoError>(
                    new IoError($"failed to start process: {cmd}"));
            }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new ResultOk<ProcessOutput, IoError>(
                new ProcessOutput(p.ExitCode, stdout, stderr));
        }
        catch (Exception ex)
        {
            return new ResultErr<ProcessOutput, IoError>(new IoError(ex.Message));
        }
    }
}
