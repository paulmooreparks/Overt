using System.Reflection;
using System.Text;

// BindGenerator lives in the global namespace alongside the `Cli` static
// class from Program.cs — Program.cs is a top-level file and doesn't
// introduce a namespace, so staying namespace-less keeps the references
// simple.

/// <summary>
/// Reflection-driven facade generator for the <c>overt bind</c> subcommand.
/// Given a .NET type's full name, walks its public static methods and emits
/// an Overt module of <c>extern "csharp" fn ...</c> declarations.
///
/// MVP scope — by design — covers the subset that's safe to auto-generate
/// without guesswork:
/// <list type="bullet">
///   <item>Public static methods (no instance methods, no properties, no
///     constructors; those need <c>extern</c> grammar extensions first).</item>
///   <item>Parameters and return types that map cleanly to Overt primitives
///     (<c>string</c>, <c>int</c>, <c>long</c>, <c>double</c>, <c>bool</c>,
///     <c>void</c>). Anything else skips the method with a <c>// skipped</c>
///     comment.</item>
///   <item>Effect rows come from a curated namespace table. The conservative
///     default is <c>!{io, fails}</c> for everything not known to be pure;
///     per our v1 design decision, over-declaring io is safer than under-
///     declaring it.</item>
///   <item>Methods whose return type isn't <c>void</c> are wrapped in
///     <c>Result&lt;T, IoError&gt;</c> so the exception-to-Err conversion
///     the extern runtime does actually fires. Pure methods (per the
///     effects table) are left unwrapped.</item>
/// </list>
///
/// Output is a fully-formed Overt module. Users are expected to check the
/// result in, edit as needed (e.g. refining effect rows for specific
/// methods), and regenerate only when the upstream API changes.
/// </summary>
public static class BindGenerator
{
    // ------------------------------------------------------ effect table
    //
    // Namespace prefix -> effect set. Matched longest-prefix-first. If none
    // match, the conservative fallback `{io, fails}` applies. Keep this list
    // curated and small; extend deliberately.

    private static readonly (string Prefix, string[] Effects, bool Pure)[] EffectRules = new[]
    {
        // Pure computation: no effects, and we don't wrap in Result.
        ("System.Math",   Array.Empty<string>(), true),
        ("System.String", Array.Empty<string>(), true),
        ("System.Char",   Array.Empty<string>(), true),
        ("System.Convert", Array.Empty<string>(), true),
        ("System.IO.Path", Array.Empty<string>(), true), // pure string manip; not I/O

        // I/O with possible failure.
        ("System.IO",          new[] { "io", "fails" }, false),
        ("System.Net",         new[] { "io", "async", "fails" }, false),
        ("System.Console",     new[] { "io" }, false),
        ("System.Environment", new[] { "io" }, false),

        // Async-only concurrency.
        ("System.Threading.Tasks", new[] { "async" }, false),
    };

    public static string Generate(string moduleName, Type targetType)
    {
        var (effects, pure) = EffectsFor(targetType.FullName ?? "");
        var sb = new StringBuilder();

        sb.AppendLine($"module {moduleName}");
        sb.AppendLine();
        sb.AppendLine($"// Auto-generated from `{targetType.FullName}` via `overt bind`.");
        sb.AppendLine("// Hand-edits may be overwritten; prefer regenerating and layering");
        sb.AppendLine("// hand-curated effect annotations on top.");
        sb.AppendLine();

        var methods = targetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // skip property accessors, operators
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();

        // Overt doesn't support function overloading — one name per function.
        // First we count how many renderable overloads each Overt name has, so
        // we can decide whether to add an arity suffix at all. A single
        // renderable overload keeps the bare name even if some unrenderable
        // overloads share it; multiple renderable ones all get `_<arity>`.
        var renderableByName = methods
            .Where(m => IsRenderable(m))
            .GroupBy(m => ToSnakeCase(m.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var method in methods)
        {
            var overt = ToSnakeCase(method.Name);
            var needsSuffix = renderableByName.GetValueOrDefault(overt) > 1;
            var name = needsSuffix ? $"{overt}_{method.GetParameters().Length}" : overt;
            EmitMethod(sb, targetType, method, effects, pure, name);
        }

        return sb.ToString();
    }

    /// <summary>Pre-check: would <see cref="EmitMethod"/> succeed on this
    /// method? Mirrors EmitMethod's checks so we can count emitted overloads
    /// without actually rendering them.</summary>
    private static bool IsRenderable(MethodInfo method)
    {
        if (method.IsGenericMethod) return false;
        foreach (var p in method.GetParameters())
        {
            if (p.IsOptional || p.IsOut || p.ParameterType.IsByRef) return false;
            if (MapCSharpTypeToOvert(p.ParameterType) is null) return false;
        }
        if (method.ReturnType != typeof(void)
            && MapCSharpTypeToOvert(method.ReturnType) is null)
            return false;
        return true;
    }

    private static void EmitMethod(
        StringBuilder sb, Type targetType, MethodInfo method, string[] effects, bool pure,
        string overtName)
    {
        var paramList = new List<string>();
        var canRender = true;
        var skipReason = "";

        foreach (var p in method.GetParameters())
        {
            var overtType = MapCSharpTypeToOvert(p.ParameterType);
            if (overtType is null)
            {
                canRender = false;
                skipReason = $"parameter '{p.Name}' has unsupported type '{p.ParameterType.FullName}'";
                break;
            }
            if (p.IsOptional || p.IsOut || p.ParameterType.IsByRef)
            {
                canRender = false;
                skipReason = $"parameter '{p.Name}' is optional/out/ref";
                break;
            }
            var paramName = ToSnakeCase(p.Name ?? "arg");
            paramList.Add($"{paramName}: {overtType}");
        }

        if (method.IsGenericMethod)
        {
            canRender = false;
            skipReason = "method is generic";
        }

        string? returnOvertType = null;
        if (canRender)
        {
            if (method.ReturnType == typeof(void))
            {
                returnOvertType = "()";
            }
            else
            {
                returnOvertType = MapCSharpTypeToOvert(method.ReturnType);
                if (returnOvertType is null)
                {
                    canRender = false;
                    skipReason = $"return type '{method.ReturnType.FullName}' is unsupported";
                }
            }
        }

        if (!canRender)
        {
            sb.AppendLine($"// skipped {targetType.FullName}.{method.Name}: {skipReason}");
            return;
        }

        // Multi-arg calls in Overt use named args; single-arg is permitted
        // positional but the named form is still legal. Signature stays the
        // same in both cases — the rule is at the *call site*, not declaration.
        sb.Append("extern \"csharp\" fn ");
        sb.Append(overtName);
        sb.Append('(');
        sb.Append(string.Join(", ", paramList));
        sb.Append(')');

        if (effects.Length > 0)
        {
            sb.Append($" !{{{string.Join(", ", effects)}}}");
        }

        // Result-wrap non-pure returns that aren't already Unit-valued.
        var retText = (pure || returnOvertType == "()")
            ? returnOvertType
            : $"Result<{returnOvertType}, IoError>";
        sb.Append($" -> {retText}");

        sb.AppendLine();
        sb.AppendLine($"    binds \"{targetType.FullName}.{method.Name}\"");
        sb.AppendLine();
    }

    // ------------------------------------------------------------- mappings

    /// <summary>Map a .NET type to its Overt spelling; null for types we don't
    /// handle in the MVP.</summary>
    private static string? MapCSharpTypeToOvert(Type t)
    {
        if (t == typeof(string)) return "String";
        if (t == typeof(int)) return "Int";
        if (t == typeof(long)) return "Int";
        if (t == typeof(double) || t == typeof(float)) return "Float";
        if (t == typeof(bool)) return "Bool";
        return null;
    }

    /// <summary>Look up the effects for a .NET namespace. Longest-prefix-match
    /// wins; falls back to the conservative default.</summary>
    private static (string[] Effects, bool Pure) EffectsFor(string fullName)
    {
        var match = EffectRules
            .Where(r => fullName.StartsWith(r.Prefix, StringComparison.Ordinal))
            .OrderByDescending(r => r.Prefix.Length)
            .FirstOrDefault();
        if (match.Prefix is not null) return (match.Effects, match.Pure);
        // Conservative default per DESIGN.md §17: unknown namespaces are
        // assumed to perform I/O and fail. Over-declaring is safer than
        // under-declaring — wrong "pure" silently launders effects.
        return (new[] { "io", "fails" }, false);
    }

    /// <summary>Lowercase-with-underscores conversion for Overt identifiers.
    /// Handles acronym runs conservatively: <c>ReadAllText</c> →
    /// <c>read_all_text</c>, <c>HTTPClient</c> → <c>http_client</c>.</summary>
    private static string ToSnakeCase(string pascal)
    {
        if (string.IsNullOrEmpty(pascal)) return pascal;
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            var isUpper = char.IsUpper(c);
            if (isUpper && i > 0)
            {
                // Insert `_` before an upper-case char that starts a new word.
                // Word break occurs when: previous char is lower-case OR
                // this is the last upper of an acronym run (next is lower).
                var prev = pascal[i - 1];
                var next = i + 1 < pascal.Length ? pascal[i + 1] : ' ';
                if (char.IsLower(prev) || (char.IsUpper(prev) && char.IsLower(next)))
                {
                    sb.Append('_');
                }
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
