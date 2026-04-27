package overt

import (
	"fmt"
	"os"
	"sync"
)

// LogLevel is the severity level on a TraceEvent. The four-level set
// (Debug / Info / Warn / Error) matches the lowest common denominator
// across logging libraries; programs needing a fifth build on top via
// the subscriber. Encoded as a sealed sum on the C# side; on Go we
// use a small int enum since Go has no real sums.
type LogLevel int

const (
	LogLevelDebug LogLevel = iota
	LogLevelInfo
	LogLevelWarn
	LogLevelError
)

// String renders the level as DEBUG / INFO / WARN / ERROR.
func (l LogLevel) String() string {
	switch l {
	case LogLevelDebug:
		return "DEBUG"
	case LogLevelInfo:
		return "INFO"
	case LogLevelWarn:
		return "WARN"
	case LogLevelError:
		return "ERROR"
	}
	return "?"
}

// TraceEvent is a single event emitted by a `trace { ... }` block or
// a Log.X(message) call. Subscribers see both sources through the
// same Trace.subscribe channel.
type TraceEvent struct {
	Level   LogLevel
	Message string
}

// String renders TraceEvent as `[LEVEL] message` so `%v` interpolation
// produces the same shape the default Log consumer prints.
func (e TraceEvent) String() string {
	return "[" + e.Level.String() + "] " + e.Message
}

// Subscriber list. Process-wide; emit dispatches synchronously in
// registration order, matching the C# runtime's contract.
var (
	traceSubscribersMu sync.Mutex
	traceSubscribers   []func(TraceEvent)
	logFallbackOnce    sync.Once
)

// TraceSubscribe registers a consumer for trace events. Multiple
// subscribers fire in registration order.
func TraceSubscribe(consumer func(TraceEvent)) {
	traceSubscribersMu.Lock()
	traceSubscribers = append(traceSubscribers, consumer)
	traceSubscribersMu.Unlock()
}

// TraceEmit dispatches event to every subscriber.
func TraceEmit(evt TraceEvent) {
	traceSubscribersMu.Lock()
	snapshot := make([]func(TraceEvent), len(traceSubscribers))
	copy(snapshot, traceSubscribers)
	traceSubscribersMu.Unlock()
	for _, s := range snapshot {
		s(evt)
	}
}

// LogDebug / LogInfo / LogWarn / LogError emit a TraceEvent at the
// corresponding level. The default consumer (installed lazily on the
// first Log call) writes [LEVEL] message to stderr; subscribers
// registered on top run alongside it.
func LogDebug(message string) Unit { return logEmit(LogLevelDebug, message) }
func LogInfo(message string) Unit  { return logEmit(LogLevelInfo, message) }
func LogWarn(message string) Unit  { return logEmit(LogLevelWarn, message) }
func LogError(message string) Unit { return logEmit(LogLevelError, message) }

func logEmit(level LogLevel, message string) Unit {
	logFallbackOnce.Do(func() {
		TraceSubscribe(func(evt TraceEvent) {
			fmt.Fprintln(os.Stderr, evt.String())
		})
	})
	TraceEmit(TraceEvent{Level: level, Message: message})
	return UnitValue
}
