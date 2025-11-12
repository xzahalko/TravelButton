using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

// Tracks visited cities and persists to visited.json inside the mod folder.
// This is intentionally simple: stored as an array of city name strings.

public static class VisitedTracker
{
    private static readonly string SavePath = Path.Combine(Application.dataPath, "Mods", "TravelButton", "config", "visited.json");
    private static HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static VisitedTracker()
    {
        Load();
    }

    public static bool HasVisited(string city)
    {
        if (string.IsNullOrEmpty(city)) return false;
        return visited.Contains(city);
    }

    public static void MarkVisited(string city)
    {
        if (string.IsNullOrEmpty(city)) return;
        if (visited.Add(city))
        {
            Save();
            Debug.Log($"[TravelButton] VisitedTracker: marked visited: {city}");
        }
    }

    public static void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(SavePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(SavePath))
            {
                Save(); // creates an empty file
                return;
            }

            var json = File.ReadAllText(SavePath);
            var list = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
            visited = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            Debug.Log($"[TravelButton] VisitedTracker: loaded {visited.Count} visited entries from {SavePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] VisitedTracker.Load exception: " + ex);
            visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SavePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var list = new List<string>(visited);
            File.WriteAllText(SavePath, JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented));
            Debug.Log("[TravelButton] VisitedTracker: saved visited list.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] VisitedTracker.Save exception: " + ex);
        }
    }
}