using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

public static class SceneVariantProvider
{
    // Returns (rawVariableName, normalizedVariant) or (null,null) if not found.
    // sceneToken: short scene identifier like "Cierzo" (case-insensitive).
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

                        // only consider variables matching scene token OR variables that include a scene-like substring
                        if (!varName.IndexOf(sceneToken, StringComparison.OrdinalIgnoreCase).Equals(-1) ||
                            varName.IndexOf(sceneToken, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var varObj = de.Value;
                            var runtimeVal = TryGetRuntimeValue(varObj, binding);
                            if (runtimeVal != null)
                            {
                                GameObject go = null;
                                if (runtimeVal is GameObject goVal) go = goVal;
                                else if (runtimeVal is Component comp) go = comp.gameObject;

                                if (go != null)
                                {
                                    if (go.activeInHierarchy)
                                    {
                                        // found active variant
                                        return (varName, NormalizeVariantName(varName, sceneToken));
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // fallback: try to inspect serialized JSON if present, but that won't say active state.
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
                                if (token.IndexOf(sceneToken, StringComparison.OrdinalIgnoreCase) >= 0) tokens.Add(token);
                            }
                            if (tokens.Count > 0)
                            {
                                // return first match as raw (no active-state info)
                                var raw = tokens[0];
                                return (raw, NormalizeVariantName(raw, sceneToken));
                            }
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

        // common mapping
        var lower = s.ToLowerInvariant();
        if (lower.Contains("normal")) return "Normal";
        if (lower.Contains("destroy") || lower.Contains("broken")) return "Destroyed";
        if (Regex.IsMatch(lower, @"\bdefault\b")) return "Normal";
        // fallback: capitalise first token
        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
            return char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1);
        return s;
    }
}