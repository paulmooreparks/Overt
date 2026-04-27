// Package overt is the Go-side runtime for code transpiled from Overt.
//
// It mirrors the small surface that Overt programs depend on regardless
// of user code: Unit, Result[T, E], Option[T], IoError, and the prelude
// functions like Println / Eprintln. The C# runtime
// (Overt.Runtime.Prelude) is the reference; this file ports the same
// shapes to idiomatic Go using generics (Go 1.18+).
//
// Source layout: each namespace lives in its own file (list.go,
// string.go, bytes.go, file.go, etc.). They all live in package overt
// and share the types declared here.
package overt

import "fmt"

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
