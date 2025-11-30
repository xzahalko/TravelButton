using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// VisitedStore - a small, conservative canonical visited-state persistence helper.
/// - Stores a simple list of visited city names in JSON at Application.persistentDataPath/TravelButton_visited.json
/// - Provides HasVisited, MarkVisited, GetAllVisited, ClearVisited, and Save/Load semantics.
/// - Designed to be used by VisitedTracker and TravelButtonVisitedManager as a single canonical backend.
/// - Very conservative: does not alter format of existing trackers; this is a new canonical store to be adopted by adapters.
/// </summary>
public static class VisitedStore
{
    private static readonly object Sync = new object();
    private static HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool loaded = false;

    // File name (conservative default). Kept short and descriptive to avoid colliding with other mods.
    private const string FileName = "TravelButton_visited.json";

    // Full file path
    public static string FilePath
    {
        get
        {
            try
            {
                return Path.Combine(Application.persistentDataPath ?? ".", FileName);
            }
            catch
            {
                return FileName;
            }
        }
    }

    /// <summary>
    /// Ensure the store is loaded (lazy).
    /// </summary>
    private static void EnsureLoaded()
    {
        if (loaded) return;
        lock (Sync)
        {
            if (loaded) return;
            LoadFromDisk();
            loaded = true;
        }
    }

    /// <summary>
    /// Returns true if the given city name has been marked visited.
    /// Null/empty names return false.
    /// </summary>
    public static bool HasVisited(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        try
        {
            EnsureLoaded();
            lock (Sync)
            {
                return visited.Contains(cityName);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("VisitedStore.HasVisited exception: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Marks a city visited and persists to disk. Returns true if the value changed.
    /// </summary>
    public static bool MarkVisited(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        try
        {
            EnsureLoaded();
            lock (Sync)
            {
                if (visited.Contains(cityName)) return false;
                visited.Add(cityName);
                SaveToDisk();
                return true;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("VisitedStore.MarkVisited exception: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Returns a copy of all visited city names.
    /// </summary>
    public static List<string> GetAllVisited()
    {
        EnsureLoaded();
        lock (Sync)
        {
            return new List<string>(visited);
        }
    }

    /// <summary>
    /// Clear visited list and persist.
    /// </summary>
    public static void ClearVisited()
    {
        try
        {
            EnsureLoaded();
            lock (Sync)
            {
                visited.Clear();
                SaveToDisk();
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("VisitedStore.ClearVisited exception: " + ex.Message);
        }
    }

    /// <summary>
    /// Load visited set from disk. If file missing or parse fails, treat as empty.
    /// </summary>
    private static void LoadFromDisk()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                TBLog.Info($"VisitedStore: no visited file found at '{path}'. Starting with empty set.");
                return;
            }

            var txt = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt))
            {
                visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                TBLog.Info($"VisitedStore: visited file '{path}' empty -> starting with empty set.");
                return;
            }

            // Use Unity's JsonUtility with a simple wrapper type to deserialize an array.
            try
            {
                var wrapper = JsonUtility.FromJson<VisitedWrapper>(txt);
                if (wrapper != null && wrapper.visited != null)
                {
                    visited = new HashSet<string>(wrapper.visited, StringComparer.OrdinalIgnoreCase);
                    TBLog.Info($"VisitedStore: loaded {visited.Count} visited entries from '{path}'.");
                }
                else
                {
                    visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    TBLog.Warn($"VisitedStore: visited file '{path}' parsed to null wrapper; starting empty.");
                }
            }
            catch (Exception exJson)
            {
                // Fall back to attempting to parse a raw JSON array using a minimal approach
                TBLog.Warn("VisitedStore: JsonUtility.FromJson failed: " + exJson.Message + " - attempting minimal parse.");
                try
                {
                    // Attempt to parse as ["a","b",...] by trimming brackets and splitting commas conservatively
                    var trimmed = txt.Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        var list = new List<string>();
                        if (trimmed.Length > 0)
                        {
                            // split on commas but strip quotes and spaces
                            var parts = trimmed.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var p in parts)
                            {
                                var s = p.Trim().Trim('"').Trim();
                                if (!string.IsNullOrEmpty(s)) list.Add(s);
                            }
                        }
                        visited = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                        TBLog.Info($"VisitedStore: minimal parse loaded {visited.Count} entries.");
                    }
                    else
                    {
                        visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        TBLog.Warn("VisitedStore: visited file did not look like an array - starting empty.");
                    }
                }
                catch (Exception ex2)
                {
                    visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    TBLog.Warn("VisitedStore: fallback parse failed: " + ex2.Message);
                }
            }
        }
        catch (Exception ex)
        {
            visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TBLog.Warn("VisitedStore.LoadFromDisk exception: " + ex.Message);
        }
    }

    /// <summary>
    /// Persist visited set to disk (overwrites existing file).
    /// </summary>
    private static void SaveToDisk()
    {
        try
        {
            var wrapper = new VisitedWrapper() { visited = new List<string>(visited) };
            var json = JsonUtility.ToJson(wrapper, true);
            var path = FilePath;

            // Ensure directory exists
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { /* ignore directory creation errors - write may still fail and be handled below */ }

            File.WriteAllText(path, json);
            TBLog.Info($"VisitedStore: saved {visited.Count} visited entries to '{path}'.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("VisitedStore.SaveToDisk exception: " + ex.Message);
        }
    }

    [Serializable]
    private class VisitedWrapper
    {
        public List<string> visited;
    }
}
