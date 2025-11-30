using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Newtonsoft.Json;

[Serializable]
public class CityConfig
{
    public string name;
    public int price = -1;
    public float[] coords;
    public string targetGameObjectName;
    public string sceneName;
    public string desc;
    public bool visited = false;
    
    // New fields for multi-variant support
    public string[] variants;
    public string lastKnownVariant;
    
    public CityConfig() { }

    public CityConfig(string name) { this.name = name; }

    // Prevent double-serialization: ignore the property in JSON so only the 'visited' field is written.
    [JsonIgnore]
    public bool Visited
    {
        get => visited;
        set => visited = value;
    }

    public CityConfig(string name, int price, float[] coords, string targetGameObjectName, string sceneName, string desc)
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
    public List<CityConfig> cities = new List<CityConfig>();

    public static TravelConfig LoadFromFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        try
        {
            string json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var wrapper = JsonUtility.FromJson<TravelConfig>(json);
            if (wrapper != null && wrapper.cities != null && wrapper.cities.Count > 0) return wrapper;
            return ParseObjectKeyedForm(json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] TravelConfig.LoadFromFile failed: {ex.Message}");
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
            int citiesIdx = json.IndexOf("\"cities\"", StringComparison.OrdinalIgnoreCase);
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
            city.price = priceValue ?? -1; // use -1 sentinel when json omits price

            // Try to extract visited flag if present
            bool? visitedVal = ExtractBool(cityJson, "visited");
            city.visited = visitedVal ?? false;
            
            // Try to extract variants array if present
            city.variants = ExtractStringArray(cityJson, "variants");
            
            // Try to extract lastKnownVariant if present
            city.lastKnownVariant = ExtractString(cityJson, "lastKnownVariant");
            
            // Backward compatibility: if variants not present but legacy variant fields exist, migrate them
            if ((city.variants == null || city.variants.Length == 0))
            {
                var variantNormal = ExtractString(cityJson, "variantNormalName");
                var variantDestroyed = ExtractString(cityJson, "variantDestroyedName");
                
                if (!string.IsNullOrEmpty(variantNormal) || !string.IsNullOrEmpty(variantDestroyed))
                {
                    var variantsList = new List<string>();
                    if (!string.IsNullOrEmpty(variantNormal)) variantsList.Add(variantNormal);
                    if (!string.IsNullOrEmpty(variantDestroyed)) variantsList.Add(variantDestroyed);
                    city.variants = variantsList.ToArray();
                    
                    // If lastKnownVariant not present, default to normal variant
                    if (string.IsNullOrEmpty(city.lastKnownVariant) && !string.IsNullOrEmpty(variantNormal))
                    {
                        city.lastKnownVariant = variantNormal;
                    }
                }
            }

            return city;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TravelButton] ParseCityObject failed for {cityName}: {ex.Message}");
            return null;
        }
    }

    private static string ExtractString(string json, string propName)
    {
        int idx = json.IndexOf($"\"{propName}\"", StringComparison.OrdinalIgnoreCase);
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
        int idx = json.IndexOf($"\"{propName}\"", StringComparison.OrdinalIgnoreCase);
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
        int idx = json.IndexOf($"\"{propName}\"", StringComparison.OrdinalIgnoreCase);
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
        int idx = json.IndexOf($"\"{propName}\"", StringComparison.OrdinalIgnoreCase);
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

    private static string[] ExtractStringArray(string json, string propName)
    {
        int idx = json.IndexOf($"\"{propName}\"", StringComparison.OrdinalIgnoreCase);
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
        
        // Parse array content - handle quoted strings
        var strings = new List<string>();
        int pos = 0;
        while (pos < arrContent.Length)
        {
            // Skip whitespace and commas
            while (pos < arrContent.Length && (char.IsWhiteSpace(arrContent[pos]) || arrContent[pos] == ','))
                pos++;
            
            if (pos >= arrContent.Length) break;
            
            // Expect quoted string
            if (arrContent[pos] != '"') break;
            
            int strEnd = FindClosingQuote(arrContent, pos);
            if (strEnd < 0) break;
            
            string str = arrContent.Substring(pos + 1, strEnd - pos - 1);
            strings.Add(str);
            pos = strEnd + 1;
        }

        return strings.Count > 0 ? strings.ToArray() : null;
    }

    private static int FindClosingQuote(string json, int startIdx)
    {
        if (startIdx < 0 || startIdx >= json.Length) return -1;
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
        if (startIdx < 0 || startIdx >= json.Length) return -1;
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
        if (startIdx < 0 || startIdx >= json.Length) return -1;
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

    // Provide a default TravelConfig (seeded with user-provided defaults)
    // This implementation obtains canonical defaults from ConfigManager.Default() and maps them into this TravelConfig form.
    // If ConfigManager.Default() is unavailable or mapping fails, returns an empty TravelConfig.
    public static TravelConfig Default()
    {
        try
        {
            // Try to call ConfigManager.Default() via reflection to avoid hard compile-time coupling.
            Type cmType = null;
            try
            {
                cmType = Type.GetType("ConfigManager") ?? typeof(ConfigManager);
            }
            catch { cmType = null; }

            if (cmType != null)
            {
                var mi = cmType.GetMethod("Default", BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                {
                    var defaultsObj = mi.Invoke(null, null);
                    if (defaultsObj != null)
                    {
                        // Try to read a 'cities' member (dictionary or IEnumerable)
                        object citiesVal = null;
                        var prop = defaultsObj.GetType().GetProperty("cities", BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null) citiesVal = prop.GetValue(defaultsObj);
                        else
                        {
                            var field = defaultsObj.GetType().GetField("cities", BindingFlags.Public | BindingFlags.Instance);
                            if (field != null) citiesVal = field.GetValue(defaultsObj);
                        }

                        if (citiesVal != null)
                        {
                            var result = new TravelConfig();

                            // If it's a dictionary (object keyed), iterate entries
                            if (citiesVal is IDictionary dict)
                            {
                                CityMappingHelpers.MapDefaultCityToTarget(citiesVal, result.cities);
                                return result;
                            }

                            // If it's an IEnumerable (list), iterate items and map
                            if (citiesVal is IEnumerable ie)
                            {
                                CityMappingHelpers.MapParsedCityToTarget(citiesVal, result.cities);
                                return result;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] TravelConfig.Default reflection/mapping failed: " + ex);
        }

        // Last resort: return an empty TravelConfig
        return new TravelConfig();
    }
}
