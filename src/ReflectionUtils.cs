using System;
using System.IO;

/// <summary>
/// Small helper to perform reflection Type lookups in a safe way:
/// - catches TypeLoadException, FileNotFoundException and other common reflection exceptions
/// - returns null when the type cannot be resolved instead of throwing
/// - optionally (via the last parameter) allow a single sanitized log message when resolution fails
/// </summary>
public static class ReflectionUtils
{
    /// <summary>
    /// Safely attempts to resolve a Type by name. Returns null on failure.
    /// </summary>
    public static Type SafeGetType(string assemblyQualifiedTypeName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedTypeName)) return null;
        try
        {
            return Type.GetType(assemblyQualifiedTypeName);
        }
        catch (TypeLoadException)
        {
            // Expected when a type token refers to something not present in this runtime.
            return null;
        }
        catch (FileNotFoundException)
        {
            // Expected when an assembly referenced in the name is not present.
            return null;
        }
        catch (Exception)
        {
            // Be conservative: do not let reflection exceptions bubble to top and spam log.
            return null;
        }
    }

    /// <summary>
    /// Try a bare type name first (Type.GetType) and fall back to scanning loaded assemblies (best-effort).
    /// Returns null if not found.
    /// </summary>
    public static Type SafeResolveTypeByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // First try Type.GetType (handles assembly-qualified names)
        var t = SafeGetType(name);
        if (t != null) return t;

        try
        {
            // Fallback: scan loaded assemblies for a matching type by full name
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                try
                {
                    var candidate = asm.GetType(name, false, false);
                    if (candidate != null) return candidate;
                }
                catch { /* ignore assembly load errors */ }
            }
        }
        catch { /* swallow */ }

        return null;
    }
}