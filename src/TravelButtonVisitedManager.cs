using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Manages the visited state of cities. Loads and saves visited flags to TravelButton_Cities.json
/// in the plugin directory. Uses city name as the key for tracking visited state.
/// </summary>
public static class TravelButtonVisitedManager
{
    private static HashSet<string> visitedCities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool loaded = false;

    /// <summary>
    /// Ensures the visited cities data has been loaded from disk.
    /// Safe to call multiple times - only loads once.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (loaded) return;

        try
        {
            TravelButtonMod.LogInfo("TravelButtonVisitedManager.EnsureLoaded: loading visited cities from JSON.");
            
            string filePath = GetCitiesFilePath();
            if (!File.Exists(filePath))
            {
                TravelButtonMod.LogInfo("TravelButtonVisitedManager: Cities file does not exist yet, starting with empty visited set.");
                loaded = true;
                return;
            }

            // Read the JSON file
            string json = File.ReadAllText(filePath);
            
            // Parse using JsonUtility (handle both array format and wrapped format)
            TravelButtonMod.CityContainer container = null;
            
            if (json.TrimStart().StartsWith("["))
            {
                // Wrap array format
                string wrapped = "{\"cities\":" + json + "}";
                container = JsonUtility.FromJson<TravelButtonMod.CityContainer>(wrapped);
            }
            else
            {
                container = JsonUtility.FromJson<TravelButtonMod.CityContainer>(json);
            }

            if (container != null && container.cities != null)
            {
                visitedCities.Clear();
                foreach (var city in container.cities)
                {
                    if (city.visited && !string.IsNullOrEmpty(city.name))
                    {
                        visitedCities.Add(city.name);
                        TravelButtonMod.LogInfo($"TravelButtonVisitedManager: Loaded visited flag for city '{city.name}'");
                    }
                }
                TravelButtonMod.LogInfo($"TravelButtonVisitedManager: Loaded {visitedCities.Count} visited cities.");
            }
            
            loaded = true;
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError($"TravelButtonVisitedManager.EnsureLoaded failed: {ex}");
            loaded = true; // Mark as loaded even on error to avoid retry loops
        }
    }

    /// <summary>
    /// Check if a city has been visited by the player.
    /// </summary>
    /// <param name="cityName">Name of the city to check</param>
    /// <returns>True if visited, false otherwise</returns>
    public static bool IsCityVisited(string cityName)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(cityName)) return false;
        return visitedCities.Contains(cityName);
    }

    /// <summary>
    /// Mark a city as visited and save to disk immediately.
    /// </summary>
    /// <param name="cityName">Name of the city to mark as visited</param>
    public static void MarkVisited(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return;
        
        EnsureLoaded();
        
        if (visitedCities.Contains(cityName))
        {
            TravelButtonMod.LogInfo($"TravelButtonVisitedManager.MarkVisited: '{cityName}' already marked as visited.");
            return;
        }
        
        visitedCities.Add(cityName);
        TravelButtonMod.LogInfo($"TravelButtonVisitedManager.MarkVisited: marked '{cityName}' as visited.");
        
        // Immediately save to disk
        Save();
    }

    /// <summary>
    /// Save the current visited state back to the JSON file.
    /// Updates the visited flags in the in-memory Cities list and writes to disk.
    /// </summary>
    public static void Save()
    {
        try
        {
            // Merge visited flags into TravelButtonMod.Cities
            MergeVisitedFlagsIntoCities();
            
            string filePath = GetCitiesFilePath();
            
            // Create container for serialization
            var container = new TravelButtonMod.CityContainer
            {
                cities = TravelButtonMod.Cities.ToArray()
            };
            
            string json = JsonUtility.ToJson(container, true);
            
            // Ensure directory exists
            string directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            File.WriteAllText(filePath, json);
            TravelButtonMod.LogInfo($"TravelButtonVisitedManager.Save: saved visited cities to {filePath}");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError($"TravelButtonVisitedManager.Save failed: {ex}");
        }
    }

    /// <summary>
    /// Merge the in-memory visited flags into the TravelButtonMod.Cities list.
    /// Updates each city's visited field based on whether it's in the visitedCities set.
    /// </summary>
    public static void MergeVisitedFlagsIntoCities()
    {
        if (TravelButtonMod.Cities == null) return;
        
        foreach (var city in TravelButtonMod.Cities)
        {
            if (!string.IsNullOrEmpty(city.name))
            {
                city.visited = visitedCities.Contains(city.name);
            }
        }
        
        TravelButtonMod.LogInfo("TravelButtonVisitedManager.MergeVisitedFlagsIntoCities: merged visited flags into Cities list.");
    }

    /// <summary>
    /// Get the file path for the cities JSON file.
    /// Uses the plugin directory (next to the DLL) to ensure runtime plugin reads/writes in the correct location.
    /// </summary>
    /// <returns>Full path to TravelButton_Cities.json</returns>
    public static string GetCitiesFilePath()
    {
        // Use BepInEx PluginPath which points to BepInEx/plugins
        // This matches the path used in TravelButtonMod.cs
        return Path.Combine(BepInEx.Paths.PluginPath, "TravelButton", "TravelButton_Cities.json");
    }
}
