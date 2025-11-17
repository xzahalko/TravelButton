using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using System;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

//
// TravelButtonMod.cs
// - BepInEx plugin bootstrap (TravelButtonPlugin) + runtime static helpers (TravelButtonMod).
// - Integrates with an optional external ConfigManager (safely, via reflection) and with BepInEx config system
//   so Configuration Manager displays editable settings.
// - Provides City model used by TravelButtonUI and helpers to map/persist configuration.
// - Adds diagnostics helpers DumpTravelButtonState and ForceShowTravelButton for runtime inspection.
//
[BepInPlugin("com.xzahalko.travelbutton", "TravelButton", "1.0.0")]
public class TravelButtonPlugin : BaseUnityPlugin
{

    // BepInEx config entries (top-level)
    private ConfigEntry<bool> bex_enableMod;
    private ConfigEntry<int> bex_globalPrice;
    private ConfigEntry<string> bex_currencyItem;

    // per-city config entries
    private Dictionary<string, ConfigEntry<bool>> bex_cityEnabled = new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ConfigEntry<int>> bex_cityPrice = new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase);

    // Optional prefix to make entries easy to find in BepInEx logs
    // Set by the plugin during Awake: e.g. TravelButtonPlugin.Initialize(this.Logger);
    public static ManualLogSource LogSource { get; private set; }
    private const string Prefix = "[TravelButton] ";

    public static void Initialize(ManualLogSource manualLogSource)
    {
        if (manualLogSource == null) throw new ArgumentNullException(nameof(manualLogSource));
        LogSource = manualLogSource;
        try { LogSource.LogInfo(Prefix + "TravelButtonPlugin initialized with BepInEx ManualLogSource."); } catch { /* swallow */ }
    }

    /// <summary>
    /// Best-effort: locate TravelButton_Cities.json in common locations and parse it using UnityEngine.JsonUtility.
    /// Supports two common shapes:
    ///  - { "cities": { "TownA": { ... }, "TownB": { ... } } }  (dictionary form)
    ///  - { "cities": [ { "name":"TownA", ... }, { "name":"TownB", ... } ] }  (array form)
    /// If the file uses the dictionary form this method transforms it into an array of objects with "name" set.
    /// The method is defensive and will not throw on parse errors; it logs and returns.
    /// </summary>
    /// <summary>
    /// Best-effort: locate TravelButton_Cities.json in common locations and parse it using UnityEngine.JsonUtility.
    /// Supports two common shapes:
    ///  - { "cities": { "TownA": { ... }, "TownB": { ... } } }  (dictionary form)
    ///  - { "cities": [ { "name":"TownA", ... }, { "name":"TownB", ... } ] }  (array form)
    /// If the file uses the dictionary form this method transforms it into an array of objects with "name" set.
    /// The method is defensive and will not throw on parse errors; it logs and returns.
    /// </summary>
    private static void TryLoadCitiesJsonIntoTravelButtonMod()
    {
        try
        {
            var logger = LogSource;
            void LInfo(string m) { try { logger?.LogInfo(Prefix + m); } catch { } }
            void LWarn(string m) { try { logger?.LogWarning(Prefix + m); } catch { } }

            var candidatePaths = new List<string>();

            // Common BepInEx config location
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                candidatePaths.Add(Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities.json"));
                candidatePaths.Add(Path.Combine(baseDir, "config", "TravelButton_Cities.json"));
            }
            catch { }

            // Assembly location (same folder as plugin)
            try
            {
                var asmLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmLocation))
                    candidatePaths.Add(Path.Combine(asmLocation, "TravelButton_Cities.json"));
            }
            catch { }

            // Current working directory
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                candidatePaths.Add(Path.Combine(cwd, "TravelButton_Cities.json"));
            }
            catch { }

            // Unity data path (best-effort; may not be available at domain-load time)
            try
            {
                var dataPath = Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                    candidatePaths.Add(Path.Combine(dataPath, "TravelButton_Cities.json"));
            }
            catch { }

            // Also consider the BepInEx config path returned by our helper (if available)
            try
            {
                var cfgPath = TravelButtonMod.ConfigFilePath;
                if (!string.IsNullOrEmpty(cfgPath) && cfgPath != "(unknown)")
                {
                    var dir = cfgPath;
                    try
                    {
                        if (File.Exists(cfgPath)) dir = Path.GetDirectoryName(cfgPath);
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(dir))
                        candidatePaths.Add(Path.Combine(dir, "TravelButton_Cities.json"));
                }
            }
            catch { }

            // De-duplicate and test existence
            var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string foundPath = null;
            foreach (var p in candidatePaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string full;
                try { full = Path.GetFullPath(p); } catch { full = p; }
                if (tested.Contains(full)) continue;
                tested.Add(full);
                try
                {
                    if (File.Exists(full))
                    {
                        foundPath = full;
                        break;
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(foundPath))
            {
                LInfo("No TravelButton_Cities.json found in candidate locations.");
                return;
            }

            LInfo("Found TravelButton_Cities.json at: " + foundPath);
            string json = null;
            try
            {
                json = File.ReadAllText(foundPath);
            }
            catch (Exception ex)
            {
                LWarn("Could not read TravelButton_Cities.json: " + ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                LWarn("TravelButton_Cities.json is empty.");
                return;
            }

            // Determine how "cities" is represented: object (dictionary) or array.
            try
            {
                int idx = IndexOfJsonProperty(json, "cities");
                if (idx < 0)
                {
                    LWarn("TravelButton_Cities.json does not contain a top-level 'cities' property.");
                    return;
                }

                // Find the colon after the property name
                int colon = json.IndexOf(':', idx);
                if (colon < 0)
                {
                    LWarn("Malformed TravelButton_Cities.json: cannot find ':' after 'cities'.");
                    return;
                }

                // Find first non-whitespace char after colon
                int i = colon + 1;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length)
                {
                    LWarn("Malformed TravelButton_Cities.json: unexpected end after 'cities:'.");
                    return;
                }

                // If cities is an array, we could try the previous JsonUtility path; but since JsonUtility was unreliable for your file,
                // we'll parse the dictionary/array into TravelButtonMod.City instances using a simple targeted parser that doesn't require Newtonsoft.
                var citiesList = new List<TravelButtonMod.City>();

                if (json[i] == '{')
                {
                    // dictionary/object form: parse each "CityName": { ... } entry out of the cities object
                    int objStart = i;
                    int objEnd = FindMatchingClosingBrace(json, objStart);
                    if (objEnd < 0)
                    {
                        LWarn("Malformed TravelButton_Cities.json: cannot find matching '}' for cities object.");
                        return;
                    }

                    string citiesObjText = json.Substring(objStart + 1, objEnd - objStart - 1); // inner content

                    int pos = 0;
                    while (pos < citiesObjText.Length)
                    {
                        // skip whitespace and commas
                        while (pos < citiesObjText.Length && (char.IsWhiteSpace(citiesObjText[pos]) || citiesObjText[pos] == ',')) pos++;
                        if (pos >= citiesObjText.Length) break;

                        // expect quoted property name
                        if (citiesObjText[pos] != '\"')
                        {
                            // skip to next quote if formatting differs
                            int nextQuote = citiesObjText.IndexOf('\"', pos);
                            if (nextQuote < 0) break;
                            pos = nextQuote;
                            if (citiesObjText[pos] != '\"') break;
                        }

                        int nameStart = pos;
                        int nameEnd = FindClosingQuote(citiesObjText, nameStart);
                        if (nameEnd < 0) break;
                        string cityName = citiesObjText.Substring(nameStart + 1, nameEnd - nameStart - 1);
                        pos = nameEnd + 1;

                        // skip whitespace and colon
                        while (pos < citiesObjText.Length && char.IsWhiteSpace(citiesObjText[pos])) pos++;
                        if (pos < citiesObjText.Length && citiesObjText[pos] == ':') pos++;
                        while (pos < citiesObjText.Length && char.IsWhiteSpace(citiesObjText[pos])) pos++;

                        // next token should be an object {...}
                        if (pos >= citiesObjText.Length || citiesObjText[pos] != '{') break;
                        int valueStart = pos;
                        int valueEnd = FindMatchingClosingBrace(citiesObjText, valueStart);
                        if (valueEnd < 0) break;
                        string innerObject = citiesObjText.Substring(valueStart + 1, valueEnd - valueStart - 1).Trim();

                        // Parse innerObject with targeted extraction (no Json library)
                        try
                        {
                            var city = ParseCityInnerJson(cityName, innerObject);
                            if (city != null) citiesList.Add(city);
                        }
                        catch (Exception pe)
                        {
                            LWarn($"Error parsing city '{cityName}': {pe.Message}");
                        }

                        pos = valueEnd + 1;
                    }
                }
                else if (json[i] == '[')
                {
                    // array form: find array end and iterate items that must include a "name" property
                    int arrStart = i;
                    int arrEnd = FindMatchingClosingBracket(json, arrStart);
                    if (arrEnd < 0)
                    {
                        LWarn("Malformed TravelButton_Cities.json: cannot find matching ']' for cities array.");
                        return;
                    }
                    string arrText = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                    int pos = 0;
                    while (pos < arrText.Length)
                    {
                        // skip whitespace and commas
                        while (pos < arrText.Length && (char.IsWhiteSpace(arrText[pos]) || arrText[pos] == ',')) pos++;
                        if (pos >= arrText.Length) break;
                        if (arrText[pos] != '{') break;
                        int itemStart = pos;
                        int itemEnd = FindMatchingClosingBrace(arrText, itemStart);
                        if (itemEnd < 0) break;
                        string itemInner = arrText.Substring(itemStart + 1, itemEnd - itemStart - 1).Trim();

                        // attempt to read name property inside itemInner
                        int nameIdx = IndexOfJsonProperty(itemInner, "name");
                        string nameVal = null;
                        if (nameIdx >= 0)
                        {
                            int colonIdx = itemInner.IndexOf(':', nameIdx);
                            if (colonIdx >= 0)
                            {
                                int j = colonIdx + 1;
                                while (j < itemInner.Length && char.IsWhiteSpace(itemInner[j])) j++;
                                if (j < itemInner.Length && itemInner[j] == '"')
                                {
                                    int qend = FindClosingQuote(itemInner, j);
                                    if (qend >= 0) nameVal = itemInner.Substring(j + 1, qend - j - 1);
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(nameVal))
                        {
                            try
                            {
                                var city = ParseCityInnerJson(nameVal, itemInner);
                                if (city != null) citiesList.Add(city);
                            }
                            catch (Exception pe)
                            {
                                LWarn($"Error parsing city '{nameVal}' from array: {pe.Message}");
                            }
                        }

                        pos = itemEnd + 1;
                    }
                }
                else
                {
                    LWarn("Unsupported JSON token after 'cities': expected '[' or '{'.");
                    return;
                }

                if (citiesList.Count > 0)
                {
                    TravelButtonMod.Cities = citiesList;
                    LInfo($"Loaded {TravelButtonMod.Cities.Count} cities from TravelButton_Cities.json (metadata only).");
                    
                    // Check if the JSON already contains visited flags
                    bool jsonHadVisited = citiesList.Any(c => c.visited);
                    
                    if (jsonHadVisited)
                    {
                        LInfo("TravelButton_Cities.json already contains visited flags; skipping legacy .cfg migration.");
                    }
                    else
                    {
                        // No visited flags in JSON - check if legacy .cfg exists and migrate if present
                        var cfgPath = TravelButtonMod.ConfigFilePath;
                        if (!string.IsNullOrEmpty(cfgPath) && cfgPath != "(unknown)" && File.Exists(cfgPath))
                        {
                            try
                            {
                                ApplyVisitedFlagsFromCfg();
                                LInfo($"Migrated visited flags from .cfg ({cfgPath}) into TravelButton_Cities.json.");
                                
                                try
                                {
                                    PersistCitiesToConfigUsingUnity();
                                    LInfo("Persisted migrated TravelButton_Cities.json to disk.");
                                }
                                catch (Exception ex)
                                {
                                    LWarn("Persist: " + ex.Message);
                                }
                            }
                            catch (Exception ex)
                            {
                                LWarn("ApplyVisitedFlagsFromCfg failed during migration: " + ex.Message);
                            }
                        }
                        else
                        {
                            LInfo("No legacy .cfg present to migrate visited flags from.");
                        }
                    }
                }
                else
                {
                    LWarn("No cities were parsed from TravelButton_Cities.json (after manual parse).");
                }
            }
            catch (Exception ex)
            {
                LWarn("Error while processing TravelButton_Cities.json: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            try { LogSource?.LogWarning(Prefix + "TryLoadCitiesJsonIntoTravelButtonMod unexpected failure: " + ex.Message); } catch { }
        }
    }

    /// <summary>
    /// Apply visited flags from the legacy BepInEx .cfg file into the in-memory TravelButtonMod.Cities.
    /// This reads from the BepInEx config file (if it exists) and marks cities as visited in VisitedTracker.
    /// The BepInEx config file typically stores per-city config entries including visited state.
    /// </summary>
    private static void ApplyVisitedFlagsFromCfg()
    {
        try
        {
            // The BepInEx config is managed by the plugin instance, not directly accessible here.
            // For a one-time migration, we look for the BepInEx .cfg file on disk and parse it manually.
            // BepInEx config files are typically at: <GameDir>/BepInEx/config/com.xzahalko.travelbutton.cfg
            
            var logger = LogSource;
            void LInfo(string m) { try { logger?.LogInfo(Prefix + m); } catch { } }
            void LWarn(string m) { try { logger?.LogWarning(Prefix + m); } catch { } }
            
            // Try to locate the BepInEx .cfg file
            var candidatePaths = new List<string>();
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                candidatePaths.Add(Path.Combine(baseDir, "BepInEx", "config", "com.xzahalko.travelbutton.cfg"));
                candidatePaths.Add(Path.Combine(baseDir, "BepInEx", "config", "cz.valheimskal.travelbutton.cfg"));
            }
            catch { }
            
            string cfgPath = null;
            foreach (var p in candidatePaths)
            {
                try
                {
                    if (File.Exists(p))
                    {
                        cfgPath = p;
                        break;
                    }
                }
                catch { }
            }
            
            if (string.IsNullOrEmpty(cfgPath))
            {
                LInfo("ApplyVisitedFlagsFromCfg: No BepInEx .cfg file found.");
                return;
            }
            
            LInfo($"ApplyVisitedFlagsFromCfg: Reading legacy .cfg from {cfgPath}");
            
            // Parse the .cfg file manually (BepInEx .cfg format is simple INI-like)
            // Look for entries like: [TravelButton.Cities]
            //                         CityName.Visited = true
            var lines = File.ReadAllLines(cfgPath);
            bool inCitiesSection = false;
            int migratedCount = 0;
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                // Check for section headers
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    var section = trimmed.Substring(1, trimmed.Length - 2);
                    inCitiesSection = section.Equals("TravelButton.Cities", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                
                // If we're in the cities section, look for Visited entries
                if (inCitiesSection && trimmed.Contains("="))
                {
                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();
                        
                        // Check if this is a Visited entry (e.g., "Berg.Visited = True")
                        if (key.EndsWith(".Visited", StringComparison.OrdinalIgnoreCase))
                        {
                            var cityName = key.Substring(0, key.Length - ".Visited".Length);
                            if (value.Equals("True", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase))
                            {
                                // Find the city and mark it visited
                                var city = TravelButtonMod.Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
                                if (city != null)
                                {
                                    city.visited = true;
                                    migratedCount++;
                                    LInfo($"ApplyVisitedFlagsFromCfg: Migrated visited flag for '{cityName}'");
                                }
                            }
                        }
                    }
                }
            }
            
            LInfo($"ApplyVisitedFlagsFromCfg: Migrated {migratedCount} visited flags from legacy .cfg");
        }
        catch (Exception ex)
        {
            try { LogSource?.LogWarning(Prefix + "ApplyVisitedFlagsFromCfg exception: " + ex.Message); } catch { }
        }
    }

    /// <summary>
    /// Persist the current TravelButtonMod.Cities (with visited flags) to TravelButton_Cities.json using Unity's JsonUtility.
    /// This writes the cities back to the same location where TravelButton_Cities.json was loaded from.
    /// </summary>
    private static void PersistCitiesToConfigUsingUnity()
    {
        try
        {
            var logger = LogSource;
            void LInfo(string m) { try { logger?.LogInfo(Prefix + m); } catch { } }
            void LWarn(string m) { try { logger?.LogWarning(Prefix + m); } catch { } }
            
            if (TravelButtonMod.Cities == null || TravelButtonMod.Cities.Count == 0)
            {
                LWarn("PersistCitiesToConfigUsingUnity: No cities to persist.");
                return;
            }
            
            // Determine the output path for TravelButton_Cities.json
            // We'll use the same logic as TryLoadCitiesJsonIntoTravelButtonMod to find the file
            var candidatePaths = new List<string>();
            
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                candidatePaths.Add(Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities.json"));
                candidatePaths.Add(Path.Combine(baseDir, "config", "TravelButton_Cities.json"));
            }
            catch { }
            
            try
            {
                var asmLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmLocation))
                    candidatePaths.Add(Path.Combine(asmLocation, "TravelButton_Cities.json"));
            }
            catch { }
            
            try
            {
                var cfgPath = TravelButtonMod.ConfigFilePath;
                if (!string.IsNullOrEmpty(cfgPath) && cfgPath != "(unknown)")
                {
                    var dir = cfgPath;
                    try
                    {
                        if (File.Exists(cfgPath)) dir = Path.GetDirectoryName(cfgPath);
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(dir))
                        candidatePaths.Add(Path.Combine(dir, "TravelButton_Cities.json"));
                }
            }
            catch { }
            
            // Find existing file or use first candidate
            string outputPath = null;
            var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in candidatePaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string full;
                try { full = Path.GetFullPath(p); } catch { full = p; }
                if (tested.Contains(full)) continue;
                tested.Add(full);
                
                if (File.Exists(full))
                {
                    outputPath = full;
                    break;
                }
            }
            
            // If no existing file found, use the first candidate path
            if (string.IsNullOrEmpty(outputPath) && candidatePaths.Count > 0)
            {
                try { outputPath = Path.GetFullPath(candidatePaths[0]); } catch { outputPath = candidatePaths[0]; }
            }
            
            if (string.IsNullOrEmpty(outputPath))
            {
                LWarn("PersistCitiesToConfigUsingUnity: Could not determine output path.");
                return;
            }
            
            // Build the JSON structure manually (dictionary form to match the existing format)
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"enabled\": true,");
            sb.AppendLine("  \"currencyItem\": \"Silver\",");
            sb.AppendLine("  \"globalTeleportPrice\": 100,");
            sb.AppendLine("  \"cities\": {");
            
            bool first = true;
            foreach (var city in TravelButtonMod.Cities)
            {
                if (!first) sb.AppendLine(",");
                first = false;
                
                sb.AppendLine($"    \"{city.name}\": {{");
                sb.AppendLine($"      \"enabled\": {(city.enabled ? "true" : "false")},");
                sb.AppendLine($"      \"price\": {city.price?.ToString() ?? "null"},");
                
                if (city.coords != null && city.coords.Length >= 3)
                {
                    sb.AppendLine($"      \"coords\": [{city.coords[0]}, {city.coords[1]}, {city.coords[2]}],");
                }
                else
                {
                    sb.AppendLine("      \"coords\": null,");
                }
                
                sb.AppendLine($"      \"targetGameObjectName\": {(string.IsNullOrEmpty(city.targetGameObjectName) ? "null" : $"\"{city.targetGameObjectName}\"")},");
                sb.AppendLine($"      \"sceneName\": {(string.IsNullOrEmpty(city.sceneName) ? "null" : $"\"{city.sceneName}\"")},");
                sb.AppendLine("      \"desc\": null,");
                sb.AppendLine($"      \"visited\": {(city.visited ? "true" : "false")}");
                sb.Append("    }");
            }
            
            sb.AppendLine();
            sb.AppendLine("  }");
            sb.AppendLine("}");
            
            // Ensure directory exists
            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                LWarn($"PersistCitiesToConfigUsingUnity: Could not create directory: {ex.Message}");
            }
            
            // Write the JSON file
            File.WriteAllText(outputPath, sb.ToString());
            LInfo($"PersistCitiesToConfigUsingUnity: Persisted {TravelButtonMod.Cities.Count} cities to {outputPath}");
        }
        catch (Exception ex)
        {
            try { LogSource?.LogWarning(Prefix + "PersistCitiesToConfigUsingUnity exception: " + ex.Message); } catch { }
        }
    }

    // Parse the inner JSON object text for a single city (contents between { ... } without the outer braces)
    // Returns both the City object and the visited flag from JSON (if present)
    private static TravelButtonMod.City ParseCityInnerJson(string cityName, string innerObject)
    {
        if (string.IsNullOrEmpty(cityName)) return null;
        var city = new TravelButtonMod.City(cityName);

        // enabled (bool)
        var en = ExtractJsonBool(innerObject, "enabled");
        if (en.HasValue) city.enabled = en.Value;

        // price (int?)
        var price = ExtractJsonIntNullable(innerObject, "price");
        city.price = price;

        // coords (float[] of at least 3)
        var coords = ExtractJsonFloatArray(innerObject, "coords");
        if (coords != null && coords.Length >= 3)
            city.coords = new float[] { coords[0], coords[1], coords[2] };

        // targetGameObjectName (string)
        var tgn = ExtractJsonString(innerObject, "targetGameObjectName") ?? ExtractJsonString(innerObject, "targetGameObject") ?? ExtractJsonString(innerObject, "target");
        if (!string.IsNullOrEmpty(tgn)) city.targetGameObjectName = tgn;

        // sceneName (string)
        var scn = ExtractJsonString(innerObject, "sceneName") ?? ExtractJsonString(innerObject, "scene");
        if (!string.IsNullOrEmpty(scn)) city.sceneName = scn;

        // visited (bool) - extract and apply if present in JSON
        var vis = ExtractJsonBool(innerObject, "visited");
        if (vis.HasValue && vis.Value) city.visited = true;

        return city;
    }

    // Extract a quoted string value for a property name from the given JSON object fragment (best-effort)
    private static string ExtractJsonString(string jsonFrag, string propName)
    {
        int idx = IndexOfJsonProperty(jsonFrag, propName);
        if (idx < 0) return null;
        int colon = jsonFrag.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < jsonFrag.Length && char.IsWhiteSpace(jsonFrag[i])) i++;
        if (i >= jsonFrag.Length) return null;
        if (jsonFrag[i] != '"') return null;
        int qend = FindClosingQuote(jsonFrag, i);
        if (qend < 0) return null;
        return jsonFrag.Substring(i + 1, qend - i - 1);
    }

    // Extract a nullable int for a property (best-effort)
    private static int? ExtractJsonIntNullable(string jsonFrag, string propName)
    {
        int idx = IndexOfJsonProperty(jsonFrag, propName);
        if (idx < 0) return null;
        int colon = jsonFrag.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        // read until comma or end or closing brace
        while (i < jsonFrag.Length && char.IsWhiteSpace(jsonFrag[i])) i++;
        if (i >= jsonFrag.Length) return null;
        int start = i;
        while (i < jsonFrag.Length && (char.IsDigit(jsonFrag[i]) || jsonFrag[i] == '-' || jsonFrag[i] == '+')) i++;
        var token = jsonFrag.Substring(start, i - start);
        if (int.TryParse(token, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    // Extract a bool for a property (best-effort)
    private static bool? ExtractJsonBool(string jsonFrag, string propName)
    {
        int idx = IndexOfJsonProperty(jsonFrag, propName);
        if (idx < 0) return null;
        int colon = jsonFrag.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < jsonFrag.Length && char.IsWhiteSpace(jsonFrag[i])) i++;
        if (i >= jsonFrag.Length) return null;
        if (jsonFrag.Substring(i).StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (jsonFrag.Substring(i).StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    // Extract an array of floats like [x,y,z] (best-effort)
    private static float[] ExtractJsonFloatArray(string jsonFrag, string propName)
    {
        int idx = IndexOfJsonProperty(jsonFrag, propName);
        if (idx < 0) return null;
        int colon = jsonFrag.IndexOf(':', idx);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < jsonFrag.Length && char.IsWhiteSpace(jsonFrag[i])) i++;
        if (i >= jsonFrag.Length || jsonFrag[i] != '[') return null;
        int arrStart = i;
        int arrEnd = FindMatchingClosingBracket(jsonFrag, arrStart);
        if (arrEnd < 0) return null;
        string inner = jsonFrag.Substring(arrStart + 1, arrEnd - arrStart - 1);
        var parts = inner.Split(',');
        var list = new List<float>();
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (float.TryParse(t, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var f))
                list.Add(f);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    // Find matching closing bracket ']' for an array that starts at startIndex (assumes json[startIndex] == '[')
    private static int FindMatchingClosingBracket(string json, int startIndex)
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

    // Helper: find the index of a JSON property name in the text (best-effort, case-sensitive looking for "propName")
    private static int IndexOfJsonProperty(string json, string propName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propName)) return -1;
        string quoted = "\"" + propName + "\"";
        return json.IndexOf(quoted, StringComparison.Ordinal);
    }

    // Find matching closing brace for an object that starts at startIndex (assumes json[startIndex] == '{')
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

    // Find closing quote for a string starting at idx (json[idx] == '"')
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

    // static wrappers - always delegate safely to TravelButtonPlugin
    public static void LogInfo(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogInfo(Prefix + (message ?? ""));
        }
        catch { /* swallow */ }
    }

    public static void LogWarning(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogWarning(Prefix + (message ?? ""));
        }
        catch { /* swallow */ }
    }

    public static void LogError(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogError(Prefix + (message ?? ""));
        }
        catch { /* swallow */ }
    }

    public static void LogDebug(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogDebug(Prefix + (message ?? ""));
        }
        catch
        { }
    }

    private void Awake()
    {
        DebugConfig.IsDebug = false;

        try { TravelButtonPlugin.Initialize(this.Logger); } catch { /* swallow */
        }

        this.Logger.LogInfo("[TravelButton] direct Logger test (should appear in LogOutput.log)");
        TBLog.Info("TravelButtonPlugin test (should appear in LogOutput.log)");

        // sanity checks to confirm BepInEx receives logs:
        TBLog.Info("[TravelButton] BepInEx Logger is available (this.Logger) - test message");

        
        // Attempt to load TravelButton_Cities.json from likely locations and populate TravelButtonMod.Cities.
        // This is a best-effort load for deterministic defaults so that other initialization steps can observe cities.
        try
        {
            TryLoadCitiesJsonIntoTravelButtonMod();
        }
        catch (Exception ex)
        {
            try { LogSource?.LogWarning(Prefix + "Failed to load TravelButton_Cities.json during Initialize: " + ex.Message); } catch { }
        }
        
        try
        {
            TBLog.Info("TravelButton: startup - loaded cities:");
            if (TravelButtonMod.Cities == null) TBLog.Info(" - Cities == null");
            else
            {
                foreach (var c in TravelButtonMod.Cities)
                {
                    try
                    {
                        TBLog.Info($" - '{c.name}' sceneName='{c.sceneName ?? ""}' coords=[{(c.coords != null ? string.Join(", ", c.coords) : "")}]");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("Startup city log failed: " + ex);
        }

        try
        {
            TBLog.Info("TravelButtonPlugin.Awake: plugin initializing.");
            TravelButtonMod.InitFromConfig();
            if (TravelButtonMod.Cities != null && TravelButtonMod.Cities.Count > 0)
            {
                TBLog.Info($"Successfully loaded {TravelButtonMod.Cities.Count} cities from TravelButton_Cities.json.");
            }
            else
            {
                TBLog.Warn("Failed to load cities from TravelButton_Cities.json or the file is empty.");
            }
            // Start coroutine that will attempt to initialize config safely (may call ConfigManager.Load when safe)
            StartCoroutine(TryInitConfigCoroutine());
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonPlugin.Awake exception: " + ex);
        }
    }

    private IEnumerator TryInitConfigCoroutine()
    {
        int maxAttempts = 10;
        int attempt = 0;
        bool initialized = false;

        while (attempt < maxAttempts && !initialized)
        {
            attempt++;
            TBLog.Info($"TryInitConfigCoroutine: attempt {attempt}/{maxAttempts} to obtain config.");
            try
            {
                initialized = TravelButtonMod.InitFromConfig();
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryInitConfigCoroutine: InitFromConfig threw: " + ex.Message);
                initialized = false;
            }

            if (!initialized)
                yield return new WaitForSeconds(1.0f);
        }

        if (!initialized)
        {
            TBLog.Warn("TryInitConfigCoroutine: InitFromConfig did not find an external config after retries; using defaults.");
            if (TravelButtonMod.Cities == null || TravelButtonMod.Cities.Count == 0)
            {
                // Try local Default() again as a deterministic fallback
                try
                {
                    var localCfg = TravelButtonMod.GetLocalType("ConfigManager");
                    if (localCfg != null)
                    {
                        var def = localCfg.GetMethod("Default", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                        if (def != null)
                        {
                            TravelButtonMod.MapConfigInstanceToLocal(def);
                            TBLog.Info("TryInitConfigCoroutine: populated config from local ConfigManager.Default() fallback.");
                            initialized = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TryInitConfigCoroutine: fallback Default() failed: " + ex.Message);
                }
            }
        }

        // IMPORTANT: create BepInEx Config bindings so Configuration Manager (and BepInEx GUI) can show/edit settings.
        try
        {
            EnsureBepInExConfigBindings();
            TBLog.Info("BepInEx config bindings created.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("Failed to create BepInEx config bindings: " + ex);
        }

        // Bind any cities that were added after initial bind (defensive)
        try
        {
            BindCityConfigsForNewCities();
        }
        catch (Exception ex)
        {
            TBLog.Warn("BindCityConfigsForNewCities failed: " + ex);
        }

        // Finally ensure UI exists so the player can interact
        EnsureTravelButtonUI();
    }

    public static void LogCitySceneName(string cityName)
    {
        try
        {
            if (string.IsNullOrEmpty(cityName))
            {
                TBLog.Warn("LogCitySceneName: cityName is null/empty.");
                return;
            }

            var city = TravelButtonMod.Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
            if (city == null)
            {
                TBLog.Warn($"LogCitySceneName: city '{cityName}' not found in TravelButtonMod.Cities.");
                return;
            }

            TBLog.Info($"LogCitySceneName: city='{city.name}', sceneName='{city.sceneName ?? "(null)"}', coords={(city.coords != null ? $"[{string.Join(", ", city.coords)}]" : "(null)")}, targetGameObjectName='{city.targetGameObjectName ?? "(null)"}'");
        }
        catch (Exception ex)
        {
            TBLog.Warn("LogCitySceneName exception: " + ex);
        }
    }

    // Exposed logger set by the plugin bootstrap. May be null early during domain load.
    private void EnsureTravelButtonUI()
    {
        try
        {
            var existing = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (existing != null)
            {
                TBLog.Info("EnsureTravelButtonUI: TravelButtonUI already present in scene.");
                // Ensure DontDestroyOnLoad is set on the existing GameObject
                UnityEngine.Object.DontDestroyOnLoad(existing.gameObject);
                return;
            }

            var go = new GameObject("TravelButton_Global");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TravelButtonUI>();
            go.AddComponent<CityDiscovery>(); // Add CityDiscovery to the same persistent GameObject
            TBLog.Info("EnsureTravelButtonUI: TravelButtonUI and CityDiscovery components created and DontDestroyOnLoad applied.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("EnsureTravelButtonUI failed: " + ex);
        }
    }

    // --- BepInEx config binding helpers ---

    // Create top-level and per-city BepInEx Config.Bind entries and wire change handlers.
    // Call this once after TravelButtonMod.Cities is populated (InitFromConfig success or fallback).
    private void EnsureBepInExConfigBindings()
    {
        try
        {
            // Top-level bindings (section: TravelButton)
            bex_enableMod = Config.Bind("TravelButton", "EnableMod", TravelButtonMod.cfgEnableMod.Value, "Enable or disable the TravelButton mod");
            bex_globalPrice = Config.Bind("TravelButton", "GlobalTravelPrice", TravelButtonMod.cfgTravelCost.Value, "Default cost for teleport (silver)");
            bex_currencyItem = Config.Bind("TravelButton", "CurrencyItem", TravelButtonMod.cfgCurrencyItem.Value, "Item name used as currency");

            // Apply values from ConfigEntries into runtime wrappers
            TravelButtonMod.cfgEnableMod.Value = bex_enableMod.Value;
            TravelButtonMod.cfgTravelCost.Value = bex_globalPrice.Value;
            TravelButtonMod.cfgCurrencyItem.Value = bex_currencyItem.Value;

            // Hook top-level changes so runtime values update when user edits via CM
            bex_enableMod.SettingChanged += (s, e) =>
            {
                TravelButtonMod.cfgEnableMod.Value = bex_enableMod.Value;
                TravelButtonMod.PersistCitiesToConfig();
                TBLog.Info($"BepInEx config changed: EnableMod = {bex_enableMod.Value}");
            };
            bex_globalPrice.SettingChanged += (s, e) =>
            {
                TravelButtonMod.cfgTravelCost.Value = bex_globalPrice.Value;
                TBLog.Info($"BepInEx config changed: GlobalTravelPrice = {bex_globalPrice.Value}");
            };
            bex_currencyItem.SettingChanged += (s, e) =>
            {
                TravelButtonMod.cfgCurrencyItem.Value = bex_currencyItem.Value;
            };

            // Per-city bindings (section: TravelButton.Cities)
            if (TravelButtonMod.Cities == null) TravelButtonMod.Cities = new List<TravelButtonMod.City>();

            foreach (var city in TravelButtonMod.Cities)
            {
                // Avoid duplicate binds
                if (bex_cityEnabled.ContainsKey(city.name)) continue;

                string section = "TravelButton.Cities";
                var enabledKey = Config.Bind(section, $"{city.name}.Enabled", city.enabled, $"Enable teleport destination {city.name}");
                var priceDefault = city.price ?? TravelButtonMod.cfgTravelCost.Value;
                var priceKey = Config.Bind(section, $"{city.name}.Price", priceDefault, $"Price to teleport to {city.name} (overrides global)");

                bex_cityEnabled[city.name] = enabledKey;
                bex_cityPrice[city.name] = priceKey;

                // Sync config values into runtime city object
                city.enabled = enabledKey.Value;
                city.price = priceKey.Value;

                enabledKey.SettingChanged += (s, e) =>
                {
                    city.enabled = enabledKey.Value;
                    TBLog.Info($"Config changed: {city.name}.Enabled = {enabledKey.Value}");
                    TravelButtonMod.PersistCitiesToConfig();
                };
                priceKey.SettingChanged += (s, e) =>
                {
                    city.price = priceKey.Value;
                    TBLog.Info($"Config changed: {city.name}.Price = {priceKey.Value}");
                    TravelButtonMod.PersistCitiesToConfig();
                };
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("EnsureBepInExConfigBindings failed: " + ex);
        }
    }

    // Helper to bind config entries for any cities added at runtime after initial bind
    // Call BindCityConfigsForNewCities() if your code adds new cities later.
    private void BindCityConfigsForNewCities()
    {
        try
        {
            if (TravelButtonMod.Cities == null) return;
            foreach (var city in TravelButtonMod.Cities)
            {
                if (bex_cityEnabled.ContainsKey(city.name)) continue;
                string section = "TravelButton.Cities";
                var enabledKey = Config.Bind(section, $"{city.name}.Enabled", city.enabled, $"Enable teleport destination {city.name}");
                var priceDefault = city.price ?? TravelButtonMod.cfgTravelCost.Value;
                var priceKey = Config.Bind(section, $"{city.name}.Price", priceDefault, $"Price to teleport to {city.name} (overrides global)");

                bex_cityEnabled[city.name] = enabledKey;
                bex_cityPrice[city.name] = priceKey;

                // sync initial runtime
                city.enabled = enabledKey.Value;
                city.price = priceKey.Value;

                enabledKey.SettingChanged += (s, e) =>
                {
                    city.enabled = enabledKey.Value;
                    TravelButtonMod.PersistCitiesToConfig();
                };
                priceKey.SettingChanged += (s, e) =>
                {
                    city.price = priceKey.Value;
                    TravelButtonMod.PersistCitiesToConfig();
                };
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("BindCityConfigsForNewCities failed: " + ex);
        }
    }

    // Helper: search loaded assemblies for a type by simple name
    private static Type GetTypeByName(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetTypes().FirstOrDefault(x => x.Name == simpleName);
                if (t != null) return t;
            }
            catch { /* ignore */ }
        }
        return null;
    }
}

public static class TravelButtonMod
{
    public static bool TeleportInProgress = false;

    public static void LogLoadedScenesAndRootObjects()
    {
        try
        {
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            TBLog.Info($"LogLoadedScenesAndRootObjects: {sceneCount} loaded scene(s).");
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.IsValid()) continue;
                TBLog.Info($" Scene #{i}: name='{scene.name}', isLoaded={scene.isLoaded}, isDirty={scene.isDirty}");
                var roots = scene.GetRootGameObjects();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    TBLog.Info($"  root: '{r.name}' (children count approx: {r.transform.childCount})");
                }
            }
        }
        catch (Exception ex) { TBLog.Warn("LogLoadedScenesAndRootObjects exception: " + ex.Message); }
    }

    public static void LogCityAnchorsFromLoadedScenes()
    {
        try
        {
            if (Cities == null || Cities.Count == 0)
            {
                TBLog.Warn("LogCityAnchorsFromLoadedScenes: no cities available.");
                return;
            }

            TBLog.Info($"LogCityAnchorsFromLoadedScenes: scanning {UnityEngine.SceneManagement.SceneManager.sceneCount} loaded scene(s) for city anchors...");

            // For each loaded scene, scan root objects and children once and build a lookup of names -> (scene, transform)
            var lookup = new Dictionary<string, List<(string sceneName, Transform t)>>(StringComparer.OrdinalIgnoreCase);

            int scCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int si = 0; si < scCount; si++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    var all = root.GetComponentsInChildren<Transform>(true);
                    foreach (var tr in all)
                    {
                        if (tr == null) continue;
                        string name = tr.name ?? "";
                        if (!lookup.TryGetValue(name, out var list))
                        {
                            list = new List<(string, Transform)>();
                            lookup[name] = list;
                        }
                        list.Add((scene.name, tr));
                    }
                }
            }

            // For each city, try to find explicit targetGameObjectName first, then name-substring matches.
            foreach (var city in Cities)
            {
                try
                {
                    if (city == null) continue;
                    string cname = city.name ?? "(null)";
                    string target = city.targetGameObjectName ?? "";

                    TBLog.Info($"CityScan: --- {cname} --- targetGameObjectName='{target}' (existing sceneName='{city.sceneName ?? ""}'), coords={(city.coords != null ? $"[{string.Join(", ", city.coords)}]" : "(null)")}");

                    bool foundAny = false;

                    if (!string.IsNullOrEmpty(target))
                    {
                        if (lookup.TryGetValue(target, out var exacts) && exacts.Count > 0)
                        {
                            foreach (var (sceneName, tr) in exacts)
                            {
                                var pos = tr.position;
                                TBLog.Info($"CityScan: FOUND exact '{target}' in scene '{sceneName}' at ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) path='{GetFullPath(tr)}'");
                                foundAny = true;
                            }
                        }
                        else
                        {
                            // try GameObject.Find (active objects)
                            var go = GameObject.Find(target);
                            if (go != null)
                            {
                                var s = go.scene.IsValid() ? go.scene.name : "(unknown)";
                                var p = go.transform.position;
                                TBLog.Info($"CityScan: FOUND active exact '{target}' in scene '{s}' at ({p.x:F3}, {p.y:F3}, {p.z:F3})");
                                foundAny = true;
                            }
                        }
                    }

                    // Substring matches: look for transforms with names containing the city name (case-insensitive).
                    var substrMatches = new List<(string scene, Transform tr)>();
                    if (!string.IsNullOrEmpty(cname))
                    {
                        foreach (var kv in lookup)
                        {
                            if (kv.Key.IndexOf(cname, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foreach (var pair in kv.Value)
                                    substrMatches.Add((pair.sceneName, pair.t));
                            }
                        }

                        // Also consider active scene objects not included in lookup (should be included, but double-check)
                        var allActive = UnityEngine.Object.FindObjectsOfType<Transform>();
                        foreach (var tr in allActive)
                        {
                            if (tr == null) continue;
                            if (tr.name.IndexOf(cname, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                substrMatches.Add((tr.gameObject.scene.IsValid() ? tr.gameObject.scene.name : "(unknown)", tr));
                            }
                        }
                    }

                    // De-duplicate substrMatches by transform instance and log the most useful ones
                    var reported = new HashSet<int>();
                    int reportedCount = 0;
                    foreach (var m in substrMatches)
                    {
                        if (m.tr == null) continue;
                        int id = m.tr.GetInstanceID();
                        if (reported.Contains(id)) continue;
                        reported.Add(id);

                        var pos = m.tr.position;
                        string sceneN = m.scene ?? "(unknown)";
                        string path = GetFullPath(m.tr);
                        TBLog.Info($"CityScan: SUBSTRING match '{m.tr.name}' in scene '{sceneN}' at ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) path='{path}'");
                        reportedCount++;
                        if (reportedCount >= 20) break;
                    }

                    if (!foundAny && reportedCount == 0)
                        TBLog.Info($"CityScan: no matches found in loaded scenes for city '{cname}'. Consider loading the map or using in-game travel to that map, then run this again.");
                }
                catch (Exception exCity)
                {
                    TBLog.Warn("CityScan: error scanning city: " + exCity.Message);
                }
            }

            TBLog.Info("LogCityAnchorsFromLoadedScenes: scan complete.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("LogCityAnchorsFromLoadedScenes exception: " + ex.Message);
        }
    }

    public static void AutoAssignSceneNamesFromLoadedScenes()
    {
        try
        {
            TBLog.Info("AutoAssignSceneNamesFromLoadedScenes: scanning loaded scenes for city anchors/names...");
            if (Cities == null || Cities.Count == 0)
            {
                TBLog.Warn("AutoAssignSceneNamesFromLoadedScenes: no cities available to scan.");
                return;
            }

            int assigned = 0;
            // iterate loaded scenes
            int sceneCount = SceneManager.sceneCount;
            for (int si = 0; si < sceneCount; si++)
            {
                var scene = SceneManager.GetSceneAt(si);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    var allTransforms = root.GetComponentsInChildren<Transform>(true);
                    foreach (var tr in allTransforms)
                    {
                        if (tr == null) continue;
                        string gname = tr.name ?? "";
                        // try match by exact targetGameObjectName first, then city name substring
                        foreach (var city in Cities)
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(city.targetGameObjectName) &&
                                    string.Equals(gname, city.targetGameObjectName, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (string.IsNullOrEmpty(city.sceneName) || city.sceneName != scene.name)
                                    {
                                        city.sceneName = scene.name;
                                        TBLog.Info($"AutoAssign: matched targetGameObjectName '{gname}' -> setting city '{city.name}'.sceneName = '{scene.name}'");
                                        assigned++;
                                    }
                                }
                                else if (gname.IndexOf(city.name ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (string.IsNullOrEmpty(city.sceneName) || city.sceneName != scene.name)
                                    {
                                        city.sceneName = scene.name;
                                        TBLog.Info($"AutoAssign: matched name substring '{gname}' -> setting city '{city.name}'.sceneName = '{scene.name}'");
                                        assigned++;
                                    }
                                }
                            }
                            catch { /* ignore per-city errors */ }
                        }
                    }
                }
            }

            if (assigned > 0)
            {
                TBLog.Info($"AutoAssignSceneNamesFromLoadedScenes: assigned {assigned} sceneName(s). Persisting cities to config.");
                try { PersistCitiesToConfig(); } catch { TBLog.Warn("AutoAssignSceneNamesFromLoadedScenes: PersistCitiesToConfig failed."); }
            }
            else
            {
                TBLog.Info("AutoAssignSceneNamesFromLoadedScenes: no matches found in loaded scenes. Make sure the correct scene is loaded and try again.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AutoAssignSceneNamesFromLoadedScenes exception: " + ex.Message);
        }
    }

    // Simple configurable wrappers to keep compatibility with existing code
    public class ConfigEntry<T>
    {
        public T Value;
        public ConfigEntry(T v) { Value = v; }
    }

    // Global config entries (accessed as TravelButtonMod.cfgTravelCost.Value in existing UI)
    public static ConfigEntry<int> cfgTravelCost = new ConfigEntry<int>(100);
    public static ConfigEntry<bool> cfgEnableTeleport = new ConfigEntry<bool>(true);
    public static ConfigEntry<bool> cfgEnableMod = new ConfigEntry<bool>(true);
    public static ConfigEntry<string> cfgCurrencyItem = new ConfigEntry<string>("Silver");

    // City representation consumed by UI code
    [Serializable]
    public class City
    {
        public string name;
        // coords array [x,y,z] or null
        public float[] coords;
        // optional name of a GameObject to find at runtime
        public string targetGameObjectName;
        // optional per-city price; null means use global
        public int? price;
        // whether city is explicitly enabled in config (default false)
        public bool enabled;

        public string sceneName;

        public City(string name)
        {
            this.name = name;
            this.coords = null;
            this.targetGameObjectName = null;
            this.price = null;
            this.enabled = false;
            this.sceneName = null;
        }

        // Compatibility properties expected by older code:
        // property 'visited' (lowercase) → maps to VisitedTracker if available
        public bool visited
        {
            get
            {
                try { return VisitedTracker.HasVisited(this.name); }
                catch { return false; }
            }
            set
            {
                try
                {
                    if (value) VisitedTracker.MarkVisited(this.name);
                }
                catch { }
            }
        }

        // compatibility method name used previously in code: isCityEnabled()
        public bool isCityEnabled()
        {
            return TravelButtonMod.IsCityEnabled(this.name);
        }
    }

    // Public list used by UI code (TravelButtonUI reads TravelButtonMod.Cities)
    public static List<City> Cities { get; set; } = new List<City>();

    // Path/filename helpers exposed for debugging
    public static string ConfigFilePath
    {
        get
        {
            try { return ConfigManager.ConfigPathForLog(); }
            catch { return "(unknown)"; }
        }
    }

    // Initialize mod state from JSON config -> should be called once at mod load
    // Returns true if a config instance was located and mapped (or local default used), false otherwise.
    public static bool InitFromConfig()
    {
        try
        {
            TBLog.Info("InitFromConfig: attempting to obtain ConfigManager.Config (safe, no unconditional Load).");

            // Try to locate a type named ConfigManager in loaded assemblies
            Type cfgMgrType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    cfgMgrType = asm.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
                    if (cfgMgrType != null) break;
                }
                catch { /* ignore assemblies that can't enumerate types */ }
            }

            object cfgInstance = null;

            // If we found a ConfigManager type, try to read its static Config (do NOT call Load() yet)
            if (cfgMgrType != null)
            {
                try
                {
                    var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                    var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                    if (cfgProp != null) cfgInstance = cfgProp.GetValue(null);
                    else if (cfgField != null) cfgInstance = cfgField.GetValue(null);
                }
                catch (Exception ex)
                {
                    TBLog.Warn("InitFromConfig: reading ConfigManager.Config threw: " + ex.Message);
                    cfgInstance = null;
                }
            }

            // If no ConfigManager type found OR the found type has a null Config,
            // try to use a local ConfigManager.Default() (the Default() you added in src/ConfigManager.cs).
            // This guarantees deterministic defaults even if an external ConfigManager hasn't initialized.
            if (cfgInstance == null)
            {
                var localCfgMgr = GetLocalType("ConfigManager");
                if (localCfgMgr != null)
                {
                    try
                    {
                        var defMethod = localCfgMgr.GetMethod("Default", BindingFlags.Public | BindingFlags.Static);
                        if (defMethod != null)
                        {
                            var def = defMethod.Invoke(null, null);
                            if (def != null)
                            {
                                MapConfigInstanceToLocal(def);
                                TBLog.Info("InitFromConfig: used local ConfigManager.Default() to populate config.");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("InitFromConfig: calling local ConfigManager.Default() failed: " + ex.Message);
                        // continue to try safer external Load path below
                    }
                }
            }

            // If we still don't have a config instance but found an external ConfigManager type,
            // we may attempt to call its Load() safely (only if local or Newtonsoft is available).
            if (cfgInstance == null && cfgMgrType != null)
            {
                bool callLoad = false;
                bool isLocalConfigMgr = cfgMgrType.Assembly == typeof(TravelButtonMod).Assembly;

                if (isLocalConfigMgr)
                {
                    callLoad = true;
                    TBLog.Info("InitFromConfig: calling Load() on local ConfigManager type.");
                }
                else
                {
                    // Only call Load on external ConfigManager when Newtonsoft is available, to avoid assembly load exceptions.
                    bool hasNewtonsoft = AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                    {
                        try { return a.GetTypes().Any(t => t.FullName == "Newtonsoft.Json.JsonConvert"); } catch { return false; }
                    });

                    if (hasNewtonsoft)
                    {
                        callLoad = true;
                        TBLog.Info("InitFromConfig: external ConfigManager found and Newtonsoft present; will call Load() via reflection.");
                    }
                    else
                    {
                        TBLog.Warn("InitFromConfig: external ConfigManager found but Newtonsoft not present; skipping Load() to avoid assembly load errors.");
                    }
                }

                if (callLoad)
                {
                    try
                    {
                        var loadMethod = cfgMgrType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                        if (loadMethod != null)
                        {
                            loadMethod.Invoke(null, null);
                            // read Config after Load()
                            var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                            var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                            cfgInstance = cfgProp != null ? cfgProp.GetValue(null) : cfgField != null ? cfgField.GetValue(null) : null;
                        }
                        else
                        {
                            TBLog.Warn("InitFromConfig: ConfigManager.Load method not found.");
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        TBLog.Warn("InitFromConfig: ConfigManager.Load failed via reflection: " + (tie.InnerException?.Message ?? tie.Message));
                        return false; // allow retry from coroutine
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("InitFromConfig: exception invoking ConfigManager.Load: " + ex.Message);
                        return false;
                    }
                }
            }

            // If we have a config instance now, map it into local fields and cities
            if (cfgInstance != null)
            {
                MapConfigInstanceToLocal(cfgInstance);
                TBLog.Info($"InitFromConfig: Loaded {Cities?.Count ?? 0} cities from ConfigManager.");
                return true;
            }

            // No config available (and we failed to get a local default); signal caller to retry / fallback.
            TBLog.Info("InitFromConfig: no config instance available (will retry or fallback).");
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("InitFromConfig: unexpected exception: " + ex);
            return false;
        }
    }

    // Map a config object (the ConfigManager.Config instance) into TravelButtonMod fields (cfgTravelCost, cfgCurrencyItem, Cities list)
    public static void MapConfigInstanceToLocal(object cfgInstance)
    {
        if (cfgInstance == null) return;
        try
        {
            // top-level mappings
            try
            {
                var enabledMember = cfgInstance.GetType().GetField("enabled") ?? (MemberInfo)cfgInstance.GetType().GetProperty("enabled");
                if (enabledMember is FieldInfo fe) cfgEnableMod.Value = SafeGetBool(fe.GetValue(cfgInstance));
                else if (enabledMember is PropertyInfo pe) cfgEnableMod.Value = SafeGetBool(pe.GetValue(cfgInstance));

                var curMember = cfgInstance.GetType().GetField("currencyItem") ?? (MemberInfo)cfgInstance.GetType().GetProperty("currencyItem");
                if (curMember is FieldInfo fc) cfgCurrencyItem.Value = SafeGetString(fc.GetValue(cfgInstance)) ?? "Silver";
                else if (curMember is PropertyInfo pc) cfgCurrencyItem.Value = SafeGetString(pc.GetValue(cfgInstance)) ?? "Silver";

                var gtpMember = cfgInstance.GetType().GetField("globalTeleportPrice") ?? (MemberInfo)cfgInstance.GetType().GetProperty("globalTeleportPrice");
                if (gtpMember is FieldInfo fg) cfgTravelCost.Value = SafeGetInt(fg.GetValue(cfgInstance), cfgTravelCost.Value);
                else if (gtpMember is PropertyInfo pg) cfgTravelCost.Value = SafeGetInt(pg.GetValue(cfgInstance), cfgTravelCost.Value);
            }
            catch (Exception ex)
            {
                TBLog.Warn("MapConfigInstanceToLocal: top-level map failed: " + ex.Message);
            }

            // cities
            try
            {
                Cities = new List<City>();
                var citiesMemberField = cfgInstance.GetType().GetField("cities", BindingFlags.Public | BindingFlags.Instance);
                var citiesMemberProp = cfgInstance.GetType().GetProperty("cities", BindingFlags.Public | BindingFlags.Instance);
                object citiesObj = citiesMemberField != null ? citiesMemberField.GetValue(cfgInstance) : citiesMemberProp != null ? citiesMemberProp.GetValue(cfgInstance) : null;

                if (citiesObj != null)
                {
                    var dict = citiesObj as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (var key in dict.Keys)
                        {
                            try
                            {
                                string cname = key.ToString();
                                var cityCfgObj = dict[key];
                                var mapped = MapSingleCityFromObject(cname, cityCfgObj);
                                if (mapped != null) Cities.Add(mapped);
                            }
                            catch (Exception inner)
                            {
                                TBLog.Warn("MapConfigInstanceToLocal: error mapping city entry: " + inner.Message);
                            }
                        }
                    }
                    else
                    {
                        // try enumerator approach for generic IDictionary<,>
                        var getEnum = citiesObj.GetType().GetMethod("GetEnumerator");
                        if (getEnum != null)
                        {
                            var enumerator = getEnum.Invoke(citiesObj, null);
                            var moveNext = enumerator.GetType().GetMethod("MoveNext");
                            var currentProp = enumerator.GetType().GetProperty("Current");
                            while ((bool)moveNext.Invoke(enumerator, null))
                            {
                                var current = currentProp.GetValue(enumerator);
                                var keyProp = current.GetType().GetProperty("Key");
                                var valProp = current.GetType().GetProperty("Value");
                                var k = keyProp.GetValue(current);
                                var v = valProp.GetValue(current);
                                string cname = k.ToString();
                                var mapped = MapSingleCityFromObject(cname, v);
                                if (mapped != null) Cities.Add(mapped);
                            }
                        }
                        else
                        {
                            TBLog.Warn("MapConfigInstanceToLocal: cfg.cities is not enumerable.");
                        }
                    }
                }
                else
                {
                    TBLog.Warn("MapConfigInstanceToLocal: cfg.cities is null.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("MapConfigInstanceToLocal: cities mapping failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("MapConfigInstanceToLocal: unexpected: " + ex.Message);
        }
    }

    private static City MapSingleCityFromObject(string cname, object cityCfgObj)
    {
        try
        {
            var city = new City(cname);

            var enabledMember = cityCfgObj.GetType().GetField("enabled") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("enabled");
            if (enabledMember is FieldInfo fe) city.enabled = SafeGetBool(fe.GetValue(cityCfgObj));
            else if (enabledMember is PropertyInfo pe) city.enabled = SafeGetBool(pe.GetValue(cityCfgObj));

            var priceMember = cityCfgObj.GetType().GetField("price") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("price");
            if (priceMember is FieldInfo fprice) city.price = SafeGetNullableInt(fprice.GetValue(cityCfgObj));
            else if (priceMember is PropertyInfo pprice) city.price = SafeGetNullableInt(pprice.GetValue(cityCfgObj));

            var coordsMember = cityCfgObj.GetType().GetField("coords") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("coords");
            object coordsVal = coordsMember is FieldInfo fc ? fc.GetValue(cityCfgObj) : coordsMember is PropertyInfo pc ? pc.GetValue(cityCfgObj) : null;
            if (coordsVal != null)
            {
                var list = coordsVal as System.Collections.IList;
                if (list != null && list.Count >= 3)
                {
                    try
                    {
                        city.coords = new float[3] { Convert.ToSingle(list[0]), Convert.ToSingle(list[1]), Convert.ToSingle(list[2]) };
                    }
                    catch { city.coords = null; }
                }
            }

            var tgnMember = cityCfgObj.GetType().GetField("targetGameObjectName") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("targetGameObjectName");
            if (tgnMember is FieldInfo ftgn) city.targetGameObjectName = SafeGetString(ftgn.GetValue(cityCfgObj));
            else if (tgnMember is PropertyInfo ptgn) city.targetGameObjectName = SafeGetString(ptgn.GetValue(cityCfgObj));

            var sceneMember = cityCfgObj.GetType().GetField("sceneName") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("sceneName");
            if (sceneMember is FieldInfo fsc) city.sceneName = SafeGetString(fsc.GetValue(cityCfgObj));
            else if (sceneMember is PropertyInfo psc) city.sceneName = SafeGetString(psc.GetValue(cityCfgObj));

            return city;
        }
        catch (Exception ex)
        {
            TBLog.Warn("MapSingleCityFromObject: " + ex.Message);
            return null;
        }
    }

    // Try to find a type defined in our loaded assemblies by simple name (prefers our assembly)
    public static Type GetLocalType(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm == typeof(TravelButtonMod).Assembly)
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == simpleName);
                    if (t != null) return t;
                }
            }
            catch { }
        }
        return null;
    }

    // Safe parsing helpers
    private static bool SafeGetBool(object o)
    {
        if (o == null) return false;
        if (o is bool b) return b;
        if (o is int i) return i != 0;
        if (o is long l) return l != 0;
        if (o is string s && bool.TryParse(s, out var r)) return r;
        return false;
    }

    private static int SafeGetInt(object o, int fallback)
    {
        if (o == null) return fallback;
        try
        {
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o is string s && int.TryParse(s, out var r)) return r;
            if (o is double d) return (int)d;
            if (o is float f) return (int)f;
        }
        catch { }
        return fallback;
    }

    private static int? SafeGetNullableInt(object o)
    {
        if (o == null) return null;
        try
        {
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o is string s && int.TryParse(s, out var r)) return r;
            if (o is double d) return (int)d;
            if (o is float f) return (int)f;
        }
        catch { }
        return null;
    }

    private static string SafeGetString(object o)
    {
        if (o == null) return null;
        try { return o.ToString(); } catch { return null; }
    }

    // Try to resolve the target position for a city. Tries active objects, inactive/assets,
    // substring heuristics, tag lookup, and falls back to explicit coords. Logs helpful debug information.
    public static bool TryGetTargetPosition(string targetGameObjectName, float[] coordsFallback, string cityName, out Vector3 outPos)
    {
        outPos = Vector3.zero;

        if (!string.IsNullOrEmpty(targetGameObjectName))
        {
            try
            {
                // 1) Fast path: active object by exact name (but ignore UI objects)
                var go = GameObject.Find(targetGameObjectName);
                if (go != null)
                {
                    if (!IsUiGameObject(go) && go.scene.IsValid() && go.scene.isLoaded)
                    {
                        outPos = go.transform.position;
                        TBLog.Info($"TryGetTargetPosition: found active GameObject '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                        return true;
                    }
                    else
                    {
                        TBLog.Warn($"TryGetTargetPosition: found '{targetGameObjectName}' but it's a UI/invalid-scene object (ignored).");
                    }
                }

                // 2) Search all objects (includes inactive and assets) but only accept valid scene objects
                var all = Resources.FindObjectsOfTypeAll<GameObject>();

                // Exact name match but only if in a loaded scene and not UI
                var exactSceneObj = all.FirstOrDefault(c =>
                    string.Equals(c.name, targetGameObjectName, StringComparison.Ordinal) &&
                    c.scene.IsValid() && c.scene.isLoaded &&
                    !IsUiGameObject(c) &&
                    c.transform != null && c.transform.position.sqrMagnitude > 0.0001f);

                if (exactSceneObj != null)
                {
                    // If city.sceneName set, require scene match
                    if (string.IsNullOrEmpty(cityName) || string.IsNullOrEmpty(exactSceneObj.scene.name) || true) { /* keep logging below */ }
                    outPos = exactSceneObj.transform.position;
                    TBLog.Info($"TryGetTargetPosition: found scene GameObject by exact match '{exactSceneObj.name}' at {outPos} for city '{cityName}'.");
                    return true;
                }

                // Substring/clone match but be conservative:
                // - candidate name length >= 3 (avoid single-letter false matches)
                // - candidate must be part of a valid loaded scene (not an asset)
                // - candidate must not be UI and must have non-zero world position
                var containsSceneObj = all.FirstOrDefault(c =>
                    !string.IsNullOrEmpty(c.name) &&
                    c.name.Length >= 3 &&
                    (c.name.IndexOf(targetGameObjectName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     targetGameObjectName.IndexOf(c.name, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    c.scene.IsValid() && c.scene.isLoaded &&
                    !IsUiGameObject(c) &&
                    c.transform != null && c.transform.position.sqrMagnitude > 0.0001f);

                if (containsSceneObj != null)
                {
                    outPos = containsSceneObj.transform.position;
                    TBLog.Info($"TryGetTargetPosition: found scene GameObject by substring match '{containsSceneObj.name}' -> '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                    return true;
                }

                // Tag lookup fallback (only accept scene objects and non-UI)
                try
                {
                    var byTag = GameObject.FindGameObjectWithTag(targetGameObjectName);
                    if (byTag != null && byTag.scene.IsValid() && byTag.scene.isLoaded && !IsUiGameObject(byTag))
                    {
                        outPos = byTag.transform.position;
                        TBLog.Info($"TryGetTargetPosition: found GameObject by tag '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                        return true;
                    }
                }
                catch { /* ignore tag errors */ }

                TBLog.Warn($"TryGetTargetPosition: target GameObject '{targetGameObjectName}' not found in any loaded scene for city '{cityName}'.");
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TryGetTargetPosition: error while searching for '{targetGameObjectName}' for city '{cityName}': {ex.Message}");
            }

            // Optionally emit diagnostic candidates for debugging
            try { LogCandidateAnchorNames(cityName); } catch { }
        }

        // 3) Fallback to explicit coords (if present)
        if (coordsFallback != null && coordsFallback.Length >= 3)
        {
            outPos = new Vector3(coordsFallback[0], coordsFallback[1], coordsFallback[2]);
            TBLog.Info($"TryGetTargetPosition: using explicit coords ({outPos.x}, {outPos.y}, {outPos.z}) for city '{cityName}'.");
            return true;
        }

        TBLog.Warn($"TryGetTargetPosition: no GameObject and no explicit coords available for city '{cityName}'.");
        return false;
    }

    // Helper: returns true for UI elements we should ignore as teleport anchors
    private static bool IsUiGameObject(GameObject go)
    {
        if (go == null) return false;
        try
        {
            // RectTransform indicates a UI element
            if (go.GetComponent<RectTransform>() != null) return true;
            // If any parent has a Canvas, treat as UI
            if (go.GetComponentInParent<Canvas>() != null) return true;
            // If it's on the UI layer (named "UI"), treat as UI
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1 && go.layer == uiLayer) return true;
        }
        catch { /* ignore reflection errors */ }
        return false;
    }

    // Diagnostic helper: enumerate likely anchor GameObjects and log them to the TravelButton log.
    public static void LogCandidateAnchorNames(string cityName, int maxResults = 50)
    {
        try
        {
            TBLog.Info($"Anchor diagnostic: searching for candidates for city '{cityName}'...");

            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            int count = 0;
            foreach (var go in all)
            {
                if (go == null) continue;
                var name = go.name ?? "";
                if (name.IndexOf(cityName ?? "", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("town", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("village", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                    string path = GetFullPath(go.transform);
                    string scene = (go.scene.IsValid() ? go.scene.name : "(asset)");
                    TBLog.Info($"Anchor candidate #{count}: name='{name}' scene='{scene}' path='{path}'");
                    if (count >= maxResults) break;
                }
            }

            if (count == 0)
                TBLog.Info($"Anchor diagnostic: no candidates found for '{cityName}' (tried substrings). Consider checking scene objects or config targetGameObjectName.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("Anchor diagnostic failed: " + ex.Message);
        }
    }

    // Helper: return full transform path "Root/Child/GrandChild"
    private static string GetFullPath(Transform t)
    {
        if (t == null) return "(null)";
        var parts = new List<string>();
        var cur = t;
        while (cur != null)
        {
            parts.Add(cur.name ?? "(null)");
            cur = cur.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // Query if a city is enabled in config (does not consider visited state)
    public static bool IsCityEnabled(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        try
        {
            // Prefer reading external ConfigManager.Config if available (without calling Load again)
            Type cfgMgrType = null;
            object cfgInstance = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    cfgMgrType = asm.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
                    if (cfgMgrType != null) break;
                }
                catch { }
            }

            if (cfgMgrType != null)
            {
                try
                {
                    var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                    var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                    cfgInstance = cfgProp != null ? cfgProp.GetValue(null) : cfgField != null ? cfgField.GetValue(null) : null;
                }
                catch { cfgInstance = null; }
            }

            if (cfgInstance != null)
            {
                var citiesMemberField = cfgInstance.GetType().GetField("cities");
                var citiesMemberProp = cfgInstance.GetType().GetProperty("cities");
                object citiesObj = citiesMemberField != null ? citiesMemberField.GetValue(cfgInstance) : citiesMemberProp != null ? citiesMemberProp.GetValue(cfgInstance) : null;
                if (citiesObj is System.Collections.IDictionary dict)
                {
                    if (dict.Contains(cityName))
                    {
                        var cityCfgObj = dict[cityName];
                        var enabledMember = cityCfgObj.GetType().GetField("enabled") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("enabled");
                        if (enabledMember is FieldInfo fe) return SafeGetBool(fe.GetValue(cityCfgObj));
                        if (enabledMember is PropertyInfo pe) return SafeGetBool(pe.GetValue(cityCfgObj));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("IsCityEnabled: reading external config failed: " + ex.Message);
        }

        var local = Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
        if (local != null) return local.enabled;

        return false;
    }

    // Update in-memory Cities -> config and save; useful if user toggles a city via UI/editor
    public static void PersistCitiesToConfig()
    {
        try
        {
            bool persisted = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var cfgMgrType = asm.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
                    if (cfgMgrType == null) continue;

                    var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                    var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                    object cfgInstance = cfgProp != null ? cfgProp.GetValue(null) : cfgField != null ? cfgField.GetValue(null) : null;
                    if (cfgInstance == null) continue;

                    var citiesMemberField = cfgInstance.GetType().GetField("cities");
                    var citiesMemberProp = cfgInstance.GetType().GetProperty("cities");
                    object citiesObj = citiesMemberField != null ? citiesMemberField.GetValue(cfgInstance) : citiesMemberProp != null ? citiesMemberProp.GetValue(cfgInstance) : null;

                    if (citiesObj is System.Collections.IDictionary dict)
                    {
                        var genericArgs = dict.GetType().GetGenericArguments();
                        Type cityCfgType = genericArgs != null && genericArgs.Length >= 2 ? genericArgs[1] : null;

                        foreach (var city in Cities)
                        {
                            object cc = null;
                            if (cityCfgType != null)
                            {
                                try
                                {
                                    cc = Activator.CreateInstance(cityCfgType);
                                    var fEnabled = cityCfgType.GetField("enabled") ?? (MemberInfo)cityCfgType.GetProperty("enabled");
                                    if (fEnabled is FieldInfo fe) fe.SetValue(cc, city.enabled);
                                    else if (fEnabled is PropertyInfo pe) pe.SetValue(cc, city.enabled);

                                    var fPrice = cityCfgType.GetField("price") ?? (MemberInfo)cityCfgType.GetProperty("price");
                                    if (fPrice is FieldInfo fp) fp.SetValue(cc, city.price);
                                    else if (fPrice is PropertyInfo pp) pp.SetValue(cc, city.price);

                                    var fCoords = cityCfgType.GetField("coords") ?? (MemberInfo)cityCfgType.GetProperty("coords");
                                    if (fCoords is FieldInfo fc) fc.SetValue(cc, city.coords);
                                    else if (fCoords is PropertyInfo pc) pc.SetValue(cc, city.coords);

                                    var fTgn = cityCfgType.GetField("targetGameObjectName") ?? (MemberInfo)cityCfgType.GetProperty("targetGameObjectName");
                                    if (fTgn is FieldInfo ft) ft.SetValue(cc, city.targetGameObjectName);
                                    else if (fTgn is PropertyInfo pt) pt.SetValue(cc, city.targetGameObjectName);
                                }
                                catch { cc = null; }
                            }

                            try
                            {
                                dict[city.name] = cc ?? city;
                            }
                            catch
                            {
                                var addMethod = dict.GetType().GetMethod("Add");
                                if (addMethod != null) addMethod.Invoke(dict, new object[] { city.name, cc ?? city });
                            }
                        }

                        var saveMethod = cfgMgrType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static);
                        saveMethod?.Invoke(null, null);
                        persisted = true;
                        TBLog.Info("PersistCitiesToConfig: persisted cities into external ConfigManager.Config and called Save().");
                        break;
                    }
                }
                catch { }
            }

            if (!persisted)
            {
                TBLog.Warn("PersistCitiesToConfig: Could not persist cities because external ConfigManager not found or not writable.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("PersistCitiesToConfig exception: " + ex);
        }
    }

    // Convenience: find a City by name (case-insensitive)
    public static City FindCity(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var c in Cities)
        {
            if (string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    // Called by UI after successful teleport to mark visited and persist if needed
    public static void OnSuccessfulTeleport(string cityName)
    {
        try
        {
            if (string.IsNullOrEmpty(cityName)) return;
            try { VisitedTracker.MarkVisited(cityName); } catch { }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("OnSuccessfulTeleport exception: " + ex);
        }
    }

    // --- Diagnostic helpers requested: DumpTravelButtonState and ForceShowTravelButton ---

    // Dumps TravelButton GameObject state (parent, canvas, rect, image, button, canvasGroup) to the mod log.
    public static void DumpTravelButtonState()
    {
        try
        {
            var tb = GameObject.Find("TravelButton");
            if (tb == null)
            {
                TBLog.Warn("DumpTravelButtonState: TravelButton GameObject not found.");
                return;
            }

            var rt = tb.GetComponent<RectTransform>();
            var btn = tb.GetComponent<Button>();
            var img = tb.GetComponent<Image>();
            var cg = tb.GetComponent<CanvasGroup>();
            var root = tb.transform.root;
            TBLog.Info($"DumpTravelButtonState: name='{tb.name}', activeSelf={tb.activeSelf}, activeInHierarchy={tb.activeInHierarchy}");
            TBLog.Info($"DumpTravelButtonState: parent='{tb.transform.parent?.name}', root='{root?.name}'");
            if (rt != null) TBLog.Info($"DumpTravelButtonState: anchoredPosition={rt.anchoredPosition}, sizeDelta={rt.sizeDelta}, anchorMin={rt.anchorMin}, anchorMax={rt.anchorMax}, pivot={rt.pivot}");
            if (btn != null) TBLog.Info($"DumpTravelButtonState: Button.interactable={btn.interactable}");
            if (img != null) TBLog.Info($"DumpTravelButtonState: Image.color={img.color}, raycastTarget={img.raycastTarget}");
            if (cg != null) TBLog.Info($"DumpTravelButtonState: CanvasGroup alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts}");
            var canvas = tb.GetComponentInParent<Canvas>();
            if (canvas != null) TBLog.Info($"DumpTravelButtonState: Canvas name={canvas.gameObject.name}, sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode}");
            else TBLog.Warn("DumpTravelButtonState: No parent Canvas found.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpTravelButtonState exception: " + ex.Message);
        }
    }

}
