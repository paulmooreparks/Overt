package overt

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// FileReadToString reads the file at path as UTF-8 and returns its
// contents as a Result. Errors (not found, permission, encoding)
// surface as Err with the host's error message in the IoError
// narrative — same convention as the C# runtime, so a program reading
// the same path against the same file gets equivalent telemetry on
// either back end.
func FileReadToString(path string) Result[string, IoError] {
	bytes, err := os.ReadFile(path)
	if err != nil {
		return Err[string, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[string, IoError](string(bytes))
}

// FileWriteAllText writes contents to path as UTF-8, overwriting any
// existing file. Permissions are 0644 (rw-r--r--), matching the C#
// runtime's File.WriteAllText default.
func FileWriteAllText(path string, contents string) Result[Unit, IoError] {
	if err := os.WriteFile(path, []byte(contents), 0644); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// FileExists is true iff path names an existing file (not a directory).
// A directory at the path returns false; an unreadable path also
// returns false (the OS-level distinguishing of "doesn't exist" vs
// "exists but unreadable" is a sharper edge than v1 carves out).
func FileExists(path string) bool {
	info, err := os.Stat(path)
	if err != nil {
		return false
	}
	return !info.IsDir()
}

// FileReadLines reads the file as UTF-8 and splits on newlines. Each
// line excludes the trailing '\n' (and '\r\n' on Windows). The final
// line is included even without a trailing newline. Empty file →
// empty list.
func FileReadLines(path string) Result[List[string], IoError] {
	bytes, err := os.ReadFile(path)
	if err != nil {
		return Err[List[string], IoError](IoError{Narrative: err.Error()})
	}
	if len(bytes) == 0 {
		return Ok[List[string], IoError](List[string]{Items: []string{}})
	}
	// Strip a single trailing newline so the split doesn't add a
	// spurious empty trailing element. CRLF or LF.
	body := string(bytes)
	if strings.HasSuffix(body, "\r\n") {
		body = body[:len(body)-2]
	} else if strings.HasSuffix(body, "\n") {
		body = body[:len(body)-1]
	}
	// Split on '\n', then trim a trailing '\r' on each (Windows CRLF).
	parts := strings.Split(body, "\n")
	for i, p := range parts {
		if strings.HasSuffix(p, "\r") {
			parts[i] = p[:len(p)-1]
		}
	}
	return Ok[List[string], IoError](List[string]{Items: parts})
}

// FileAppendText appends contents to path (UTF-8). Creates the file
// if missing. Default mode 0644 to match FileWriteAllText.
func FileAppendText(path string, contents string) Result[Unit, IoError] {
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_APPEND|os.O_CREATE, 0644)
	if err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	defer f.Close()
	if _, err := f.WriteString(contents); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// FileDelete removes the file. Deleting a non-existent file is a no-op
// (matches .NET File.Delete and `rm -f` style — programs that want
// "missing" diagnostics guard with FileExists first).
func FileDelete(path string) Result[Unit, IoError] {
	if err := os.Remove(path); err != nil {
		// IsNotExist → silent success, matching .NET semantics.
		if os.IsNotExist(err) {
			return Ok[Unit, IoError](UnitValue)
		}
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// FileSize returns the file's byte count, clamped to Int range. Files
// larger than ~2 GB return Err with a guidance message — programs
// that need them FFI to os.FileInfo directly.
func FileSize(path string) Result[int, IoError] {
	info, err := os.Stat(path)
	if err != nil {
		return Err[int, IoError](IoError{Narrative: err.Error()})
	}
	if info.Size() > int64(^uint(0)>>1) /* int max */ {
		return Err[int, IoError](IoError{Narrative: fmt.Sprintf(
			"File.size: file %q exceeds Int range (%d bytes); use FFI for large files",
			path, info.Size())})
	}
	return Ok[int, IoError](int(info.Size()))
}

// FileMove renames from → to. Atomic on the same filesystem; cross-
// filesystem behavior is OS-specific (Linux: EXDEV, error; macOS / Windows:
// fall back). Programs needing strict cross-fs semantics handle the
// error themselves.
func FileMove(from string, to string) Result[Unit, IoError] {
	if err := os.Rename(from, to); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// FileReadBytes reads the file as raw bytes. Pairs with
// BytesFromUtf8 / BytesToUtf8 when round-tripping text.
func FileReadBytes(path string) Result[Bytes, IoError] {
	data, err := os.ReadFile(path)
	if err != nil {
		return Err[Bytes, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Bytes, IoError](Bytes{Items: data})
}

// FileWriteBytes writes raw bytes, overwriting any existing file.
// Default mode 0644 to match FileWriteAllText.
func FileWriteBytes(path string, data Bytes) Result[Unit, IoError] {
	if err := os.WriteFile(path, data.Items, 0644); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// FileCopy copies src → dst. Existing dst is overwritten. Mode bits
// are not preserved beyond what the new file's umask allows; programs
// that need source-mode preservation use FFI to os.Chmod after copy.
func FileCopy(from string, to string) Result[Unit, IoError] {
	srcBytes, err := os.ReadFile(from)
	if err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	if err := os.WriteFile(to, srcBytes, 0644); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// DirectoryExists is the directory-existence predicate (distinct from
// FileExists, which excludes directories).
func DirectoryExists(path string) bool {
	info, err := os.Stat(path)
	if err != nil {
		return false
	}
	return info.IsDir()
}

// DirectoryCreate creates the directory, including any missing
// parents. Mode 0755 (rwxr-xr-x) by default.
func DirectoryCreate(path string) Result[Unit, IoError] {
	if err := os.MkdirAll(path, 0755); err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// DirectoryList returns the entry names in the directory (file and
// subdirectory names, not full paths). Order is filesystem-dependent
// and not stable across hosts.
func DirectoryList(path string) Result[List[string], IoError] {
	entries, err := os.ReadDir(path)
	if err != nil {
		return Err[List[string], IoError](IoError{Narrative: err.Error()})
	}
	out := make([]string, len(entries))
	for i, e := range entries {
		out[i] = e.Name()
	}
	return Ok[List[string], IoError](List[string]{Items: out})
}

// DirectoryDelete removes the directory. recursive=true removes all
// contents (rm -r style); false requires the directory to be empty
// (rmdir style).
func DirectoryDelete(path string, recursive bool) Result[Unit, IoError] {
	var err error
	if recursive {
		err = os.RemoveAll(path)
	} else {
		err = os.Remove(path)
	}
	if err != nil {
		return Err[Unit, IoError](IoError{Narrative: err.Error()})
	}
	return Ok[Unit, IoError](UnitValue)
}

// PathJoin joins two path segments with the platform-appropriate
// separator. Mirrors C# Path.Combine semantics.
func PathJoin(parent string, child string) string {
	return filepath.Join(parent, child)
}

// PathParent returns the directory portion of path, or None when
// the path has no parent.
func PathParent(path string) Option[string] {
	dir := filepath.Dir(path)
	// filepath.Dir returns "." for paths with no directory, "/" for
	// the root, and the path itself for unrooted single segments.
	// Treat "." (no parent) as None to match the C# runtime's
	// "string.IsNullOrEmpty(GetDirectoryName)" behavior.
	if dir == "." || dir == "" {
		return None[string]()
	}
	return Some(dir)
}

// PathFileName returns the final segment of path, or None for the
// empty string. Mirrors filepath.Base except None instead of "."
// for paths consisting only of separators.
func PathFileName(path string) Option[string] {
	if path == "" {
		return None[string]()
	}
	name := filepath.Base(path)
	if name == "." || name == string(filepath.Separator) {
		return None[string]()
	}
	return Some(name)
}

// PathExtension returns the file extension including the leading
// dot (e.g. ".ov"), or None when the path has no extension.
func PathExtension(path string) Option[string] {
	ext := filepath.Ext(path)
	if ext == "" {
		return None[string]()
	}
	return Some(ext)
}

// PathWithExtension replaces (or adds) the extension on path. The
// supplied ext may include or omit the leading dot; both forms are
// accepted. Empty ext strips any existing extension.
func PathWithExtension(path string, ext string) string {
	stripped := strings.TrimSuffix(path, filepath.Ext(path))
	if ext == "" {
		return stripped
	}
	if !strings.HasPrefix(ext, ".") {
		ext = "." + ext
	}
	return stripped + ext
}

// PathIsAbsolute is the absolute-path predicate.
func PathIsAbsolute(path string) bool {
	return filepath.IsAbs(path)
}
