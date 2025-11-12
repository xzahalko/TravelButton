using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

// Simple config manager for TravelButton mod.
// Reads config/travel_config.json, ensures defaults, and exposes config to other classes.
public class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(Application.dataPath, "Mods", "TravelButton", "config", "travel_config.json");
    public static TravelConfig Config { get; private set; }

    static ConfigManager()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                // write default
                Config = TravelConfig.Default();
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
                Debug.Log("[TravelButton] Created default config at " + ConfigPath);
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            Config = JsonConvert.DeserializeObject<TravelConfig>(json) ?? TravelConfig.Default();

            // Fill missing keys with defaults (backwards compatibility)
            var defaultConfig = TravelConfig.Default();
            if (Config.cities == null) Config.cities = defaultConfig.cities;
            if (string.IsNullOrEmpty(Config.currencyItem)) Config.currencyItem = defaultConfig.currencyItem;
            if (Config.globalTeleportPrice == 0) Config.globalTeleportPrice = defaultConfig.globalTeleportPrice;
            // ensure entries exist for default winds if missing
            foreach (var kv in defaultConfig.cities)
            {
                if (!Config.cities.ContainsKey(kv.Key)) Config.cities[kv.Key] = kv.Value;
            }

            Save(); // write back any added defaults
            Debug.Log("[TravelButton] Config loaded and defaults applied.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] Failed to load config: " + ex);
            Config = TravelConfig.Default();
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
            Debug.Log("[TravelButton] Config saved.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] Failed to save config: " + ex);
        }
    }
}

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
            { "Cierzo", new CityConfig { enabled = false } },
            { "Levant", new CityConfig { enabled = false } },
            { "Monsoon", new CityConfig { enabled = false } },
            { "Berg", new CityConfig { enabled = false } },
            { "Harmattan", new CityConfig { enabled = false } },
            { "Sirocco", new CityConfig { enabled = false } }
        };
        return t;
    }
}

[Serializable]
public class CityConfig
{
    public bool enabled = false;
    public int? price = null;
    // coords: [x,y,z] or null
    public float[] coords = null;
    public string note = null;
}