using Overt.Compiler.Modules;

namespace Overt.Backend.CSharp;

/// <summary>
/// Adapter between the platform-agnostic <see cref="ExternUseExpander"/> and
/// the C# back end's <see cref="BindGenerator"/>. Hosts (CLI, MSBuild task,
/// tests) wire this into the compile pipeline so that
/// <c>extern "csharp" use "..."</c> declarations get expanded against
/// .NET reflection.
///
/// Sibling backends (Go, Rust) ship their own resolvers in their own
/// projects; the compiler stays free of any backend reference.
/// </summary>
public static class CSharpExternUseResolver
{
    /// <summary>
    /// Resolver delegate suitable for passing to
    /// <see cref="ExternUseExpander.Expand"/>. Returns null for any
    /// platform other than <c>"csharp"</c>; for <c>"csharp"</c>, looks
    /// up the target type name across all currently-loaded assemblies and
    /// invokes <see cref="BindGenerator"/> on the result.
    /// </summary>
    public static string? Resolve(string platform, string target)
    {
        if (platform != "csharp")
        {
            // Other-platform `use` declarations are not this resolver's
            // problem. Return null so the expander emits a clean
            // "no resolver for platform" diagnostic upstream.
            return null;
        }

        var type = ResolveType(target);
        if (type is null)
        {
            return null;
        }

        // Synthesize a stable module name from the target type. The name
        // is internal to the expander pipeline; downstream passes only see
        // the spliced declarations, not the synthetic module wrapper.
        var moduleName = "__overt_extern_csharp_" + target.Replace('.', '_');
        return BindGenerator.Generate(moduleName, type);
    }

    /// <summary>
    /// Find a .NET type by full name across every assembly currently loaded
    /// in the AppDomain. Returns null if no assembly exposes the name.
    /// Hosts wanting to resolve types from a specific assembly (e.g. the
    /// consumer's PackageReferences) should preload those assemblies before
    /// invoking the expander; <c>Type.GetType(string)</c> on its own only
    /// searches the calling assembly and mscorlib.
    /// </summary>
    private static Type? ResolveType(string fullName)
    {
        // Direct lookup first; cheap when the type is in mscorlib or the
        // calling assembly.
        var direct = Type.GetType(fullName);
        if (direct is not null)
        {
            return direct;
        }

        // Fall back to scanning every loaded assembly. Assemblies pulled in
        // via PreloadCommonBclAssemblies (or via consumer dependencies in
        // the MSBuild task) become visible here.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic)
            {
                continue;
            }
            try
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t is not null)
                {
                    return t;
                }
            }
            catch
            {
                // Some dynamic / reflection-only assemblies throw on
                // GetType; ignore and continue. Worst case is a null
                // result and an OV0170 from the expander.
            }
        }

        return null;
    }
}
