using System;
using System.IO;
using UnityEngine;

// ConfigManager: load/save travel_config.json (per-city config).
// - Creates default config if missing.
// - Fills missing cities (from Default()) for backward compatibility.
// - Exposes the TravelConfig object for other code to use.
// NOTE: This is a legacy config manager that uses Newtonsoft.Json and dictionary-based cities.
// The new system uses CityConfig.cs with JsonUtility and array-based cities.

public class LegacyCityConfig
{
    public string name;
    public int price;
    public float[] coords;
    public string targetGameObjectName;
    public string sceneName;
    public string desc;

public class LegacyTravelConfig
{
    public bool enabled = true;
    public string currencyItem = "Silver";
    public int globalTeleportPrice = 100;
    public Dictionary<string, LegacyCityConfig> cities = new Dictionary<string, LegacyCityConfig>();
}

/// <summary>
/// Container for the cities array in TravelButton_Cities.json.
/// Uses array-based schema with no global settings or per-city runtime state.
/// </summary>
[Serializable]
public class TravelConfig
{
    public List<CityConfig> cities;

    // Runtime-held config instance
    public static LegacyTravelConfig Config { get; set; } = null;

    /// <summary>
    /// Returns the default TravelConfig with all 6 cities configured.
    /// This is the canonical source of city metadata.
    /// </summary>
    public static TravelConfig Default()
    {
        var config = new TravelConfig();
        
        config.cities.Add(new CityConfig(
            "Cierzo",
            200,
            new float[] { 1410.388f, 6.786f, 1665.642f },
            "Cierzo",
            "CierzoNewTerrain",
            "Cierzo - example description"
        ));
        
        config.cities.Add(new CityConfig(
            "Levant",
            200,
            new float[] { -55.212f, 1.056f, 79.379f },
            "WarpLocation_HM",
            "Levant",
            "Levant - example description"
        ));
        
        config.cities.Add(new CityConfig(
            "Monsoon",
            200,
            new float[] { 61.553f, -3.743f, 167.599f },
            "Monsoon_Location",
            "Monsoon",
            "Monsoon - example description"
        ));
        
        config.cities.Add(new CityConfig(
            "Berg",
            200,
            new float[] { 1202.414f, -13.071f, 1378.836f },
            "Berg",
            "Berg",
            "Berg - example description"
        ));
        
        config.cities.Add(new CityConfig(
            "Harmattan",
            200,
            new float[] { 93.757f, 65.474f, 767.849f },
            "Harmattan_Location",
            "Harmattan",
            "Harmattan - example description"
        ));
        
        config.cities.Add(new CityConfig(
            "Sirocco",
            200,
            new float[] { 600.0f, 1.2f, -300.0f },
            "Sirocco_Location",
            "NewSirocco",
            "Sirocco - example description"
        ));
        
        return config;
    }

    /// <summary>
    /// Loads TravelConfig from a JSON file. Returns null on error.
    /// Supports both array-form and legacy keyed-object form for backward compatibility.
    /// </summary>
    public static TravelConfig LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                TBLog.Warn($"TravelConfig.LoadFromFile: file not found at {path}");
                return null;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                TBLog.Warn($"TravelConfig.LoadFromFile: file is empty at {path}");
                return null;
            }

            var json = File.ReadAllText(DefaultConfigPath);
            var des = JsonConvert.DeserializeObject<LegacyTravelConfig>(json);
            if (des == null) des = Default();

            // fill missing keys with defaults for backward compatibility
            var defaults = Default();
            if (des.cities == null) des.cities = defaults.cities;
            if (string.IsNullOrEmpty(des.currencyItem)) des.currencyItem = defaults.currencyItem;
            if (des.globalTeleportPrice == 0) des.globalTeleportPrice = defaults.globalTeleportPrice;

            // ensure known city entries exist (so config always contains the known city keys)
            foreach (var kv in defaults.cities)
            {
                var config = JsonConvert.DeserializeObject<TravelConfig>(json);
                if (config != null && config.cities != null && config.cities.Count > 0)
                {
                    TBLog.Info($"TravelConfig.LoadFromFile: loaded {config.cities.Count} cities from {path} (array form)");
                    return config;
                }
            }
            catch (JsonException)
            {
                // If array form fails, try legacy keyed-object form
                TBLog.Info("TravelConfig.LoadFromFile: array form parse failed, trying legacy format");
            }

            // Try legacy format: { "cities": { "CityName": { ... }, ... } }
            try
            {
                var jObject = Newtonsoft.Json.Linq.JObject.Parse(json);
                var citiesToken = jObject["cities"];
                if (citiesToken != null && citiesToken.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                {
                    var config = new TravelConfig();
                    foreach (var prop in ((Newtonsoft.Json.Linq.JObject)citiesToken).Properties())
                    {
                        var cityName = prop.Name;
                        var cityObj = prop.Value as Newtonsoft.Json.Linq.JObject;
                        if (cityObj != null)
                        {
                            var city = new CityConfig
                            {
                                name = cityName,
                                price = (int)(cityObj["price"] ?? 200),
                                coords = cityObj["coords"]?.ToObject<float[]>(),
                                targetGameObjectName = (string)(cityObj["targetGameObjectName"] ?? ""),
                                sceneName = (string)(cityObj["sceneName"] ?? ""),
                                desc = (string)(cityObj["desc"] ?? "")
                            };
                            config.cities.Add(city);
                        }
                    }
                    TBLog.Info($"TravelConfig.LoadFromFile: loaded {config.cities.Count} cities from {path} (legacy format)");
                    return config;
                }
            }
            catch (Exception legacyEx)
            {
                TBLog.Warn($"TravelConfig.LoadFromFile: legacy format parse failed: {legacyEx.Message}");
            }

            TBLog.Warn($"TravelConfig.LoadFromFile: unable to parse JSON from {path}");
            return null;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TravelConfig.LoadFromFile: exception reading {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Saves TravelConfig to a JSON file in array form.
    /// </summary>
    public static bool SaveToFile(TravelConfig config, string path)
    {
        try
        {
            if (config == null)
            {
                TBLog.Warn("TravelConfig.SaveToFile: config is null");
                return false;
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(path, json);
            TBLog.Info($"TravelConfig.SaveToFile: saved {config.cities?.Count ?? 0} cities to {path}");
            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TravelConfig.SaveToFile: exception writing {path}: {ex.Message}");
            return false;
        }
        catch { }
    }
}

// Legacy ConfigManager retained for backward compatibility
public static class ConfigManager
{
    // NOTE: This is a legacy implementation that's kept for reflection-based callers
    private static readonly string DefaultConfigPath = Path.Combine(Application.dataPath, "Mods", "TravelButton", "config", "TravelButton_Cities.json");

    // Runtime-held config instance (unused in new implementation, kept for compatibility)
    public static object Config { get; set; } = null;

    public static string ConfigPathForLog()
    {
        return DefaultConfigPath;
    }

    // Legacy Load method kept for backward compatibility (unused in new implementation)
    public static void Load()
    {
        try
        {
            TBLog.Info("ConfigManager.Load: legacy method called (new implementation uses TravelConfig.LoadFromFile)");
        }
        catch (Exception ex)
        {
            TBLog.Warn("ConfigManager.Load exception: " + ex);
        }
        catch { }
    }

    // Return a default TravelConfig populated with the exact values you provided.
    public static LegacyTravelConfig Default()
    {
        var t = new LegacyTravelConfig();
        t.enabled = true;
        t.currencyItem = "Silver";
        t.globalTeleportPrice = 100;
        t.cities = new Dictionary<string, LegacyCityConfig>
        {
            { "Cierzo", new LegacyCityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{1410.388f, 6.786f, 1665.642f},
                targetGameObjectName = "",
                sceneName="CierzoNewTerrain",
                desc = "Cierzo - example description",
                visited = false,
                note = "coords required"
            } },
            { "Levant", new LegacyCityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{-55.212f, 10.056f, 79.379f},
                targetGameObjectName = "",
                sceneName="Levant",
                desc = "Levant - example description",
                visited = false,
                note = "coords required"
            } },
            { "Monsoon", new LegacyCityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{57.352f, -3.877f, 113.537f},
                targetGameObjectName = "",
                sceneName="Monsoon",
                desc = "Monsoon - example description",
                visited = false,
                note = "coords required"
            } },
            { "Berg", new LegacyCityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{1202.414f, -13.071f, 1378.836f}, 
                targetGameObjectName = "",
                sceneName="Berg",
                desc = "Berg - example description",
                visited = false,
                note = "coords required"
            } },
            { "Harmattan", new LegacyCityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{93.757f, 65.474f, 767.849f},
                targetGameObjectName = "",
                sceneName="Harmattan",
                desc = "Harmattan - example description",
                visited = false,
                note = "coords required"
            } },
            { "Sirocco", new LegacyCityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{100.0f, 1.2f, 300.0f},
                targetGameObjectName = "",
                sceneName="NewSirocco",
                desc = "Sirocco - example description",
                visited = false,
                note = "coords required"
            } }
        };
        return t;
    }
}
