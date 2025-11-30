using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using static TravelButtonUI;

/// <summary>
/// Helper utilities to convert parsed JSON DTOs (CityEntry) and ConfigManager defaults
/// into the runtime collection type expected by TravelButton.Cities.
/// - Robust parsing of TravelButton_Cities.json (supports variants[] and lastKnownVariant)
/// - Converts parsed DTO -> runtime city instances via reflection (three strategies)
/// - Converts ConfigManager.Default() -> runtime collection if JSON missing
/// - Assigns converted collection to TravelButton.Cities (static field/property) if compatible
/// - Minimizes duplicate reads/writes: this helper reads JSON/defaults and prepares runtime objects.
/// </summary>
public static class CityMappingHelpers
{
    // Holds the parsed city list from JSON (kept for diagnostics)
    private static List<CityEntry> loadedCities;

    // ----------------
    // Public entrypoints
    // ----------------

    // Attempt to initialize city list.
    // First try the canonical path provided by TravelButton.GetCitiesJsonPath(),
    // then fall back to the legacy candidate locations for robustness.
    public static void InitCities()
    {
        try
        {
            // Try canonical plugin-path first (uses the runtime helper in TravelButton.cs)
            try
            {
                var canonical = TryGetCanonicalCitiesJsonPath();

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
                    // ConfigPath is a public static property on BepInEx.Paths
                    var prop = pathsType.GetProperty("ConfigPath", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var cfg = prop.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(cfg))
                        {
                            string fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";
                            try
                            {
                                fileName = typeof(TravelButtonPlugin).GetField("CitiesJsonFileName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null
                                    ? TravelButtonPlugin.CitiesJsonFileName
                                    : fileName;
                            }
                            catch { /* ignore and use literal fallback */ }

                            var bepConfig = Path.Combine(cfg, fileName);
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
                candidates.Add(Path.GetFullPath(Path.Combine(TravelButtonPlugin.GetCitiesJsonPath(), TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json")));
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

    /// <summary>
    /// Parse a JSON file into a list of CityEntry DTOs.
    /// Tolerant to leading comment lines and supports:
    ///  - single object root (treated as one city)
    ///  - object root with 'cities' array
    ///  - array root
    /// Supports fields: name, sceneName, targetGameObjectName, coords, price, visited, variants, lastKnownVariant, desc
    /// Returns null on parse error or zero entries.
    /// </summary>
    public static List<CityEntry> ParseCitiesJsonFile(string filePath)
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

            // Parse JSON but ignore comments (allow files with // header comments)
            JToken root;
            try
            {
                // Trim only the leading comment/header block so internal comments in JSON (if any) remain untouched.
                string trimmed;
                using (var reader = new StringReader(txt))
                {
                    var sb = new System.Text.StringBuilder();
                    string line;
                    bool seenNonComment = false;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!seenNonComment)
                        {
                            if (string.IsNullOrWhiteSpace(line))
                                continue; // skip leading blank lines
                            var t = line.TrimStart();
                            if (t.StartsWith("//")) continue; // skip leading comment line
                                                              // first non-comment, non-blank line -> start keeping from here
                            seenNonComment = true;
                        }
                        sb.AppendLine(line);
                    }
                    trimmed = sb.ToString();
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    TBLog.Warn($"ParseCitiesJsonFile: file contained only comments/whitespace: {full}");
                    return null;
                }

                root = JToken.Parse(trimmed);
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
                                SetFloatArrayOnTarget(city, coords, "coords", "Coords", "position", "Position");
                            }
                        }
                        catch { /* ignore invalid coords */ }
                    }

                    // variants (string[] array)
                    var variantsToken = obj["variants"] ?? obj["Variants"];
                    if (variantsToken != null && variantsToken.Type == JTokenType.Array)
                    {
                        try
                        {
                            var varArr = (JArray)variantsToken;
                            var variants = new List<string>();
                            foreach (var vToken in varArr)
                            {
                                if (vToken != null && vToken.Type == JTokenType.String)
                                {
                                    variants.Add(vToken.Value<string>());
                                }
                            }
                            if (variants.Count > 0)
                            {
                                SetStringArrayOnTarget(city, variants.ToArray(), "variants", "Variants");
                            }
                        }
                        catch { /* ignore invalid variants */ }
                    }

                    // lastKnownVariant (string)
                    var lastKnownVariantToken = obj["lastKnownVariant"] ?? obj["LastKnownVariant"];
                    if (lastKnownVariantToken != null && lastKnownVariantToken.Type == JTokenType.String)
                    {
                        try
                        {
                            var lastKnownVariant = lastKnownVariantToken.Value<string>();
                            if (!string.IsNullOrEmpty(lastKnownVariant))
                            {
                                SetStringOnTarget(city, lastKnownVariant, "lastKnownVariant", "LastKnownVariant");
                            }
                        }
                        catch { /* ignore invalid lastKnownVariant */ }
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

    // Diagnostic preview of loadedCities
    private static void DumpLoadedCitiesPreview()
    {
        try
        {
            if (loadedCities == null) { TBLog.Info("DumpLoadedCitiesPreview: loadedCities null"); return; }
            for (int i = 0; i < Math.Min(loadedCities.Count, 10); i++)
            {
                var c = loadedCities[i];
                // Use reflection-safe getters to avoid compile-time errors when underlying CityEntry varies across assemblies
                string name = GetStringFromSource(c, "name") ?? GetStringFromSource(c, "Name") ?? "(null)";
                string scene = GetStringFromSource(c, "sceneName") ?? GetStringFromSource(c, "scene") ?? "(null)";

                string coordsStr;
                try
                {
                    var coords = GetFloatArrayFromSource(c, "coords") ?? GetFloatArrayFromSource(c, "Coords");
                    coordsStr = coords == null ? "null" : string.Join(",", coords);
                }
                catch { coordsStr = "null"; }

                string variantsStr;
                try
                {
                    var variants = GetStringArrayFromSource(c, "variants") ?? GetStringArrayFromSource(c, "Variants");
                    variantsStr = variants == null ? "null" : $"[{string.Join(", ", variants)}]";
                }
                catch { variantsStr = "null"; }

                string lastKnownVariant = GetStringFromSource(c, "lastKnownVariant") ?? GetStringFromSource(c, "LastKnownVariant") ?? "null";

                TBLog.Info($"  city[{i}] name='{name}' scene='{scene}' coords=[{coordsStr}] variants={variantsStr} lastKnownVariant={lastKnownVariant}");
            }
        }
        catch { }
    }

    // Helper: search loadedCities (if available) by name or sceneName (case-insensitive)
    private static bool TryFindCityByNameOrScene(string candidate, out CityEntry outCity)
    {
        outCity = null;
        if (string.IsNullOrEmpty(candidate)) return false;
        try
        {
            // loadedCities is set by InitCities earlier
            var list = loadedCities;
            if (list == null || list.Count == 0) return false;

            string cLower = candidate.Trim().ToLowerInvariant();
            foreach (var c in list)
            {
                if (c == null) continue;
                if (!string.IsNullOrEmpty(c.name) && c.name.Trim().ToLowerInvariant() == cLower) { outCity = c; return true; }
                if (!string.IsNullOrEmpty(c.sceneName) && c.sceneName.Trim().ToLowerInvariant() == cLower) { outCity = c; return true; }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryFindCityByNameOrScene: lookup failed: " + ex);
        }
        return false;
    }

    // Try to resolve cities from canonical JSON or ConfigManager defaults, convert to runtime, assign, and persist only if needed.
    // This method is intended to be called once at startup by the plugin bootstrap.
    public static void EnsureCitiesInitializedFromJsonOrDefaults()
    {
        try
        {
            var runtime = TravelButton.Cities ?? new List<City>();

            // If runtime appears empty, attempt to populate from config defaults (best-effort)
            if ((runtime == null || runtime.Count == 0))
            {
                TravelButton.InitFromConfig(); // safe to call (it returns quickly if nothing to load)
                runtime = TravelButton.Cities ?? new List<City>();
            }

            var lookup = new Dictionary<string, City>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in runtime)
            {
                if (string.IsNullOrEmpty(c?.name)) continue;
                lookup[c.name] = c;
            }

            try
            {
                var defaults = TravelButton.Cities ?? new List<City>();
                foreach (var d in defaults)
                {
                    if (string.IsNullOrEmpty(d?.name)) continue;
                    if (!lookup.TryGetValue(d.name, out var existing))
                    {
                        var clone = new City(d.name)
                        {
                            coords = d.coords,
                            sceneName = d.sceneName,
                            targetGameObjectName = d.targetGameObjectName,
                            price = d.price,
                            enabled = d.enabled,
                            visited = d.visited,
                            lastKnownVariant = d.lastKnownVariant ?? ""
                        };
                        lookup[clone.name] = clone;
                        TBLog.Info($"EnsureCitiesInitializedFromJsonOrDefaults: New city '{clone.name}': enabled={clone.enabled} price={(clone.price.HasValue ? clone.price.Value.ToString() : "null")} visited={clone.visited}");
                    }
                    else
                    {
                        if (existing.lastKnownVariant == null) existing.lastKnownVariant = d.lastKnownVariant ?? "";
                        if (existing.sceneName == null) existing.sceneName = d.sceneName;
                        if (existing.targetGameObjectName == null) existing.targetGameObjectName = d.targetGameObjectName;
                        if (!existing.price.HasValue && d.price.HasValue) existing.price = d.price;
                    }
                }
            }
            catch (Exception exMerge)
            {
                TBLog.Warn("EnsureCitiesInitializedFromJsonOrDefaults: merge defaults failed: " + exMerge.Message);
            }

            var final = lookup.Values.ToList();
            foreach (var c in final)
            {
                if (c.lastKnownVariant == null) c.lastKnownVariant = "";
            }

            TravelButton.Cities = final;
            TBLog.Info($"EnsureCitiesInitializedFromJsonOrDefaults: final Cities count = {TravelButton.Cities?.Count ?? 0}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("EnsureCitiesInitializedFromJsonOrDefaults: " + ex.Message);
        }
    }

    // -----------------------
    // Conversion & assignment
    // -----------------------

    // Convert parsed DTOs to the runtime collection expected by TravelButton.Cities.
    // Strategy:
    //  1) If travelButton.Cities element type is discoverable, try to instantiate elementType and map fields via reflection
    //  2) If that fails, JSON roundtrip to elementType
    //  3) If still fails, attempt GetUninitializedObject + reflection mapping
    public static object ConvertParsedCitiesToRuntime(List<CityEntry> parsed)
    {
        if (parsed == null) return null;
        try
        {
            var citiesField = typeof(TravelButton).GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var citiesProp = typeof(TravelButton).GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) as PropertyInfo;
            Type citiesType = null;

            if (citiesField != null) citiesType = citiesField.FieldType;
            else if (citiesProp != null) citiesType = citiesProp.PropertyType;

            if (citiesType != null)
            {
                Type elementType = GetElementTypeFromCollectionType(citiesType);
                if (elementType != null)
                {
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var listInstance = (IList)Activator.CreateInstance(listType);

                    foreach (var src in parsed)
                    {
                        try
                        {
                            object dst = null;
                            // 1) default ctor + map
                            try
                            {
                                dst = Activator.CreateInstance(elementType);
                                MapParsedCityToTarget(src, dst);
                            }
                            catch { dst = null; }

                            // 2) json roundtrip
                            if (dst == null)
                            {
                                try
                                {
                                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(src);
                                    dst = Newtonsoft.Json.JsonConvert.DeserializeObject(json, elementType);
                                }
                                catch (Exception jex)
                                {
                                    TBLog.Warn($"ConvertParsedCitiesToRuntime: json roundtrip conversion failed for elementType={elementType}: {jex.Message}");
                                    dst = null;
                                }
                            }

                            // 3) uninitialized + map
                            if (dst == null)
                            {
                                try
                                {
                                    var uninit = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(elementType);
                                    if (uninit != null)
                                    {
                                        MapParsedCityToTarget(src, uninit);
                                        dst = uninit;
                                    }
                                }
                                catch (Exception uex)
                                {
                                    TBLog.Warn($"ConvertParsedCitiesToRuntime: GetUninitializedObject failed for {elementType}: {uex.Message}");
                                }
                            }

                            if (dst != null) listInstance.Add(dst);
                            else
                            {
                                TBLog.Warn($"ConvertParsedCitiesToRuntime: unable to convert parsed item to {elementType}; aborting conversion.");
                                return null;
                            }
                        }
                        catch (Exception exItem)
                        {
                            TBLog.Warn("ConvertParsedCitiesToRuntime: failed converting a parsed city entry: " + exItem);
                        }
                    }

                    return listInstance;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("CityMappingHelpers.ConvertParsedCitiesToRuntime: reflection-based conversion failed: " + ex);
        }

        // fallback: return parsed list (List<CityEntry>)
        return parsed;
    }

    // Convert ConfigManager.Default() defaults object into runtime collection expected by TravelButton.Cities.
    public static object ConvertConfigManagerDefaultsToRuntime(object configManagerDefaults)
    {
        if (configManagerDefaults == null) return null;

        try
        {
            var defaultsType = configManagerDefaults.GetType();
            IEnumerable<object> defaultsCities = null;

            // try property or field named 'cities' (instance)
            var prop = defaultsType.GetProperty("cities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var val = prop.GetValue(configManagerDefaults);
                if (val is IEnumerable e) defaultsCities = e.Cast<object>().ToList();
            }
            else
            {
                var field = defaultsType.GetField("cities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    var val = field.GetValue(configManagerDefaults);
                    if (val is IEnumerable e) defaultsCities = e.Cast<object>().ToList();
                }
            }

            if (defaultsCities == null)
            {
                TBLog.Warn("ConvertConfigManagerDefaultsToRuntime: could not locate a 'cities' collection on defaults - returning empty list.");
                return new List<CityEntry>();
            }

            var citiesField = typeof(TravelButton).GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var citiesProp = typeof(TravelButton).GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) as PropertyInfo;
            Type citiesType = (citiesField != null) ? citiesField.FieldType : (citiesProp != null ? citiesProp.PropertyType : null);

            if (citiesType != null)
            {
                Type elementType = GetElementTypeFromCollectionType(citiesType);
                if (elementType != null)
                {
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var listInstance = (IList)Activator.CreateInstance(listType);

                    foreach (var src in defaultsCities)
                    {
                        var dst = Activator.CreateInstance(elementType);
                        MapDefaultCityToTarget(src, dst);
                        listInstance.Add(dst);
                    }
                    return listInstance;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ConvertConfigManagerDefaultsToRuntime: conversion failed: " + ex);
        }

        // fallback: try to build List<CityEntry> from defaults
        try
        {
            var fallback = new List<CityEntry>();
            var defaultsType2 = configManagerDefaults.GetType();
            var prop2 = defaultsType2.GetProperty("cities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop2 != null)
            {
                var val = prop2.GetValue(configManagerDefaults) as IEnumerable;
                if (val != null)
                {
                    foreach (var item in val)
                    {
                        var ce = new CityEntry();
                        SetStringOnTarget(ce, GetStringFromSource(item, "name"), "name");
                        SetStringOnTarget(ce, GetStringFromSource(item, "sceneName") ?? GetStringFromSource(item, "scene"), "sceneName");
                        SetStringOnTarget(ce, GetStringFromSource(item, "targetGameObjectName") ?? GetStringFromSource(item, "target"), "targetGameObjectName");
                        SetStringOnTarget(ce, GetStringFromSource(item, "desc") ?? GetStringFromSource(item, "description"), "desc");
                        var price = GetIntFromSource(item, "price");
                        ce.price = price ?? -1;
                        var v = GetBoolFromSource(item, "visited");
                        ce.visited = v ?? false;
                        var coords = GetFloatArrayFromSource(item, "coords");
                        ce.coords = coords;
                        fallback.Add(ce);
                    }
                    return fallback;
                }
            }
        }
        catch { }

        return new List<CityEntry>();
    }

    // Assign the converted collection object into TravelButton.Cities (static field or property).
    // Returns true if assignment succeeded.
    private static bool AssignConvertedCitiesToTravelButton(object converted)
    {
        if (converted == null) return false;

        var travelButtonType = typeof(TravelButton);

        var field = travelButtonType.GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            if (field.FieldType.IsAssignableFrom(converted.GetType()))
            {
                field.SetValue(null, converted);
                return true;
            }

            if (TryBuildAndAssignCollectionFromEnumerable(converted as IEnumerable, field.FieldType, out object built))
            {
                field.SetValue(null, built);
                return true;
            }

            TBLog.Warn($"AssignConvertedCitiesToTravelButton: field 'Cities' exists but type '{converted.GetType()}' is not assignable to '{field.FieldType}'.");
            return false;
        }

        var prop = travelButtonType.GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite)
        {
            if (prop.PropertyType.IsAssignableFrom(converted.GetType()))
            {
                prop.SetValue(null, converted, null);
                return true;
            }

            if (TryBuildAndAssignCollectionFromEnumerable(converted as IEnumerable, prop.PropertyType, out object built))
            {
                prop.SetValue(null, built, null);
                return true;
            }

            TBLog.Warn($"AssignConvertedCitiesToTravelButton: property 'Cities' exists but type '{converted.GetType()}' is not assignable to '{prop.PropertyType}'.");
            return false;
        }

        TBLog.Warn("AssignConvertedCitiesToTravelButton: no static field/property named 'Cities' found on TravelButton.");
        return false;
    }

    // Try to create a collection of targetCollectionType and copy elements from source enumerable.
    private static bool TryBuildAndAssignCollectionFromEnumerable(IEnumerable sourceEnum, Type targetCollectionType, out object built)
    {
        built = null;
        if (sourceEnum == null || targetCollectionType == null) return false;

        Type elementType = GetElementTypeFromCollectionType(targetCollectionType);
        if (elementType == null) return false;

        try
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            var listInstance = (IList)Activator.CreateInstance(listType);

            foreach (var item in sourceEnum)
            {
                if (item == null)
                {
                    listInstance.Add(null);
                    continue;
                }

                if (elementType.IsAssignableFrom(item.GetType()))
                {
                    listInstance.Add(item);
                }
                else
                {
                    if (item is CityEntry ce)
                    {
                        var dst = Activator.CreateInstance(elementType);
                        MapParsedCityToTarget(ce, dst);
                        listInstance.Add(dst);
                    }
                    else
                    {
                        try
                        {
                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(item);
                            var dst = Newtonsoft.Json.JsonConvert.DeserializeObject(json, elementType);
                            listInstance.Add(dst);
                        }
                        catch
                        {
                            TBLog.Warn($"TryBuildAndAssignCollectionFromEnumerable: unable to convert item of type {item.GetType()} to {elementType}");
                            return false;
                        }
                    }
                }
            }

            if (targetCollectionType.IsArray)
            {
                var toArrayMethod = listType.GetMethod("ToArray");
                var arr = toArrayMethod.Invoke(listInstance, null);
                built = arr;
            }
            else if (targetCollectionType.IsAssignableFrom(listInstance.GetType()))
            {
                built = listInstance;
            }
            else
            {
                try
                {
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(listInstance);
                    built = Newtonsoft.Json.JsonConvert.DeserializeObject(json, targetCollectionType);
                }
                catch
                {
                    TBLog.Warn($"TryBuildAndAssignCollectionFromEnumerable: cannot create target collection of type {targetCollectionType}");
                    return false;
                }
            }

            return built != null;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryBuildAndAssignCollectionFromEnumerable: exception: " + ex);
            return false;
        }
    }

    private static void TryAssignFallbackListOfCityEntry(object convertedDefaults)
    {
        try
        {
            if (convertedDefaults is List<CityEntry> asList)
            {
                var travelButtonType = typeof(TravelButton);
                var field = travelButtonType.GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType.IsAssignableFrom(typeof(List<CityEntry>)))
                {
                    field.SetValue(null, asList);
                    TBLog.Info("Assigned List<CityEntry> fallback to TravelButton.Cities.");
                    return;
                }
                var prop = travelButtonType.GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsAssignableFrom(typeof(List<CityEntry>)))
                {
                    prop.SetValue(null, asList, null);
                    TBLog.Info("Assigned List<CityEntry> fallback to TravelButton.Cities (prop).");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryAssignFallbackListOfCityEntry: " + ex);
        }
    }

    // -----------------------
    // Reflection mapping helpers
    // -----------------------

    private static Type GetElementTypeFromCollectionType(Type collectionType)
    {
        if (collectionType.IsArray) return collectionType.GetElementType();
        if (collectionType.IsGenericType)
        {
            var genArgs = collectionType.GetGenericArguments();
            if (genArgs != null && genArgs.Length == 1) return genArgs[0];
        }
        var iface = collectionType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (iface != null) return iface.GetGenericArguments()[0];
        return null;
    }

    public static void MapParsedCityToTarget(object src, object dst)
    {
        if (src == null || dst == null) return;
        try
        {
            SetStringOnTarget(dst, GetStringFromSource(src, "name") ?? GetStringFromSource(src, "Name"), "name");

            var price = GetIntFromSource(src, "price") ?? GetIntFromSource(src, "Price");
            SetIntOnTarget(dst, price ?? -1, "price");

            var coords = GetFloatArrayFromSource(src, "coords") ?? GetFloatArrayFromSource(src, "Coords");
            SetFloatArrayOnTarget(dst, coords, "coords");

            var targetName = GetStringFromSource(src, "targetGameObjectName") ?? GetStringFromSource(src, "target") ?? GetStringFromSource(src, "targetName");
            SetStringOnTarget(dst, targetName, "targetGameObjectName", "target", "targetName");

            var scene = GetStringFromSource(src, "sceneName") ?? GetStringFromSource(src, "scene");
            SetStringOnTarget(dst, scene, "sceneName", "scene");

            var desc = GetStringFromSource(src, "desc") ?? GetStringFromSource(src, "description") ?? GetStringFromSource(src, "descText");
            SetStringOnTarget(dst, desc, "desc", "description", "descText");

            var visited = GetBoolFromSource(src, "visited") ?? GetBoolFromSource(src, "Visited");
            SetBoolOnTarget(dst, visited ?? false, "visited", "Visited");

            var variants = GetStringArrayFromSource(src, "variants") ?? GetStringArrayFromSource(src, "Variants");
            SetStringArrayOnTarget(dst, variants, "variants", "Variants");

            var lastKnownVariant = GetStringFromSource(src, "lastKnownVariant") ?? GetStringFromSource(src, "LastKnownVariant");
            SetStringOnTarget(dst, lastKnownVariant, "lastKnownVariant", "LastKnownVariant");
        }
        catch (Exception ex)
        {
            TBLog.Warn("MapParsedCityToTarget: failed mapping city '" + GetStringFromSource(src, "name") + "': " + ex);
        }
    }

    public static void MapDefaultCityToTarget(object src, object dst)
    {
        if (src == null || dst == null) return;
        try
        {
            SetStringOnTarget(dst, GetStringFromSource(src, "name"), "name");
            SetIntOnTarget(dst, GetIntFromSource(src, "price") ?? -1, "price");
            SetFloatArrayOnTarget(dst, GetFloatArrayFromSource(src, "coords"), "coords");
            SetStringOnTarget(dst, GetStringFromSource(src, "targetGameObjectName") ?? GetStringFromSource(src, "target"), "targetGameObjectName", "target", "targetName");
            SetStringOnTarget(dst, GetStringFromSource(src, "sceneName") ?? GetStringFromSource(src, "scene"), "sceneName", "scene");
            SetStringOnTarget(dst, GetStringFromSource(src, "desc") ?? GetStringFromSource(src, "description"), "desc", "description");
            SetBoolOnTarget(dst, GetBoolFromSource(src, "visited") ?? false, "visited", "Visited");
            SetStringArrayOnTarget(dst, GetStringArrayFromSource(src, "variants"), "variants", "Variants");
            SetStringOnTarget(dst, GetStringFromSource(src, "lastKnownVariant"), "lastKnownVariant", "LastKnownVariant");
        }
        catch (Exception ex)
        {
            TBLog.Warn("MapDefaultCityToTarget: failed mapping default city: " + ex);
        }
    }

    private static void SetStringOnTarget(object target, string value, params string[] candidateNames)
    {
        if (target == null) return;
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
            catch { }
        }
    }

    private static void SetIntOnTarget(object target, int value, params string[] candidateNames)
    {
        if (target == null) return;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null && p.CanWrite && (p.PropertyType == typeof(int) || p.PropertyType == typeof(int?)))
                {
                    p.SetValue(target, value);
                    return;
                }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null && (f.FieldType == typeof(int) || f.FieldType == typeof(int?)))
                {
                    f.SetValue(target, value);
                    return;
                }
            }
            catch { }
        }
    }

    private static void SetBoolOnTarget(object target, bool value, params string[] candidateNames)
    {
        if (target == null) return;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null && p.CanWrite && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?)))
                {
                    p.SetValue(target, value);
                    return;
                }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null && (f.FieldType == typeof(bool) || f.FieldType == typeof(bool?)))
                {
                    f.SetValue(target, value);
                    return;
                }
            }
            catch { }
        }
    }

    public static string GetStringFromSource(object src, string propName)
    {
        if (src == null) return null;
        var t = src.GetType();
        var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (p != null && p.CanRead && p.PropertyType == typeof(string)) return p.GetValue(src) as string;
        var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (f != null && f.FieldType == typeof(string)) return f.GetValue(src) as string;
        return null;
    }

    private static int? GetIntFromSource(object src, string propName)
    {
        if (src == null) return null;
        var t = src.GetType();
        var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (p != null && p.CanRead)
        {
            var v = p.GetValue(src);
            if (v is int i) return i;
            try { return Convert.ToInt32(v); } catch { }
        }
        var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (f != null)
        {
            var v = f.GetValue(src);
            if (v is int i2) return i2;
            try { return Convert.ToInt32(v); } catch { }
        }
        return null;
    }

    private static bool? GetBoolFromSource(object src, string propName)
    {
        if (src == null) return null;
        var t = src.GetType();
        var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (p != null && p.CanRead)
        {
            var v = p.GetValue(src);
            if (v is bool b) return b;
            if (v != null) { if (bool.TryParse(v.ToString(), out var parsed)) return parsed; }
        }
        var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (f != null)
        {
            var v = f.GetValue(src);
            if (v is bool b2) return b2;
            if (v != null) { if (bool.TryParse(v.ToString(), out var parsed2)) return parsed2; }
        }
        return null;
    }

    private static float[] GetFloatArrayFromSource(object src, string propName)
    {
        if (src == null) return null;
        var t = src.GetType();
        var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        object val = null;
        if (p != null && p.CanRead) val = p.GetValue(src);
        else
        {
            var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null) val = f.GetValue(src);
        }
        if (val == null) return null;
        if (val is float[] fa) return fa;
        if (val is IEnumerable e)
        {
            var list = new List<float>();
            foreach (var it in e)
            {
                try { list.Add(Convert.ToSingle(it)); } catch { }
            }
            if (list.Count > 0) return list.ToArray();
        }
        return null;
    }

    private static string[] GetStringArrayFromSource(object src, string propName)
    {
        if (src == null) return null;
        var t = src.GetType();
        var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        object val = null;
        if (p != null && p.CanRead) val = p.GetValue(src);
        else
        {
            var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null) val = f.GetValue(src);
        }
        if (val == null) return null;
        if (val is string[] sa) return sa;
        if (val is IEnumerable e)
        {
            var list = new List<string>();
            foreach (var it in e) if (it != null) list.Add(it.ToString());
            if (list.Count > 0) return list.ToArray();
        }
        return null;
    }

    private static void SetStringArrayOnTarget(object target, string[] arr, params string[] candidateNames)
    {
        if (target == null || candidateNames == null || candidateNames.Length == 0) return;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null && p.CanWrite && (p.PropertyType == typeof(string[]) || p.PropertyType.IsAssignableFrom(typeof(IEnumerable<string>))))
                {
                    p.SetValue(target, arr);
                    return;
                }
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null && (f.FieldType == typeof(string[]) || f.FieldType.IsAssignableFrom(typeof(IEnumerable<string>))))
                {
                    f.SetValue(target, arr);
                    return;
                }
            }
            catch { }
        }
    }

    private static void SetFloatArrayOnTarget(object target, float[] arr, params string[] candidateNames)
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
            catch { }
        }
    }

    private static void SetStringMember(object target, string value, params string[] candidateNames)
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
            catch { }
        }
    }

    // -------------------
    // Utility / filesystem
    // -------------------

    // Build fallback candidate paths for TravelButton_Cities.json
    private static List<string> BuildCandidatePaths()
    {
        var candidates = new List<string>();
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
                        candidates.Add(Path.Combine(cfg, TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json"));
                    }
                }
            }
        }
        catch { }

        try { candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BepInEx", "config", "TravelButton_Cities.json"))); } catch { }
        try
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            if (!string.IsNullOrEmpty(asmDir)) candidates.Add(Path.Combine(asmDir, "TravelButton_Cities.json"));
        }
        catch { }
        try { candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "TravelButton_Cities.json")); } catch { }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Try to obtain canonical JSON path using TravelButtonPlugin helper first (preferred).
    private static string TryGetCanonicalCitiesJsonPath()
    {
        try
        {
            var mi = typeof(TravelButtonPlugin).GetMethod("GetCitiesJsonPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null)
            {
                var res = mi.Invoke(null, null) as string;
                if (!string.IsNullOrEmpty(res)) return res;
            }
        }
        catch (Exception ex) { TBLog.Warn("TryGetCanonicalCitiesJsonPath: TravelButton.GetCitiesJsonPath() reflection failed: " + ex.Message); }

        // BepInEx.Paths.ConfigPath
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
                        var fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";
                        return Path.Combine(cfg, fileName);
                    }
                }
            }
        }
        catch { }

        // Plugin folder
        try
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                var fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";
                return Path.Combine(asmDir, fileName);
            }
        }
        catch { }

        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrEmpty(cwd))
            {
                var fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";
                return Path.Combine(cwd, fileName);
            }
        }
        catch { }

        return null;
    }

    // Return runtime TravelButton.Cities count for logging; uses reflection
    private static int GetRuntimeCitiesCount()
    {
        try
        {
            var travelButtonType = typeof(TravelButton);
            var field = travelButtonType.GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object val = null;
            if (field != null) val = field.GetValue(null);
            else
            {
                var prop = travelButtonType.GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) val = prop.GetValue(null, null);
            }

            if (val is ICollection coll) return coll.Count;
            if (val is IEnumerable e)
            {
                int cnt = 0;
                foreach (var _ in e) cnt++;
                return cnt;
            }
        }
        catch { }
        return -1;
    }

    // Public wrapper that callers should use
    public static bool TryResolveCityDataFromObject(object cityObj,
                                                    out string sceneName,
                                                    out string targetName,
                                                    out Vector3 coords,
                                                    out bool haveCoords,
                                                    out int price)
    {
        // If the real implementation is private/internal named TryResolveCityDataFromObject_Internal
        // replace the call below with the real internal method name.
        return TryResolveCityDataFromObject_Internal(cityObj, out sceneName, out targetName, out coords, out haveCoords, out price);
    }

    private static bool TryResolveCityDataFromObject_Internal(object cityObj, out string outSceneName, out string outTargetName, out Vector3 outCoords, out bool outHaveCoords, out int outPrice)
    {
        outSceneName = null;
        outTargetName = null;
        outCoords = Vector3.zero;
        outHaveCoords = false;
        outPrice = 0;

        if (cityObj == null) return false;

        // 1) Fast-path: if it's already a CityEntry
        if (cityObj is CityEntry ce)
        {
            outSceneName = ce.sceneName;
            outTargetName = ce.targetGameObjectName;
            if (ce.coords != null && ce.coords.Length >= 3)
            {
                outCoords = new Vector3(ce.coords[0], ce.coords[1], ce.coords[2]);
                outHaveCoords = true;
            }
            outPrice = ce.price;
            return !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
        }

        // 2) If it's a string, try lookup by name/scene
        if (cityObj is string s)
        {
            if (TryFindCityByNameOrScene(s, out CityEntry foundS))
            {
                outSceneName = foundS.sceneName;
                outTargetName = foundS.targetGameObjectName;
                if (foundS.coords != null && foundS.coords.Length >= 3)
                {
                    outCoords = new Vector3(foundS.coords[0], foundS.coords[1], foundS.coords[2]);
                    outHaveCoords = true;
                }
                outPrice = foundS.price;
                return !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
            }
            outSceneName = s; // interpret as direct scene name
            return true;
        }

        // 3) Try to match by ToString() or 'name' property against loadedCities
        try
        {
            string candidateName = null;
            var nameProp = cityObj.GetType().GetProperty("name", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                         ?? cityObj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp != null) candidateName = nameProp.GetValue(cityObj) as string;
            if (string.IsNullOrEmpty(candidateName))
            {
                try { candidateName = cityObj.ToString(); } catch { candidateName = null; }
            }

            if (!string.IsNullOrEmpty(candidateName))
            {
                if (TryFindCityByNameOrScene(candidateName, out CityEntry found2))
                {
                    outSceneName = found2.sceneName;
                    outTargetName = found2.targetGameObjectName;
                    if (found2.coords != null && found2.coords.Length >= 3) { outCoords = new Vector3(found2.coords[0], found2.coords[1], found2.coords[2]); outHaveCoords = true; }
                    outPrice = found2.price;
                    return !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
                }
            }
        }
        catch { /* ignore */ }

        // 4) Enhanced reflection: inspect properties and fields (public + non-public) for likely candidates.
        try
        {
            var t = cityObj.GetType();

            // helper lists of candidate member name patterns
            string[] sceneKeys = new[] { "scenename", "scene", "scene_name", "sceneName" };
            string[] targetKeys = new[] { "targetgameobjectname", "target", "targetname", "target_gameobject_name", "targetGameObjectName" };
            string[] nameKeys = new[] { "name", "displayname", "title" };
            string[] priceKeys = new[] { "price", "cost", "fee" };
            string[] coordsKeys = new[] { "coords", "position", "pos", "location", "transform", "vector", "coordinate" };

            // scan properties first
            foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    object val = null;
                    try { val = prop.GetValue(cityObj); } catch { continue; }
                    if (val == null) continue;

                    string propName = prop.Name.ToLowerInvariant();

                    // strings
                    if (val is string sval)
                    {
                        if (Array.Exists(sceneKeys, k => propName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval;
                        }
                        else if (Array.Exists(targetKeys, k => propName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outTargetName)) outTargetName = sval;
                        }
                        else if (Array.Exists(nameKeys, k => propName.Contains(k)))
                        {
                            // try lookup by this name
                            if (TryFindCityByNameOrScene(sval, out CityEntry f))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f.targetGameObjectName;
                                if (!outHaveCoords && f.coords != null && f.coords.Length >= 3) { outCoords = new Vector3(f.coords[0], f.coords[1], f.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f.price;
                            }
                            else
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval; // fallback: treat as scene
                            }
                        }
                        else
                        {
                            // also check whether the string value itself matches a known scene/name
                            if (TryFindCityByNameOrScene(sval, out CityEntry f2))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f2.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f2.targetGameObjectName;
                                if (!outHaveCoords && f2.coords != null && f2.coords.Length >= 3) { outCoords = new Vector3(f2.coords[0], f2.coords[1], f2.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f2.price;
                            }
                        }
                    }
                    // numeric arrays or lists -> coords
                    else if (val is float[] farr && farr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3(farr[0], farr[1], farr[2]); outHaveCoords = true; }
                    }
                    else if (val is double[] darr && darr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3((float)darr[0], (float)darr[1], (float)darr[2]); outHaveCoords = true; }
                    }
                    else if (val is System.Collections.IList list && list.Count >= 3)
                    {
                        // try to convert first three items to floats
                        try
                        {
                            float a = Convert.ToSingle(list[0]);
                            float b = Convert.ToSingle(list[1]);
                            float c = Convert.ToSingle(list[2]);
                            if (!outHaveCoords) { outCoords = new Vector3(a, b, c); outHaveCoords = true; }
                        }
                        catch { }
                    }
                    else
                    {
                        // If it's a UnityEngine.GameObject or Component, try to use its name
                        var asGameObject = val as GameObject;
                        if (asGameObject != null && string.IsNullOrEmpty(outTargetName))
                            outTargetName = asGameObject.name;
                        else
                        {
                            var asComponent = val as Component;
                            if (asComponent != null && string.IsNullOrEmpty(outTargetName))
                                outTargetName = asComponent.gameObject?.name;
                        }
                    }
                }
                catch { /* ignore one property failure */ }
            }

            // scan fields as well (public + non-public)
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    object val = null;
                    try { val = field.GetValue(cityObj); } catch { continue; }
                    if (val == null) continue;

                    string fieldName = field.Name.ToLowerInvariant();

                    if (val is string sval)
                    {
                        if (Array.Exists(sceneKeys, k => fieldName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval;
                        }
                        else if (Array.Exists(targetKeys, k => fieldName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outTargetName)) outTargetName = sval;
                        }
                        else if (Array.Exists(nameKeys, k => fieldName.Contains(k)))
                        {
                            if (TryFindCityByNameOrScene(sval, out CityEntry f))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f.targetGameObjectName;
                                if (!outHaveCoords && f.coords != null && f.coords.Length >= 3) { outCoords = new Vector3(f.coords[0], f.coords[1], f.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f.price;
                            }
                            else
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval;
                            }
                        }
                        else
                        {
                            if (TryFindCityByNameOrScene(sval, out CityEntry f2))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f2.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f2.targetGameObjectName;
                                if (!outHaveCoords && f2.coords != null && f2.coords.Length >= 3) { outCoords = new Vector3(f2.coords[0], f2.coords[1], f2.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f2.price;
                            }
                        }
                    }
                    else if (val is float[] farr && farr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3(farr[0], farr[1], farr[2]); outHaveCoords = true; }
                    }
                    else if (val is double[] darr && darr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3((float)darr[0], (float)darr[1], (float)darr[2]); outHaveCoords = true; }
                    }
                    else if (val is System.Collections.IList list && list.Count >= 3)
                    {
                        try
                        {
                            float a = Convert.ToSingle(list[0]);
                            float b = Convert.ToSingle(list[1]);
                            float c = Convert.ToSingle(list[2]);
                            if (!outHaveCoords) { outCoords = new Vector3(a, b, c); outHaveCoords = true; }
                        }
                        catch { }
                    }
                    else
                    {
                        var asGameObject = val as GameObject;
                        if (asGameObject != null && string.IsNullOrEmpty(outTargetName))
                            outTargetName = asGameObject.name;
                        else
                        {
                            var asComponent = val as Component;
                            if (asComponent != null && string.IsNullOrEmpty(outTargetName))
                                outTargetName = asComponent.gameObject?.name;
                        }
                    }
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryResolveCityDataFromObject: reflection fallback failed: " + ex);
        }

        // Last attempt: if we have loadedCities, try to pick one by matching any discovered strings
        try
        {
            if (!string.IsNullOrEmpty(outSceneName))
            {
                if (TryFindCityByNameOrScene(outSceneName, out CityEntry f3))
                {
                    // harmonize names
                    if (string.IsNullOrEmpty(outTargetName)) outTargetName = f3.targetGameObjectName;
                    if (!outHaveCoords && f3.coords != null && f3.coords.Length >= 3) { outCoords = new Vector3(f3.coords[0], f3.coords[1], f3.coords[2]); outHaveCoords = true; }
                    if (outPrice == 0) outPrice = f3.price;
                }
            }
        }
        catch { }

        // Return true if we discovered either sceneName or coords
        bool ok = !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
        if (!ok)
        {
            // Diagnostic: log members (names only) to help debugging
            try
            {
                var t2 = cityObj.GetType();
                var membs = new List<string>();
                foreach (var p in t2.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) membs.Add("P:" + p.Name);
                foreach (var f in t2.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) membs.Add("F:" + f.Name);
                TBLog.Warn($"TryResolveCityDataFromObject: failed to resolve data for object type={t2.FullName}. Members: {string.Join(", ", membs)}");
            }
            catch { }
        }
        return ok;
    }

}
