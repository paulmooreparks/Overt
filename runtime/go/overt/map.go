package overt

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
