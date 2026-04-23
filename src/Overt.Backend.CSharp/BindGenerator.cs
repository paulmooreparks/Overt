using System.Reflection;
using System.Text;

namespace Overt.Backend.CSharp;

/// <summary>
/// Reflection-driven facade generator for the <c>overt bind</c> subcommand.
/// Given a .NET type's full name, walks its public static methods and emits
/// an Overt module of <c>extern "csharp" fn ...</c> declarations.
///
/// This is <b>C#-backend-specific tooling</b> (Tier 2 per DESIGN.md §20). It
/// lives in <c>Overt.Backend.CSharp</c> because it inherently uses .NET
/// reflection and produces <c>extern "csharp"</c> declarations. Sibling
/// backends (Go, C++, TypeScript) will ship their own binding generators
/// tied to their own host ecosystems.
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

        // Static read-only properties and fields emit as zero-arg externs; the
        // extern runtime detects them via reflection and emits bare member
        // access instead of a call. Properties without a public getter and
        // write-only fields are skipped.
        var properties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.GetMethod is { IsPublic: true })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var fields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsSpecialName)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var prop in properties) EmitProperty(sb, targetType, prop, effects, pure);
        foreach (var field in fields)   EmitField(sb, targetType, field, effects, pure);

        // Track top-level names that become free functions in the facade.
        // Parameter names that collide are mangled (suffix `_arg`) so Overt's
        // no-shadowing rule (§3) doesn't reject the facade. The collision
        // typically shows up when a type has both a property (e.g.
        // `Environment.ExitCode`) and a method parameter with the matching
        // snake_case name (e.g. `Environment.Exit(exitCode)`).
        var topLevelNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in properties) topLevelNames.Add(ToSnakeCase(p.Name));
        foreach (var f in fields)     topLevelNames.Add(ToSnakeCase(f.Name));

        var methods = targetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // skip property accessors, operators
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();
        foreach (var m in methods) topLevelNames.Add(ToSnakeCase(m.Name));

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
            // Disambiguate overloads by parameter types, not arity. Many BCL
            // types (Math, Convert) have overloads that share arity but
            // differ in primitive type — `Abs(int)` vs `Abs(double)` both
            // become `abs_1` under arity-only. Type-suffixing yields
            // `abs_int` / `abs_double`.
            var name = needsSuffix ? $"{overt}_{OverloadSuffix(method)}" : overt;
            EmitMethod(sb, targetType, method, effects, pure, name, topLevelNames);
        }

        return sb.ToString();
    }

    private static void EmitProperty(
        StringBuilder sb, Type targetType, PropertyInfo prop, string[] effects, bool pure)
    {
        var overtType = MapCSharpTypeToOvert(prop.PropertyType);
        if (overtType is null)
        {
            sb.AppendLine($"// skipped {targetType.FullName}.{prop.Name}: "
                + $"property type '{prop.PropertyType.FullName}' is unsupported");
            return;
        }

        var name = ToSnakeCase(prop.Name);
        sb.Append("extern \"csharp\" fn ");
        sb.Append(name);
        sb.Append("()");
        if (effects.Length > 0) sb.Append($" !{{{string.Join(", ", effects)}}}");
        var retText = pure ? overtType : $"Result<{overtType}, IoError>";
        sb.Append($" -> {retText}");
        sb.AppendLine();
        sb.AppendLine($"    binds \"{targetType.FullName}.{prop.Name}\"");
        sb.AppendLine();
    }

    private static void EmitField(
        StringBuilder sb, Type targetType, FieldInfo field, string[] effects, bool pure)
    {
        var overtType = MapCSharpTypeToOvert(field.FieldType);
        if (overtType is null)
        {
            sb.AppendLine($"// skipped {targetType.FullName}.{field.Name}: "
                + $"field type '{field.FieldType.FullName}' is unsupported");
            return;
        }

        var name = ToSnakeCase(field.Name);
        sb.Append("extern \"csharp\" fn ");
        sb.Append(name);
        sb.Append("()");
        if (effects.Length > 0) sb.Append($" !{{{string.Join(", ", effects)}}}");
        var retText = pure ? overtType : $"Result<{overtType}, IoError>";
        sb.Append($" -> {retText}");
        sb.AppendLine();
        sb.AppendLine($"    binds \"{targetType.FullName}.{field.Name}\"");
        sb.AppendLine();
    }

    /// <summary>Compact signature suffix for overload disambiguation, using
    /// C# type names so <c>Abs(float)</c> and <c>Abs(double)</c> get distinct
    /// Overt names (both would map to Overt's <c>Float</c>). Names are the
    /// C# primitive aliases where known, or the type's Name for others.
    /// </summary>
    private static string OverloadSuffix(MethodInfo method)
    {
        var parts = method.GetParameters()
            .Select(p => CSharpTypeSuffix(p.ParameterType))
            .ToArray();
        return parts.Length == 0 ? "noargs" : string.Join("_", parts);
    }

    private static string CSharpTypeSuffix(Type t)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(int)) return "int";
        if (t == typeof(long)) return "long";
        if (t == typeof(short)) return "short";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(sbyte)) return "sbyte";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(ushort)) return "ushort";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(char)) return "char";
        // Fall back to the simple name, snake-cased, for any non-primitive.
        return ToSnakeCase(t.Name);
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
        string overtName, HashSet<string> topLevelNames)
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
            // Mangle parameter names that would shadow a free function in this
            // facade (Overt's no-shadowing rule, §3). Appending `_arg` keeps
            // the name readable while avoiding the collision.
            if (topLevelNames.Contains(paramName)) paramName = paramName + "_arg";
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
    /// handle in the MVP. <c>long</c> is deliberately NOT mapped to Overt's
    /// <c>Int</c>: Overt's Int lowers to C# <c>int</c> (32-bit), so a BCL
    /// method returning <c>long</c> would produce a mismatch at emit time.
    /// An <c>Int64</c> type is future work; until then <c>long</c>-returning
    /// methods are emitted as <c>// skipped</c>.</summary>
    private static string? MapCSharpTypeToOvert(Type t)
    {
        if (t == typeof(string)) return "String";
        if (t == typeof(int)) return "Int";
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
