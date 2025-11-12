using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

// ConfigManager: load/save travel_config.json (per-city config).
// This version is intentionally simple and robust: it creates default config if missing,
// fills missing cities (from TravelConfig.Default) and exposes the TravelConfig object for other code to use.

public static class ConfigManager
{
    // NOTE: keep this path in sync with where you place the mod files.
    // This returns a path like "<GameData>/Mods/TravelButton/config/travel_config.json".
    private static readonly string DefaultConfigPath = Path.Combine(Application.dataPath, "Mods", "TravelButton", "config", "travel_config.json");
    public static TravelConfig Config { get; set; } = null;

    public static string ConfigPathForLog()
    {
        return DefaultConfigPath;
    }

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
                Config = TravelConfig.Default();
                File.WriteAllText(DefaultConfigPath, JsonConvert.SerializeObject(Config, Newtonsoft.Json.Formatting.Indented));
                Debug.Log("[TravelButton] ConfigManager: created default config at " + DefaultConfigPath);
                return;
            }

            var json = File.ReadAllText(DefaultConfigPath);
            var des = JsonConvert.DeserializeObject<TravelConfig>(json);
            if (des == null) des = TravelConfig.Default();

            // fill missing keys with defaults for backward compatibility
            var defaults = TravelConfig.Default();
            if (des.cities == null) des.cities = defaults.cities;
            if (string.IsNullOrEmpty(des.currencyItem)) des.currencyItem = defaults.currencyItem;
            if (des.globalTeleportPrice == 0) des.globalTeleportPrice = defaults.globalTeleportPrice;

            // ensure default wind-named entries exist (so config always contains the known city keys)
            foreach (var kv in defaults.cities)
            {
                if (!des.cities.ContainsKey(kv.Key))
                {
                    des.cities[kv.Key] = kv.Value;
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
            Config = TravelConfig.Default();
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultConfigPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(Config ?? TravelConfig.Default(), Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(DefaultConfigPath, json);
            Debug.Log("[TravelButton] ConfigManager: config saved to " + DefaultConfigPath);
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] ConfigManager.Save exception: " + ex);
        }
    }
}

// Serializable config mapping matching config/travel_config.json
[Serializable]
public class TravelConfig
{
    public bool enabled = true;
    public string currencyItem = "Silver";
    public int globalTeleportPrice = 100;
    public Dictionary<string, CityConfig> cities = new Dictionary<string, CityConfig>();

    public static TravelConfig Default()
    {
        var t = new TravelConfig();
        t.enabled = true;
        t.currencyItem = "Silver";
        t.globalTeleportPrice = 100;
        t.cities = new Dictionary<string, CityConfig>
        {
            { "Cierzo", new CityConfig { enabled = false, price = null, coords = null, note = "coords required" } },
            { "Levant", new CityConfig { enabled = false, price = null, coords = null, note = "coords required" } },
            { "Monsoon", new CityConfig { enabled = false, price = null, coords = null, note = "coords required" } },
            { "Berg", new CityConfig { enabled = false, price = null, coords = null, note = "coords required" } },
            { "Harmattan", new CityConfig { enabled = false, price = null, coords = null, note = "coords required" } },
            { "Sirocco", new CityConfig { enabled = false, price = null, coords = null, note = "coords required" } }
        };
        return t;
    }
}

[Serializable]
public class CityConfig
{
    public bool enabled = false;
    public int? price = null;
    // coords array [x,y,z] or null
    public float[] coords = null;
    public string note = null;
    // optional runtime helper: name of a GameObject to find instead of coordinates
    public string targetGameObjectName = null;
}