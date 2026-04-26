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
