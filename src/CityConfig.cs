using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// CityConfig.cs
// - DTOs representing the shape of TravelButton_Cities.json
// - TravelConfig provides LoadFromFile, SaveToFile, and Default() helpers
// - Uses UnityEngine.JsonUtility for read/write

[Serializable]
public class CityConfig
{
    public bool enabled = true;
    public int? price = 200;
    public float[] coords = null;
    public string targetGameObjectName = null;
    public string sceneName = null;
    public string desc = null;
    public bool visited = false;
}

[Serializable]
public class TravelConfigCitiesWrapper
{
    public List<CityConfigWithName> cities = new List<CityConfigWithName>();
}

[Serializable]
public class CityConfigWithName
{
    public string name;
    public bool enabled = true;
    public int? price = 200;
    public float[] coords = null;
    public string targetGameObjectName = null;
    public string sceneName = null;
    public string desc = null;
    public bool visited = false;
}

[Serializable]
public class TravelConfig
{
    public bool enabled = true;
    public string currencyItem = "Silver";
    public int globalTeleportPrice = 200;
    public Dictionary<string, CityConfig> cities = new Dictionary<string, CityConfig>(StringComparer.OrdinalIgnoreCase);

    // LoadFromFile: attempt to load TravelButton_Cities.json from the specified path
    // Returns null if file doesn't exist or can't be parsed
    public static TravelConfig LoadFromFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            // Parse JSON manually since it uses dictionary shape which JsonUtility doesn't support well
            // We'll use a simple parser to extract the fields
            var config = new TravelConfig();

            // Extract top-level fields
            config.enabled = ExtractBoolField(json, "enabled", true);
            config.currencyItem = ExtractStringField(json, "currencyItem", "Silver");
            config.globalTeleportPrice = ExtractIntField(json, "globalTeleportPrice", 200);

            // Extract cities object
            int citiesIdx = json.IndexOf("\"cities\"", StringComparison.Ordinal);
            if (citiesIdx >= 0)
            {
                int colonIdx = json.IndexOf(':', citiesIdx);
                if (colonIdx >= 0)
                {
                    int i = colonIdx + 1;
                    while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                    if (i < json.Length && json[i] == '{')
                    {
                        // Parse cities dictionary
                        int objStart = i;
                        int objEnd = FindMatchingBrace(json, objStart);
                        if (objEnd > objStart)
                        {
                            string citiesObj = json.Substring(objStart + 1, objEnd - objStart - 1);
                            ParseCitiesDictionary(citiesObj, config.cities);
                        }
                    }
                }
            }

            return config;
        }
        catch (Exception ex)
        {
            try { TBLog.Warn("LoadFromFile failed: " + ex.Message); } catch { }
            return null;
        }
    }

    // SaveToFile: save TravelConfig to the specified path using UnityEngine.JsonUtility
    public static void SaveToFile(string path, TravelConfig config)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || config == null)
                return;

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Build JSON manually since JsonUtility doesn't handle Dictionary well
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"enabled\": {(config.enabled ? "true" : "false")},");
            sb.AppendLine($"  \"currencyItem\": \"{config.currencyItem}\",");
            sb.AppendLine($"  \"globalTeleportPrice\": {config.globalTeleportPrice},");
            sb.AppendLine("  \"cities\": {");

            bool first = true;
            foreach (var kv in config.cities)
            {
                if (!first) sb.AppendLine(",");
                first = false;

                sb.AppendLine($"    \"{kv.Key}\": {{");
                sb.AppendLine($"      \"enabled\": {(kv.Value.enabled ? "true" : "false")},");
                sb.AppendLine($"      \"price\": {kv.Value.price},");

                if (kv.Value.coords != null && kv.Value.coords.Length >= 3)
                    sb.AppendLine($"      \"coords\": [{kv.Value.coords[0]}, {kv.Value.coords[1]}, {kv.Value.coords[2]}],");
                else
                    sb.AppendLine("      \"coords\": [],");

                sb.AppendLine($"      \"targetGameObjectName\": \"{kv.Value.targetGameObjectName ?? ""}\",");
                sb.AppendLine($"      \"sceneName\": \"{kv.Value.sceneName ?? ""}\",");
                sb.AppendLine($"      \"desc\": \"{kv.Value.desc ?? ""}\",");
                sb.Append($"      \"visited\": {(kv.Value.visited ? "true" : "false")}");
                sb.AppendLine();
                sb.Append("    }");
            }

            sb.AppendLine();
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            try { TBLog.Warn("SaveToFile failed: " + ex.Message); } catch { }
        }
    }

    // Default: return a default TravelConfig with the provided city data
    public static TravelConfig Default()
    {
        var config = new TravelConfig
        {
            enabled = true,
            currencyItem = "Silver",
            globalTeleportPrice = 200,
            cities = new Dictionary<string, CityConfig>(StringComparer.OrdinalIgnoreCase)
            {
                { "Cierzo", new CityConfig {
                    enabled = true,
                    price = 200,
                    coords = new float[] { 1410.388f, 6.786f, 1665.642f },
                    targetGameObjectName = "Cierzo",
                    sceneName = "CierzoNewTerrain",
                    desc = "Cierzo - example description",
                    visited = false
                }},
                { "Levant", new CityConfig {
                    enabled = true,
                    price = 200,
                    coords = new float[] { -55.212f, 1.056f, 79.379f },
                    targetGameObjectName = "WarpLocation_HM",
                    sceneName = "Levant",
                    desc = "Levant - example description",
                    visited = false
                }},
                { "Monsoon", new CityConfig {
                    enabled = true,
                    price = 200,
                    coords = new float[] { 61.553f, -3.743f, 167.599f },
                    targetGameObjectName = "Monsoon_Location",
                    sceneName = "Monsoon",
                    desc = "Monsoon - example description",
                    visited = false
                }},
                { "Berg", new CityConfig {
                    enabled = true,
                    price = 200,
                    coords = new float[] { 1202.414f, -13.071f, 1378.836f },
                    targetGameObjectName = "Berg",
                    sceneName = "Berg",
                    desc = "Berg - example description",
                    visited = false
                }},
                { "Harmattan", new CityConfig {
                    enabled = true,
                    price = 200,
                    coords = new float[] { 93.757f, 65.474f, 767.849f },
                    targetGameObjectName = "Harmattan_Location",
                    sceneName = "Harmattan",
                    desc = "Harmattan - example description",
                    visited = false
                }},
                { "Sirocco", new CityConfig {
                    enabled = true,
                    price = 200,
                    coords = new float[] { 600.0f, 1.2f, -300.0f },
                    targetGameObjectName = "Sirocco_Location",
                    sceneName = "NewSirocco",
                    desc = "Sirocco - example description",
                    visited = false
                }}
            }
        };

        return config;
    }

    // Helper methods for parsing JSON
    private static bool ExtractBoolField(string json, string fieldName, bool defaultValue)
    {
        try
        {
            int idx = json.IndexOf($"\"{fieldName}\"", StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return defaultValue;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return defaultValue;
            if (json.Substring(i).StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (json.Substring(i).StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }
        catch { return defaultValue; }
    }

    private static string ExtractStringField(string json, string fieldName, string defaultValue)
    {
        try
        {
            int idx = json.IndexOf($"\"{fieldName}\"", StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return defaultValue;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return defaultValue;
            int endQuote = FindClosingQuote(json, i);
            if (endQuote < 0) return defaultValue;
            return json.Substring(i + 1, endQuote - i - 1);
        }
        catch { return defaultValue; }
    }

    private static int ExtractIntField(string json, string fieldName, int defaultValue)
    {
        try
        {
            int idx = json.IndexOf($"\"{fieldName}\"", StringComparison.Ordinal);
            if (idx < 0) return defaultValue;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return defaultValue;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return defaultValue;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '+')) i++;
            string token = json.Substring(start, i - start);
            if (int.TryParse(token, out int value))
                return value;
            return defaultValue;
        }
        catch { return defaultValue; }
    }

    private static int? ExtractNullableIntField(string json, string fieldName)
    {
        try
        {
            int idx = json.IndexOf($"\"{fieldName}\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '+')) i++;
            string token = json.Substring(start, i - start);
            if (int.TryParse(token, out int value))
                return value;
            return null;
        }
        catch { return null; }
    }

    private static void ParseCitiesDictionary(string citiesObj, Dictionary<string, CityConfig> cities)
    {
        try
        {
            int pos = 0;
            while (pos < citiesObj.Length)
            {
                // Skip whitespace and commas
                while (pos < citiesObj.Length && (char.IsWhiteSpace(citiesObj[pos]) || citiesObj[pos] == ',')) pos++;
                if (pos >= citiesObj.Length) break;

                // Expect quoted city name
                if (citiesObj[pos] != '"') break;
                int nameStart = pos;
                int nameEnd = FindClosingQuote(citiesObj, nameStart);
                if (nameEnd < 0) break;
                string cityName = citiesObj.Substring(nameStart + 1, nameEnd - nameStart - 1);
                pos = nameEnd + 1;

                // Skip whitespace and colon
                while (pos < citiesObj.Length && char.IsWhiteSpace(citiesObj[pos])) pos++;
                if (pos < citiesObj.Length && citiesObj[pos] == ':') pos++;
                while (pos < citiesObj.Length && char.IsWhiteSpace(citiesObj[pos])) pos++;

                // Expect city object
                if (pos >= citiesObj.Length || citiesObj[pos] != '{') break;
                int objStart = pos;
                int objEnd = FindMatchingBrace(citiesObj, objStart);
                if (objEnd < 0) break;
                string cityObj = citiesObj.Substring(objStart + 1, objEnd - objStart - 1);

                // Parse city object
                var city = new CityConfig();
                city.enabled = ExtractBoolField(cityObj, "enabled", true);
                city.price = ExtractNullableIntField(cityObj, "price") ?? 200;
                city.targetGameObjectName = ExtractStringField(cityObj, "targetGameObjectName", null);
                city.sceneName = ExtractStringField(cityObj, "sceneName", null);
                city.desc = ExtractStringField(cityObj, "desc", null);
                city.visited = ExtractBoolField(cityObj, "visited", false);

                // Parse coords array
                city.coords = ExtractFloatArray(cityObj, "coords");

                cities[cityName] = city;
                pos = objEnd + 1;
            }
        }
        catch (Exception ex)
        {
            try { TBLog.Warn("ParseCitiesDictionary failed: " + ex.Message); } catch { }
        }
    }

    private static float[] ExtractFloatArray(string json, string fieldName)
    {
        try
        {
            int idx = json.IndexOf($"\"{fieldName}\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '[') return null;
            int arrStart = i;
            int arrEnd = FindMatchingBracket(json, arrStart);
            if (arrEnd < 0) return null;
            string inner = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            var parts = inner.Split(',');
            var list = new System.Collections.Generic.List<float>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    list.Add(f);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }
        catch { return null; }
    }

    private static int FindMatchingBrace(string json, int startIndex)
    {
        if (string.IsNullOrEmpty(json) || startIndex < 0 || startIndex >= json.Length) return -1;
        if (json[startIndex] != '{') return -1;
        int depth = 0;
        bool inString = false;
        for (int i = startIndex; i < json.Length; i++)
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

    private static int FindMatchingBracket(string json, int startIndex)
    {
        if (string.IsNullOrEmpty(json) || startIndex < 0 || startIndex >= json.Length) return -1;
        if (json[startIndex] != '[') return -1;
        int depth = 0;
        bool inString = false;
        for (int i = startIndex; i < json.Length; i++)
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

    private static int FindClosingQuote(string json, int idx)
    {
        if (string.IsNullOrEmpty(json) || idx < 0 || idx >= json.Length) return -1;
        if (json[idx] != '"') return -1;
        for (int i = idx + 1; i < json.Length; i++)
        {
            if (json[i] == '"' && json[i - 1] != '\\') return i;
        }
        return -1;
    }
}
