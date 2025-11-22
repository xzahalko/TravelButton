using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// CitiesJsonManager - safer update logic for variant names and lastKnownVariant.
/// - Avoids persisting clearly invalid variant names (procedural chunk labels, coordinate snippets).
/// - Only writes lastKnownVariant when it's not "Unknown" (configurable).
/// - Writes atomically and logs decisions.
/// </summary>
public static class CitiesJsonManager
{
    // If true, don't write lastKnownVariant when it's "Unknown"
    const bool SkipUnknownLastKnownVariant = true;

    /// <summary>
    /// Overload that accepts detector confidence and applies a conservative persistence policy:
    /// - If confidence >= Medium -> persist names + lastKnownVariant (via existing 4-arg method).
    /// - If confidence < Medium -> persist only plausible names (no lastKnownVariant).
    /// </summary>
    public static bool UpdateCityVariantData(string sceneName, string normalName, string destroyedName, string lastKnownVariant, ExtraSceneVariantDetection.VariantConfidence confidence)
    {
        try
        {
            // If confidence is high enough, forward to the existing 4-arg API to persist everything.
            if (confidence >= ExtraSceneVariantDetection.VariantConfidence.Medium)
            {
                return UpdateCityVariantData(sceneName, normalName, destroyedName, lastKnownVariant);
            }

            // Low confidence: only persist plausible names (avoid writing lastKnownVariant).
            string normalToPersist = IsPlausibleVariantName(normalName) ? normalName : null;
            string destroyedToPersist = IsPlausibleVariantName(destroyedName) ? destroyedName : null;

            // Call existing 4-arg method (lastKnownVariant = null)
            return UpdateCityVariantData(sceneName, normalToPersist, destroyedToPersist, null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CitiesJsonManager] Exception in confidence-aware overload: " + ex);
            return false;
        }
    }

    // Replace your current UpdateCityVariantData method with this instrumented version
    public static bool UpdateCityVariantData(string sceneName, string normalName, string destroyedName, string lastKnownVariant, bool debugForcePersist = false)
    {
        try
        {
            string path = TravelButtonPlugin.GetCitiesJsonPath();
            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("[CitiesJsonManager] No TravelButton_Cities.json found in candidate locations.");
                return false;
            }

            Debug.Log($"[CitiesJsonManager] (Diag) Loading cities JSON from: {path}");
            string originalText = File.ReadAllText(path);
            Debug.Log($"[CitiesJsonManager] (Diag) Original file length={originalText?.Length ?? 0}");

            JToken root = JToken.Parse(originalText);

            // Normalize root -> cities array
            JArray citiesArray = null;
            bool rootWasObjectWithCities = false;
            if (root.Type == JTokenType.Object && root["cities"] != null && root["cities"].Type == JTokenType.Array)
            {
                citiesArray = (JArray)root["cities"];
                rootWasObjectWithCities = true;
            }
            else if (root.Type == JTokenType.Array)
            {
                citiesArray = (JArray)root;
            }
            else if (root.Type == JTokenType.Object)
            {
                citiesArray = new JArray(root);
            }
            else
            {
                Debug.LogWarning("[CitiesJsonManager] (Diag) Unexpected JSON root type.");
                return false;
            }

            var cityToken = citiesArray.FirstOrDefault(c =>
            {
                var sn = c["sceneName"]?.ToString();
                return !string.IsNullOrEmpty(sn) && string.Equals(sn, sceneName, StringComparison.OrdinalIgnoreCase);
            });

            if (cityToken == null)
            {
                Debug.Log($"[CitiesJsonManager] (Diag) No city entry found with sceneName='{sceneName}' in {path}.");
                return false;
            }

            Debug.Log($"[CitiesJsonManager] (Diag) Found city token: {cityToken.ToString(Formatting.None)}");

            bool changed = false;

            // For diagnostics, always compute current values and log them
            string curNormal = cityToken["variantNormalName"]?.ToString();
            string curDestroyed = cityToken["variantDestroyedName"]?.ToString();
            string curLast = cityToken["lastKnownVariant"]?.ToString();

            Debug.Log($"[CitiesJsonManager] (Diag) Current: variantNormalName='{curNormal ?? "<null>"}' variantDestroyedName='{curDestroyed ?? "<null>"}' lastKnownVariant='{curLast ?? "<null>"}'");
            Debug.Log($"[CitiesJsonManager] (Diag) Incoming: normalName='{normalName ?? "<null>"}' destroyedName='{destroyedName ?? "<null>"}' lastKnownVariant='{lastKnownVariant ?? "<null>"}' forcePersist={debugForcePersist}");

            // Validate names with heuristics to avoid procedural/coordinate labels
            bool normalPlausible = IsPlausibleVariantName(normalName);
            bool destroyedPlausible = IsPlausibleVariantName(destroyedName);

            // Decide persist of normalName
            if (!string.IsNullOrEmpty(normalName) && (debugForcePersist || normalPlausible))
            {
                if (!string.Equals(curNormal, normalName, StringComparison.Ordinal))
                {
                    cityToken["variantNormalName"] = normalName;
                    Debug.Log($"[CitiesJsonManager] (Diag) Will update variantNormalName: '{curNormal ?? "<null>"}' -> '{normalName}'");
                    changed = true;
                }
                else
                {
                    Debug.Log("[CitiesJsonManager] (Diag) variantNormalName unchanged.");
                }
            }
            else if (!string.IsNullOrEmpty(normalName))
            {
                Debug.Log($"[CitiesJsonManager] (Diag) Skipping variantNormalName persist (implausible): '{normalName}'");
            }

            // Decide persist of destroyedName
            if (!string.IsNullOrEmpty(destroyedName) && (debugForcePersist || destroyedPlausible))
            {
                if (!string.Equals(curDestroyed, destroyedName, StringComparison.Ordinal))
                {
                    cityToken["variantDestroyedName"] = destroyedName;
                    Debug.Log($"[CitiesJsonManager] (Diag) Will update variantDestroyedName: '{curDestroyed ?? "<null>"}' -> '{destroyedName}'");
                    changed = true;
                }
                else
                {
                    Debug.Log("[CitiesJsonManager] (Diag) variantDestroyedName unchanged.");
                }
            }
            else if (!string.IsNullOrEmpty(destroyedName))
            {
                Debug.Log($"[CitiesJsonManager] (Diag) Skipping variantDestroyedName persist (implausible): '{destroyedName}'");
            }

            // Decide persist of lastKnownVariant
            if (!string.IsNullOrEmpty(lastKnownVariant))
            {
                if (SkipUnknownLastKnownVariant && string.Equals(lastKnownVariant, "Unknown", StringComparison.OrdinalIgnoreCase) && !debugForcePersist)
                {
                    Debug.Log($"[CitiesJsonManager] (Diag) Skipping persist of lastKnownVariant='Unknown'");
                }
                else
                {
                    if (!string.Equals(curLast, lastKnownVariant, StringComparison.Ordinal))
                    {
                        cityToken["lastKnownVariant"] = lastKnownVariant;
                        Debug.Log($"[CitiesJsonManager] (Diag) Will update lastKnownVariant: '{curLast ?? "<null>"}' -> '{lastKnownVariant}'");
                        changed = true;
                    }
                    else
                    {
                        Debug.Log("[CitiesJsonManager] (Diag) lastKnownVariant unchanged.");
                    }
                }
            }

            if (!changed && !debugForcePersist)
            {
                Debug.Log("[CitiesJsonManager] (Diag) No changes required to city data (or changes skipped as implausible).");
                return true;
            }

            // If we are here and debugForcePersist==true, we may force fields even if they are implausible.
            if (debugForcePersist)
            {
                // Ensure fields are present even if empty string values supplied
                var cityObj = cityToken as JObject;
                if (cityObj != null)
                {
                    if (cityObj["variantNormalName"] == null) cityObj["variantNormalName"] = curNormal ?? "";
                    if (cityObj["variantDestroyedName"] == null) cityObj["variantDestroyedName"] = curDestroyed ?? "";
                    if (cityObj["lastKnownVariant"] == null) cityObj["lastKnownVariant"] = curLast ?? "";
                }
                else
                {
                    Debug.LogWarning("[CitiesJsonManager] (Diag) cityToken is not a JObject; cannot ensure fields.");
                }
                // (we already set any changes above)
            }

            // Prepare new text
            string newText;
            if (rootWasObjectWithCities)
                newText = root.ToString(Formatting.Indented);
            else
                newText = citiesArray.ToString(Formatting.Indented);

            Debug.Log($"[CitiesJsonManager] (Diag) New JSON length={newText.Length}. Attempting atomic write...");

            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, newText);
                Debug.Log($"[CitiesJsonManager] (Diag) Wrote temp file: {tmp}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CitiesJsonManager] (Diag) Failed to write temp file '{tmp}': {ex}");
                return false;
            }

            bool replaced = false;
            try
            {
                File.Replace(tmp, path, null);
                replaced = true;
                Debug.Log($"[CitiesJsonManager] (Diag) File.Replace succeeded for {path}");
            }
            catch (Exception exReplace)
            {
                Debug.LogWarning($"[CitiesJsonManager] (Diag) File.Replace failed: {exReplace}. Attempting fallback File.Copy");
                try
                {
                    File.Copy(tmp, path, true);
                    replaced = true;
                    Debug.Log($"[CitiesJsonManager] (Diag) Fallback File.Copy succeeded for {path}");
                }
                catch (Exception exCopy)
                {
                    Debug.LogWarning($"[CitiesJsonManager] (Diag) Fallback File.Copy failed: {exCopy}. Attempting File.WriteAllText final fallback");
                    try
                    {
                        File.WriteAllText(path, newText);
                        replaced = true;
                        Debug.Log($"[CitiesJsonManager] (Diag) Final fallback File.WriteAllText succeeded for {path}");
                    }
                    catch (Exception exWrite)
                    {
                        Debug.LogWarning($"[CitiesJsonManager] (Diag) Final fallback File.WriteAllText failed: {exWrite}");
                        replaced = false;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            if (!replaced)
            {
                Debug.LogWarning($"[CitiesJsonManager] (Diag) Failed to persist updated JSON to '{path}'.");
                return false;
            }

            Debug.Log($"[CitiesJsonManager] (Diag) Successfully updated JSON at {path}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CitiesJsonManager] (Diag) Exception updating JSON: " + ex);
            return false;
        }
    }

    // Heuristic to filter out procedural labels and coordinate-like names
    static bool IsPlausibleVariantName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var s = name.Trim();

        // Reject obvious procedural chunk labels / placeholders
        var lower = s.ToLowerInvariant();
        if (lower.Contains("default chunk") || (lower.Contains("chunk") && Regex.IsMatch(lower, @"chunk\s*[:\(]"))) return false;
        if (lower.Contains("defaultchunk") || lower.StartsWith("chunk_")) return false;
        if (lower.Contains("default chunk :") || lower.Contains("debug") || lower.Contains("placeholder")) return false;

        // Reject coordinate-like names: "(0.0, 0.0)" etc
        if (Regex.IsMatch(s, @"\(\s*-?\d+(\.\d+)?\s*,\s*-?\d+(\.\d+)?\s*\)")) return false;

        // Reject names that look like raw transform path fragments like "Default Chunk : (0.0, 0.0)"
        if (s.IndexOf(':') >= 0 && Regex.IsMatch(s, @"\:\s*\(")) return false;

        // Accept otherwise, but also ensure it contains at least one alphabetic character
        if (!Regex.IsMatch(s, @"[A-Za-z]")) return false;

        // Length bounds (avoid extremely long blobs)
        if (s.Length < 2 || s.Length > 200) return false;

        // Looks plausible
        return true;
    }

    // Try to update city variant data and persist. Returns true when persisted (or runtime updated + persist attempted).
    // Order of attempts:
    //  1) Call CitiesJsonManager.UpdateCityVariantData(...) by reflection (if available).
    //  2) Update runtime TravelButton.Cities entry so runtime behavior is corrected immediately.
    //  3) Call TravelButton.PersistCitiesToPluginFolder() if a zero-argument overload exists.
    //  4) As a last resort, update the canonical JSON file on disk directly using Newtonsoft.Json (via reflection)
    //     to avoid relying on specific internal signatures. This step returns true if the file was changed.
    // Full-refactor signature:
    // variants: list of known variant ids (may be empty). lastKnownVariant: concrete variant id (should be in variants if possible).
    public static bool TryUpdateAndPersist(string sceneName, List<string> variants, string lastKnownVariant, object confidenceToken = null)
    {
        try
        {
            TBLog.Info($"CitiesJsonManagerCompat: TryUpdateAndPersist(scene='{sceneName}', variants=[{(variants != null ? string.Join(",", variants) : "")}], lastKnown='{lastKnownVariant}')");

            bool attemptedUpdate = false;
            bool persisted = false;

            // 1) legacy runtime bridge (best-effort)
            try
            {
                var cjType = Type.GetType("CitiesJsonManager, Assembly-CSharp", false, true);
                if (cjType != null)
                {
                    var mi = cjType.GetMethod("UpdateCityVariantData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        try
                        {
                            string normal = (variants != null && variants.Count >= 1) ? variants[0] : "";
                            string destroyed = (variants != null && variants.Count >= 2) ? variants[1] : "";
                            var paramCount = mi.GetParameters().Length;
                            object ret = null;
                            if (paramCount == 5) ret = mi.Invoke(null, new object[] { sceneName, normal, destroyed, lastKnownVariant, confidenceToken });
                            else if (paramCount == 4) ret = mi.Invoke(null, new object[] { sceneName, normal, destroyed, lastKnownVariant });
                            else { try { ret = mi.Invoke(null, new object[] { sceneName, normal, destroyed, lastKnownVariant }); } catch { ret = null; } }

                            if (ret is bool b) { attemptedUpdate = true; persisted = b; }
                            else { attemptedUpdate = true; persisted = true; }
                            TBLog.Info($"CitiesJsonManagerCompat: CitiesJsonManager.UpdateCityVariantData invoked via legacy bridge returned={persisted}");
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn("CitiesJsonManagerCompat: CitiesJsonManager.UpdateCityVariantData threw: " + ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("CitiesJsonManagerCompat: error while trying legacy CitiesJsonManager call: " + ex);
            }

            // 2) update runtime
            try
            {
                TBLog.Info("CitiesJsonManagerCompat: Attempting to update in-memory TravelButton runtime city entry (variants + lastKnown).");
                TravelButtonVariantUpdater.UpdateCityVariantDataWithVariants(sceneName, variants, lastKnownVariant);
                TBLog.Info("CitiesJsonManagerCompat: TravelButtonVariantUpdater.UpdateCityVariantDataWithVariants completed.");
            }
            catch (Exception ex)
            {
                TBLog.Warn("CitiesJsonManagerCompat: TravelButtonVariantUpdater threw: " + ex);
            }

            // 3) try legacy PersistCitiesToPluginFolder zero-arg
            if (!persisted)
            {
                try
                {
                    var tbType = typeof(TravelButton);
                    var persistMi = tbType.GetMethod("PersistCitiesToPluginFolder", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (persistMi != null)
                    {
                        var pcount = persistMi.GetParameters()?.Length ?? 0;
                        if (pcount == 0)
                        {
                            try
                            {
                                if (persistMi.IsStatic) persistMi.Invoke(null, null);
                                else
                                {
                                    var instField = tbType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                    var inst = instField != null ? instField.GetValue(null) : null;
                                    if (inst != null) persistMi.Invoke(inst, null);
                                    else persistMi.Invoke(null, null);
                                }
                                persisted = true;
                                TBLog.Info("CitiesJsonManagerCompat: called TravelButton.PersistCitiesToPluginFolder() (zero-arg) successfully.");
                            }
                            catch (Exception ex)
                            {
                                TBLog.Warn("CitiesJsonManagerCompat: TravelButton.PersistCitiesToPluginFolder zero-arg invoke threw: " + ex);
                            }
                        }
                        else TBLog.Warn($"CitiesJsonManagerCompat: PersistCitiesToPluginFolder exists but expects {pcount} parameters; skipping automatic call.");
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("CitiesJsonManagerCompat: error while trying to call PersistCitiesToPluginFolder: " + ex);
                }
            }

            // 4) last resort: write the canonical JSON using the new schema
            if (!persisted)
            {
                try
                {
                    TBLog.Info("CitiesJsonManagerCompat: Attempting direct JSON file update (new schema) as a last resort.");
                    var jsonWriteOk = TryForceWriteCitiesJsonNewSchema(sceneName, variants, lastKnownVariant);
                    if (jsonWriteOk)
                    {
                        persisted = true;
                        TBLog.Info("CitiesJsonManagerCompat: direct JSON write (new schema) succeeded.");
                    }
                    else
                    {
                        TBLog.Warn("CitiesJsonManagerCompat: direct JSON write failed or Newtonsoft unavailable.");
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("CitiesJsonManagerCompat: TryForceWriteCitiesJsonNewSchema threw: " + ex);
                }
            }

            TBLog.Info($"CitiesJsonManagerCompat: final persisted={persisted} (attemptedUpdate={attemptedUpdate})");
            return persisted;
        }
        catch (Exception ex)
        {
            TBLog.Warn("CitiesJsonManagerCompat: unexpected error: " + ex);
            return false;
        }
    }

    // New schema writer with merging & runtime-fill improvements
    static bool TryForceWriteCitiesJsonNewSchema(string sceneName, List<string> variants, string lastKnownVariant)
    {
        try
        {
            // Determine canonical path (reuse earlier reflection approach)
            string path = null;
            try
            {
                var tbPluginType = Type.GetType("TravelButtonPlugin, Assembly-CSharp", false, true) ?? Type.GetType("TravelButtonPlugin");
                if (tbPluginType != null)
                {
                    var mi = tbPluginType.GetMethod("GetCitiesJsonPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null) path = mi.Invoke(null, null) as string;
                }
            }
            catch (Exception ex) { TBLog.Warn("CitiesJsonManagerCompat: GetCitiesJsonPath reflection threw: " + ex); }

            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    var tbAsm = typeof(TravelButton).Assembly;
                    var asmPath = tbAsm.Location;
                    var dir = Path.GetDirectoryName(asmPath);
                    path = Path.Combine(dir ?? ".", "TravelButton_Cities.json");
                }
                catch { path = null; }
            }

            if (string.IsNullOrEmpty(path))
            {
                TBLog.Warn("CitiesJsonManagerCompat: could not determine cities JSON path; skipping direct write.");
                return false;
            }

            TBLog.Info($"CitiesJsonManagerCompat: TryForceWriteCitiesJsonNewSchema: path='{path}'");

            if (!File.Exists(path))
            {
                TBLog.Warn("CitiesJsonManagerCompat: Cities JSON file does not exist at path; skipping direct write.");
                return false;
            }

            string text = File.ReadAllText(path);

            // Locate Newtonsoft types
            var jObjectType = Type.GetType("Newtonsoft.Json.Linq.JObject, Newtonsoft.Json");
            var jArrayType = Type.GetType("Newtonsoft.Json.Linq.JArray, Newtonsoft.Json");
            var jTokenType = Type.GetType("Newtonsoft.Json.Linq.JToken, Newtonsoft.Json");

            if (jObjectType == null || jArrayType == null || jTokenType == null)
            {
                TBLog.Warn("CitiesJsonManagerCompat: Newtonsoft.Json.Linq not found; cannot modify JSON safely.");
                return false;
            }

            // Parse root JObject
            var parseMi = jObjectType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (parseMi == null) { TBLog.Warn("CitiesJsonManagerCompat: JObject.Parse not found"); return false; }
            object rootObj = parseMi.Invoke(null, new object[] { text });
            if (rootObj == null) { TBLog.Warn("CitiesJsonManagerCompat: JObject.Parse returned null"); return false; }

            // Access or create root["cities"] as JArray
            MethodInfo jobj_getItem = FindMethod(rootObj.GetType(), "get_Item", 1, typeof(string));
            MethodInfo jobj_setItem = FindMethod(rootObj.GetType(), "set_Item", 2, null);
            object citiesToken = null;
            try { if (jobj_getItem != null) citiesToken = jobj_getItem.Invoke(rootObj, new object[] { "cities" }); } catch (Exception ex) { TBLog.Warn("CitiesJsonManagerCompat: get_Item('cities') threw: " + ex); }
            object jArrayObj = citiesToken;
            if (jArrayObj == null)
            {
                jArrayObj = Activator.CreateInstance(jArrayType);
                if (jobj_setItem != null) jobj_setItem.Invoke(rootObj, new object[] { "cities", jArrayObj });
                else { TBLog.Warn("CitiesJsonManagerCompat: cannot set root['cities']"); return false; }
            }

            // Helpers for JArray
            MethodInfo jarray_getItem = FindMethod(jArrayObj.GetType(), "get_Item", 1, typeof(int));
            MethodInfo jarray_add = jArrayType.GetMethod("Add", new[] { jTokenType }) ?? jArrayType.GetMethod("Add", new[] { typeof(object) });
            PropertyInfo jArrayCountProp = jArrayType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);

            int count = (int)(jArrayCountProp.GetValue(jArrayObj) ?? 0);

            // 1) First pass: find exact match by sceneName property
            object matchedElement = null;
            for (int i = 0; i < count; i++)
            {
                object elem = null;
                try { if (jarray_getItem != null) elem = jarray_getItem.Invoke(jArrayObj, new object[] { i }); } catch { elem = null; }
                if (elem == null) continue;
                var elem_getItem = FindMethod(elem.GetType(), "get_Item", 1, typeof(string));
                if (elem_getItem == null) continue;
                try
                {
                    var scenePropVal = elem_getItem.Invoke(elem, new object[] { "sceneName" });
                    if (scenePropVal is string s && string.Equals(s, sceneName, StringComparison.OrdinalIgnoreCase)) { matchedElement = elem; break; }
                }
                catch { }
            }

            // 2) fallback pass: try matching by 'name' or by any property value equal to sceneName (tolerant)
            if (matchedElement == null)
            {
                for (int i = 0; i < count; i++)
                {
                    object elem = null;
                    try { if (jarray_getItem != null) elem = jarray_getItem.Invoke(jArrayObj, new object[] { i }); } catch { elem = null; }
                    if (elem == null) continue;
                    var elem_getItem = FindMethod(elem.GetType(), "get_Item", 1, typeof(string));
                    if (elem_getItem == null) continue;
                    try
                    {
                        // check "name"
                        var nameVal = elem_getItem.Invoke(elem, new object[] { "name" }) as string;
                        if (!string.IsNullOrEmpty(nameVal) && string.Equals(nameVal, sceneName, StringComparison.OrdinalIgnoreCase)) { matchedElement = elem; break; }

                        // check any property equals sceneName
                        var possible = elem.ToString();
                        if (!string.IsNullOrEmpty(possible) && possible.IndexOf(sceneName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // still prefer exact sceneName but as tolerant fallback pick this element
                            matchedElement = elem; break;
                        }
                    }
                    catch { /* ignore */ }
                }
            }

            // Prepare JToken.FromObject for conversions
            MethodInfo jtoken_fromObject = jTokenType.GetMethod("FromObject", new[] { typeof(object) });
            Type jvalueType = Type.GetType("Newtonsoft.Json.Linq.JValue, Newtonsoft.Json");
            ConstructorInfo jvalue_ctor = jvalueType?.GetConstructor(new[] { typeof(object) });

            Func<object, object> ToJToken = (obj) =>
            {
                if (obj == null) return null;
                if (jtoken_fromObject != null) return jtoken_fromObject.Invoke(null, new object[] { obj });
                if (jvalue_ctor != null) return jvalue_ctor.Invoke(new object[] { obj });
                return obj;
            };

            // Helper: attempt to get runtime data for sceneName from TravelButton.Cities
            object runtimeCity = null;
            object runtimeCoords = null;
            string runtimeName = null;
            double? runtimePrice = null;
            string runtimeTarget = null;
            string runtimeDesc = null;
            bool? runtimeVisited = null;
            try
            {
                var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
                if (citiesEnum != null)
                {
                    foreach (var c in citiesEnum)
                    {
                        if (c == null) continue;
                        var t = c.GetType();
                        string sceneProp = null;
                        try { sceneProp = t.GetProperty("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                        if (string.IsNullOrEmpty(sceneProp))
                        {
                            try { sceneProp = t.GetField("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                        }
                        if (string.IsNullOrEmpty(sceneProp)) continue;
                        if (!string.Equals(sceneProp, sceneName, StringComparison.OrdinalIgnoreCase)) continue;

                        runtimeCity = c;
                        try { runtimeName = t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                        try
                        {
                            var coordsProp = t.GetProperty("coords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            var coordsField = t.GetField("coords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            var coordsVal = coordsProp != null ? coordsProp.GetValue(c) : coordsField != null ? coordsField.GetValue(c) : null;
                            runtimeCoords = coordsVal;
                        }
                        catch { runtimeCoords = null; }
                        try
                        {
                            var p = t.GetProperty("price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (p != null) { var pv = p.GetValue(c); if (pv != null) runtimePrice = Convert.ToDouble(pv); }
                        }
                        catch { }
                        try { runtimeTarget = t.GetProperty("targetGameObjectName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                        try { runtimeDesc = t.GetProperty("desc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                        try
                        {
                            var vprop = t.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (vprop != null) runtimeVisited = vprop.GetValue(c) as bool?;
                        }
                        catch { }
                        break;
                    }
                }
            }
            catch (Exception exRuntimeLookup) { TBLog.Warn("CitiesJsonManagerCompat: runtime lookup failed: " + exRuntimeLookup); }

            if (matchedElement == null)
            {
                // create new JObject for this scene
                var jobjCtor = jObjectType.GetConstructor(Type.EmptyTypes);
                object newObj = jobjCtor != null ? jobjCtor.Invoke(null) : Activator.CreateInstance(jObjectType);
                var newObjType = newObj.GetType();
                var newObj_setItem = FindMethod(newObjType, "set_Item", 2, null);
                if (newObj_setItem == null)
                {
                    TBLog.Warn("CitiesJsonManagerCompat: new JObject does not expose set_Item; aborting add.");
                    return false;
                }

                try
                {
                    newObj_setItem.Invoke(newObj, new object[] { "sceneName", ToJToken(sceneName) });

                    // variants array
                    var jarrayCtor = jArrayType.GetConstructor(Type.EmptyTypes);
                    object variantsArr = jarrayCtor != null ? jarrayCtor.Invoke(null) : Activator.CreateInstance(jArrayType);
                    MethodInfo variants_add = jArrayType.GetMethod("Add", new[] { jTokenType }) ?? jArrayType.GetMethod("Add", new[] { typeof(object) });
                    if (variants != null)
                    {
                        foreach (var v in variants) variants_add.Invoke(variantsArr, new object[] { ToJToken(v) });
                    }
                    newObj_setItem.Invoke(newObj, new object[] { "variants", variantsArr });

                    // legacy fields
                    if (variants != null && variants.Count >= 1) newObj_setItem.Invoke(newObj, new object[] { "variantNormalName", ToJToken(variants[0]) });
                    if (variants != null && variants.Count >= 2) newObj_setItem.Invoke(newObj, new object[] { "variantDestroyedName", ToJToken(variants[1]) });
                    if (!string.IsNullOrEmpty(lastKnownVariant)) newObj_setItem.Invoke(newObj, new object[] { "lastKnownVariant", ToJToken(lastKnownVariant) });

                    // populate runtime info if available
                    if (!string.IsNullOrEmpty(runtimeName)) newObj_setItem.Invoke(newObj, new object[] { "name", ToJToken(runtimeName) });
                    if (runtimeCoords != null)
                    {
                        try
                        {
                            // convert runtimeCoords (IEnumerable) into JArray
                            var coordsArr = jarrayCtor != null ? jarrayCtor.Invoke(null) : Activator.CreateInstance(jArrayType);
                            var addMi = jArrayType.GetMethod("Add", new[] { jTokenType }) ?? jArrayType.GetMethod("Add", new[] { typeof(object) });
                            var enumerable = runtimeCoords as System.Collections.IEnumerable;
                            if (enumerable != null)
                            {
                                foreach (var v in enumerable) addMi.Invoke(coordsArr, new object[] { ToJToken(v) });
                                newObj_setItem.Invoke(newObj, new object[] { "coords", coordsArr });
                            }
                        }
                        catch (Exception exCoords) { TBLog.Warn("CitiesJsonManagerCompat: failed to set coords from runtime: " + exCoords); }
                    }
                    if (runtimePrice.HasValue) newObj_setItem.Invoke(newObj, new object[] { "price", ToJToken(runtimePrice.Value) });
                    if (!string.IsNullOrEmpty(runtimeTarget)) newObj_setItem.Invoke(newObj, new object[] { "targetGameObjectName", ToJToken(runtimeTarget) });
                    if (!string.IsNullOrEmpty(runtimeDesc)) newObj_setItem.Invoke(newObj, new object[] { "desc", ToJToken(runtimeDesc) });
                    if (runtimeVisited.HasValue) newObj_setItem.Invoke(newObj, new object[] { "visited", ToJToken(runtimeVisited.Value) });
                }
                catch (Exception exNewSet) { TBLog.Warn("CitiesJsonManagerCompat: failed to set fields on new JObject: " + exNewSet); }

                // Before appending double-check no element exists with same sceneName (race)
                bool appended = false;
                for (int i = 0; i < (int)(jArrayCountProp.GetValue(jArrayObj) ?? 0); i++)
                {
                    object elem = null;
                    try { if (jarray_getItem != null) elem = jarray_getItem.Invoke(jArrayObj, new object[] { i }); } catch { elem = null; }
                    if (elem == null) continue;
                    var elem_getItem = FindMethod(elem.GetType(), "get_Item", 1, typeof(string));
                    if (elem_getItem == null) continue;
                    try
                    {
                        var scenePropVal = elem_getItem.Invoke(elem, new object[] { "sceneName" }) as string;
                        if (!string.IsNullOrEmpty(scenePropVal) && string.Equals(scenePropVal, sceneName, StringComparison.OrdinalIgnoreCase))
                        {
                            // update this existing element instead of appending
                            matchedElement = elem;
                            appended = false;
                            break;
                        }
                    }
                    catch { }
                }

                if (matchedElement == null)
                {
                    if (jarray_add == null) { TBLog.Warn("CitiesJsonManagerCompat: JArray.Add not found"); return false; }
                    jarray_add.Invoke(jArrayObj, new object[] { newObj });
                    TBLog.Info("CitiesJsonManagerCompat: appended new city entry for scene '" + sceneName + "'");
                }
            }
            else
            {
                // update matched element: set variants array and lastKnownVariant (and legacy fields)
                var elemType = matchedElement.GetType();
                var elem_setItem = FindMethod(elemType, "set_Item", 2, null);
                try
                {
                    // create JArray for variants
                    var jarrayCtor = jArrayType.GetConstructor(Type.EmptyTypes);
                    object variantsArr = jarrayCtor != null ? jarrayCtor.Invoke(null) : Activator.CreateInstance(jArrayType);
                    MethodInfo variants_add = jArrayType.GetMethod("Add", new[] { jTokenType }) ?? jArrayType.GetMethod("Add", new[] { typeof(object) });
                    if (variants != null)
                    {
                        foreach (var v in variants) variants_add.Invoke(variantsArr, new object[] { ToJToken(v) });
                    }

                    elem_setItem.Invoke(matchedElement, new object[] { "variants", variantsArr });
                    if (variants != null && variants.Count >= 1) elem_setItem.Invoke(matchedElement, new object[] { "variantNormalName", ToJToken(variants[0]) });
                    if (variants != null && variants.Count >= 2) elem_setItem.Invoke(matchedElement, new object[] { "variantDestroyedName", ToJToken(variants[1]) });
                    if (!string.IsNullOrEmpty(lastKnownVariant)) elem_setItem.Invoke(matchedElement, new object[] { "lastKnownVariant", ToJToken(lastKnownVariant) });

                    // keep existing coords/name/etc intact (we are updating, not overwriting)
                    TBLog.Info($"CitiesJsonManagerCompat: updated matched JSON element for scene '{sceneName}'");
                }
                catch (Exception exUpd) { TBLog.Warn("CitiesJsonManagerCompat: failed to update matched JSON element: " + exUpd); }
            }

            // Serialize root to indented JSON
            var formattingType = Type.GetType("Newtonsoft.Json.Formatting, Newtonsoft.Json");
            object indentedFormatting = null;
            if (formattingType != null)
            {
                var enumVals = Enum.GetValues(formattingType);
                foreach (var ev in enumVals) if (ev.ToString().Equals("Indented", StringComparison.OrdinalIgnoreCase)) { indentedFormatting = ev; break; }
            }

            string outText = null;
            try
            {
                if (indentedFormatting != null)
                {
                    var tostringMi = jObjectType.GetMethod("ToString", new[] { formattingType });
                    if (tostringMi != null) outText = tostringMi.Invoke(rootObj, new object[] { indentedFormatting }) as string;
                }
                if (outText == null) outText = rootObj.ToString();
            }
            catch (Exception exSer) { TBLog.Warn("CitiesJsonManagerCompat: serialization failed: " + exSer); outText = rootObj.ToString(); }

            if (string.IsNullOrEmpty(outText)) { TBLog.Warn("CitiesJsonManagerCompat: serialization returned empty string"); return false; }

            // Backup + atomic write
            try
            {
                var bak = path + ".bak";
                File.Copy(path, bak, true);
                TBLog.Info("CitiesJsonManagerCompat: backup created: " + bak);
            }
            catch (Exception exBak) { TBLog.Warn("CitiesJsonManagerCompat: failed backup: " + exBak); }

            try
            {
                var tmp = Path.Combine(Path.GetDirectoryName(path) ?? Path.GetTempPath(), $"TravelButton_Cities_tmp_{Guid.NewGuid():N}.json");
                File.WriteAllText(tmp, outText);
                try { File.Replace(tmp, path, null); }
                catch (PlatformNotSupportedException) { File.Copy(tmp, path, true); File.Delete(tmp); }
                TBLog.Info("CitiesJsonManagerCompat: written updated cities JSON to '" + path + "'");
                return true;
            }
            catch (Exception exWrite) { TBLog.Warn("CitiesJsonManagerCompat: failed writing updated JSON to disk: " + exWrite); return false; }
        }
        catch (Exception ex)
        {
            TBLog.Warn("CitiesJsonManagerCompat: TryForceWriteCitiesJsonNewSchema top-level failure: " + ex);
            return false;
        }
    }

    // Migration utility and helper left unchanged (as before)...
    public static void MigrateAllCitiesToVariants()
    {
        try
        {
            TBLog.Info("CitiesJsonManagerCompat: MigrateAllCitiesToVariants: scanning runtime TravelButton.Cities");
            var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
            if (citiesEnum == null) { TBLog.Info("CitiesJsonManagerCompat: TravelButton.Cities not enumerable; skipping migration."); return; }

            foreach (var c in citiesEnum)
            {
                if (c == null) continue;
                try
                {
                    var t = c.GetType();
                    string sceneName = null;
                    try { sceneName = t.GetProperty("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                    if (string.IsNullOrEmpty(sceneName)) continue;

                    string varNormal = null, varDestroyed = null, lastKnown = null;
                    try { varNormal = t.GetProperty("variantNormalName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                    try { varDestroyed = t.GetProperty("variantDestroyedName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                    try { lastKnown = t.GetProperty("lastKnownVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }

                    var variants = new List<string>();
                    if (!string.IsNullOrEmpty(varNormal)) variants.Add(varNormal);
                    if (!string.IsNullOrEmpty(varDestroyed) && !variants.Contains(varDestroyed)) variants.Add(varDestroyed);

                    string concreteLast = lastKnown;
                    if (!string.IsNullOrEmpty(lastKnown))
                    {
                        if (string.Equals(lastKnown, "Normal", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(varNormal)) concreteLast = varNormal;
                        else if (string.Equals(lastKnown, "Destroyed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(varDestroyed)) concreteLast = varDestroyed;
                    }

                    if (variants.Count > 0 || !string.IsNullOrEmpty(concreteLast))
                    {
                        TBLog.Info($"CitiesJsonManagerCompat: migrating scene '{sceneName}' -> variants=[{string.Join(",", variants)}], lastKnown='{concreteLast}'");
                        TryUpdateAndPersist(sceneName, variants, concreteLast, null);
                    }
                }
                catch (Exception ex) { TBLog.Warn("CitiesJsonManagerCompat: per-city migration failure: " + ex); }
            }
        }
        catch (Exception exTop) { TBLog.Warn("CitiesJsonManagerCompat: MigrateAllCitiesToVariants failed: " + exTop); }
    }

    // Helper: find method with name and parameter count and optional first parameter type
    static MethodInfo FindMethod(Type t, string name, int paramCount, Type firstParamType)
    {
        try
        {
            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                              .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal)).ToArray();
            if (candidates.Length == 0) return null;
            foreach (var c in candidates)
            {
                var ps = c.GetParameters();
                if (ps.Length != paramCount) continue;
                if (firstParamType != null)
                {
                    if (ps.Length >= 1 && ps[0].ParameterType == firstParamType) return c;
                }
                else return c;
            }
            return candidates.FirstOrDefault(m => m.GetParameters().Length == paramCount);
        }
        catch { return null; }
    }
}