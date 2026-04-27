package overt

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"sync"
)

// Println writes a line to stdout. Always appends a single '\n', so
// the caller's argument should not contain a trailing newline.
// Returns Result[Unit, IoError] to match the Overt signature
// `println(s: String) !{io} -> Result<(), IoError>`.
func Println(s string) Result[Unit, IoError] {
	if _, err := fmt.Fprintln(os.Stdout, s); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// Eprintln is the stderr twin of Println, with the same shape.
func Eprintln(s string) Result[Unit, IoError] {
	if _, err := fmt.Fprintln(os.Stderr, s); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// Print is Println without the trailing newline. Common for progress
// indicators, prompts, and "running test... done." patterns.
func Print(s string) Result[Unit, IoError] {
	if _, err := fmt.Fprint(os.Stdout, s); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// Eprint is the stderr twin of Print.
func Eprint(s string) Result[Unit, IoError] {
	if _, err := fmt.Fprint(os.Stderr, s); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// ReadLine reads one line from stdin. The trailing '\n' (and the '\r'
// on Windows) is stripped; an empty line returns Some(""). EOF returns
// None; I/O errors return Err.
func ReadLine() Result[Option[string], IoError] {
	reader := getStdinReader()
	line, err := reader.ReadString('\n')
	if len(line) > 0 {
		// Trim trailing newline / CRLF.
		if line[len(line)-1] == '\n' {
			line = line[:len(line)-1]
		}
		if len(line) > 0 && line[len(line)-1] == '\r' {
			line = line[:len(line)-1]
		}
		return Ok[Option[string], IoError](Some(line))
	}
	if err != nil && err.Error() == "EOF" {
		return Ok[Option[string], IoError](None[string]())
	}
	if err != nil {
		return Err[Option[string], IoError](IoError{Narrative: err.Error()})
	}
	// Empty line at EOF without newline.
	return Ok[Option[string], IoError](None[string]())
}

// ReadToEnd consumes all of stdin as a single string. Standard
// `cat file | tool` pipe-consumer pattern.
func ReadToEnd() Result[string, IoError] {
	data, err := io.ReadAll(os.Stdin)
	if err != nil {
		return Err[string, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[string, IoError](string(data))
}

// Lazy buffered stdin reader, shared across ReadLine calls so the
// buffered reader's leftover bytes survive between reads.
var (
	stdinReader     *bufio.Reader
	stdinReaderOnce sync.Once
)

func getStdinReader() *bufio.Reader {
	stdinReaderOnce.Do(func() {
		stdinReader = bufio.NewReader(os.Stdin)
	})
	return stdinReader
}
