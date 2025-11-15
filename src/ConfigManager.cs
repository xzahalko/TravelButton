using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

// ConfigManager: load/save travel_config.json (per-city config).
// - Creates default config if missing.
// - Fills missing cities (from Default()) for backward compatibility.
// - Exposes the TravelConfig object for other code to use.

public class CityConfig
{
    public bool enabled = false;
    public int? price = null;
    public float[] coords = null;
    public string targetGameObjectName = null;
    public string note = null;
    public string sceneName = null;

    // Optional descriptive fields used by UI and persisted in the JSON file
    public string desc = null;
    public bool visited = false;
}

public class TravelConfig
{
    public bool enabled = true;
    public string currencyItem = "Silver";
    public int globalTeleportPrice = 100;
    public Dictionary<string, CityConfig> cities = new Dictionary<string, CityConfig>();
}

public static class ConfigManager
{
    // NOTE: keep this path in sync with where you place the mod files.
    // Returns a path like "<GameData>/Mods/TravelButton/config/travel_config.json".
    private static readonly string DefaultConfigPath = Path.Combine(Application.dataPath, "Mods", "TravelButton", "config", "TravelButton_Cities.json");

    // Runtime-held config instance
    public static TravelConfig Config { get; set; } = null;

    public static string ConfigPathForLog()
    {
        return DefaultConfigPath;
    }

    // Load config from disk, creating defaults when missing or incomplete.
    public static void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultConfigPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(DefaultConfigPath))
            {
                Config = Default();
                File.WriteAllText(DefaultConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
                Debug.Log("[TravelButton] ConfigManager: created default config at " + DefaultConfigPath);
                return;
            }

            var json = File.ReadAllText(DefaultConfigPath);
            var des = JsonConvert.DeserializeObject<TravelConfig>(json);
            if (des == null) des = Default();

            // fill missing keys with defaults for backward compatibility
            var defaults = Default();
            if (des.cities == null) des.cities = defaults.cities;
            if (string.IsNullOrEmpty(des.currencyItem)) des.currencyItem = defaults.currencyItem;
            if (des.globalTeleportPrice == 0) des.globalTeleportPrice = defaults.globalTeleportPrice;

            // ensure known city entries exist (so config always contains the known city keys)
            foreach (var kv in defaults.cities)
            {
                if (!des.cities.ContainsKey(kv.Key))
                {
                    des.cities[kv.Key] = kv.Value;
                }
                else
                {
                    // fill missing fields inside existing city entries if necessary
                    var target = des.cities[kv.Key];
                    var src = kv.Value;
                    if (target.coords == null && src.coords != null) target.coords = src.coords;
                    if (target.price == null && src.price != null) target.price = src.price;
                    if (string.IsNullOrEmpty(target.targetGameObjectName) && !string.IsNullOrEmpty(src.targetGameObjectName)) target.targetGameObjectName = src.targetGameObjectName;
                    if (string.IsNullOrEmpty(target.desc) && !string.IsNullOrEmpty(src.desc)) target.desc = src.desc;
                    // visited default false is OK - do not override existing visited flags
                }
            }

            Config = des;

            // Write back any defaults that were added
            Save();

            Debug.Log("[TravelButton] ConfigManager: config loaded and defaults applied.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] ConfigManager.Load exception: " + ex);
            Config = Default();
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultConfigPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(Config ?? Default(), Formatting.Indented);
            File.WriteAllText(DefaultConfigPath, json);
            Debug.Log("[TravelButton] ConfigManager: config saved to " + DefaultConfigPath);
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] ConfigManager.Save exception: " + ex);
        }
    }

    // Return a default TravelConfig populated with the exact values you provided.
    public static TravelConfig Default()
    {
        var t = new TravelConfig();
        t.enabled = true;
        t.currencyItem = "Silver";
        t.globalTeleportPrice = 100;
        t.cities = new Dictionary<string, CityConfig>
        {
            { "Cierzo", new CityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{1362.051f, 37.088f, 1652.189f},
                targetGameObjectName = "Cierzo_Location",
                sceneName="CierzoNewTerrain",
                desc = "Cierzo - example description",
                visited = false,
                note = "coords required"
            } },
            { "Levant", new CityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{-573f, 90f, -515f},
                targetGameObjectName = "Levant_Location",
                sceneName="LevantNewTerrain",
                desc = "Levant - example description",
                visited = false,
                note = "coords required"
            } },
            { "Monsoon", new CityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{3f, 1.0f, -5f},
                targetGameObjectName = "Monsoon_Location",
                sceneName="MonsoonNewTerrain",
                desc = "Monsoon - example description",
                visited = false,
                note = "coords required"
            } },
            { "Berg", new CityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{1204.881f, -13.656f, 1372.639f}, 
                targetGameObjectName = "Berg_SafeSpawn",
                sceneName="Berg",
                desc = "Berg - example description",
                visited = false,
                note = "coords required"
            } },
            { "Harmattan", new CityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{500.0f, 3.0f, 80.0f},
                targetGameObjectName = "Harmattan_Location",
                sceneName="HarmattanNewTerrain",
                desc = "Harmattan - example description",
                visited = false,
                note = "coords required"
            } },
            { "Sirocco", new CityConfig {
                enabled = false,
                price = 1,
                coords = new float[]{600.0f, 1.2f, -300.0f},
                targetGameObjectName = "Sirocco_Location",
                sceneName="SiroccoNewTerrain",
                desc = "Sirocco - example description",
                visited = false,
                note = "coords required"
            } }
        };
        return t;
    }
}