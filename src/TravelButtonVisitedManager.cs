using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Tracks visited cities and persists them to TravelButton_Visited.json placed next to the plugin DLL.
/// Provides public initialization and a helper to merge persisted visited flags into the in-memory Cities list.
/// </summary>
public static class TravelButtonVisitedManager
{
    private static HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool loaded = false;
    private static string visitedFilePath;

    [Serializable]
    private class VisitedWrapper
    {
        public List<string> visitedCities = new List<string>();
    }

    // Internal loader (kept private).
    private static void EnsureLoaded()
    {
        if (loaded) return;
        visitedFilePath = GetVisitedFilePath();
        try
        {
            if (File.Exists(visitedFilePath))
            {
                string json = File.ReadAllText(visitedFilePath);
                var wrapper = JsonUtility.FromJson<VisitedWrapper>(json);
                if (wrapper != null && wrapper.visitedCities != null)
                    visited = new HashSet<string>(wrapper.visitedCities, StringComparer.OrdinalIgnoreCase);
                else
                    visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                TravelButtonMod.LogInfo($"TravelButtonVisitedManager: Loaded {visited.Count} visited entries from {visitedFilePath}");
            }
            else
            {
                TravelButtonMod.LogInfo($"TravelButtonVisitedManager: No visited file at {visitedFilePath}, starting with empty visited set.");
                visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("TravelButtonVisitedManager: Failed to load visited file: " + ex);
            visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        loaded = true;
    }

    // Public initialization wrapper for other classes to call safely.
    public static void Initialize()
    {
        EnsureLoaded();
    }

    private static string GetVisitedFilePath()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            if (asm != null)
            {
                string asmPath = asm.Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    string dir = Path.GetDirectoryName(asmPath);
                    if (!string.IsNullOrEmpty(dir))
                        return Path.Combine(dir, "TravelButton_Visited.json");
                }
            }
        }
        catch { }

        // Fallback: put under roaming BepInEx plugins
        try
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming, "BepInEx", "plugins", "TravelButton_Visited.json");
        }
        catch
        {
            return Path.Combine(".", "TravelButton_Visited.json");
        }
    }

    public static bool IsCityVisited(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        EnsureLoaded();
        return visited.Contains(cityName);
    }

    public static void MarkVisited(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return;
        EnsureLoaded();
        if (visited.Add(cityName))
        {
            TravelButtonMod.LogInfo($"TravelButtonVisitedManager: Marked visited: {cityName}");
            Save();
        }
    }

    public static void Save()
    {
        EnsureLoaded();
        try
        {
            var wrapper = new VisitedWrapper() { visitedCities = new List<string>(visited) };
            string json = JsonUtility.ToJson(wrapper, true);
            File.WriteAllText(visitedFilePath, json);
            TravelButtonMod.LogInfo($"TravelButtonVisitedManager: Saved {visited.Count} visited entries to {visitedFilePath}");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("TravelButtonVisitedManager: Failed to save visited file: " + ex);
        }
    }

    /// <summary>
    /// Merge persisted visited flags into the in-memory TravelButtonMod.Cities list.
    /// After loading cities from TravelButton_Cities.json, call this to apply persisted visit state.
    /// </summary>
    public static void MergeVisitedFlagsIntoCities()
    {
        try
        {
            EnsureLoaded();
            // Try to get the Cities list from TravelButtonMod
            var citiesField = typeof(TravelButtonMod).GetField("Cities", BindingFlags.Public | BindingFlags.Static);
            IList<TravelButtonMod.City> cities = null;
            if (citiesField != null)
            {
                cities = citiesField.GetValue(null) as IList<TravelButtonMod.City>;
            }
            else
            {
                var prop = typeof(TravelButtonMod).GetProperty("Cities", BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                    cities = prop.GetValue(null, null) as IList<TravelButtonMod.City>;
            }

            if (cities == null)
            {
                TravelButtonMod.LogWarning("TravelButtonVisitedManager: MergeVisitedFlagsIntoCities - could not find TravelButtonMod.Cities.");
                return;
            }

            int applied = 0;
            foreach (var c in cities)
            {
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                bool persisted = IsCityVisited(c.name);
                if (persisted && !c.visited)
                {
                    c.visited = true;
                    applied++;
                }
            }

            TravelButtonMod.LogInfo($"TravelButtonVisitedManager: Merged visited flags into Cities list. Applied={applied}");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("TravelButtonVisitedManager: MergeVisitedFlagsIntoCities failed: " + ex);
        }
    }

    // PUBLIC diagnostic helper: logs candidate fields/properties on player components.
    public static void LogPlayerCandidateVisitedFields()
    {
        try
        {
            var player = GameObject.FindWithTag("Player");
            if (player == null)
            {
                TravelButtonMod.LogWarning("LogPlayerCandidateVisitedFields: Player not found (tag 'Player').");
                return;
            }

            var comps = player.GetComponents<Component>();
            TravelButtonMod.LogInfo($"LogPlayerCandidateVisitedFields: inspecting {comps.Length} components:");
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();
                TravelButtonMod.LogInfo($"  Component: {t.FullName}");
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    string fname = f.Name.ToLowerInvariant();
                    if (fname.Contains("visit") || fname.Contains("discover") || fname.Contains("region") || fname.Contains("city") || fname.Contains("town"))
                    {
                        object val = null;
                        try { val = f.GetValue(c); } catch (Exception ex) { val = $"<err:{ex.Message}>"; }
                        TravelButtonMod.LogInfo($"    field: {f.FieldType.Name} {f.Name} = {SafeToString(val)}");
                    }
                }
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    string pname = p.Name.ToLowerInvariant();
                    if (pname.Contains("visit") || pname.Contains("discover") || pname.Contains("region") || pname.Contains("city") || pname.Contains("town"))
                    {
                        object val = null;
                        try { val = p.GetValue(c, null); } catch (Exception ex) { val = $"<err:{ex.Message}>"; }
                        TravelButtonMod.LogInfo($"    prop: {p.PropertyType.Name} {p.Name} = {SafeToString(val)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("LogPlayerCandidateVisitedFields: failed: " + ex);
        }
    }

    private static string SafeToString(object obj)
    {
        if (obj == null) return "null";
        try
        {
            if (obj is System.Collections.IEnumerable en && !(obj is string))
            {
                int i = 0;
                var sb = new System.Text.StringBuilder();
                sb.Append("[");
                foreach (var v in en)
                {
                    if (i++ > 0) sb.Append(", ");
                    if (i > 20) { sb.Append("..."); break; }
                    sb.Append(v?.ToString() ?? "null");
                }
                sb.Append("]");
                return sb.ToString();
            }
            return obj.ToString();
        }
        catch { return "<tostr-error>"; }
    }
}