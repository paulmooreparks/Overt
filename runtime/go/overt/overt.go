// Package overt is the Go-side runtime for code transpiled from Overt.
//
// It mirrors the small surface that Overt programs depend on regardless
// of user code: Unit, Result[T, E], Option[T], IoError, and the prelude
// functions like Println / Eprintln. The C# runtime
// (Overt.Runtime.Prelude) is the reference; this file ports the same
// shapes to idiomatic Go using generics (Go 1.18+).
//
// Scope (initial scaffold): just enough surface to run a hello-world
// transpiled module that uses println, Ok / Err, the question-mark
// short-circuit, and Result<Unit, IoError>. List / String / Int.range
// and the rest of the prelude come in follow-up work.
package overt

import (
	"bufio"
	"bytes"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
)

// Unit is the zero-information value returned by fns whose Overt
// signature ends in `-> ()`. Mirrors `Overt.Runtime.Unit` on the C#
// side: there's a single canonical instance, UnitValue.
type Unit struct{}

// UnitValue is the canonical Unit. Returned from Ok-of-Unit results.
var UnitValue = Unit{}

// IoError is the standard error type for I/O-rowed effects. Carries a
// human-readable narrative; future fields can capture an underlying
// errno or wrapped error without breaking callers.
type IoError struct {
	Narrative string
}

// Error implements Go's `error` interface so an IoError can flow
// through Go-native error sites if the user mixes idioms.
func (e IoError) Error() string { return "IoError: " + e.Narrative }

// Result is a tagged union for fallible values. IsOk picks the active
// arm; the inactive arm holds the zero value of its type. Pattern
// matches in Overt lower to `if r.IsOk { ... } else { ... }`, and
// ?-propagation lowers to an early-return guarded by !IsOk.
type Result[T any, E any] struct {
	IsOk  bool
	Value T
	Err   E
}

// Ok constructs an Ok-arm Result. Type parameters are usually inferred
// at the call site from the contextual return type; explicit Ok[T, E]
// is occasionally needed when the inferred T or E is ambiguous.
func Ok[T any, E any](v T) Result[T, E] {
	return Result[T, E]{IsOk: true, Value: v}
}

// Err constructs an Err-arm Result.
func Err[T any, E any](e E) Result[T, E] {
	return Result[T, E]{IsOk: false, Err: e}
}

// Option is the nullable-by-construction sibling of Result. Only the
// IsSome arm carries a value.
type Option[T any] struct {
	IsSome bool
	Value  T
}

// Some constructs the populated arm.
func Some[T any](v T) Option[T] {
	return Option[T]{IsSome: true, Value: v}
}

// None constructs the empty arm. Caller passes the type parameter
// explicitly because there is no value to infer from.
func None[T any]() Option[T] {
	return Option[T]{IsSome: false}
}

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

// TraceEvent is the (placeholder) shape of an event emitted by a
// `trace { ... }` block. The C# runtime carries fn-entry / fn-exit /
// binding / branch / arm structured events; the Go target ships a
// minimal stub for now so trace blocks compile and the
// Trace.subscribe call has a fn-typed argument to bind to. With no
// real events emitted (the GoEmitter currently lowers trace blocks
// as zero-cost pass-throughs), the stub is sufficient.
type TraceEvent struct {
	Description string
}

// String implements fmt.Stringer so `%v` interpolation against a
// TraceEvent renders the description rather than struct dump syntax.
func (e TraceEvent) String() string { return e.Description }

// traceConsumer is the registered subscriber, if any. Singleton
// because Overt's Trace.subscribe replaces the previous registration
// rather than chaining; that's the C# runtime's behavior too.
var traceConsumer func(TraceEvent)

// TraceSubscribe registers a consumer for trace events. The Overt
// fn shape is `Trace.subscribe(consumer: fn(TraceEvent) !{io} -> ())`
// returning Unit; the Go-side parameter mirrors the Unit-return as
// no-return-slot. Today the GoEmitter doesn't actually emit events
// (trace blocks are pass-through), so this records the consumer for
// when it does. When the emitter grows event emission, this is
// where dispatch hooks in.
func TraceSubscribe(consumer func(TraceEvent)) {
	traceConsumer = consumer
}

// List is Overt's persistent, immutable sequence type. The Go layout is
// a thin wrapper around a slice; the emitter never emits mutation
// against List values, so the slice is treated as read-only by
// convention even though Go's type system can't enforce it. The C#
// runtime uses ImmutableArray<T> for the same shape; Go's lack of an
// immutable-collection equivalent is the practical reason for the
// convention rather than the enforcement.
type List[T any] struct {
	Items []T
}

// ListEmpty constructs the empty List. Type parameter is explicit
// because there's no value to infer from.
func ListEmpty[T any]() List[T] {
	return List[T]{Items: []T{}}
}

// ListSingleton wraps one value as a one-element List.
func ListSingleton[T any](v T) List[T] {
	return List[T]{Items: []T{v}}
}

// ListAt returns the element at the given index. Out-of-range index
// panics — matches the C# runtime's ArgumentOutOfRangeException
// behavior. Callers can guard with a Size check or use Option-shaped
// helpers when a missing element should be a value, not a fault.
func ListAt[T any](list List[T], index int) T {
	return list.Items[index]
}

// ListConcatThree appends three Lists end-to-end. Mirrors the Overt
// `List.concat_three(first, middle, last)` shape; useful when the
// front end has unrolled a small sequence at compile time.
func ListConcatThree[T any](first, middle, last List[T]) List[T] {
	out := make([]T, 0, len(first.Items)+len(middle.Items)+len(last.Items))
	out = append(out, first.Items...)
	out = append(out, middle.Items...)
	out = append(out, last.Items...)
	return List[T]{Items: out}
}

// Size, Len, and Length are three names for closely related operations.
// Size and Len both return the element count of a List; Length returns
// the byte length of a string. Overt's prelude exposes all three names
// (size and len as synonyms for List, length for String); the runtime
// faithfully provides each so the emitter doesn't have to rewrite at
// the call site.
func Size[T any](list List[T]) int   { return len(list.Items) }
func Len[T any](list List[T]) int    { return len(list.Items) }
func Length(s string) int            { return len(s) }

// ListConcat appends two Lists end-to-end. Two-arity sibling of
// ListConcatThree; useful for the common case of growing a list by
// one batch.
func ListConcat[T any](left List[T], right List[T]) List[T] {
	out := make([]T, 0, len(left.Items)+len(right.Items))
	out = append(out, left.Items...)
	out = append(out, right.Items...)
	return List[T]{Items: out}
}

// ListHead returns the first element wrapped in Some, or None for the
// empty list. Pairs with ListTail for the standard pattern-matching
// recursion idiom.
func ListHead[T any](list List[T]) Option[T] {
	if len(list.Items) == 0 {
		return None[T]()
	}
	return Some(list.Items[0])
}

// ListTail returns everything but the first element. Tail of empty is
// empty (Haskell-flavored, not panic-on-empty).
func ListTail[T any](list List[T]) List[T] {
	if len(list.Items) == 0 {
		return list
	}
	out := make([]T, len(list.Items)-1)
	copy(out, list.Items[1:])
	return List[T]{Items: out}
}

// ListTake returns the first n elements. Negative n yields empty;
// n >= length yields the whole list. Total recovery on programmer
// input.
func ListTake[T any](list List[T], n int) List[T] {
	if n <= 0 {
		return List[T]{Items: []T{}}
	}
	if n >= len(list.Items) {
		return list
	}
	out := make([]T, n)
	copy(out, list.Items[:n])
	return List[T]{Items: out}
}

// ListDrop returns everything past the first n elements. Symmetric
// recovery to ListTake.
func ListDrop[T any](list List[T], n int) List[T] {
	if n <= 0 {
		return list
	}
	if n >= len(list.Items) {
		return List[T]{Items: []T{}}
	}
	out := make([]T, len(list.Items)-n)
	copy(out, list.Items[n:])
	return List[T]{Items: out}
}

// ListReverse returns a new List with elements in reverse order.
func ListReverse[T any](list List[T]) List[T] {
	if len(list.Items) <= 1 {
		return list
	}
	out := make([]T, len(list.Items))
	for i, v := range list.Items {
		out[len(list.Items)-1-i] = v
	}
	return List[T]{Items: out}
}

// ListFind returns Some of the first element matching predicate,
// None when no element matches.
func ListFind[T any](list List[T], predicate func(T) bool) Option[T] {
	for _, v := range list.Items {
		if predicate(v) {
			return Some(v)
		}
	}
	return None[T]()
}

// ListFindIndex returns Some of the first index whose element matches
// predicate, None when no element matches.
func ListFindIndex[T any](list List[T], predicate func(T) bool) Option[int] {
	for i, v := range list.Items {
		if predicate(v) {
			return Some(i)
		}
	}
	return None[int]()
}

// ListContains is membership via Go's `==` operator. Requires T to be
// comparable; Overt's type checker doesn't enforce this yet, so a
// caller passing a non-comparable T gets a Go-level error.
func ListContains[T comparable](list List[T], value T) bool {
	for _, v := range list.Items {
		if v == value {
			return true
		}
	}
	return false
}

// ListFlatMap maps each element to a list, then concats the results.
func ListFlatMap[T, U any](list List[T], f func(T) List[U]) List[U] {
	var out []U
	for _, v := range list.Items {
		out = append(out, f(v).Items...)
	}
	if out == nil {
		out = []U{}
	}
	return List[U]{Items: out}
}

// ListPartitionResult is the two-bucket result of ListPartition.
// Mirrors the C# ListPartition<T> record. Field naming bridges
// snake_case (Overt) ↔ TitleCase (Go) via the emitter.
type ListPartitionResult[T any] struct {
	Matched   List[T]
	Unmatched List[T]
}

// ListPartition splits list into (matched, unmatched) by predicate,
// preserving order within each bucket.
func ListPartition[T any](list List[T], predicate func(T) bool) ListPartitionResult[T] {
	var yes, no []T
	for _, v := range list.Items {
		if predicate(v) {
			yes = append(yes, v)
		} else {
			no = append(no, v)
		}
	}
	if yes == nil {
		yes = []T{}
	}
	if no == nil {
		no = []T{}
	}
	return ListPartitionResult[T]{
		Matched:   List[T]{Items: yes},
		Unmatched: List[T]{Items: no},
	}
}

// ListMap applies f to each element of list, returning a new List with
// the results in order. Pure: does not mutate either input. The runtime
// fn is named ListMap (not Map) so the function namespace doesn't clash
// with the Map[K, V] type defined below — Go forbids type/function name
// collisions. The emitter rewrites the user-side `map(...)` call to
// `overt.ListMap(...)` accordingly.
func ListMap[T, U any](list List[T], f func(T) U) List[U] {
	out := make([]U, len(list.Items))
	for i, v := range list.Items {
		out[i] = f(v)
	}
	return List[U]{Items: out}
}

// Filter returns a new List with only those elements of list for
// which pred returns true. Order is preserved.
func Filter[T any](list List[T], pred func(T) bool) List[T] {
	out := make([]T, 0, len(list.Items))
	for _, v := range list.Items {
		if pred(v) {
			out = append(out, v)
		}
	}
	return List[T]{Items: out}
}

// Fold folds list left-to-right with seed as the initial accumulator;
// step receives the accumulator and the current element and returns
// the next accumulator value.
func Fold[T, U any](list List[T], seed U, step func(U, T) U) U {
	acc := seed
	for _, v := range list.Items {
		acc = step(acc, v)
	}
	return acc
}

// All returns true iff pred holds for every element. Vacuously true
// on the empty List. Short-circuits on the first false.
func All[T any](list List[T], pred func(T) bool) bool {
	for _, v := range list.Items {
		if !pred(v) {
			return false
		}
	}
	return true
}

// Any returns true iff pred holds for at least one element. Vacuously
// false on the empty List. Short-circuits on the first true.
func Any[T any](list List[T], pred func(T) bool) bool {
	for _, v := range list.Items {
		if pred(v) {
			return true
		}
	}
	return false
}

// ParMap applies f to each element of list concurrently and returns
// a List of the results in input order, OR the first Err encountered
// if any element's call failed. On empty input returns Ok of the
// empty list. Mirrors C# runtime's par_map.
//
// Implementation: goroutine per item with a WaitGroup join. Results
// are written into a pre-sized slice indexed by position so order is
// preserved without needing a channel-based collector. Per-item
// goroutines force the work onto the scheduler instead of running
// inline (the inline-loop fallback some parallel-loop libs do
// silently breaks the "genuinely concurrent" contract this fn
// promises).
func ParMap[T, U, E any](list List[T], f func(T) Result[U, E]) Result[List[U], E] {
	items := list.Items
	if len(items) == 0 {
		return Ok[List[U], E](List[U]{Items: []U{}})
	}
	results := make([]Result[U, E], len(items))
	var wg sync.WaitGroup
	wg.Add(len(items))
	for i, v := range items {
		i, v := i, v
		go func() {
			defer wg.Done()
			results[i] = f(v)
		}()
	}
	wg.Wait()
	for _, r := range results {
		if !r.IsOk {
			return Err[List[U], E](r.Err)
		}
	}
	out := make([]U, len(items))
	for i, r := range results {
		out[i] = r.Value
	}
	return Ok[List[U], E](List[U]{Items: out})
}

// TryMap is the sequential, pure-effects cousin of ParMap. Walks the
// input list in order, calls f on each, short-circuits on the first
// Err. No goroutines; no async effect on the caller side. Use when
// the callback is a pure validator and the parallelism in ParMap
// would force unwanted effects into the caller's row.
func TryMap[T, U, E any](list List[T], f func(T) Result[U, E]) Result[List[U], E] {
	out := make([]U, 0, len(list.Items))
	for _, v := range list.Items {
		r := f(v)
		if !r.IsOk {
			return Err[List[U], E](r.Err)
		}
		out = append(out, r.Value)
	}
	return Ok[List[U], E](List[U]{Items: out})
}

// RefinementViolation is the panic value raised when a value flowing
// into a refinement-typed boundary fails the refinement's predicate
// at runtime. The C# runtime throws an exception of the same shape
// (Overt.Runtime.RefinementViolation); Go has no exceptions, so the
// emitted check panics with this struct and callers that want
// structured access can `recover()`. The default formatted output
// (via Error / String) matches the C# message verbatim, so a panic
// transcript reads the same on both targets.
//
// Compile-time checks (OV0311) catch literal violations; this covers
// the cases that the type checker can't decide statically — non-
// literal values, predicates calling functions like `size(self) > 0`,
// etc.
type RefinementViolation struct {
	AliasName      string
	PredicateText  string
	OffendingValue any
}

// Error implements the error interface so a recovered RefinementViolation
// flows naturally through error-aware code if the user mixes idioms.
func (v RefinementViolation) Error() string {
	return fmt.Sprintf("value %s does not satisfy refinement `%s` predicate: %s",
		refinementRepr(v.OffendingValue), v.AliasName, v.PredicateText)
}

// String mirrors Error so `%v` and `%s` formatting both produce the
// human-readable narrative rather than struct-dump syntax.
func (v RefinementViolation) String() string { return v.Error() }

// refinementRepr quotes strings and prints other values via %v, matching
// the C# runtime's Repr helper so violation messages stay aligned across
// targets.
func refinementRepr(v any) string {
	switch x := v.(type) {
	case nil:
		return "null"
	case string:
		return fmt.Sprintf("%q", x)
	default:
		return fmt.Sprintf("%v", v)
	}
}

// RefinementError is the default Err arm of an auto-generated
// `Alias.try_from(raw)` when the refinement type does not supply an
// `else { ... }` clause. Refinements that DO supply one use their
// own domain type instead, so this is the fallback "no custom error
// declared" shape. Round-trips through the emitter as a value, not
// a panic — distinct from RefinementViolation, which IS a panic
// raised by the always-on `__Refinement_{Alias}__Check` boundary
// helper.
//
// Fields use Go's TitleCase export convention; the Overt-level field
// names (`alias_name`, etc.) are reserved for if/when user code wants
// to read them via field-access syntax, at which point the emitter
// can route the access. For now the helper constructs values directly
// and users typically just propagate or pattern-match on the type.
type RefinementError struct {
	AliasName      string
	PredicateText  string
	OffendingValue any
}

func (e RefinementError) String() string {
	return fmt.Sprintf("value %s does not satisfy refinement `%s` predicate: %s",
		refinementRepr(e.OffendingValue), e.AliasName, e.PredicateText)
}

// Error mirrors String so RefinementError can flow through error-aware
// code if the user mixes idioms. The default narrative matches the C#
// runtime's RefinementError.ToString output verbatim.
func (e RefinementError) Error() string { return e.String() }

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

// StringTrim removes leading and trailing whitespace per Unicode rules.
// Mirrors C# String.Trim() and Python str.strip(); same set of code
// points considered whitespace.
func StringTrim(s string) string {
	return strings.TrimSpace(s)
}

// StringToUpper / StringToLower do invariant-culture case conversion,
// avoiding the Turkish-locale "i" surprise that bit Java for years.
// Programs that want locale-aware case use FFI to the host's locale
// machinery.
func StringToUpper(s string) string { return strings.ToUpper(s) }
func StringToLower(s string) string { return strings.ToLower(s) }

// StringReplace replaces every occurrence of `from` with `to`. Empty
// `from` is a programmer error and panics, matching the C# runtime's
// ArgumentException shape (cross-target consistency on the failure
// mode).
func StringReplace(s string, from string, to string) string {
	if from == "" {
		panic("String.replace: 'from' must be non-empty")
	}
	return strings.ReplaceAll(s, from, to)
}

// StringSubstring returns the half-open [start, end) substring. Both
// indices are byte offsets (matching Length / Code_at conventions).
// Out-of-range or inverted indices panic; callers guard with length()
// checks.
func StringSubstring(s string, start int, end int) string {
	if start < 0 || end < 0 || start > len(s) || end > len(s) || start > end {
		panic(fmt.Sprintf(
			"String.substring: indices out of range or inverted "+
				"(start=%d, end=%d, length=%d)",
			start, end, len(s)))
	}
	return s[start:end]
}

// StringIndexOf returns Some(i) for the first byte-offset of needle
// in s, None when absent. Empty needle is 0 (Go's strings.Index
// convention; matches .NET String.IndexOf).
func StringIndexOf(s string, needle string) Option[int] {
	i := strings.Index(s, needle)
	if i < 0 {
		return None[int]()
	}
	return Some(i)
}

// StringRepeat returns s repeated n times. n=0 or empty s yields "".
// Negative n is a programmer error and panics.
func StringRepeat(s string, n int) string {
	if n < 0 {
		panic(fmt.Sprintf("String.repeat: count must be non-negative (got %d)", n))
	}
	return strings.Repeat(s, n)
}

// StringParseInt parses a decimal integer string into a Result. Mirrors
// the C# Prelude.String.parse_int contract: invariant-formatted (no
// locale), accepts an optional leading minus, rejects whitespace and
// trailing junk. Bad input returns Err(IoError) with a narrative that
// echoes the offending input — matches across targets so a program
// reading it in either back end gets the same string.
func StringParseInt(s string) Result[int, IoError] {
	n, err := strconv.Atoi(s)
	if err != nil {
		return Err[int, IoError](IoError{Narrative: "could not parse '" + s + "' as Int"})
	}
	return Ok[int, IoError](n)
}

// StringParseFloat is the float-shaped sibling. Same contract; same
// narrative shape on failure.
func StringParseFloat(s string) Result[float64, IoError] {
	d, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return Err[float64, IoError](IoError{Narrative: "could not parse '" + s + "' as Float"})
	}
	return Ok[float64, IoError](d)
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

// Map is the immutable key-value type backing Overt's `Map<K, V>`. The
// underlying Go map is treated as read-only by convention; mutating
// operations always allocate a new map. K must be `comparable` per
// Go's generics rules — the same constraint Overt's user-side keys
// inherit (in practice: primitive types, refinement aliases of
// primitives, simple records).
type Map[K comparable, V any] struct {
	Items map[K]V
}

// MapEntry is one key-value pair as a value type. Returned from MapEntries.
// Field names use Go's TitleCase export convention; the emitter bridges
// the naming for Overt-side `entry.key` / `entry.value` access.
type MapEntry[K comparable, V any] struct {
	Key   K
	Value V
}

// MapEmpty constructs the empty map. Type parameters are explicit because
// there's no value to infer from.
func MapEmpty[K comparable, V any]() Map[K, V] {
	return Map[K, V]{Items: map[K]V{}}
}

// MapGet returns Some(value) when key is present, None otherwise.
func MapGet[K comparable, V any](m Map[K, V], key K) Option[V] {
	if v, ok := m.Items[key]; ok {
		return Some(v)
	}
	return None[V]()
}

// MapContainsKey is true iff key is present in m.
func MapContainsKey[K comparable, V any](m Map[K, V], key K) bool {
	_, ok := m.Items[key]
	return ok
}

// MapInsert returns a new Map with (key, value) added (or replaced if
// key was already present). The original is unchanged.
func MapInsert[K comparable, V any](m Map[K, V], key K, value V) Map[K, V] {
	out := make(map[K]V, len(m.Items)+1)
	for k, v := range m.Items {
		out[k] = v
	}
	out[key] = value
	return Map[K, V]{Items: out}
}

// MapRemove returns a new Map without key. Removing an absent key is a
// no-op; the returned Map is structurally equal to the input (via a
// fresh allocation; no aliasing).
func MapRemove[K comparable, V any](m Map[K, V], key K) Map[K, V] {
	if _, ok := m.Items[key]; !ok {
		// Avoid the copy when key isn't present; caller can't observe
		// the aliasing because Map values are immutable by convention.
		return m
	}
	out := make(map[K]V, len(m.Items)-1)
	for k, v := range m.Items {
		if k == key {
			continue
		}
		out[k] = v
	}
	return Map[K, V]{Items: out}
}

// MapSize returns the entry count.
func MapSize[K comparable, V any](m Map[K, V]) int {
	return len(m.Items)
}

// MapKeys returns the keys as a List in iteration order. Go's map
// iteration is unspecified order; programs that need a deterministic
// order sort the returned list.
func MapKeys[K comparable, V any](m Map[K, V]) List[K] {
	out := make([]K, 0, len(m.Items))
	for k := range m.Items {
		out = append(out, k)
	}
	return List[K]{Items: out}
}

// MapValues returns the values as a List in iteration order. See
// MapKeys for ordering caveats.
func MapValues[K comparable, V any](m Map[K, V]) List[V] {
	out := make([]V, 0, len(m.Items))
	for _, v := range m.Items {
		out = append(out, v)
	}
	return List[V]{Items: out}
}

// MapEntries returns each (key, value) as a MapEntry record. Pairs
// with Overt's `for entry in entries { ... }` iteration; users access
// `entry.key` / `entry.value`.
func MapEntries[K comparable, V any](m Map[K, V]) List[MapEntry[K, V]] {
	out := make([]MapEntry[K, V], 0, len(m.Items))
	for k, v := range m.Items {
		out = append(out, MapEntry[K, V]{Key: k, Value: v})
	}
	return List[MapEntry[K, V]]{Items: out}
}

// MapMerge: right wins on key collision, matching the C# runtime's
// last-writer-wins convention.
func MapMerge[K comparable, V any](left Map[K, V], right Map[K, V]) Map[K, V] {
	out := make(map[K]V, len(left.Items)+len(right.Items))
	for k, v := range left.Items {
		out[k] = v
	}
	for k, v := range right.Items {
		out[k] = v
	}
	return Map[K, V]{Items: out}
}

// MapMap transforms each value by f, keeping keys identical.
func MapMap[K comparable, V, W any](m Map[K, V], f func(V) W) Map[K, W] {
	out := make(map[K]W, len(m.Items))
	for k, v := range m.Items {
		out[k] = f(v)
	}
	return Map[K, W]{Items: out}
}

// MapFilter keeps only entries for which pred returns true.
func MapFilter[K comparable, V any](m Map[K, V], pred func(K, V) bool) Map[K, V] {
	out := make(map[K]V)
	for k, v := range m.Items {
		if pred(k, v) {
			out[k] = v
		}
	}
	return Map[K, V]{Items: out}
}

// Set is the immutable membership type backing Overt's `Set<T>`. The
// underlying Go map (with empty-struct values) is the idiomatic Go set
// pattern; mutating operations always allocate a new map.
type Set[T comparable] struct {
	Items map[T]struct{}
}

// SetEmpty constructs the empty set.
func SetEmpty[T comparable]() Set[T] {
	return Set[T]{Items: map[T]struct{}{}}
}

// SetContains is the membership predicate.
func SetContains[T comparable](s Set[T], value T) bool {
	_, ok := s.Items[value]
	return ok
}

// SetInsert returns a new Set with value added. Adding an element that's
// already present is a no-op (returns a structurally equal Set).
func SetInsert[T comparable](s Set[T], value T) Set[T] {
	if _, ok := s.Items[value]; ok {
		return s
	}
	out := make(map[T]struct{}, len(s.Items)+1)
	for k := range s.Items {
		out[k] = struct{}{}
	}
	out[value] = struct{}{}
	return Set[T]{Items: out}
}

// SetRemove returns a new Set without value. Removing an absent value
// is a no-op.
func SetRemove[T comparable](s Set[T], value T) Set[T] {
	if _, ok := s.Items[value]; !ok {
		return s
	}
	out := make(map[T]struct{}, len(s.Items)-1)
	for k := range s.Items {
		if k == value {
			continue
		}
		out[k] = struct{}{}
	}
	return Set[T]{Items: out}
}

// SetSize returns the element count.
func SetSize[T comparable](s Set[T]) int {
	return len(s.Items)
}

// SetUnion returns the elements present in either set.
func SetUnion[T comparable](left Set[T], right Set[T]) Set[T] {
	out := make(map[T]struct{}, len(left.Items)+len(right.Items))
	for k := range left.Items {
		out[k] = struct{}{}
	}
	for k := range right.Items {
		out[k] = struct{}{}
	}
	return Set[T]{Items: out}
}

// SetIntersect returns the elements present in both sets.
func SetIntersect[T comparable](left Set[T], right Set[T]) Set[T] {
	out := make(map[T]struct{})
	// Iterate the smaller set for fewer probes.
	a, b := left, right
	if len(a.Items) > len(b.Items) {
		a, b = b, a
	}
	for k := range a.Items {
		if _, ok := b.Items[k]; ok {
			out[k] = struct{}{}
		}
	}
	return Set[T]{Items: out}
}

// SetDifference returns the elements present in left but not in right.
func SetDifference[T comparable](left Set[T], right Set[T]) Set[T] {
	out := make(map[T]struct{})
	for k := range left.Items {
		if _, ok := right.Items[k]; !ok {
			out[k] = struct{}{}
		}
	}
	return Set[T]{Items: out}
}

// IntRange returns the half-open integer range [start, end) as a List.
// start >= end yields the empty List (Python semantics).
func IntRange(start, end int) List[int] {
	if start >= end {
		return List[int]{Items: []int{}}
	}
	out := make([]int, 0, end-start)
	for i := start; i < end; i++ {
		out = append(out, i)
	}
	return List[int]{Items: out}
}
