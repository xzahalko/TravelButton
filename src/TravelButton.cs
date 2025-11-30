using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq;
using System.Reflection;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static TravelButton;
using static TravelButtonUI;

//
// TravelButtonMod.cs
// - BepInEx plugin bootstrap (TravelButtonPlugin) + runtime static helpers (TravelButtonMod).
// - Integrates with an optional external ConfigManager (safely, via reflection) and with BepInEx config system
//   so Configuration Manager displays editable settings.
// - Provides City model used by TravelButtonUI and helpers to map/persist configuration.
// - Adds diagnostics helpers DumpTravelButtonState and ForceShowTravelButton for runtime inspection.
//
public static class TravelButton
{
    // cached visited keys extracted from save root (raw strings)
//    private static HashSet<string> s_visitedKeysSet = null;
    // whether the saved-key set appears useless (empty or only generic entries)
    private static bool s_visitedSetUninformative = true;
    // mark if we've prepared the lookup for current save
    private static bool s_visitedLookupPrepared = false;

    public static bool TeleportInProgress = false;

    // Cached raw visited keys extracted from save data (lazy-init)
    private static HashSet<string> s_visitedKeysSet = null;
    // One-time init guard for visited keys
    private static bool s_visitedKeysSetInitialized = false;
    // Lock used during visited-key initialization
    private static readonly object s_visitedKeysInitLock = new object();
    private static HashSet<string> _visitedLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool _visitedLookupPrepared = false;

    public static void LogLoadedScenesAndRootObjects()
    {
        try
        {
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            TBLog.Info($"LogLoadedScenesAndRootObjects: {sceneCount} loaded scene(s).");
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.IsValid()) continue;
                TBLog.Info($" Scene #{i}: name='{scene.name}', isLoaded={scene.isLoaded}, isDirty={scene.isDirty}");
                var roots = scene.GetRootGameObjects();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    TBLog.Info($"  root: '{r.name}' (children count approx: {r.transform.childCount})");
                }
            }
        }
        catch (Exception ex) { TBLog.Warn("LogLoadedScenesAndRootObjects exception: " + ex.Message); }
    }

    public static void LogCityAnchorsFromLoadedScenes()
    {
        try
        {
            if (Cities == null || Cities.Count == 0)
            {
                TBLog.Warn("LogCityAnchorsFromLoadedScenes: no cities available.");
                return;
            }

            TBLog.Info($"LogCityAnchorsFromLoadedScenes: scanning {UnityEngine.SceneManagement.SceneManager.sceneCount} loaded scene(s) for city anchors...");

            // For each loaded scene, scan root objects and children once and build a lookup of names -> (scene, transform)
            var lookup = new Dictionary<string, List<(string sceneName, Transform t)>>(StringComparer.OrdinalIgnoreCase);

            int scCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int si = 0; si < scCount; si++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    var all = root.GetComponentsInChildren<Transform>(true);
                    foreach (var tr in all)
                    {
                        if (tr == null) continue;
                        string name = tr.name ?? "";
                        if (!lookup.TryGetValue(name, out var list))
                        {
                            list = new List<(string, Transform)>();
                            lookup[name] = list;
                        }
                        list.Add((scene.name, tr));
                    }
                }
            }

            // For each city, try to find explicit targetGameObjectName first, then name-substring matches.
            foreach (var city in Cities)
            {
                try
                {
                    if (city == null) continue;
                    string cname = city.name ?? "(null)";
                    string target = city.targetGameObjectName ?? "";

                    TBLog.Info($"CityScan: --- {cname} --- targetGameObjectName='{target}' (existing sceneName='{city.sceneName ?? ""}'), coords={(city.coords != null ? $"[{string.Join(", ", city.coords)}]" : "(null)")}");

                    bool foundAny = false;

                    if (!string.IsNullOrEmpty(target))
                    {
                        if (lookup.TryGetValue(target, out var exacts) && exacts.Count > 0)
                        {
                            foreach (var (sceneName, tr) in exacts)
                            {
                                var pos = tr.position;
                                TBLog.Info($"CityScan: FOUND exact '{target}' in scene '{sceneName}' at ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) path='{GetFullPath(tr)}'");
                                foundAny = true;
                            }
                        }
                        else
                        {
                            // try GameObject.Find (active objects)
                            var go = GameObject.Find(target);
                            if (go != null)
                            {
                                var s = go.scene.IsValid() ? go.scene.name : "(unknown)";
                                var p = go.transform.position;
                                TBLog.Info($"CityScan: FOUND active exact '{target}' in scene '{s}' at ({p.x:F3}, {p.y:F3}, {p.z:F3})");
                                foundAny = true;
                            }
                        }
                    }

                    // Substring matches: look for transforms with names containing the city name (case-insensitive).
                    var substrMatches = new List<(string scene, Transform tr)>();
                    if (!string.IsNullOrEmpty(cname))
                    {
                        foreach (var kv in lookup)
                        {
                            if (kv.Key.IndexOf(cname, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foreach (var pair in kv.Value)
                                    substrMatches.Add((pair.sceneName, pair.t));
                            }
                        }

                        // Also consider active scene objects not included in lookup (should be included, but double-check)
                        var allActive = UnityEngine.Object.FindObjectsOfType<Transform>();
                        foreach (var tr in allActive)
                        {
                            if (tr == null) continue;
                            if (tr.name.IndexOf(cname, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                substrMatches.Add((tr.gameObject.scene.IsValid() ? tr.gameObject.scene.name : "(unknown)", tr));
                            }
                        }
                    }

                    // De-duplicate substrMatches by transform instance and log the most useful ones
                    var reported = new HashSet<int>();
                    int reportedCount = 0;
                    foreach (var m in substrMatches)
                    {
                        if (m.tr == null) continue;
                        int id = m.tr.GetInstanceID();
                        if (reported.Contains(id)) continue;
                        reported.Add(id);

                        var pos = m.tr.position;
                        string sceneN = m.scene ?? "(unknown)";
                        string path = GetFullPath(m.tr);
                        TBLog.Info($"CityScan: SUBSTRING match '{m.tr.name}' in scene '{sceneN}' at ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) path='{path}'");
                        reportedCount++;
                        if (reportedCount >= 20) break;
                    }

                    if (!foundAny && reportedCount == 0)
                        TBLog.Info($"CityScan: no matches found in loaded scenes for city '{cname}'. Consider loading the map or using in-game travel to that map, then run this again.");
                }
                catch (Exception exCity)
                {
                    TBLog.Warn("CityScan: error scanning city: " + exCity.Message);
                }
            }

            TBLog.Info("LogCityAnchorsFromLoadedScenes: scan complete.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("LogCityAnchorsFromLoadedScenes exception: " + ex.Message);
        }
    }

    // Auto-assign sceneName for a single city (by exact city.name match).
    // Only assigns if both sceneName is null/empty AND coords are not available (null or length < 3).
    public static void AutoAssignSceneNameForCity(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return;

        // Resolve Cities collection (if you're inside TravelButtonMod you can reference it directly)
        var citiesField = typeof(TravelButton).GetField("Cities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (citiesField == null)
        {
            TBLog.Warn("AutoAssignSceneNameForCity: TravelButtonMod.Cities field not found.");
            return;
        }

        var cities = citiesField.GetValue(null) as System.Collections.IList;
        if (cities == null)
        {
            TBLog.Warn("AutoAssignSceneNameForCity: Cities list is null.");
            return;
        }

        // Find the city object with matching name property
        object targetCity = null;
        foreach (var c in cities)
        {
            try
            {
                var nameProp = c.GetType().GetProperty("name");
                var n = nameProp?.GetValue(c) as string;
                if (string.Equals(n, cityName, StringComparison.OrdinalIgnoreCase))
                {
                    targetCity = c;
                    break;
                }
            }
            catch { }
        }

        if (targetCity == null)
        {
            TBLog.Info($"AutoAssignSceneNameForCity: city '{cityName}' not found in TravelButtonMod.Cities.");
            return;
        }

        // Check existing sceneName and coords
        var sceneNameProp = targetCity.GetType().GetProperty("sceneName");
        var coordsField = targetCity.GetType().GetField("coords");
        var existingSceneName = sceneNameProp?.GetValue(targetCity) as string;
        var coords = coordsField?.GetValue(targetCity) as float[];

        bool hasCoords = (coords != null && coords.Length >= 3);
        if (!string.IsNullOrEmpty(existingSceneName) || hasCoords)
        {
            TBLog.Info($"AutoAssignSceneNameForCity: city '{cityName}' already has sceneName or coords; skipping.");
            return;
        }

        // Search loaded scenes for a scene whose name contains the city name (case-insensitive)
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            var sName = scene.name ?? "";
            if (!string.IsNullOrEmpty(sName) && sName.IndexOf(cityName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sceneNameProp?.SetValue(targetCity, sName);
                TBLog.Info($"AutoAssignSceneNameForCity: assigned sceneName='{sName}' to city '{cityName}'.");
                return;
            }
        }

        TBLog.Info($"AutoAssignSceneNameForCity: no loaded scene matched city '{cityName}'.");
    }

/*
    public static bool HasPlayerVisited(City city)
    {
        if (city == null) return false;

        string cacheKey = city.name ?? string.Empty;
        if (string.IsNullOrEmpty(cacheKey)) return false;

        // Fast per-city cache
        lock (s_cityVisitedLock)
        {
            if (s_cityVisitedCache.TryGetValue(cacheKey, out bool cached))
                return cached;
        }

        bool result = false;

        try
        {
            // Build a small candidate set of identifiers to try (avoid duplicates)
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddCandidate(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                var t = s.Trim();
                if (t.Length == 0) return;
                candidates.Add(t);
                // include a normalized lowercase variant for some heuristics (HasPlayerVisitedFast also normalizes)
                candidates.Add(t.ToLowerInvariant());
                candidates.Add(t.Replace(" ", "").ToLowerInvariant());
            }

            AddCandidate(city.name);
            AddCandidate(city.sceneName);
            AddCandidate(city.targetGameObjectName);

            // Try fast string-based checks first (cheap)
            foreach (var cand in candidates)
            {
                try
                {
                    if (HasPlayerVisitedFast(cand))
                    {
                        result = true;
                        TBLog.Info($"HasPlayerVisited: fast match '{cand}' => {city.name}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"HasPlayerVisited: HasPlayerVisitedFast threw for '{cand}': {ex.Message}");
                }
            }

            // If no fast match, call the legacy fallback delegate once (if present)
            if (!result)
            {
                var fallback = TravelButtonUI.IsCityVisitedFallback;
                if (fallback != null)
                {
                    try
                    {
                        TBLog.Info($"HasPlayerVisited: calling legacy fallback for '{city.name}'");
                        result = fallback(city);
                        TBLog.Info($"HasPlayerVisited: legacy fallback for '{city.name}' => {result}");
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("HasPlayerVisited: legacy fallback threw: " + ex.Message);
                        result = false;
                    }
                }
                else
                {
                    TBLog.Info("HasPlayerVisited: no legacy fallback registered.");
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("HasPlayerVisited: unexpected error: " + ex.Message);
            result = false;
        }

        // Cache the computed result so subsequent checks are cheap and deterministic for this dialog/session
        lock (s_cityVisitedLock)
        {
            try { s_cityVisitedCache[cacheKey] = result; } catch { }
        }

        return result;
    }
*/

    // add to TravelButtonMod (src/TravelButtonMod.cs)
    public static void DumpCityInteractability()
    {
        try
        {
            TBLog.Info("DumpCityInteractability: activeScene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            long currentMoney = -1;
            try { currentMoney = TravelButtonUI.GetPlayerCurrencyAmountOrMinusOne(); } catch { currentMoney = -1; }
            TBLog.Info($"DumpCityInteractability: PlayerMoney={currentMoney}");
            if (Cities == null || Cities.Count == 0)
            {
                TBLog.Info("DumpCityInteractability: Cities list is empty or null.");
                return;
            }

            foreach (var city in Cities)
            {
                try
                {
                    string cname = city?.name ?? "(null)";
                    bool enabledByConfig = IsCityEnabled(cname);
                    bool visitedInHistory = false;
                    try {
                        try
                        {
                            visitedInHistory = TravelButton.HasPlayerVisited(city);
                        }
                        catch (Exception ex)
                        {
                            visitedInHistory = false;
                            TBLog.Warn("OpenTravelDialog: HasPlayerVisited failed for '" + city?.name + "': " + ex.Message);
                        }
                    } catch { visitedInHistory = false; }
                    bool coordsAvailable = !string.IsNullOrEmpty(city?.targetGameObjectName) || (city?.coords != null && city.coords.Length >= 3);
                    bool targetSceneSpecified = city != null && !string.IsNullOrEmpty(city.sceneName);
                    var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    bool isCurrentScene = targetSceneSpecified && string.Equals(city.sceneName, active, StringComparison.OrdinalIgnoreCase);
                    bool haveMoneyInfo = currentMoney >= 0;
                    int cost = city?.price ?? cfgTravelCost.Value;
                    bool hasEnoughMoney = haveMoneyInfo ? (currentMoney >= cost) : true;
                    bool canVisit = coordsAvailable || (targetSceneSpecified && !isCurrentScene);
                    bool shouldBeInteractableNow = enabledByConfig && visitedInHistory && hasEnoughMoney && canVisit && !isCurrentScene;

                    TBLog.Info($"City='{cname}': enabledByConfig={enabledByConfig}, visitedInHistory={visitedInHistory}, playerMoney={currentMoney}, price={cost}, hasEnoughMoney={hasEnoughMoney}, coordsAvailable={coordsAvailable}, targetScene='{city?.sceneName ?? "(null)"}', isCurrentScene={isCurrentScene}, canVisit={canVisit} -> shouldBeInteractableNow={shouldBeInteractableNow}");
                }
                catch (Exception e)
                {
                    TBLog.Warn("DumpCityInteractability: failed for a city: " + e.Message);
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpCityInteractability: top-level error: " + ex.Message);
        }
    }

    /// <summary>
    /// Returns whether the player has visited the city (using the prepared visited lookup).
    /// Normalizes by city name, sceneName and targetGameObjectName to favor matches from different sources.
    /// </summary>
    // Use this as the canonical object-based visited check. Replace the unused variant with this.
    public static bool HasPlayerVisited(object cityObj)
    {
        if (cityObj == null) return false;

        // ensure our plugin persisted lookup is prepared
        PrepareVisitedLookup();

        // Try to read name/scene/target from the runtime city object
        string name = null;
        string sceneName = null;
        string targetName = null;

        try
        {
            var t = cityObj.GetType();

            // try properties first
            try { name = t.GetProperty("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cityObj, null)?.ToString(); } catch { }
            try { sceneName = t.GetProperty("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cityObj, null)?.ToString(); } catch { }
            try { targetName = t.GetProperty("targetGameObjectName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cityObj, null)?.ToString(); } catch { }

            // fallback to fields if properties missing
            if (string.IsNullOrEmpty(name))
                try { name = t.GetField("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cityObj)?.ToString(); } catch { }
            if (string.IsNullOrEmpty(sceneName))
                try { sceneName = t.GetField("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cityObj)?.ToString(); } catch { }
            if (string.IsNullOrEmpty(targetName))
                try { targetName = t.GetField("targetGameObjectName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(cityObj)?.ToString(); } catch { }
        }
        catch { /* ignore reflection errors */ }

        // 1) check our prepared plugin-persisted lookup (authoritative)
        if (!string.IsNullOrEmpty(name) && _visitedLookup.Contains(NormalizeVisitedKey(name))) return true;
        if (!string.IsNullOrEmpty(sceneName) && _visitedLookup.Contains(NormalizeVisitedKey(sceneName))) return true;
        if (!string.IsNullOrEmpty(targetName) && _visitedLookup.Contains(NormalizeVisitedKey(targetName))) return true;

        // 2) fallback: if we have a HasPlayerVisitedFast(string) function (existing), try it using normalized keys
        //    Only use fallback when the plugin lookup didn't contain the key; this avoids conflicting signals.
        try
        {
            if (!string.IsNullOrEmpty(name))
            {
                if (HasPlayerVisitedFast != null)
                {
                    if (HasPlayerVisitedFast(NormalizeVisitedKey(name))) return true;
                }
                else
                {
                    // if your fast function is a named static method (not a delegate), call it directly:
                    // if (HasPlayerVisitedFast(NormalizeVisitedKey(name))) return true;
                }
            }

            if (!string.IsNullOrEmpty(sceneName))
            {
                if (HasPlayerVisitedFast != null)
                {
                    if (HasPlayerVisitedFast(NormalizeVisitedKey(sceneName))) return true;
                }
                // or direct call as above
            }

            if (!string.IsNullOrEmpty(targetName))
            {
                if (HasPlayerVisitedFast != null)
                {
                    if (HasPlayerVisitedFast(NormalizeVisitedKey(targetName))) return true;
                }
            }
        }
        catch { /* ignore fallback errors */ }

        return false;
    }


    // Vrací true pokud by tlaèítko mìsta mìlo být interaktivní (clickable) právì teï
    private static bool ShouldBeInteractableNow(City city, Vector3 playerPos)
    {
        if (city == null) return false;

        // 1) enabled in config
        bool enabledByConfig = GetBoolMemberOrDefault(city, true, "enabled", "Enabled"); // nebo city.enabled

        // 2) visited in history (from PrepareVisitedLookup / _visitedLookup)
        // Normalize stejné klíèe jako PrepareVisitedLookup používá
        bool visitedInHistory = HasPlayerVisited(city); // adaptovat na vaši funkci (fast match + legacy fallback)

        // 3) player has enough money
        int price = GetCityPrice(city); // získejte cenu (reflexnì nebo property), vrací >=0
        long playerMoney = TravelButtonUI.GetPlayerCurrencyAmountOrMinusOne();
        bool hasEnoughMoney = (price <= 0) || (playerMoney >= price);

        // 4) coords available
        bool coordsAvailable = (city.coords != null && city.coords.Length >= 3) || !string.IsNullOrEmpty(city.targetGameObjectName);

        // 5) is current scene?
        bool sceneMatches = IsPlayerInScene(city.sceneName, city.targetGameObjectName); // vaše existující pomocná funkce

        // final: initialInteractable podle pravidel, pøeloženo z vašeho popisu:
        bool initialInteractable = enabledByConfig && visitedInHistory && hasEnoughMoney && coordsAvailable && !sceneMatches;

        return initialInteractable;
    }

    // needs: using System; using System.Reflection; using UnityEngine; using UnityEngine.SceneManagement;

    private static bool IsPlayerInScene(string sceneName, string targetGameObjectName)
    {
        try
        {
            // 1) check active scene name
            if (!string.IsNullOrEmpty(sceneName))
            {
                var active = SceneManager.GetActiveScene().name;
                if (!string.IsNullOrEmpty(active) && string.Equals(active, sceneName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 2) fallback: check for named object present in the currently loaded scenes
            if (!string.IsNullOrEmpty(targetGameObjectName))
            {
                try
                {
                    var go = GameObject.Find(targetGameObjectName);
                    if (go != null)
                        return true;
                }
                catch { /* ignore GameObject.Find exceptions */ }
            }
        }
        catch { /* ignore any unexpected errors */ }

        return false;
    }

    // replace your existing GetCityPrice implementation with this version
    private static int GetCityPrice(object cityObj)
    {
        if (cityObj == null) return -1;

        try
        {
            var t = cityObj.GetType();

            // try property first
            var pinfo = t.GetProperty("price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (pinfo != null)
            {
                var raw = pinfo.GetValue(cityObj, null);
                if (raw != null)
                {
                    // Common numeric types handled explicitly
                    if (raw is int ri) return ri;
                    if (raw is long rl) return Convert.ToInt32(rl);
                    if (raw is float rf) return Convert.ToInt32(rf);
                    if (raw is double rd) return Convert.ToInt32(rd);

                    // boxed nullable<int> with value will typically be boxed as int, so above covers it.
                    // Fallback: try Convert.ToInt32 for other numeric/string types
                    try { return Convert.ToInt32(raw); } catch { /* ignore conversion errors */ }
                }
            }

            // try field next
            var finfo = t.GetField("price", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (finfo != null)
            {
                var raw = finfo.GetValue(cityObj);
                if (raw != null)
                {
                    if (raw is int ri2) return ri2;
                    if (raw is long rl2) return Convert.ToInt32(rl2);
                    if (raw is float rf2) return Convert.ToInt32(rf2);
                    if (raw is double rd2) return Convert.ToInt32(rd2);

                    try { return Convert.ToInt32(raw); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // swallow reflection/convert exceptions and return sentinel below
        }

        // sentinel meaning "no price"
        return -1;
    }

    // Try to locate a public or non-public static field/property named "Cities" in any loaded assembly
    // and return its value as IList (or null if not found). Logs helpful diagnostics.
    // Reflection helper: find a static "Cities" field or property in loaded assemblies and return it as an IList.
    // Uses FieldInfo / PropertyInfo correctly so GetValue is called on reflection objects, not on IList.
    // Returns the static Cities collection (as IList) or null if not found.
    private static System.Collections.IList FindStaticCitiesCollection()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsNestedPrivate) continue;

                    var field = t.GetField("Cities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
                    if (field != null)
                    {
                        var val = field.GetValue(null);
                        if (val is System.Collections.IList list) return list;
                        if (val is System.Collections.IEnumerable enumv)
                        {
                            var temp = new System.Collections.ArrayList();
                            foreach (var item in enumv) temp.Add(item);
                            return temp;
                        }
                    }

                    var prop = t.GetProperty("Cities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null, null);
                        if (val is System.Collections.IList list2) return list2;
                        if (val is System.Collections.IEnumerable enumv2)
                        {
                            var temp = new System.Collections.ArrayList();
                            foreach (var item in enumv2) temp.Add(item);
                            return temp;
                        }
                    }
                }
            }

            TBLog.Warn("[TravelButton] FindStaticCitiesCollection: static member named 'Cities' not found in loaded assemblies.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] FindStaticCitiesCollection failed: " + ex.Message);
        }

        return null;
    }

    // Sweep: Auto-assign sceneName for all cities that are missing both sceneName and coords.
    public static void AutoAssignSceneNamesFromLoadedScenes()
    {
        var cities = FindStaticCitiesCollection();
        if (cities == null)
        {
            TBLog.Warn("AutoAssignSceneNamesFromLoadedScenes: Cities list is null.");
            return;
        }

        foreach (var city in cities)
        {
            try
            {
                var nameProp = city.GetType().GetProperty("name");
                var sceneNameProp = city.GetType().GetProperty("sceneName");
                var coordsField = city.GetType().GetField("coords");

                var cityName = nameProp?.GetValue(city) as string;
                var existingSceneName = sceneNameProp?.GetValue(city) as string;
                var coords = coordsField?.GetValue(city) as float[];

                bool hasCoords = (coords != null && coords.Length >= 3);
                if (string.IsNullOrEmpty(cityName)) continue;

                if (!string.IsNullOrEmpty(existingSceneName) || hasCoords)
                {
                    // skip: city already has explicit sceneName or coordinates
                    continue;
                }

                // find a loaded scene whose name contains the city name
                string matchedScene = null;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    var sname = scene.name ?? "";
                    if (!string.IsNullOrEmpty(sname) && sname.IndexOf(cityName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedScene = sname;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(matchedScene))
                {
                    sceneNameProp?.SetValue(city, matchedScene);
                    TBLog.Info($"AutoAssignSceneNamesFromLoadedScenes: set sceneName='{matchedScene}' for city '{cityName}'.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("AutoAssignSceneNamesFromLoadedScenes: exception: " + ex.Message);
            }
        }
    }

    // Global config entries (accessed as TravelButtonMod.cfgTravelCost.Value in existing UI)
    public static ConfigEntry<int> cfgTravelCost = new ConfigEntry<int>(100);
    public static ConfigEntry<bool> cfgEnableTeleport = new ConfigEntry<bool>(true);
    public static ConfigEntry<bool> cfgEnableMod = new ConfigEntry<bool>(true);
    public static ConfigEntry<string> cfgCurrencyItem = new ConfigEntry<string>("Silver");

    // Public list used by UI code (TravelButtonUI reads TravelButtonMod.Cities)
    public static List<City> Cities { get; set; } = new List<City>();

    // Path/filename helpers exposed for debugging
    public static string ConfigFilePath
    {
        get
        {
            try { return TravelButtonPlugin.GetLegacyCfgPath(); }
            catch { return "(unknown)"; }
        }
    }

    // Initialize mod state from JSON config -> should be called once at mod load
    // Returns true if a config instance was located and mapped (or local default used), false otherwise.
    public static bool InitFromConfig()
    {
        try
        {
            TBLog.Info("InitFromConfig: attempting to obtain ConfigManager.Config (safe, no unconditional Load).");

            // Try to locate a type named ConfigManager in loaded assemblies
            Type cfgMgrType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    cfgMgrType = asm.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
                    if (cfgMgrType != null) break;
                }
                catch { /* ignore assemblies that can't enumerate types */ }
            }

            object cfgInstance = null;

            // If we found a ConfigManager type, try to read its static Config (do NOT call Load() yet)
            if (cfgMgrType != null)
            {
                try
                {
                    var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                    var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                    if (cfgProp != null) cfgInstance = cfgProp.GetValue(null);
                    else if (cfgField != null) cfgInstance = cfgField.GetValue(null);
                }
                catch (Exception ex)
                {
                    TBLog.Warn("InitFromConfig: reading ConfigManager.Config threw: " + ex.Message);
                    cfgInstance = null;
                }
            }

            // If no ConfigManager type found OR the found type has a null Config,
            // try to use a local ConfigManager.Default() (the Default() you added in src/ConfigManager.cs).
            // This guarantees deterministic defaults even if an external ConfigManager hasn't initialized.
            if (cfgInstance == null)
            {
                var localCfgMgr = GetLocalType("ConfigManager");
                if (localCfgMgr != null)
                {
                    try
                    {
                        var defMethod = localCfgMgr.GetMethod("Default", BindingFlags.Public | BindingFlags.Static);
                        if (defMethod != null)
                        {
                            var def = defMethod.Invoke(null, null);
                            if (def != null)
                            {
                                MapConfigInstanceToLocal(def);
                                TBLog.Info("InitFromConfig: used local ConfigManager.Default() to populate config.");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("InitFromConfig: calling local ConfigManager.Default() failed: " + ex.Message);
                        // continue to try safer external Load path below
                    }
                }
            }

            // If we still don't have a config instance but found an external ConfigManager type,
            // we may attempt to call its Load() safely (only if local or Newtonsoft is available).
            if (cfgInstance == null && cfgMgrType != null)
            {
                bool callLoad = false;
                bool isLocalConfigMgr = cfgMgrType.Assembly == typeof(TravelButton).Assembly;

                if (isLocalConfigMgr)
                {
                    callLoad = true;
                    TBLog.Info("InitFromConfig: calling Load() on local ConfigManager type.");
                }
                else
                {
                    // Only call Load on external ConfigManager when Newtonsoft is available, to avoid assembly load exceptions.
                    bool hasNewtonsoft = AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                    {
                        try { return a.GetTypes().Any(t => t.FullName == "Newtonsoft.Json.JsonConvert"); } catch { return false; }
                    });

                    if (hasNewtonsoft)
                    {
                        callLoad = true;
                        TBLog.Info("InitFromConfig: external ConfigManager found and Newtonsoft present; will call Load() via reflection.");
                    }
                    else
                    {
                        TBLog.Warn("InitFromConfig: external ConfigManager found but Newtonsoft not present; skipping Load() to avoid assembly load errors.");
                    }
                }

                if (callLoad)
                {
                    try
                    {
                        var loadMethod = cfgMgrType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                        if (loadMethod != null)
                        {
                            loadMethod.Invoke(null, null);
                            // read Config after Load()
                            var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                            var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                            cfgInstance = cfgProp != null ? cfgProp.GetValue(null) : cfgField != null ? cfgField.GetValue(null) : null;
                        }
                        else
                        {
                            TBLog.Warn("InitFromConfig: ConfigManager.Load method not found.");
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        TBLog.Warn("InitFromConfig: ConfigManager.Load failed via reflection: " + (tie.InnerException?.Message ?? tie.Message));
                        return false; // allow retry from coroutine
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("InitFromConfig: exception invoking ConfigManager.Load: " + ex.Message);
                        return false;
                    }
                }
            }

            // If we have a config instance now, map it into local fields and cities
            if (cfgInstance != null)
            {
                MapConfigInstanceToLocal(cfgInstance);
                TBLog.Info($"InitFromConfig: Loaded {Cities?.Count ?? 0} cities from ConfigManager.");
                return true;
            }

            // No config available (and we failed to get a local default); signal caller to retry / fallback.
            TBLog.Info("InitFromConfig: no config instance available (will retry or fallback).");
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("InitFromConfig: unexpected exception: " + ex);
            return false;
        }
    }

    // Map a config object (the ConfigManager.Config instance) into TravelButtonMod fields (cfgTravelCost, cfgCurrencyItem, Cities list)
    public static void MapConfigInstanceToLocal(object cfgInstance)
    {
        if (cfgInstance == null) return;
        try
        {
            try
            {
                var enabledMember = cfgInstance.GetType().GetField("enabled") ?? (MemberInfo)cfgInstance.GetType().GetProperty("enabled");
                if (enabledMember is FieldInfo fe) cfgEnableMod.Value = SafeGetBool(fe.GetValue(cfgInstance));
                else if (enabledMember is PropertyInfo pe) cfgEnableMod.Value = SafeGetBool(pe.GetValue(cfgInstance));

                var curMember = cfgInstance.GetType().GetField("currencyItem") ?? (MemberInfo)cfgInstance.GetType().GetProperty("currencyItem");
                if (curMember is FieldInfo fc) cfgCurrencyItem.Value = SafeGetString(fc.GetValue(cfgInstance)) ?? "Silver";
                else if (curMember is PropertyInfo pc) cfgCurrencyItem.Value = SafeGetString(pc.GetValue(cfgInstance)) ?? "Silver";

                var gtpMember = cfgInstance.GetType().GetField("globalTeleportPrice") ?? (MemberInfo)cfgInstance.GetType().GetProperty("globalTeleportPrice");
                if (gtpMember is FieldInfo fg) cfgTravelCost.Value = SafeGetInt(fg.GetValue(cfgInstance), cfgTravelCost.Value);
                else if (gtpMember is PropertyInfo pg) cfgTravelCost.Value = SafeGetInt(pg.GetValue(cfgInstance), cfgTravelCost.Value);
            }
            catch (Exception ex)
            {
                TBLog.Warn("MapConfigInstanceToLocal: top-level map failed: " + ex.Message);
            }

            try
            {
                Cities = new List<City>();
                var citiesMemberField = cfgInstance.GetType().GetField("cities", BindingFlags.Public | BindingFlags.Instance);
                var citiesMemberProp = cfgInstance.GetType().GetProperty("cities", BindingFlags.Public | BindingFlags.Instance);
                object citiesObj = citiesMemberField != null ? citiesMemberField.GetValue(cfgInstance) : citiesMemberProp != null ? citiesMemberProp.GetValue(cfgInstance) : null;

                if (citiesObj != null)
                {
                    var dict = citiesObj as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (var key in dict.Keys)
                        {
                            try
                            {
                                string cname = key.ToString();
                                var cityCfgObj = dict[key];
                                var mapped = MapSingleCityFromObject(cname, cityCfgObj);
                                if (mapped == null) continue;

                                if (TravelButton.Cities == null) TravelButton.Cities = new List<City>();

                                // Try to find existing by name (case-insensitive)
                                var existing = TravelButton.Cities.FirstOrDefault(c => string.Equals(c.name, mapped.name, StringComparison.OrdinalIgnoreCase));
                                if (existing == null)
                                {
                                    TravelButton.Cities.Add(mapped);
                                    TBLog.Info($"MapConfigInstanceToLocal: added mapped legacy-config city '{mapped.name}' (no existing JSON/runtime entry).");
                                }
                                else
                                {
                                    // Merge mapped values into existing only when fields are missing on existing
                                    TBLog.Info($"MapConfigInstanceToLocal: merging mapped legacy-config city '{mapped.name}' into existing entry.");

                                    if (string.IsNullOrEmpty(existing.sceneName) && !string.IsNullOrEmpty(mapped.sceneName))
                                    {
                                        existing.sceneName = mapped.sceneName;
                                        TBLog.Info($"MapConfigInstanceToLocal: set sceneName for '{existing.name}' = '{existing.sceneName}'");
                                    }

                                    if ((existing.coords == null || existing.coords.Length < 3) && mapped.coords != null && mapped.coords.Length >= 3)
                                    {
                                        existing.coords = mapped.coords;
                                        TBLog.Info($"MapConfigInstanceToLocal: set coords for '{existing.name}' from mapped config");
                                    }

                                    if (string.IsNullOrEmpty(existing.targetGameObjectName) && !string.IsNullOrEmpty(mapped.targetGameObjectName))
                                    {
                                        existing.targetGameObjectName = mapped.targetGameObjectName;
                                        TBLog.Info($"MapConfigInstanceToLocal: set targetGameObjectName for '{existing.name}' = '{existing.targetGameObjectName}'");
                                    }

                                    // Do NOT overwrite price/enabled/visited here — those should be set by EnsureBepInExConfigBindings which runs after merging.
                                    // However if a mapped city explicitly includes variants/lastKnownVariant and existing lacks them, merge those too:
                                    if ((existing.variants == null || existing.variants.Length == 0) && mapped.variants != null && mapped.variants.Length > 0)
                                    {
                                        existing.variants = mapped.variants;
                                        TBLog.Info($"MapConfigInstanceToLocal: merged variants for '{existing.name}' (count={mapped.variants.Length})");
                                    }

                                    if (string.IsNullOrEmpty(existing.lastKnownVariant) && !string.IsNullOrEmpty(mapped.lastKnownVariant))
                                    {
                                        existing.lastKnownVariant = mapped.lastKnownVariant;
                                        TBLog.Info($"MapConfigInstanceToLocal: merged lastKnownVariant for '{existing.name}' = '{existing.lastKnownVariant}'");
                                    }
                                }
                            }
                            catch (Exception inner)
                            {
                                TBLog.Warn($"MapConfigInstanceToLocal: failed mapping legacy city for key '{key}': {inner.Message}");
                            }
                        }
                    }
                    else
                    {
                        var getEnum = citiesObj.GetType().GetMethod("GetEnumerator");
                        if (getEnum != null)
                        {
                            var enumerator = getEnum.Invoke(citiesObj, null);
                            var moveNext = enumerator.GetType().GetMethod("MoveNext");
                            var currentProp = enumerator.GetType().GetProperty("Current");
                            while ((bool)moveNext.Invoke(enumerator, null))
                            {
                                var current = currentProp.GetValue(enumerator);
                                var keyProp = current.GetType().GetProperty("Key");
                                var valProp = current.GetType().GetProperty("Value");
                                var k = keyProp.GetValue(current);
                                var v = valProp.GetValue(current);
                                string cname = k.ToString();
                                var mapped = MapSingleCityFromObject(cname, v);
                                if (mapped != null) Cities.Add(mapped);
                            }
                        }
                        else
                        {
                            TBLog.Warn("MapConfigInstanceToLocal: cfg.cities is not enumerable.");
                        }
                    }
                }
                else
                {
                    TBLog.Warn("MapConfigInstanceToLocal: cfg.cities is null.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("MapConfigInstanceToLocal: cities mapping failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("MapConfigInstanceToLocal: unexpected: " + ex.Message);
        }
    }

    // Corrected MapSingleCityFromObject implementation that calls CityMappingHelpers.TryResolveCityDataFromObject
    // Merge/replace this method in your TravelButton.cs class.

    private static City MapSingleCityFromObject(string cname, object cityCfgObj)
    {
        try
        {
            if (string.IsNullOrEmpty(cname) || cityCfgObj == null)
            {
                TBLog.Info($"MapSingleCityFromObject: skipping empty cname or null cityCfgObj (cname='{cname}').");
                return null;
            }

            TBLog.Info($"MapSingleCityFromObject: mapping city '{cname}' from object type '{cityCfgObj.GetType().FullName}'.");

            var city = new City(cname);

            // enabled
            try
            {
                var enabledMember = cityCfgObj.GetType().GetField("enabled") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("enabled");
                if (enabledMember is FieldInfo fe) city.enabled = SafeGetBool(fe.GetValue(cityCfgObj));
                else if (enabledMember is PropertyInfo pe) city.enabled = SafeGetBool(pe.GetValue(cityCfgObj));
                TBLog.Info($"MapSingleCityFromObject: '{cname}' enabled={city.enabled} (from reflected field/property).");
            }
            catch (Exception ex)
            {
                TBLog.Warn($"MapSingleCityFromObject: '{cname}' enabled read failed: {ex.Message}");
            }

            // price (from reflected object)
            try
            {
                var priceMember = cityCfgObj.GetType().GetField("price") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("price");
                if (priceMember is FieldInfo fprice) city.price = SafeGetNullableInt(fprice.GetValue(cityCfgObj));
                else if (priceMember is PropertyInfo pprice) city.price = SafeGetNullableInt(pprice.GetValue(cityCfgObj));
                TBLog.Info($"MapSingleCityFromObject: '{cname}' price={(city.price.HasValue ? city.price.Value.ToString() : "null")} (from reflected field/property).");
            }
            catch (Exception ex)
            {
                TBLog.Warn($"MapSingleCityFromObject: '{cname}' price read failed: {ex.Message}");
            }

            // Try centralized resolver implemented in CityMappingHelpers
            try
            {
                if (CityMappingHelpers.TryResolveCityDataFromObject(
                    cityCfgObj,
                    out string resolvedSceneName,
                    out string resolvedTargetGameObjectName,
                    out UnityEngine.Vector3 resolvedCoordsVec,
                    out bool resolvedHaveCoords,
                    out int resolvedPrice))
                {
                    TBLog.Info($"MapSingleCityFromObject: CityMappingHelpers resolved for '{cname}': scene='{resolvedSceneName}', target='{resolvedTargetGameObjectName}', haveCoords={resolvedHaveCoords}, price={resolvedPrice}");
                    if (resolvedHaveCoords)
                    {
                        city.coords = new float[] { resolvedCoordsVec.x, resolvedCoordsVec.y, resolvedCoordsVec.z };
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' coords set from resolver = [{city.coords[0]}, {city.coords[1]}, {city.coords[2]}].");
                    }

                    if (!string.IsNullOrEmpty(resolvedTargetGameObjectName))
                    {
                        city.targetGameObjectName = resolvedTargetGameObjectName;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' targetGameObjectName set from resolver = '{city.targetGameObjectName}'.");
                    }

                    if (!string.IsNullOrEmpty(resolvedSceneName))
                    {
                        city.sceneName = resolvedSceneName;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' sceneName set from resolver = '{city.sceneName}'.");
                    }

                    // Only override price if helper returned a meaningful non-zero value
                    if (resolvedPrice != 0)
                    {
                        city.price = resolvedPrice;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' price overridden from resolver = {resolvedPrice}.");
                    }
                }
                else
                {
                    TBLog.Info($"MapSingleCityFromObject: CityMappingHelpers.TryResolveCityDataFromObject returned false for '{cname}'.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("MapSingleCityFromObject: CityMappingHelpers.TryResolveCityDataFromObject threw: " + ex.Message);
            }

            // Defensive fallbacks: if any key piece of data is still missing, try to read it individually
            try
            {
                if (city.coords == null)
                {
                    var coordsMember = cityCfgObj.GetType().GetField("coords") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("coords");
                    object coordsVal = coordsMember is FieldInfo fc ? fc.GetValue(cityCfgObj) : coordsMember is PropertyInfo pc ? pc.GetValue(cityCfgObj) : null;
                    if (coordsVal != null)
                    {
                        var list = coordsVal as System.Collections.IList;
                        if (list != null && list.Count >= 3)
                        {
                            try
                            {
                                city.coords = new float[3] { Convert.ToSingle(list[0]), Convert.ToSingle(list[1]), Convert.ToSingle(list[2]) };
                                TBLog.Info($"MapSingleCityFromObject: '{cname}' coords set from coords field/list = [{city.coords[0]}, {city.coords[1]}, {city.coords[2]}].");
                            }
                            catch (Exception ex)
                            {
                                city.coords = null;
                                TBLog.Warn($"MapSingleCityFromObject: '{cname}' coords conversion failed: {ex.Message}");
                            }
                        }
                        else if (coordsVal is float[] farr && farr.Length >= 3)
                        {
                            city.coords = new float[3] { farr[0], farr[1], farr[2] };
                            TBLog.Info($"MapSingleCityFromObject: '{cname}' coords set from float[] = [{city.coords[0]}, {city.coords[1]}, {city.coords[2]}].");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"MapSingleCityFromObject: '{cname}' coords fallback failed: {ex.Message}");
            }

            try
            {
                if (string.IsNullOrEmpty(city.targetGameObjectName))
                {
                    var tgnMember = cityCfgObj.GetType().GetField("targetGameObjectName") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("targetGameObjectName");
                    object tgnVal = tgnMember is FieldInfo ftgn ? ftgn.GetValue(cityCfgObj) : tgnMember is PropertyInfo ptgn ? ptgn.GetValue(cityCfgObj) : null;
                    var tgnStr = SafeGetString(tgnVal);
                    if (!string.IsNullOrEmpty(tgnStr))
                    {
                        city.targetGameObjectName = tgnStr;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' targetGameObjectName set from reflected field/property = '{city.targetGameObjectName}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"MapSingleCityFromObject: '{cname}' targetGameObjectName fallback failed: {ex.Message}");
            }

            try
            {
                if (string.IsNullOrEmpty(city.sceneName))
                {
                    var sceneMember = cityCfgObj.GetType().GetField("sceneName") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("sceneName");
                    object sceneVal = sceneMember is FieldInfo fsc ? fsc.GetValue(cityCfgObj) : sceneMember is PropertyInfo psc ? psc.GetValue(cityCfgObj) : null;
                    var s = SafeGetString(sceneVal);
                    if (!string.IsNullOrEmpty(s))
                    {
                        city.sceneName = s;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' sceneName set from reflected field/property = '{city.sceneName}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"MapSingleCityFromObject: '{cname}' sceneName fallback failed: {ex.Message}");
            }

            // Variants and lastKnownVariant handling: check multiple possible fields
            try
            {
                bool variantsPopulated = false;

                // 1) try 'variants' field/property (array or list)
                var varMember = cityCfgObj.GetType().GetField("variants") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("variants");
                object varVal = varMember is FieldInfo fv ? fv.GetValue(cityCfgObj) : varMember is PropertyInfo pv ? pv.GetValue(cityCfgObj) : null;
                if (varVal != null)
                {
                    if (varVal is string[] sarr && sarr.Length > 0)
                    {
                        city.variants = sarr;
                        variantsPopulated = true;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' variants set from 'variants' string[] (count={sarr.Length}).");
                    }
                    else if (varVal is System.Collections.IList vlist)
                    {
                        var list = new List<string>();
                        foreach (var item in vlist) { var ss = SafeGetString(item); if (!string.IsNullOrEmpty(ss)) list.Add(ss); }
                        if (list.Count > 0)
                        {
                            city.variants = list.ToArray();
                            variantsPopulated = true;
                            TBLog.Info($"MapSingleCityFromObject: '{cname}' variants set from 'variants' IList (count={list.Count}).");
                        }
                    }
                    else
                    {
                        var single = SafeGetString(varVal);
                        if (!string.IsNullOrEmpty(single))
                        {
                            city.variants = new string[] { single };
                            variantsPopulated = true;
                            TBLog.Info($"MapSingleCityFromObject: '{cname}' variants set from single 'variants' value = '{single}'.");
                        }
                    }
                }

                // 2) try separate variant names if variants still empty (variantNormalName / variantDestroyedName)
                if (!variantsPopulated)
                {
                    var normal = SafeGetString(cityCfgObj.GetType().GetField("variantNormalName")?.GetValue(cityCfgObj) ?? cityCfgObj.GetType().GetProperty("variantNormalName")?.GetValue(cityCfgObj));
                    var destroyed = SafeGetString(cityCfgObj.GetType().GetField("variantDestroyedName")?.GetValue(cityCfgObj) ?? cityCfgObj.GetType().GetProperty("variantDestroyedName")?.GetValue(cityCfgObj));
                    var vt = new List<string>();
                    if (!string.IsNullOrEmpty(normal)) vt.Add(normal);
                    if (!string.IsNullOrEmpty(destroyed)) vt.Add(destroyed);
                    if (vt.Count > 0)
                    {
                        city.variants = vt.ToArray();
                        variantsPopulated = true;
                        TBLog.Info($"MapSingleCityFromObject: '{cname}' variants assembled from variantNormalName/variantDestroyedName: [{string.Join(", ", city.variants)}].");
                    }
                }

                // 3) lastKnownVariant
                if (string.IsNullOrEmpty(city.lastKnownVariant))
                {
                    // Prefer 'lastKnownVariant' field/property
                    var lastVarMember = cityCfgObj.GetType().GetField("lastKnownVariant") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("lastKnownVariant");
                    if (lastVarMember != null)
                    {
                        object lastVal = lastVarMember is FieldInfo fl ? fl.GetValue(cityCfgObj) : lastVarMember is PropertyInfo pl ? pl.GetValue(cityCfgObj) : null;
                        var sv = SafeGetString(lastVal);
                        if (!string.IsNullOrEmpty(sv))
                        {
                            city.lastKnownVariant = sv;
                            TBLog.Info($"MapSingleCityFromObject: '{cname}' lastKnownVariant set from reflected field/property = '{city.lastKnownVariant}'.");
                        }
                    }

                    // If still empty, try to infer from variantNormalName
                    if (string.IsNullOrEmpty(city.lastKnownVariant))
                    {
                        var inferred = SafeGetString(cityCfgObj.GetType().GetField("variantNormalName")?.GetValue(cityCfgObj) ?? cityCfgObj.GetType().GetProperty("variantNormalName")?.GetValue(cityCfgObj));
                        if (!string.IsNullOrEmpty(inferred))
                        {
                            city.lastKnownVariant = inferred;
                            TBLog.Info($"MapSingleCityFromObject: '{cname}' lastKnownVariant inferred from variantNormalName = '{city.lastKnownVariant}'.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"MapSingleCityFromObject: '{cname}' variants/lastKnownVariant handling failed: {ex.Message}");
            }

            // Final debug dump for this city mapping
            try
            {
                string coordsStr = city.coords != null ? string.Join(", ", city.coords) : "null";
                string variantsStr = city.variants != null ? ("[" + string.Join(", ", city.variants) + "]") : "null";
                TBLog.Info($"MapSingleCityFromObject: mapped city '{cname}' => scene='{city.sceneName ?? ""}', coords=[{coordsStr}], target='{city.targetGameObjectName ?? ""}', price={(city.price.HasValue ? city.price.Value.ToString() : "null")}, variants={variantsStr}, lastKnownVariant='{city.lastKnownVariant ?? ""}'");
            }
            catch { /* ignore final logging failures */ }

            return city;
        }
        catch (Exception ex)
        {
            TBLog.Warn("MapSingleCityFromObject: " + ex.Message);
            return null;
        }
    }

    // Try to find a type defined in our loaded assemblies by simple name (prefers our assembly)
    public static Type GetLocalType(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm == typeof(TravelButton).Assembly)
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == simpleName);
                    if (t != null) return t;
                }
            }
            catch { }
        }
        return null;
    }

    // Safe parsing helpers
    private static bool SafeGetBool(object o)
    {
        if (o == null) return false;
        if (o is bool b) return b;
        if (o is int i) return i != 0;
        if (o is long l) return l != 0;
        if (o is string s && bool.TryParse(s, out var r)) return r;
        return false;
    }

    private static int SafeGetInt(object o, int fallback)
    {
        if (o == null) return fallback;
        try
        {
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o is string s && int.TryParse(s, out var r)) return r;
            if (o is double d) return (int)d;
            if (o is float f) return (int)f;
        }
        catch { }
        return fallback;
    }

    private static int? SafeGetNullableInt(object o)
    {
        if (o == null) return null;
        try
        {
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o is string s && int.TryParse(s, out var r)) return r;
            if (o is double d) return (int)d;
            if (o is float f) return (int)f;
        }
        catch { }
        return null;
    }

    private static string SafeGetString(object o)
    {
        if (o == null) return null;
        try { return o.ToString(); } catch { return null; }
    }

    // Try to resolve the target position for a city. Tries active objects, inactive/assets,
    // substring heuristics, tag lookup, and falls back to explicit coords. Logs helpful debug information.
    public static bool TryGetTargetPosition(string targetGameObjectName, float[] coordsFallback, string cityName, out Vector3 outPos)
    {
        outPos = Vector3.zero;

        if (!string.IsNullOrEmpty(targetGameObjectName))
        {
            try
            {
                // 1) Fast path: active object by exact name (but ignore UI objects)
                var go = GameObject.Find(targetGameObjectName);
                if (go != null)
                {
                    if (!IsUiGameObject(go) && go.scene.IsValid() && go.scene.isLoaded)
                    {
                        outPos = go.transform.position;
                        TBLog.Info($"TryGetTargetPosition: found active GameObject '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                        return true;
                    }
                    else
                    {
                        TBLog.Warn($"TryGetTargetPosition: found '{targetGameObjectName}' but it's a UI/invalid-scene object (ignored).");
                    }
                }

                // 2) Search all objects (includes inactive and assets) but only accept valid scene objects
                var all = Resources.FindObjectsOfTypeAll<GameObject>();

                // Exact name match but only if in a loaded scene and not UI
                var exactSceneObj = all.FirstOrDefault(c =>
                    string.Equals(c.name, targetGameObjectName, StringComparison.Ordinal) &&
                    c.scene.IsValid() && c.scene.isLoaded &&
                    !IsUiGameObject(c) &&
                    c.transform != null && c.transform.position.sqrMagnitude > 0.0001f);

                if (exactSceneObj != null)
                {
                    // If city.sceneName set, require scene match
                    if (string.IsNullOrEmpty(cityName) || string.IsNullOrEmpty(exactSceneObj.scene.name) || true) { /* keep logging below */ }
                    outPos = exactSceneObj.transform.position;
                    TBLog.Info($"TryGetTargetPosition: found scene GameObject by exact match '{exactSceneObj.name}' at {outPos} for city '{cityName}'.");
                    return true;
                }

                // Substring/clone match but be conservative:
                // - candidate name length >= 3 (avoid single-letter false matches)
                // - candidate must be part of a valid loaded scene (not an asset)
                // - candidate must not be UI and must have non-zero world position
                var containsSceneObj = all.FirstOrDefault(c =>
                    !string.IsNullOrEmpty(c.name) &&
                    c.name.Length >= 3 &&
                    (c.name.IndexOf(targetGameObjectName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     targetGameObjectName.IndexOf(c.name, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    c.scene.IsValid() && c.scene.isLoaded &&
                    !IsUiGameObject(c) &&
                    c.transform != null && c.transform.position.sqrMagnitude > 0.0001f);

                if (containsSceneObj != null)
                {
                    outPos = containsSceneObj.transform.position;
                    TBLog.Info($"TryGetTargetPosition: found scene GameObject by substring match '{containsSceneObj.name}' -> '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                    return true;
                }

                // Tag lookup fallback (only accept scene objects and non-UI)
                try
                {
                    var byTag = GameObject.FindGameObjectWithTag(targetGameObjectName);
                    if (byTag != null && byTag.scene.IsValid() && byTag.scene.isLoaded && !IsUiGameObject(byTag))
                    {
                        outPos = byTag.transform.position;
                        TBLog.Info($"TryGetTargetPosition: found GameObject by tag '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                        return true;
                    }
                }
                catch { /* ignore tag errors */ }

                TBLog.Warn($"TryGetTargetPosition: target GameObject '{targetGameObjectName}' not found in any loaded scene for city '{cityName}'.");
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TryGetTargetPosition: error while searching for '{targetGameObjectName}' for city '{cityName}': {ex.Message}");
            }

            // Optionally emit diagnostic candidates for debugging
            try { LogCandidateAnchorNames(cityName); } catch { }
        }

        // 3) Fallback to explicit coords (if present)
        if (coordsFallback != null && coordsFallback.Length >= 3)
        {
            outPos = new Vector3(coordsFallback[0], coordsFallback[1], coordsFallback[2]);
            TBLog.Info($"TryGetTargetPosition: using explicit coords ({outPos.x}, {outPos.y}, {outPos.z}) for city '{cityName}'.");
            return true;
        }

        TBLog.Warn($"TryGetTargetPosition: no GameObject and no explicit coords available for city '{cityName}'.");
        return false;
    }

    // Helper: returns true for UI elements we should ignore as teleport anchors
    private static bool IsUiGameObject(GameObject go)
    {
        if (go == null) return false;
        try
        {
            // RectTransform indicates a UI element
            if (go.GetComponent<RectTransform>() != null) return true;
            // If any parent has a Canvas, treat as UI
            if (go.GetComponentInParent<Canvas>() != null) return true;
            // If it's on the UI layer (named "UI"), treat as UI
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1 && go.layer == uiLayer) return true;
        }
        catch { /* ignore reflection errors */ }
        return false;
    }

    // Diagnostic helper: enumerate likely anchor GameObjects and log them to the TravelButton log.
    public static void LogCandidateAnchorNames(string cityName, int maxResults = 50)
    {
        try
        {
            TBLog.Info($"Anchor diagnostic: searching for candidates for city '{cityName}'...");

            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            int count = 0;
            foreach (var go in all)
            {
                if (go == null) continue;
                var name = go.name ?? "";
                if (name.IndexOf(cityName ?? "", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("town", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("village", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                    string path = GetFullPath(go.transform);
                    string scene = (go.scene.IsValid() ? go.scene.name : "(asset)");
                    TBLog.Info($"Anchor candidate #{count}: name='{name}' scene='{scene}' path='{path}'");
                    if (count >= maxResults) break;
                }
            }

            if (count == 0)
                TBLog.Info($"Anchor diagnostic: no candidates found for '{cityName}' (tried substrings). Consider checking scene objects or config targetGameObjectName.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("Anchor diagnostic failed: " + ex.Message);
        }
    }

    // Helper: return full transform path "Root/Child/GrandChild"
    private static string GetFullPath(Transform t)
    {
        if (t == null) return "(null)";
        var parts = new List<string>();
        var cur = t;
        while (cur != null)
        {
            parts.Add(cur.name ?? "(null)");
            cur = cur.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // Query if a city is enabled in config (does not consider visited state)
    public static bool IsCityEnabled(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return false;
        try
        {
            // Prefer reading external ConfigManager.Config if available (without calling Load again)
            Type cfgMgrType = null;
            object cfgInstance = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    cfgMgrType = asm.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
                    if (cfgMgrType != null) break;
                }
                catch { }
            }

            if (cfgMgrType != null)
            {
                try
                {
                    var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                    var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                    cfgInstance = cfgProp != null ? cfgProp.GetValue(null) : cfgField != null ? cfgField.GetValue(null) : null;
                }
                catch { cfgInstance = null; }
            }

            if (cfgInstance != null)
            {
                var citiesMemberField = cfgInstance.GetType().GetField("cities");
                var citiesMemberProp = cfgInstance.GetType().GetProperty("cities");
                object citiesObj = citiesMemberField != null ? citiesMemberField.GetValue(cfgInstance) : citiesMemberProp != null ? citiesMemberProp.GetValue(cfgInstance) : null;
                if (citiesObj is System.Collections.IDictionary dict)
                {
                    if (dict.Contains(cityName))
                    {
                        var cityCfgObj = dict[cityName];
                        var enabledMember = cityCfgObj.GetType().GetField("enabled") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("enabled");
                        if (enabledMember is FieldInfo fe) return SafeGetBool(fe.GetValue(cityCfgObj));
                        if (enabledMember is PropertyInfo pe) return SafeGetBool(pe.GetValue(cityCfgObj));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("IsCityEnabled: reading external config failed: " + ex.Message);
        }

        var local = Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
        if (local != null) return local.enabled;

        return false;
    }

    // Unity JsonUtility wrapper used by fallback
    [System.Serializable]
    private class CityListWrapper
    {
        public List<CityDto> cities;
    }

    // small DTO wrapper used for Unity JsonUtility serialization
    // DTOs used for Unity JsonUtility serialization
    [Serializable]
    private class CityDto
    {
        public string name;
        public int price; // use sentinel -1 to represent "no price"
        public float[] coords;
        public string targetGameObjectName;
        public string sceneName;
        public string desc;
        public bool visited;
    }

    // Add these helpers inside the TravelButton class (near other reflection helpers)

    private static string GetStringMemberSafe(object target, params string[] candidateNames)
    {
        if (target == null || candidateNames == null) return null;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                if (p != null && p.CanRead)
                {
                    var val = p.GetValue(target, null);
                    if (val != null) return val.ToString();
                }

                var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                if (f != null)
                {
                    var val = f.GetValue(target);
                    if (val != null) return val.ToString();
                }
            }
            catch
            {
                // ignore and try next candidate
            }
        }
        return null;
    }

    private static bool GetBoolMemberOrDefault(object target, bool defaultValue, params string[] candidateNames)
    {
        if (target == null || candidateNames == null) return defaultValue;
        var t = target.GetType();
        foreach (var name in candidateNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                if (p != null && p.CanRead)
                {
                    var val = p.GetValue(target, null);
                    if (val is bool b) return b;
                    if (val != null)
                    {
                        // try common conversions
                        if (bool.TryParse(val.ToString(), out bool parsed)) return parsed;
                        try { return Convert.ToBoolean(val); } catch { }
                    }
                }

                var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                if (f != null)
                {
                    var val = f.GetValue(target);
                    if (val is bool b2) return b2;
                    if (val != null)
                    {
                        if (bool.TryParse(val.ToString(), out bool parsed2)) return parsed2;
                        try { return Convert.ToBoolean(val); } catch { }
                    }
                }
            }
            catch
            {
                // ignore and try next
            }
        }
        return defaultValue;
    }

    // PersistCitiesToPluginFolder: writes canonical JSON next to the plugin DLL
    // By default (forceWrite = false) this writes only if the canonical JSON file is missing.
    // If you pass forceWrite = true it will overwrite existing JSON.
    public static void PersistCitiesToPluginFolder()
    {
        try
        {
            var path = TravelButtonPlugin.GetCitiesJsonPath();
            var root = new JObject();
            var jCities = new JArray();

            foreach (var city in TravelButton.Cities ?? Enumerable.Empty<City>())
            {
                // Ensure runtime objects have non-null fields
                if (city.variants == null) city.variants = new string[0];
                if (city.lastKnownVariant == null) city.lastKnownVariant = "";

                jCities.Add(TravelButton.BuildJObjectForCity(city));
            }

            root["cities"] = jCities;

            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToString(Formatting.Indented));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception exReplace)
            {
                TBLog.Warn("PersistCitiesToPluginFolder: File.Replace fallback: " + exReplace.Message);
                File.Copy(temp, path, true);
                File.Delete(temp);
            }

            TBLog.Info($"PersistCitiesToPluginFolder: canonical JSON persisted to {path} (cities={TravelButton.Cities?.Count ?? 0}).");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PersistCitiesToPluginFolder: " + ex.Message);
        }
    }

    // New: update only visited flag and coords in the canonical JSON, preserve all other fields untouched.
    // If a runtime city is not present, it will be appended.
    public static bool PersistVisitedOnly()
    {
        try
        {
            TBLog.Info("PersistVisitedAndCoordsOnly: begin");

            var jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
            if (string.IsNullOrEmpty(jsonPath))
            {
                TBLog.Warn("PersistVisitedAndCoordsOnly: canonical path empty");
                return false;
            }

            JObject root;
            if (File.Exists(jsonPath))
            {
                // load existing JSON
                try
                {
                    string txt = File.ReadAllText(jsonPath);
                    root = string.IsNullOrWhiteSpace(txt) ? new JObject() : JObject.Parse(txt);
                }
                catch (Exception ex)
                {
                    TBLog.Warn("PersistVisitedAndCoordsOnly: failed to read/parse existing JSON, aborting: " + ex);
                    return false;
                }
            }
            else
            {
                // nothing exists -> create new structure
                root = new JObject();
                root["cities"] = new JArray();
            }

            // Ensure cities array exists
            if (!(root["cities"] is JArray citiesArray))
            {
                citiesArray = new JArray();
                root["cities"] = citiesArray;
            }

            // Build lookup by name for quick matching
            var jsonIndexByName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in citiesArray.Children())
            {
                if (token is JObject jo)
                {
                    var name = jo.Value<string>("name") ?? jo.Value<string>("Name");
                    if (!string.IsNullOrEmpty(name) && !jsonIndexByName.ContainsKey(name))
                        jsonIndexByName[name] = jo;
                }
            }

            // For each runtime city update visited and coords
            foreach (var c in TravelButton.Cities)
            {
                if (c == null) continue;
                string name = c.name ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                JObject jo;
                if (!jsonIndexByName.TryGetValue(name, out jo))
                {
                    // create a new minimal object with name + visited + coords
                    jo = new JObject();
                    jo["name"] = name;
                    citiesArray.Add(jo);
                    jsonIndexByName[name] = jo;
                }

                // update visited
                bool visitedVal = GetBoolMemberOrDefault(c, false, "visited", "Visited");
                jo["visited"] = visitedVal;
            }

            // Write back canonical JSON (indented)
            try
            {
                var dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }

            // Write back canonical JSON (indented)
            try
            {
                var jsonDto = JsonTravelConfig.Default();
                int jsonDtoCount = jsonDto?.cities?.Count ?? 0;
                TBLog.Info($"Persist default TravelButton_Cities.json: JsonTravelConfig.Default produced {jsonDtoCount} entries.");

                if (jsonDtoCount == 0)
                {
                    TBLog.Warn("Persist default: JsonTravelConfig.Default produced 0 entries; attempting to write constructed JSON root instead.");

                    // If we have entries in the citiesArray we built earlier, persist that root (contains visited flags)
                    try
                    {
                        if (citiesArray != null && citiesArray.Count > 0)
                        {
                            var dir = Path.GetDirectoryName(jsonPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            File.WriteAllText(jsonPath, root.ToString(Formatting.Indented), System.Text.Encoding.UTF8);
                            TBLog.Info($"PersistVisitedAndCoordsOnly: wrote constructed JSON root to: {jsonPath}");
                            return true;
                        }
                        else
                        {
                            TBLog.Warn("Persist default: no cities to write (citiesArray empty) — skipping write.");
                            return false;
                        }
                    }
                    catch (Exception exWriteFallback)
                    {
                        TBLog.Warn("Persist default: failed writing constructed JSON root: " + exWriteFallback);
                        return false;
                    }
                }
                else
                {
                    // Merge runtime visited flags (and coords if you want) into DTO entries by name (case-insensitive)
                    try
                    {
                        var map = new Dictionary<string, JsonCityConfig>(StringComparer.OrdinalIgnoreCase);
                        foreach (var j in jsonDto.cities)
                        {
                            if (j?.name != null) map[j.name] = j;
                        }

                        var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable ?? new List<object>();
                        foreach (var c in citiesEnum)
                        {
                            if (c == null) continue;
                            // attempt to read name and visited via reflection to support runtime types
                            string name = null;
                            bool? visited = null;
                            try
                            {
                                var t = c.GetType();
                                var pName = t.GetProperty("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                                if (pName != null) name = pName.GetValue(c) as string;
                                else
                                {
                                    var fName = t.GetField("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                                    if (fName != null) name = fName.GetValue(c) as string;
                                }
                                var pVisited = t.GetProperty("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)
                                               ?? t.GetProperty("Visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                                if (pVisited != null)
                                {
                                    var v = pVisited.GetValue(c);
                                    if (v is bool b) visited = b;
                                }
                                else
                                {
                                    var fVisited = t.GetField("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)
                                                   ?? t.GetField("Visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                                    if (fVisited != null)
                                    {
                                        var v = fVisited.GetValue(c);
                                        if (v is bool b2) visited = b2;
                                    }
                                }
                            }
                            catch { }

                            if (!string.IsNullOrEmpty(name) && visited.HasValue)
                            {
                                if (map.TryGetValue(name, out var jc))
                                {
                                    jc.visited = visited.Value;
                                }
                            }
                        }
                    }
                    catch (Exception exMerge)
                    {
                        TBLog.Warn("Persist default: failed merging visited flags into DTO: " + exMerge);
                    }

                    // Finally write the DTO via its SaveToJson (includes header and ensures cities array)
                    try
                    {
                        jsonDto.SaveToJson(jsonPath);
                        TBLog.Info("Wrote default TravelButton_Cities.json to: " + jsonPath);
                        return true;
                    }
                    catch (Exception exWrite)
                    {
                        TBLog.Warn("Failed to write default TravelButton_Cities.json: " + exWrite.Message);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("Error while persisting default TravelButton_Cities.json: " + ex);
                return false;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PersistVisitedAndCoordsOnly: unexpected error: " + ex);
            return false;
        }
    }

    // Small debug helper to log the canonical JSON contents (for verification)
    public static void DumpCanonicalJsonToLog()
    {
        try
        {
            var jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
            if (!File.Exists(jsonPath))
            {
                TBLog.Info("DumpCanonicalJsonToLog: canonical JSON not found at: " + jsonPath);
                return;
            }
            var txt = File.ReadAllText(jsonPath);
            TBLog.Info("DumpCanonicalJsonToLog: begin file contents ----------");
            foreach (var line in txt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                TBLog.Info("JSON: " + line);
            TBLog.Info("DumpCanonicalJsonToLog: end file contents ----------");
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpCanonicalJsonToLog: failed: " + ex.Message);
        }
    }

    private static object GetMemberRaw(object obj, string[] candidateNames)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        foreach (var name in candidateNames)
        {
            try
            {
                var prop = t.GetProperty(name, flags);
                if (prop != null)
                    return prop.GetValue(obj, null);

                var field = t.GetField(name, flags);
                if (field != null)
                    return field.GetValue(obj);
            }
            catch { /* ignore per-member reflection errors */ }
        }
        return null;
    }

    private static string GetStringMember(object obj, params string[] names)
    {
        var v = GetMemberRaw(obj, names);
        return v?.ToString();
    }

    private static int? GetNullableIntMember(object obj, params string[] names)
    {
        var v = GetMemberRaw(obj, names);
        if (v == null) return null;
        try
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is string s && int.TryParse(s, out var p)) return p;
            return Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static float[] GetFloatArrayMember(object obj, params string[] names)
    {
        var v = GetMemberRaw(obj, names);
        if (v == null) return null;
        try
        {
            if (v is float[] fa) return fa;
            if (v is double[] da) return Array.ConvertAll(da, d => (float)d);
            if (v is System.Collections.IList list)
            {
                var outList = new List<float>();
                foreach (var it in list)
                {
                    if (it == null) continue;
                    if (it is float f) outList.Add(f);
                    else if (it is double d) outList.Add((float)d);
                    else
                    {
                        if (float.TryParse(it.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv))
                            outList.Add(fv);
                    }
                }
                return outList.Count > 0 ? outList.ToArray() : null;
            }

            // UnityEngine.Vector3 (or similar) handling - check both property and field members
            var vt = v.GetType();
            if (vt != null && (vt.FullName == "UnityEngine.Vector3" || vt.Name == "Vector3"))
            {
                // Try property first
                var xProp = vt.GetProperty("x", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var yProp = vt.GetProperty("y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var zProp = vt.GetProperty("z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (xProp != null && yProp != null && zProp != null)
                {
                    float x = Convert.ToSingle(xProp.GetValue(v, null));
                    float y = Convert.ToSingle(yProp.GetValue(v, null));
                    float z = Convert.ToSingle(zProp.GetValue(v, null));
                    return new float[] { x, y, z };
                }

                // Fallback to fields
                var xField = vt.GetField("x", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var yField = vt.GetField("y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var zField = vt.GetField("z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (xField != null && yField != null && zField != null)
                {
                    float x = Convert.ToSingle(xField.GetValue(v));
                    float y = Convert.ToSingle(yField.GetValue(v));
                    float z = Convert.ToSingle(zField.GetValue(v));
                    return new float[] { x, y, z };
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Set (or add) an entry CityName.Visited = true/false under [TravelButton.Cities]
    /// in cz.valheimskal.travelbutton.cfg located under Paths.ConfigPath (if available).
    /// If the file doesn't exist it will be created. This is intentionally tolerant and
    /// will not throw on minor parse errors; it will log the outcome.
    /// </summary>
    public static void WriteVisitedFlagToCfg(string cityName, bool visitedValue)
    {
        try
        {
            // Determine cfg file path (prefer BepInEx Paths.ConfigPath)
            string cfgFile = null;
            try
            {
                cfgFile = System.IO.Path.Combine(Paths.ConfigPath, TravelButtonPlugin.LegacyCfgFileName);
            }
            catch
            {
                cfgFile = TravelButton.ConfigFilePath; // fallback
            }

            if (string.IsNullOrEmpty(cfgFile) || cfgFile == "(unknown)")
            {
                TBLog.Warn("WriteVisitedFlagToCfg: config path unknown; skipping write.");
                return;
            }

            List<string> lines = new List<string>();
            if (File.Exists(cfgFile))
            {
                try
                {
                    lines = new List<string>(File.ReadAllLines(cfgFile));
                }
                catch (Exception ex)
                {
                    TBLog.Warn("WriteVisitedFlagToCfg: failed to read existing cfg file: " + ex.Message);
                    // start with empty
                    lines = new List<string>();
                }
            }
            else
            {
                // Ensure directory exists
                try
                {
                    var dir = Path.GetDirectoryName(cfgFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch { /* ignore */ }
            }

            const string targetSection = "TravelButton.Cities";
            string sectionHeader = "[" + targetSection + "]";

            // Parse file into pre-section, section lines, post-section
            int sectionStart = -1;
            int sectionEnd = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionStart = i;
                        // find section end
                        int j = i + 1;
                        for (; j < lines.Count; j++)
                        {
                            var t = lines[j].Trim();
                            if (t.StartsWith("[") && t.EndsWith("]")) break;
                        }
                        sectionEnd = (j < lines.Count) ? j : lines.Count;
                        break;
                    }
                }
            }

            // Build kv from existing section (if any)
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sectionStart >= 0)
            {
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var raw = lines[i];
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var lt = raw.Trim();
                    if (lt.StartsWith("#") || lt.StartsWith(";")) continue;
                    int eq = lt.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = lt.Substring(0, eq).Trim().Trim('"');
                    var val = lt.Substring(eq + 1).Trim();
                    // strip inline comments
                    int commentIdx = val.IndexOf('#');
                    if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();
                    commentIdx = val.IndexOf(';');
                    if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();
                    val = val.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(key))
                    {
                        kv[key] = val;
                    }
                }
            }

            // Update the specific key
            var keyName = $"{cityName}.Visited";
            kv[keyName] = visitedValue ? "true" : "false";

            // Reconstruct file lines
            var outLines = new List<string>();

            if (sectionStart >= 0)
            {
                // keep pre-section
                for (int i = 0; i < sectionStart; i++) outLines.Add(lines[i]);
            }
            else
            {
                // include all existing lines before adding section
                outLines.AddRange(lines);
            }

            // Ensure there's a blank line before the section if file not empty
            if (outLines.Count > 0 && !string.IsNullOrWhiteSpace(outLines[outLines.Count - 1])) outLines.Add("");

            // Write section header
            outLines.Add(sectionHeader);

            // Write keys (preserve existing order where possible: existing keys first, then any new)
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sectionStart >= 0)
            {
                // preserve ordering of old keys as much as possible
                for (int i = sectionStart + 1; i < sectionEnd; i++)
                {
                    var raw = lines[i];
                    var lt = raw.Trim();
                    if (lt.StartsWith("#") || lt.StartsWith(";") || lt.Length == 0)
                    {
                        outLines.Add(lines[i]);
                        continue;
                    }
                    int eq = lt.IndexOf('=');
                    if (eq <= 0)
                    {
                        outLines.Add(lines[i]);
                        continue;
                    }
                    var key = lt.Substring(0, eq).Trim().Trim('"');
                    if (kv.TryGetValue(key, out var v))
                    {
                        outLines.Add($"{key} = {v}");
                        written.Add(key);
                    }
                    else
                    {
                        // if key was removed from kv (unlikely), skip it
                    }
                }
            }

            // Write remaining keys that were not present
            foreach (var pair in kv)
            {
                if (written.Contains(pair.Key)) continue;
                outLines.Add($"{pair.Key} = {pair.Value}");
            }

            // Add post-section content: if sectionEnd was determined, append the rest of original file after sectionEnd
            if (sectionStart >= 0 && sectionEnd < lines.Count)
            {
                outLines.Add(""); // separate
                for (int i = sectionEnd; i < lines.Count; i++)
                    outLines.Add(lines[i]);
            }

            // Finally write file
            try
            {
                File.WriteAllText(cfgFile, string.Join(Environment.NewLine, outLines), System.Text.Encoding.UTF8);
                TBLog.Info($"WriteVisitedFlagToCfg: wrote visited flag for '{cityName}' = {visitedValue} to cfg: {cfgFile}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("WriteVisitedFlagToCfg: failed to write cfg file: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("WriteVisitedFlagToCfg: unexpected error: " + ex.Message);
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
    // file: src/TravelButtonMod.cs (or wherever TravelButtonMod is implemented)
    // add or update OnSuccessfulTeleport
    public static void OnSuccessfulTeleport(string cityName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cityName)) return;
            if (Cities == null) return;

            var city = Cities.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
            if (city != null)
            {
                // set a persistent visited flag on the city configuration
                // If your City class has a field/property named 'visited' or similar, set it; otherwise add one.
                try
                {
                    var f = city.GetType().GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        f.SetValue(city, true);
                    }
                    else
                    {
                        var p = city.GetType().GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                            p.SetValue(city, true, null);
                        else
                        {
                            // fallback: try to set a field named 'enabled' or 'discovered' if you use different naming
                            var f2 = city.GetType().GetField("enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (f2 != null && (f2.FieldType == typeof(bool))) f2.SetValue(city, true);
                        }
                    }
                }
                catch { /* ignore reflection issues; still attempt persistence below */ }

                // persist the city config (existing plugin method)
                try { PersistCitiesToPluginFolder(); } catch (Exception ex) { TBLog.Warn("OnSuccessfulTeleport: PersistCitiesToConfig failed: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("OnSuccessfulTeleport exception: " + ex);
        }
    }

    // --- Diagnostic helpers requested: DumpTravelButtonState and ForceShowTravelButton ---

    // Dumps TravelButton GameObject state (parent, canvas, rect, image, button, canvasGroup) to the mod log.
    public static void DumpTravelButtonState()
    {
        try
        {
            var tb = GameObject.Find("TravelButton");
            if (tb == null)
            {
                TBLog.Warn("DumpTravelButtonState: TravelButton GameObject not found.");
                return;
            }

            var rt = tb.GetComponent<RectTransform>();
            var btn = tb.GetComponent<Button>();
            var img = tb.GetComponent<Image>();
            var cg = tb.GetComponent<CanvasGroup>();
            var root = tb.transform.root;
            TBLog.Info($"DumpTravelButtonState: name='{tb.name}', activeSelf={tb.activeSelf}, activeInHierarchy={tb.activeInHierarchy}");
            TBLog.Info($"DumpTravelButtonState: parent='{tb.transform.parent?.name}', root='{root?.name}'");
            if (rt != null) TBLog.Info($"DumpTravelButtonState: anchoredPosition={rt.anchoredPosition}, sizeDelta={rt.sizeDelta}, anchorMin={rt.anchorMin}, anchorMax={rt.anchorMax}, pivot={rt.pivot}");
            if (btn != null) TBLog.Info($"DumpTravelButtonState: Button.interactable={btn.interactable}");
            if (img != null) TBLog.Info($"DumpTravelButtonState: Image.color={img.color}, raycastTarget={img.raycastTarget}");
            if (cg != null) TBLog.Info($"DumpTravelButtonState: CanvasGroup alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts}");
            var canvas = tb.GetComponentInParent<Canvas>();
            if (canvas != null) TBLog.Info($"DumpTravelButtonState: Canvas name={canvas.gameObject.name}, sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode}");
            else TBLog.Warn("DumpTravelButtonState: No parent Canvas found.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpTravelButtonState exception: " + ex.Message);
        }
    }

    // Helper: try to read a bool key in a specific [section] from a BepInEx .cfg file.
    // Example cfg snippet:
    // [TravelButton.Cities]
    // Sirocco.Enabled = true
    // Returns true if a boolean value was found and parsed; false otherwise.
    public static bool TryReadBoolFromCfgFile(string cfgFilePath, string sectionName, string keyName, out bool value)
    {
        value = false;
        try
        {
            if (string.IsNullOrEmpty(cfgFilePath) || !File.Exists(cfgFilePath))
                return false;

            bool inSection = false;
            foreach (var raw in File.ReadLines(cfgFilePath))
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith(";") || line.StartsWith("#")) continue;

                // Section header
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    var sec = line.Substring(1, line.Length - 2).Trim();
                    inSection = string.Equals(sec, sectionName, StringComparison.InvariantCultureIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                // Key = value  (split on first '=')
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var left = line.Substring(0, idx).Trim();
                var right = line.Substring(idx + 1).Trim();

                // Remove inline comments after ';' if present
                var semicolon = right.IndexOf(';');
                if (semicolon >= 0) right = right.Substring(0, semicolon).Trim();

                if (string.Equals(left, keyName, StringComparison.InvariantCultureIgnoreCase))
                {
                    // Try parse boolean
                    if (bool.TryParse(right, out var parsed))
                    {
                        value = parsed;
                        return true;
                    }
                    // sometimes values are stored like "1" or "0"
                    if (int.TryParse(right, out var ival))
                    {
                        value = ival != 0;
                        return true;
                    }
                    // unknown format -> ignore
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            try { TBLog.Warn($"TryReadBoolFromCfgFile failed: {ex.Message}"); } catch { }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the current player save indicates the given cityId/name has been visited.
    /// This is best-effort reflection: it will search loaded assemblies for likely SaveManager/PlayerSave types
    /// and look for member collections that match common names. Comparison is case-insensitive.
    /// </summary>
    // Added: runtime check whether the player has visited a given city.
    // Usage: call HasPlayerVisited(city.name) when building the dialog.
    // This will try several identifiers (city.name, city.sceneName, city.targetGameObjectName)
    // and will log the discovered save root, visited member and sample values to help debug mismatches.
    // Return the inner per-character save object (e.g. CharacterSaveInstanceHolder.Save / CharacterSaveInstanceHolder.characterSave / .SaveData).
    // Looks up SaveManager.Instance -> m_charSaves (dictionary) -> first value -> inner save object.
    private static object GetFirstCharacterInnerSave()
    {
        try
        {
            var saveRoot = FindSaveRootInstance();
            if (saveRoot == null) return null;

            var rootType = saveRoot.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;

            // Get the charSaves container
            object charSaves = null;
            var f = rootType.GetField("m_charSaves", flags) ?? rootType.GetField("charSaves", flags);
            if (f != null) charSaves = f.GetValue(saveRoot);
            else
            {
                var p = rootType.GetProperty("CharacterSaves", flags) ?? rootType.GetProperty("characterSaves", flags);
                if (p != null) charSaves = p.GetValue(saveRoot, null);
            }
            if (charSaves == null) return null;

            // If dictionary-like, try to obtain Values or m_values
            if (charSaves is System.Collections.IDictionary dict)
            {
                // get first value
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    var holder = kv.Value;
                    if (holder == null) continue;
                    // try to get holder inner save
                    var inner = TryGetMemberValue(holder, "Save") ?? TryGetMemberValue(holder, "characterSave")
                                ?? TryGetMemberValue(holder, "CharacterSave") ?? TryGetMemberValue(holder, "SaveData")
                                ?? TryGetMemberValue(holder, "data");
                    if (inner != null) return inner;
                    // if inner not found, return holder itself (we can probe it later)
                    return holder;
                }
                return null;
            }

            // If it exposes Values/ValuesArray property (DictionaryExt)
            var valuesProp = charSaves.GetType().GetProperty("Values", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (valuesProp != null)
            {
                var valuesObj = valuesProp.GetValue(charSaves, null);
                if (valuesObj is System.Collections.IEnumerable valuesEnum)
                {
                    foreach (var holder in valuesEnum)
                    {
                        if (holder == null) continue;
                        var inner = TryGetMemberValue(holder, "Save") ?? TryGetMemberValue(holder, "characterSave")
                                    ?? TryGetMemberValue(holder, "CharacterSave") ?? TryGetMemberValue(holder, "SaveData")
                                    ?? TryGetMemberValue(holder, "data");
                        if (inner != null) return inner;
                        return holder;
                    }
                }
            }

            // As fallback, if object has m_values list/array field
            var mValuesField = charSaves.GetType().GetField("m_values", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
            if (mValuesField != null)
            {
                var mv = mValuesField.GetValue(charSaves);
                if (mv is System.Collections.IEnumerable mvEnum)
                {
                    foreach (var holder in mvEnum)
                    {
                        if (holder == null) continue;
                        var inner = TryGetMemberValue(holder, "Save") ?? TryGetMemberValue(holder, "characterSave")
                                    ?? TryGetMemberValue(holder, "CharacterSave") ?? TryGetMemberValue(holder, "SaveData")
                                    ?? TryGetMemberValue(holder, "data");
                        if (inner != null) return inner;
                        return holder;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            TBLog.Warn("GetFirstCharacterInnerSave failed: " + ex.Message);
            return null;
        }
    }

    // Try to extract a visited/discovered collection from the provided save-like object.
    // Returns flattened List<object> or null.
    private static List<object> GetVisitedCollectionFromSaveObject(object saveObj)
    {
        if (saveObj == null) return null;
        try
        {
            var t = saveObj.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;

            // Candidate names (common in many games)
            var visitedCandidates = new[]
            {
            "visitedLocations","discoveredLocations","discovered","visitedCities","discoveredCities",
            "knownLocations","knownDestinations","visited","discoveredIds","visitedIds","visitedLocationNames",
            "visitedNodes","discoveredNodes","visitedLocationDictionary","visitedLocationsByName"
        };

            foreach (var name in visitedCandidates)
            {
                try
                {
                    var f = t.GetField(name, flags);
                    if (f != null)
                    {
                        var val = f.GetValue(saveObj);
                        var items = ConvertToObjectList(val);
                        if (items != null && items.Count > 0) return items;
                    }
                    var p = t.GetProperty(name, flags);
                    if (p != null)
                    {
                        var val = p.GetValue(saveObj, null);
                        var items = ConvertToObjectList(val);
                        if (items != null && items.Count > 0) return items;
                    }
                }
                catch { /* ignore per-member errors */ }
            }

            // If none matched by name, scan for dictionaries and lists and prefer Dictionary<string, ?> or HashSet-like
            foreach (var f in t.GetFields(flags))
            {
                try
                {
                    var val = f.GetValue(saveObj);
                    var items = ConvertToObjectList(val);
                    if (items != null && items.Count > 0)
                    {
                        // prefer dictionary keys if dictionary
                        if (val is System.Collections.IDictionary) return items;
                        // otherwise keep as candidate (return first found)
                        return items;
                    }
                }
                catch { }
            }
            foreach (var p in t.GetProperties(flags))
            {
                try
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var val = p.GetValue(saveObj, null);
                    var items = ConvertToObjectList(val);
                    if (items != null && items.Count > 0)
                    {
                        if (val is System.Collections.IDictionary) return items;
                        return items;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("GetVisitedCollectionFromSaveObject failed: " + ex.Message);
        }
        return null;
    }

    private static readonly string[] SaveManagerTypeNameCandidates = new[]
    {
    "SaveManager", "PlayerSave", "PlayerProfile", "SaveData", "SaveSystem", "ProfileManager",
    "GameSave", "WorldState", "SaveManagerBase"
    };

    private static readonly string[] SaveInstanceMemberCandidates = new[]
    {
    "Instance", "instance", "Current", "CurrentSave", "Save", "SaveData", "Profile", "Data"
    };

    private static readonly string[] VisitedMemberCandidates = new[]
    {
    "visitedLocations", "discoveredLocations", "discovered", "visitedCities", "discoveredCities",
    "knownLocations", "knownDestinations", "visited", "discoveredIds", "visitedIds"
    };

    private static readonly string[] HasVisitedAlternateKeys = new[]
    {
    "name", "sceneName", "targetGameObjectName"
    };

    private static readonly object s_cityVisitedLock = new object();
    private static Dictionary<string, bool> s_cityVisitedCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clear per-city visited cache (call before rebuilding dialog or when save changes).
    /// </summary>
    public static void ClearCityVisitedCache()
    {
        lock (s_cityVisitedLock)
        {
            s_cityVisitedCache.Clear();
        }
        TBLog.Info("ClearCityVisitedCache: cleared per-city visited cache.");
    }

    public static void DumpVisitedKeys()
    {
        try
        {
            var fld = typeof(TravelButton).GetField("s_visitedKeysSet", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (fld == null)
            {
                TBLog.Info("DumpVisitedKeys: s_visitedKeysSet field not found.");
                return;
            }
            var set = fld.GetValue(null) as System.Collections.IEnumerable;
            if (set == null)
            {
                TBLog.Info("DumpVisitedKeys: visited set is null or empty.");
                return;
            }
            int i = 0;
            foreach (var item in set)
            {
                if (i++ >= 50) break;
                TBLog.Info($"DumpVisitedKeys #{i}: '{item?.ToString() ?? "(null)"}'");
            }
            if (i == 0) TBLog.Info("DumpVisitedKeys: visited set empty.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpVisitedKeys failed: " + ex.Message);
        }
    }

    // PERFORMANCE: cache of visited keys to avoid repeated reflection per city
    private static object s_visitedLock = new object();
    private static HashSet<string> s_cachedVisitedKeys = null; // case-insensitive
    private static object s_cachedSaveRootRef = null; // reference to SaveManager.Instance used to build the cache

    

    // Fast per-city check using precomputed set. Very cheap (O(1) per city).
    // If cache is missing, it can optionally call legacy HasPlayerVisited as a fallback.
    // Fast cached lookup. If the cache is missing, try to build it once and re-check.
    // Returns false if no match and no cache could be built.
    // Replace/insert this in TravelButtonMod.cs (same class as existing helpers).
    // Replace existing HasPlayerVisitedFast implementation with this version.
    // This implementation is defensive, caches results, builds the visited set once per cache flush,
    // avoids repeated heavy reflection, and reduces log spam.

    private static readonly object s_visitedCacheLock = new object();
    private static Dictionary<string, bool> s_hasVisitedCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
//    private static HashSet<string> s_visitedKeysSet = null; // normalized lowercase keys extracted from save
//    private static bool s_visitedKeysSetInitialized = false;
    private static bool s_visitedTrackerReflectionFailed = false;

    /// <summary>
    /// Fast, cached visited check. Uses a one-time-built visited key set (from SaveManager / character/world save)
    /// to answer repeated queries quickly. Falls back to a single (quiet) attempt at VisitedTracker.HasVisited
    /// only if no save-based visited keys were found.
    /// </summary>
    // --- REPLACE the initialization block in HasPlayerVisitedFast with this (entire function shown) ---

    public static bool HasPlayerVisitedFast(string cityId)
    {
        if (string.IsNullOrEmpty(cityId)) return false;

        // normalize lookup key
        string key = cityId.Trim();

        // quick cache hit (original cache)
        lock (s_visitedCacheLock)
        {
            if (s_hasVisitedCache.TryGetValue(key, out bool cachedValue))
                return cachedValue;
        }

        // Ensure persistent visited lookup is prepared once (cheap no-op on subsequent calls)
        try
        {
            PrepareVisitedLookup();
        }
        catch (Exception ex)
        {
            TBLog.Warn("HasPlayerVisitedFast: PrepareVisitedLookup failed: " + ex.Message);
            // ensure non-null safe fallback
            if (s_visitedKeysSet == null)
                s_visitedKeysSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            s_visitedKeysSetInitialized = true;
        }

        // Now check the built set for direct matches against likely ids.
        bool result = false;
        try
        {
            // Direct membership (case-insensitive)
            if (s_visitedKeysSet != null && s_visitedKeysSet.Contains(key))
            {
                result = true;
            }
            else
            {
                PrepareVisitedLookup();

                // Precompute normalized forms so they're in scope for all checks
                string keyLower = key.ToLowerInvariant();
                string keyNoSpace = keyLower.Replace("_", "").Replace(" ", "");

                // direct substring match: visited-key may contain city name as part of string
                if (s_visitedKeysSet != null && s_visitedKeysSet.Count > 0)
                {
                    // Direct contains checks (s_visitedKeysSet was built with OrdinalIgnoreCase so case is not important)
                    if (s_visitedKeysSet.Contains(key) || s_visitedKeysSet.Contains(keyLower) || s_visitedKeysSet.Contains(keyNoSpace))
                    {
                        result = true;
                    }
                    else
                    {
                        // Iterate saved keys and try more flexible matches (substring, normalized no-space)
                        foreach (var saved in s_visitedKeysSet)
                        {
                            if (string.IsNullOrEmpty(saved)) continue;
                            var savedLower = saved.ToLowerInvariant();

                            // exact / substring / reverse-substring checks
                            if (savedLower == keyLower || savedLower.Contains(keyLower) || keyLower.Contains(savedLower))
                            {
                                result = true;
                                break;
                            }

                            // also try matching ignoring separators/underscores
                            var savedNoSpace = savedLower.Replace("_", "").Replace(" ", "");
                            if (savedNoSpace == keyNoSpace)
                            {
                                result = true;
                                break;
                            }
                        }
                    }
                }

                // As an additional fallback, normalize common separators
                if (!result && s_visitedKeysSet != null && s_visitedKeysSet.Count > 0)
                {
                    var alt = keyLower.Replace("_", "").Replace(" ", "");
                    foreach (var k in s_visitedKeysSet)
                    {
                        if (string.IsNullOrEmpty(k)) continue;
                        var kl = k.ToLowerInvariant().Replace("_", "").Replace(" ", "");
                        if (kl == alt)
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("HasPlayerVisitedFast: error while checking visited set: " + ex.Message);
            result = false;
        }

        // If visited set is empty/uninformative, try a single quiet call to VisitedTracker.HasVisited (but only once)
        if (!result && (s_visitedKeysSet == null || s_visitedKeysSet.Count == 0) && !s_visitedTrackerReflectionFailed)
        {
            try
            {
                var vtType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "VisitedTracker");
                if (vtType != null)
                {
                    var hasVisitedMethod = vtType.GetMethod("HasVisited", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (hasVisitedMethod != null)
                    {
                        var res = hasVisitedMethod.Invoke(null, new object[] { key });
                        if (res is bool b && b) result = true;
                    }
                }
            }
            catch (TargetInvocationException tie)
            {
                // Reflection target threw; avoid trying again and log a single warning
                s_visitedTrackerReflectionFailed = true;
                TBLog.Warn("HasPlayerVisitedFast: VisitedTracker.HasVisited invocation failed: " + (tie.InnerException?.Message ?? tie.Message));
            }
            catch (Exception ex)
            {
                s_visitedTrackerReflectionFailed = true;
                TBLog.Warn("HasPlayerVisitedFast: VisitedTracker fallback failed: " + ex.Message);
            }
        }

        // Cache result for this key so subsequent checks are fast
        lock (s_visitedCacheLock)
        {
            try { s_hasVisitedCache[key] = result; } catch { }
        }

        // Sparse diagnostic log
        if (!result)
        {
            TBLog.Info($"HasPlayerVisitedFast: returning false for '{cityId}' (visitedKeysCount={s_visitedKeysSet?.Count ?? 0})");
        }

        return result;
    }

    /// <summary>
    /// Call this when you know saved visited state changed (e.g., after loading a save or marking a city visited)
    /// to force the cache to be rebuilt on next HasPlayerVisitedFast call.
    /// </summary>
    public static void ClearVisitedCache()
    {
        try
        {
            TBLog.Info("ClearVisitedCache: cleared visited caches and reset visited lookup.");
            _visitedLookupPrepared = false;

            if (_visitedLookup == null)
                _visitedLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            else
                _visitedLookup.Clear();
        }
        catch (Exception ex)
        {
            TBLog.Warn("ClearVisitedCache: error while clearing visited cache: " + ex);
            _visitedLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _visitedLookupPrepared = false;
        }
    }

    // Probe an object for likely visited/discovered members and log their contents (small sample)
    private static void ProbeForVisitedMembers(object obj)
    {
        if (obj == null) return;
        try
        {
            var visitedNames = new[] {
            "visitedLocations","discoveredLocations","discovered","visitedCities","discoveredCities",
            "knownLocations","knownDestinations","visited","discoveredIds","visitedIds","visitedLocationNames",
            "visitedNodes","discoveredNodes","visitedLocationDictionary"
        };

            var t = obj.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;
            foreach (var name in visitedNames)
            {
                try
                {
                    var f = t.GetField(name, flags);
                    if (f != null)
                    {
                        var val = f.GetValue(obj);
                        var list = ConvertToObjectList(val);
                        TBLog.Info($"ProbeForVisitedMembers: found field '{name}' ({f.FieldType.FullName}) -> items={(list == null ? 0 : list.Count)}, sample=[{(list == null ? "" : string.Join(", ", list.Take(8).Select(x => x?.ToString() ?? "<null>")))}]");
                        continue;
                    }
                    var p = t.GetProperty(name, flags);
                    if (p != null)
                    {
                        var val = p.GetValue(obj, null);
                        var list = ConvertToObjectList(val);
                        TBLog.Info($"ProbeForVisitedMembers: found prop '{name}' ({p.PropertyType.FullName}) -> items={(list == null ? 0 : list.Count)}, sample=[{(list == null ? "" : string.Join(", ", list.Take(8).Select(x => x?.ToString() ?? "<null>")))}]");
                        continue;
                    }
                }
                catch { /* ignore per member */ }
            }

            // Also look for any Dictionary<string, ?> or HashSet-like fields/properties and report a sample
            foreach (var f in t.GetFields(flags))
            {
                try
                {
                    var val = f.GetValue(obj);
                    if (val is System.Collections.IDictionary dict)
                    {
                        TBLog.Info($"ProbeForVisitedMembers: dictionary field '{f.Name}' type={val.GetType().FullName} count={dict.Count} sampleKeys=[{string.Join(", ", dict.Keys.Cast<object>().Take(8).Select(k => k?.ToString() ?? "<null>"))}]");
                    }
                }
                catch { }
            }
            foreach (var p in t.GetProperties(flags))
            {
                try
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var val = p.GetValue(obj, null);
                    if (val is System.Collections.IDictionary dict)
                    {
                        TBLog.Info($"ProbeForVisitedMembers: dictionary prop '{p.Name}' type={val.GetType().FullName} count={dict.Count} sampleKeys=[{string.Join(", ", dict.Keys.Cast<object>().Take(8).Select(k => k?.ToString() ?? "<null>"))}]");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ProbeForVisitedMembers failed: " + ex.Message);
        }
    }

    // Diagnostic: dump first character save holder and its nested members
    public static void DumpFirstCharacterSaveHolder()
    {
        try
        {
            var saveRoot = FindSaveRootInstance();
            if (saveRoot == null)
            {
                TBLog.Info("DumpFirstCharacterSaveHolder: saveRoot null");
                return;
            }

            // Try to get CharacterSaves property/field
            var t = saveRoot.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;

            object charSavesObj = null;
            var field = t.GetField("m_charSaves", flags) ?? t.GetField("charSaves", flags);
            if (field != null) charSavesObj = field.GetValue(saveRoot);
            else
            {
                var prop = t.GetProperty("CharacterSaves", flags) ?? t.GetProperty("characterSaves", flags);
                if (prop != null) charSavesObj = prop.GetValue(saveRoot, null);
            }

            if (charSavesObj == null)
            {
                TBLog.Info("DumpFirstCharacterSaveHolder: CharacterSaves not present on save root");
                return;
            }

            // charSavesObj is IList<CharacterSaveInstanceHolder> or similar
            var list = charSavesObj as System.Collections.IEnumerable;
            if (list == null)
            {
                TBLog.Info("DumpFirstCharacterSaveHolder: CharacterSaves is not enumerable");
                return;
            }

            // take first element
            object first = null;
            foreach (var it in list)
            {
                first = it;
                break;
            }

            if (first == null)
            {
                TBLog.Info("DumpFirstCharacterSaveHolder: CharacterSaves empty");
                return;
            }

            TBLog.Info($"DumpFirstCharacterSaveHolder: first holder type = {first.GetType().FullName}");
            // Dump its fields/properties (limited)
            DumpObjectMembersSample(first, 6);
            // If there is an inner Save or SaveData or CharacterSave member, dump it too
            var inner = TryGetMemberValue(first, "Save") ?? TryGetMemberValue(first, "characterSave") ?? TryGetMemberValue(first, "SaveData") ?? TryGetMemberValue(first, "data");
            if (inner != null)
            {
                TBLog.Info($"DumpFirstCharacterSaveHolder: inner save/object type = {inner.GetType().FullName}");
                DumpObjectMembersSample(inner, 12);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpFirstCharacterSaveHolder failed: " + ex.Message);
        }
    }

    // Diagnostic: dump WorldSave object (if present)
    public static void DumpWorldSave()
    {
        try
        {
            var saveRoot = FindSaveRootInstance();
            if (saveRoot == null) { TBLog.Info("DumpWorldSave: saveRoot null"); return; }

            var t = saveRoot.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;

            var worldSaveProp = t.GetProperty("WorldSave", flags) ?? t.GetProperty("worldSave", flags);
            object worldSave = null;
            if (worldSaveProp != null) worldSave = worldSaveProp.GetValue(saveRoot, null);

            if (worldSave == null)
            {
                TBLog.Info("DumpWorldSave: WorldSave not present on save root");
                return;
            }

            TBLog.Info($"DumpWorldSave: WorldSave type = {worldSave.GetType().FullName}");
            DumpObjectMembersSample(worldSave, 12);
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpWorldSave failed: " + ex.Message);
        }
    }

    // Small utility: dump up to 'maxMembers' fields/properties of an object with collection sampling
    private static void DumpObjectMembersSample(object obj, int maxMembers)
    {
        if (obj == null) { TBLog.Info("DumpObjectMembersSample: obj null"); return; }
        try
        {
            var t = obj.GetType();
            TBLog.Info($"DumpObjectMembersSample: type={t.FullName}");

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            int count = 0;

            foreach (var f in t.GetFields(flags))
            {
                if (++count > maxMembers) break;
                object val = null;
                try { val = f.GetValue(obj); } catch (Exception ex) { TBLog.Info($" Field {f.Name}: <error {ex.Message}>"); continue; }
                DumpMemberSample($"Field {f.Name}", f.FieldType, val);
            }

            foreach (var p in t.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (++count > maxMembers) break;
                object val = null;
                try { val = p.GetValue(obj, null); } catch (Exception ex) { TBLog.Info($" Prop {p.Name}: <error {ex.Message}>"); continue; }
                DumpMemberSample($"Prop {p.Name}", p.PropertyType, val);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpObjectMembersSample failed: " + ex.Message);
        }
    }

    public static void DumpMemberSample(string label, Type memberType, object val)
    {
        try
        {
            if (val == null)
            {
                TBLog.Info($"{label} ({memberType.FullName}): null");
                return;
            }

            if (val is System.Collections.IDictionary dict)
            {
                var keys = new List<string>();
                int i = 0;
                foreach (var k in dict.Keys) { if (i++ >= 8) break; keys.Add(k?.ToString() ?? "<null>"); }
                TBLog.Info($"{label} ({memberType.FullName}): IDictionary count={dict.Count}, sampleKeys=[{string.Join(", ", keys)}]");
                return;
            }

            if (val is System.Collections.IEnumerable ie && !(val is string))
            {
                var items = new List<string>();
                int i = 0;
                foreach (var it in ie) { if (i++ >= 8) break; items.Add(it?.ToString() ?? "<null>"); }
                // try Count
                string cnt = "unknown";
                try { var cp = val.GetType().GetProperty("Count"); if (cp != null) cnt = cp.GetValue(val, null)?.ToString() ?? "0"; } catch { }
                TBLog.Info($"{label} ({memberType.FullName}): IEnumerable count={cnt}, sample=[{string.Join(", ", items)}]");
                return;
            }

            TBLog.Info($"{label} ({memberType.FullName}): value={val.ToString()}");
        }
        catch { /* ignore */ }
    }

    // Utility: try to read a field or property by several common names
    private static object TryGetMemberValue(object obj, string memberName)
    {
        if (obj == null) return null;
        try
        {
            var t = obj.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;
            var f = t.GetField(memberName, flags);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(memberName, flags);
            if (p != null) return p.GetValue(obj, null);
        }
        catch { }
        return null;
    }

    private static readonly object s_saveRootLock = new object();
    private static object s_cachedSaveRoot = null;
    private static DateTime s_saveRootCachedAt = DateTime.MinValue;
    private static readonly TimeSpan s_saveRootCacheTtl = TimeSpan.FromSeconds(10); // safety TTL if you want periodic refresh
    private static bool s_saveRootLogged = false;
    private static System.Collections.Generic.HashSet<string> s_pluginVisitedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Find the game's save root (SaveManager instance or equivalent).
    /// This implementation caches the result and is intentionally low-noise.
    /// Use ClearSaveRootCache() to force re-evaluation (e.g., after loading a save).
    /// </summary>
    public static object FindSaveRootInstance()
    {
        try
        {
            lock (s_saveRootLock)
            {
                // Return cached if still valid
                if (s_cachedSaveRoot != null)
                {
                    // Unity objects may be destroyed, verify if still alive when possible
                    try
                    {
                        var unityObj = s_cachedSaveRoot as UnityEngine.Object;
                        if (unityObj != null && unityObj == null) // destroyed
                        {
                            s_cachedSaveRoot = null;
                            s_saveRootLogged = false;
                        }
                        else
                        {
                            // TTL check: refresh occasionally in case the runtime replaced the instance
                            if (DateTime.UtcNow - s_saveRootCachedAt < s_saveRootCacheTtl)
                            {
                                return s_cachedSaveRoot;
                            }
                            // else fall through to a lightweight re-check below
                        }
                    }
                    catch
                    {
                        // If any verification fails, drop cache and recompute
                        s_cachedSaveRoot = null;
                        s_saveRootLogged = false;
                    }
                }

                // Attempt lightweight discovery strategies in order

                // 1) Try to find a type named SaveManager and read its public static Instance property/field (fast)
                try
                {
                    Type saveManagerType = null;
                    // Search loaded assemblies for a type called "SaveManager" (fast enumeration)
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            saveManagerType = asm.GetTypes().FirstOrDefault(t => t.Name == "SaveManager");
                            if (saveManagerType != null) break;
                        }
                        catch { /* ignore assemblies that can't enumerate types */ }
                    }

                    if (saveManagerType != null)
                    {
                        // try common property names
                        var prop = saveManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
                                   ?? saveManagerType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                        if (prop != null)
                        {
                            var inst = prop.GetValue(null, null);
                            if (inst != null)
                            {
                                s_cachedSaveRoot = inst;
                                s_saveRootCachedAt = DateTime.UtcNow;
                                if (!s_saveRootLogged)
                                {
                                    s_saveRootLogged = true;
                                    TBLog.Info("FindSaveRootInstance: found save root via property SaveManager.Instance");
                                }
                                return s_cachedSaveRoot;
                            }
                        }

                        // try a static field as fallback
                        var fld = saveManagerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
                                  ?? saveManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                        if (fld != null)
                        {
                            var inst = fld.GetValue(null);
                            if (inst != null)
                            {
                                s_cachedSaveRoot = inst;
                                s_saveRootCachedAt = DateTime.UtcNow;
                                if (!s_saveRootLogged)
                                {
                                    s_saveRootLogged = true;
                                    TBLog.Info("FindSaveRootInstance: found save root via field SaveManager.Instance");
                                }
                                return s_cachedSaveRoot;
                            }
                        }
                    }
                }
                catch
                {
                    // fail silently; we'll try other inexpensive methods
                }

                // 2) Try Resources.FindObjectsOfTypeAll for a likely SaveManager component/object
                try
                {
                    // This is slightly heavier, but we do it only when cache empty and only once per cache TTL.
                    var all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
                    foreach (var obj in all)
                    {
                        if (obj == null) continue;
                        var t = obj.GetType();
                        var name = t.Name ?? "";
                        // Heuristic: type name containing "Save" and "Manager" is likely the save root
                        if (name.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            name.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            s_cachedSaveRoot = obj;
                            s_saveRootCachedAt = DateTime.UtcNow;
                            if (!s_saveRootLogged)
                            {
                                s_saveRootLogged = true;
                                TBLog.Info($"FindSaveRootInstance: found save root by scanning objects (type='{name}').");
                            }
                            return s_cachedSaveRoot;
                        }
                    }
                }
                catch
                {
                    // ignore heavy fallback errors
                }

                // Nothing found
                s_cachedSaveRoot = null;
                s_saveRootCachedAt = DateTime.MinValue;
                // Do not spam logs when not found; only log first time to help debugging
                if (!s_saveRootLogged)
                {
                    s_saveRootLogged = true;
                    TBLog.Info("FindSaveRootInstance: save root not found (will attempt again later).");
                }
                return null;
            }
        }
        catch (Exception ex)
        {
            // Very defensive: never throw from this helper
            TBLog.Warn("FindSaveRootInstance: unexpected error: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Clear cached SaveRoot so next FindSaveRootInstance will re-scan.
    /// Call this when a save is loaded, when scenes change dramatically, or during debugging.
    /// </summary>
    public static void ClearSaveRootCache()
    {
        lock (s_saveRootLock)
        {
            s_cachedSaveRoot = null;
            s_saveRootCachedAt = DateTime.MinValue;
            s_saveRootLogged = false;
        }
        TBLog.Info("ClearSaveRootCache: cleared save-root cache.");
    }

    // Diagnostic: dump fields/properties of the provided saveRoot (safe, limited sampling)
    public static void DumpSaveRootMembers(object saveRoot)
    {
        try
        {
            if (saveRoot == null)
            {
                TBLog.Info("DumpSaveRootMembers: saveRoot is null.");
                return;
            }

            var t = saveRoot.GetType();
            TBLog.Info($"DumpSaveRootMembers: saveRoot type = {t.FullName}");

            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            int maxItemsSample = 8;

            // Fields
            foreach (var f in t.GetFields(flags))
            {
                object val = null;
                try { val = f.GetValue(saveRoot); } catch (Exception ex) { TBLog.Info($"Field {f.Name} ({f.FieldType.Name}): <error reading: {ex.Message}>"); continue; }

                if (val == null)
                {
                    TBLog.Info($"Field {f.Name} ({f.FieldType.FullName}): null");
                    continue;
                }

                // If IDictionary -> log count and sample keys
                if (val is System.Collections.IDictionary dict)
                {
                    var keys = new List<string>();
                    int i = 0;
                    foreach (var k in dict.Keys)
                    {
                        if (i++ >= maxItemsSample) break;
                        keys.Add(k?.ToString() ?? "<null>");
                    }
                    TBLog.Info($"Field {f.Name} ({f.FieldType.FullName}): IDictionary count={dict.Count}, sample keys=[{string.Join(", ", keys)}]");
                    continue;
                }

                // If IEnumerable (but not string) -> log count (if possible) and sample items
                if (val is System.Collections.IEnumerable ie && !(val is string))
                {
                    var items = new List<string>();
                    int cnt = 0;
                    foreach (var it in ie)
                    {
                        if (cnt++ >= maxItemsSample) break;
                        items.Add(it?.ToString() ?? "<null>");
                    }
                    // Try to get Count property if exists
                    int? maybeCount = null;
                    try
                    {
                        var countProp = val.GetType().GetProperty("Count");
                        if (countProp != null) maybeCount = (int)countProp.GetValue(val, null);
                    }
                    catch { maybeCount = null; }
                    TBLog.Info($"Field {f.Name} ({f.FieldType.FullName}): IEnumerable count={(maybeCount.HasValue ? maybeCount.Value.ToString() : "unknown")}, sample=[{string.Join(", ", items)}]");
                    continue;
                }

                // Scalar / object -> log type and ToString sample
                TBLog.Info($"Field {f.Name} ({f.FieldType.FullName}): value={val.ToString()}");
            }

            // Properties
            foreach (var p in t.GetProperties(flags))
            {
                // skip indexers
                if (p.GetIndexParameters().Length > 0) continue;
                object val = null;
                try { val = p.GetValue(saveRoot, null); } catch (Exception ex) { TBLog.Info($"Prop {p.Name} ({p.PropertyType.Name}): <error reading: {ex.Message}>"); continue; }

                if (val == null)
                {
                    TBLog.Info($"Prop {p.Name} ({p.PropertyType.FullName}): null");
                    continue;
                }

                if (val is System.Collections.IDictionary dict)
                {
                    var keys = new List<string>();
                    int i = 0;
                    foreach (var k in dict.Keys)
                    {
                        if (i++ >= maxItemsSample) break;
                        keys.Add(k?.ToString() ?? "<null>");
                    }
                    TBLog.Info($"Prop {p.Name} ({p.PropertyType.FullName}): IDictionary count={dict.Count}, sample keys=[{string.Join(", ", keys)}]");
                    continue;
                }

                if (val is System.Collections.IEnumerable ie && !(val is string))
                {
                    var items = new List<string>();
                    int cnt = 0;
                    foreach (var it in ie)
                    {
                        if (cnt++ >= maxItemsSample) break;
                        items.Add(it?.ToString() ?? "<null>");
                    }
                    int? maybeCount = null;
                    try
                    {
                        var countProp = val.GetType().GetProperty("Count");
                        if (countProp != null) maybeCount = (int)countProp.GetValue(val, null);
                    }
                    catch { maybeCount = null; }
                    TBLog.Info($"Prop {p.Name} ({p.PropertyType.FullName}): IEnumerable count={(maybeCount.HasValue ? maybeCount.Value.ToString() : "unknown")}, sample=[{string.Join(", ", items)}]");
                    continue;
                }

                TBLog.Info($"Prop {p.Name} ({p.PropertyType.FullName}): value={val.ToString()}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpSaveRootMembers failed: " + ex.Message);
        }
    }

    // Tries to find visited collection on the given object; returns the flattened list and the member name used.
    // Also logs which member was selected for diagnostics.
    private static bool TryFindVisitedCollectionWithDiagnostics(object root, out List<object> outList, out string usedMemberName)
    {
        outList = null;
        usedMemberName = null;
        if (root == null) return false;

        try
        {
            var list = FindVisitedCollectionInObject(root);
            if (list != null && list.Count > 0)
            {
                outList = list;
                usedMemberName = "direct"; // best-effort; specific member logged inside FindVisitedCollectionInObject when found
                return true;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryFindVisitedCollectionWithDiagnostics failed: " + ex.Message);
        }

        return false;
    }

    // This function is the same basic scan used previously but it will log which field/property provided the collection.
    private static List<object> FindVisitedCollectionInObject(object root)
    {
        if (root == null) return null;

        try
        {
            var rootType = root.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;

            // Candidate member names we check first (helps us find the correct visited member quickly)
            var visitedCandidates = new[]
            {
            "visitedLocations", "discoveredLocations", "discovered", "visitedCities", "discoveredCities",
            "knownLocations", "knownDestinations", "visited", "discoveredIds", "visitedIds", "visitedLocationsByName"
        };

            foreach (var name in visitedCandidates)
            {
                try
                {
                    var field = rootType.GetField(name, flags);
                    if (field != null)
                    {
                        var val = field.GetValue(root);
                        var items = ConvertToObjectList(val);
                        if (items != null && items.Count > 0)
                        {
                            TBLog.Info($"FindVisitedCollectionInObject: found visited collection on field '{rootType.FullName}.{name}' with {items.Count} items.");
                            return items;
                        }
                    }

                    var prop = rootType.GetProperty(name, flags);
                    if (prop != null)
                    {
                        object val = null;
                        try { val = prop.GetValue(root, null); } catch { val = null; }
                        var items = ConvertToObjectList(val);
                        if (items != null && items.Count > 0)
                        {
                            TBLog.Info($"FindVisitedCollectionInObject: found visited collection on property '{rootType.FullName}.{name}' with {items.Count} items.");
                            return items;
                        }
                    }
                }
                catch { /* ignore per-candidate failures */ }
            }

            // Check nested containers (common patterns)
            var nestedCandidates = new[] { "Save", "SaveData", "Profile", "Data", "CurrentSave", "PlayerSave" };
            foreach (var nestedName in nestedCandidates)
            {
                try
                {
                    var field = rootType.GetField(nestedName, flags);
                    object nested = null;
                    if (field != null) nested = field.GetValue(root);
                    else
                    {
                        var prop = rootType.GetProperty(nestedName, flags);
                        if (prop != null) nested = prop.GetValue(root, null);
                    }

                    if (nested != null)
                    {
                        var nestedItems = FindVisitedCollectionInObject(nested); // recursion
                        if (nestedItems != null && nestedItems.Count > 0) return nestedItems;
                    }
                }
                catch { /* ignore nested errors */ }
            }

            // Last resort: scan all fields/properties and return the first plausible collection (but log candidate names)
            foreach (var field in rootType.GetFields(flags))
            {
                try
                {
                    var val = field.GetValue(root);
                    var items = ConvertToObjectList(val);
                    if (items != null && items.Count > 0)
                    {
                        TBLog.Info($"FindVisitedCollectionInObject: heuristically selected field '{rootType.FullName}.{field.Name}' as visited collection with {items.Count} items.");
                        return items;
                    }
                }
                catch { }
            }

            foreach (var prop in rootType.GetProperties(flags))
            {
                try
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    object val = null;
                    try { val = prop.GetValue(root, null); } catch { continue; }
                    var items = ConvertToObjectList(val);
                    if (items != null && items.Count > 0)
                    {
                        TBLog.Info($"FindVisitedCollectionInObject: heuristically selected property '{rootType.FullName}.{prop.Name}' as visited collection with {items.Count} items.");
                        return items;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("FindVisitedCollectionInObject failed: " + ex.Message);
        }

        return null;
    }

    // Convert various collection types to a List<object>. For IDictionary return keys (commonly used for visited sets).
    private static List<object> ConvertToObjectList(object val)
    {
        if (val == null) return null;

        try
        {
            if (val is System.Collections.IDictionary dict)
            {
                var keys = new List<object>();
                foreach (var key in dict.Keys) keys.Add(key);
                if (keys.Count > 0) return keys;
                return null;
            }

            if (val is System.Collections.IEnumerable ie && !(val is string))
            {
                var list = new List<object>();
                foreach (var item in ie) list.Add(item);
                if (list.Count > 0) return list;
            }
        }
        catch { /* ignore conversion errors */ }

        return null;
    }

    /// <summary>
    /// Returns true if any visited key appears to correspond to the city identifier.
    /// Uses normalization and substring heuristics to accommodate different save-key formats.
    /// </summary>
    private static bool VisitedSetContainsCity(HashSet<string> visitedSet, string candidate)
    {
        if (visitedSet == null || visitedSet.Count == 0 || string.IsNullOrEmpty(candidate)) return false;
        var candNorm = NormalizeVisitedKey(candidate);
        if (string.IsNullOrEmpty(candNorm)) return false;

        // Direct exact match against raw visited keys (case-insensitive)
        foreach (var raw in visitedSet)
        {
            if (string.Equals(raw, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Normalized matching
        foreach (var raw in visitedSet)
        {
            var rawNorm = NormalizeVisitedKey(raw);
            if (string.IsNullOrEmpty(rawNorm)) continue;

            // exact normalized equality
            if (rawNorm == candNorm) return true;

            // substring match (either direction)
            if (rawNorm.Contains(candNorm) || candNorm.Contains(rawNorm)) return true;
        }

        return false;
    }

    /// <summary>
    /// Debug helper to log visited keys and candidate matching results (call temporarily).
    /// </summary>
    public static void DumpVisitedKeysAndCandidates(IEnumerable<string> candidates = null)
    {
        try
        {
            // access private visited set field if you kept it, else rebuild via FindSaveRootInstance as before
            var field = typeof(TravelButton).GetField("s_visitedKeysSet", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var setObj = field?.GetValue(null) as System.Collections.IEnumerable;
            if (setObj == null)
            {
                TBLog.Info("DumpVisitedKeysAndCandidates: visited set is null or empty.");
                return;
            }
            var list = new List<string>();
            foreach (var it in setObj) list.Add(it?.ToString() ?? "(null)");

            TBLog.Info($"DumpVisitedKeysAndCandidates: total visited keys = {list.Count}");
            int i = 0;
            foreach (var k in list)
            {
                i++;
                TBLog.Info($"VisitedKey#{i}: '{k}' -> normalized='{NormalizeVisitedKey(k)}'");
            }

            if (candidates != null)
            {
                foreach (var cand in candidates)
                {
                    var cn = cand ?? "(null)";
                    TBLog.Info($"Candidate '{cn}' normalized='{NormalizeVisitedKey(cn)}' => match={VisitedSetContainsCity(new HashSet<string>(list, StringComparer.OrdinalIgnoreCase), cn)}");
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpVisitedKeysAndCandidates failed: " + ex.Message);
        }
    }

    // Generic extractor: try to pull persistent "visited" keys from save root / character saves via reflection.
    // Returns a HashSet<string> of raw visited-like keys (may be empty if none found).
    // Improved reflection-based extractor: recursively scan likely save-root objects and collect string keys.
    // This is best-effort: it filters out obvious file-paths and short/generic tokens.
    // Returns a set of raw strings found in save-like containers.
    private static HashSet<string> BuildVisitedKeysFromSave()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Try to find a SaveManager / SaveRoot instance first
            Type saveManagerType = assemblies.SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                                             .FirstOrDefault(t => t.Name.IndexOf("SaveManager", StringComparison.OrdinalIgnoreCase) >= 0
                                                               || t.Name.IndexOf("SaveRoot", StringComparison.OrdinalIgnoreCase) >= 0
                                                               || t.Name.IndexOf("SaveSystem", StringComparison.OrdinalIgnoreCase) >= 0);

            var roots = new List<object>();

            if (saveManagerType != null)
            {
                try
                {
                    // try static Instance
                    var instProp = saveManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (instProp != null)
                    {
                        try { var v = instProp.GetValue(null); if (v != null) roots.Add(v); } catch { }
                    }
                    // try scene instance
                    if (typeof(UnityEngine.Object).IsAssignableFrom(saveManagerType))
                    {
                        try { var sceneInst = UnityEngine.Object.FindObjectOfType(saveManagerType); if (sceneInst != null) roots.Add(sceneInst); } catch { }
                    }
                }
                catch { }
            }

            // Also search for types with "CharacterSave" or "WorldSave" in their name
            var candidateTypes = assemblies.SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                                           .Where(t => t.Name.IndexOf("CharacterSave", StringComparison.OrdinalIgnoreCase) >= 0
                                                    || t.Name.IndexOf("WorldSave", StringComparison.OrdinalIgnoreCase) >= 0
                                                    || t.Name.IndexOf("SaveData", StringComparison.OrdinalIgnoreCase) >= 0)
                                           .ToArray();
            foreach (var t in candidateTypes)
            {
                try
                {
                    // static Instance
                    var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (p != null) { try { var v = p.GetValue(null); if (v != null) roots.Add(v); } catch { } }
                    // scene instance
                    if (typeof(UnityEngine.Object).IsAssignableFrom(t))
                    {
                        try { var o = UnityEngine.Object.FindObjectOfType(t); if (o != null) roots.Add(o); } catch { }
                    }
                }
                catch { }
            }

            // If still empty, include any object named CharacterSaveInstanceHolder (we saw that token in logs)
            try
            {
                var csType = assemblies.SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                                       .FirstOrDefault(tt => string.Equals(tt.Name, "CharacterSaveInstanceHolder", StringComparison.OrdinalIgnoreCase));
                if (csType != null)
                {
                    try { var obj = UnityEngine.Object.FindObjectOfType(csType); if (obj != null) roots.Add(obj); } catch { }
                }
            }
            catch { }

            // If nothing found, bail with empty set
            if (roots.Count == 0)
            {
                return result;
            }

            // Recursively scan objects up to a limited depth and collect string-like entries
            void ScanObject(object obj, int depth)
            {
                if (obj == null || depth <= 0) return;
                try
                {
                    // Strings
                    if (obj is string s)
                    {
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
                        return;
                    }

                    // IDictionary: add keys and values
                    if (obj is System.Collections.IDictionary dict)
                    {
                        foreach (var k in dict.Keys)
                        {
                            if (k != null) ScanObject(k, depth - 1);
                        }
                        foreach (var v in dict.Values)
                        {
                            if (v != null) ScanObject(v, depth - 1);
                        }
                        return;
                    }

                    // IEnumerable: add items (but ignore UnityEngine.Object enumerations like GameObjects)
                    if (obj is System.Collections.IEnumerable ie && !(obj is UnityEngine.Object))
                    {
                        foreach (var it in ie)
                        {
                            if (it == null) continue;
                            ScanObject(it, depth - 1);
                        }
                        return;
                    }

                    // Reflection: inspect fields & properties
                    var t = obj.GetType();

                    // Prefer likely-named fields/properties first
                    string[] likelyNames = new[] { "visited", "visitedLocations", "visitedCities", "discovered", "discoveredScenes", "visitedList", "visitedIds", "visitedNames" };
                    foreach (var name in likelyNames)
                    {
                        try
                        {
                            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (f != null) { var fv = f.GetValue(obj); if (fv != null) ScanObject(fv, depth - 1); }
                        }
                        catch { }
                        try
                        {
                            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (p != null && p.GetIndexParameters().Length == 0) { var pv = p.GetValue(obj, null); if (pv != null) ScanObject(pv, depth - 1); }
                        }
                        catch { }
                    }

                    // Generic scan of fields/properties
                    foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            var v = fi.GetValue(obj);
                            if (v != null) ScanObject(v, depth - 1);
                        }
                        catch { }
                    }
                    foreach (var pi in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (pi.GetIndexParameters().Length > 0) continue;
                        try
                        {
                            if (!pi.CanRead) continue;
                            var v = pi.GetValue(obj, null);
                            if (v != null) ScanObject(v, depth - 1);
                        }
                        catch { }
                    }
                }
                catch { /* swallow */ }
            }

            // Run scan on each root with limited depth to avoid explosion
            foreach (var r in roots.Distinct().Where(x => x != null))
                ScanObject(r, 4);

            // Heuristic filter: remove file paths, tiny tokens, and known generic tokens
            var cleaned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var genericBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".char","savegames","charactersaveinstanceholder","saveroot","savemetadata"
        };

            foreach (var raw in result)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string s = raw.Trim();

                // skip obvious file paths (windows path containing ':' or backslash)
                if (s.IndexOf(':') >= 0 || s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0)
                {
                    // allow if it contains a city name later (we'll rely on secondary matching)
                    continue;
                }

                var norm = new string(s.Where(c => char.IsLetterOrDigit(c)).ToArray());
                if (string.IsNullOrEmpty(norm)) continue;
                if (norm.Length < 3) continue;
                if (genericBlacklist.Contains(norm.ToLowerInvariant())) continue;

                cleaned.Add(s);
            }

            return cleaned;
        }
        catch (Exception ex)
        {
            TBLog.Warn("BuildVisitedKeysFromSave exception: " + ex.Message);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // --- ADD PrepareVisitedLookup() TO TravelButton CLASS ---

    /// <summary>
    /// Prepare the persistent visited-key lookup once per save/dialog open.
    /// Calls BuildVisitedKeysFromSave() (the generic extractor) and caches results.
    /// If forceRebuild==true, always rebuild the cache.
    /// </summary>
    // Prepare the persistent visited-key lookup once per save/dialog open.
    // Calls BuildVisitedKeysFromSave() and caches results.
    // Replace your existing PrepareVisitedLookup (or integrate this logic) —
    // this version prefers plugin-persisted visited flags if present.
    public static void PrepareVisitedLookup()
    {
        if (_visitedLookupPrepared) return;

        try
        {
            TBLog.Info("PrepareVisitedLookup: building visited lookup...");

            var pluginPersisted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (TravelButton.Cities != null)
            {
                foreach (var cc in TravelButton.Cities)
                {
                    try
                    {
                        if (cc != null && cc.visited)
                        {
                            var normalized = NormalizeVisitedKey(cc.name);
                            if (!string.IsNullOrEmpty(normalized)) pluginPersisted.Add(normalized);
                        }
                    }
                    catch { }
                }
            }

            var saveExtracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                FillSaveExtractedVisitedKeys(saveExtracted);
            }
            catch (Exception ex)
            {
                TBLog.Warn("PrepareVisitedLookup: failed to extract visited keys from saves: " + ex.Message);
            }

            var normalizedSave = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in saveExtracted)
                if (!string.IsNullOrEmpty(s)) normalizedSave.Add(NormalizeVisitedKey(s));

            HashSet<string> finalSet;
            if (pluginPersisted.Count > 0)
            {
                finalSet = new HashSet<string>(pluginPersisted, StringComparer.OrdinalIgnoreCase);
                TBLog.Info($"PrepareVisitedLookup: using plugin-persisted visited flags ({pluginPersisted.Count} entries) and ignoring save history.");
            }
            else
            {
                finalSet = new HashSet<string>(normalizedSave, StringComparer.OrdinalIgnoreCase);
                TBLog.Info($"PrepareVisitedLookup: no plugin-persisted visited flags found; using save-extracted visited keys ({normalizedSave.Count} entries).");
            }

            // Always assign a non-null set
            _visitedLookup = finalSet ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _visitedLookupPrepared = true;
            TBLog.Info($"PrepareVisitedLookup: final visited lookup count = {_visitedLookup.Count}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PrepareVisitedLookup: unexpected error: " + ex);
            _visitedLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _visitedLookupPrepared = true;
        }
    }

    // Normalizes a visited key for stable comparison across JSON, scene names and save-extracted keys.
    // Converts to lower, trims, and keeps only alphanumeric characters.
    private static string NormalizeVisitedKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var lowered = key.Trim().ToLowerInvariant();

        var sb = new System.Text.StringBuilder(lowered.Length);
        foreach (char c in lowered)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Attempts to fill `outSet` with visited keys extracted from the game's save data.
    /// This function uses reflection to try a few common existing method/field names in this plugin so you
    /// don't have to wire it manually right away. If your project already has a concrete extractor,
    /// replace the body with a direct call to it.
    /// </summary>
    private static void FillSaveExtractedVisitedKeys(HashSet<string> outSet)
    {
        if (outSet == null) return;

        try
        {
            // 1) Try to find a method that returns IEnumerable<string> and takes no parameters.
            var candidateMethodNames = new[]
            {
            "ExtractVisitedKeysFromSave",
            "GetVisitedKeysFromSaves",
            "BuildSaveVisitedKeys",
            "CollectVisitedKeysFromSave",
            "GetVisitedKeys",
            "ExtractVisitedKeys",
            "BuildVisitedKeysFromSaves"
        };

            var tbType = typeof(TravelButton);
            foreach (var name in candidateMethodNames)
            {
                try
                {
                    var mi = tbType.GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        // If returns IEnumerable<string> and requires no args
                        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(mi.ReturnType) && mi.GetParameters().Length == 0)
                        {
                            var ret = mi.Invoke(null, null) ?? mi.Invoke(Activator.CreateInstance(tbType), null);
                            if (ret is System.Collections.IEnumerable ie)
                            {
                                foreach (var item in ie)
                                {
                                    if (item != null)
                                        outSet.Add(NormalizeVisitedKey(item.ToString()));
                                }
                                if (outSet.Count > 0) return;
                            }
                        }

                        // If it takes a collection parameter to fill (e.g., void Fill(HashSet<string> outSet))
                        var pars = mi.GetParameters();
                        if (pars.Length == 1 && typeof(System.Collections.ICollection).IsAssignableFrom(pars[0].ParameterType))
                        {
                            // try invoke with a HashSet<string>
                            object instance = null;
                            if (!mi.IsStatic)
                            {
                                try { instance = Activator.CreateInstance(tbType); } catch { instance = null; }
                            }
                            var temp = new System.Collections.Generic.List<string>();
                            mi.Invoke(instance, new object[] { temp });
                            foreach (var s in temp) if (s != null) outSet.Add(NormalizeVisitedKey(s.ToString()));
                            if (outSet.Count > 0) return;
                        }
                    }
                }
                catch { /* ignore and try next candidate */ }
            }

            // 2) Try to discover a field/prop that already contains visited keys (common names)
            var candidateFieldNames = new[]
            {
            "_visitedKeys", "visitedKeys", "SaveVisitedKeys", "saveVisitedKeys", "extractedVisitedKeys", "_saveVisitedKeys"
        };

            foreach (var fname in candidateFieldNames)
            {
                try
                {
                    var fi = tbType.GetField(fname, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (fi != null)
                    {
                        var val = fi.GetValue(null) ?? fi.GetValue(Activator.CreateInstance(tbType));
                        if (val is System.Collections.IEnumerable ie)
                        {
                            foreach (var item in ie)
                                if (item != null)
                                    outSet.Add(NormalizeVisitedKey(item.ToString()));
                            if (outSet.Count > 0) return;
                        }
                    }

                    var pi = tbType.GetProperty(fname, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (pi != null)
                    {
                        var val = pi.GetValue(null, null) ?? pi.GetValue(Activator.CreateInstance(tbType), null);
                        if (val is System.Collections.IEnumerable ie2)
                        {
                            foreach (var item in ie2)
                                if (item != null)
                                    outSet.Add(NormalizeVisitedKey(item.ToString()));
                            if (outSet.Count > 0) return;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            // 3) Nothing discovered — leave outSet empty. Caller will fall back as appropriate.
            TBLog.Info($"PrepareVisitedLookup: no automatic save-extractor found via reflection; saveExtracted will be empty. If you have a custom extractor, replace FillSaveExtractedVisitedKeys with a direct call to it.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("FillSaveExtractedVisitedKeys: reflection attempt failed: " + ex.Message);
        }
    }

    // Call this whenever visited flags (TravelButton.Cities) changed and you want UI to reflect them.
    public static void NotifyVisitedFlagsChanged()
    {
        try
        {
            TBLog.Info("NotifyVisitedFlagsChanged: started");

            // Reset and prepare the lookup so HasPlayerVisited uses fresh data
            ClearVisitedCache();
            PrepareVisitedLookup();

            // Call the static RebuildTravelDialog if available
            try
            {
                TBLog.Info("NotifyVisitedFlagsChanged: calling TravelButtonUI.RebuildTravelDialog()");
                try { TravelButtonUI.RebuildTravelDialog(); }
                catch (Exception e) { TBLog.Warn("NotifyVisitedFlagsChanged: RebuildTravelDialog failed: " + e); }
            }
            catch (Exception ex)
            {
                TBLog.Warn("NotifyVisitedFlagsChanged: error while calling RebuildTravelDialog: " + ex);
            }

            // Attempt instance-based in-dialog refresh: find the UI instance and start its coroutine
            try
            {
                var ui = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
                if (ui != null)
                {
                    TBLog.Info("NotifyVisitedFlagsChanged: found TravelButtonUI instance, attempting to start RefreshCityButtonsWhileOpen coroutine");

                    // Try to obtain a dialogRoot GameObject from the ui instance (common names)
                    GameObject dialogRoot = null;
                    try
                    {
                        var uiType = ui.GetType();
                        // try property then field, common names
                        var p = uiType.GetProperty("dialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)
                                ?? uiType.GetProperty("DialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                        if (p != null)
                        {
                            var val = p.GetValue(ui, null);
                            dialogRoot = val as GameObject;
                        }
                        else
                        {
                            var f = uiType.GetField("dialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)
                                    ?? uiType.GetField("DialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                            if (f != null)
                            {
                                var val = f.GetValue(ui);
                                dialogRoot = val as GameObject;
                            }
                        }
                    }
                    catch (Exception exGet)
                    {
                        TBLog.Warn("NotifyVisitedFlagsChanged: failed to obtain dialogRoot via reflection: " + exGet);
                        dialogRoot = null;
                    }

                    // Start the coroutine on the ui instance; pass null if we couldn't find dialogRoot
                    try
                    {
                        ui.StartCoroutine(ui.RefreshCityButtonsWhileOpen(dialogRoot));
                        TBLog.Info("NotifyVisitedFlagsChanged: started RefreshCityButtonsWhileOpen coroutine on TravelButtonUI instance");
                    }
                    catch (Exception exStart)
                    {
                        TBLog.Warn("NotifyVisitedFlagsChanged: failed to start RefreshCityButtonsWhileOpen coroutine: " + exStart);
                    }
                }
                else
                {
                    TBLog.Info("NotifyVisitedFlagsChanged: TravelButtonUI instance not found (dialog not created yet)");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("NotifyVisitedFlagsChanged: unexpected error while trying instance refresh: " + ex);
            }

            TBLog.Info("NotifyVisitedFlagsChanged: completed");
        }
        catch (Exception ex)
        {
            TBLog.Warn("NotifyVisitedFlagsChanged: unexpected error: " + ex);
        }
    }

    /// <summary>
    /// Best-effort: get player position from scene (returns null if not found).
    /// </summary>
    public static Vector3? GetPlayerPositionInScene()
    {
        try
        {
            // Try common tags first
            var go = GameObject.FindWithTag("Player");
            if (go != null) return go.transform.position;

            // Fallback: try find objects named like PlayerChar*
            foreach (var g in GameObject.FindObjectsOfType<GameObject>())
            {
                if (g == null || string.IsNullOrEmpty(g.name)) continue;
                if (g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) ||
                    g.name.Equals("Player", StringComparison.OrdinalIgnoreCase))
                {
                    return g.transform.position;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("GetPlayerPositionInScene failed: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Best-effort detection of an in-scene target GameObject name that could represent the city anchor.
    /// Returns null when nothing sensible found.
    /// </summary>
    public static string DetectTargetGameObjectName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return null;
        try
        {
            // Find objects whose name contains the sceneName (case-insensitive) and are in a valid loaded scene.
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (var g in all)
            {
                if (g == null || string.IsNullOrEmpty(g.name)) continue;
                if (!g.scene.IsValid() || !g.scene.isLoaded) continue;
                if (g.name.IndexOf(sceneName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return g.name;
                }
            }

            // fallback: find any GameObject whose name contains 'location' or 'town' and is loaded
            foreach (var g in all)
            {
                if (g == null || string.IsNullOrEmpty(g.name)) continue;
                if (!g.scene.IsValid() || !g.scene.isLoaded) continue;
                var n = g.name.ToLowerInvariant();
                if (n.Contains("location") || n.Contains("town") || n.Contains("village") || n.Contains("spawn"))
                    return g.name;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DetectTargetGameObjectName failed: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Store a newly discovered/visited scene into the canonical TravelButton_Cities.json.
    /// - Backs up the file before modification.
    /// - Appends a new city entry when no existing entry with matching sceneName is found.
    /// - Validates JSON after writing; on failure restores from backup.
    /// </summary>
    /// <param name="newSceneName">Name of the scene (required)</param>
    /// <param name="playerPos">Player coordinates (nullable)</param>
    /// <param name="detectedTarget">Detected targetGameObjectName (nullable)</param>
    /// <param name="sceneDesc">Optional description</param>
    // Replace the existing StoreVisitedSceneToJson implementation with this version.
    // This creates a single reusable backup file (TravelButton_Cities.json.bak) and reuses it
    // for subsequent calls instead of creating timestamped backups every time.
    // Insert/replace this method inside the existing TravelButton partial class (src/TravelButton.cs).
    // This version:
    // - skips blacklisted transient scenes
    // - creates a single reusable backup file (TravelButton_Cities.json.bak)
    // - defers writing coords by starting the plugin coroutine when coords are missing/invalid
    // - validates the JSON structure and restores from the single backup on failure
    // - uses atomic replace where possible (File.Replace)
    // Replace the existing StoreVisitedSceneToJson with this implementation.
    // Key fixes:
    // - single reusable backup TravelButton_Cities.json.bak
    // - if both json and .bak parse fail, preserve the original json into .bak and continue with a fresh root so new scene can be saved
    // - defers coords detection to coroutine when coords are invalid
    // Treat coords as invalid when any axis looks like the -5000 sentinel, or when zero or null.

    public static IEnumerator WaitForSceneReadiness(
        string sceneName,
        string requiredComponentTypeName,   // e.g. "TownController" or null
        string requiredObjectNameContains,  // e.g. "BGM_TownCierzo" or null (case-insensitive)
        Action<bool> onReady,               // invoked with true if ready, false on timeout
        float maxWaitSeconds = 8f,
        float pollInterval = 0.5f)
    {
        float elapsed = 0f;
        yield return null; // allow one frame for scene to start initializing

        while (elapsed < maxWaitSeconds)
        {
            try
            {
                bool compOk = false;
                bool nameOk = false;

                // 1) check component type (preferred)
                if (!string.IsNullOrEmpty(requiredComponentTypeName))
                {
                    var types = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var asm in types)
                    {
                        try
                        {
                            var t = asm.GetType(requiredComponentTypeName, false, true);
                            if (t != null && typeof(UnityEngine.Component).IsAssignableFrom(t))
                            {
                                var found = UnityEngine.Object.FindObjectOfType(t);
                                if (found != null)
                                {
                                    var go = ((Component)found).gameObject;
                                    if (go.activeInHierarchy)
                                    {
                                        compOk = true;
                                        break;
                                    }
                                }
                            }
                        }
                        catch { /* ignore assembly errors */ }
                    }
                }

                // 2) check by name contains (fallback)
                if (!string.IsNullOrEmpty(requiredObjectNameContains))
                {
                    var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
                    foreach (var go in all)
                    {
                        if (!go.activeInHierarchy) continue;
                        if (go.name.IndexOf(requiredObjectNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // optional: ensure object's world position is not sentinel
                            var p = go.transform.position;
                            bool notSentinel = !(Math.Abs(p.x + 5000f) < 200f || Math.Abs(p.y + 5000f) < 200f || Math.Abs(p.z + 5000f) < 200f);
                            if (notSentinel)
                            {
                                nameOk = true;
                                break;
                            }
                        }
                    }
                }

                // Decide "ready" policy: prefer component check when provided, otherwise use name check
                bool ready = false;
                if (!string.IsNullOrEmpty(requiredComponentTypeName))
                    ready = compOk;
                else if (!string.IsNullOrEmpty(requiredObjectNameContains))
                    ready = nameOk;
                else
                {
                    // If no requirement given, consider scene "ready" when Camera.main and a player object exist
                    ready = (Camera.main != null && GetPlayerObject() != null);
                }

                if (ready)
                {
                    onReady?.Invoke(true);
                    yield break;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"WaitForSceneReadiness: exception while checking readiness: {ex.Message}");
            }

            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;
        }

        // timed out
        onReady?.Invoke(false);
    }

    // Helper to find the player GameObject used in other parts of plugin.
    private static GameObject GetPlayerObject()
    {
        // try tag
        var byTag = GameObject.FindWithTag("Player");
        if (byTag != null) return byTag;

        // fallback: heuristics by name
        var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
        foreach (var go in all)
        {
            if (go.name.StartsWith("PlayerChar") || go.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                return go;
        }
        return null;
    }

    // Scene readiness map inside TravelButton (add near top of file)
    public static readonly Dictionary<string, string> SceneReadinessComponentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // If you know a controller type add that (preferred).
        // For Chersonese we don't have a stable controller type in the dump, so we will use "Environment" object fallback.
        ["ChersoneseNewTerrain"] = null,
        ["CierzoNewTerrain"] = null,
        // add other scenes as you identify authoritative component names
    };

    // Build a canonical JObject representation for a single city.
    // Ensures "variants" (array) and "lastKnownVariant" (string) keys are always present.
    public static JObject BuildJObjectForCity(City city)
    {
        var jo = new JObject
        {
            ["name"] = city.name ?? "",
            ["sceneName"] = city.sceneName ?? "",
            ["coords"] = (city.coords != null && city.coords.Length >= 3)
                ? new JArray(city.coords.Select(f => (JToken)JToken.FromObject(f)))
                : new JArray(), // keep key present
            ["price"] = city.price.HasValue ? (JToken)city.price.Value : JValue.CreateNull(),
            ["targetGameObjectName"] = !string.IsNullOrEmpty(city.targetGameObjectName) ? city.targetGameObjectName : "",
            ["desc"] = "", // reserved
            ["visited"] = city.visited
        };

        // variants: always present (default empty array)
        if (city.variants != null && city.variants.Length > 0)
            jo["variants"] = new JArray(city.variants);
        else
            jo["variants"] = new JArray();

        // lastKnownVariant: always present (default empty string)
        jo["lastKnownVariant"] = city.lastKnownVariant ?? "";

        return jo;
    }

    // Append or update a single city in the canonical TravelButton_Cities.json and write atomically.
    public static void AppendOrUpdateCityInJsonAndSave(City city)
    {
        try
        {
            var path = TravelButtonPlugin.GetCitiesJsonPath();
            JObject root;

            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                try
                {
                    root = JObject.Parse(text);
                }
                catch (JsonException)
                {
                    TBLog.Warn($"AppendOrUpdateCityInJsonAndSave: existing JSON parse failed at '{path}', recreating root.");
                    root = new JObject();
                }
                if (root["cities"] == null || !(root["cities"] is JArray)) root["cities"] = new JArray();
            }
            else
            {
                root = new JObject { ["cities"] = new JArray() };
            }

            var citiesArray = (JArray)root["cities"];
            var existing = citiesArray.OfType<JObject>()
                .FirstOrDefault(j => string.Equals(j.Value<string>("name"), city.name, StringComparison.OrdinalIgnoreCase));

            var newJo = BuildJObjectForCity(city);

            if (existing != null)
            {
                existing.Replace(newJo);
            }
            else
            {
                citiesArray.Add(newJo);
            }

            // Atomic write: write to temp then replace
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToString(Formatting.Indented));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception exReplace)
            {
                TBLog.Warn("AppendOrUpdateCityInJsonAndSave: File.Replace fallback: " + exReplace.Message);
                File.Copy(temp, path, true);
                File.Delete(temp);
            }

            TBLog.Info($"AppendOrUpdateCityInJsonAndSave: persisted city '{city.name}' to {path}.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("AppendOrUpdateCityInJsonAndSave: " + ex.Message);
        }
    }

    // Called when a variant is detected for a city (integration point).
    // Updates in-memory city.lastKnownVariant and persists only that city to JSON.
    public static void OnVariantDetectedForCity(string cityName, string detectedVariantName)
    {
        try
        {
            if (string.IsNullOrEmpty(cityName)) return;
            var city = Cities?.FirstOrDefault(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
            if (city == null)
            {
                TBLog.Warn($"OnVariantDetectedForCity: city '{cityName}' not found in TravelButton.Cities");
                return;
            }

            var newVariant = detectedVariantName ?? "";
            if (string.Equals(city.lastKnownVariant ?? "", newVariant, StringComparison.Ordinal))
            {
                TBLog.Info($"OnVariantDetectedForCity: '{cityName}' lastKnownVariant already '{newVariant}', no change.");
                return;
            }

            city.lastKnownVariant = newVariant;
            TBLog.Info($"OnVariantDetectedForCity: '{cityName}' lastKnownVariant set to '{city.lastKnownVariant}' in memory; persisting JSON.");

            // Ensure variants array contains the detected variant if not present
            if (!string.IsNullOrEmpty(newVariant))
            {
                var variants = city.variants ?? new string[0];
                if (!variants.Contains(newVariant))
                {
                    var list = new List<string>(variants) { newVariant };
                    city.variants = list.ToArray();
                }
            }

            AppendOrUpdateCityInJsonAndSave(city);
        }
        catch (Exception ex)
        {
            TBLog.Warn("OnVariantDetectedForCity: " + ex.Message);
        }
    }

    private static void test2()
    {
        try
        {
            Debug.Log("[VariantRefProbe] start");

            var scene = SceneManager.GetActiveScene();
            Debug.Log("[VariantRefProbe] active scene='" + scene.name + "' rootCount=" + scene.rootCount);

            // helper: get full path of a transform
            string GetFullPath(Transform tr)
            {
                if (tr == null) return "(null)";
                var parts = new List<string>();
                var cur = tr;
                while (cur != null)
                {
                    parts.Add(cur.name);
                    cur = cur.parent;
                }
                parts.Reverse();
                return string.Join("/", parts);
            }

            // find Interactions root
            GameObject interactionsRoot = null;
            foreach (var r in scene.GetRootGameObjects())
            {
                if (r == null) continue;
                if (string.Equals(r.name, "Interactions", StringComparison.OrdinalIgnoreCase) || r.name.IndexOf("Interactions", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    interactionsRoot = r;
                    break;
                }
            }
            if (interactionsRoot == null)
            {
                Debug.Log("[VariantRefProbe] Interactions root not found");
                return;
            }
            Debug.Log("[VariantRefProbe] Interactions root: " + interactionsRoot.name);

            // find NormalCierzo and DestroyedCierzo under Interactions (any depth)
            GameObject normal = null;
            GameObject destroyed = null;
            var allTransforms = interactionsRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                var n = t.name ?? "";
                if (normal == null && n.IndexOf("NormalCierzo", StringComparison.OrdinalIgnoreCase) >= 0) normal = t.gameObject;
                if (destroyed == null && n.IndexOf("DestroyedCierzo", StringComparison.OrdinalIgnoreCase) >= 0) destroyed = t.gameObject;
            }

            Debug.Log("[VariantRefProbe] NormalCierzo found=" + (normal != null) + " DestroyedCierzo found=" + (destroyed != null));
            if (normal != null) Debug.Log("[VariantRefProbe] NormalCierzo path: " + GetFullPath(normal.transform) + " active=" + normal.activeInHierarchy);
            if (destroyed != null) Debug.Log("[VariantRefProbe] DestroyedCierzo path: " + GetFullPath(destroyed.transform) + " active=" + destroyed.activeInHierarchy);

            // list components on Interactions root and children
            Debug.Log("[VariantRefProbe] Components on Interactions root:");
            foreach (var c in interactionsRoot.GetComponents<Component>()) Debug.Log("[VariantRefProbe]  - " + (c == null ? "(null)" : c.GetType().FullName));

            if (normal != null)
            {
                Debug.Log("[VariantRefProbe] Components on NormalCierzo:");
                foreach (var c in normal.GetComponents<Component>()) Debug.Log("[VariantRefProbe]  - " + (c == null ? "(null)" : c.GetType().FullName));
            }
            if (destroyed != null)
            {
                Debug.Log("[VariantRefProbe] Components on DestroyedCierzo:");
                foreach (var c in destroyed.GetComponents<Component>()) Debug.Log("[VariantRefProbe]  - " + (c == null ? "(null)" : c.GetType().FullName));
            }

            // Scan scene for components that reference these objects or contain their names in strings
            var sceneRoots = scene.GetRootGameObjects();
            var sceneTransforms = sceneRoots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();

            int refMatches = 0;
            int stringMatches = 0;
            Debug.Log("[VariantRefProbe] Scanning scene components for references to NormalCierzo/DestroyedCierzo...");

            foreach (var tr in sceneTransforms)
            {
                if (tr == null) continue;
                var go = tr.gameObject;
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var t = comp.GetType();

                    // fields
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object val = null;
                        try { val = f.GetValue(comp); } catch { val = null; }
                        if (val == null) continue;

                        // handle GameObject
                        if (val is GameObject g)
                        {
                            if (g == normal || g == destroyed)
                            {
                                Debug.Log("[VariantRefProbe] REF Field: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> references " + (g == normal ? "NormalCierzo" : "DestroyedCierzo"));
                                refMatches++;
                            }
                            continue;
                        }

                        // handle Transform
                        if (val is Transform tf)
                        {
                            if (tf == normal?.transform || tf == destroyed?.transform)
                            {
                                Debug.Log("[VariantRefProbe] REF Field: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> references Transform " + (tf == normal?.transform ? "NormalCierzo" : "DestroyedCierzo"));
                                refMatches++;
                            }
                            continue;
                        }

                        // handle Component
                        if (val is Component cm)
                        {
                            if (cm.gameObject == normal || cm.gameObject == destroyed)
                            {
                                Debug.Log("[VariantRefProbe] REF Field: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> references component on " + (cm.gameObject == normal ? "NormalCierzo" : "DestroyedCierzo"));
                                refMatches++;
                            }
                            continue;
                        }

                        // arrays and lists (simple safe handling)
                        if (val is Array arr)
                        {
                            foreach (var item in arr)
                            {
                                if (item is GameObject ga && (ga == normal || ga == destroyed))
                                {
                                    Debug.Log("[VariantRefProbe] REF Field(Array): GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> contains reference to " + (ga == normal ? "NormalCierzo" : "DestroyedCierzo"));
                                    refMatches++;
                                }
                                else if (item is Transform ta && (ta == normal?.transform || ta == destroyed?.transform))
                                {
                                    Debug.Log("[VariantRefProbe] REF Field(Array): GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> contains Transform ref " + (ta == normal?.transform ? "NormalCierzo" : "DestroyedCierzo"));
                                    refMatches++;
                                }
                                else if (item is Component cb && (cb.gameObject == normal || cb.gameObject == destroyed))
                                {
                                    Debug.Log("[VariantRefProbe] REF Field(Array): GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> contains Component ref on " + (cb.gameObject == normal ? "NormalCierzo" : "DestroyedCierzo"));
                                    refMatches++;
                                }
                                else if (item is string ss && (!string.IsNullOrEmpty(ss) && ((normal != null && ss.IndexOf(normal.name, StringComparison.OrdinalIgnoreCase) >= 0) || (destroyed != null && ss.IndexOf(destroyed.name, StringComparison.OrdinalIgnoreCase) >= 0))))
                                {
                                    Debug.Log("[VariantRefProbe] STR Field(Array): GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> string contains token '" + ss + "'");
                                    stringMatches++;
                                }
                            }
                            continue;
                        }

                        // string field
                        if (val is string s)
                        {
                            if ((normal != null && s.IndexOf(normal.name, StringComparison.OrdinalIgnoreCase) >= 0) || (destroyed != null && s.IndexOf(destroyed.name, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                Debug.Log("[VariantRefProbe] STR Field: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Field='" + f.Name + "' -> '" + s + "'");
                                stringMatches++;
                            }
                        }
                    }

                    // properties (read-only)
                    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
                        object pval = null;
                        try { pval = p.GetValue(comp); } catch { pval = null; }
                        if (pval == null) continue;

                        if (pval is GameObject pg)
                        {
                            if (pg == normal || pg == destroyed)
                            {
                                Debug.Log("[VariantRefProbe] REF Prop: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Prop='" + p.Name + "' -> references " + (pg == normal ? "NormalCierzo" : "DestroyedCierzo"));
                                refMatches++;
                            }
                            continue;
                        }
                        if (pval is Transform ptf)
                        {
                            if (ptf == normal?.transform || ptf == destroyed?.transform)
                            {
                                Debug.Log("[VariantRefProbe] REF Prop: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Prop='" + p.Name + "' -> references Transform " + (ptf == normal?.transform ? "NormalCierzo" : "DestroyedCierzo"));
                                refMatches++;
                            }
                            continue;
                        }
                        if (pval is string ps)
                        {
                            if ((normal != null && ps.IndexOf(normal.name, StringComparison.OrdinalIgnoreCase) >= 0) || (destroyed != null && ps.IndexOf(destroyed.name, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                Debug.Log("[VariantRefProbe] STR Prop: GO='" + GetFullPath(tr) + "' Component='" + t.FullName + "' Prop='" + p.Name + "' -> '" + ps + "'");
                                stringMatches++;
                            }
                        }
                    }
                }
            }

            Debug.Log("[VariantRefProbe] scan complete. refMatches=" + refMatches + " stringMatches=" + stringMatches);
            Debug.Log("[VariantRefProbe] done");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[VariantRefProbe] error: " + ex);
        }
    }
}

