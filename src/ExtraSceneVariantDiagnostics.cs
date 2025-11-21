using BepInEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ExtraSceneVariantDiagnostics
/// - Detects candidate "Normal"/"Destroyed" GameObject name pairs in a Scene.
/// - Determines which variant appears active.
/// - Scans components, string fields, UnityEngine.Object fields, NodeCanvas blackboards, and common manager fields
///   for occurrences of those candidate names and logs details for diagnostics.
/// - Writes a diagnostics file to Paths.ConfigPath for offline inspection.
/// 
/// Usage: ExtraSceneVariantDiagnostics.DetectAndDump(scene);
/// </summary>
public static class ExtraSceneVariantDiagnostics
{
    // tokens used to identify normal/destroyed variants; you can extend these lists.
    static readonly string[] NormalTokens = new[] { "Normal", "Intact", "Living", "Default", "NormalCierzo", "Normal" };
    static readonly string[] DestroyedTokens = new[] { "Destroyed", "Ruined", "Ruin", "Broken", "Damaged", "DestroyedCierzo" };

    public enum SceneVariant { Unknown, Normal, Destroyed }

    // Public entry: detect, log and write diagnostics file
    public static SceneVariant DetectAndDump(Scene scene)
    {
        try
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning($"[VariantDiag] Scene invalid or not loaded: '{scene.name}'");
                return SceneVariant.Unknown;
            }

            var filePath = Path.Combine(Paths.ConfigPath, $"ExtraSceneVariantDiagnostics_{SanitizeFileName(scene.name)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
            using (var sw = new StreamWriter(filePath, false))
            {
                void SWLog(string s) { sw.WriteLine(s); Debug.Log(s); }

                SWLog($"[VariantDiag] DetectAndDump for scene '{scene.name}'");
                SWLog($"[VariantDiag] RootCount={scene.rootCount}");

                // gather all transform names + mapping path -> transform
                var allTransforms = new List<Transform>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    allTransforms.AddRange(root.GetComponentsInChildren<Transform>(true));
                }

                SWLog($"[VariantDiag] Total transforms scanned: {allTransforms.Count}");

                var nameGroups = allTransforms.GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.ToList());

                // Find candidate names that include normal/destroyed tokens
                var normalCandidates = nameGroups.Keys.Where(n => NormalTokens.Any(tok => n.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                var destroyedCandidates = nameGroups.Keys.Where(n => DestroyedTokens.Any(tok => n.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

                SWLog($"[VariantDiag] Normal name candidates count={normalCandidates.Count}: {string.Join(", ", normalCandidates.Take(50))}");
                SWLog($"[VariantDiag] Destroyed name candidates count={destroyedCandidates.Count}: {string.Join(", ", destroyedCandidates.Take(50))}");

                // Attempt to pair candidates by removing token substrings and matching base tokens
                var pairs = FindCandidatePairs(nameGroups.Keys.ToList(), NormalTokens, DestroyedTokens);

                SWLog($"[VariantDiag] Candidate pairs found: {pairs.Count}");
                int idx = 0;
                foreach (var p in pairs)
                {
                    idx++;
                    SWLog($"[VariantDiag] Pair #{idx}: Normal='{p.normalName}'  Destroyed='{p.destroyedName}'");
                    // list up to 5 matching paths for each
                    List<string> normalPaths = nameGroups.TryGetValue(p.normalName, out var listN) ? listN.Take(5).Select(t => GetTransformPath(t)).ToList() : new List<string>();
                    List<string> destrPaths = nameGroups.TryGetValue(p.destroyedName, out var listD) ? listD.Take(5).Select(t => GetTransformPath(t)).ToList() : new List<string>();
                    SWLog($"[VariantDiag]   Normal paths sample: {string.Join(" | ", normalPaths)}");
                    SWLog($"[VariantDiag]   Destroyed paths sample: {string.Join(" | ", destrPaths)}");
                    // active state
                    var anyNormalActive = (nameGroups.TryGetValue(p.normalName, out var nn) && nn.Any(t => t.gameObject.activeInHierarchy));
                    var anyDestroyedActive = (nameGroups.TryGetValue(p.destroyedName, out var dd) && dd.Any(t => t.gameObject.activeInHierarchy));
                    SWLog($"[VariantDiag]   Active: NormalActive={anyNormalActive} DestroyedActive={anyDestroyedActive}");
                }

                // If no explicit pairs found, list some near-matches and top names for inspection
                if (pairs.Count == 0)
                {
                    SWLog("[VariantDiag] No explicit Normal/Destroyed pairs found by token rules. Listing top names:");
                    foreach (var kv in nameGroups.OrderByDescending(kv => kv.Value.Count).Take(40))
                        SWLog($"[VariantDiag]   Name='{kv.Key}' instances={kv.Value.Count} examplePath='{GetTransformPath(kv.Value.First())}'");
                }

                // Deep scan components and fields for the found candidate names (or common tokens)
                var interestingTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in pairs)
                {
                    if (!string.IsNullOrEmpty(p.normalName)) interestingTokens.Add(p.normalName);
                    if (!string.IsNullOrEmpty(p.destroyedName)) interestingTokens.Add(p.destroyedName);
                }
                // If we have no explicit tokens, fall back to tokens lists
                if (interestingTokens.Count == 0)
                {
                    foreach (var t in NormalTokens.Concat(DestroyedTokens)) interestingTokens.Add(t);
                }

                SWLog($"[VariantDiag] Doing deep component scan for tokens: {string.Join(", ", interestingTokens)}");

                var comps = new List<Component>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    comps.AddRange(root.GetComponentsInChildren<Component>(true));
                }
                SWLog($"[VariantDiag] Total components scanned: {comps.Count}");

                int matchCount = 0;
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    // scan string fields
                    var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var f in fields)
                    {
                        try
                        {
                            if (f.FieldType == typeof(string))
                            {
                                var val = f.GetValue(comp) as string;
                                if (!string.IsNullOrEmpty(val))
                                {
                                    foreach (var tok in interestingTokens)
                                    {
                                        if (val.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            SWLog($"[VariantDiag] STRING FIELD MATCH: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' field='{f.Name}' valueSnippet='{SafeSnippet(val)}'");
                                            matchCount++;
                                            break;
                                        }
                                    }
                                }
                            }
                            else if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                            {
                                var objVal = f.GetValue(comp) as UnityEngine.Object;
                                if (objVal != null)
                                {
                                    string oname = objVal.name;
                                    if (!string.IsNullOrEmpty(oname))
                                    {
                                        foreach (var tok in interestingTokens)
                                        {
                                            if (oname.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                SWLog($"[VariantDiag] OBJECT FIELD MATCH: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' field='{f.Name}' -> referencedObject='{oname}' referencedPath='{GetGameObjectPathSafe(objVal)}'");
                                                matchCount++;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            else if (typeof(System.Collections.IList).IsAssignableFrom(f.FieldType))
                            {
                                var listVal = f.GetValue(comp) as System.Collections.IList;
                                if (listVal != null)
                                {
                                    for (int i = 0; i < listVal.Count; i++)
                                    {
                                        try
                                        {
                                            var item = listVal[i];
                                            if (item == null) continue;
                                            if (item is string sItem)
                                            {
                                                foreach (var tok in interestingTokens)
                                                    if (sItem.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                                    {
                                                        SWLog($"[VariantDiag] LIST-STRING FIELD MATCH: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' field='{f.Name}[{i}]' value='{SafeSnippet(sItem)}'");
                                                        matchCount++; break;
                                                    }
                                            }
                                            else if (item is UnityEngine.Object uo)
                                            {
                                                foreach (var tok in interestingTokens)
                                                    if (!string.IsNullOrEmpty(uo.name) && uo.name.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                                    {
                                                        SWLog($"[VariantDiag] LIST-OBJECT FIELD MATCH: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' field='{f.Name}[{i}]' -> referencedObject='{uo.name}' referencedPath='{GetGameObjectPathSafe(uo)}'");
                                                        matchCount++; break;
                                                    }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // also check properties (public readable)
                    var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(p => p.CanRead);
                    foreach (var p in props)
                    {
                        try
                        {
                            if (p.PropertyType == typeof(string))
                            {
                                var val = p.GetValue(comp, null) as string;
                                if (!string.IsNullOrEmpty(val))
                                {
                                    foreach (var tok in interestingTokens)
                                    {
                                        if (val.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            SWLog($"[VariantDiag] STRING PROP MATCH: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' prop='{p.Name}' valueSnippet='{SafeSnippet(val)}'");
                                            matchCount++; break;
                                        }
                                    }
                                }
                            }
                            else if (typeof(UnityEngine.Object).IsAssignableFrom(p.PropertyType))
                            {
                                var objVal = p.GetValue(comp, null) as UnityEngine.Object;
                                if (objVal != null)
                                {
                                    foreach (var tok in interestingTokens)
                                    {
                                        if (!string.IsNullOrEmpty(objVal.name) && objVal.name.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            SWLog($"[VariantDiag] OBJECT PROP MATCH: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' prop='{p.Name}' -> '{objVal.name}'");
                                            matchCount++; break;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                SWLog($"[VariantDiag] Deep field scan matches found: {matchCount}");

                // Additional special checks: search for common manager fields across all components (m_previousScene, m_lastLoadedLevelName, m_lastLoadedScene)
                SWLog($"[VariantDiag] Searching for common manager fields (m_previousScene, m_lastLoadedLevelName, m_lastLoadedScene)...");
                int mgrMatches = 0;
                var wantFields = new[] { "m_previousScene", "m_lastLoadedLevelName", "m_lastLoadedScene", "m_lastLoadedLevel" };
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    foreach (var fn in wantFields)
                    {
                        try
                        {
                            var f = t.GetField(fn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (f != null && f.FieldType == typeof(string))
                            {
                                var v = f.GetValue(comp) as string;
                                SWLog($"[VariantDiag] Manager field: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' field='{fn}' value='{v}'");
                                mgrMatches++;
                            }
                        }
                        catch { }
                    }
                }
                SWLog($"[VariantDiag] Manager-like fields found: {mgrMatches}");

                // NodeCanvas blackboard serialized fields scan (string fields named like _serializedBlackboard)
                SWLog("[VariantDiag] Scanning for serialized blackboard strings...");
                int serMatches = 0;
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var f in fields)
                    {
                        if (f.FieldType != typeof(string)) continue;
                        if (!f.Name.ToLowerInvariant().Contains("serialized") && !f.Name.ToLowerInvariant().Contains("blackboard")) continue;
                        try
                        {
                            var val = f.GetValue(comp) as string;
                            if (string.IsNullOrEmpty(val)) continue;
                            foreach (var tok in interestingTokens)
                            {
                                if (val.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    SWLog($"[VariantDiag] Serialized blackboard match: GO='{GetGameObjectNameSafe(comp.gameObject)}' path='{GetGameObjectPath(comp.gameObject)}' component='{t.FullName}' field='{f.Name}' contains token='{tok}' len={val.Length}");
                                    serMatches++; break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                SWLog($"[VariantDiag] Serialized blackboard string matches: {serMatches}");

                SWLog($"[VariantDiag] Diagnostic dump complete. Written to: {filePath}");
            }

            return DetermineSceneVariantFromPairs(scene);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[VariantDiag] DetectAndDump top-level exception: " + ex);
            return SceneVariant.Unknown;
        }
    }

    // Helper: determine final SceneVariant by scanning the scene for any Normal/Destroyed pair active state
    static SceneVariant DetermineSceneVariantFromPairs(Scene scene)
    {
        try
        {
            var pairs = FindCandidatePairsForScene(scene);
            foreach (var p in pairs)
            {
                // check if any normal instances active
                var normalActive = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .Any(t => string.Equals(t.name, p.normalName, StringComparison.Ordinal) && t.gameObject.activeInHierarchy);
                var destroyedActive = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .Any(t => string.Equals(t.name, p.destroyedName, StringComparison.Ordinal) && t.gameObject.activeInHierarchy);

                if (normalActive && !destroyedActive) return SceneVariant.Normal;
                if (destroyedActive && !normalActive) return SceneVariant.Destroyed;
                // If both present, continue checking others
            }
        }
        catch { }
        return SceneVariant.Unknown;
    }

    // find candidate pairs given full scene names set using configured tokens
    static List<(string normalName, string destroyedName)> FindCandidatePairs(IEnumerable<string> names)
    {
        return FindCandidatePairs(names, NormalTokens, DestroyedTokens);
    }

    // overloaded: find candidate pairs using provided tokens
    static List<(string normalName, string destroyedName)> FindCandidatePairs(IEnumerable<string> names, IEnumerable<string> normalTokens, IEnumerable<string> destroyedTokens)
    {
        var namesList = names.ToList();
        var pairs = new List<(string normalName, string destroyedName)>();

        // Strategy: for each normal-token name, try to form counterpart name by replacing normal token
        foreach (var n in namesList)
        {
            foreach (var normalTok in normalTokens)
            {
                if (n.IndexOf(normalTok, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // attempt replacements
                foreach (var destTok in destroyedTokens)
                {
                    var candidate = ReplaceTokenInsensitive(n, normalTok, destTok);
                    if (namesList.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        pairs.Add((n, candidate));
                        break;
                    }
                    // try prefix/suffix variants (e.g., NormalCierzo -> CierzoDestroyed)
                    var alt = TrySwapPrefixSuffix(n, normalTok);
                    if (alt != null)
                    {
                        var candidate2 = ReplaceTokenInsensitive(alt, normalTok, destTok);
                        if (namesList.Contains(candidate2, StringComparer.OrdinalIgnoreCase))
                        {
                            pairs.Add((alt, candidate2));
                            break;
                        }
                    }
                }
            }
        }

        // dedupe by names
        pairs = pairs.GroupBy(p => (p.normalName.ToLowerInvariant(), p.destroyedName.ToLowerInvariant()))
                     .Select(g => g.First()).ToList();

        return pairs;
    }

    // convenience: find pairs directly from scene
    static List<(string normalName, string destroyedName)> FindCandidatePairsForScene(Scene scene)
    {
        var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root == null) continue;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!string.IsNullOrEmpty(t.name)) allNames.Add(t.name);
        }
        return FindCandidatePairs(allNames);
    }

    static string ReplaceTokenInsensitive(string source, string oldToken, string newToken)
    {
        int idx = source.IndexOf(oldToken, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return source;
        return source.Substring(0, idx) + newToken + source.Substring(idx + oldToken.Length);
    }

    static string TrySwapPrefixSuffix(string s, string token)
    {
        // If name contains token as prefix or suffix try swap: NormalCierzo -> CierzoNormal
        int idx = s.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var before = s.Substring(0, idx);
        var after = s.Substring(idx + token.Length);
        var swapped = (before + after) + token;
        if (swapped == s) return null;
        return swapped;
    }

    static string GetTransformPath(Transform t)
    {
        try
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch { return "<path-error>"; }
    }

    static string GetGameObjectPath(GameObject go)
    {
        if (go == null) return "<null>";
        return GetTransformPath(go.transform);
    }

    static string GetGameObjectPathSafe(UnityEngine.Object o)
    {
        try
        {
            if (o is GameObject g) return GetGameObjectPath(g);
            if (o is Component c) return GetGameObjectPath(c.gameObject);
            return $"Object({o.name})";
        }
        catch { return $"Object({o?.name ?? "<null>"})"; }
    }

    static string GetGameObjectNameSafe(GameObject go)
    {
        try { return go?.name ?? "<null>"; } catch { return "<null>"; }
    }

    static string SafeSnippet(string s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }

    static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}