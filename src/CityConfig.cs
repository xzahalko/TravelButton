using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// DTOs and JSON helpers for TravelButton_Cities.json
/// TravelConfig is the root object used by JsonUtility. CityConfig describes per-city metadata.
/// LoadFromFile supports both array-form and object-keyed JSON shapes.
/// </summary>
[Serializable]
public class CityConfig
{
    public string name;
    public int? price;
    public float[] coords;
    public string targetGameObjectName;
    public string sceneName;
    public string desc;
    public bool visited;

    public CityConfig() { }
    public CityConfig(string name) { this.name = name; }
}

[Serializable]
public class TravelConfig
{
    // JsonUtility-friendly array wrapper
    public List<CityConfig> cities = new List<CityConfig>();

    /// <summary>
    /// Load TravelConfig from a given path. Supports two JSON shapes:
    ///  - wrapper object with array: { "cities": [ { "name": "...", ... }, ... ] }
    ///  - keyed object: { "cities": { "TownA": { ... }, "TownB": { ... } } }
    /// Returns null if file not found or parsing failed.
    /// </summary>
    public static TravelConfig LoadFromFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var txt = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt)) return null;

            // Try direct JsonUtility parse first (expects array wrapper)
            try
            {
                var parsed = JsonUtility.FromJson<TravelConfig>(txt);
                if (parsed != null && parsed.cities != null && parsed.cities.Count > 0)
                    return parsed;
            }
            catch { /* fallthrough to keyed-object parse */ }

            // Fallback: detect keyed-object form "cities": { "Name": {...}, ... }
            // We'll try a lightweight parse to transform it into TravelConfig with array.
            // Use regex to capture the inner object and then parse each city object by name.
            try
            {
                // find the "cities" property
                var m = Regex.Match(txt, "\"cities\"\\s*:\\s*\\{", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    // find the brace start index
                    int start = m.Index + m.Length - 1; // position of '{'
                    // attempt to find the matching closing brace for this object
                    int end = FindMatchingClosingBrace(txt, start);
                    if (end > start)
                    {
                        string inner = txt.Substring(start + 1, end - start - 1);
                        var cfg = new TravelConfig();
                        int pos = 0;
                        while (pos < inner.Length)
                        {
                            // skip whitespace and commas
                            while (pos < inner.Length && (char.IsWhiteSpace(inner[pos]) || inner[pos] == ',')) pos++;
                            if (pos >= inner.Length) break;
                            // expect quoted city name
                            if (inner[pos] != '"')
                            {
                                int q = inner.IndexOf('"', pos);
                                if (q < 0) break;
                                pos = q;
                            }
                            int qend = FindClosingQuote(inner, pos);
                            if (qend < 0) break;
                            string cityName = inner.Substring(pos + 1, qend - pos - 1);
                            pos = qend + 1;
                            // skip to colon
                            while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
                            if (pos < inner.Length && inner[pos] == ':') pos++;
                            while (pos < inner.Length && char.IsWhiteSpace(inner[pos])) pos++;
                            if (pos >= inner.Length || inner[pos] != '{') break;
                            int objStart = pos;
                            int objEnd = FindMatchingClosingBrace(inner, objStart);
                            if (objEnd < 0) break;
                            string objInner = inner.Substring(objStart, objEnd - objStart + 1); // includes braces
                            // We have raw JSON for the city's object; insert name property to allow JsonUtility parse
                            string wrapped = "{ \"name\": \"" + EscapeForJson(cityName) + "\", \"__inner\": " + objInner + " }";
                            // Replace __inner placeholder by removing it: we need a valid flattened object
                            // Simpler approach: manually parse known fields using regex
                            var city = new CityConfig(cityName);
                            // price
                            var mPrice = Regex.Match(objInner, "\"price\"\\s*:\\s*([-0-9.]+)", RegexOptions.IgnoreCase);
                            if (mPrice.Success && int.TryParse(mPrice.Groups[1].Value, out int pval)) city.price = pval;
                            // coords array
                            var mCoords = Regex.Match(objInner, "\"coords\"\\s*:\\s*\\[([^\\]]+)\\]", RegexOptions.IgnoreCase);
                            if (mCoords.Success)
                            {
                                var parts = mCoords.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                                var list = new List<float>();
                                foreach (var part in parts)
                                {
                                    if (float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fv))
                                        list.Add(fv);
                                }
                                if (list.Count >= 3) city.coords = list.ToArray();
                            }
                            // targetGameObjectName
                            var mTgn = Regex.Match(objInner, "\"targetGameObjectName\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (mTgn.Success) city.targetGameObjectName = mTgn.Groups[1].Value;
                            // sceneName
                            var mScn = Regex.Match(objInner, "\"sceneName\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (mScn.Success) city.sceneName = mScn.Groups[1].Value;
                            // desc
                            var mDesc = Regex.Match(objInner, "\"desc\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (mDesc.Success) city.desc = mDesc.Groups[1].Value;
                            // visited
                            var mVisited = Regex.Match(objInner, "\"visited\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                            if (mVisited.Success && bool.TryParse(mVisited.Groups[1].Value, out bool bv)) city.visited = bv;
                            cfg.cities.Add(city);
                            pos = objEnd + 1;
                        }
                        if (cfg.cities.Count > 0) return cfg;
                    }
                }
            }
            catch { /* ignore fallback parse errors */ }

            return null;
        }
        catch { return null; }
    }

    // Save JSON to file path using JsonUtility (array wrapper). Returns true on success.
    public bool SaveToFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string txt = JsonUtility.ToJson(this, true);
            File.WriteAllText(path, txt, System.Text.Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    private static int FindMatchingClosingBrace(string json, int startIndex)
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

    private static int FindClosingQuote(string json, int idx)
    {
        if (string.IsNullOrEmpty(json) || idx < 0 || idx >= json.Length) return -1;
        if (json[idx] != '"') return -1;
        for (int i = idx + 1; i < json.Length; i++)
            if (json[i] == '"' && json[i - 1] != '\\') return i;
        return -1;
    }

    private static string EscapeForJson(string s)
    {
        return s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
    }

    // Provide a default TravelConfig (seeded with user-provided defaults)
    public static TravelConfig Default()
    {
        var tc = new TravelConfig();
        tc.cities.Add(new CityConfig("Cierzo") { price = 200, coords = new float[] { 1410.388f, 6.786f, 1665.642f }, targetGameObjectName = "Cierzo", sceneName = "CierzoNewTerrain", desc = "Cierzo - example description", visited = false });
        tc.cities.Add(new CityConfig("Levant") { price = 200, coords = new float[] { -55.212f, 1.056f, 79.379f }, targetGameObjectName = "WarpLocation_HM", sceneName = "Levant", desc = "Levant - example description", visited = false });
        tc.cities.Add(new CityConfig("Monsoon") { price = 200, coords = new float[] { 61.553f, -3.743f, 167.599f }, targetGameObjectName = "Monsoon_Location", sceneName = "Monsoon", desc = "Monsoon - example description", visited = false });
        tc.cities.Add(new CityConfig("Berg") { price = 200, coords = new float[] { 1202.414f, -13.071f, 1378.836f }, targetGameObjectName = "Berg", sceneName = "Berg", desc = "Berg - example description", visited = false });
        tc.cities.Add(new CityConfig("Harmattan") { price = 200, coords = new float[] { 93.757f, 65.474f, 767.849f }, targetGameObjectName = "Harmattan_Location", sceneName = "Harmattan", desc = "Harmattan - example description", visited = false });
        tc.cities.Add(new CityConfig("Sirocco") { price = 200, coords = new float[] { 600.0f, 1.2f, -300.0f }, targetGameObjectName = "Sirocco_Location", sceneName = "NewSirocco", desc = "Sirocco - example description", visited = false });
        return tc;
    }
}