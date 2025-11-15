using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Safe reflection helpers: never throw on missing types, and provide a best-effort resolution fallback.
/// Also deduplicates missing-type warnings so the log won't be spammed.
/// </summary>
public static class ReflectionUtils
{
    // remember which type names we've already warned about so we only warn once
    private static readonly HashSet<string> warnedMissingTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Safely attempts to resolve a Type by assembly-qualified name or simple name.
    /// Returns null when the type cannot be resolved.
    /// Does not throw TypeLoadException/FileNotFoundException; those are swallowed.
    /// </summary>
    public static Type SafeGetType(string assemblyQualifiedTypeName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedTypeName)) return null;

        // 1) Try Type.GetType directly (assembly-qualified names)
        try
        {
            // Use the throwOnError=false overload where available; Type.GetType(string) can still throw in some runtimes so catch broadly.
            var t = Type.GetType(assemblyQualifiedTypeName, false);
            if (t != null) return t;
        }
        catch
        {
            // swallow; we'll try other strategies
        }

        // 2) Extract simple name (e.g., "UnityEngine.Input" from "UnityEngine.Input, UnityEngine.CoreModule, ...")
        string simpleName = assemblyQualifiedTypeName;
        int commaIndex = assemblyQualifiedTypeName.IndexOf(',');
        if (commaIndex >= 0) simpleName = assemblyQualifiedTypeName.Substring(0, commaIndex).Trim();

        // 3) Scan all loaded assemblies for the simple name (best-effort)
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = null;
                try
                {
                    found = asm.GetType(simpleName, false, false);
                }
                catch
                {
                    // ignore assembly load issues
                }
                if (found != null) return found;
            }
        }
        catch
        {
            // swallow; we cannot resolve the type
        }

        // 4) Log a single sanitized warning the first time we fail to resolve this name (do not spam)
        try
        {
            lock (warnedMissingTypes)
            {
                if (!warnedMissingTypes.Contains(assemblyQualifiedTypeName))
                {
                    warnedMissingTypes.Add(assemblyQualifiedTypeName);
                    // Use TravelButtonPlugin if available (it is safe), otherwise fallback to UnityEngine.Debug
                    try
                    {
                        TBLog.Warn($"Reflection: could not resolve type '{assemblyQualifiedTypeName}'. Some optional features may be disabled.");
                    }
                    catch
                    {
                        try { UnityEngine.Debug.LogWarning($"[TravelButton] Reflection: could not resolve type '{assemblyQualifiedTypeName}'."); } catch { }
                    }
                }
            }
        }
        catch { /* swallow logging errors */ }

        return null;
    }
}
