package overt

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

// SetValues returns the set's elements as a List in iteration order.
// Iteration order is hash-defined and not stable across hosts; programs
// that need a deterministic order sort the returned list.
func SetValues[T comparable](set Set[T]) List[T] {
	out := make([]T, 0, len(set.Items))
	for k := range set.Items {
		out = append(out, k)
	}
	return List[T]{Items: out}
}
