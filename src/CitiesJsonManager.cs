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

    public static bool UpdateCityVariantData(string sceneName, string normalName, string destroyedName, string lastKnownVariant)
    {
        try
        {
            string path = TravelButtonPlugin.GetCitiesJsonPath();
            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("[CitiesJsonManager] No TravelButton_Cities.json found in candidate locations.");
                return false;
            }

            Debug.Log($"[CitiesJsonManager] Loading cities JSON from: {path}");
            var text = File.ReadAllText(path);
            JToken root = JToken.Parse(text);

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
                Debug.LogWarning("[CitiesJsonManager] Unexpected JSON root type.");
                return false;
            }

            var cityToken = citiesArray.FirstOrDefault(c =>
            {
                var sn = c["sceneName"]?.ToString();
                return !string.IsNullOrEmpty(sn) && string.Equals(sn, sceneName, StringComparison.OrdinalIgnoreCase);
            });

            if (cityToken == null)
            {
                Debug.Log($"[CitiesJsonManager] No city entry found with sceneName='{sceneName}' in {path}.");
                return false;
            }

            bool changed = false;

            // Validate names with heuristics to avoid procedural/coordinate labels
            bool normalPlausible = IsPlausibleVariantName(normalName);
            bool destroyedPlausible = IsPlausibleVariantName(destroyedName);

            // Only update name fields when plausible
            if (normalPlausible)
            {
                var cur = cityToken["variantNormalName"]?.ToString();
                if (!string.Equals(cur, normalName, StringComparison.Ordinal))
                {
                    cityToken["variantNormalName"] = normalName;
                    Debug.Log($"[CitiesJsonManager] Updating variantNormalName for scene '{sceneName}' -> '{normalName}' (was '{cur ?? "<null>"}').");
                    changed = true;
                }
            }
            else if (!string.IsNullOrEmpty(normalName))
            {
                Debug.Log($"[CitiesJsonManager] Skipping persist of variantNormalName='{normalName}' for scene '{sceneName}' (deemed implausible).");
            }

            if (destroyedPlausible)
            {
                var cur = cityToken["variantDestroyedName"]?.ToString();
                if (!string.Equals(cur, destroyedName, StringComparison.Ordinal))
                {
                    cityToken["variantDestroyedName"] = destroyedName;
                    Debug.Log($"[CitiesJsonManager] Updating variantDestroyedName for scene '{sceneName}' -> '{destroyedName}' (was '{cur ?? "<null>"}').");
                    changed = true;
                }
            }
            else if (!string.IsNullOrEmpty(destroyedName))
            {
                Debug.Log($"[CitiesJsonManager] Skipping persist of variantDestroyedName='{destroyedName}' for scene '{sceneName}' (deemed implausible).");
            }

            // lastKnownVariant: only write if meaningful
            if (!string.IsNullOrEmpty(lastKnownVariant))
            {
                if (SkipUnknownLastKnownVariant && string.Equals(lastKnownVariant, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[CitiesJsonManager] Skipping persist of lastKnownVariant='Unknown' for scene '{sceneName}'.");
                }
                else
                {
                    var cur = cityToken["lastKnownVariant"]?.ToString();
                    if (!string.Equals(cur, lastKnownVariant, StringComparison.Ordinal))
                    {
                        cityToken["lastKnownVariant"] = lastKnownVariant;
                        Debug.Log($"[CitiesJsonManager] Updating lastKnownVariant for scene '{sceneName}' -> '{lastKnownVariant}' (was '{cur ?? "<null>"}').");
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                Debug.Log("[CitiesJsonManager] No changes required to city data (or changes skipped as implausible).");
                return true;
            }

            // write atomically
            string newText;
            if (rootWasObjectWithCities)
            {
                newText = root.ToString(Formatting.Indented);
            }
            else if (root.Type == JTokenType.Array)
            {
                newText = citiesArray.ToString(Formatting.Indented);
            }
            else
            {
                // we constructed array from single object; write array form
                newText = citiesArray.ToString(Formatting.Indented);
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, newText);
            // Replace ensures atomic swap across most platforms
            File.Replace(tmp, path, null);
            Debug.Log($"[CitiesJsonManager] Updated cities JSON at {path}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CitiesJsonManager] Exception updating JSON: " + ex);
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
        if (lower.Contains("default chunk") || lower.Contains("chunk") && Regex.IsMatch(lower, @"chunk\s*[:\(]")) return false;
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