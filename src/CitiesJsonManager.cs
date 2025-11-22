using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
}