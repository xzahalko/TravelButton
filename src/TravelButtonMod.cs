using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System;
using BepInEx.Logging;

//
// TravelButtonMod.cs
// - BepInEx plugin bootstrap (TravelButtonPlugin) + runtime static helpers (TravelButtonMod).
// - Integrates with an optional external ConfigManager (safely, via reflection) and with BepInEx config system
//   so Configuration Manager displays editable settings.
// - Provides City model used by TravelButtonUI and helpers to map/persist configuration.
// - Adds diagnostics helpers DumpTravelButtonState and ForceShowTravelButton for runtime inspection.
//
[BepInPlugin("com.xzahalko.travelbutton", "TravelButton", "1.0.0")]
public class TravelButtonPlugin : BaseUnityPlugin
{

    // BepInEx config entries (top-level)
    private ConfigEntry<bool> bex_enableMod;
    private ConfigEntry<int> bex_globalPrice;
    private ConfigEntry<string> bex_currencyItem;

    // per-city config entries
    private Dictionary<string, ConfigEntry<bool>> bex_cityEnabled = new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ConfigEntry<int>> bex_cityPrice = new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase);

    // Optional prefix to make entries easy to find in BepInEx logs
    // Set by the plugin during Awake: e.g. TravelButtonPlugin.Initialize(this.Logger);
    public static ManualLogSource LogSource { get; private set; }
    private const string Prefix = "[TravelButton] ";

    public static void Initialize(ManualLogSource manualLogSource)
    {
        if (manualLogSource == null) throw new ArgumentNullException(nameof(manualLogSource));
        LogSource = manualLogSource;
        try { LogSource.LogInfo(Prefix + "TravelButtonPlugin initialized with BepInEx ManualLogSource."); } catch { /* swallow */ }
    }

    // static wrappers - always delegate safely to TravelButtonPlugin
    public static void LogInfo(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogInfo(Prefix + (message ?? ""));
        }
        catch { /* swallow */ }
    }

    public static void LogWarning(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogWarning(Prefix + (message ?? ""));
        }
        catch { /* swallow */ }
    }

    public static void LogError(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogError(Prefix + (message ?? ""));
        }
        catch { /* swallow */ }
    }

    public static void LogDebug(string message)
    {
        try
        {
            var src = LogSource;
            if (src == null) return;
            src.LogDebug(Prefix + (message ?? ""));
        }
        catch
        { }
    }

    private void Awake()
    {
        try { TravelButtonPlugin.Initialize(this.Logger); } catch { /* swallow */
        }

        this.Logger.LogInfo("[TravelButton] direct Logger test (should appear in LogOutput.log)");
        TravelButtonPlugin.LogInfo("TravelButtonPlugin test (should appear in LogOutput.log)");

        // sanity checks to confirm BepInEx receives logs:
        TravelButtonPlugin.LogInfo("[TravelButton] BepInEx Logger is available (this.Logger) - test message");

        try
        {
            TravelButtonPlugin.LogInfo("TravelButton: startup - loaded cities:");
            if (TravelButtonMod.Cities == null) TravelButtonPlugin.LogInfo(" - Cities == null");
            else
            {
                foreach (var c in TravelButtonMod.Cities)
                {
                    try
                    {
                        TravelButtonPlugin.LogInfo($" - '{c.name}' sceneName='{c.sceneName ?? ""}' coords=[{(c.coords != null ? string.Join(", ", c.coords) : "")}]");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("Startup city log failed: " + ex);
        }

        try
        {
            //TravelButtonPlugin.Logger = base.Logger;
            TravelButtonPlugin.LogInfo("TravelButtonPlugin.Awake: plugin initializing.");

            // Start coroutine that will attempt to initialize config safely (may call ConfigManager.Load when safe)
            StartCoroutine(TryInitConfigCoroutine());
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonPlugin.Awake exception: " + ex);
        }
    }

    private IEnumerator TryInitConfigCoroutine()
    {
        int maxAttempts = 10;
        int attempt = 0;
        bool initialized = false;

        while (attempt < maxAttempts && !initialized)
        {
            attempt++;
            TravelButtonPlugin.LogInfo($"TryInitConfigCoroutine: attempt {attempt}/{maxAttempts} to obtain config.");
            try
            {
                initialized = TravelButtonMod.InitFromConfig();
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("TryInitConfigCoroutine: InitFromConfig threw: " + ex.Message);
                initialized = false;
            }

            if (!initialized)
                yield return new WaitForSeconds(1.0f);
        }

        if (!initialized)
        {
            TravelButtonPlugin.LogWarning("TryInitConfigCoroutine: InitFromConfig did not find an external config after retries; using defaults.");
            if (TravelButtonMod.Cities == null || TravelButtonMod.Cities.Count == 0)
            {
                // Try local Default() again as a deterministic fallback
                try
                {
                    var localCfg = TravelButtonMod.GetLocalType("ConfigManager");
                    if (localCfg != null)
                    {
                        var def = localCfg.GetMethod("Default", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                        if (def != null)
                        {
                            TravelButtonMod.MapConfigInstanceToLocal(def);
                            TravelButtonPlugin.LogInfo("TryInitConfigCoroutine: populated config from local ConfigManager.Default() fallback.");
                            initialized = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("TryInitConfigCoroutine: fallback Default() failed: " + ex.Message);
                }
            }
        }

        // IMPORTANT: create BepInEx Config bindings so Configuration Manager (and BepInEx GUI) can show/edit settings.
        try
        {
            EnsureBepInExConfigBindings();
            TravelButtonPlugin.LogInfo("BepInEx config bindings created.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("Failed to create BepInEx config bindings: " + ex);
        }

        // Bind any cities that were added after initial bind (defensive)
        try
        {
            BindCityConfigsForNewCities();
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("BindCityConfigsForNewCities failed: " + ex);
        }

        // Finally ensure UI exists so the player can interact
        EnsureTravelButtonUI();
    }

    // Exposed logger set by the plugin bootstrap. May be null early during domain load.
    private void EnsureTravelButtonUI()
    {
        try
        {
            var existing = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (existing != null)
            {
                TravelButtonPlugin.LogInfo("EnsureTravelButtonUI: TravelButtonUI already present in scene.");
                return;
            }

            var go = new GameObject("TravelButtonUI_Bootstrap");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TravelButtonUI>();
            TravelButtonPlugin.LogInfo("EnsureTravelButtonUI: TravelButtonUI component created and DontDestroyOnLoad applied.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("EnsureTravelButtonUI failed: " + ex);
        }
    }

    // --- BepInEx config binding helpers ---

    // Create top-level and per-city BepInEx Config.Bind entries and wire change handlers.
    // Call this once after TravelButtonMod.Cities is populated (InitFromConfig success or fallback).
    private void EnsureBepInExConfigBindings()
    {
        try
        {
            // Top-level bindings (section: TravelButton)
            bex_enableMod = Config.Bind("TravelButton", "EnableMod", TravelButtonMod.cfgEnableMod.Value, "Enable or disable the TravelButton mod");
            bex_globalPrice = Config.Bind("TravelButton", "GlobalTravelPrice", TravelButtonMod.cfgTravelCost.Value, "Default cost for teleport (silver)");
            bex_currencyItem = Config.Bind("TravelButton", "CurrencyItem", TravelButtonMod.cfgCurrencyItem.Value, "Item name used as currency");

            // Apply values from ConfigEntries into runtime wrappers
            TravelButtonMod.cfgEnableMod.Value = bex_enableMod.Value;
            TravelButtonMod.cfgTravelCost.Value = bex_globalPrice.Value;
            TravelButtonMod.cfgCurrencyItem.Value = bex_currencyItem.Value;

            // Hook top-level changes so runtime values update when user edits via CM
            bex_enableMod.SettingChanged += (s, e) =>
            {
                TravelButtonMod.cfgEnableMod.Value = bex_enableMod.Value;
                TravelButtonMod.PersistCitiesToConfig();
                TravelButtonPlugin.LogInfo($"BepInEx config changed: EnableMod = {bex_enableMod.Value}");
            };
            bex_globalPrice.SettingChanged += (s, e) =>
            {
                TravelButtonMod.cfgTravelCost.Value = bex_globalPrice.Value;
                TravelButtonPlugin.LogInfo($"BepInEx config changed: GlobalTravelPrice = {bex_globalPrice.Value}");
            };
            bex_currencyItem.SettingChanged += (s, e) =>
            {
                TravelButtonMod.cfgCurrencyItem.Value = bex_currencyItem.Value;
            };

            // Per-city bindings (section: TravelButton.Cities)
            if (TravelButtonMod.Cities == null) TravelButtonMod.Cities = new List<TravelButtonMod.City>();

            foreach (var city in TravelButtonMod.Cities)
            {
                // Avoid duplicate binds
                if (bex_cityEnabled.ContainsKey(city.name)) continue;

                string section = "TravelButton.Cities";
                var enabledKey = Config.Bind(section, $"{city.name}.Enabled", city.enabled, $"Enable teleport destination {city.name}");
                var priceDefault = city.price ?? TravelButtonMod.cfgTravelCost.Value;
                var priceKey = Config.Bind(section, $"{city.name}.Price", priceDefault, $"Price to teleport to {city.name} (overrides global)");

                bex_cityEnabled[city.name] = enabledKey;
                bex_cityPrice[city.name] = priceKey;

                // Sync config values into runtime city object
                city.enabled = enabledKey.Value;
                city.price = priceKey.Value;

                enabledKey.SettingChanged += (s, e) =>
                {
                    city.enabled = enabledKey.Value;
                    TravelButtonPlugin.LogInfo($"Config changed: {city.name}.Enabled = {enabledKey.Value}");
                    TravelButtonMod.PersistCitiesToConfig();
                };
                priceKey.SettingChanged += (s, e) =>
                {
                    city.price = priceKey.Value;
                    TravelButtonPlugin.LogInfo($"Config changed: {city.name}.Price = {priceKey.Value}");
                    TravelButtonMod.PersistCitiesToConfig();
                };
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("EnsureBepInExConfigBindings failed: " + ex);
        }
    }

    // Helper to bind config entries for any cities added at runtime after initial bind
    // Call BindCityConfigsForNewCities() if your code adds new cities later.
    private void BindCityConfigsForNewCities()
    {
        try
        {
            if (TravelButtonMod.Cities == null) return;
            foreach (var city in TravelButtonMod.Cities)
            {
                if (bex_cityEnabled.ContainsKey(city.name)) continue;
                string section = "TravelButton.Cities";
                var enabledKey = Config.Bind(section, $"{city.name}.Enabled", city.enabled, $"Enable teleport destination {city.name}");
                var priceDefault = city.price ?? TravelButtonMod.cfgTravelCost.Value;
                var priceKey = Config.Bind(section, $"{city.name}.Price", priceDefault, $"Price to teleport to {city.name} (overrides global)");

                bex_cityEnabled[city.name] = enabledKey;
                bex_cityPrice[city.name] = priceKey;

                // sync initial runtime
                city.enabled = enabledKey.Value;
                city.price = priceKey.Value;

                enabledKey.SettingChanged += (s, e) =>
                {
                    city.enabled = enabledKey.Value;
                    TravelButtonMod.PersistCitiesToConfig();
                };
                priceKey.SettingChanged += (s, e) =>
                {
                    city.price = priceKey.Value;
                    TravelButtonMod.PersistCitiesToConfig();
                };
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("BindCityConfigsForNewCities failed: " + ex);
        }
    }

    // Helper: search loaded assemblies for a type by simple name
    private static Type GetTypeByName(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetTypes().FirstOrDefault(x => x.Name == simpleName);
                if (t != null) return t;
            }
            catch { /* ignore */ }
        }
        return null;
    }
}

public static class TravelButtonMod
{
    public static bool TeleportInProgress = false;

    public static void LogLoadedScenesAndRootObjects()
    {
        try
        {
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            TravelButtonPlugin.LogInfo($"LogLoadedScenesAndRootObjects: {sceneCount} loaded scene(s).");
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.IsValid()) continue;
                TravelButtonPlugin.LogInfo($" Scene #{i}: name='{scene.name}', isLoaded={scene.isLoaded}, isDirty={scene.isDirty}");
                var roots = scene.GetRootGameObjects();
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    TravelButtonPlugin.LogInfo($"  root: '{r.name}' (children count approx: {r.transform.childCount})");
                }
            }
        }
        catch (Exception ex) { TravelButtonPlugin.LogWarning("LogLoadedScenesAndRootObjects exception: " + ex.Message); }
    }

    public static void LogCityAnchorsFromLoadedScenes()
    {
        try
        {
            if (Cities == null || Cities.Count == 0)
            {
                TravelButtonPlugin.LogWarning("LogCityAnchorsFromLoadedScenes: no cities available.");
                return;
            }

            TravelButtonPlugin.LogInfo($"LogCityAnchorsFromLoadedScenes: scanning {UnityEngine.SceneManagement.SceneManager.sceneCount} loaded scene(s) for city anchors...");

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

                    TravelButtonPlugin.LogInfo($"CityScan: --- {cname} --- targetGameObjectName='{target}' (existing sceneName='{city.sceneName ?? ""}'), coords={(city.coords != null ? $"[{string.Join(", ", city.coords)}]" : "(null)")}");

                    bool foundAny = false;

                    if (!string.IsNullOrEmpty(target))
                    {
                        if (lookup.TryGetValue(target, out var exacts) && exacts.Count > 0)
                        {
                            foreach (var (sceneName, tr) in exacts)
                            {
                                var pos = tr.position;
                                TravelButtonPlugin.LogInfo($"CityScan: FOUND exact '{target}' in scene '{sceneName}' at ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) path='{GetFullPath(tr)}'");
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
                                TravelButtonPlugin.LogInfo($"CityScan: FOUND active exact '{target}' in scene '{s}' at ({p.x:F3}, {p.y:F3}, {p.z:F3})");
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
                        TravelButtonPlugin.LogInfo($"CityScan: SUBSTRING match '{m.tr.name}' in scene '{sceneN}' at ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) path='{path}'");
                        reportedCount++;
                        if (reportedCount >= 20) break;
                    }

                    if (!foundAny && reportedCount == 0)
                        TravelButtonPlugin.LogInfo($"CityScan: no matches found in loaded scenes for city '{cname}'. Consider loading the map or using in-game travel to that map, then run this again.");
                }
                catch (Exception exCity)
                {
                    TravelButtonPlugin.LogWarning("CityScan: error scanning city: " + exCity.Message);
                }
            }

            TravelButtonPlugin.LogInfo("LogCityAnchorsFromLoadedScenes: scan complete.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("LogCityAnchorsFromLoadedScenes exception: " + ex.Message);
        }
    }

    public static void AutoAssignSceneNamesFromLoadedScenes()
    {
        try
        {
            TravelButtonPlugin.LogInfo("AutoAssignSceneNamesFromLoadedScenes: scanning loaded scenes for city anchors/names...");
            if (Cities == null || Cities.Count == 0)
            {
                TravelButtonPlugin.LogWarning("AutoAssignSceneNamesFromLoadedScenes: no cities available to scan.");
                return;
            }

            int assigned = 0;
            // iterate loaded scenes
            int sceneCount = SceneManager.sceneCount;
            for (int si = 0; si < sceneCount; si++)
            {
                var scene = SceneManager.GetSceneAt(si);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    var allTransforms = root.GetComponentsInChildren<Transform>(true);
                    foreach (var tr in allTransforms)
                    {
                        if (tr == null) continue;
                        string gname = tr.name ?? "";
                        // try match by exact targetGameObjectName first, then city name substring
                        foreach (var city in Cities)
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(city.targetGameObjectName) &&
                                    string.Equals(gname, city.targetGameObjectName, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (string.IsNullOrEmpty(city.sceneName) || city.sceneName != scene.name)
                                    {
                                        city.sceneName = scene.name;
                                        TravelButtonPlugin.LogInfo($"AutoAssign: matched targetGameObjectName '{gname}' -> setting city '{city.name}'.sceneName = '{scene.name}'");
                                        assigned++;
                                    }
                                }
                                else if (gname.IndexOf(city.name ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (string.IsNullOrEmpty(city.sceneName) || city.sceneName != scene.name)
                                    {
                                        city.sceneName = scene.name;
                                        TravelButtonPlugin.LogInfo($"AutoAssign: matched name substring '{gname}' -> setting city '{city.name}'.sceneName = '{scene.name}'");
                                        assigned++;
                                    }
                                }
                            }
                            catch { /* ignore per-city errors */ }
                        }
                    }
                }
            }

            if (assigned > 0)
            {
                TravelButtonPlugin.LogInfo($"AutoAssignSceneNamesFromLoadedScenes: assigned {assigned} sceneName(s). Persisting cities to config.");
                try { PersistCitiesToConfig(); } catch { TravelButtonPlugin.LogWarning("AutoAssignSceneNamesFromLoadedScenes: PersistCitiesToConfig failed."); }
            }
            else
            {
                TravelButtonPlugin.LogInfo("AutoAssignSceneNamesFromLoadedScenes: no matches found in loaded scenes. Make sure the correct scene is loaded and try again.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("AutoAssignSceneNamesFromLoadedScenes exception: " + ex.Message);
        }
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

        public string sceneName;

        public City(string name)
        {
            this.name = name;
            this.coords = null;
            this.targetGameObjectName = null;
            this.price = null;
            this.enabled = false;
            this.sceneName = null;
        }

        // Compatibility properties expected by older code:
        // property 'visited' (lowercase) → maps to VisitedTracker if available
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
    public static List<City> Cities { get; set; } = new List<City>();

    // Path/filename helpers exposed for debugging
    public static string ConfigFilePath
    {
        get
        {
            try { return ConfigManager.ConfigPathForLog(); }
            catch { return "(unknown)"; }
        }
    }

    // Initialize mod state from JSON config -> should be called once at mod load
    // Returns true if a config instance was located and mapped (or local default used), false otherwise.
    public static bool InitFromConfig()
    {
        try
        {
            TravelButtonPlugin.LogInfo("InitFromConfig: attempting to obtain ConfigManager.Config (safe, no unconditional Load).");

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
                    TravelButtonPlugin.LogWarning("InitFromConfig: reading ConfigManager.Config threw: " + ex.Message);
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
                                TravelButtonPlugin.LogInfo("InitFromConfig: used local ConfigManager.Default() to populate config.");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning("InitFromConfig: calling local ConfigManager.Default() failed: " + ex.Message);
                        // continue to try safer external Load path below
                    }
                }
            }

            // If we still don't have a config instance but found an external ConfigManager type,
            // we may attempt to call its Load() safely (only if local or Newtonsoft is available).
            if (cfgInstance == null && cfgMgrType != null)
            {
                bool callLoad = false;
                bool isLocalConfigMgr = cfgMgrType.Assembly == typeof(TravelButtonMod).Assembly;

                if (isLocalConfigMgr)
                {
                    callLoad = true;
                    TravelButtonPlugin.LogInfo("InitFromConfig: calling Load() on local ConfigManager type.");
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
                        TravelButtonPlugin.LogInfo("InitFromConfig: external ConfigManager found and Newtonsoft present; will call Load() via reflection.");
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning("InitFromConfig: external ConfigManager found but Newtonsoft not present; skipping Load() to avoid assembly load errors.");
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
                            TravelButtonPlugin.LogWarning("InitFromConfig: ConfigManager.Load method not found.");
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        TravelButtonPlugin.LogWarning("InitFromConfig: ConfigManager.Load failed via reflection: " + (tie.InnerException?.Message ?? tie.Message));
                        return false; // allow retry from coroutine
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning("InitFromConfig: exception invoking ConfigManager.Load: " + ex.Message);
                        return false;
                    }
                }
            }

            // If we have a config instance now, map it into local fields and cities
            if (cfgInstance != null)
            {
                MapConfigInstanceToLocal(cfgInstance);
                TravelButtonPlugin.LogInfo($"InitFromConfig: Loaded {Cities?.Count ?? 0} cities from ConfigManager.");
                return true;
            }

            // No config available (and we failed to get a local default); signal caller to retry / fallback.
            TravelButtonPlugin.LogInfo("InitFromConfig: no config instance available (will retry or fallback).");
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
            // top-level mappings
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
                TravelButtonPlugin.LogWarning("MapConfigInstanceToLocal: top-level map failed: " + ex.Message);
            }

            // cities
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
                                if (mapped != null) Cities.Add(mapped);
                            }
                            catch (Exception inner)
                            {
                                TravelButtonPlugin.LogWarning("MapConfigInstanceToLocal: error mapping city entry: " + inner.Message);
                            }
                        }
                    }
                    else
                    {
                        // try enumerator approach for generic IDictionary<,>
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
                            TravelButtonPlugin.LogWarning("MapConfigInstanceToLocal: cfg.cities is not enumerable.");
                        }
                    }
                }
                else
                {
                    TravelButtonPlugin.LogWarning("MapConfigInstanceToLocal: cfg.cities is null.");
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("MapConfigInstanceToLocal: cities mapping failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("MapConfigInstanceToLocal: unexpected: " + ex.Message);
        }
    }

    private static City MapSingleCityFromObject(string cname, object cityCfgObj)
    {
        try
        {
            var city = new City(cname);

            var enabledMember = cityCfgObj.GetType().GetField("enabled") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("enabled");
            if (enabledMember is FieldInfo fe) city.enabled = SafeGetBool(fe.GetValue(cityCfgObj));
            else if (enabledMember is PropertyInfo pe) city.enabled = SafeGetBool(pe.GetValue(cityCfgObj));

            var priceMember = cityCfgObj.GetType().GetField("price") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("price");
            if (priceMember is FieldInfo fprice) city.price = SafeGetNullableInt(fprice.GetValue(cityCfgObj));
            else if (priceMember is PropertyInfo pprice) city.price = SafeGetNullableInt(pprice.GetValue(cityCfgObj));

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
                    }
                    catch { city.coords = null; }
                }
            }

            var tgnMember = cityCfgObj.GetType().GetField("targetGameObjectName") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("targetGameObjectName");
            if (tgnMember is FieldInfo ftgn) city.targetGameObjectName = SafeGetString(ftgn.GetValue(cityCfgObj));
            else if (tgnMember is PropertyInfo ptgn) city.targetGameObjectName = SafeGetString(ptgn.GetValue(cityCfgObj));

            var sceneMember = cityCfgObj.GetType().GetField("sceneName") ?? (MemberInfo)cityCfgObj.GetType().GetProperty("sceneName");
            if (sceneMember is FieldInfo fsc) city.sceneName = SafeGetString(fsc.GetValue(cityCfgObj));
            else if (sceneMember is PropertyInfo psc) city.sceneName = SafeGetString(psc.GetValue(cityCfgObj));

            return city;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("MapSingleCityFromObject: " + ex.Message);
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
                if (asm == typeof(TravelButtonMod).Assembly)
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
                        TravelButtonPlugin.LogInfo($"TryGetTargetPosition: found active GameObject '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                        return true;
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning($"TryGetTargetPosition: found '{targetGameObjectName}' but it's a UI/invalid-scene object (ignored).");
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
                    TravelButtonPlugin.LogInfo($"TryGetTargetPosition: found scene GameObject by exact match '{exactSceneObj.name}' at {outPos} for city '{cityName}'.");
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
                    TravelButtonPlugin.LogInfo($"TryGetTargetPosition: found scene GameObject by substring match '{containsSceneObj.name}' -> '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                    return true;
                }

                // Tag lookup fallback (only accept scene objects and non-UI)
                try
                {
                    var byTag = GameObject.FindGameObjectWithTag(targetGameObjectName);
                    if (byTag != null && byTag.scene.IsValid() && byTag.scene.isLoaded && !IsUiGameObject(byTag))
                    {
                        outPos = byTag.transform.position;
                        TravelButtonPlugin.LogInfo($"TryGetTargetPosition: found GameObject by tag '{targetGameObjectName}' at {outPos} for city '{cityName}'.");
                        return true;
                    }
                }
                catch { /* ignore tag errors */ }

                TravelButtonPlugin.LogWarning($"TryGetTargetPosition: target GameObject '{targetGameObjectName}' not found in any loaded scene for city '{cityName}'.");
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning($"TryGetTargetPosition: error while searching for '{targetGameObjectName}' for city '{cityName}': {ex.Message}");
            }

            // Optionally emit diagnostic candidates for debugging
            try { LogCandidateAnchorNames(cityName); } catch { }
        }

        // 3) Fallback to explicit coords (if present)
        if (coordsFallback != null && coordsFallback.Length >= 3)
        {
            outPos = new Vector3(coordsFallback[0], coordsFallback[1], coordsFallback[2]);
            TravelButtonPlugin.LogInfo($"TryGetTargetPosition: using explicit coords ({outPos.x}, {outPos.y}, {outPos.z}) for city '{cityName}'.");
            return true;
        }

        TravelButtonPlugin.LogWarning($"TryGetTargetPosition: no GameObject and no explicit coords available for city '{cityName}'.");
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
            TravelButtonPlugin.LogInfo($"Anchor diagnostic: searching for candidates for city '{cityName}'...");

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
                    TravelButtonPlugin.LogInfo($"Anchor candidate #{count}: name='{name}' scene='{scene}' path='{path}'");
                    if (count >= maxResults) break;
                }
            }

            if (count == 0)
                TravelButtonPlugin.LogInfo($"Anchor diagnostic: no candidates found for '{cityName}' (tried substrings). Consider checking scene objects or config targetGameObjectName.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("Anchor diagnostic failed: " + ex.Message);
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
            TravelButtonPlugin.LogWarning("IsCityEnabled: reading external config failed: " + ex.Message);
        }

        var local = Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
        if (local != null) return local.enabled;

        return false;
    }

    // Update in-memory Cities -> config and save; useful if user toggles a city via UI/editor
    public static void PersistCitiesToConfig()
    {
        try
        {
            bool persisted = false;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var cfgMgrType = asm.GetTypes().FirstOrDefault(t => t.Name == "ConfigManager");
                    if (cfgMgrType == null) continue;

                    var cfgProp = cfgMgrType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                    var cfgField = cfgMgrType.GetField("Config", BindingFlags.Public | BindingFlags.Static);
                    object cfgInstance = cfgProp != null ? cfgProp.GetValue(null) : cfgField != null ? cfgField.GetValue(null) : null;
                    if (cfgInstance == null) continue;

                    var citiesMemberField = cfgInstance.GetType().GetField("cities");
                    var citiesMemberProp = cfgInstance.GetType().GetProperty("cities");
                    object citiesObj = citiesMemberField != null ? citiesMemberField.GetValue(cfgInstance) : citiesMemberProp != null ? citiesMemberProp.GetValue(cfgInstance) : null;

                    if (citiesObj is System.Collections.IDictionary dict)
                    {
                        var genericArgs = dict.GetType().GetGenericArguments();
                        Type cityCfgType = genericArgs != null && genericArgs.Length >= 2 ? genericArgs[1] : null;

                        foreach (var city in Cities)
                        {
                            object cc = null;
                            if (cityCfgType != null)
                            {
                                try
                                {
                                    cc = Activator.CreateInstance(cityCfgType);
                                    var fEnabled = cityCfgType.GetField("enabled") ?? (MemberInfo)cityCfgType.GetProperty("enabled");
                                    if (fEnabled is FieldInfo fe) fe.SetValue(cc, city.enabled);
                                    else if (fEnabled is PropertyInfo pe) pe.SetValue(cc, city.enabled);

                                    var fPrice = cityCfgType.GetField("price") ?? (MemberInfo)cityCfgType.GetProperty("price");
                                    if (fPrice is FieldInfo fp) fp.SetValue(cc, city.price);
                                    else if (fPrice is PropertyInfo pp) pp.SetValue(cc, city.price);

                                    var fCoords = cityCfgType.GetField("coords") ?? (MemberInfo)cityCfgType.GetProperty("coords");
                                    if (fCoords is FieldInfo fc) fc.SetValue(cc, city.coords);
                                    else if (fCoords is PropertyInfo pc) pc.SetValue(cc, city.coords);

                                    var fTgn = cityCfgType.GetField("targetGameObjectName") ?? (MemberInfo)cityCfgType.GetProperty("targetGameObjectName");
                                    if (fTgn is FieldInfo ft) ft.SetValue(cc, city.targetGameObjectName);
                                    else if (fTgn is PropertyInfo pt) pt.SetValue(cc, city.targetGameObjectName);
                                }
                                catch { cc = null; }
                            }

                            try
                            {
                                dict[city.name] = cc ?? city;
                            }
                            catch
                            {
                                var addMethod = dict.GetType().GetMethod("Add");
                                if (addMethod != null) addMethod.Invoke(dict, new object[] { city.name, cc ?? city });
                            }
                        }

                        var saveMethod = cfgMgrType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static);
                        saveMethod?.Invoke(null, null);
                        persisted = true;
                        TravelButtonPlugin.LogInfo("PersistCitiesToConfig: persisted cities into external ConfigManager.Config and called Save().");
                        break;
                    }
                }
                catch { }
            }

            if (!persisted)
            {
                TravelButtonPlugin.LogWarning("PersistCitiesToConfig: Could not persist cities because external ConfigManager not found or not writable.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("PersistCitiesToConfig exception: " + ex);
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
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("OnSuccessfulTeleport exception: " + ex);
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
                TravelButtonPlugin.LogWarning("DumpTravelButtonState: TravelButton GameObject not found.");
                return;
            }

            var rt = tb.GetComponent<RectTransform>();
            var btn = tb.GetComponent<Button>();
            var img = tb.GetComponent<Image>();
            var cg = tb.GetComponent<CanvasGroup>();
            var root = tb.transform.root;
            TravelButtonPlugin.LogInfo($"DumpTravelButtonState: name='{tb.name}', activeSelf={tb.activeSelf}, activeInHierarchy={tb.activeInHierarchy}");
            TravelButtonPlugin.LogInfo($"DumpTravelButtonState: parent='{tb.transform.parent?.name}', root='{root?.name}'");
            if (rt != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: anchoredPosition={rt.anchoredPosition}, sizeDelta={rt.sizeDelta}, anchorMin={rt.anchorMin}, anchorMax={rt.anchorMax}, pivot={rt.pivot}");
            if (btn != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: Button.interactable={btn.interactable}");
            if (img != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: Image.color={img.color}, raycastTarget={img.raycastTarget}");
            if (cg != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: CanvasGroup alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts}");
            var canvas = tb.GetComponentInParent<Canvas>();
            if (canvas != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: Canvas name={canvas.gameObject.name}, sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode}");
            else TravelButtonPlugin.LogWarning("DumpTravelButtonState: No parent Canvas found.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("DumpTravelButtonState exception: " + ex.Message);
        }
    }

    // Force the TravelButton to a top-level Canvas and make it visible. Useful for debugging visibility/clipping/sorting issues.
    public static void ForceShowTravelButton()
    {
        try
        {
            var tb = GameObject.Find("TravelButton");
            if (tb == null)
            {
                TravelButtonPlugin.LogWarning("ForceShowTravelButton: TravelButton GameObject not found.");
                return;
            }

            // find or create a top-level Canvas
            Canvas canvas = GameObject.FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                var go = new GameObject("TravelButton_DebugCanvas");
                canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                go.AddComponent<GraphicRaycaster>();
                UnityEngine.Object.DontDestroyOnLoad(go);
                TravelButtonPlugin.LogInfo("ForceShowTravelButton: created debug Canvas 'TravelButton_DebugCanvas'.");
            }
            else
            {
                // ensure GraphicRaycaster present
                if (canvas.GetComponent<GraphicRaycaster>() == null)
                {
                    canvas.gameObject.AddComponent<GraphicRaycaster>();
                    TravelButtonPlugin.LogInfo("ForceShowTravelButton: added missing GraphicRaycaster to existing Canvas.");
                }
            }

            // Reparent the TravelButton to the canvas root
            tb.transform.SetParent(canvas.transform, false);

            // Ensure RectTransform exists and set to top-center default
            var rt = tb.GetComponent<RectTransform>();
            if (rt == null) rt = tb.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -40);
            rt.sizeDelta = new Vector2(140, 32);

            tb.SetActive(true);

            // Reset visuals so it's visible
            var img = tb.GetComponent<Image>();
            if (img != null) img.color = new Color(0.45f, 0.26f, 0.13f, 1f);

            var btn = tb.GetComponent<Button>();
            if (btn != null) btn.interactable = true;

            // If there is a CanvasGroup on any parent that could hide it, try to set parent CanvasGroup alpha to 1
            var parentCg = tb.GetComponentInParent<CanvasGroup>();
            if (parentCg != null)
            {
                try
                {
                    parentCg.alpha = 1f;
                    parentCg.interactable = true;
                    parentCg.blocksRaycasts = true;
                }
                catch { }
            }

            TravelButtonPlugin.LogInfo("ForceShowTravelButton: forced TravelButton onto top Canvas and made visible.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ForceShowTravelButton exception: " + ex.Message);
        }
    }
}