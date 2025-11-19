using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
//
// TravelButtonVisitedManager
// - Loads/saves visited city names (and optional coordinates) to TravelButton_Visited.json
// - Backwards compatible with previous format (JSON array of names)
// - Exposes MarkVisited(name, Vector3? pos) so callers can persist discovered positions
// - MergeVisitedFlagsIntoCities will apply visited flags and saved coords into TravelButtonMod.Cities
// - Diagnostic helper LogPlayerCandidateVisitedFields for CityDiscovery
//
public static class TravelButtonVisitedManager
{
    [Serializable]
    private class VisitedInfo
    {
        public string name;
        public float[] coords; // optional: [x,y,z]
    }

    [Serializable]
    private class VisitedWrapper
    {
        public List<VisitedInfo> visited = new List<VisitedInfo>();
    }

    // Helper wrapper for legacy string-array parsing via JsonUtility
    [Serializable]
    private class WrapperStrings
    {
        public string[] items;
    }

    private static Dictionary<string, VisitedInfo> visited = new Dictionary<string, VisitedInfo>(StringComparer.OrdinalIgnoreCase);
    private static bool loaded = false;
    private static string visitedFilePath;

    private static void EnsureLoaded()
    {
        if (loaded) return;
        visitedFilePath = GetVisitedFilePath();
        try
        {
            if (!File.Exists(visitedFilePath))
            {
                TBLog.Info($"TravelButtonVisitedManager: No visited file at {visitedFilePath}, starting with empty visited set.");
                visited.Clear();
                loaded = true;
                return;
            }

            var txt = File.ReadAllText(visitedFilePath).Trim();
            if (string.IsNullOrEmpty(txt))
            {
                visited.Clear();
                loaded = true;
                return;
            }

            // Try new object-array format first
            try
            {
                var wrapper = JsonUtility.FromJson<VisitedWrapper>(txt);
                if (wrapper != null && wrapper.visited != null && wrapper.visited.Count > 0)
                {
                    visited.Clear();
                    foreach (var v in wrapper.visited)
                    {
                        if (string.IsNullOrEmpty(v.name)) continue;
                        visited[v.name] = v;
                    }
                    TBLog.Info($"TravelButtonVisitedManager: Loaded {visited.Count} visited entries (with metadata) from {visitedFilePath}.");
                    loaded = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TravelButtonVisitedManager: parse as object-array failed: " + ex);
            }

            // Fallback: try legacy array of strings: wrap and parse
            try
            {
                if (txt.StartsWith("["))
                {
                    var wrapped = "{\"items\":" + txt + "}";
                    var names = JsonUtility.FromJson<WrapperStrings>(wrapped);
                    if (names != null && names.items != null && names.items.Length > 0)
                    {
                        visited.Clear();
                        foreach (var n in names.items)
                        {
                            if (string.IsNullOrEmpty(n)) continue;
                            visited[n] = new VisitedInfo { name = n, coords = null };
                        }
                        TBLog.Info($"TravelButtonVisitedManager: Loaded {visited.Count} legacy visited entries (names-only) from {visitedFilePath}.");
                        loaded = true;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TravelButtonVisitedManager: legacy parse failed: " + ex);
            }

            // Nothing parsed -> empty set
            visited.Clear();
            loaded = true;
            TBLog.Warn($"TravelButtonVisitedManager: No valid visited data parsed from {visitedFilePath}; starting empty.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonVisitedManager.EnsureLoaded failed: " + ex);
            visited.Clear();
            loaded = true;
        }
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
                    {
                        // Avoid creating a nested TravelButton/TravelButton directory if the assembly already sits in a TravelButton folder.
                        string folderName = Path.GetFileName(dir) ?? "";
                        string plugDir = folderName.Equals("TravelButton", StringComparison.OrdinalIgnoreCase)
                            ? dir
                            : Path.Combine(dir, "TravelButton");

                        if (!Directory.Exists(plugDir)) Directory.CreateDirectory(plugDir);
                        return Path.Combine(plugDir, "TravelButton_Visited.json");
                    }
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

    public static void Initialize()
    {
        EnsureLoaded();
    }

    public static bool IsCityVisited(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        EnsureLoaded();
        return visited.ContainsKey(cityName);
    }

    // Mark a city visited; optional position will be stored into visited metadata.
    // Also updates the in-memory TravelButtonMod.Cities entry so UI and other code see the changed state immediately.
    public static void MarkVisited(string cityName, Vector3? position = null)
    {
        if (string.IsNullOrEmpty(cityName)) return;
        EnsureLoaded();
        try
        {
            VisitedInfo info;
            if (!visited.TryGetValue(cityName, out info))
            {
                info = new VisitedInfo { name = cityName, coords = null };
                visited[cityName] = info;
                TBLog.Info($"TravelButtonVisitedManager: Marked visited: {cityName}");
            }
            else
            {
                TravelButtonPlugin.LogDebug($"TravelButtonVisitedManager: MarkVisited for already-visited city {cityName} (may enrich coords).");
            }

            if (position.HasValue)
            {
                var v = position.Value;
                info.coords = new float[] { v.x, v.y, v.z };
                TBLog.Info($"TravelButtonVisitedManager: Stored coords for {cityName} = ({v.x:F2},{v.y:F2},{v.z:F2})");
            }

            // Update in-memory TravelButtonMod.Cities if available so UI/teleport code sees the visit immediately
            try
            {
                var citiesField = typeof(TravelButton).GetField("Cities", BindingFlags.Public | BindingFlags.Static);
                if (citiesField != null)
                {
                    var cities = citiesField.GetValue(null) as IList<TravelButton.City>;
                    if (cities != null)
                    {
                        foreach (var city in cities)
                        {
                            if (city == null) continue;
                            if (string.Equals(city.name, cityName, StringComparison.OrdinalIgnoreCase))
                            {
                                city.visited = true;
                                city.enabled = true;
                                if ((city.coords == null || city.coords.Length < 3) && info.coords != null && info.coords.Length >= 3)
                                {
                                    city.coords = new float[] { info.coords[0], info.coords[1], info.coords[2] };
                                    TBLog.Info($"TravelButtonVisitedManager: Applied saved coords to in-memory city '{city.name}'");
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TravelButtonVisitedManager: failed to update in-memory Cities entry: " + ex);
            }

            Save();
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonVisitedManager.MarkVisited failed: " + ex);
        }
    }

    public static void Save()
    {
        try
        {
            EnsureLoaded();
            var wrapper = new VisitedWrapper
            {
                visited = visited.Values.ToList()
            };

            var dir = Path.GetDirectoryName(GetVisitedFilePath());
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonUtility.ToJson(wrapper, true);
            File.WriteAllText(GetVisitedFilePath(), json);
            TBLog.Info($"TravelButtonVisitedManager: Saved {wrapper.visited.Count} visited entries to {GetVisitedFilePath()}");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonVisitedManager.Save failed: " + ex);
        }
    }

    // Merge persisted visited flags and metadata into the in-memory TravelButtonMod.Cities list.
    // Effects:
    //  - sets city.visited = true for visited cities
    //  - if saved coords exist and the city has no coords, apply saved coords to the city.coords
    //  - ensures visited cities are enabled in the dialog (city.isCityEnabled = true)
    public static void MergeVisitedFlagsIntoCities()
    {
        try
        {
            EnsureLoaded();
            var citiesField = typeof(TravelButton).GetField("Cities", BindingFlags.Public | BindingFlags.Static);
            if (citiesField == null)
            {
                TBLog.Warn("TravelButtonVisitedManager.MergeVisitedFlagsIntoCities: TravelButtonMod.Cities field not found.");
                return;
            }

            var cities = citiesField.GetValue(null) as IList<TravelButton.City>;
            if (cities == null)
            {
                TBLog.Warn("TravelButtonVisitedManager.MergeVisitedFlagsIntoCities: TravelButtonMod.Cities is null.");
                return;
            }

            int applied = 0;
            foreach (var city in cities)
            {
                if (city == null || string.IsNullOrEmpty(city.name)) continue;
                if (visited.TryGetValue(city.name, out var info))
                {
                    city.visited = true;
                    city.enabled = true;
                    if ((city.coords == null || city.coords.Length < 3) && info.coords != null && info.coords.Length >= 3)
                    {
                        city.coords = new float[] { info.coords[0], info.coords[1], info.coords[2] };
                        TBLog.Info($"TravelButtonVisitedManager: Applied saved coords to city '{city.name}'");
                    }
                    applied++;
                }
            }

            TBLog.Info($"TravelButtonVisitedManager: Merged visited flags into Cities list. Applied={applied}");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonVisitedManager.MergeVisitedFlagsIntoCities failed: " + ex);
        }
    }

    // --- Diagnostic helper used by CityDiscovery.Start() ---
    // Scans scene components and logs any fields/properties that look like visited/discovered/region/city flags.
    public static void LogPlayerCandidateVisitedFields()
    {
        try
        {
            TBLog.Info("TravelButtonVisitedManager: Scanning scene for potential visited-like fields/properties...");

            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null || !root.activeInHierarchy) continue;

                // Inspect all components in this root (including inactive)
                var comps = root.GetComponentsInChildren<Component>(true);
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var t = c.GetType();

                    // Check fields
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        string fname = f.Name.ToLowerInvariant();
                        if (fname.Contains("visit") || fname.Contains("discover") || fname.Contains("region") || fname.Contains("city") || fname.Contains("town"))
                        {
                            object val = null;
                            try { val = f.GetValue(c); } catch (Exception ex) { val = $"<err:{ex.Message}>"; }
                            TBLog.Info($"TravelButtonVisitedManager: field: {f.FieldType.Name} {t.Name}.{f.Name} = {SafeToString(val)}");
                        }
                    }

                    // Check properties
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        string pname = p.Name.ToLowerInvariant();
                        if (pname.Contains("visit") || pname.Contains("discover") || pname.Contains("region") || pname.Contains("city") || pname.Contains("town"))
                        {
                            object val = null;
                            try { val = p.GetValue(c, null); } catch (Exception ex) { val = $"<err:{ex.Message}>"; }
                            TBLog.Info($"TravelButtonVisitedManager: prop: {p.PropertyType.Name} {t.Name}.{p.Name} = {SafeToString(val)}");
                        }
                    }
                }
            }

            TBLog.Info("TravelButtonVisitedManager: Scan complete.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TravelButtonVisitedManager.LogPlayerCandidateVisitedFields failed: " + ex);
        }
    }

    private static string SafeToString(object val)
    {
        try
        {
            if (val == null) return "null";
            return val.ToString();
        }
        catch
        {
            return "<err>";
        }
    }
}
