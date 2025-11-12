using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// add the plugin metadata so BepInEx will load this class
[BepInPlugin("com.xzahalko.travelbutton", "TravelButton", "0.1.0")]
public class TravelButtonMod : BaseUnityPlugin
{
    public static BepInEx.Logging.ManualLogSource LogStatic;
    private Harmony harmony;

    // Config entries
    private ConfigEntry<bool> cfgEnableOverlay;
    private ConfigEntry<KeyCode> cfgToggleKey;
    private ConfigEntry<string> cfgOverlayText;

    // New config: enable actual teleport/payment
    public static ConfigEntry<bool> cfgEnableTeleport;

    // New config: travel cost in silver
    public static ConfigEntry<int> cfgTravelCost;

    // runtime
    private bool showOverlay = false;

    // Cities loaded from JSON
    public class City
    {
        public string name;
        public string targetGameObjectName; // optional: GameObject name to find in scene
        public float[] coords; // optional: { x, y, z }
        public string description; // human-readable description shown in dialog
        public bool visited; // whether player has visited this city
        public bool isCityEnabled = true;
        public override string ToString()
        {
            return $"{name} (obj='{targetGameObjectName ?? ""}' coords={(coords != null ? string.Join(",", coords) : "")} visited={visited})";
        }
    }

    // Make CityContainer public so TravelButtonVisitedManager can use it if desired
    [Serializable]
    public class CityContainer
    {
        public City[] cities;
    }

    public static List<City> Cities = new List<City>();

    private static string CitiesFilePath => Path.Combine(Paths.PluginPath, "TravelButton", "TravelButton_Cities.json");

    // dynamic per-city config entries
    private static Dictionary<string, ConfigEntry<bool>> cityConfigEntries = new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);

    private void Awake()
    {
        LogStatic = this.Logger;
        this.Logger.LogInfo("TravelButtonMod Awake");

        cfgEnableOverlay = this.Config.Bind("General", "EnableOverlay", true, "Show small debug overlay in-game");
        cfgToggleKey = this.Config.Bind("General", "ToggleKey", KeyCode.F10, "Key to toggle overlay visibility");
        cfgOverlayText = this.Config.Bind("General", "OverlayText", "TravelButtonMod active", "Text shown in overlay");

        cfgEnableTeleport = this.Config.Bind("General", "EnableTeleport", true, "If false, travel will not deduct money or teleport the player (UI-only mode)");
        cfgTravelCost = this.Config.Bind("General", "TravelCost", 200, "Cost (in silver coins) to pay for teleporting via the travel UI");

        try
        {
            harmony = new Harmony("com.xzahalko.travelbutton.harmony");
        }
        catch (Exception ex)
        {
            this.Logger.LogError("Failed to create Harmony instance: " + ex);
        }

        // Ensure the TravelButtonUI MonoBehaviour exists
        try
        {
            var existing = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (existing != null)
            {
                this.Logger.LogInfo("TravelButtonUI already present in scene.");
            }
            else
            {
                this.Logger.LogInfo("TravelButtonUI not found - creating GameObject and attaching TravelButtonUI.");
                var go = new GameObject("TravelButtonUI");
                go.AddComponent<TravelButtonUI>();
                UnityEngine.Object.DontDestroyOnLoad(go);
                this.Logger.LogInfo("TravelButtonUI GameObject created and marked DontDestroyOnLoad.");
            }
        }
        catch (Exception ex)
        {
            this.Logger.LogError("Error while ensuring TravelButtonUI exists: " + ex);
        }

        // Load cities data from JSON (robust)
        try
        {
            LoadCities();
            if (Cities == null || Cities.Count == 0)
            {
                WriteDefaultCitiesFile();
                LoadCities(); // reload after writing defaults
            }

            // Load visited flags from JSON
            TravelButtonVisitedManager.Initialize();

            // Merge visited flags & coords into Cities so visited cities appear in dialog
            TravelButtonVisitedManager.MergeVisitedFlagsIntoCities();

            // Create per-city config toggles (persistent)
            CreateCityConfigEntries();

            this.Logger.LogInfo($"Loaded {Cities.Count} cities from {CitiesFilePath}");
            foreach (var c in Cities) this.Logger.LogInfo($" City: {c.name} - desc length: {(c.description ?? "").Length}, visited: {c.visited}");
        }
        catch (Exception ex)
        {
            this.Logger.LogError("Error loading cities: " + ex);
        }

        // Create CityDiscovery component for auto-discovery
        try
        {
            var existingDiscovery = UnityEngine.Object.FindObjectOfType<CityDiscovery>();
            if (existingDiscovery == null)
            {
                this.Logger.LogInfo("Creating CityDiscovery component for auto-discovery.");
                var discoveryGO = new GameObject("CityDiscovery");
                discoveryGO.AddComponent<CityDiscovery>();
                UnityEngine.Object.DontDestroyOnLoad(discoveryGO);
                this.Logger.LogInfo("CityDiscovery GameObject created and marked DontDestroyOnLoad.");
            }
            else
            {
                this.Logger.LogInfo("CityDiscovery already exists in scene.");
            }
        }
        catch (Exception ex)
        {
            this.Logger.LogError("Error creating CityDiscovery: " + ex);
        }
    }

    // Create per-city config entries so each city can be toggled on/off
    private void CreateCityConfigEntries()
    {
        try
        {
            cityConfigEntries.Clear();
            foreach (var city in Cities)
            {
                var safeName = city.name.Replace(' ', '_');
                var cfg = this.Config.Bind("Cities", safeName + ".Enabled", city.isCityEnabled,
                    $"Enable city: {city.name} (set false to hide this destination in the UI)");
                cityConfigEntries[city.name] = cfg;
            }
        }
        catch (Exception ex)
        {
            LogStatic?.LogError("CreateCityConfigEntries failed: " + ex);
        }
    }

    // Public helper used by UI code
    public static bool IsCityEnabled(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return true;

        // If city was visited (persisted or in-memory), always enable it
        try
        {
            if (TravelButtonVisitedManager.IsCityVisited(cityName)) return true;

            // also check in-memory flag in Cities list (in case MarkVisited updated in-memory object but file hasn't been reloaded yet)
            var citiesField = typeof(TravelButtonMod).GetField("Cities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (citiesField != null)
            {
                var cities = citiesField.GetValue(null) as IList<City>;
                if (cities != null)
                {
                    foreach (var c in cities)
                    {
                        if (c != null && string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (c.visited) return true;
                            break;
                        }
                    }
                }
            }
        }
        catch { /* ignore and fall back to config */ }

        // If we have an explicit config entry, respect it for unvisited cities
        if (cityConfigEntries.TryGetValue(cityName, out var cfg))
        {
            try
            {
                return cfg.Value;
            }
            catch
            {
                return true;
            }
        }

        // fallback true if we don't have config entry
        return true;
    }

    // Robust loader: accepts both wrapped { "cities": [...] } and bare JSON arrays.
    public static void LoadCities()
    {
        try
        {
            var dir = Path.GetDirectoryName(CitiesFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(CitiesFilePath))
            {
                WriteDefaultCitiesFile();
            }

            var txt = File.ReadAllText(CitiesFilePath).Trim();
            CityContainer container = null;

            // If file is an array, wrap it
            if (txt.StartsWith("["))
            {
                var wrapped = "{\"cities\":" + txt + "}";
                container = UnityEngine.JsonUtility.FromJson<CityContainer>(wrapped);
            }
            else
            {
                try
                {
                    container = UnityEngine.JsonUtility.FromJson<CityContainer>(txt);
                }
                catch { container = null; }
            }

            // fallback: try wrapping anyway
            if ((container == null || container.cities == null || container.cities.Length == 0) && !txt.StartsWith("["))
            {
                var altWrapped = "{\"cities\":" + txt + "}";
                try
                {
                    container = UnityEngine.JsonUtility.FromJson<CityContainer>(altWrapped);
                }
                catch { container = null; }
            }

            if (container != null && container.cities != null && container.cities.Length > 0)
            {
                Cities = new List<City>(container.cities);
            }
            else
            {
                LogStatic?.LogWarning("LoadCities: no cities parsed from JSON, populating defaults in memory.");
                Cities = DefaultCities();
            }
        }
        catch (Exception ex)
        {
            LogStatic?.LogError("LoadCities failed: " + ex);
            Cities = new List<City>();
        }
    }

    private static void WriteDefaultCitiesFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(CitiesFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var defaults = new CityContainer
            {
                cities = DefaultCities().ToArray()
            };
            var json = UnityEngine.JsonUtility.ToJson(defaults, true);
            File.WriteAllText(CitiesFilePath, json);
            LogStatic?.LogInfo($"Created default cities file at {CitiesFilePath}");
        }
        catch (Exception ex)
        {
            LogStatic?.LogError("WriteDefaultCitiesFile failed: " + ex);
        }
    }

    private static List<City> DefaultCities()
    {
        return new List<City>
        {
            new City {
                name = "Cierzo",
                targetGameObjectName = "",
                description = "The cierzo is a strong, dry and usually cold wind that blows from the North or Northwest through the regions of Aragon, La Rioja and Navarra in the Ebro valley in Spain."
            },
            new City {
                name = "Levant",
                targetGameObjectName = "",
                description = "The levant is an easterly wind that blows in the western Mediterranean Sea and southern France, an example of mountain-gap wind."
            },
            new City {
                name = "Monsoon",
                targetGameObjectName = "",
                description = "A seasonal reversing wind accompanied by corresponding changes in precipitation."
            },
            new City {
                name = "Berg",
                targetGameObjectName = "",
                description = "Berg wind is the South African name for a katabatic wind: a hot dry wind blowing down the Great Escarpment from the high central plateau to the coast."
            },
            new City {
                name = "Harmattan",
                targetGameObjectName = "",
                description = "A dry and dusty northeasterly trade wind, which blows from the Sahara Desert over West Africa into the Gulf of Guinea."
            },
            new City {
                name = "Sirocco",
                targetGameObjectName = "",
                description = "Sirocco is a Mediterranean wind that comes from the Sahara and can reach hurricane speeds in North Africa and Southern Europe, especially during the summer season."
            }
        };
    }

    public static void LogInfo(string message) => LogStatic?.LogInfo(message);
    public static void LogWarning(string message) => LogStatic?.LogWarning(message);
    public static void LogError(string message) => LogStatic?.LogError(message);
    public static void LogDebug(string message) => LogStatic?.LogDebug(message);
}