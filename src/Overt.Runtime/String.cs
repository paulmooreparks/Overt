// String — the static class that backs Overt source's `String.X` calls.
// (Overt's String primitive itself is .NET's `string`; this is the
// namespace companion for module-qualified operations on it.)

namespace Overt.Runtime;

/// <summary>
/// Non-generic namespace companion for <c>String.X</c> module-qualified
/// calls in Overt source. Overt's <c>String</c> primitive lowers to .NET's
/// <see cref="string"/>; this class collects the operations the prelude
/// signature table exposes under that namespace.
/// </summary>
public static class String
{
    // Splits on the literal separator (no regex). Empty separator is a
    // programmer error and throws. Adjacent separators yield empty
    // segments (StringSplitOptions.None semantics) — callers that want
    // empties collapsed can filter() the result.
    public static List<string> split(string s, string sep)
    {
        if (sep.Length == 0)
        {
            throw new ArgumentException(
                "String.split: separator must be non-empty",
                nameof(sep));
        }
        var parts = s.Split(sep, StringSplitOptions.None);
        return new List<string>(System.Collections.Immutable.ImmutableArray.Create(parts));
    }

    // Inverse of split. Empty list yields empty string.
    public static string join(List<string> list, string sep)
        => string.Join(sep, list.Items);

    // UTF-16 code unit at the given index, as an Int. Useful for
    // building character-class predicates (digit / alpha / etc.) by
    // arithmetic on the result, without per-predicate FFI bindings.
    // Out-of-range index throws.
    public static int code_at(string s, int index)
    {
        if ((uint)index >= (uint)s.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"String.code_at: index out of range [0, {s.Length})");
        }
        return s[index];
    }

    // Each character of the input string as a single-character string.
    // Returned as an Overt List<String> so callers can iterate with
    // `for c in s.chars()` and pattern-match against literal strings.
    // For pure ASCII / character-class work that just needs the code
    // point as a number, prefer `code_points` to avoid the per-char
    // string allocation.
    public static List<string> chars(string s)
    {
        if (s.Length == 0)
        {
            return new List<string>(System.Collections.Immutable.ImmutableArray<string>.Empty);
        }
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            builder.Add(s[i].ToString());
        }
        return new List<string>(builder.MoveToImmutable());
    }

    // Each character's UTF-16 code unit as an Int, in order. The
    // numeric companion to `chars()` — same iteration shape but
    // avoids string boxing per character. Surrogate pairs surface as
    // two ints (matching .NET semantics); SemVer-style ASCII work
    // doesn't care, and Unicode-aware code can post-process.
    public static List<int> code_points(string s)
    {
        if (s.Length == 0)
        {
            return new List<int>(System.Collections.Immutable.ImmutableArray<int>.Empty);
        }
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            builder.Add(s[i]);
        }
        return new List<int>(builder.MoveToImmutable());
    }

    // Three predicate-shape helpers built on .NET's String methods.
    // Empty argument is true in every case (matches StringComparison.Ordinal
    // semantics); callers that want "non-empty prefix" can guard with
    // `length(prefix) > 0 && s.starts_with(prefix)`.
    public static bool starts_with(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);
    public static bool ends_with(string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal);
    public static bool contains(string s, string needle) => s.Contains(needle, StringComparison.Ordinal);

    // Trim removes leading and trailing whitespace (Unicode-aware via .NET's
    // Char.IsWhiteSpace). The narrative "trim" matches Rust / Python; .NET's
    // String.Trim() does the same.
    public static string trim(string s) => s.Trim();

    // Case conversion. Invariant culture so locale doesn't affect the result;
    // a Turkish-locale "i" → "I" surprise that bit Java for years isn't a
    // shape Overt programs should inherit. Programs that want locale-aware
    // case use FFI to System.Globalization.
    public static string to_upper(string s) => s.ToUpperInvariant();
    public static string to_lower(string s) => s.ToLowerInvariant();

    // Replace every occurrence of `from` with `to`. Empty `from` is a
    // programmer error and throws (matches .NET's String.Replace contract).
    public static string replace(string s, string from, string to)
    {
        if (from.Length == 0)
        {
            throw new ArgumentException(
                "String.replace: 'from' must be non-empty",
                nameof(from));
        }
        return s.Replace(from, to, StringComparison.Ordinal);
    }

    // UTF-16 code-unit-indexed substring; half-open [start, end). Out-of-range
    // indices throw (programmer error; callers guard with length() check).
    public static string substring(string s, int start, int end)
    {
        if ((uint)start > (uint)s.Length || (uint)end > (uint)s.Length || start > end)
        {
            throw new ArgumentOutOfRangeException(
                $"String.substring: indices out of range or inverted "
                + $"(start={start}, end={end}, length={s.Length})");
        }
        return s.Substring(start, end - start);
    }

    // Find the index of `needle` in `s`, or None when absent. Empty needle is
    // 0 (matches .NET / Python convention).
    public static Option<int> index_of(string s, string needle)
    {
        var i = s.IndexOf(needle, StringComparison.Ordinal);
        return i < 0 ? new OptionNone<int>() : new OptionSome<int>(i);
    }

    // Repeat the string n times. n=0 yields the empty string; negative n is
    // a programmer error and throws.
    public static string repeat(string s, int n)
    {
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(n), n,
                "String.repeat: count must be non-negative");
        }
        if (n == 0 || s.Length == 0) return string.Empty;
        return string.Concat(System.Linq.Enumerable.Repeat(s, n));
    }

    // Parse helpers. CLI arg parsing and config readers are the typical
    // callers; both paths want a Result to thread into refinement
    // try_from. Invariant culture so locale doesn't affect semantics —
    // Overt programs that need locale-aware parsing build atop these.
    public static Result<int, IoError> parse_int(string s)
    {
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return new ResultOk<int, IoError>(n);
        }
        return new ResultErr<int, IoError>(
            new IoError($"could not parse '{s}' as Int"));
    }

    public static Result<double, IoError> parse_float(string s)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return new ResultOk<double, IoError>(d);
        }
        return new ResultErr<double, IoError>(
            new IoError($"could not parse '{s}' as Float"));
    }
}
