using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Lightweight local ConfigManager used as a fallback if an external ConfigManager is not present.
/// This file defines the legacy config shape used by the older config system. To avoid name
/// collisions with the JSON DTO types in CityConfig.cs (TravelConfig), the legacy types are
/// explicitly named LegacyTravelConfig and LegacyCityConfig.
///
/// Provides:
///  - Default(): returns a populated legacy config instance (used via reflection as fallback)
///  - LoadFromFile(path): attempts to load the legacy config JSON (supports both array/wrapper and object-keyed shapes)
///  - SaveToFile(path, config): persist legacy config with JsonUtility
/// </summary>
public static class ConfigManager
{
    /// <summary>
    /// Legacy travel config shape used by the (old) ConfigManager/ConfigManager.Config instance.
    /// Renamed to avoid collision with the JSON TravelConfig DTO.
    /// </summary>
    [Serializable]
    public class LegacyTravelConfig
    {
        public bool enabled = true;
        public string currencyItem = "Silver";
        public int globalTeleportPrice = 100;

        // Legacy shape used an object/dictionary keyed by city name
        public Dictionary<string, LegacyCityConfig> cities = new Dictionary<string, LegacyCityConfig>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Legacy city entry used inside LegacyTravelConfig.cities dictionary.
    /// Includes enabled and price fields that the legacy config stored.
    /// </summary>
    [Serializable]
    public class LegacyCityConfig
    {
        // Whether the city is enabled in legacy config (BepInEx will be authoritative at runtime)
        public bool enabled = false;

        // Price in legacy config; nullable to allow omission (null means use global)
        public int? price = null;

        // Metadata that can also be present in the JSON file (coords/target/scene)
        public float[] coords = null;
        public string targetGameObjectName = null;
        public string sceneName = null;

        // Optional description (not required by all code paths)
        public string desc = null;

        // Persisted visited flag — default false.
        public bool visited = false;
    }

    // Return a default instance of the legacy config shape.
    // TravelButtonMod.InitFromConfig will locate this type via reflection and invoke Default().
    public static LegacyTravelConfig Default()
    {
        var cfg = new LegacyTravelConfig
        {
            enabled = true,
            currencyItem = "Silver",
            globalTeleportPrice = 200,
            cities = new Dictionary<string, LegacyCityConfig>(StringComparer.OrdinalIgnoreCase)
        };

        cfg.cities["Cierzo"] = new LegacyCityConfig
        {
            enabled = true,
            price = 200,
            coords = new float[] { 1410.3f, 6.7f, 1665.6f },
            targetGameObjectName = "Cierzo",
            sceneName = "CierzoNewTerrain",
            desc = "Cierzo - example description",
            visited = false
        };

        cfg.cities["Levant"] = new LegacyCityConfig
        {
            enabled = true,
            price = 200,
            coords = new float[] { -55.2f, 1.0f, 79.3f },
            targetGameObjectName = "Levant_Location",
            sceneName = "Levant",
            desc = "Levant - example description",
            visited = false
        };

        cfg.cities["Monsoon"] = new LegacyCityConfig
        {
            enabled = true,
            price = 200,
            coords = new float[] { 61.5f, -3.7f, 167.5f },
            targetGameObjectName = "Monsoon_Location",
            sceneName = "Monsoon",
            desc = "Monsoon - example description",
            visited = false
        };

        cfg.cities["Berg"] = new LegacyCityConfig
        {
            enabled = true,
            price = 200,
            coords = new float[] { 1202.4f, 13.0f, 1378.8f },
            targetGameObjectName = "xxx",
            sceneName = "Berg",
            desc = "Berg - example description",
            visited = false
        };

        cfg.cities["Harmattan"] = new LegacyCityConfig
        {
            enabled = true,
            price = 200,
            coords = new float[] { 93.7f, 65.4f, 767.8f },
            targetGameObjectName = "Harmattan_Location",
            sceneName = "Harmattan",
            desc = "Harmattan - example description",
            visited = false
        };

        cfg.cities["Sirocco"] = new LegacyCityConfig
        {
            enabled = false,
            price = 200,
            coords = new float[] { 62.5f, 56.8f, -54.0f },
            targetGameObjectName = "Sirocco_Location",
            sceneName = "NewSirocco",
            desc = "Sirocco - example description",
            visited = false
        };

        return cfg;
    }

    /// <summary>
    /// Try to load a legacy config JSON file. Supports:
    ///  - object wrapper matching LegacyTravelConfig (JsonUtility)
    ///  - fallback keyed-object form where "cities" is an object (cityName -> cityObject)
    /// Returns null on failure.
    /// </summary>
    public static LegacyTravelConfig LoadFromFile(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                var cfg = JsonUtility.FromJson<LegacyTravelConfig>(json);
                if (cfg != null && cfg.cities != null) return cfg;
            }
            catch { /* fallthrough to keyed-object parse */ }

            // fallback: parse object-keyed form
            var parsed = ParseObjectKeyedForm(json);
            return parsed;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] ConfigManager.LoadFromFile failed: {ex.Message}");
            return null;
        }
    }

    public static string ConfigPathForLog()
    {
        try
        {
            var candidates = new List<string>();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            candidates.Add(Path.Combine(baseDir, "BepInEx", "config", "cz.valheimskal.travelbutton.cfg"));            
            foreach (var c in candidates) { try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { } }
            // return preferred candidate (first)
            return Path.GetFullPath(candidates[0]);
        }
        catch { return "(unknown)"; }
    }

    /// <summary>
    /// Save the legacy config to file using JsonUtility (pretty).
    /// </summary>
    public static bool SaveToFile(string filePath, LegacyTravelConfig cfg)
    {
        if (string.IsNullOrEmpty(filePath) || cfg == null) return false;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonUtility.ToJson(cfg, true);
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            Debug.Log($"[TravelButton] Legacy config saved to: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] ConfigManager.SaveToFile failed: {ex.Message}");
            return false;
        }
    }

    // --- Internal helper: parse keyed object form for legacy cfg ---
    // expects {"cities": { "Name": { ... }, ... }, "enabled": true, ... } or similar
    private static LegacyTravelConfig ParseObjectKeyedForm(string json)
    {
        try
        {
            int citiesIdx = json.IndexOf("\"cities\"");
            if (citiesIdx < 0) return null;
            int colonIdx = json.IndexOf(':', citiesIdx);
            if (colonIdx < 0) return null;

            int i = colonIdx + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;

            int start = i;
            int end = FindMatchingBrace(json, start);
            if (end < 0) return null;

            string citiesContent = json.Substring(start + 1, end - start - 1);
            var cfg = new LegacyTravelConfig();
            cfg.cities = new Dictionary<string, LegacyCityConfig>(StringComparer.OrdinalIgnoreCase);

            int pos = 0;
            while (pos < citiesContent.Length)
            {
                while (pos < citiesContent.Length && (char.IsWhiteSpace(citiesContent[pos]) || citiesContent[pos] == ',')) pos++;
                if (pos >= citiesContent.Length) break;
                if (citiesContent[pos] != '"') break;

                int nameStart = pos;
                int nameEnd = FindClosingQuote(citiesContent, nameStart);
                if (nameEnd < 0) break;
                string cityName = citiesContent.Substring(nameStart + 1, nameEnd - nameStart - 1);
                pos = nameEnd + 1;

                while (pos < citiesContent.Length && char.IsWhiteSpace(citiesContent[pos])) pos++;
                if (pos < citiesContent.Length && citiesContent[pos] == ':') pos++;
                while (pos < citiesContent.Length && char.IsWhiteSpace(citiesContent[pos])) pos++;

                if (pos >= citiesContent.Length || citiesContent[pos] != '{') break;
                int objStart = pos;
                int objEnd = FindMatchingBrace(citiesContent, objStart);
                if (objEnd < 0) break;

                string cityJson = citiesContent.Substring(objStart, objEnd - objStart + 1);
                var cityCfg = ParseCityObject(cityName, cityJson);
                if (cityCfg != null)
                {
                    cfg.cities[cityName] = cityCfg;
                }

                pos = objEnd + 1;
            }

            // Also try to extract top-level enabled/currency/globalTeleportPrice if present
            var enabledVal = ExtractBool(json, "\"enabled\"");
            if (enabledVal.HasValue) cfg.enabled = enabledVal.Value;
            var curItem = ExtractString(json, "\"currencyItem\"");
            if (!string.IsNullOrEmpty(curItem)) cfg.currencyItem = curItem;
            var gtp = ExtractInt(json, "\"globalTeleportPrice\"");
            if (gtp.HasValue) cfg.globalTeleportPrice = gtp.Value;

            return cfg;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] ParseObjectKeyedForm failed: {ex.Message}");
            return null;
        }
    }

    private static LegacyCityConfig ParseCityObject(string cname, string cityJson)
    {
        try
        {
            var city = new LegacyCityConfig();
            city.enabled = ExtractBool(cityJson, "\"enabled\"") ?? false;
            city.price = ExtractInt(cityJson, "\"price\"");
            city.coords = ExtractFloatArray(cityJson, "\"coords\"");
            city.targetGameObjectName = ExtractString(cityJson, "\"targetGameObjectName\"");
            city.sceneName = ExtractString(cityJson, "\"sceneName\"");
            city.desc = ExtractString(cityJson, "\"desc\"");
            // Try to extract visited flag if present in legacy keyed form
            var visitedVal = ExtractBool(cityJson, "\"visited\"");
            city.visited = visitedVal ?? false;
            return city;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractString(string json, string propName)
    {
        int idx = json.IndexOf(propName);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length) return null;
        if (json[i] != '"') return null;
        int end = FindClosingQuote(json, i);
        if (end < 0) return null;
        return json.Substring(i + 1, end - i - 1);
    }

    private static int? ExtractInt(string json, string propName)
    {
        int idx = json.IndexOf(propName);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        int start = i;
        if (i >= json.Length) return null;
        while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '+')) i++;
        var tok = json.Substring(start, i - start);
        if (int.TryParse(tok, out int v)) return v;
        return null;
    }

    private static bool? ExtractBool(string json, string propName)
    {
        int idx = json.IndexOf(propName);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        var token = json.Substring(i, Math.Min(5, json.Length - i));
        if (token.StartsWith("true")) return true;
        if (token.StartsWith("false")) return false;
        return null;
    }

    private static float[] ExtractFloatArray(string json, string propName)
    {
        int idx = json.IndexOf(propName);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '[') return null;
        int arrStart = i;
        int arrEnd = FindMatchingBracket(json, arrStart);
        if (arrEnd < 0) return null;
        string content = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
        var parts = content.Split(',');
        var list = new List<float>();
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                list.Add(f);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static int FindClosingQuote(string json, int startIdx)
    {
        if (startIdx < 0 || startIdx >= json.Length || json[startIdx] != '"') return -1;
        for (int i = startIdx + 1; i < json.Length; i++)
        {
            if (json[i] == '"' && json[i - 1] != '\\') return i;
        }
        return -1;
    }

    private static int FindMatchingBrace(string json, int startIdx)
    {
        if (startIdx < 0 || startIdx >= json.Length || json[startIdx] != '{') return -1;
        int depth = 0;
        bool inString = false;
        for (int i = startIdx; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int FindMatchingBracket(string json, int startIdx)
    {
        if (startIdx < 0 || startIdx >= json.Length || json[startIdx] != '[') return -1;
        int depth = 0;
        bool inString = false;
        for (int i = startIdx; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}