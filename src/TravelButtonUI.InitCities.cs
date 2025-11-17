using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class TravelButtonUI : MonoBehaviour
{
    // Holds the parsed city list from JSON
    private List<CityEntry> loadedCities;

    // Called early in the GameObject lifecycle
    private void Awake()
    {
        try
        {
            InitCities();
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitCities: unexpected error in Awake: " + ex);
        }
    }

    // Attempt to initialize city list.
    // First try the canonical path provided by TravelButton.GetCitiesJsonPath(),
    // then fall back to the legacy candidate locations for robustness.
    private void InitCities()
    {
        try
        {
            // Try canonical plugin-path first (uses the runtime helper in TravelButton.cs)
            try
            {
                string canonical = null;
                try
                {
                    // Expect TravelButton.GetCitiesJsonPath() to exist and return the canonical path.
                    // If the method is non-public in your codebase, change accessibility or call via your preferred accessor.
                    var mi = typeof(TravelButton).GetMethod("GetCitiesJsonPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null)
                        canonical = mi.Invoke(null, null) as string;
                }
                catch (Exception ex)
                {
                    TBLog.Warn("InitCities: failed calling TravelButton.GetCitiesJsonPath(): " + ex.Message);
                    canonical = null;
                }

                if (!string.IsNullOrEmpty(canonical))
                {
                    try
                    {
                        var full = Path.GetFullPath(canonical);
                        if (File.Exists(full))
                        {
                            var list = ParseCitiesJsonFile(full);
                            if (list != null && list.Count > 0)
                            {
                                loadedCities = list;
                                TBLog.Info($"InitCities: loaded {loadedCities.Count} cities from canonical path: {full}");
                                DumpLoadedCitiesPreview();
                                return;
                            }
                        }
                        else
                        {
                            TBLog.Info($"InitCities: canonical TravelButton_Cities.json not found at: {full}");
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("InitCities: error while trying canonical path '" + canonical + "': " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("InitCities: canonical-path attempt threw: " + ex);
            }

            // Fallback candidate locations (legacy behavior)
            var candidates = new List<string>();

            // Try to obtain BepInEx config path via reflection (no compile-time dependency)
            try
            {
                var pathsType = Type.GetType("BepInEx.Paths, BepInEx");
                if (pathsType != null)
                {
                    var prop = pathsType.GetProperty("ConfigPath", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var cfg = prop.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(cfg))
                        {
                            var bepConfig = Path.Combine(cfg, "TravelButton_Cities.json");
                            candidates.Add(bepConfig);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("InitCities: reflection attempt to read BepInEx.Paths.ConfigPath failed: " + ex);
            }

            // Common game / plugin locations (relative to Application.dataPath)
            try
            {
                candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BepInEx", "config", "TravelButton_Cities.json")));
            }
            catch { }
            try
            {
                candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "TravelButton_Cities.json")));
            }
            catch { }
            try
            {
                candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TravelButton_Cities.json")));
            }
            catch { }

            // Also try plugin DLL directory (robust fallback in case canonical detection failed)
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmDir))
                    candidates.Add(Path.Combine(asmDir, "TravelButton_Cities.json"));
            }
            catch { }

            // Attempt to load from first existing candidate
            foreach (var path in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    string full = Path.GetFullPath(path);
                    if (!File.Exists(full)) continue;

                    var list = ParseCitiesJsonFile(full);
                    if (list != null && list.Count > 0)
                    {
                        loadedCities = list;
                        TBLog.Info($"InitCities: loaded {loadedCities.Count} cities from: {full}");
                        DumpLoadedCitiesPreview();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("InitCities: error while trying candidate path '" + path + "': " + ex);
                }
            }

            TBLog.Warn("InitCities: no valid TravelButton_Cities.json found in canonical or candidate locations; loadedCities is null or empty.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitCities: unexpected outer error: " + ex);
        }
    }

    // Parsing helper reusing the same tolerant JSON parsing as your LoadCitiesFromJson implementation.
    // Returns null on parse error or no entries.
    private List<CityEntry> ParseCitiesJsonFile(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                TBLog.Warn("ParseCitiesJsonFile: filePath is null/empty");
                return null;
            }

            string full = Path.GetFullPath(filePath);
            if (!File.Exists(full))
            {
                TBLog.Warn($"ParseCitiesJsonFile: file not found: {full}");
                return null;
            }

            string txt = File.ReadAllText(full);
            if (string.IsNullOrWhiteSpace(txt))
            {
                TBLog.Warn($"ParseCitiesJsonFile: file is empty: {full}");
                return null;
            }

            JToken root;
            try
            {
                root = JToken.Parse(txt);
            }
            catch (Exception jex)
            {
                TBLog.Warn($"ParseCitiesJsonFile: JSON parse error for file {full}: {jex.Message}");
                return null;
            }

            JArray citiesArray = null;

            if (root.Type == JTokenType.Object)
            {
                var objRoot = (JObject)root;
                var token = objRoot["cities"] ?? objRoot["Cities"] ?? objRoot["list"];
                if (token != null && token.Type == JTokenType.Array)
                    citiesArray = (JArray)token;
                else
                {
                    TBLog.Info($"ParseCitiesJsonFile: root is an object but does not contain 'cities' array; attempting to interpret root as single city object.");
                    citiesArray = new JArray(objRoot);
                }
            }
            else if (root.Type == JTokenType.Array)
            {
                citiesArray = (JArray)root;
            }
            else
            {
                TBLog.Warn($"ParseCitiesJsonFile: unexpected JSON root type {root.Type} in file {full}");
                return null;
            }

            var result = new List<CityEntry>(citiesArray.Count);
            for (int i = 0; i < citiesArray.Count; i++)
            {
                try
                {
                    var item = citiesArray[i];
                    if (item == null || item.Type != JTokenType.Object) continue;
                    var obj = (JObject)item;

                    var city = new CityEntry();

                    // name
                    SetStringMember(city, obj.Value<string>("name") ?? obj.Value<string>("Name") ?? string.Empty, "name", "Name");

                    // sceneName
                    SetStringMember(city, obj.Value<string>("sceneName") ?? obj.Value<string>("SceneName") ?? obj.Value<string>("scene") ?? string.Empty,
                                    "sceneName", "SceneName", "scene");

                    // targetGameObjectName
                    SetStringMember(city, obj.Value<string>("targetGameObjectName") ?? obj.Value<string>("target") ?? string.Empty,
                                    "targetGameObjectName", "target", "targetName");

                    // desc (use reflection to set whatever member CityEntry actually has)
                    SetStringMember(city, obj.Value<string>("desc") ?? obj.Value<string>("description") ?? string.Empty,
                                    "desc", "description", "descText", "Description");

                    // price (int) - try setting via known member names, using reflection if needed
                    int price = -1;
                    var priceToken = obj["price"] ?? obj["Price"];
                    if (priceToken != null && priceToken.Type != JTokenType.Null)
                    {
                        try { price = priceToken.Value<int>(); } catch { try { price = Convert.ToInt32(priceToken.Value<double>()); } catch { price = -1; } }
                    }
                    // Try to set an int property/field named "price" / "Price"
                    try
                    {
                        var t = city.GetType();
                        var p = t.GetProperty("price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                                ?? t.GetProperty("Price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        if (p != null && p.CanWrite && p.PropertyType == typeof(int)) p.SetValue(city, price);
                        else
                        {
                            var f = t.GetField("price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                                    ?? t.GetField("Price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (f != null && f.FieldType == typeof(int)) f.SetValue(city, price);
                        }
                    }
                    catch { /* ignore */ }

                    // visited (bool)
                    bool visited = false;
                    var visitedToken = obj["visited"] ?? obj["Visited"];
                    if (visitedToken != null && visitedToken.Type != JTokenType.Null)
                    {
                        try { visited = visitedToken.Value<bool>(); } catch { visited = false; }
                    }
                    try
                    {
                        var t = city.GetType();
                        var p = t.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                                ?? t.GetProperty("Visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) p.SetValue(city, visited);
                        else
                        {
                            var f = t.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                                    ?? t.GetField("Visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (f != null && f.FieldType == typeof(bool)) f.SetValue(city, visited);
                        }
                    }
                    catch { /* ignore */ }

                    // coords (float[] of length >=3)
                    var coordsToken = obj["coords"] ?? obj["Coords"];
                    if (coordsToken != null && coordsToken.Type == JTokenType.Array)
                    {
                        try
                        {
                            var arr = (JArray)coordsToken;
                            if (arr.Count >= 3)
                            {
                                float[] coords = new float[3];
                                coords[0] = arr[0].Value<float>();
                                coords[1] = arr[1].Value<float>();
                                coords[2] = arr[2].Value<float>();
                                SetFloatArrayMember(city, coords, "coords", "Coords", "position", "Position");
                            }
                        }
                        catch { /* ignore invalid coords */ }
                    }

                    result.Add(city);
                }
                catch (Exception exItem)
                {
                    TBLog.Warn($"ParseCitiesJsonFile: failed to parse city entry index {i} in {full}: {exItem.Message}");
                    // continue with other entries
                }
            }

            if (result.Count == 0)
            {
                TBLog.Warn($"ParseCitiesJsonFile: parsed JSON but found 0 cities in {full}");
                return null;
            }

            TBLog.Info($"ParseCitiesJsonFile: successfully parsed {result.Count} cities from: {full}");
            return result;
        }
        catch (Exception ex)
        {
            TBLog.Warn("ParseCitiesJsonFile: unexpected error: " + ex);
            return null;
        }
    }

    private void DumpLoadedCitiesPreview()
    {
        try
        {
            for (int i = 0; i < Math.Min(loadedCities.Count, 10); i++)
            {
                var c = loadedCities[i];
                TBLog.Info($"  city[{i}] name='{c?.name}' scene='{c?.sceneName}' coords={(c?.coords == null ? "null" : string.Join(",", c.coords))}");
            }
        }
        catch { }
    }

    // Helper: expose loadedCities to other code if needed
    private IReadOnlyList<CityEntry> GetLoadedCities()
    {
        return loadedCities;
    }

    private void SetStringMember(object target, string value, params string[] candidateNames)
    {
        if (target == null || candidateNames == null || candidateNames.Length == 0) return;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                {
                    p.SetValue(target, value);
                    return;
                }

                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null && f.FieldType == typeof(string))
                {
                    f.SetValue(target, value);
                    return;
                }
            }
            catch { /* swallow - try next candidate */ }
        }
    }

    // Optionally helper for float[] coords if CityEntry uses different member name
    private void SetFloatArrayMember(object target, float[] arr, params string[] candidateNames)
    {
        if (target == null || candidateNames == null || candidateNames.Length == 0) return;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null && p.CanWrite && p.PropertyType == typeof(float[]))
                {
                    p.SetValue(target, arr);
                    return;
                }

                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null && f.FieldType == typeof(float[]))
                {
                    f.SetValue(target, arr);
                    return;
                }
            }
            catch { /* swallow - try next candidate */ }
        }
    }


}