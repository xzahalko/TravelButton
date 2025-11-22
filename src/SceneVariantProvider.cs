using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Helper to read NodeCanvas blackboard variables and determine the currently active
/// variant for a scene (e.g. CierzoNormal / CierzoDestroyed).
/// Returns (rawVariableName, normalizedVariantLabel) or (null,null) if not found.
/// </summary>
public static class SceneVariantProvider
{
    // Returns (rawVariableName, normalizedVariant) or (null,null) if not found.
    // sceneToken: scene.name or a short token like "Cierzo" (case-insensitive).
    public static (string raw, string normalized) GetActiveVariantForScene(string sceneToken)
    {
        if (string.IsNullOrEmpty(sceneToken)) return (null, null);
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type bbType = null;
            foreach (var a in assemblies)
            {
                try
                {
                    foreach (var t in a.GetTypes())
                    {
                        if (t == null) continue;
                        if (t.FullName == "NodeCanvas.Framework.Blackboard" || t.Name == "Blackboard")
                        {
                            bbType = t;
                            break;
                        }
                    }
                }
                catch { }
                if (bbType != null) break;
            }
            if (bbType == null) return (null, null);

            // find all blackboards in scene
            var bbs = UnityEngine.Object.FindObjectsOfType(bbType);
            var binding = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            foreach (var bb in bbs)
            {
                // prefer an actual runtime dictionary 'variables' or '_variables' on the Blackboard
                object dictObj = null;
                var dictField = bbType.GetField("_variables", binding) ?? bbType.GetField("variables", binding);
                if (dictField != null) dictObj = dictField.GetValue(bb);
                else
                {
                    var dictProp = bbType.GetProperty("variables", binding) ?? bbType.GetProperty("_variables", binding);
                    if (dictProp != null) dictObj = dictProp.GetValue(bb, null);
                }

                // if dictionary found and is IDictionary, iterate
                if (dictObj is IDictionary dict)
                {
                    foreach (DictionaryEntry de in dict)
                    {
                        var varName = de.Key as string;
                        if (string.IsNullOrEmpty(varName)) continue;

                        // only consider variables that mention sceneToken (e.g. 'CierzoNormal')
                        if (varName.IndexOf(sceneToken, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        var varObj = de.Value;
                        var runtimeVal = TryGetRuntimeValue(varObj, binding);
                        if (runtimeVal != null)
                        {
                            GameObject go = null;
                            if (runtimeVal is GameObject goVal) go = goVal;
                            else if (runtimeVal is Component comp) go = comp.gameObject;

                            if (go != null)
                            {
                                // active GO -> current variant
                                if (go.activeInHierarchy)
                                    return (varName, NormalizeVariantName(varName, sceneToken));
                            }
                        }
                    }
                }
                else
                {
                    // fallback: try to inspect serialized JSON to find tokens if active-state not available
                    var serField = bbType.GetField("_serializedBlackboard", binding);
                    if (serField != null)
                    {
                        var json = serField.GetValue(bb) as string;
                        if (!string.IsNullOrEmpty(json))
                        {
                            // quick find tokens that include sceneToken
                            var tokens = new List<string>();
                            foreach (Match m in Regex.Matches(json, @"""([A-Za-z0-9_ \-]+)"""))
                            {
                                var token = m.Groups[1].Value;
                                if (token.IndexOf(sceneToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                    tokens.Add(token);
                            }
                            if (tokens.Count > 0)
                                return (tokens[0], NormalizeVariantName(tokens[0], sceneToken)); // best-effort
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneVariantProvider] exception: " + ex);
        }
        return (null, null);
    }

    // Try to get the runtime value from NodeCanvas Variable object via reflection:
    // common backing field names: "_value", "value", "m_value"
    static object TryGetRuntimeValue(object varObj, BindingFlags binding)
    {
        if (varObj == null) return null;
        try
        {
            var vt = varObj.GetType();
            var fv = vt.GetField("_value", binding) ?? vt.GetField("value", binding) ?? vt.GetField("m_value", binding);
            if (fv != null) return fv.GetValue(varObj);
            var pv = vt.GetProperty("value", binding) ?? vt.GetProperty("Value", binding);
            if (pv != null) return pv.GetValue(varObj, null);
            return null;
        }
        catch { return null; }
    }

    // Normalize a variable like "CierzoNormal" to "Normal", or "Cierzo_Destroyed" -> "Destroyed".
    static string NormalizeVariantName(string varName, string sceneToken)
    {
        if (string.IsNullOrEmpty(varName)) return null;
        string s = varName;

        // remove scene token prefix if present
        if (!string.IsNullOrEmpty(sceneToken) && s.StartsWith(sceneToken, StringComparison.OrdinalIgnoreCase))
            s = s.Substring(sceneToken.Length);

        // remove non-alpha chars and underscores/spaces
        s = Regex.Replace(s, @"[^A-Za-z0-9]+", " ").Trim();

        var lower = s.ToLowerInvariant();
        if (lower.Contains("normal")) return "Normal";
        if (lower.Contains("destroy") || lower.Contains("broken")) return "Destroyed";
        if (Regex.IsMatch(lower, @"\bdefault\b")) return "Normal";

        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
            return char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1);
        return s;
    }

    // Diagnostic: dump blackboards and variable names (non-destructive)
    public static void DumpBlackboardsDiagnostics(string sceneTokenHint = null)
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type bbType = null;
            foreach (var a in assemblies)
            {
                try
                {
                    foreach (var t in a.GetTypes())
                    {
                        if (t == null) continue;
                        if (t.FullName == "NodeCanvas.Framework.Blackboard" || t.Name == "Blackboard")
                        {
                            bbType = t;
                            break;
                        }
                    }
                }
                catch { }
                if (bbType != null) break;
            }
            if (bbType == null)
            {
                Debug.Log("[SVP_DIAG] Blackboard type not found.");
                return;
            }

            var bbs = UnityEngine.Object.FindObjectsOfType(bbType);
            Debug.Log($"[SVP_DIAG] Found {bbs.Length} Blackboard instances. sceneTokenHint='{sceneTokenHint}'");
            var binding = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var dictField = bbType.GetField("_variables", binding) ?? bbType.GetField("variables", binding);
            var serField = bbType.GetField("_serializedBlackboard", binding);

            int idx = 0;
            foreach (var bb in bbs)
            {
                string goName = "(no-go)";
                try { var comp = bb as UnityEngine.Component; if (comp != null) goName = comp.gameObject.name; } catch { }
                Debug.Log($"[SVP_DIAG] Blackboard[{idx}] GO='{goName}' type={bb.GetType().FullName}");

                object dictObj = dictField != null ? dictField.GetValue(bb) : null;
                if (dictObj is System.Collections.IDictionary idict)
                {
                    Debug.Log($"[SVP_DIAG]   variables dictionary entries = {idict.Count}");
                    foreach (System.Collections.DictionaryEntry de in idict)
                    {
                        var varName = de.Key as string;
                        object runtimeVal = null;
                        try
                        {
                            runtimeVal = TryGetRuntimeValue(de.Value, binding);
                        }
                        catch { }
                        string activeStr = "(null)";
                        if (runtimeVal is GameObject go) activeStr = $"GO name='{go.name}' active={go.activeInHierarchy}";
                        else if (runtimeVal is Component comp) activeStr = $"Component on GO='{comp.gameObject.name}' active={comp.gameObject.activeInHierarchy}";
                        else if (runtimeVal != null) activeStr = $"Type={runtimeVal.GetType().FullName}";

                        Debug.Log($"[SVP_DIAG]     var='{varName}' -> {activeStr}");
                    }
                }
                else
                {
                    var json = serField != null ? serField.GetValue(bb) as string : null;
                    if (!string.IsNullOrEmpty(json))
                    {
                        var snippet = json.Length > 800 ? json.Substring(0, 800) + " ...(truncated)..." : json;
                        Debug.Log($"[SVP_DIAG]   serialized JSON snippet: {snippet}");
                    }
                    else
                    {
                        Debug.Log($"[SVP_DIAG]   no variables dictionary and no serialized JSON present.");
                    }
                }

                idx++;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SVP_DIAG] exception: " + ex);
        }
    }
}