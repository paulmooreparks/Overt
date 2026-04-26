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
	"fmt"
	"os"
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
var traceConsumer func(TraceEvent) Result[Unit, IoError]

// TraceSubscribe registers a consumer for trace events. The Overt
// fn shape is `Trace.subscribe(consumer: fn(TraceEvent) !{io} -> ())`
// returning Unit. Today the GoEmitter doesn't actually emit events
// (trace blocks are pass-through), so this records the consumer for
// when it does. When the emitter grows event emission, this is
// where dispatch hooks in.
func TraceSubscribe(consumer func(TraceEvent) Result[Unit, IoError]) {
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

// Map applies f to each element of list, returning a new List with the
// results in order. Pure: does not mutate either input.
func Map[T, U any](list List[T], f func(T) U) List[U] {
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
