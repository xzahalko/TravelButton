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
[BepInPlugin("cz.valheimskal.travelbutton", "TravelButton", "1.1.1")]
public class TravelButtonPlugin : BaseUnityPlugin
{
    public static TravelButtonPlugin Instance { get; private set; }

    // BepInEx config entries (top-level)
    private BepInEx.Configuration.ConfigEntry<bool> bex_enableMod;
    private BepInEx.Configuration.ConfigEntry<int> bex_globalPrice;
    private BepInEx.Configuration.ConfigEntry<string> bex_currencyItem;
    //    private BepInEx.Configuration.ConfigEntry<string> bex_teleportMode;

    // example: add in your plugin Init (BepInEx) so users can toggle:
    public static BepInEx.Configuration.ConfigEntry<bool> cfgUseTransitionScene;

    // per-city config entries
    private Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> bex_cityEnabled = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> bex_cityPrice = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>(StringComparer.InvariantCultureIgnoreCase);
    private Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> bex_cityVisited = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.InvariantCultureIgnoreCase);

    // Filenames used by the plugin
    public const string CitiesJsonFileName = "TravelButton_Cities.json";
    public const string LegacyCfgFileName = "cz.valheimskal.travelbutton.cfg";

    /// <summary>
    /// Directory that contains the plugin DLL. Detected at runtime from Assembly location.
    /// </summary>
    private static string PluginTravelButtonFolder => GetPluginFolder();

    /// <summary>
    /// Detect BepInEx config folder by walking up from the plugin folder to the BepInEx root, returning its "config" child.
    /// Falls back to AppDomain base directory + BepInEx/config when not discovered.
    /// </summary>
    private static string PreferredBepInExConfigFolder => GetBepInExConfigFolder();

    // Optional prefix to make entries easy to find in BepInEx logs
    // Set by the plugin during Awake: e.g. TravelButtonPlugin.Initialize(this.Logger);
    public static ManualLogSource LogSource { get; private set; }
    private const string Prefix = "[TravelButton] ";

    private DateTime _lastConfigChange = DateTime.MinValue;

    private bool _suppressVisitedSettingChanged = false;

    private static TeleportMode ParseTeleportMode(string v)
    {
        if (string.IsNullOrEmpty(v)) return TeleportMode.Auto;
        if (Enum.TryParse<TeleportMode>(v, true, out var parsed)) return parsed;
        // optional: log invalid value
        TBLog.Warn($"[TravelButtonPlugin] Unknown TeleportMode '{v}', defaulting to Auto.");
        return TeleportMode.Auto;
    }

    private static string TeleportModeToString(TeleportMode mode) => mode.ToString();

    public static void Initialize(ManualLogSource manualLogSource)
    {
        if (manualLogSource == null) throw new ArgumentNullException(nameof(manualLogSource));
        LogSource = manualLogSource;
        try { LogSource.LogInfo(Prefix + "TravelButtonPlugin initialized with BepInEx ManualLogSource."); } catch { /* swallow */ }
    }

    /// <summary>
    /// Full path to TravelButton_Cities.json (expected next to the plugin DLL).
    /// </summary>
    public static string GetCitiesJsonPath()
    {
        try
        {
            var dir = PluginTravelButtonFolder;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir ?? string.Empty, CitiesJsonFileName);
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] GetCitiesJsonPath: failed to construct path: " + ex);
            return CitiesJsonFileName;
        }
    }

    /// <summary>
    /// Full path to the legacy .cfg file under the detected BepInEx config folder.
    /// </summary>
    public static string GetLegacyCfgPath()
    {
        try
        {
            var dir = PreferredBepInExConfigFolder;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir ?? string.Empty, LegacyCfgFileName);
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] GetLegacyCfgPath: failed to construct path: " + ex);
            return LegacyCfgFileName;
        }
    }

    private static string GetPluginFolder()
    {
        try
        {
            // Use the location of this assembly (the DLL) to find the plugin directory.
            var asm = Assembly.GetExecutingAssembly();
            var asmPath = asm?.Location;
            if (!string.IsNullOrEmpty(asmPath))
            {
                var dir = Path.GetDirectoryName(asmPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    TBLog.Info($"[TravelButton] Detected plugin folder from assembly location: {dir}");
                    return dir;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] GetPluginFolder: assembly-based detection failed: " + ex);
        }

        // Fallback: use current base directory (game root) + BepInEx/plugins/TravelButton
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
            var fallback = Path.Combine(baseDir, "BepInEx", "plugins", "TravelButton");
            TBLog.Warn($"[TravelButton] GetPluginFolder: falling back to guessed path: {fallback}");
            return fallback;
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] GetPluginFolder: fallback path construction failed: " + ex);
            return string.Empty;
        }
    }

    private static string GetBepInExConfigFolder()
    {
        try
        {
            // If we have a plugin folder discovered, walk upward to find BepInEx root
            var pluginFolder = GetPluginFolder();
            if (!string.IsNullOrEmpty(pluginFolder))
            {
                var di = new DirectoryInfo(pluginFolder);
                DirectoryInfo current = di;
                while (current != null)
                {
                    if (string.Equals(current.Name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                    {
                        var cfg = Path.Combine(current.FullName, "config");
                        TBLog.Info($"[TravelButton] Detected BepInEx config folder by walking up from plugin folder: {cfg}");
                        return cfg;
                    }
                    current = current.Parent;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] GetBepInExConfigFolder: walking-from-plugin detection failed: " + ex);
        }

        // Fallback: base directory + BepInEx/config
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
            var fallbackCfg = Path.Combine(baseDir, "BepInEx", "config");
            TBLog.Warn($"[TravelButton] GetBepInExConfigFolder: falling back to guessed path: {fallbackCfg}");
            return fallbackCfg;
        }
        catch (Exception ex)
        {
            TBLog.Warn("[TravelButton] GetBepInExConfigFolder: fallback path construction failed: " + ex);
            return string.Empty;
        }
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

    // 1) In Awake(), call the instance explicitly to avoid ambiguity.
    // 2) Keep the full consolidated InitializeCitiesAndConfig (the second version below).
    // Make sure you remove any other duplicate InitializeCitiesAndConfig definitions in this file.

    private void Awake()
    {
        DebugConfig.IsDebug = true;

        // avoid creating multiple hooks
        if (GameObject.Find("SceneLoadHook") == null)
        {
            var hookGO = new GameObject("SceneLoadHook");
            hookGO.AddComponent<SceneLoadHook>();
            UnityEngine.Object.DontDestroyOnLoad(hookGO);
        }

        // Set the static instance reference
        Instance = this;

        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

        try { TravelButtonPlugin.Initialize(this.Logger); } catch { /* swallow */ }

        this.Logger.LogInfo("[TravelButton] direct Logger test (should appear in LogOutput.log)");
        TBLog.Info("TravelButtonPlugin test (should appear in LogOutput.log)");
        TBLog.Info("[TravelButton] BepInEx Logger is available (this.Logger) - test message");

        // Consolidated initialization: read JSON/config once and wire bindings/watchers.
        try
        {
            // explicit instance call to disambiguate if any static/duplicate method exists
            this.InitializeCitiesAndConfig();
        }
        catch (Exception ex)
        {
            TBLog.Warn("TravelButtonPlugin.Awake: InitializeCitiesAndConfig failed: " + ex);
        }

        ShowPlayerNotification = (msg) =>
        {
            TravelButtonNotificationUI.Show(msg, 3f);
        };

        cfgUseTransitionScene = Config.Bind("Travel", "UseTransitionScene", true, "Load LowMemory_TransitionScene before the real target to force engine re-init.");
    }

    /// <summary>
    /// Consolidated initialization flow according to specification:
    /// 1. Check/Create JSON from TravelManager.DefaultCities.
    /// 2. Check completeness of JSON against TravelManager.DefaultCities and add missing.
    /// 3. Load JSON into TravelButton.Cities.
    /// 4. Create CFG if missing based on TravelButton.Cities.
    /// 5. Update TravelButton.Cities from CFG if it exists.
    /// 6. Add missing entries to CFG.
    /// 7. Update In-Game config.
    /// </summary>
    private void InitializeCitiesAndConfig()
    {
        TBLog.Info("InitializeCitiesAndConfig: BEGIN initialization per spec.");

        try
        {
            // 1. & 2. Check/Create JSON and completeness
            TravelButton.EnsureJsonIntegrity();
            TBLog.Info("InitializeCitiesAndConfig: JSON integrity check completed.");

            // 3. Load JSON into TravelButton.Cities
            if (TravelButton.LoadCitiesFromJson())
            {
                TBLog.Info("InitializeCitiesAndConfig: Loaded cities from JSON.");
            }
            else
            {
                TBLog.Warn("InitializeCitiesAndConfig: Failed to load cities from JSON.");
            }

            // 4. Create CFG if missing & 6. Update CFG with missing entries
            // 5. Update TravelButton.Cities from CFG & 7. Update In-Game config
            // EnsureBepInExConfigBindings handles creation, binding (reading from CFG), and in-game config updates.
            EnsureBepInExConfigBindings();
            TBLog.Info("InitializeCitiesAndConfig: CFG and BepInEx bindings processed.");

            StartConfigWatcher();
            TBLog.Info("InitializeCitiesAndConfig: Config watcher started.");

            // Ensure UI
            EnsureTravelButtonUI();
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: Initialization failed: " + ex);
        }

        TBLog.Info("InitializeCitiesAndConfig: END initialization.");
    }


    /// <summary>
    /// Diagnostic helper: dump runtime TravelButton.Cities state to log.
    /// </summary>
    private void DumpRuntimeCitiesState(string context)
    {
        try
        {
            TBLog.Info($"DumpRuntimeCitiesState [{context}]: Cities count = {TravelButton.Cities?.Count ?? 0}");
            if (TravelButton.Cities != null)
            {
                foreach (var c in TravelButton.Cities)
                {
                    try
                    {
                        // Try to read variants and lastKnownVariant via reflection
                        string variantsStr = "null";
                        string lastKnownVariantStr = "null";
                        
                        try
                        {
                            var ct = c.GetType();
                            var variantsProp = ct.GetProperty("variants", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (variantsProp != null)
                            {
                                var variantsVal = variantsProp.GetValue(c);
                                if (variantsVal is string[] vArr)
                                    variantsStr = $"[{string.Join(", ", vArr)}]";
                            }

                            var lastKnownVariantProp = ct.GetProperty("lastKnownVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (lastKnownVariantProp != null)
                            {
                                var lastKnownVariantVal = lastKnownVariantProp.GetValue(c) as string;
                                lastKnownVariantStr = lastKnownVariantVal ?? "null";
                            }
                        }
                        catch { }

                        TBLog.Info($"  - '{c.name}' scene='{c.sceneName ?? ""}' coords=[{(c.coords != null ? string.Join(", ", c.coords) : "")}] variants={variantsStr} lastKnownVariant={lastKnownVariantStr}");
                    }
                    catch (Exception exCity)
                    {
                        TBLog.Warn($"DumpRuntimeCitiesState: failed to dump city: {exCity.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"DumpRuntimeCitiesState [{context}]: failed: {ex}");
        }
    }

    // Add OnDestroy to clean up the watcher and any resources:
    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        try
        {
            StopConfigWatcher();

            // other cleanup if necessary (e.g., persist, unsubscribe)
        }
        catch (Exception ex)
        {
            TBLog.Warn("OnDestroy cleanup failed: " + ex);
        }
    }

    // handler
    // OnSceneLoaded + deferred detection helper (place in your TravelButton plugin class file)
    private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string sceneName = scene.name ?? "";
            if (string.IsNullOrEmpty(sceneName)) return;

            TBLog.Info($"OnSceneLoaded START: scene='{sceneName}' isLoaded={scene.isLoaded} rootCount={scene.rootCount} mode={mode}");

            // Log active scene info (diagnostic)
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                LogActiveSceneInfo();
                sw.Stop();
                TBLog.Info($"OnSceneLoaded: LogActiveSceneInfo completed in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception exLog)
            {
                TBLog.Warn("OnSceneLoaded: LogActiveSceneInfo failed: " + exLog.Message);
            }

            // Gather best-effort player position
            UnityEngine.Vector3? playerPos = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                playerPos = TravelButton.GetPlayerPositionInScene();
                sw.Stop();
                TBLog.Info($"OnSceneLoaded: GetPlayerPositionInScene -> {(playerPos.HasValue ? playerPos.Value.ToString("F3") : "<null>")} (took {sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception exPos)
            {
                TBLog.Warn("OnSceneLoaded: GetPlayerPositionInScene failed: " + exPos.Message);
            }

            // Best-effort detect a target GameObject name for the scene
            string detectedTarget = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                detectedTarget = TravelButton.DetectTargetGameObjectName(sceneName);
                sw.Stop();
                TBLog.Info($"OnSceneLoaded: DetectTargetGameObjectName -> '{detectedTarget ?? "<null>"}' (took {sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception exDetect)
            {
                TBLog.Warn("OnSceneLoaded: DetectTargetGameObjectName failed: " + exDetect.Message);
            }

            // Optional description (keep null unless you have a source)
            string sceneDesc = null;

            // Record discovered scene into canonical JSON (safe, idempotent)
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                TravelButton.StoreVisitedSceneToJson(sceneName, playerPos, detectedTarget, sceneDesc);
                sw.Stop();
                TBLog.Info($"OnSceneLoaded: StoreVisitedSceneToJson completed (took {sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception exStore)
            {
                TBLog.Warn("OnSceneLoaded: StoreVisitedSceneToJson failed: " + exStore.Message);
            }

            // Existing visit marking logic
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                MarkCityVisitedByScene(sceneName);
                sw.Stop();
                TBLog.Info($"OnSceneLoaded: MarkCityVisitedByScene completed (took {sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception exMark)
            {
                TBLog.Warn("OnSceneLoaded: MarkCityVisitedByScene failed: " + exMark.Message);
            }

            // --- replace the existing derive/persist try-block in OnSceneLoaded with this ---
            // derive/persist block — replace existing block with this
            try
            {
                string preferredToken = sceneName;
                try { preferredToken = TravelButtonUI.GetBaseTokenFromSceneName(sceneName) ?? sceneName; } catch { }

                string derivedNormal = null;
                string derivedDestroyed = null;
                try
                {
                    ExtraSceneStateSetter.DeriveNormalDestroyedNamesFromScene(scene, preferredToken, out derivedNormal, out derivedDestroyed);
                    TBLog.Info($"OnSceneLoaded: Derived variants (preferred='{preferredToken}'): normal='{derivedNormal}', destroyed='{derivedDestroyed}'");
                }
                catch (Exception exDerive)
                {
                    TBLog.Warn("OnSceneLoaded: DeriveNormalDestroyedNamesFromScene failed: " + exDerive);
                }

                // Build variants list (order-preserving; normal first if available)
                var variants = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(derivedNormal) && !variants.Contains(derivedNormal)) variants.Add(derivedNormal);
                if (!string.IsNullOrEmpty(derivedDestroyed) && !variants.Contains(derivedDestroyed)) variants.Add(derivedDestroyed);

                // determine lastKnown as the concrete detected variant name
                string lastKnown = null;
                if (!string.IsNullOrEmpty(derivedNormal)) lastKnown = derivedNormal;
                else if (!string.IsNullOrEmpty(derivedDestroyed)) lastKnown = derivedDestroyed;

                // Check if runtime city entry already has coords (so we can persist fully now)
                bool hasCoords = false;
                try
                {
                    var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
                    if (citiesEnum != null)
                    {
                        foreach (var c in citiesEnum)
                        {
                            if (c == null) continue;
                            var t = c.GetType();
                            string sceneProp = null;
                            try { sceneProp = t.GetProperty("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                            if (string.IsNullOrEmpty(sceneProp))
                            {
                                try { sceneProp = t.GetField("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                            }
                            if (string.IsNullOrEmpty(sceneProp) || !string.Equals(sceneProp, sceneName, StringComparison.OrdinalIgnoreCase)) continue;

                            // try to read coords
                            try
                            {
                                var coordsProp = t.GetProperty("coords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                                var coordsField = t.GetField("coords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase);
                                var coordsVal = coordsProp != null ? coordsProp.GetValue(c) : coordsField != null ? coordsField.GetValue(c) : null;
                                if (coordsVal != null)
                                {
                                    // check if coords enumerable yields at least one numeric value
                                    var en = coordsVal as System.Collections.IEnumerable;
                                    if (en != null)
                                    {
                                        int cnt = 0;
                                        foreach (var _ in en) { cnt++; if (cnt > 0) break; }
                                        if (cnt > 0) hasCoords = true;
                                    }
                                }
                            }
                            catch { /* ignore */ }
                            break;
                        }
                    }
                }
                catch (Exception exDetectCoords)
                {
                    TBLog.Warn("OnSceneLoaded: runtime coords detection failed: " + exDetectCoords);
                }

                // If coords exist, persist now (will write full entry).
                // If coords don't exist, schedule a deferred detection/persist (DelayedVariantDetect) so the entry can be enriched later.
                try
                {
                    if (hasCoords)
                    {
                        bool persisted = CitiesJsonManager.TryUpdateAndPersist(sceneName, variants, lastKnown, ExtraSceneVariantDetection.VariantConfidence.High);
                        TBLog.Info($"OnSceneLoaded: CitiesJsonManagerCompat.TryUpdateAndPersist returned {persisted} for scene '{sceneName}' (immediate; coords present)");
                    }
                    else
                    {
                        // Optional: record the variant names immediately (may write a minimal entry),
                        // but prefer scheduling DelayedVariantDetect to enrich later.
                        TBLog.Info($"OnSceneLoaded: coords not available yet for '{sceneName}' — scheduling deferred detection/persist.");
                        // start DelayedVariantDetect to attempt more robust detection and persisting later.
                        try
                        {
                            // ensure you have a coroutine runner helper (TravelButtonRunner or similar)
                            TravelButtonRunner.Instance?.StartSafeCoroutine(DelayedVariantDetect(scene, 0.08f));
                        }
                        catch (Exception exStart) { TBLog.Warn("OnSceneLoaded: failed to start DelayedVariantDetect: " + exStart); }

                        // Optionally also call TryUpdateAndPersist now to capture variants (compat has retry-enrich logic).
                        // CitiesJsonManagerCompat.TryUpdateAndPersist(sceneName, variants, lastKnown, ExtraSceneVariantDetection.VariantConfidence.Medium);
                    }
                }
                catch (Exception exCompat)
                {
                    TBLog.Warn("OnSceneLoaded: CitiesJsonManagerCompat.TryUpdateAndPersist threw: " + exCompat);
                }
            }
            catch (Exception exOuterApply)
            {
                TBLog.Warn("OnSceneLoaded: variant derive/update block failed: " + exOuterApply);
            }

            totalSw.Stop();
            TBLog.Info($"OnSceneLoaded END: scene='{sceneName}' totalElapsed={totalSw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            TBLog.Warn("OnSceneLoaded: " + ex.Message);
        }
    }

    private static System.Collections.IEnumerator DelayedVariantDetect(UnityEngine.SceneManagement.Scene scene, float waitSeconds = 0.08f)
    {
        // wait a frame so Unity finishes Awake/Start for scene objects
        yield return null;
        if (waitSeconds > 0f) yield return new UnityEngine.WaitForSecondsRealtime(waitSeconds);

        TBLog.Info($"DelayedVariantDetect: running detection for scene '{scene.name}'");

        // -----------------------
        // 1) Try SceneVariantProvider with multiple tokens + retry
        // -----------------------
        {
            TBLog.Info($"DelayedVariantDetect: attempting SceneVariantProvider for scene '{scene.name}' using multiple tokens (with retries)");

            // Build candidate tokens (order matters)
            var tryTokens = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(scene.name)) tryTokens.Add(scene.name);

            // common suffix stripping
            string[] suffixes = new[] { "NewTerrain", "Terrain", "Map" };
            foreach (var sfx in suffixes)
            {
                if (!string.IsNullOrEmpty(scene.name) && scene.name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = scene.name.Substring(0, scene.name.Length - sfx.Length);
                    if (!string.IsNullOrEmpty(trimmed) && !tryTokens.Exists(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
                        tryTokens.Add(trimmed);
                }
            }

            // try to find matching city entry and add city.name and parts of targetGameObjectName
            try
            {
                var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
                if (citiesEnum != null)
                {
                    foreach (var c in citiesEnum)
                    {
                        try
                        {
                            var t = c.GetType();
                            string citySceneName = null;
                            try { citySceneName = t.GetProperty("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                            if (string.IsNullOrEmpty(citySceneName))
                            {
                                try { citySceneName = t.GetField("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                            }

                            // If the city entry matches the currently loaded sceneName, collect candidate tokens from that entry.
                            if (!string.IsNullOrEmpty(citySceneName) && string.Equals(citySceneName, scene.name, StringComparison.OrdinalIgnoreCase))
                            {
                                // city.name
                                string cityName = null;
                                try { cityName = t.GetProperty("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                                if (!string.IsNullOrEmpty(cityName) && !tryTokens.Exists(x => string.Equals(x, cityName, StringComparison.OrdinalIgnoreCase)))
                                    tryTokens.Add(citName);

                                // targetGameObjectName (may contain BGM_TownCierzo(Clone) etc)
                                string targetName = null;
                                try { targetName = t.GetProperty("targetGameObjectName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                                if (string.IsNullOrEmpty(targetName))
                                {
                                    try { targetName = t.GetField("targetGameObjectName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                                }
                                if (!string.IsNullOrEmpty(targetName))
                                {
                                    // Try to extract a core alpha token (e.g., "Cierzo" from "BGM_TownCierzo(Clone)")
                                    var m = System.Text.RegularExpressions.Regex.Match(targetName, @"([A-Za-z]{3,})");
                                    if (m.Success)
                                    {
                                        var cleaned = m.Groups[1].Value;
                                        if (!tryTokens.Exists(x => string.Equals(x, cleaned, StringComparison.OrdinalIgnoreCase)))
                                            tryTokens.Add(cleaned);
                                    }

                                    // Also split on non-alphanumerics and add each reasonable part
                                    var parts = System.Text.RegularExpressions.Regex.Split(targetName, @"[^A-Za-z0-9]+");
                                    foreach (var p in parts)
                                        if (!string.IsNullOrEmpty(p) && p.Length >= 3 && !tryTokens.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                                            tryTokens.Add(p);
                                }

                                // Found matching city entry -> stop enumerating further city entries
                                break;
                            }
                        }
                        catch { /* ignore per-city reflection errors */ }
                    }
                }
            }
            catch { /* ignore city enumeration errors */ }

            // fallback: capitalized tokens from scene name
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(scene.name ?? "", @"[A-Z][a-z]{2,}"))
            {
                var token = m.Value;
                if (!string.IsNullOrEmpty(token) && !tryTokens.Exists(x => string.Equals(x, token, StringComparison.OrdinalIgnoreCase)))
                    tryTokens.Add(token);
            }

            // Retry loop - yields are outside the try blocks that have catches
            const int maxAttempts = 5;
            const float attemptDelay = 0.20f;
            bool providerFound = false;

            for (int attempt = 1; attempt <= maxAttempts && !providerFound; attempt++)
            {
                TBLog.Info($"DelayedVariantDetect: SceneVariantProvider attempt {attempt}/{maxAttempts} for scene '{scene.name}'");

                // Inner provider try/catch - does NOT contain any yield
                try
                {
                    // Optional diagnostic call if available (wrap in try to avoid crashing)
                    try
                    {
                        SceneVariantProvider.DumpBlackboardsDiagnostics(scene.name);
                    }
                    catch { /* ignore diag errors */ }

                    foreach (var tok in tryTokens)
                    {
                        if (string.IsNullOrEmpty(tok)) continue;
                        TBLog.Info($"DelayedVariantDetect: SceneVariantProvider trying token '{tok}' (attempt {attempt})");
                        var (rawVar, normalized) = SceneVariantProvider.GetActiveVariantForScene(tok);
                        if (!string.IsNullOrEmpty(rawVar))
                        {
                            TBLog.Info($"DelayedVariantDetect: SceneVariantProvider returned raw='{rawVar}', normalized='{normalized}' for token='{tok}' - persisting and skipping heavy detection.");
                            try
                            {
                                // Build variants list and choose concrete lastKnown
                                var variants = new System.Collections.Generic.List<string>();
                                if (!string.IsNullOrEmpty(rawVar) && !variants.Contains(rawVar)) variants.Add(rawVar);
                                if (!string.IsNullOrEmpty(normalized) && !variants.Contains(normalized)) variants.Add(normalized);
                                string lastKnown = !string.IsNullOrEmpty(normalized) ? normalized : rawVar;

                                bool providerPersisted = CitiesJsonManager.TryUpdateAndPersist(scene.name, variants, lastKnown, ExtraSceneVariantDetection.VariantConfidence.High);
                                TBLog.Info($"DelayedVariantDetect: SceneVariantProvider compat persist returned {providerPersisted}");
                            }
                            catch (Exception exProvPersist)
                            {
                                TBLog.Warn("DelayedVariantDetect: SceneVariantProvider compat persist threw: " + exProvPersist.Message);
                            }
                            providerFound = true;
                            break;
                        }
                    }
                }
                catch (Exception exInner)
                {
                    TBLog.Warn("DelayedVariantDetect: SceneVariantProvider inner attempt threw: " + exInner.Message);
                }

                if (providerFound) break;

                // wait before next attempt (yield is here but not inside a try with catch)
                if (attempt < maxAttempts)
                    yield return new UnityEngine.WaitForSecondsRealtime(attemptDelay);
            }

            if (providerFound)
            {
                yield break; // already persisted by provider
            }

            TBLog.Info("DelayedVariantDetect: SceneVariantProvider did not find an active variant after retries; continuing to fallback detection.");
        }

        // -----------------------
        // 2) Quick scene-scan fallback: dynamic tokenCandidates generated from TravelButton.Cities
        // -----------------------
        {
            // Build tokenCandidates dynamically from TravelButton.Cities and the scene name.
            var tokenSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(scene.name)) tokenSet.Add(scene.name);

            // Add variants of the scene name (strip common suffixes)
            string[] suffixes = new[] { "NewTerrain", "Terrain", "Map" };
            foreach (var sfx in suffixes)
            {
                if (!string.IsNullOrEmpty(scene.name) && scene.name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = scene.name.Substring(0, scene.name.Length - sfx.Length);
                    if (!string.IsNullOrEmpty(trimmed)) tokenSet.Add(trimmed);
                }
            }

            // Add tokens from runtime city entries (name, sceneName, targetGameObjectName parts)
            try
            {
                var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
                if (citiesEnum != null)
                {
                    foreach (var c in citiesEnum)
                    {
                        try
                        {
                            var t = c.GetType();

                            // add city.name if present
                            string cityName = null;
                            try { cityName = t.GetProperty("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                            if (!string.IsNullOrEmpty(cityName)) tokenSet.Add(cityName);

                            // add sceneName if present
                            string citySceneName = null;
                            try { citySceneName = t.GetProperty("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                            if (!string.IsNullOrEmpty(citySceneName)) tokenSet.Add(citySceneName);

                            // add targetGameObjectName-derived tokens
                            string targetName = null;
                            try { targetName = t.GetProperty("targetGameObjectName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c, null) as string; } catch { }
                            if (string.IsNullOrEmpty(targetName))
                            {
                                try { targetName = t.GetField("targetGameObjectName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(c) as string; } catch { }
                            }
                            if (!string.IsNullOrEmpty(targetName))
                            {
                                // try a simple alpha token extraction and splitting
                                var m = System.Text.RegularExpressions.Regex.Match(targetName, @"([A-Za-z]{3,})");
                                if (m.Success) tokenSet.Add(m.Groups[1].Value);

                                var parts = System.Text.RegularExpressions.Regex.Split(targetName, @"[^A-Za-z0-9]+");
                                foreach (var p in parts)
                                    if (!string.IsNullOrEmpty(p) && p.Length >= 3)
                                        tokenSet.Add(p);
                            }
                        }
                        catch { /* ignore per-city reflection errors */ }
                    }
                }
            }
            catch { /* ignore enumeration errors */ }

            // Add capitalized tokens from the scene name
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(scene.name ?? "", @"[A-Z][a-z]{2,}"))
            {
                tokenSet.Add(m.Value);
            }

            // Convert to list to preserve iteration behavior
            var tokenCandidates = new System.Collections.Generic.List<string>(tokenSet);

            TBLog.Info($"DelayedVariantDetect: scene-scan will try {tokenCandidates.Count} dynamic tokens for scene '{scene.name}'");

            // For each candidate token, try to find common name patterns or a broader name match on GameObjects
            foreach (var tok in tokenCandidates)
            {
                if (string.IsNullOrEmpty(tok)) continue;

                // patterns (explicit names)
                var patterns = new[] {
                $"Normal{tok}", $"{tok}Normal", $"Destroyed{tok}", $"{tok}Destroyed",
                $"Normal_{tok}", $"{tok}_Normal", $"Destroyed_{tok}", $"{tok}_Destroyed"
            };

                foreach (var pat in patterns)
                {
                    var go = UnityEngine.GameObject.Find(pat);
                    if (go != null && go.activeInHierarchy)
                    {
                        // Persist as high-confidence and short-circuit
                        TBLog.Info($"DelayedVariantDetect: scene-scan found GameObject '{pat}' active -> persisting variant (token='{tok}')");
                        try
                        {
                            var variants = new System.Collections.Generic.List<string>();
                            // use the GO name as a concrete variant
                            variants.Add(pat);
                            // also add token-based guesses
                            if (!variants.Contains($"{tok}Normal")) variants.Add($"{tok}Normal");
                            if (!variants.Contains($"{tok}Destroyed")) variants.Add($"{tok}Destroyed");

                            string lastKnown = pat; // concrete name
                            CitiesJsonManager.TryUpdateAndPersist(scene.name, variants, lastKnown, ExtraSceneVariantDetection.VariantConfidence.High);
                        }
                        catch (Exception exPersist)
                        {
                            TBLog.Warn("DelayedVariantDetect: scene-scan persist threw: " + exPersist.Message);
                        }
                        yield break;
                    }
                }

                // broader scan: any GO whose name contains tok and Normal/Destroyed
                var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.GameObject>();
                foreach (var g in all)
                {
                    if (!g.activeInHierarchy) continue;
                    var n = g.name ?? "";
                    if (n.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (n.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        var which = n.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 ? "Normal" : "Destroyed";
                        TBLog.Info($"DelayedVariantDetect: broad scene-scan found '{g.name}' -> persisting variant {which} (token='{tok}')");
                        try
                        {
                            var variants = new System.Collections.Generic.List<string>();
                            variants.Add(n); // concrete
                                             // add probable alternatives
                            if (!variants.Contains($"{tok}Normal")) variants.Add($"{tok}Normal");
                            if (!variants.Contains($"{tok}Destroyed")) variants.Add($"{tok}Destroyed");

                            CitiesJsonManager.TryUpdateAndPersist(scene.name, variants, n, ExtraSceneVariantDetection.VariantConfidence.High);
                        }
                        catch (Exception exPersist)
                        {
                            TBLog.Warn("DelayedVariantDetect: scene-scan broad persist threw: " + exPersist.Message);
                        }
                        yield break;
                    }
                }
            }
        }

        // -----------------------
        // 3) Fallback: existing heuristic detection (slower)
        // -----------------------
        var detectSw = System.Diagnostics.Stopwatch.StartNew();
        var (normal, destroyed, confidence) = ExtraSceneVariantDetection.DetectVariantNamesWithConfidence(scene, scene.name);
        detectSw.Stop();
        TBLog.Info($"DelayedVariantDetect: DetectVariantNamesWithConfidence -> normal='{normal ?? ""}' destroyed='{destroyed ?? ""}' confidence={confidence} (took {detectSw.ElapsedMilliseconds} ms)");

        // diagnostics dump (safe)
        try
        {
            var fast = VariantDetectDiagnostics.DumpDiagnostics(scene);
            TBLog.Info($"[VariantDetectDiag] DumpDiagnostics fast -> normal='{fast.normalName ?? ""}' destroyed='{fast.destroyedName ?? ""}' confidence={fast.confidence}");
        }
        catch (Exception exDump)
        {
            TBLog.Warn("[VariantDetectDiag] DumpDiagnostics threw: " + exDump);
        }

        if (confidence < ExtraSceneVariantDetection.VariantConfidence.Medium)
        {
            try
            {
                TBLog.Info("DelayedVariantDetect: confidence low — running DetectAndDump for offline diagnostics.");
                var diag = ExtraSceneVariantDiagnostics.DetectAndDump(scene);
                TBLog.Info("DelayedVariantDetect: DetectAndDump returned: " + diag);
            }
            catch (Exception exDiag)
            {
                TBLog.Warn("DelayedVariantDetect: DetectAndDump failed: " + exDiag.Message);
            }
        }

        // Persist detection (no yields inside try)
        bool persisted = false;
        try
        {
            var persistSw = System.Diagnostics.Stopwatch.StartNew();
            var variants = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(normal) && !variants.Contains(normal)) variants.Add(normal);
            if (!string.IsNullOrEmpty(destroyed) && !variants.Contains(destroyed)) variants.Add(destroyed);

            var finalVariantConcrete = !string.IsNullOrEmpty(normal) ? normal : (!string.IsNullOrEmpty(destroyed) ? destroyed : null);

            persisted = CitiesJsonManager.TryUpdateAndPersist(scene.name, variants, finalVariantConcrete, confidence);
            persistSw.Stop();
            TBLog.Info($"DelayedVariantDetect: CitiesJsonManagerCompat.TryUpdateAndPersist returned {persisted} (took {persistSw.ElapsedMilliseconds} ms)");
        }
        catch (Exception exPersist)
        {
            TBLog.Warn("DelayedVariantDetect: UpdateCityVariantData threw exception: " + exPersist.Message);
        }

        if (!persisted)
        {
            TBLog.Info($"DelayedVariantDetect: Persist failed - CitiesJsonPath='{TravelButtonPlugin.GetCitiesJsonPath() ?? "<null>"}'");
            try
            {
                var path = TravelButtonPlugin.GetCitiesJsonPath();
                if (!string.IsNullOrEmpty(path))
                {
                    var fi = new System.IO.FileInfo(path);
                    TBLog.Info($"DelayedVariantDetect: CitiesJson file exists={fi.Exists}, length={(fi.Exists ? fi.Length.ToString() : "N/A")}, readonly={(fi.Exists ? fi.IsReadOnly.ToString() : "N/A")}");
                    var dir = System.IO.Path.GetDirectoryName(path);
                    var tmp = System.IO.Path.Combine(dir ?? System.IO.Path.GetTempPath(), $"TravelButton_write_test_{Guid.NewGuid():N}.tmp");
                    System.IO.File.WriteAllText(tmp, "ping");
                    System.IO.File.Delete(tmp);
                    TBLog.Info($"DelayedVariantDetect: Temp write to dir '{dir}' succeeded");
                }
            }
            catch (Exception exDiag)
            {
                TBLog.Warn("DelayedVariantDetect: Extra persist diagnostics failed: " + exDiag.Message);
            }
        }

        TBLog.Info($"DelayedVariantDetect: finished for scene '{scene.name}' persisted={persisted}");
    }

    // Fallback inline detection if scheduling coroutine isn't possible. Keeps same detection+persist steps.
    // Call only as a fallback from OnSceneLoaded.
    private static void RunVariantDetectionInline(UnityEngine.SceneManagement.Scene scene, string sceneName)
    {
        try
        {
            var (normal, destroyed, confidence) = ExtraSceneVariantDetection.DetectVariantNamesWithConfidence(scene, sceneName);
            TBLog.Info($"RunVariantDetectionInline: detected normal='{normal ?? ""}' destroyed='{destroyed ?? ""}' confidence={confidence}");
            string finalVariantStr = "Unknown";
            if (confidence >= ExtraSceneVariantDetection.VariantConfidence.Medium)
            {
                try
                {
                    finalVariantStr = ExtraSceneVariantDiagnostics.DetectAndDump(scene).ToString();
                }
                catch (Exception exDiag)
                {
                    TBLog.Warn("RunVariantDetectionInline: DetectAndDump failed: " + exDiag.Message);
                }
            }

            bool persisted = CitiesJsonManager.UpdateCityVariantData(sceneName, normal, destroyed, finalVariantStr, confidence);
            TBLog.Info($"RunVariantDetectionInline: UpdateCityVariantData returned {persisted}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("RunVariantDetectionInline: " + ex.Message);
        }
    }

    // New wrapper coroutine for DetectAndPersistCoordsCoroutine.
    // This allows TravelButtonUI (or other components) to call the logic on the plugin instance.
    public IEnumerator DetectAndPersistVariantsForCityCoroutine(TravelButton.City city, float initialDelay, float scanDurationSeconds)
    {
        // ... (implementation of DetectAndPersistVariantsForCityCoroutine here, likely empty or delegating to the static/private logic if you moved it) ...
        // Since the user asked for "how scene loads... from fix branch", and the fix branch likely had this method implemented inside TravelButtonPlugin (or TravelButton.cs),
        // we should ensure the logic is present.
        // Based on the reference files provided (TravelButton_ref.cs), it seems TravelButtonPlugin handles this.
        // However, the `DetectAndPersistVariantsForCityCoroutine` logic was present in `TravelButtonUI_ref.cs` (as a static helper wrapper) but implemented fully in `TravelButton.cs` (as `DelayedVariantDetect`).
        // Wait, looking at `TravelButtonUI_ref.cs` again, it contains `DetectAndPersistVariantsForCityCoroutine` implementation at the end!
        // No, `TravelButtonUI_ref.cs` calls `plugin.DetectAndPersistVariantsForCityCoroutine`.
        // So `TravelButtonPlugin` MUST implement `DetectAndPersistVariantsForCityCoroutine`.

        // I will implement the logic here, adapting it to use the new `DelayedVariantDetect` structure or similar.
        // Actually, looking at the previous file content I pasted (`TravelButton.cs`), `DelayedVariantDetect` *is* the logic.
        // But `DetectAndPersistVariantsForCityCoroutine` signature is what `TravelButtonUI` calls.
        // I should add a wrapper.

        yield return DelayedVariantDetect(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), initialDelay);
    }

}

public class TravelButtonRunner : MonoBehaviour
{
    static TravelButtonRunner _instance;
    public static TravelButtonRunner Instance
    {
        get
        {
            if (_instance != null) return _instance;
            var go = new GameObject("TravelButtonRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<TravelButtonRunner>();
            return _instance;
        }
    }

    // convenience wrapper
    public Coroutine StartSafeCoroutine(IEnumerator coro) => StartCoroutine(coro);
}