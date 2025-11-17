using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// DTOs and JSON helpers for TravelButton_Cities.json (array form).
/// TravelConfig uses a List<CityConfig> as the 'cities' property so callers can Add().
/// </summary>
[Serializable]
public class CityConfig
{
    public string name;
    public int? price; // nullable so omission in JSON is explicit
    public float[] coords;
    public string targetGameObjectName;
    public string sceneName;
    public string desc;

    // Persisted visited flag — default false.
    public bool visited = false;

    public CityConfig() { }

    // simple constructor for name-only calls
    public CityConfig(string name) { this.name = name; }

    // Public property wrapper for visited to provide a clean getter/setter API.
    // Use property Visited in code; the JSON serializer will still read/write the public field 'visited'.
    public bool Visited
    {
        get => visited;
        set => visited = value;
    }

    // convenience constructor used by Default() and other initializers
    public CityConfig(string name, int? price, float[] coords, string targetGameObjectName, string sceneName, string desc)
    {
        this.name = name;
        this.price = price;
        this.coords = coords;
        this.targetGameObjectName = targetGameObjectName;
        this.sceneName = sceneName;
        this.desc = desc;
        this.visited = false;
    }
}

[Serializable]
public class TravelConfig
{
    // Use List<T> so callers can Add(...) items
    public List<CityConfig> cities = new List<CityConfig>();

    /// <summary>
    /// Load TravelConfig from a JSON file using UnityEngine.JsonUtility (expects { "cities": [ ... ] }).
    /// Falls back to object-keyed format if needed via ParseObjectKeyedForm.
    /// </summary>
    public static TravelConfig LoadFromFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            // Try to parse as array-form first (preferred format)
            // Format: { "cities": [ { "name": "...", ... }, ... ] }
            var wrapper = JsonUtility.FromJson<TravelConfig>(json);
            if (wrapper != null && wrapper.cities != null && wrapper.cities.Count > 0)
            {
                return wrapper;
            }

            // Fallback: try to parse object-keyed form
            // Format: { "cities": { "CityA": { ... }, "CityB": { ... } } }
            return ParseObjectKeyedForm(json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] CityConfig.LoadFromFile failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse object-keyed format where cities is a dictionary:
    /// { "cities": { "Name": { ... }, ... } }
    /// Produces a TravelConfig with cities as a List.
    /// </summary>
    private static TravelConfig ParseObjectKeyedForm(string json)
    {
        try
        {
            // Find the "cities" property
            int citiesIdx = json.IndexOf("\"cities\"");
            if (citiesIdx < 0) return null;

            int colonIdx = json.IndexOf(':', citiesIdx);
            if (colonIdx < 0) return null;

            // Skip whitespace after colon
            int i = colonIdx + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

            if (i >= json.Length || json[i] != '{') return null;

            // Find matching closing brace
            int start = i;
            int depth = 0;
            bool inString = false;
            int end = -1;

            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                }
                if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            end = i;
                            break;
                        }
                    }
                }
            }

            if (end < 0) return null;

            // Extract city entries
            string citiesContent = json.Substring(start + 1, end - start - 1);
            List<CityConfig> cityList = new List<CityConfig>();

            // Parse each city entry
            int pos = 0;
            while (pos < citiesContent.Length)
            {
                // Skip whitespace and commas
                while (pos < citiesContent.Length && (char.IsWhiteSpace(citiesContent[pos]) || citiesContent[pos] == ','))
                    pos++;

                if (pos >= citiesContent.Length) break;

                // Expect quoted city name
                if (citiesContent[pos] != '"') break;

                int nameStart = pos;
                int nameEnd = FindClosingQuote(citiesContent, nameStart);
                if (nameEnd < 0) break;

                string cityName = citiesContent.Substring(nameStart + 1, nameEnd - nameStart - 1);
                pos = nameEnd + 1;

                // Skip whitespace and colon
                while (pos < citiesContent.Length && char.IsWhiteSpace(citiesContent[pos])) pos++;
                if (pos < citiesContent.Length && citiesContent[pos] == ':') pos++;
                while (pos < citiesContent.Length && char.IsWhiteSpace(citiesContent[pos])) pos++;

                // Expect city object
                if (pos >= citiesContent.Length || citiesContent[pos] != '{') break;

                int objStart = pos;
                int objEnd = FindMatchingBrace(citiesContent, objStart);
                if (objEnd < 0) break;

                string cityJson = citiesContent.Substring(objStart, objEnd - objStart + 1);

                // Parse the city object
                CityConfig city = ParseCityObject(cityName, cityJson);
                if (city != null)
                {
                    cityList.Add(city);
                }

                pos = objEnd + 1;
            }

            if (cityList.Count > 0)
            {
                TravelConfig config = new TravelConfig();
                config.cities = cityList;
                return config;
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] ParseObjectKeyedForm failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse a single city object from JSON string
    /// </summary>
    private static CityConfig ParseCityObject(string cityName, string cityJson)
    {
        try
        {
            CityConfig city = new CityConfig();
            city.name = cityName;

            // Parse fields using simple string extraction
            city.coords = ExtractFloatArray(cityJson, "coords");
            city.targetGameObjectName = ExtractString(cityJson, "targetGameObjectName");
            city.sceneName = ExtractString(cityJson, "sceneName");
            city.desc = ExtractString(cityJson, "desc");

            int? priceValue = ExtractInt(cityJson, "price");
            city.price = priceValue; // preserve null if absent

            // Try to extract visited flag if present
            bool? visitedVal = ExtractBool(cityJson, "visited");
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
        int idx = json.IndexOf($"\"{propName}\"");
        if (idx < 0) return null;

        int colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return null;

        int i = colonIdx + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

        if (i >= json.Length || json[i] != '"') return null;

        int endQuote = FindClosingQuote(json, i);
        if (endQuote < 0) return null;

        return json.Substring(i + 1, endQuote - i - 1);
    }

    private static int? ExtractInt(string json, string propName)
    {
        int idx = json.IndexOf($"\"{propName}\"");
        if (idx < 0) return null;

        int colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return null;

        int i = colonIdx + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

        if (i >= json.Length) return null;

        int start = i;
        while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '+'))
            i++;

        string token = json.Substring(start, i - start);
        if (int.TryParse(token, out int value))
            return value;

        return null;
    }

    private static float[] ExtractFloatArray(string json, string propName)
    {
        int idx = json.IndexOf($"\"{propName}\"");
        if (idx < 0) return null;

        int colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0) return null;

        int i = colonIdx + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

        if (i >= json.Length || json[i] != '[') return null;

        int arrStart = i;
        int arrEnd = FindMatchingBracket(json, arrStart);
        if (arrEnd < 0) return null;

        string arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
        string[] parts = arrContent.Split(',');

        List<float> floats = new List<float>();
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f))
            {
                floats.Add(f);
            }
        }

        return floats.Count > 0 ? floats.ToArray() : null;
    }

    private static bool? ExtractBool(string json, string propName)
    {
        int idx = json.IndexOf($"\"{propName}\"");
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length) return null;
        // read up to 5 chars to cover "true"/"false"
        int len = Math.Min(5, json.Length - i);
        var token = json.Substring(i, len).ToLowerInvariant();
        if (token.StartsWith("true")) return true;
        if (token.StartsWith("false")) return false;
        return null;
    }

    private static int FindClosingQuote(string json, int startIdx)
    {
        if (json[startIdx] != '"') return -1;

        for (int i = startIdx + 1; i < json.Length; i++)
        {
            if (json[i] == '"' && json[i - 1] != '\\')
                return i;
        }

        return -1;
    }

    private static int FindMatchingBrace(string json, int startIdx)
    {
        if (json[startIdx] != '{') return -1;

        int depth = 0;
        bool inString = false;

        for (int i = startIdx; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                inString = !inString;

            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }

        return -1;
    }

    private static int FindMatchingBracket(string json, int startIdx)
    {
        if (json[startIdx] != '[') return -1;

        int depth = 0;
        bool inString = false;

        for (int i = startIdx; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                inString = !inString;

            if (!inString)
            {
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Save TravelConfig to a JSON file using UnityEngine.JsonUtility
    /// Instance method used by callers.
    /// </summary>
    public bool SaveToFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;

        try
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonUtility.ToJson(this, true);
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);

            Debug.Log($"[TravelButton] CityConfig saved to: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] CityConfig.SaveToFile failed: {ex.Message}");
            return false;
        }
    }

    // Provide a default TravelConfig (seeded with user-provided defaults)
    // NOTE: now sets 'visited' fields to false for each seeded city.
    public static TravelConfig Default()
    {
        var tc = new TravelConfig();

        tc.cities.Add(new CityConfig("Cierzo") { price = 200, coords = new float[] { 1410.388f, 6.786f, 1665.642f }, targetGameObjectName = "Cierzo", sceneName = "CierzoNewTerrain", desc = "Cierzo - example description" });
        tc.cities.Add(new CityConfig("Levant") { price = 200, coords = new float[] { -55.212f, 1.056f, 79.379f }, targetGameObjectName = "Levant_Location", sceneName = "Levant", desc = "Levant - example description" });
        tc.cities.Add(new CityConfig("Monsoon") { price = 200, coords = new float[] { 61.553f, -3.743f, 167.599f }, targetGameObjectName = "Monsoon_Location", sceneName = "Monsoon", desc = "Monsoon - example description" });
        tc.cities.Add(new CityConfig("Berg") { price = 200, coords = new float[] { 1202.414f, -13.071f, 1378.836f }, targetGameObjectName = "Berg", sceneName = "Berg", desc = "Berg - example description" });
        tc.cities.Add(new CityConfig("Harmattan") { price = 200, coords = new float[] { 93.757f, 65.474f, 767.849f }, targetGameObjectName = "Harmattan_Location", sceneName = "Harmattan", desc = "Harmattan - example description" });
        tc.cities.Add(new CityConfig("Sirocco") { price = 200, coords = new float[] { 62.530f, 56.805f, -54.049f }, targetGameObjectName = "Sirocco_Location", sceneName = "NewSirocco", desc = "Sirocco - example description" });

        return tc;
    }
}