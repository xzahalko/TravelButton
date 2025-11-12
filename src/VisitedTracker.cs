using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

// Tracks visited cities. Persists to visited.json in mod config folder.
public class VisitedTracker
{
    private static readonly string SavePath = Path.Combine(Application.dataPath, "Mods", "TravelButton", "visited.json");
    private static HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static VisitedTracker()
    {
        Load();
    }

    public static bool HasVisited(string city)
    {
        return visited.Contains(city);
    }

    public static void MarkVisited(string city)
    {
        if (string.IsNullOrEmpty(city)) return;
        if (visited.Add(city))
        {
            Save();
            Debug.Log($"[TravelButton] Marked visited: {city}");
        }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SavePath))
            {
                Save(); // writes empty file
                return;
            }
            var json = File.ReadAllText(SavePath);
            var list = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
            visited = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] Failed to load visited data: " + ex);
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
            File.WriteAllText(SavePath, JsonConvert.SerializeObject(list, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] Failed to save visited data: " + ex);
        }
    }
}