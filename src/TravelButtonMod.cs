using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// TravelButtonMod - central mod runtime helper.
/// Responsible for:
/// - exposing config-backed values (cfgTravelCost, cfgEnableTeleport, cfgEnableMod)
/// - exposing Cities list (built from config/travel_config.json)
/// - helper IsCityEnabled(name)
/// - syncing runtime city data back to config when modified
///
/// NOTE: This is intentionally conservative: it reads the config produced by ConfigManager (travel_config.json)
/// and maps each city entry into a TravelButtonMod.City instance which the UI code expects.
/// </summary>
public static class TravelButtonMod
{
    // ---- Logging helpers (added to satisfy TravelButtonUI references) ----
    public static void LogInfo(string message)
    {
        Debug.Log($"[TravelButton][INFO] {message}");
    }

    public static void LogWarning(string message)
    {
        Debug.LogWarning($"[TravelButton][WARN] {message}");
    }

    public static void LogError(string message)
    {
        Debug.LogError($"[TravelButton][ERROR] {message}");
    }

    public static void LogDebug(string message)
    {
        // Keep debug logs optional; you can toggle this behavior later.
        Debug.Log($"[TravelButton][DEBUG] {message}");
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

        public City(string name)
        {
            this.name = name;
            this.coords = null;
            this.targetGameObjectName = null;
            this.price = null;
            this.enabled = false;
        }

        // Compatibility properties expected by older code:
        // property 'visited' (lowercase) — maps to VisitedTracker
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
    public static List<City> Cities { get; private set; } = new List<City>();

    // Path/filename helpers exposed for debugging
    public static string ConfigFilePath => ConfigManager.ConfigPathForLog();

    // Initialize mod state from JSON config -> should be called once at mod load
    public static void InitFromConfig()
    {
        try
        {
            ConfigManager.Load(); // ensure config is loaded and defaults are applied
            var cfg = ConfigManager.Config;
            if (cfg == null)
            {
                Debug.LogWarning("[TravelButtonMod] InitFromConfig: ConfigManager.Config is null; using defaults.");
                cfg = TravelConfig.Default();
            }

            // populate wrapper entries
            cfgEnableMod.Value = cfg.enabled;
            cfgCurrencyItem.Value = string.IsNullOrEmpty(cfg.currencyItem) ? "Silver" : cfg.currencyItem;
            cfgTravelCost.Value = cfg.globalTeleportPrice == 0 ? 100 : cfg.globalTeleportPrice;

            // by default allow teleport (cfg field may be extended in config later)
            cfgEnableTeleport.Value = true;

            // Build Cities list from cfg.cities dictionary. Preserve iteration order from config where available.
            Cities.Clear();
            if (cfg.cities != null)
            {
                foreach (var kv in cfg.cities)
                {
                    var cname = kv.Key;
                    var cityCfg = kv.Value ?? new CityConfig();

                    var city = new City(cname);
                    city.enabled = cityCfg.enabled;
                    city.price = cityCfg.price;
                    if (cityCfg.coords != null && cityCfg.coords.Length == 3)
                    {
                        city.coords = new float[3] { cityCfg.coords[0], cityCfg.coords[1], cityCfg.coords[2] };
                    }
                    else
                    {
                        city.coords = null;
                    }

                    // If the JSON had a note or gameObject name, allow targetGameObjectName (compatibility)
                    if (!string.IsNullOrEmpty(cityCfg.targetGameObjectName))
                    {
                        city.targetGameObjectName = cityCfg.targetGameObjectName;
                    }
                    else
                    {
                        city.targetGameObjectName = null;
                    }

                    Cities.Add(city);
                }
            }
            else
            {
                Debug.LogWarning("[TravelButtonMod] InitFromConfig: no cfg.cities found; Cities list will be empty.");
            }

            Debug.Log($"[TravelButtonMod] InitFromConfig: Loaded {Cities.Count} cities from config ({ConfigFilePath}).");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButtonMod] InitFromConfig exception: " + ex);
        }
    }

    // Query if a city is enabled in config (does not consider visited state)
    public static bool IsCityEnabled(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        var cfg = ConfigManager.Config;
        if (cfg == null || cfg.cities == null) return false;
        if (cfg.cities.TryGetValue(cityName, out CityConfig c))
        {
            return c.enabled;
        }
        return false;
    }

    // Update in-memory Cities -> config and save; useful if user toggles a city via UI/editor
    public static void PersistCitiesToConfig()
    {
        try
        {
            var cfg = ConfigManager.Config ?? TravelConfig.Default();
            if (cfg.cities == null) cfg.cities = new Dictionary<string, CityConfig>();

            foreach (var city in Cities)
            {
                CityConfig cc = null;
                if (cfg.cities.ContainsKey(city.name))
                {
                    cc = cfg.cities[city.name];
                }
                else
                {
                    cc = new CityConfig();
                    cfg.cities[city.name] = cc;
                }

                cc.enabled = city.enabled;
                cc.price = city.price;
                cc.coords = city.coords;
                // preserve note / targetGameObjectName if present in cc
                cc.targetGameObjectName = city.targetGameObjectName ?? cc.targetGameObjectName;
            }

            // Persist global values too
            cfg.enabled = cfgEnableMod.Value;
            cfg.currencyItem = cfgCurrencyItem.Value;
            cfg.globalTeleportPrice = cfgTravelCost.Value;

            ConfigManager.Config = cfg;
            ConfigManager.Save();
            Debug.Log("[TravelButtonMod] PersistCitiesToConfig: saved cities/config.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButtonMod] PersistCitiesToConfig exception: " + ex);
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
            // optionally persist any runtime changes (e.g., visited flags are separate, we persist cities only if changed)
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButtonMod] OnSuccessfulTeleport exception: " + ex);
        }
    }
}