using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

    // Try several candidate locations for the TravelButton_Cities.json and load the first valid one.
    // Try several candidate locations for the TravelButton_Cities.json and load the first valid one.
    private void InitCities()
    {
        // Candidate paths (adjust/extend if you use different locations)
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
        // Use Path.Combine to build platform-correct paths
        try
        {
            // ../BepInEx/config/TravelButton_Cities.json (game root -> BepInEx/config)
            candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BepInEx", "config", "TravelButton_Cities.json")));
        }
        catch { }

        try
        {
            // Game folder root (next to Assets)
            candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "TravelButton_Cities.json")));
        }
        catch { }

        try
        {
            // one level up from game folder (some installs)
            candidates.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TravelButton_Cities.json")));
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

                var list = LoadCitiesFromJson(full);
                if (list != null && list.Count > 0)
                {
                    loadedCities = list;
                    TBLog.Info($"InitCities: loaded {loadedCities.Count} cities from: {full}");
                    // Debug dump of first few entries (optional)
                    for (int i = 0; i < Math.Min(loadedCities.Count, 10); i++)
                    {
                        var c = loadedCities[i];
                        TBLog.Info($"  city[{i}] name='{c?.name}' scene='{c?.sceneName}' coords={(c?.coords == null ? "null" : string.Join(",", c.coords))}");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("InitCities: error while trying candidate path '" + path + "': " + ex);
            }
        }

        TBLog.Warn("InitCities: no valid TravelButton_Cities.json found in candidate locations; loadedCities is null or empty.");
    }

    // Helper: expose loadedCities to other code if needed
    private IReadOnlyList<CityEntry> GetLoadedCities()
    {
        return loadedCities;
    }

    // Example usage:
    // In your dialog-open code, after you have the contentParent and buttonPrefab:
    // var cities = GetLoadedCities();
    // if (cities != null) PopulateCityButtons(contentParentTransform, cities, buttonPrefab);
}