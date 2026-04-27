package overt

import (
	"fmt"
	"unicode/utf8"
)

// Bytes is the foundational immutable byte sequence. Wraps a Go []byte
// behind a thin shell — programs read but don't mutate. Used by
// FileReadBytes / FileWriteBytes and any extern boundary that crosses
// binary data. The Overt-side `Bytes.at` returns Int (0..255); a
// separate Byte primitive would duplicate a refinement type the
// language already supports.
type Bytes struct {
	Items []byte
}

// BytesEmpty / BytesFromList / etc. — the namespace operations on
// Bytes. Naming convention matches the existing pattern (e.g.
// ListEmpty for List operations).

func BytesEmpty() Bytes {
	return Bytes{Items: []byte{}}
}

// BytesFromList converts a List<Int> to Bytes. Each Int must be in
// 0..255; out-of-range values panic (programmer error per the Overt
// stdlib's contract).
func BytesFromList(list List[int]) Bytes {
	out := make([]byte, len(list.Items))
	for i, v := range list.Items {
		if v < 0 || v > 255 {
			panic(fmt.Sprintf(
				"Bytes.from_list: element at index %d is %d, expected 0..255",
				i, v))
		}
		out[i] = byte(v)
	}
	return Bytes{Items: out}
}

// BytesSize returns the byte count.
func BytesSize(b Bytes) int { return len(b.Items) }

// BytesAt returns the byte at index as Int. Out-of-range panics.
func BytesAt(b Bytes, index int) int {
	if index < 0 || index >= len(b.Items) {
		panic(fmt.Sprintf(
			"Bytes.at: index out of range [0, %d)", len(b.Items)))
	}
	return int(b.Items[index])
}

// BytesSlice returns the half-open [start, end) sub-Bytes.
// Out-of-range or inverted indices panic.
func BytesSlice(b Bytes, start int, end int) Bytes {
	if start < 0 || end < 0 || start > len(b.Items) || end > len(b.Items) || start > end {
		panic(fmt.Sprintf(
			"Bytes.slice: indices out of range or inverted "+
				"(start=%d, end=%d, length=%d)",
			start, end, len(b.Items)))
	}
	out := make([]byte, end-start)
	copy(out, b.Items[start:end])
	return Bytes{Items: out}
}

// BytesConcat appends two Bytes end-to-end.
func BytesConcat(left Bytes, right Bytes) Bytes {
	out := make([]byte, len(left.Items)+len(right.Items))
	copy(out, left.Items)
	copy(out[len(left.Items):], right.Items)
	return Bytes{Items: out}
}

// BytesFromUtf8 encodes a String as UTF-8 bytes.
func BytesFromUtf8(s string) Bytes {
	return Bytes{Items: []byte(s)}
}

// BytesToUtf8 decodes Bytes as UTF-8. Invalid sequences surface as
// Err(IoError); the narrative carries the offending byte index.
// Same convention as the C# runtime — UTF-8 validity is enforced
// because programs that round-trip through bytes can produce broken
// sequences if the binary boundary mishandles encoding.
func BytesToUtf8(b Bytes) Result[string, IoError] {
	// Go's `string(bytes)` is permissive — replaces invalid sequences
	// with U+FFFD silently. For Result-shaped semantics we validate
	// explicitly first.
	if !utf8.Valid(b.Items) {
		return Err[string, IoError](IoError{
			Narrative: "Bytes.to_utf8: input is not valid UTF-8",
		})
	}
	return Ok[string, IoError](string(b.Items))
}
