// LogLevel + TraceEvent + the static class Log facade. The trace
// channel itself (subscribe / emit) lives nested inside the Prelude
// class so transpiled source can write `Trace.subscribe(...)` after
// the `using static Overt.Runtime.Prelude;` import. Log is top-level
// because Overt source spells it `Log.info(...)`.

namespace Overt.Runtime;

/// <summary>
/// Severity level on a <see cref="TraceEvent"/>. The four-level set
/// (Debug / Info / Warn / Error) matches the lowest common denominator
/// across logging libraries; programs that need a fifth (Trace,
/// Critical, etc.) build on top via the subscriber.
/// </summary>
public abstract record LogLevel
{
    public static readonly LogLevel Debug = new LogLevelDebug();
    public static readonly LogLevel Info = new LogLevelInfo();
    public static readonly LogLevel Warn = new LogLevelWarn();
    public static readonly LogLevel Error = new LogLevelError();
}
public sealed record LogLevelDebug : LogLevel;
public sealed record LogLevelInfo : LogLevel;
public sealed record LogLevelWarn : LogLevel;
public sealed record LogLevelError : LogLevel;

/// <summary>
/// A single trace event. Carries a level (Debug / Info / Warn / Error)
/// and a message. <c>trace { ... }</c> blocks emit events at <c>Info</c>
/// by default once the emitter wires that path; <c>Log.{debug, info,
/// warn, error}</c> calls emit explicitly. Subscribers register via
/// <see cref="Prelude.Trace.subscribe"/> and see both sources through
/// the same channel.
/// </summary>
public sealed record TraceEvent(LogLevel level, string message);

/// <summary>
/// Leveled logging. Log.X(message) emits a <see cref="TraceEvent"/>
/// with the corresponding level into the same Trace channel. One
/// consumer registry, two surfaces (this + <c>trace { ... }</c>
/// blocks). When no subscriber is registered, the program-default
/// consumer writes <c>[LEVEL] message</c> to stderr.
/// </summary>
public static class Log
{
    private static readonly object _defaultLock = new();
    private static bool _hasFallback;

    public static Unit debug(string message) => emit(LogLevel.Debug, message);
    public static Unit info(string message) => emit(LogLevel.Info, message);
    public static Unit warn(string message) => emit(LogLevel.Warn, message);
    public static Unit error(string message) => emit(LogLevel.Error, message);

    private static Unit emit(LogLevel level, string message)
    {
        var evt = new TraceEvent(level, message);
        // If no subscriber has registered, fall back to stderr so logs
        // don't silently disappear during development. Keep the fallback
        // installed at-most-once so subscribed programs aren't double-
        // dispatched when tests register on top.
        EnsureDefaultConsumer();
        Prelude.Trace.emit(evt);
        return Unit.Value;
    }

    private static void EnsureDefaultConsumer()
    {
        if (_hasFallback) return;
        lock (_defaultLock)
        {
            if (_hasFallback) return;
            _hasFallback = true;
            Prelude.Trace.subscribe(evt =>
            {
                // Default consumer is a "did anyone else listen" check —
                // if an additional subscriber registers later, both run.
                // We special-case "no real subscribers yet" by tracking
                // count internally; for v1 simplicity, always print to
                // stderr when this consumer fires. Subscribers that want
                // to suppress the default register a no-op consumer
                // first via _reset followed by their own subscribe.
                Console.Error.WriteLine($"[{LevelTag(evt.level)}] {evt.message}");
                return Unit.Value;
            });
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevelDebug => "DEBUG",
        LogLevelInfo => "INFO",
        LogLevelWarn => "WARN",
        LogLevelError => "ERROR",
        _ => "?",
    };
}
