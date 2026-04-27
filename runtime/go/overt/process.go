package overt

import (
	"bytes"
	"os"
	"os/exec"
)

// ProcessOutput is the captured result of a synchronous Process.run
// invocation: exit code plus stdout / stderr as strings. Field names
// are TitleCase per Go's exported-name convention; the Go emitter's
// stdlib-record field-access translation maps Overt's lowercase
// field references (`output.exit_code`) to the matching capitalized
// Go fields.
type ProcessOutput struct {
	ExitCode int
	Stdout   string
	Stderr   string
}

// ProcessRun runs cmd with the given args, blocks until it completes,
// and returns the captured outputs. A process that fails to launch
// surfaces as Err(IoError); a process that ran and exited non-zero
// is still Ok — the caller branches on output.exit_code.
func ProcessRun(cmd string, args List[string]) Result[ProcessOutput, IoError] {
	c := exec.Command(cmd, args.Items...)
	var stdoutBuf, stderrBuf bytes.Buffer
	c.Stdout = &stdoutBuf
	c.Stderr = &stderrBuf
	err := c.Run()
	// A non-zero exit surfaces as *exec.ExitError; the process did
	// run, just unhappily. Other error shapes (binary not found,
	// permission denied launching) are launch failures and surface
	// as Err.
	if err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			return Ok[ProcessOutput, IoError](ProcessOutput{
				ExitCode: exitErr.ExitCode(),
				Stdout:   stdoutBuf.String(),
				Stderr:   stderrBuf.String(),
			})
		}
		return Err[ProcessOutput, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[ProcessOutput, IoError](ProcessOutput{
		ExitCode: 0,
		Stdout:   stdoutBuf.String(),
		Stderr:   stderrBuf.String(),
	})
}

// Args returns the process command-line arguments minus the executable
// path that os.Args puts at index 0. Mirrors the C# runtime's Prelude.args()
// — both targets observe the same shape so a program reading argv via
// `args()` stdlib gets identical behavior across back ends. Returns the
// empty List when there are no user-supplied args. Effect-row-tracked
// `!{io}` because it observes process state.
func Args() List[string] {
	raw := os.Args
	if len(raw) <= 1 {
		return List[string]{Items: []string{}}
	}
	out := make([]string, len(raw)-1)
	copy(out, raw[1:])
	return List[string]{Items: out}
}
