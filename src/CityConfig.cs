using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// CityConfig.cs: DTO classes and JSON helpers for TravelButton_Cities.json
// Uses UnityEngine.JsonUtility for serialization (lightweight, no external dependencies)

/// <summary>
/// DTO representing a single city's configuration
/// </summary>
[Serializable]
public class CityConfig
{
    public string name;
    public float[] coords;
    public string targetGameObjectName;
    public string sceneName;
    public string desc;
    public int price;
    
    // Note: enabled and visited are managed by BepInEx and VisitedTracker respectively
    // They are not stored in the JSON file to avoid duplication
}

/// <summary>
/// DTO representing the entire travel configuration
/// </summary>
[Serializable]
public class TravelConfig
{
    public CityConfig[] cities;
    
    /// <summary>
    /// Load TravelConfig from a JSON file using UnityEngine.JsonUtility
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
            if (wrapper != null && wrapper.cities != null && wrapper.cities.Length > 0)
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
    /// Parse object-keyed format where cities is a dictionary
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
                config.cities = cityList.ToArray();
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
            city.price = priceValue ?? 200; // Default price if not specified
            
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
    /// </summary>
    public static bool SaveToFile(string filePath, TravelConfig config)
    {
        if (string.IsNullOrEmpty(filePath) || config == null)
        {
            return false;
        }
        
        try
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            string json = JsonUtility.ToJson(config, true);
            File.WriteAllText(filePath, json);
            
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
    // NOTE: Does not set 'visited' field - visited state is managed by VisitedTracker, not JSON
    public static TravelConfig Default()
    {
        var tc = new TravelConfig();
        tc.cities.Add(new CityConfig("Cierzo") { price = 200, coords = new float[] { 1410.388f, 6.786f, 1665.642f }, targetGameObjectName = "Cierzo", sceneName = "CierzoNewTerrain", desc = "Cierzo - example description" });
        tc.cities.Add(new CityConfig("Levant") { price = 200, coords = new float[] { -55.212f, 1.056f, 79.379f }, targetGameObjectName = "WarpLocation_HM", sceneName = "Levant", desc = "Levant - example description" });
        tc.cities.Add(new CityConfig("Monsoon") { price = 200, coords = new float[] { 61.553f, -3.743f, 167.599f }, targetGameObjectName = "Monsoon_Location", sceneName = "Monsoon", desc = "Monsoon - example description" });
        tc.cities.Add(new CityConfig("Berg") { price = 200, coords = new float[] { 1202.414f, -13.071f, 1378.836f }, targetGameObjectName = "Berg", sceneName = "Berg", desc = "Berg - example description" });
        tc.cities.Add(new CityConfig("Harmattan") { price = 200, coords = new float[] { 93.757f, 65.474f, 767.849f }, targetGameObjectName = "Harmattan_Location", sceneName = "Harmattan", desc = "Harmattan - example description" });
        tc.cities.Add(new CityConfig("Sirocco") { price = 200, coords = new float[] { 600.0f, 1.2f, -300.0f }, targetGameObjectName = "Sirocco_Location", sceneName = "NewSirocco", desc = "Sirocco - example description" });
        return tc;
    }
}
