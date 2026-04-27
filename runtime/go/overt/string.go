package overt

import (
	"fmt"
	"strconv"
	"strings"
)

// Length returns the byte length of a string. Distinct from List.Size
// because the two operate on different types — not synonyms.
func Length(s string) int { return len(s) }

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
