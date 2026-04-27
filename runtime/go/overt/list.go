package overt

import (
	"sort"
	"sync"
)

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

// Size returns the element count of a List. The String byte-length
// counterpart is Length, in string.go.
func Size[T any](list List[T]) int { return len(list.Items) }

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

// Pair is the universal two-things container — the working form of a
// 2-tuple in Overt source until tuple-type annotations land. Used as
// the element type for ListZip and as the return shape for ListUnzip.
type Pair[T, U any] struct {
	Left  T
	Right U
}

// ListZip pairs corresponding elements from left and right; truncates
// to the shorter when lengths disagree.
func ListZip[T, U any](left List[T], right List[U]) List[Pair[T, U]] {
	n := len(left.Items)
	if len(right.Items) < n {
		n = len(right.Items)
	}
	if n == 0 {
		return List[Pair[T, U]]{Items: []Pair[T, U]{}}
	}
	out := make([]Pair[T, U], n)
	for i := 0; i < n; i++ {
		out[i] = Pair[T, U]{Left: left.Items[i], Right: right.Items[i]}
	}
	return List[Pair[T, U]]{Items: out}
}

// ListUnzip splits a list of pairs into parallel left and right lists
// returned as a Pair<List<T>, List<U>>.
func ListUnzip[T, U any](pairs List[Pair[T, U]]) Pair[List[T], List[U]] {
	n := len(pairs.Items)
	if n == 0 {
		return Pair[List[T], List[U]]{
			Left:  List[T]{Items: []T{}},
			Right: List[U]{Items: []U{}},
		}
	}
	lefts := make([]T, n)
	rights := make([]U, n)
	for i, p := range pairs.Items {
		lefts[i] = p.Left
		rights[i] = p.Right
	}
	return Pair[List[T], List[U]]{
		Left:  List[T]{Items: lefts},
		Right: List[U]{Items: rights},
	}
}

// ListFlatten concatenates a list of lists into one list. Inner-list
// order preserved.
func ListFlatten[T any](lists List[List[T]]) List[T] {
	total := 0
	for _, inner := range lists.Items {
		total += len(inner.Items)
	}
	if total == 0 {
		return List[T]{Items: []T{}}
	}
	out := make([]T, 0, total)
	for _, inner := range lists.Items {
		out = append(out, inner.Items...)
	}
	return List[T]{Items: out}
}

// ListSortBy stable-sorts list by cmp. cmp returns negative / zero /
// positive (libc convention). Stable: ties retain input order.
func ListSortBy[T any](list List[T], cmp func(T, T) int) List[T] {
	if len(list.Items) <= 1 {
		return list
	}
	out := make([]T, len(list.Items))
	copy(out, list.Items)
	sort.SliceStable(out, func(i, j int) bool {
		return cmp(out[i], out[j]) < 0
	})
	return List[T]{Items: out}
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
