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
            TravelButtonPlugin.LogError("TravelButtonPlugin.Awake: InitializeCitiesAndConfig failed: " + ex);
        }

        ShowPlayerNotification = (msg) =>
        {
            TravelButtonNotificationUI.Show(msg, 3f);
        };

        cfgUseTransitionScene = Config.Bind("Travel", "UseTransitionScene", true, "Load LowMemory_TransitionScene before the real target to force engine re-init.");
    }

    /// <summary>
    /// Consolidated initialization flow that:
    /// 1. CityMappingHelpers.InitCities() (diagnostic only)
    /// 2. TryLoadCitiesJsonIntoTravelButtonMod() (map JSON into runtime with variants/lastKnownVariant)
    /// 3. TravelButton.InitFromConfig() (attempt external config)
    /// 4. CityMappingHelpers.EnsureCitiesInitializedFromJsonOrDefaults() (final ensure & persist-if-missing)
    /// 5. EnsureBepInExConfigBindings() (create BepInEx bindings with SettingChanged handlers that WRITE to files only)
    /// 6. StartConfigWatcher() (watch legacy cfg)
    /// 7. Start TryInitConfigCoroutine() as before (retrier)
    /// </summary>
    private void InitializeCitiesAndConfig()
    {
        TBLog.Info("InitializeCitiesAndConfig: BEGIN consolidated initialization.");

        try
        {
            // 1) Prepare any internal city mapping helpers (build any runtime lookup tables)
            CityMappingHelpers.InitCities();
            TBLog.Info("InitializeCitiesAndConfig: CityMappingHelpers.InitCities() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: CityMappingHelpers.InitCities() failed: " + ex);
        }

        try
        {
            // 2) Load JSON city definitions first and merge into runtime list
            if (TryLoadCitiesJsonIntoTravelButtonMod())
            {
                TBLog.Info("InitializeCitiesAndConfig: TryLoadCitiesJsonIntoTravelButtonMod() completed and parsed/merged JSON.");
            }
            else
            {
                TBLog.Info("InitializeCitiesAndConfig: TryLoadCitiesJsonIntoTravelButtonMod() completed with no JSON loaded.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: TryLoadCitiesJsonIntoTravelButtonMod failed: " + ex);
        }

        try
        {
            // 3) Read legacy/ConfigManager defaults into mapped objects (will be merged, not overwrite)
            TravelButton.InitFromConfig();
            TBLog.Info("InitializeCitiesAndConfig: TravelButton.InitFromConfig() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: TravelButton.InitFromConfig() failed: " + ex);
        }

        try
        {
            // 4) Ensure every default city exists and only fill missing fields (scene/coords/target) — won't overwrite JSON fields
            CityMappingHelpers.EnsureCitiesInitializedFromJsonOrDefaults();
            TBLog.Info("InitializeCitiesAndConfig: CityMappingHelpers.EnsureCitiesInitializedFromJsonOrDefaults() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: EnsureCitiesInitializedFromJsonOrDefaults failed: " + ex);
        }

        try
        {
            // 5) Create/apply BepInEx config bindings for all cities and apply any cfg overrides (price/enabled/visited) into memory
            EnsureBepInExConfigBindings();
            TBLog.Info("InitializeCitiesAndConfig: EnsureBepInExConfigBindings() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: EnsureBepInExConfigBindings failed: " + ex);
        }

        try
        {
            // 6) Start watcher/watchers for legacy cfg changes, etc.
            StartConfigWatcher();
            TBLog.Info("InitializeCitiesAndConfig: StartConfigWatcher() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: StartConfigWatcher() failed: " + ex);
        }

        try
        {
            // 7) Start coroutine that attempts to fully initialize config (if needed)
            StartCoroutine(TryInitConfigCoroutine());
            TBLog.Info("InitializeCitiesAndConfig: Started TryInitConfigCoroutine().");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: Failed to start TryInitConfigCoroutine(): " + ex);
        }

        TBLog.Info("InitializeCitiesAndConfig: END consolidated initialization.");
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
    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        var swTotal = TBPerf.StartTimer();
        try
        {
            string sceneName = scene.name ?? "";
            if (string.IsNullOrEmpty(sceneName))
            {
                TBPerf.Log($"OnSceneLoaded:Total:<empty>", swTotal, "");
                return;
            }

            // Log active scene info (diagnostic)
            try
            {
                var sw = TBPerf.StartTimer();
                LogActiveSceneInfo();
                TBPerf.Log($"OnSceneLoaded:LogActiveSceneInfo:{sceneName}", sw, "");
            }
            catch (Exception exLog)
            {
                TBLog.Warn("OnSceneLoaded: LogActiveSceneInfo failed: " + exLog.Message);
            }

            // Gather best-effort player position
            UnityEngine.Vector3? playerPos = null;
            try
            {
                var sw = TBPerf.StartTimer();
                playerPos = TravelButton.GetPlayerPositionInScene();
                TBPerf.Log($"OnSceneLoaded:GetPlayerPositionInScene:{sceneName}", sw, $"pos={(playerPos.HasValue ? playerPos.Value.ToString("F3") : "<null>")}");
            }
            catch (Exception exPos)
            {
                TBLog.Warn("OnSceneLoaded: GetPlayerPositionInScene failed: " + exPos.Message);
            }

            // Best-effort detect a target GameObject name for the scene
            string detectedTarget = null;
            try
            {
                var sw = TBPerf.StartTimer();
                detectedTarget = TravelButton.DetectTargetGameObjectName(sceneName);
                TBPerf.Log($"OnSceneLoaded:DetectTargetGameObjectName:{sceneName}", sw, $"detected={(string.IsNullOrEmpty(detectedTarget) ? "<none>" : detectedTarget)}");
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
                var sw = TBPerf.StartTimer();
                StoreVisitedSceneToJson(sceneName, playerPos, detectedTarget, sceneDesc);
                TBPerf.Log($"OnSceneLoaded:StoreVisitedSceneToJson:{sceneName}", sw, "");
            }
            catch (Exception exStore)
            {
                TBLog.Warn("OnSceneLoaded: StoreVisitedSceneToJson failed: " + exStore.Message);
            }

            // Existing visit marking logic
            try
            {
                var sw = TBPerf.StartTimer();
                MarkCityVisitedByScene(sceneName);
                TBPerf.Log($"OnSceneLoaded:MarkCityVisitedByScene:{sceneName}", sw, "");
            }
            catch (Exception exMark)
            {
                TBLog.Warn("OnSceneLoaded: MarkCityVisitedByScene failed: " + exMark.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("OnSceneLoaded: " + ex.Message);
        }
        finally
        {
            try
            {
                TBPerf.Log($"OnSceneLoaded:Total:{scene.name}", swTotal, "");
            }
            catch { /* swallow any logging errors */ }
        }
    }

    // Add these snippets into src/TravelButton.cs in the TravelButton class.
    // 1) Add a plugin fallback set (near other static fields)
    private static System.Collections.Generic.HashSet<string> s_pluginVisitedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    // 2) If City does not already have a persisted visited field, add it to the City definition:
    // Insert this inside the City class (near other serializable fields)
    public bool visited = false; // persisted visited flag (default false)

    private static bool PersistCitiesJsonSafely(string jsonPath, JObject rootFallback = null)
    {
        try
        {
            var dto = JsonTravelConfig.Default();
            int dtoCount = dto?.cities?.Count ?? 0;
            TBLog.Info($"PersistCitiesJsonSafely: JsonTravelConfig.Default produced {dtoCount} entries.");

            if (dtoCount == 0)
            {
                TBLog.Warn("PersistCitiesJsonSafely: DTO has 0 entries; attempting fallback root if provided.");
                if (rootFallback != null)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(jsonPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        File.WriteAllText(jsonPath, rootFallback.ToString(Formatting.Indented), System.Text.Encoding.UTF8);
                        TBLog.Info($"PersistCitiesJsonSafely: wrote fallback JSON root to: {jsonPath}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("PersistCitiesJsonSafely: failed writing fallback root: " + ex);
                        return false;
                    }
                }

                TBLog.Warn("PersistCitiesJsonSafely: no fallback provided — skipping write to avoid empty JSON.");
                return false;
            }

            // Merge runtime visited flags into DTO by name (case-insensitive)
            try
            {
                var map = new Dictionary<string, JsonCityConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var jc in dto.cities)
                    if (jc?.name != null) map[jc.name] = jc;

                var citiesEnum = TravelButton.Cities as System.Collections.IEnumerable;
                if (citiesEnum != null)
                {
                    foreach (var c in citiesEnum)
                    {
                        if (c == null) continue;
                        string name = null;
                        bool? visited = null;
                        try
                        {
                            var t = c.GetType();
                            var pName = t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (pName != null) name = pName.GetValue(c) as string;
                            else
                            {
                                var fName = t.GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                if (fName != null) name = fName.GetValue(c) as string;
                            }

                            var pVisited = t.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                                           ?? t.GetProperty("Visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                            if (pVisited != null)
                            {
                                var v = pVisited.GetValue(c);
                                if (v is bool b) visited = b;
                            }
                            else
                            {
                                var fVisited = t.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase)
                                               ?? t.GetField("Visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                if (fVisited != null)
                                {
                                    var v = fVisited.GetValue(c);
                                    if (v is bool b2) visited = b2;
                                }
                            }
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(name) && visited.HasValue && map.TryGetValue(name, out var entry))
                            entry.visited = visited.Value;
                    }
                }
            }
            catch (Exception exMerge)
            {
                TBLog.Warn("PersistCitiesJsonSafely: merging visited flags failed: " + exMerge);
            }

            // Write DTO using SaveToJson (ensures cities array and header)
            try
            {
                dto.SaveToJson(jsonPath);
                TBLog.Info("PersistCitiesJsonSafely: wrote DTO to: " + jsonPath);
                return true;
            }
            catch (Exception exWrite)
            {
                TBLog.Warn("PersistCitiesJsonSafely: failed saving DTO: " + exWrite);
                return false;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PersistCitiesJsonSafely: unexpected error: " + ex);
            return false;
        }
    }

    // Public wrapper so other static classes can request deferred storage via coroutine
    public Coroutine StartWaitForPlayerPlacementAndStore(string sceneName, float maxWaitSeconds = 6f, float pollInterval = 0.5f)
    {
        try
        {
            return StartCoroutine(WaitForPlayerPlacementAndStore(sceneName, maxWaitSeconds, pollInterval));
        }
        catch (Exception ex)
        {
            TBLog.Warn("StartWaitForPlayerPlacementAndStore: failed to start coroutine: " + ex.Message);
            return null;
        }
    }

    // Coroutine: wait until a reliable player position is available or until timeout,
    // then call TravelButton.StoreVisitedSceneToJson with detected coords.
    private IEnumerator WaitForPlayerPlacementAndStore(string sceneName, float maxWaitSeconds = 6f, float pollInterval = 0.5f)
    {
        Vector3? acceptedPos = null;
        float elapsed = 0f;

        // small initial delay to allow scene objects to begin initializing
        yield return null;

        while (elapsed < maxWaitSeconds)
        {
            try
            {
                // Prefer the more thorough TryGetExactPlayerWorldPosition helper if available.
                // This method should return true when a reliable position is found.
                if (PlayerPositionExact.TryGetExactPlayerWorldPosition(out Vector3 exactPos))
                {
                    bool looksLikeSentinel =
                        Math.Abs(exactPos.x + 5000f) < 200f ||
                        Math.Abs(exactPos.y + 5000f) < 200f ||
                        Math.Abs(exactPos.z + 5000f) < 200f;

                    bool isZero = exactPos == Vector3.zero;

                    if (!looksLikeSentinel && !isZero)
                    {
                        acceptedPos = exactPos;
                        break;
                    }
                }
            }
            catch (Exception exTry)
            {
                TBLog.Warn("WaitForPlayerPlacementAndStore: TryGetExactPlayerWorldPosition threw: " + exTry.Message);
            }

            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;
        }

        // fallback: best-effort GetPlayerPositionInScene
        if (!acceptedPos.HasValue)
        {
            try { acceptedPos = TravelButton.GetPlayerPositionInScene(); } catch { acceptedPos = null; }
        }

        string detectedTarget = null;
        try { detectedTarget = TravelButton.DetectTargetGameObjectName(sceneName); } catch { detectedTarget = null; }

        try
        {
            StoreVisitedSceneToJson(sceneName, acceptedPos, detectedTarget, null);
        }
        catch (Exception exStore)
        {
            TBLog.Warn("WaitForPlayerPlacementAndStore: StoreVisitedSceneToJson failed: " + exStore.Message);
        }
    }


    /// <summary>
    /// Load TravelButton_Cities.json from candidate locations using TravelConfig.LoadFromFile.
    /// Creates a default file if missing/unparsable. Maps CityConfig entries into TravelButtonMod.City
    /// instances with metadata only (coords, targetGameObjectName, sceneName, desc). Sets price=null and
    /// enabled=false so EnsureBepInExConfigBindings will create BepInEx config bindings and populate runtime values.
    /// Deduplicates cities by case-insensitive name.
    /// </summary>
    // Replace the existing TryLoadCitiesJsonIntoTravelButtonMod method body with this corrected implementation.
    private bool TryLoadCitiesJsonIntoTravelButtonMod()
    {
        try
        {
            var path = GetCitiesJsonPath();
            if (!File.Exists(path))
            {
                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: no JSON found at '{path}'.");
                return false;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                TBLog.Warn($"TryLoadCitiesJsonIntoTravelButtonMod: file empty: '{path}'.");
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException jex)
            {
                TBLog.Warn("TryLoadCitiesJsonIntoTravelButtonMod: JSON parse failed: " + jex.Message);
                return false;
            }

            var jCities = root["cities"] as JArray;
            if (jCities == null)
            {
                TBLog.Warn($"TryLoadCitiesJsonIntoTravelButtonMod: JSON missing 'cities' array at '{path}'.");
                return false;
            }

            var parsed = new List<City>();
            foreach (var token in jCities.OfType<JObject>())
            {
                var name = token.Value<string>("name");
                if (string.IsNullOrEmpty(name)) continue;

                var city = new City(name);

                city.sceneName = token.Value<string>("sceneName") ?? city.sceneName;
                city.targetGameObjectName = token.Value<string>("targetGameObjectName") ?? city.targetGameObjectName;
                city.price = token["price"] != null ? (int?)token.Value<int?>("price") : city.price;
                city.visited = token.Value<bool?>("visited") ?? city.visited;

                // coords
                var coordsToken = token["coords"] as JArray;
                if (coordsToken != null && coordsToken.Count >= 3)
                {
                    try
                    {
                        city.coords = new float[3]
                        {
                        coordsToken[0].Value<float>(),
                        coordsToken[1].Value<float>(),
                        coordsToken[2].Value<float>()
                        };
                    }
                    catch { city.coords = null; }
                }

                // variants: prefer 'variants'; fallback to variantNormalName/variantDestroyedName if present
                var variantsToken = token["variants"] as JArray;
                if (variantsToken != null && variantsToken.Count > 0)
                {
                    city.variants = variantsToken.Select(v => (string)v).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                }
                else
                {
                    var normal = token.Value<string>("variantNormalName");
                    var destroyed = token.Value<string>("variantDestroyedName");
                    var vt = new List<string>();
                    if (!string.IsNullOrEmpty(normal)) vt.Add(normal);
                    if (!string.IsNullOrEmpty(destroyed)) vt.Add(destroyed);
                    city.variants = vt.ToArray(); // possibly empty
                }

                // lastKnownVariant (default to empty string to ensure key exists downstream)
                city.lastKnownVariant = token.Value<string>("lastKnownVariant") ?? "";

                parsed.Add(city);
                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: parsed city '{name}' (scene='{city.sceneName}', variantsCount={city.variants?.Length ?? 0}, lastKnownVariant='{city.lastKnownVariant}').");
            }

            // Merge parsed JSON entries into existing TravelButton.Cities (do not blindly overwrite)
            if (TravelButton.Cities == null || TravelButton.Cities.Count == 0)
            {
                TravelButton.Cities = parsed;
                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: TravelButton.Cities was empty — assigned parsed list ({parsed.Count} cities).");
            }
            else
            {
                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merging parsed JSON ({parsed.Count}) into existing TravelButton.Cities ({TravelButton.Cities.Count}).");
                // Build dictionary for case-insensitive lookup
                var existingDict = TravelButton.Cities.ToDictionary(c => (c.name ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

                foreach (var p in parsed)
                {
                    if (string.IsNullOrEmpty(p.name))
                        continue;

                    if (!existingDict.TryGetValue(p.name, out var existing))
                    {
                        // new city from JSON -> add
                        TravelButton.Cities.Add(p);
                        existingDict[p.name] = p;
                        TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: added JSON-only city '{p.name}'");
                    }
                    else
                    {
                        // merge: only overwrite fields that JSON explicitly provided (and prefer JSON when it is explicit),
                        // but avoid clobbering already set runtime/config values accidentally.
                        // sceneName/target/coords: if JSON provided a non-empty value, ensure it's set on existing (if missing or different, log)
                        if (!string.IsNullOrEmpty(p.sceneName))
                        {
                            if (string.IsNullOrEmpty(existing.sceneName) || !string.Equals(existing.sceneName, p.sceneName, StringComparison.OrdinalIgnoreCase))
                            {
                                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merging sceneName for '{existing.name}': '{existing.sceneName}' -> '{p.sceneName}'");
                                existing.sceneName = p.sceneName;
                            }
                        }

                        if (!string.IsNullOrEmpty(p.targetGameObjectName))
                        {
                            if (string.IsNullOrEmpty(existing.targetGameObjectName) || !string.Equals(existing.targetGameObjectName, p.targetGameObjectName, StringComparison.OrdinalIgnoreCase))
                            {
                                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merging targetGameObjectName for '{existing.name}': '{existing.targetGameObjectName}' -> '{p.targetGameObjectName}'");
                                existing.targetGameObjectName = p.targetGameObjectName;
                            }
                        }

                        if (p.coords != null && p.coords.Length >= 3)
                        {
                            if (existing.coords == null || existing.coords.Length < 3)
                            {
                                existing.coords = p.coords;
                                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merged coords for '{existing.name}' from JSON");
                            }
                        }

                        // price: only overwrite if JSON provided a value (non-null)
                        if (p.price.HasValue)
                        {
                            existing.price = p.price;
                            TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merged JSON price for '{existing.name}': {p.price}");
                        }

                        // variants/lastKnown: accept JSON-provided arrays/lastKnown if present (replace only if JSON has content)
                        if (p.variants != null && p.variants.Length > 0)
                        {
                            existing.variants = p.variants;
                            TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merged JSON variants for '{existing.name}' (count={p.variants.Length})");
                        }

                        if (!string.IsNullOrEmpty(p.lastKnownVariant))
                        {
                            existing.lastKnownVariant = p.lastKnownVariant;
                            TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merged JSON lastKnownVariant for '{existing.name}' = '{p.lastKnownVariant}'");
                        }

                        // desc / visited
                        if (!string.IsNullOrEmpty(p.desc))
                        {
                            existing.GetType().GetField("desc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(existing, p.desc);
                        }

                        existing.visited = p.visited;
                    }
                }

                TBLog.Info($"TryLoadCitiesJsonIntoTravelButtonMod: merge completed. TravelButton.Cities count = {TravelButton.Cities.Count}");
            }

            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryLoadCitiesJsonIntoTravelButtonMod: unexpected: " + ex.Message);
            return false;
        }
    }

    // Helper: read cz.valheimskal.travelbutton.cfg and apply any CityName.Visited = true/false entries to the in-memory Cities list.
    private static void ApplyVisitedFlagsFromCfg()
    {
        try
        {
            if (Cities == null || Cities.Count == 0)
            {
                TBLog.Info("ApplyVisitedFlagsFromCfg: Cities is empty; nothing to apply.");
                return;
            }

            string cfgFile = null;
            try
            {
                // prefer BepInEx Paths.ConfigPath if available
                cfgFile = Path.Combine(Paths.ConfigPath, LegacyCfgFileName);
            }
            catch
            {
                cfgFile = ConfigFilePath;
            }

            if (string.IsNullOrEmpty(cfgFile) || cfgFile == "(unknown)" || !File.Exists(cfgFile))
            {
                TBLog.Info($"ApplyVisitedFlagsFromCfg: cfg file not found at: {cfgFile}");
                return;
            }

            var lines = File.ReadAllLines(cfgFile);
            if (lines == null || lines.Length == 0)
            {
                TBLog.Info("ApplyVisitedFlagsFromCfg: cfg file empty.");
                return;
            }

            const string sectionHeader = "[TravelButton.Cities]";
            bool inSection = false;
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in lines)
            {
                if (raw == null) continue;
                var line = raw.Trim();
                if (line.Length == 0) continue;

                // section header detection
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inSection = string.Equals(line, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                // skip comments
                if (line.StartsWith("#") || line.StartsWith(";")) continue;

                // Expect key = value
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var keyPart = line.Substring(0, eq).Trim().Trim('"');
                var valPart = line.Substring(eq + 1).Trim().Trim('"');

                if (!string.IsNullOrEmpty(keyPart))
                {
                    kv[keyPart] = valPart;
                }
            }

            int applied = 0;
            foreach (var city in Cities)
            {
                try
                {
                    if (string.IsNullOrEmpty(city?.name)) continue;
                    var key = $"{city.name}.Visited";

                    if (!kv.TryGetValue(key, out var sval)) continue;
                    if (string.IsNullOrEmpty(sval)) continue;

                    sval = sval.Trim().ToLowerInvariant();
                    bool newVal = sval.StartsWith("true") || sval.StartsWith("1") || sval.StartsWith("yes") || sval.StartsWith("on");

                    // Apply: set property; City.visited setter will call VisitedTracker.MarkVisited when true.
                    try
                    {
                        if (newVal)
                        {
                            city.visited = true;
                            applied++;
                            TBLog.Info($"ApplyVisitedFlagsFromCfg: applied {city.name}.Visited = {newVal} (from cfg)");
                        }
                        else
                        {
                            // If cfg explicitly sets false, we cannot reliably "unmark" visited via VisitedTracker,
                            // but we still log the intent. (VisitedTracker API doesn't expose unmarking in current design.)
                            TBLog.Info($"ApplyVisitedFlagsFromCfg: cfg requests {city.name}.Visited = false; no action (cannot unmark visited).");
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"ApplyVisitedFlagsFromCfg: failed to apply visited for {city.name}: {ex.Message}");
                    }
                }
                catch { /* continue */ }
            }

            TBLog.Info($"ApplyVisitedFlagsFromCfg: applied visited flags for {applied} cities (cfg={cfgFile}).");
        }
        catch (Exception ex)
        {
            TBLog.Warn("ApplyVisitedFlagsFromCfg: unexpected error: " + ex.Message);
        }
    }

    // mark and persist
    // --- Updated MarkCityVisitedByScene: identical to your version but calls NotifyVisitedFlagsChanged() after Persist.
    // Add these fields to the TeleportManager class (near other private/static state fields)
    private static readonly System.Collections.Generic.HashSet<string> _variantDetectionInProgress = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    private static readonly object _variantDetectionLock = new object();

    /// <summary>
    /// MarkCityVisitedByScene: simplified and instrumented with TBPerf.
    /// - Marks matching cities as visited (via property or field).
    /// - Starts DetectAndPersistVariantsForCityCoroutine only once per city (tracked).
    /// - Adds TBPerf timing around main phases for diagnostics.
    /// </summary>
    private static void MarkCityVisitedByScene(string sceneName)
    {
        var swTotal = TBPerf.StartTimer();
        try
        {
            TBLog.Info($"MarkCityVisitedByScene: enter sceneName='{sceneName}'");

            if (string.IsNullOrEmpty(sceneName))
            {
                TBLog.Info("MarkCityVisitedByScene: sceneName empty -> nothing to do");
                TBPerf.Log($"MarkCityVisitedByScene:Total:<empty>", swTotal, "");
                return;
            }

            // --- Debug: log player position at method entry (best-effort) ---
            try
            {
                var swPlayerBefore = TBPerf.StartTimer();
                try
                {
                    var beforePos = TeleportManager.GetPlayerPositionDebug();
                    TBLog.Info($"MarkCityVisitedByScene: player position (before) = {beforePos}");
                }
                catch (Exception exPlayerBeforeInner)
                {
                    TBLog.Warn("MarkCityVisitedByScene: failed to read player position at entry (inner): " + exPlayerBeforeInner.Message);
                }
                TBPerf.Log($"MarkCityVisitedByScene:PlayerPosBefore", swPlayerBefore, "");
            }
            catch (Exception exPlayerBefore)
            {
                TBLog.Warn("MarkCityVisitedByScene: failed to read player position at entry: " + exPlayerBefore.Message);
            }
            // --- end debug player-before ---

            if (TravelButton.Cities == null)
            {
                TBLog.Info("MarkCityVisitedByScene: TravelButton.Cities == null; nothing to do.");
                TBPerf.Log($"MarkCityVisitedByScene:Total:{sceneName}", swTotal, "no_cities");
                return;
            }

            TBLog.Info($"MarkCityVisitedByScene: Cities.Count = {TravelButton.Cities.Count}");
            bool anyChange = false;

            // timer for per-city handling (helps find slow city reflections)
            var swPerCityLoop = TBPerf.StartTimer();
            foreach (var city in TravelButton.Cities)
            {
                var swCity = TBPerf.StartTimer();
                try
                {
                    if (city == null)
                    {
                        TBLog.Info("MarkCityVisitedByScene: skipped null city entry");
                        continue;
                    }

                    TBLog.Info($"MarkCityVisitedByScene: checking city='{city.name}' sceneName='{city.sceneName}' targetGameObjectName='{city.targetGameObjectName}'");

                    // quick-match test
                    if (!string.Equals(city.sceneName, sceneName, System.StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(city.targetGameObjectName, sceneName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        TBPerf.Log($"MarkCityVisitedByScene:CitySkip:{city.name}", swCity, $"no_scene_match");
                        continue;
                    }

                    TBLog.Info($"MarkCityVisitedByScene: scene matches city '{city.name}' (sceneName='{city.sceneName}' target='{city.targetGameObjectName}')");

                    var type = city.GetType();

                    // Try property first (Visited / visited)
                    var prop = type.GetProperty("Visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            ?? type.GetProperty("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                    bool startedVariantDetection = false;

                    if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                    {
                        try
                        {
                            var current = (bool)prop.GetValue(city, null);
                            TBLog.Info($"MarkCityVisitedByScene: property '{prop.Name}' current value={current} on city '{city.name}'");
                            if (!current)
                            {
                                prop.SetValue(city, true, null);
                                TBLog.Info($"MarkCityVisitedByScene: set property '{prop.Name}' = true on city '{city.name}'");
                                anyChange = true;

                                try { WriteVisitedFlagToCfg(city.name, true); } catch (Exception exCfg) { TBLog.Warn($"MarkCityVisitedByScene: WriteVisitedFlagToCfg failed for '{city.name}': {exCfg.Message}"); }
                            }
                            else
                            {
                                TBLog.Info($"MarkCityVisitedByScene: property '{prop.Name}' already true for city '{city.name}'");
                            }
                        }
                        catch (Exception exProp)
                        {
                            TBLog.Warn($"MarkCityVisitedByScene: failed to get/set property '{prop.Name}' on city '{city.name}': {exProp.Message}");
                        }

                        // Start variant detection coroutine if needed (guarded)
                        try
                        {
                            if (string.IsNullOrEmpty(city.lastKnownVariant) || (city.variants == null || city.variants.Length == 0))
                            {
                                var plugin = TravelButtonPlugin.Instance;
                                if (plugin != null)
                                {
                                    if (TryStartVariantDetection(plugin, city))
                                    {
                                        startedVariantDetection = true;
                                        TBLog.Info($"MarkCityVisitedByScene: scheduled variant detection coroutine for '{city.name}'.");
                                    }
                                    else
                                    {
                                        TBLog.Info($"MarkCityVisitedByScene: variant detection already in progress for '{city.name}'.");
                                    }
                                }
                                else
                                {
                                    TBLog.Info("MarkCityVisitedByScene: plugin instance not available to start variant detection coroutine.");
                                }
                            }
                        }
                        catch (Exception exDetectStart)
                        {
                            TBLog.Warn($"MarkCityVisitedByScene: error checking/starting variant detection for '{city.name}': {exDetectStart.Message}");
                        }

                        TBPerf.Log($"MarkCityVisitedByScene:CityHandled:{city.name}", swCity, $"propVisited=true, startedDetect={startedVariantDetection}");
                        continue; // done with this city
                    }

                    // Fallback to field (visited / Visited)
                    var field = type.GetField("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            ?? type.GetField("Visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                    if (field != null && field.FieldType == typeof(bool))
                    {
                        try
                        {
                            var cur = (bool)field.GetValue(city);
                            TBLog.Info($"MarkCityVisitedByScene: field '{field.Name}' current value={cur} on city '{city.name}'");
                            if (!cur)
                            {
                                field.SetValue(city, true);
                                TBLog.Info($"MarkCityVisitedByScene: set field '{field.Name}' = true on city '{city.name}'");
                                anyChange = true;

                                try { WriteVisitedFlagToCfg(city.name, true); } catch (Exception exCfg) { TBLog.Warn($"MarkCityVisitedByScene: WriteVisitedFlagToCfg failed for '{city.name}': {exCfg.Message}"); }
                            }
                            else
                            {
                                TBLog.Info($"MarkCityVisitedByScene: field '{field.Name}' already true for city '{city.name}'");
                            }
                        }
                        catch (Exception exField)
                        {
                            TBLog.Warn($"MarkCityVisitedByScene: failed to get/set field '{field.Name}' on city '{city.name}': {exField.Message}");
                        }

                        // Start variant detection coroutine if needed (guarded)
                        try
                        {
                            if (string.IsNullOrEmpty(city.lastKnownVariant) || (city.variants == null || city.variants.Length == 0))
                            {
                                var plugin = TravelButtonPlugin.Instance;
                                if (plugin != null)
                                {
                                    if (TryStartVariantDetection(plugin, city))
                                    {
                                        startedVariantDetection = true;
                                        TBLog.Info($"MarkCityVisitedByScene: scheduled variant detection coroutine for '{city.name}'.");
                                    }
                                    else
                                    {
                                        TBLog.Info($"MarkCityVisitedByScene: variant detection already in progress for '{city.name}'.");
                                    }
                                }
                                else
                                {
                                    TBLog.Info("MarkCityVisitedByScene: plugin instance not available to start variant detection coroutine.");
                                }
                            }
                        }
                        catch (Exception exDetectStart)
                        {
                            TBLog.Warn($"MarkCityVisitedByScene: error checking/starting variant detection for '{city.name}': {exDetectStart.Message}");
                        }

                        TBPerf.Log($"MarkCityVisitedByScene:CityHandled:{city.name}", swCity, $"fieldVisited=true, startedDetect={startedVariantDetection}");
                        continue;
                    }

                    // If neither property nor field found, still attempt variant detection (guarded)
                    TBLog.Info($"MarkCityVisitedByScene: no 'visited' property/field found on city type '{type.FullName}' for city '{city.name}'");

                    try
                    {
                        if (string.IsNullOrEmpty(city.lastKnownVariant) || (city.variants == null || city.variants.Length == 0))
                        {
                            var plugin = TravelButtonPlugin.Instance;
                            if (plugin != null)
                            {
                                if (TryStartVariantDetection(plugin, city))
                                {
                                    startedVariantDetection = true;
                                    TBLog.Info($"MarkCityVisitedByScene: scheduled variant detection coroutine for '{city.name}' (no visited field/property).");
                                }
                                else
                                {
                                    TBLog.Info($"MarkCityVisitedByScene: variant detection already in progress for '{city.name}'.");
                                }
                            }
                            else
                            {
                                TBLog.Info("MarkCityVisitedByScene: plugin instance not available to start variant detection coroutine.");
                            }
                        }
                    }
                    catch (Exception exDetectStart)
                    {
                        TBLog.Warn($"MarkCityVisitedByScene: error checking/starting variant detection for '{city.name}': {exDetectStart.Message}");
                    }

                    TBPerf.Log($"MarkCityVisitedByScene:CityHandled:{city.name}", swCity, $"noVisitedMember, startedDetect={startedVariantDetection}");
                }
                catch (Exception exCity)
                {
                    TBLog.Warn($"MarkCityVisitedByScene: per-city handler threw for city '{city?.name ?? "(null)"}': {exCity.Message}");
                }
            } // foreach city
            TBPerf.Log($"MarkCityVisitedByScene:PerCityLoop:{sceneName}", swPerCityLoop, $"citiesChecked={TravelButton.Cities.Count}");

            if (anyChange)
            {
                try
                {
                    TravelButton.PersistCitiesToPluginFolder();
                    TBLog.Info($"MarkCityVisitedByScene: marked and persisted visited for scene '{sceneName}'");
                    try { NotifyVisitedFlagsChanged(); } catch (Exception exNotify) { TBLog.Warn("MarkCityVisitedByScene: NotifyVisitedFlagsChanged threw: " + exNotify.Message); }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("MarkCityVisitedByScene: persist failed: " + ex.Message);
                }
            }
            else
            {
                TBLog.Info($"MarkCityVisitedByScene: no visited flags changed for scene '{sceneName}'");
            }

            // --- Debug: log player position at method exit (best-effort) ---
            try
            {
                var swPlayerAfter = TBPerf.StartTimer();
                try
                {
                    var afterPos = TeleportManager.GetPlayerPositionDebug();
                    TBLog.Info($"MarkCityVisitedByScene: player position (after) = {afterPos}");
                }
                catch (Exception exPlayerAfterInner)
                {
                    TBLog.Warn("MarkCityVisitedByScene: failed to read player position at exit (inner): " + exPlayerAfterInner.Message);
                }
                TBPerf.Log($"MarkCityVisitedByScene:PlayerPosAfter", swPlayerAfter, "");
            }
            catch (Exception exPlayerAfter)
            {
                TBLog.Warn("MarkCityVisitedByScene: failed to read player position at exit: " + exPlayerAfter.Message);
            }

            TBPerf.Log($"MarkCityVisitedByScene:Total:{sceneName}", swTotal, $"anyChange={anyChange}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("MarkCityVisitedByScene: unexpected error: " + ex.Message);
            TBPerf.Log($"MarkCityVisitedByScene:Total:{sceneName}", swTotal, $"exception={ex.Message}");
        }
    }

    /// <summary>
    /// Try to start variant detection for a city only once concurrently.
    /// Returns true if a new detection coroutine was started; false if one was already running.
    /// </summary>
    private static bool TryStartVariantDetection(TravelButtonPlugin plugin, City city)
    {
        if (plugin == null || city == null) return false;
        var cityKey = (city.name ?? "").Trim();
        if (string.IsNullOrEmpty(cityKey)) return false;

        lock (_variantDetectionLock)
        {
            if (_variantDetectionInProgress.Contains(cityKey)) return false;
            _variantDetectionInProgress.Add(cityKey);
        }

        // Start a wrapper coroutine that ensures the tracking set is cleared when done.
        try
        {
            plugin.StartCoroutine(VariantDetectWrapper(plugin, city));
            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TryStartVariantDetection: failed to start wrapper coroutine for '{cityKey}': {ex.Message}");
            lock (_variantDetectionLock) { _variantDetectionInProgress.Remove(cityKey); }
            return false;
        }
    }

    /// <summary>
    /// Wrapper coroutine: runs the actual DetectAndPersistVariantsForCityCoroutine and clears in-progress marker afterwards.
    /// </summary>
    private static IEnumerator VariantDetectWrapper(TravelButtonPlugin plugin, City city)
    {
        var cityKey = (city?.name ?? "").Trim();
        var sw = TBPerf.StartTimer();

        // Yield to the actual detection coroutine first (no try/catch around yield)
        yield return plugin.DetectAndPersistVariantsForCityCoroutine(city, 0.25f, 1.5f);

        // After the inner coroutine completes (or Unity's coroutine runner finished it), do logging and cleanup.
        try
        {
            TBPerf.Log($"VariantDetectWrapper:Run:{cityKey}", sw, "completed");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"VariantDetectWrapper: TBPerf.Log threw for '{cityKey}': {ex.Message}");
        }
        finally
        {
            lock (_variantDetectionLock)
            {
                _variantDetectionInProgress.Remove(cityKey);
            }
        }
    }

    // 3) Consolidated idempotent marker + source-of-mark logging helper.
    // Add this inside TravelButton class (near other helpers):
    private static void MarkCityVisited(City city, string source)
    {
        if (city == null) return;

        try
        {
            bool anyChange = false;
            var ct = city.GetType();

            // Try to set property 'visited' (preferred)
            try
            {
                var prop = ct.GetProperty("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null && prop.PropertyType == typeof(bool) && prop.CanRead && prop.CanWrite)
                {
                    bool already = false;
                    try { already = (bool)prop.GetValue(city, null); } catch { already = false; }
                    if (!already)
                    {
                        prop.SetValue(city, true, null);
                        anyChange = true;
                    }
                }
                else
                {
                    // Try a field named 'visited'
                    var field = ct.GetField("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        bool already = false;
                        try { already = (bool)field.GetValue(city); } catch { already = false; }
                        if (!already)
                        {
                            field.SetValue(city, true);
                            anyChange = true;
                        }
                    }
                }
            }
            catch { /* ignore reflection errors per-city */ }

            // Fallback: record name in plugin-level fallback set
            if (!anyChange)
            {
                string nm = city.name ?? "";
                if (!string.IsNullOrWhiteSpace(nm) && !s_pluginVisitedNames.Contains(nm))
                {
                    s_pluginVisitedNames.Add(nm);
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                try
                {
                    // Persist only on change (existing behavior)
                    TravelButton.PersistCitiesToPluginFolder();
                }
                catch (System.Exception ex)
                {
                    TBLog.Info("MarkCityVisited: PersistCitiesToConfig threw: " + ex.Message);
                }

                // ALSO persist visited flag into the cz.valheimskal.travelbutton.cfg file
                try
                {
                    var nm = city.name ?? "";
                    if (!string.IsNullOrWhiteSpace(nm))
                    {
                        WriteVisitedFlagToCfg(nm, true);
                    }
                }
                catch (System.Exception ex)
                {
                    TBLog.Warn("MarkCityVisited: WriteVisitedFlagToCfg threw: " + ex.Message);
                }

                TBLog.Info($"Marked and persisted visited for '{city.name}' (source={source}).");
            }
        }
        catch (System.Exception ex)
        {
            TBLog.Warn("MarkCityVisited exception: " + ex.Message);
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
            TBLog.Info($"TryInitConfigCoroutine: attempt {attempt}/{maxAttempts} to obtain config.");
            try
            {
                initialized = TravelButton.InitFromConfig();
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryInitConfigCoroutine: InitFromConfig threw: " + ex.Message);
                initialized = false;
            }

            if (!initialized)
                yield return new WaitForSeconds(1.0f);
        }

        if (!initialized)
        {
            TBLog.Warn("TryInitConfigCoroutine: InitFromConfig did not find an external config after retries; using defaults.");
            if (TravelButton.Cities == null || TravelButton.Cities.Count == 0)
            {
                // Try local Default() again as a deterministic fallback
                try
                {
                    var localCfg = TravelButton.GetLocalType("ConfigManager");
                    if (localCfg != null)
                    {
                        var def = localCfg.GetMethod("Default", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                        if (def != null)
                        {
                            TravelButton.MapConfigInstanceToLocal(def);
                            TBLog.Info("TryInitConfigCoroutine: populated config from local ConfigManager.Default() fallback.");
                            initialized = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TryInitConfigCoroutine: fallback Default() failed: " + ex.Message);
                }
            }
        }

        // IMPORTANT: create BepInEx Config bindings so Configuration Manager (and BepInEx GUI) can show/edit settings.
        try
        {
            EnsureBepInExConfigBindings();
            TBLog.Info("BepInEx config bindings created.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("Failed to create BepInEx config bindings: " + ex);
        }

        // Bind any cities that were added after initial bind (defensive)
        try
        {
            BindCityConfigsForNewCities();
        }
        catch (Exception ex)
        {
            TBLog.Warn("BindCityConfigsForNewCities failed: " + ex);
        }

        // Finally ensure UI exists so the player can interact
        EnsureTravelButtonUI();
    }

    public static Action<string> ShowPlayerNotification = (msg) =>
    {
        try { TBLog.Info($"[TravelButton][Notification] {msg}"); }
        catch { UnityEngine.Debug.Log("[TravelButton][Notification] " + msg); }
    };

    public static void LogCitySceneName(string cityName)
    {
        try
        {
            if (string.IsNullOrEmpty(cityName))
            {
                TBLog.Warn("LogCitySceneName: cityName is null/empty.");
                return;
            }

            var city = TravelButton.Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
            if (city == null)
            {
                TBLog.Warn($"LogCitySceneName: city '{cityName}' not found in TravelButtonMod.Cities.");
                return;
            }

            TBLog.Info($"LogCitySceneName: city='{city.name}', sceneName='{city.sceneName ?? "(null)"}', coords={(city.coords != null ? $"[{string.Join(", ", city.coords)}]" : "(null)")}, targetGameObjectName='{city.targetGameObjectName ?? "(null)"}'");
        }
        catch (Exception ex)
        {
            TBLog.Warn("LogCitySceneName exception: " + ex);
        }
    }

    // Exposed logger set by the plugin bootstrap. May be null early during domain load.
    private void EnsureTravelButtonUI()
    {
        try
        {
            var existing = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (existing != null)
            {
                TBLog.Info("EnsureTravelButtonUI: TravelButtonUI already present in scene.");
                // Ensure DontDestroyOnLoad is set on the existing GameObject
                UnityEngine.Object.DontDestroyOnLoad(existing.gameObject);
                return;
            }

            var go = new GameObject("TravelButton_Global");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TravelButtonUI>();
            go.AddComponent<CityDiscovery>(); // Add CityDiscovery to the same persistent GameObject
            TBLog.Info("EnsureTravelButtonUI: TravelButtonUI and CityDiscovery components created and DontDestroyOnLoad applied.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("EnsureTravelButtonUI failed: " + ex);
        }
    }

    // --- BepInEx config binding helpers ---
    private FileSystemWatcher configWatcher;

    private void StartConfigWatcher()
    {
        try
        {
            var configPath = GetLegacyCfgPath();
            var file = Path.GetFileName(configPath);
            var dir = Path.GetDirectoryName(configPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;

            configWatcher = new FileSystemWatcher(dir, file);
            configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            configWatcher.Changed += (s, e) =>
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        // Re-apply binding values from Config (BepInEx entries).
                        // Call the instance method - StartConfigWatcher is an instance method, so EnsureBepInExConfigBindings() is callable here.
                        EnsureBepInExConfigBindings();

                        // Refresh UI via the static helper
                        RefreshUI();

                        TBLog.Info("Config file changed on disk; UI refreshed.");
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("Config watcher callback failed: " + ex);
                    }
                });
            };
            configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { TBLog.Warn("StartConfigWatcher failed: " + ex.Message); }
    }

    // Named handler that runs when the file changes.
    // FileSystemWatcher callbacks are on a background thread so marshal to main thread.
    // ConfigWatcher_Changed: called by FileSystemWatcher when the plugin cfg file changes on disk.
    // This implementation:
    // - debounces rapid duplicate events,
    // - marshals work to the Unity main thread,
    // - ensures BepInEx config bindings exist,
    // - calls Config.Reload() so BepInEx ConfigEntry.Value instances update,
    // - copies values from BepInEx entries into runtime City objects,
    // - for cities that intentionally do not have an Enabled ConfigEntry (hidden from ConfigurationManager,
    //   e.g., "Sirocco"), reads the Enabled value directly from the cfg file and applies it,
    // - refreshes/rebuilds the UI so dialogs reflect updated values.
    private void ConfigWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        try
        {
            // Debounce: ignore very rapid repeated events (optional)
            var now = DateTime.UtcNow;
            if ((now - _lastConfigChange).TotalMilliseconds < 150) return;
            _lastConfigChange = now;

            // Enqueue to main thread (FileSystemWatcher events run on background threads)
            MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    // Ensure binds exist (if startup hasn't created them)
                    if (bex_cityPrice == null) bex_cityPrice = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>(StringComparer.InvariantCultureIgnoreCase);
                    if (bex_cityEnabled == null) bex_cityEnabled = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.InvariantCultureIgnoreCase);
                    if (bex_cityVisited == null) bex_cityVisited = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.InvariantCultureIgnoreCase);
                    if (bex_cityPrice.Count == 0 || bex_cityEnabled.Count == 0)
                    {
                        EnsureBepInExConfigBindings();
                    }

                    // Reload the BepInEx config from disk so ConfigEntry.Value picks up external edits
                    try
                    {
                        Config.Reload();
                        TBLog.Info("[TravelButton] Reloaded BepInEx config from disk.");
                    }
                    catch (Exception rex)
                    {
                        TBLog.Warn("[TravelButton] Config.Reload() failed: " + rex.Message);
                    }

                    // Update runtime city objects from the current ConfigEntry values
                    if (TravelButton.Cities != null)
                    {
                        foreach (var city in TravelButton.Cities)
                        {
                            if (string.IsNullOrEmpty(city?.name)) continue;

                            // If we have a BepInEx-enabled entry for the city, use it.
                            if (bex_cityEnabled.TryGetValue(city.name, out var enabledEntry))
                            {
                                try
                                {
                                    city.enabled = enabledEntry.Value;
                                }
                                catch (Exception ex) { TBLog.Warn($"ConfigWatcher_Changed: applying enabled for {city.name} failed: {ex.Message}"); }
                            }

                            // If we have a BepInEx price entry for the city, use it.
                            if (bex_cityPrice.TryGetValue(city.name, out var priceEntry))
                            {
                                try
                                {
                                    city.price = priceEntry.Value;
                                }
                                catch (Exception ex) { TBLog.Warn($"ConfigWatcher_Changed: applying price for {city.name} failed: {ex.Message}"); }
                            }
                        }
                    }

                    // For cities that do NOT have a bex enabled binding (hidden from ConfigurationManager,
                    // e.g. Sirocco), try to read their Enabled flag directly from the cfg file and apply it.
                    try
                    {
                        // Attempt to resolve the exact cfg path:
                        string cfgFile = null;
                        try
                        {
                            // Try to get file path from Config object via common property names
                            var cfgType = Config?.GetType();
                            var prop = cfgType?.GetProperty("ConfigFilePath") ?? cfgType?.GetProperty("FilePath") ?? cfgType?.GetProperty("FileName");
                            if (prop != null)
                            {
                                cfgFile = prop.GetValue(Config) as string;
                            }
                        }
                        catch { /* ignore reflection failures */ }

                        // Final fallback: use the filename observed in logs (adjust if your install differs)
                        if (string.IsNullOrEmpty(cfgFile))
                        {
                            cfgFile = Path.Combine(Paths.ConfigPath, LegacyCfgFileName);
                        }

                        // Parse and apply hidden-enabled keys
                        foreach (var city in TravelButton.Cities ?? Enumerable.Empty<City>())
                        {
                            if (string.IsNullOrEmpty(city?.name)) continue;

                            // Only attempt file-read if there's no bex enabled entry for this city
                            if (!bex_cityEnabled.ContainsKey(city.name))
                            {
                                if (TryReadBoolFromCfgFile(cfgFile, "TravelButton.Cities", $"{city.name}.Enabled", out bool enabledFromFile))
                                {
                                    try
                                    {
                                        city.enabled = enabledFromFile;
                                        TBLog.Info($"ConfigWatcher_Changed: applied {city.name}.Enabled={enabledFromFile} from cfg file.");
                                    }
                                    catch (Exception ex) { TBLog.Warn($"ConfigWatcher_Changed: applying file-enabled for {city.name} failed: {ex.Message}"); }
                                }
                            }
                        }
                    }
                    catch (Exception exHidden)
                    {
                        TBLog.Warn("ConfigWatcher_Changed: applying hidden config entries failed: " + exHidden.Message);
                    }

                    // Refresh UI so dialogs reflect the new prices / enabled states immediately.
                    try
                    {
                        // Try the dedicated UI refresh helper if present
                        try { RefreshUI(); } catch { }
                        // Also attempt the rebuilder fallback (defensive)
                        try { TravelButtonUI.RebuildTravelDialog(); } catch { }
                    }
                    catch (Exception exRefresh)
                    {
                        TBLog.Warn("ConfigWatcher_Changed: UI refresh failed: " + exRefresh.Message);
                    }

                    TBLog.Info($"[TravelButton] Config file changed on disk ({e.FullPath}); runtime values and UI refreshed.");
                }
                catch (Exception ex)
                {
                    TBLog.Warn("[TravelButton] ConfigWatcher_Changed (main-thread) failed: " + ex);
                }
            });
        }
        catch (Exception ex)
        {
            TBLog.Warn("ConfigWatcher_Changed (background) failed: " + ex);
        }
    }

    // Add StopConfigWatcher to dispose the watcher (if you don't already have it):
    private void StopConfigWatcher()
    {
        try
        {
            if (configWatcher != null)
            {
                configWatcher.EnableRaisingEvents = false;
                configWatcher.Changed -= ConfigWatcher_Changed; // if you used a named handler
                configWatcher.Dispose();
                configWatcher = null;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("StopConfigWatcher failed: " + ex);
        }
    }

    // EnsureBepInExConfigBindings: create BepInEx ConfigEntry bindings for cities.
    // - For normal cities: create both {City}.Enabled and {City}.Price entries and attach SettingChanged handlers.
    // - For "Sirocco": DO NOT create an Enabled ConfigEntry (so ConfigurationManager won't show a toggle).
    //     Instead: default the runtime value to false for in-game UI, but try to read an existing value
    //     from the cfg file (so manual edits to the cfg can still control Sirocco.Enabled).
    // EnsureBepInExConfigBindings: do NOT create any BepInEx ConfigEntry for "Sirocco" so it won't appear
    // in the in-game ConfigurationManager UI. Do NOT remove or modify the cfg file; instead, read any
    // existing Sirocco values from disk and apply them to the runtime model so manual edits still work.
    public void EnsureBepInExConfigBindings()
    {
        try
        {
            if (TravelButton.Cities == null)
            {
                TBLog.Warn("EnsureBepInExConfigBindings: TravelButton.Cities is null.");
                return;
            }

            const string section = "TravelButton.Cities";

            // Ensure dictionaries exist
            if (bex_cityEnabled == null) bex_cityEnabled = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.InvariantCultureIgnoreCase);
            if (bex_cityPrice == null) bex_cityPrice = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>(StringComparer.InvariantCultureIgnoreCase);
            if (bex_cityVisited == null) bex_cityVisited = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.InvariantCultureIgnoreCase);

            // Iterate cities and create bindings
            foreach (var city in TravelButton.Cities)
            {
                try
                {
                    if (string.IsNullOrEmpty(city?.name)) continue;

                    // Special-case "Sirocco": hide the Enabled/Price/Visited toggle from ConfigurationManager by NOT binding any ConfigEntry.
                    // Do NOT remove lines from the cfg file — the file remains authoritative for manual edits.
                    if (string.Equals(city.name, "Sirocco", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Default in-game value: hidden/disabled. We'll override from the cfg file if a value exists.
                        city.enabled = false;

                        try
                        {
                            // Determine cfg filename (adjust if your plugin uses a different filename)
                            string cfgFile = Path.Combine(Paths.ConfigPath, LegacyCfgFileName);

                            // Read Enabled from cfg file if present (manual edits will still be honored)
                            if (TryReadBoolFromCfgFile(cfgFile, section, $"{city.name}.Enabled", out bool enabledFromFile))
                            {
                                city.enabled = enabledFromFile;
                                TBLog.Info($"EnsureBepInExConfigBindings: applied Sirocco.Enabled from cfg file: {enabledFromFile}");
                            }

                            // Read Price from cfg file if present (so manual edits to price are also honored)
                            if (TryReadIntFromCfgFile(cfgFile, section, $"{city.name}.Price", out int priceFromFile))
                            {
                                city.price = priceFromFile;
                                TBLog.Info($"EnsureBepInExConfigBindings: applied Sirocco.Price from cfg file: {priceFromFile}");
                            }

                            // Read Visited from cfg file if present (so manual edits to visited are also honored)
                            if (TryReadBoolFromCfgFile(cfgFile, section, $"{city.name}.Visited", out bool visitedFromFile))
                            {
                                // try to apply directly to model
                                try
                                {
                                    var ct = city.GetType();
                                    var prop = ct.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                                    {
                                        prop.SetValue(city, visitedFromFile, null);
                                    }
                                    else
                                    {
                                        var field = ct.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (field != null && field.FieldType == typeof(bool))
                                            field.SetValue(city, visitedFromFile);
                                    }
                                }
                                catch { }
                                TBLog.Info($"EnsureBepInExConfigBindings: applied Sirocco.Visited from cfg file: {visitedFromFile}");
                            }
                        }
                        catch (Exception exRead)
                        {
                            TBLog.Warn("EnsureBepInExConfigBindings: reading Sirocco values from cfg failed: " + exRead.Message);
                        }

                        // IMPORTANT: do NOT bind any ConfigEntry for Sirocco (neither Enabled nor Price nor Visited).
                        // This prevents ConfigurationManager from showing Sirocco in the in-game config GUI.
                        continue;
                    }

                    // Normal cities: create Enabled, Price and Visited bindings
                    var enabledKey = Config.Bind(section, $"{city.name}.Enabled", city.enabled,
                        new ConfigDescription($"Enable teleport destination {city.name}"));
                    bex_cityEnabled[city.name] = enabledKey;

                    int defaultPriceNormal = city.price ?? 0;
                    var priceKeyNormal = Config.Bind<int>(section, $"{city.name}.Price", defaultPriceNormal,
                        new ConfigDescription($"Price for {city.name}"));
                    bex_cityPrice[city.name] = priceKeyNormal;

                    // Bind Visited (default false)
                    var visitedKey = Config.Bind<bool>(section, $"{city.name}.Visited", false,
                        new ConfigDescription($"Visited state for {city.name} (managed by plugin; mirrored here if desired)"));
                    bex_cityVisited[city.name] = visitedKey;

                    // Immediately apply the bound price value to runtime model (ensures we reflect file value)
                    try
                    {
                        city.price = priceKeyNormal.Value;
                        TBLog.Info($"EnsureBepInExConfigBindings: bound {city.name}.Price = {priceKeyNormal.Value}");
                    }
                    catch (Exception exApply)
                    {
                        TBLog.Warn($"EnsureBepInExConfigBindings: applying bound price for {city.name} failed: {exApply.Message}");
                    }

                    // Immediately apply the bound enabled value to runtime model
                    try
                    {
                        city.enabled = enabledKey.Value;
                        TBLog.Info($"EnsureBepInExConfigBindings: bound {city.name}.Enabled = {enabledKey.Value}");
                    }
                    catch (Exception exApply2)
                    {
                        TBLog.Warn($"EnsureBepInExConfigBindings: applying bound enabled for {city.name} failed: {exApply2.Message}");
                    }

                    // Immediately apply the bound visited value to runtime model
                    try
                    {
                        bool visitedValue = visitedKey.Value;
                        // try to set a visited bool on city (property or field) if present
                        try
                        {
                            var ct = city.GetType();
                            var prop = ct.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                            {
                                prop.SetValue(city, visitedValue, null);
                            }
                            else
                            {
                                var field = ct.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (field != null && field.FieldType == typeof(bool))
                                    field.SetValue(city, visitedValue);
                            }
                        }
                        catch { /* ignore reflection errors */ }

                        TBLog.Info($"EnsureBepInExConfigBindings: bound {city.name}.Visited = {visitedValue}");
                    }
                    catch (Exception exApply3)
                    {
                        TBLog.Warn($"EnsureBepInExConfigBindings: applying bound visited for {city.name} failed: {exApply3.Message}");
                    }

                    // Attach SettingChanged handlers that update runtime values and refresh UI / persist
                    {
                        var localCity = city;
                        var localEnabledKey = enabledKey;
                        var pluginInstance = this; // capture instance for PersistConfigToCfg
                        localEnabledKey.SettingChanged += (s, e) =>
                        {
                            try
                            {
                                localCity.enabled = localEnabledKey.Value;
                                TBLog.Info($"SettingChanged: {localCity.name}.Enabled = {localEnabledKey.Value}");
                                try { TravelButtonUI.RebuildTravelDialog(); } catch { }
                                // City.Enabled -> persist to legacy cfg
                                try { pluginInstance.PersistConfigToCfg(); } catch (Exception exPersist) { TBLog.Warn("SettingChanged Enabled: PersistConfigToCfg failed: " + exPersist.Message); }
                                try { pluginInstance.DumpRuntimeCitiesState($"After SettingChanged {localCity.name}.Enabled"); } catch { }
                            }
                            catch (Exception ex) { TBLog.Warn("Enabled SettingChanged handler failed: " + ex.Message); }
                        };
                    }

                    {
                        var localCity = city;
                        var localPriceKey = priceKeyNormal;
                        var pluginInstance = this;
                        localPriceKey.SettingChanged += (s, e) =>
                        {
                            try
                            {
                                localCity.price = localPriceKey.Value;
                                TBLog.Info($"SettingChanged: {localCity.name}.Price = {localPriceKey.Value}");
                                try { TravelButtonUI.RebuildTravelDialog(); } catch { }
                                // City.Price -> persist to JSON
                                try { TravelButton.PersistCitiesToPluginFolder(); } catch (Exception exPersist) { TBLog.Warn("SettingChanged Price: PersistCitiesToPluginFolder failed: " + exPersist.Message); }
                                try { pluginInstance.DumpRuntimeCitiesState($"After SettingChanged {localCity.name}.Price"); } catch { }
                            }
                            catch (Exception ex) { TBLog.Warn("Price SettingChanged handler failed: " + ex.Message); }
                        };
                    }

                    {
                        var localCity = city;
                        var localVisitedKey = visitedKey;
                        var pluginInstance = this;
                        localVisitedKey.SettingChanged += (s, e) =>
                        {
                            try
                            {
                                bool newVal = localVisitedKey.Value;
                                // apply to runtime model
                                try
                                {
                                    var ct = localCity.GetType();
                                    var prop = ct.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                                    {
                                        prop.SetValue(localCity, newVal, null);
                                    }
                                    else
                                    {
                                        var field = ct.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (field != null && field.FieldType == typeof(bool))
                                            field.SetValue(localCity, newVal);
                                    }
                                }
                                catch { }

                                TBLog.Info($"SettingChanged: {localCity.name}.Visited = {newVal}");
                                // City.Visited -> persist to JSON
                                try { TravelButton.PersistCitiesToPluginFolder(); } catch (Exception exPersist) { TBLog.Warn("SettingChanged Visited: PersistCitiesToPluginFolder failed: " + exPersist.Message); }
                                try { pluginInstance.DumpRuntimeCitiesState($"After SettingChanged {localCity.name}.Visited"); } catch { }
                            }
                            catch (Exception ex) { TBLog.Warn("Visited SettingChanged handler failed: " + ex.Message); }
                        };
                    }

                }
                catch (Exception exCity)
                {
                    TBLog.Warn($"EnsureBepInExConfigBindings: failed binding for city {city?.name}: {exCity}");
                }
            } // foreach city
        }
        catch (Exception ex)
        {
            TBLog.Warn("EnsureBepInExConfigBindings: top-level failure: " + ex);
        }
    }

    /// <summary>
    /// Try to read an integer value (e.g. Price) from a BepInEx-style cfg file inside the given section.
    /// Returns true if a value was found and parsed.
    /// </summary>
    private static bool TryReadIntFromCfgFile(string cfgFilePath, string sectionName, string keyName, out int value)
    {
        value = 0;
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
                    if (int.TryParse(right, out var parsed))
                    {
                        value = parsed;
                        return true;
                    }
                    // sometimes price could be stored as "null" or empty; treat those as not found
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            try { TBLog.Warn($"TryReadIntFromCfgFile failed: {ex.Message}"); } catch { }
        }
        return false;
    }

    /// <summary>
    /// Robust Refresh helper for TravelButtonUI that does not require modifying the original class.
    /// Finds the runtime TravelButtonUI instance and attempts several safe strategies to refresh/rebuild the dialog:
    ///  - invoke known rebuild/update methods via reflection
    ///  - call CloseDialog/OpenDialog if present
    ///  - toggle likely GameObject fields to force a UI refresh
    ///  - hide/show as a last resort
    ///
    /// Use TravelButtonUIRefresh.RefreshUI() from SettingChanged handlers.
    ///—</summary>

    /// <summary>
    /// Helper to persist config values to the legacy .cfg file.
    /// Attempts to call existing TravelButton static persist method if present; 
    /// fallback to PersistCitiesToPluginFolder.
    /// </summary>
    private void PersistConfigToCfg()
    {
        try
        {
            TBLog.Info("PersistConfigToCfg: attempting to persist config to legacy cfg file.");
            
            // Try to find and call a static persist method on TravelButton if it exists
            try
            {
                var tbType = typeof(TravelButton);
                var persistMethod = tbType.GetMethod("PersistConfigToCfg", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (persistMethod != null)
                {
                    persistMethod.Invoke(null, null);
                    TBLog.Info("PersistConfigToCfg: called TravelButton.PersistConfigToCfg() successfully.");
                    return;
                }
            }
            catch (Exception exReflect)
            {
                TBLog.Warn("PersistConfigToCfg: reflection attempt to call TravelButton.PersistConfigToCfg() failed: " + exReflect.Message);
            }

            // Fallback: persist cities to plugin folder (canonical JSON)
            try
            {
                TravelButton.PersistCitiesToPluginFolder();
                TBLog.Info("PersistConfigToCfg: fallback to PersistCitiesToPluginFolder() completed.");
            }
            catch (Exception exFallback)
            {
                TBLog.Warn("PersistConfigToCfg: fallback PersistCitiesToPluginFolder() failed: " + exFallback.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PersistConfigToCfg: unexpected error: " + ex);
        }
    }

    public static void RefreshUI()
    {
        try
        {
            var inst = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (inst == null) return;

            var t = inst.GetType();

            // 1) Try common rebuild/update method names
            string[] rebuildNames = new[]
            {
            "RebuildDialog", "BuildDialogContents", "BuildDialog", "RebuildCityList",
            "RefreshDialog", "UpdateDialog", "RebuildUI", "Rebuild"
        };
            foreach (var name in rebuildNames)
            {
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null && m.GetParameters().Length == 0)
                {
                    try { m.Invoke(inst, null); return; } catch { /* try next */ }
                }
            }

            // 2) Try Close/Open sequence if those methods exist
            var closeMethod = t.GetMethod("CloseDialog", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var openMethod = t.GetMethod("OpenDialog", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (closeMethod != null && openMethod != null)
            {
                try
                {
                    closeMethod.Invoke(inst, null);
                    openMethod.Invoke(inst, null);
                    return;
                }
                catch { /* fallthrough */ }
            }

            // 3) Try toggling likely dialog GameObject fields to force Unity to refresh visuals
            string[] dialogFieldNames = new[] { "travelDialogGameObject", "dialogRoot", "dialog", "travelDialog", "dialogGameObject" };
            foreach (var fieldName in dialogFieldNames)
            {
                var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && typeof(GameObject).IsAssignableFrom(f.FieldType))
                {
                    try
                    {
                        var go = f.GetValue(inst) as GameObject;
                        if (go != null)
                        {
                            bool wasActive = go.activeSelf;
                            // toggle to force UI refresh (briefly deactivating/reactivating)
                            go.SetActive(false);
                            go.SetActive(wasActive);
                            return;
                        }
                    }
                    catch { /* continue trying other names */ }
                }
            }

            // 4) As a last resort, rebuild the dialog by hide/show if methods exist
            var hideMethod = t.GetMethod("Hide", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? t.GetMethod("Close", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var showMethod = t.GetMethod("Show", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? t.GetMethod("Open", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (hideMethod != null && showMethod != null)
            {
                try
                {
                    hideMethod.Invoke(inst, null);
                    showMethod.Invoke(inst, null);
                    return;
                }
                catch { /* give up quietly */ }
            }
        }
        catch (Exception ex)
        {
            try { TBLog.Warn("TravelButtonUIRefresh.RefreshUI failed: " + ex.Message); } catch { Debug.LogWarning("[TravelButton] TravelButtonUIRefresh.RefreshUI failed: " + ex); }
        }
    }

    // Helper to bind config entries for any cities added at runtime after initial bind
    // Call BindCityConfigsForNewCities() if your code adds new cities later.
    // Helper to bind config entries for any cities added at runtime after initial bind
    // Call BindCityConfigsForNewCities() if your code adds new cities later.
    private void BindCityConfigsForNewCities()
    {
        try
        {
            if (TravelButton.Cities == null) return;

            // Ensure visited dictionary exists
            if (bex_cityVisited == null) bex_cityVisited = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var city in TravelButton.Cities)
            {
                if (string.IsNullOrEmpty(city?.name)) continue;
                if (bex_cityEnabled.ContainsKey(city.name)) continue;
                string section = "TravelButton.Cities";

                var priceDefault = city.price ?? TravelButton.cfgTravelCost.Value;
                var enabledDefault = city.enabled;
                var visitedDefault = false;
                try
                {
                    // try to read existing visited field on city (if present in model/JSON)
                    var ct = city.GetType();
                    var prop = ct.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null && prop.PropertyType == typeof(bool) && prop.CanRead)
                        visitedDefault = (bool)prop.GetValue(city, null);
                    else
                    {
                        var field = ct.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null && field.FieldType == typeof(bool))
                            visitedDefault = (bool)field.GetValue(city);
                    }
                }
                catch { visitedDefault = false; }

                var enabledKey = Config.Bind(section, $"{city.name}.Enabled", enabledDefault, $"Enable teleport destination {city.name}");
                var priceKey = Config.Bind<int>(section, $"{city.name}.Price", (int)city.price,
                    new ConfigDescription($"Price for {city.name}"));

                // Bind Visited (default from model if present, otherwise false)
                var visitedKey = Config.Bind<bool>(section, $"{city.name}.Visited", visitedDefault,
                    new ConfigDescription($"Visited state for {city.name} (managed by plugin; mirrored here if desired)"));

                bex_cityEnabled[city.name] = enabledKey;
                bex_cityPrice[city.name] = priceKey;
                bex_cityVisited[city.name] = visitedKey;

                // Log source of values
                bool priceFromJson = city.price.HasValue && city.price.Value == priceDefault;
                string priceSource = priceFromJson ? "JSON-seed" : "BepInEx";
                string enabledSource = enabledKey.Value == enabledDefault ? "default" : "BepInEx";
                string visitedSource = visitedKey.Value == visitedDefault ? "default" : "BepInEx";

                TBLog.Info($"New city '{city.name}': enabled={enabledKey.Value} (source: {enabledSource}), price={priceKey.Value} (source: {priceSource}), visited={visitedKey.Value} (source: {visitedSource})");

                // sync initial runtime (BepInEx is authoritative)
                city.enabled = enabledKey.Value;
                city.price = priceKey.Value;
                try
                {
                    // apply visited to runtime model (property or field)
                    var ct2 = city.GetType();
                    var prop2 = ct2.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop2 != null && prop2.PropertyType == typeof(bool) && prop2.CanWrite)
                        prop2.SetValue(city, visitedKey.Value, null);
                    else
                    {
                        var field2 = ct2.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field2 != null && field2.FieldType == typeof(bool))
                            field2.SetValue(city, visitedKey.Value);
                    }
                }
                catch { /* ignore reflection errors */ }

                enabledKey.SettingChanged += (s, e) =>
                {
                    try
                    {
                        city.enabled = enabledKey.Value;
                        TravelButton.PersistCitiesToPluginFolder();
                    }
                    catch (Exception ex) { TBLog.Warn("Enabled SettingChanged handler failed: " + ex.Message); }
                };
                priceKey.SettingChanged += (s, e) =>
                {
                    try
                    {
                        city.price = priceKey.Value;
                        TravelButton.PersistCitiesToPluginFolder();
                    }
                    catch (Exception ex) { TBLog.Warn("Price SettingChanged handler failed: " + ex.Message); }
                };
                visitedKey.SettingChanged += (s, e) =>
                {
                    try
                    {
                        // apply to runtime model
                        try
                        {
                            var ct3 = city.GetType();
                            var prop3 = ct3.GetProperty("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (prop3 != null && prop3.PropertyType == typeof(bool) && prop3.CanWrite)
                                prop3.SetValue(city, visitedKey.Value, null);
                            else
                            {
                                var field3 = ct3.GetField("visited", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (field3 != null && field3.FieldType == typeof(bool))
                                    field3.SetValue(city, visitedKey.Value);
                            }
                        }
                        catch { /* ignore */ }

                        TravelButton.PersistCitiesToPluginFolder();
                    }
                    catch (Exception ex) { TBLog.Warn("Visited SettingChanged handler failed: " + ex.Message); }
                };
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("BindCityConfigsForNewCities failed: " + ex);
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

    /// <summary>
    /// Check whether the canonical TravelButton_Cities.json contains a city with the given sceneName.
    /// If found, returns true and outputs the JObject for that city.
    /// </summary>
    public static bool VerifyJsonContainsScene(string sceneName, out JObject cityObj)
    {
        cityObj = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                TBLog.Warn("VerifyJsonContainsScene: sceneName empty");
                return false;
            }

            string jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
            {
                TBLog.Warn($"VerifyJsonContainsScene: JSON not found at path: {jsonPath}");
                return false;
            }

            var text = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                TBLog.Warn("VerifyJsonContainsScene: JSON file is empty");
                return false;
            }

            var root = JObject.Parse(text);
            var cities = root["cities"] as JArray;
            if (cities == null)
            {
                TBLog.Warn("VerifyJsonContainsScene: JSON does not contain 'cities' array");
                return false;
            }

            string norm = sceneName.Trim();
            cityObj = cities.Children<JObject>()
                .FirstOrDefault(c =>
                {
                    var sn = ((string)(c["sceneName"] ?? c["name"]))?.Trim();
                    return !string.IsNullOrEmpty(sn) && string.Equals(sn, norm, StringComparison.OrdinalIgnoreCase);
                });

            if (cityObj != null)
            {
                TBLog.Info($"VerifyJsonContainsScene: found scene '{sceneName}' in JSON: {cityObj.ToString(Newtonsoft.Json.Formatting.None)}");
                return true;
            }
            else
            {
                TBLog.Info($"VerifyJsonContainsScene: scene '{sceneName}' NOT found in JSON at {jsonPath}");
                return false;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("VerifyJsonContainsScene: error: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Attempts to reload runtime TravelButton.Cities from JSON using any available loader method (via reflection),
    /// then checks whether TravelButton.Cities contains an entry for sceneName.
    /// Returns true if runtime contains the scene after reload.
    /// </summary>
    public static bool ReloadCitiesFromJsonAndVerify(string sceneName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                TBLog.Warn("ReloadCitiesFromJsonAndVerify: sceneName empty");
                return false;
            }

            // 1) Try to call known loader methods via reflection to force runtime to read JSON:
            Type tbType = typeof(TravelButton);
            // Candidate method names observed in logs/source: ParseCitiesJsonFile, EnsureCitiesInitializedFromJsonOrDefaults, TryLoadCitiesJsonIntoTravelButtonMod, InitCities, EnsureCitiesInitializedFromJsonOrDefaults
            var methodCandidates = new[] {
                "EnsureCitiesInitializedFromJsonOrDefaults",
                "ParseCitiesJsonFile",
                "TryLoadCitiesJsonIntoTravelButtonMod",
                "InitCities",
                "EnsureCitiesInitialized"
            };

            MethodInfo foundMethod = null;
            foreach (var name in methodCandidates)
            {
                foundMethod = tbType.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (foundMethod != null) break;
            }

            if (foundMethod != null)
            {
                TBLog.Info($"ReloadCitiesFromJsonAndVerify: invoking loader method '{foundMethod.Name}' to refresh runtime cities.");
                // try invoke with a single string parameter (path) if available, else no params
                var parameters = foundMethod.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    string jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
                    foundMethod.Invoke(null, new object[] { jsonPath });
                }
                else
                {
                    foundMethod.Invoke(null, null);
                }
            }
            else
            {
                TBLog.Info("ReloadCitiesFromJsonAndVerify: no direct loader method found by reflection; attempting to refresh via PersistCitiesToPluginFolder or instructing a restart.");
                // as a fallback, try calling PersistCitiesToPluginFolder (non-destructive) or just log
                var persistMethod = tbType.GetMethod("PersistCitiesToPluginFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (persistMethod != null)
                {
                    try
                    {
                        persistMethod.Invoke(null, null);
                        TBLog.Info("ReloadCitiesFromJsonAndVerify: called PersistCitiesToPluginFolder (may not reload runtime).");
                    }
                    catch (Exception pe)
                    {
                        TBLog.Warn("ReloadCitiesFromJsonAndVerify: calling PersistCitiesToPluginFolder failed: " + pe.Message);
                    }
                }
            }

            // 2) Inspect the runtime TravelButton.Cities field or property via reflection
            // Expected: a static field/property named 'Cities' (List<City> or similar)
            var field = tbType.GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object citiesObj = null;
            if (field != null)
            {
                citiesObj = field.GetValue(null);
            }
            else
            {
                var prop = tbType.GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    citiesObj = prop.GetValue(null);
                }
            }

            if (citiesObj == null)
            {
                TBLog.Warn("ReloadCitiesFromJsonAndVerify: could not locate TravelButton.Cities field/property via reflection. The runtime collection may be inaccessible. Consider restarting plugin to load JSON.");
                // still try verifying JSON itself
                return VerifyJsonContainsScene(sceneName, out _);
            }

            // Try to enumerate the runtime collection using reflection
            var enumerable = citiesObj as System.Collections.IEnumerable;
            if (enumerable == null)
            {
                TBLog.Warn("ReloadCitiesFromJsonAndVerify: TravelButton.Cities is not enumerable via reflection.");
                return VerifyJsonContainsScene(sceneName, out _);
            }

            string norm = sceneName.Trim().ToLowerInvariant();
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                // try common property names 'sceneName' or 'SceneName' or 'scene' or 'name'
                var itemType = item.GetType();
                string sceneVal = null;
                var sceneProp = itemType.GetProperty("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (sceneProp != null)
                {
                    sceneVal = sceneProp.GetValue(item) as string;
                }
                else
                {
                    var nameProp = itemType.GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                    if (nameProp != null)
                        sceneVal = nameProp.GetValue(item) as string;
                }

                if (!string.IsNullOrEmpty(sceneVal) && sceneVal.Trim().ToLowerInvariant() == norm)
                {
                    TBLog.Info($"ReloadCitiesFromJsonAndVerify: runtime TravelButton.Cities contains scene '{sceneName}'.");
                    return true;
                }
            }

            TBLog.Warn($"ReloadCitiesFromJsonAndVerify: runtime TravelButton.Cities does NOT contain scene '{sceneName}'. Falling back to verifying JSON file itself.");
            // fallback: verify file contains it
            return VerifyJsonContainsScene(sceneName, out _);
        }
        catch (Exception ex)
        {
            TBLog.Warn("ReloadCitiesFromJsonAndVerify: unexpected error: " + ex.Message);
            return VerifyJsonContainsScene(sceneName, out _);
        }
    }

    private IEnumerator DetectAndPersistVariantsForCityCoroutine(City city, float initialDelay = 1.0f, float scanDurationSeconds = 3.0f)
    {
        var swTotal = TBPerf.StartTimer();

        if (city == null || string.IsNullOrEmpty(city.name))
        {
            TBPerf.Log("DetectVariants:Total:<invalid_city>", swTotal, "");
            yield break;
        }

        if (initialDelay > 0f)
        {
            var swDelay = TBPerf.StartTimer();
            yield return new WaitForSeconds(initialDelay);
            TBPerf.Log($"DetectVariants:InitialDelay:{city.name}", swDelay, $"delay={initialDelay:F2}s");
        }

        float deadline = Time.time + scanDurationSeconds;

        List<string> foundVariants = new List<string>();
        string foundLastVariant = null;

        string sceneKey = (city.sceneName ?? "").Trim();
        string nameKey = (city.name ?? "").Trim();
        string targetKey = (city.targetGameObjectName ?? "").Trim();

        Func<string, IEnumerable<string>> makeTokens = s =>
        {
            if (string.IsNullOrEmpty(s)) return Enumerable.Empty<string>();
            var cleaned = Regex.Replace(s, @"NewTerrain|Terrain|_Terrain|Clone", "", RegexOptions.IgnoreCase).Trim();
            cleaned = Regex.Replace(cleaned, @"[^\w]", " ");
            return cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Trim()).Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase);
        };

        var sceneTokens = makeTokens(sceneKey).ToArray();
        var nameTokens = makeTokens(nameKey).ToArray();
        var targetTokens = makeTokens(targetKey).ToArray();

        // 1) Main timed scanning loop
        var swScanLoop = TBPerf.StartTimer();
        while (Time.time <= deadline)
        {
            foundVariants.Clear();
            foundLastVariant = null;

            GameObject[] all = null;
            try
            {
                all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: FindObjectsOfType threw for '{city.name}': {ex.Message}");
                all = null;
            }

            if (all == null || all.Length == 0)
            {
                yield return null;
                continue;
            }

            var swScanOnce = TBPerf.StartTimer();
            try
            {
                foreach (var go in all)
                {
                    if (go == null) continue;
                    var raw = go.name ?? "";
                    var n = NormalizeGameObjectName(raw);
                    if (string.IsNullOrEmpty(n)) continue;

                    var low = n.ToLowerInvariant();
                    if (low.Contains("canvas") || low.Contains("ui") || low.StartsWith("btn") || low.Contains("button"))
                        continue;

                    bool matched = false;

                    if (!string.IsNullOrEmpty(targetKey) && n.IndexOf(targetKey, StringComparison.OrdinalIgnoreCase) >= 0) matched = true;
                    if (!matched && targetTokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) matched = true;

                    if (!matched && !string.IsNullOrEmpty(nameKey) && n.IndexOf(nameKey, StringComparison.OrdinalIgnoreCase) >= 0) matched = true;
                    if (!matched && nameTokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) matched = true;

                    if (!matched && !string.IsNullOrEmpty(sceneKey) && n.IndexOf(sceneKey, StringComparison.OrdinalIgnoreCase) >= 0) matched = true;
                    if (!matched && sceneTokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) matched = true;

                    if (!matched) continue;
                    if (n.Length < 3) continue;

                    if (!foundVariants.Contains(n)) foundVariants.Add(n);

                    if (go.activeInHierarchy)
                    {
                        if (!string.Equals(n, nameKey, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(n, sceneKey, StringComparison.OrdinalIgnoreCase))
                        {
                            foundLastVariant = n;
                        }
                        else if (string.IsNullOrEmpty(foundLastVariant))
                        {
                            foundLastVariant = n;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: scan exception for '{city.name}': {ex.Message}");
                // continue to next iteration until deadline
            }
            finally
            {
                TBPerf.Log($"DetectVariants:ScanOnce:{city.name}", swScanOnce, $"found={foundVariants.Count}, last={foundLastVariant ?? "<none>"}");
            }

            if (foundVariants.Count > 0) break;

            yield return null;
        }
        TBPerf.Log($"DetectVariants:ScanLoop:{city.name}", swScanLoop, $"durationRequested={scanDurationSeconds:F2}s");

        // 2) Fallback: search children of candidate roots (no yields inside try/catch)
        if (foundVariants.Count == 0)
        {
            var swFallbackRoots = TBPerf.StartTimer();
            GameObject[] roots = null;
            try
            {
                roots = UnityEngine.Object.FindObjectsOfType<GameObject>()
                    .Where(g =>
                    {
                        var nn = NormalizeGameObjectName(g?.name ?? "");
                        if (string.IsNullOrEmpty(nn)) return false;
                        if (!string.IsNullOrEmpty(targetKey) && nn.IndexOf(targetKey, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (targetTokens.Any(t => nn.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                        if (!string.IsNullOrEmpty(sceneKey) && nn.IndexOf(sceneKey, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (sceneTokens.Any(t => nn.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                        return false;
                    }).ToArray();
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: fallback roots enumeration failed for '{city.name}': {ex.Message}");
                roots = null;
            }

            if (roots != null && roots.Length > 0)
            {
                var swFallbackChildren = TBPerf.StartTimer();
                try
                {
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        foreach (Transform child in r.transform)
                        {
                            var cn = NormalizeGameObjectName(child.name);
                            if (!string.IsNullOrEmpty(cn) && !foundVariants.Contains(cn)) foundVariants.Add(cn);
                            if (child.gameObject.activeInHierarchy && string.IsNullOrEmpty(foundLastVariant)) foundLastVariant = cn;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: fallback children scan failed for '{city.name}': {ex.Message}");
                }
                finally
                {
                    TBPerf.Log($"DetectVariants:FallbackChildren:{city.name}", swFallbackChildren, $"roots={roots.Length}, found={foundVariants.Count}, last={foundLastVariant ?? "<none>"}");
                }
            }
            else
            {
                TBPerf.Log($"DetectVariants:FallbackRootsNone:{city.name}", swFallbackRoots, "no candidate roots");
            }
        }

        // 3) If still nothing, log a sample of top names
        if (foundVariants.Count == 0)
        {
            try
            {
                var swSampleNames = TBPerf.StartTimer();
                var allNames = UnityEngine.Object.FindObjectsOfType<GameObject>()
                    .Where(g => g != null)
                    .Select(g => NormalizeGameObjectName(g.name))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToArray();

                TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: no variant candidates found for '{city.name}'. Top scene object names (sample up to 80): [{string.Join(", ", allNames)}]");
                TBPerf.Log($"DetectVariants:SampleNames:{city.name}", swSampleNames, $"sampleCount={allNames.Length}");
            }
            catch { }
        }

        // 4) Finalize and persist if changed (scoring and persistence)
        try
        {
            var swFinalize = TBPerf.StartTimer();

            var finalVariants = (foundVariants ?? new List<string>()).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            finalVariants = finalVariants.Where(v =>
                !string.Equals(v, (city.targetGameObjectName ?? ""), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, (city.name ?? ""), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, (city.sceneName ?? ""), StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (finalVariants.Count == 0 && foundVariants.Count > 0)
                finalVariants = foundVariants.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Inline variant detection (no hardcoded "Normal"/"Destroyed").
            string finalLast = null;
            var swBuildScore = TBPerf.StartTimer();

            try
            {
                var candidates = new System.Collections.Generic.List<string>();
                if (finalVariants != null && finalVariants.Count > 0)
                {
                    candidates.AddRange(finalVariants.Where(s => !string.IsNullOrWhiteSpace(s)));
                }
                else
                {
                    try
                    {
                        var ctype = city.GetType();
                        var cv = (string[])(ctype.GetField("variants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city)
                                  ?? ctype.GetProperty("variants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city));
                        if (cv != null) candidates.AddRange(cv.Where(s => !string.IsNullOrWhiteSpace(s)));
                    }
                    catch { }
                }

                // include city.name token (if present)
                string cityNameForLog = "";
                try
                {
                    var ctype = city.GetType();
                    var cityName = (ctype.GetField("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string)
                                 ?? (ctype.GetProperty("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string);
                    if (!string.IsNullOrWhiteSpace(cityName) && !candidates.Contains(cityName, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(cityName);
                    cityNameForLog = cityName ?? "";
                }
                catch { }

                candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (candidates.Count > 0)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    var roots = scene.GetRootGameObjects();
                    var allTransforms = roots.SelectMany(r => r.GetComponentsInChildren<UnityEngine.Transform>(true)).ToArray();

                    // reference pos for proximity scoring
                    UnityEngine.Vector3 refPos = UnityEngine.Vector3.zero;
                    bool haveRef = false;
                    try
                    {
                        var ctype = city.GetType();
                        var coords = (float[])(ctype.GetField("coords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city)
                                   ?? ctype.GetProperty("coords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city));
                        if (coords != null && coords.Length >= 3) { refPos = new UnityEngine.Vector3(coords[0], coords[1], coords[2]); haveRef = true; }
                    }
                    catch { }
                    if (!haveRef)
                    {
                        var pgo = UnityEngine.GameObject.FindWithTag("Player");
                        if (pgo != null) { refPos = pgo.transform.position; haveRef = true; }
                        else if (UnityEngine.Camera.main != null) { refPos = UnityEngine.Camera.main.transform.position; haveRef = true; }
                    }

                    // small blacklist to avoid manager roots if they accidentally match tokens
                    var blacklistPrefixes = new[] { "Effects", "Drops", "Spawn", "Assets", "SoundPool", "Weather", "UI", "Canvas" };

                    var scored = new System.Collections.Generic.List<(string name, long score, int total, int active, float nearest)>();

                    foreach (var candidate in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        if (blacklistPrefixes.Any(b => candidate.StartsWith(b, System.StringComparison.OrdinalIgnoreCase))) continue;

                        var matches = allTransforms.Where(t =>
                        {
                            if (t == null) return false;
                            var n = t.name ?? "";
                            return (n.IndexOf(candidate, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                   || (candidate.IndexOf(n, System.StringComparison.OrdinalIgnoreCase) >= 0);
                        }).ToArray();

                        int total = matches.Length;
                        int active = matches.Count(m => m.gameObject.activeInHierarchy);

                        float nearest = float.MaxValue;
                        if (haveRef && matches.Length > 0)
                        {
                            foreach (var m in matches)
                            {
                                try { var d = UnityEngine.Vector3.Distance(refPos, m.position); if (d < nearest) nearest = d; } catch { }
                            }
                        }

                        long score = 0;
                        bool isKnownVariant = false;
                        try
                        {
                            var ctype = city.GetType();
                            var cv = (string[])(ctype.GetField("variants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city)
                                      ?? ctype.GetProperty("variants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city));
                            if (cv != null && cv.Any(v => string.Equals(v, candidate, System.StringComparison.OrdinalIgnoreCase))) isKnownVariant = true;
                        }
                        catch { }

                        if (isKnownVariant) score += 200000;
                        score += active * 2000;
                        score += total * 50;
                        if (haveRef && nearest < float.MaxValue) score -= (long)nearest;

                        if (isKnownVariant || total > 0)
                        {
                            scored.Add((candidate, score, total, active, nearest));
                        }
                    }

                    // adjust scores with blacklist/city tokens
                    try
                    {
                        var blacklistTokens = new[] {
                        "Gate","Vigil","Trigger","Lever","Visual","Template",
                        "Spawn","Respawn","Entrance","Portal","TriggerZone","GateTrigger","GateLever"
                    };

                        var cityTokens = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var ctype = city.GetType();
                            var sceneName = (ctype.GetField("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string)
                                         ?? (ctype.GetProperty("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string);
                            var cityName = (ctype.GetField("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string)
                                         ?? (ctype.GetProperty("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string);

                            if (!string.IsNullOrWhiteSpace(sceneName))
                            {
                                var cleaned = System.Text.RegularExpressions.Regex.Replace(sceneName, @"NewTerrain|Terrain|_Terrain|Clone", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                foreach (var t in cleaned.Split(new[] { ' ', '_' }, System.StringSplitOptions.RemoveEmptyEntries))
                                    if (t.Length >= 2) cityTokens.Add(t.Trim());
                            }
                            if (!string.IsNullOrWhiteSpace(cityName))
                            {
                                var cleaned = System.Text.RegularExpressions.Regex.Replace(cityName, @"NewTerrain|Terrain|_Terrain|Clone", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                foreach (var t in cleaned.Split(new[] { ' ', '_' }, System.StringSplitOptions.RemoveEmptyEntries))
                                    if (t.Length >= 2) cityTokens.Add(t.Trim());
                            }
                        }
                        catch { /* non-fatal */ }

                        for (int i = 0; i < scored.Count; i++)
                        {
                            var item = scored[i];
                            long adjust = 0;

                            if (blacklistTokens.Any(bt => item.name.IndexOf(bt, System.StringComparison.OrdinalIgnoreCase) >= 0))
                                adjust -= 200000;

                            if (cityTokens.Count > 0 && cityTokens.Any(tok => item.name.IndexOf(tok, System.StringComparison.OrdinalIgnoreCase) >= 0))
                                adjust += 300000;

                            if (item.name.IndexOf("Normal", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                adjust += 100000;

                            try
                            {
                                var ctype = city.GetType();
                                var fullScene = (ctype.GetField("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string)
                                             ?? (ctype.GetProperty("sceneName")?.GetValue(city) as string);
                                var fullCity = (ctype.GetField("name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string)
                                             ?? (ctype.GetProperty("name")?.GetValue(city) as string);
                                if (!string.IsNullOrWhiteSpace(fullScene) && item.name.IndexOf(fullScene, System.StringComparison.OrdinalIgnoreCase) >= 0) adjust += 200000;
                                if (!string.IsNullOrWhiteSpace(fullCity) && item.name.IndexOf(fullCity, System.StringComparison.OrdinalIgnoreCase) >= 0) adjust += 200000;
                            }
                            catch { }

                            scored[i] = (item.name, item.score + adjust, item.total, item.active, item.nearest);
                        }
                    }
                    catch
                    {
                        // non-fatal: leave `scored` unchanged on error
                    }

                    // log scored summary before picking best
                    TBPerf.Log($"DetectVariants:BuildScore:{city.name}", swBuildScore, $"candidates={candidates.Count}, scored={scored.Count}");

                    if (scored.Count > 0)
                    {
                        var best = scored.OrderByDescending(s => s.score)
                                         .ThenByDescending(s => s.active)
                                         .ThenByDescending(s => s.total)
                                         .ThenBy(s => s.name)
                                         .First();
                        finalLast = best.name;
                        TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: selected variant='{finalLast}' score={best.score} for city='{cityNameForLog}'");
                    }
                }
                else
                {
                    TBPerf.Log($"DetectVariants:NoCandidates:{city.name}", swBuildScore, "no candidates");
                }
            }
            catch (Exception exBuild)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: dynamic root-name detection failed: {exBuild.Message}");
            }

            // New persistence-guard: avoid overwriting a persisted token that is still present in scene
            try
            {
                var swPersistGuard = TBPerf.StartTimer();

                string persisted = null;
                try
                {
                    var ctype = city.GetType();
                    persisted = ctype.GetField("lastKnownVariant", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string
                             ?? ctype.GetProperty("lastKnownVariant", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city) as string;
                }
                catch { persisted = null; }

                bool ExistsInScene(string token)
                {
                    if (string.IsNullOrWhiteSpace(token)) return false;
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    var roots = scene.GetRootGameObjects();
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        if ((r.name ?? "").IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        foreach (UnityEngine.Transform ch in r.transform)
                        {
                            if (ch == null) continue;
                            if ((ch.name ?? "").IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                    return false;
                }

                if (!string.IsNullOrEmpty(persisted) && ExistsInScene(persisted))
                {
                    finalLast = persisted;
                    TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: keeping existing persisted variant='{persisted}' for city='{city?.name}' because it is present in scene");
                }
                else
                {
                    bool confidentToPersist = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(finalLast))
                        {
                            var ctype = city.GetType();
                            var cv = (string[])(ctype.GetField("variants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city)
                                      ?? ctype.GetProperty("variants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(city));
                            if (cv != null && cv.Any(v => string.Equals(v, finalLast, System.StringComparison.OrdinalIgnoreCase)))
                            {
                                confidentToPersist = true;
                            }

                            if (!confidentToPersist)
                            {
                                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                                var roots = scene.GetRootGameObjects();
                                foreach (var r in roots)
                                {
                                    if (r == null) continue;
                                    foreach (var tr in r.GetComponentsInChildren<UnityEngine.Transform>(true))
                                    {
                                        if (tr == null) continue;
                                        var go = tr.gameObject;
                                        foreach (var comp in go.GetComponents<UnityEngine.Component>())
                                        {
                                            if (comp == null) continue;
                                            var t = comp.GetType();
                                            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                                            {
                                                if (f.FieldType == typeof(string))
                                                {
                                                    try { var sval = f.GetValue(comp) as string; if (!string.IsNullOrEmpty(sval) && sval.IndexOf(finalLast, System.StringComparison.OrdinalIgnoreCase) >= 0) { confidentToPersist = true; break; } } catch { }
                                                }
                                            }
                                            if (confidentToPersist) break;
                                            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                                            {
                                                if (p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.CanRead)
                                                {
                                                    try { var sval = p.GetValue(comp) as string; if (!string.IsNullOrEmpty(sval) && sval.IndexOf(finalLast, System.StringComparison.OrdinalIgnoreCase) >= 0) { confidentToPersist = true; break; } } catch { }
                                                }
                                            }
                                            if (confidentToPersist) break;
                                        }
                                        if (confidentToPersist) break;
                                    }
                                    if (confidentToPersist) break;
                                }
                            }
                        }
                    }
                    catch { confidentToPersist = false; }

                    if (!confidentToPersist)
                    {
                        TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: not confident to persist detected variant='{finalLast}' for city='{city?.name}', keeping persisted");
                        finalLast = persisted ?? ((!string.IsNullOrEmpty(foundLastVariant) ? foundLastVariant : (finalVariants != null && finalVariants.Count > 0 ? finalVariants[0] : (city?.lastKnownVariant ?? ""))));
                    }
                    else
                    {
                        TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: confident to persist detected variant='{finalLast}' for city='{city?.name}'");
                    }
                }

                TBPerf.Log($"DetectVariants:PersistGuard:{city.name}", swPersistGuard, $"finalLast={(finalLast ?? "<none>")}, persisted={(persisted ?? "<none>")}");
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: persistence-guard failed: {ex.Message}");
            }

            // Continue with persisting finalVariants/finalLast into city and JSON if changed
            var variantsArray = finalVariants.ToArray();
            bool changed = false;
            if (!Enumerable.SequenceEqual(city.variants ?? new string[0], variantsArray, StringComparer.OrdinalIgnoreCase))
            {
                city.variants = variantsArray;
                changed = true;
            }

            if ((city.lastKnownVariant ?? "") != (finalLast ?? ""))
            {
                city.lastKnownVariant = finalLast ?? "";
                changed = true;
            }

            if (changed)
            {
                var swPersist = TBPerf.StartTimer();
                TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: detected variants for '{city.name}': [{string.Join(", ", city.variants ?? new string[0])}], lastKnownVariant='{city.lastKnownVariant}'");
                TravelButton.AppendOrUpdateCityInJsonAndSave(city);
                TBPerf.Log($"DetectVariants:Persist:{city.name}", swPersist, $"variants={city.variants?.Length ?? 0}");
            }
            else
            {
                TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: no variant changes for '{city.name}' (variantsCount={city.variants?.Length ?? 0}, lastKnownVariant='{city.lastKnownVariant ?? ""}').");
            }

            TBPerf.Log($"DetectVariants:Finalize:{city.name}", swFinalize, $"foundVariants={foundVariants.Count}, finalVariants={finalVariants.Count}, finalLast={(finalLast ?? "<none>")}");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: finalize failed for '{city?.name}': {ex.Message}");
        }

        TBPerf.Log($"DetectVariants:Total:{city.name}", swTotal, $"foundVariants={foundVariants.Count}");
        yield break;
    }

    // Choose best variant from an explicit candidate list using the same heuristic used interactively:
    // prefer active instances (most), tie-break by nearest to player/camera, then by total matches, then first.
    private static string ResolveBestVariantFromCandidates(IEnumerable<string> candidates)
    {
        try
        {
            if (candidates == null) return string.Empty;
            var variants = candidates.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            if (variants.Length == 0) return string.Empty;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var allTransforms = roots.SelectMany(g => g.GetComponentsInChildren<UnityEngine.Transform>(true)).ToArray();

            // try player or camera as reference
            UnityEngine.Vector3? refPos = null;
            try
            {
                var pgo = UnityEngine.GameObject.FindWithTag("Player");
                if (pgo != null) refPos = pgo.transform.position;
                else if (UnityEngine.Camera.main != null) refPos = UnityEngine.Camera.main.transform.position;
            }
            catch { refPos = null; }

            string chosen = null;
            int bestActive = -1;
            int bestTotal = -1;
            float bestDist = float.MaxValue;

            foreach (var v in variants)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;

                var matches = allTransforms.Where(t =>
                    t != null &&
                    t.gameObject.scene == scene &&
                    (string.Equals(t.name, v, StringComparison.OrdinalIgnoreCase) || t.name.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)
                ).ToArray();

                int total = matches.Length;
                int active = matches.Count(m => m.gameObject.activeInHierarchy);
                float nearest = float.MaxValue;
                if (refPos.HasValue && matches.Length > 0)
                {
                    foreach (var m in matches) { if (m == null) continue; var d = UnityEngine.Vector3.Distance(refPos.Value, m.position); if (d < nearest) nearest = d; }
                }

                TBLog.Info($"ResolveBestVariantFromCandidates: variant='{v}' total={total} active={active} nearest={(nearest == float.MaxValue ? "n/a" : nearest.ToString("F2"))}");

                if (active > 0)
                {
                    if (active > bestActive || (active == bestActive && refPos.HasValue && nearest < bestDist))
                    {
                        chosen = v;
                        bestActive = active;
                        bestTotal = total;
                        bestDist = nearest;
                    }
                }
                else
                {
                    if (bestActive <= 0)
                    {
                        if (total > bestTotal || (total == bestTotal && refPos.HasValue && nearest < bestDist))
                        {
                            chosen = v;
                            bestTotal = total;
                            bestDist = nearest;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(chosen))
            {
                chosen = variants.FirstOrDefault() ?? string.Empty;
                TBLog.Info($"ResolveBestVariantFromCandidates: fallback chosen='{chosen}'");
            }
            else
            {
                TBLog.Info($"ResolveBestVariantFromCandidates: selected='{chosen}' (bestActive={bestActive}, bestTotal={bestTotal}, bestDist={(bestDist == float.MaxValue ? "n/a" : bestDist.ToString("F2"))})");
            }

            return chosen ?? string.Empty;
        }
        catch (Exception ex)
        {
            TBLog.Warn("ResolveBestVariantFromCandidates: unexpected: " + ex.Message);
            return string.Empty;
        }
    }

    // Normalize helper (unchanged)
    private static string NormalizeGameObjectName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var s = Regex.Replace(raw, @"\s*\(Clone\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    // EnsureBepInExConfigBindingsForCity: create/read BepInEx bindings for one city and apply cfg/legacy overrides to the runtime city.
    // Add this as an instance method to TravelButtonPlugin.
    public void EnsureBepInExConfigBindingsForCity(City city)
    {
        if (city == null || string.IsNullOrEmpty(city.name)) return;

        try
        {
            string section = city.name.Trim();
            // Bind with sensible defaults (do not assume existing city.price is authoritative)
            var priceEntry = this.Config.Bind(section, "Price", city.price ?? 200, $"Travel price for city '{section}'");
            var enabledEntry = this.Config.Bind(section, "Enabled", city.enabled, $"Enable travel for city '{section}'");
            var visitedEntry = this.Config.Bind(section, "Visited", city.visited, $"Visited flag for city '{section}'");

            // Apply BepInEx values if they differ
            try
            {
                // If value differs, apply and log
                if (priceEntry != null)
                {
                    city.price = priceEntry.Value;
                    TBLog.Info($"EnsureBepInExConfigBindingsForCity: applied {section}.Price = {city.price} (from BepInEx config)");
                }

                if (enabledEntry != null)
                {
                    city.enabled = enabledEntry.Value;
                    TBLog.Info($"EnsureBepInExConfigBindingsForCity: applied {section}.Enabled = {city.enabled} (from BepInEx config)");
                }

                if (visitedEntry != null)
                {
                    city.visited = visitedEntry.Value;
                    TBLog.Info($"EnsureBepInExConfigBindingsForCity: applied {section}.Visited = {city.visited} (from BepInEx config)");
                }
            }
            catch (Exception exApply)
            {
                TBLog.Warn($"EnsureBepInExConfigBindingsForCity: failed applying ConfigEntry.Value -> city fields for '{section}': {exApply.Message}");
            }

            // Also check legacy cz.valheimskal.travelbutton.cfg (if present) for explicit overrides and prefer those.
            try
            {
                var legacy = TravelButtonPlugin.GetLegacyCfgPath();
                if (!string.IsNullOrEmpty(legacy) && File.Exists(legacy))
                {
                    var lines = File.ReadAllLines(legacy);
                    // simple regex to match e.g. "ChersoneseNewTerrain.Enabled = true" allowing optional whitespace
                    var enabledRegex = new System.Text.RegularExpressions.Regex(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(section) + @"\.Enabled\s*=\s*(true|false|1|0)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var priceRegex = new System.Text.RegularExpressions.Regex(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(section) + @"\.Price\s*=\s*(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var visitedRegex = new System.Text.RegularExpressions.Regex(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(section) + @"\.Visited\s*=\s*(true|false|1|0)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var ln = lines[i].Trim();
                        if (string.IsNullOrEmpty(ln)) continue;
                        // skip comments
                        if (ln.StartsWith("#") || ln.StartsWith(";")) continue;

                        var me = enabledRegex.Match(ln);
                        if (me.Success)
                        {
                            var v = me.Groups[1].Value.ToLowerInvariant();
                            bool newVal = v.StartsWith("true") || v == "1";
                            city.enabled = newVal;
                            TBLog.Info($"EnsureBepInExConfigBindingsForCity: applied LEGACY {section}.Enabled = {city.enabled} (from {legacy} line {i + 1})");
                            continue;
                        }

                        var mp = priceRegex.Match(ln);
                        if (mp.Success)
                        {
                            if (int.TryParse(mp.Groups[1].Value, out var foundPrice))
                            {
                                city.price = foundPrice;
                                TBLog.Info($"EnsureBepInExConfigBindingsForCity: applied LEGACY {section}.Price = {city.price} (from {legacy} line {i + 1})");
                            }
                            continue;
                        }

                        var mv = visitedRegex.Match(ln);
                        if (mv.Success)
                        {
                            var v = mv.Groups[1].Value.ToLowerInvariant();
                            bool newVal = v.StartsWith("true") || v == "1";
                            city.visited = newVal;
                            TBLog.Info($"EnsureBepInExConfigBindingsForCity: applied LEGACY {section}.Visited = {city.visited} (from {legacy} line {i + 1})");
                            continue;
                        }
                    }
                }
            }
            catch (Exception exLegacy)
            {
                TBLog.Warn($"EnsureBepInExConfigBindingsForCity: reading legacy cfg failed for '{city.name}': {exLegacy.Message}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"EnsureBepInExConfigBindingsForCity: unexpected error for '{city?.name ?? "(null)"}': {ex.Message}");
        }
    }

    /// <summary>
    /// Store a newly discovered/visited scene into the canonical TravelButton_Cities.json.
    /// Safe-guards:
    /// - single .bak backup preserved/used
    /// - aborts if both canonical json and .bak are invalid (to avoid accidental overwrite)
    /// - defers coords detection by starting plugin coroutine if coords invalid/missing
    /// - verifies written file kept previous entries and restores .bak on verification failure
    /// </summary>
    public void StoreVisitedSceneToJson(string sceneName, Vector3? detectedCoords, string detectedTarget, string sceneDesc = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                TBLog.Warn("StoreVisitedSceneToJson: newSceneName is null/empty; skipping.");
                return;
            }

            // 0) Blacklist: do not store transient/system scenes
            var blacklist = new[] { "MainMenu_Empty", "LowMemory_TransitionScene" };
            if (blacklist.Any(b => string.Equals(b, sceneName, StringComparison.OrdinalIgnoreCase)))
            {
                TBLog.Info($"StoreVisitedSceneToJson: scene '{sceneName}' is blacklisted; not storing.");
                return;
            }

            // If coords missing/invalid, defer detection using coroutine (preserve original behavior)
            if (IsInvalidCoords(detectedCoords))
            {
                if (TravelButtonPlugin.Instance != null)
                {
                    TBLog.Info($"StoreVisitedSceneToJson: coords absent/invalid for '{sceneName}', deferring detection to coroutine.");
                    try
                    {
                        TravelButtonPlugin.Instance.StartWaitForPlayerPlacementAndStore(sceneName);
                        return; // coroutine will call this method again with good coords
                    }
                    catch (Exception exStart)
                    {
                        TBLog.Warn("StoreVisitedSceneToJson: failed to start deferred coroutine; falling back to immediate write: " + exStart.Message);
                        // fall through and write without coords
                    }
                }
                else
                {
                    TBLog.Info("StoreVisitedSceneToJson: plugin instance not available to defer coords; proceeding with best-effort (coords will be null).");
                }
            }

            TBLog.Info($"StoreVisitedSceneToJson: storing visited scene '{sceneName}'.");

            // Ensure runtime list exists
            if (TravelButton.Cities == null) TravelButton.Cities = new List<City>();

            // Find existing city by scene name or city name
            var existing = TravelButton.Cities.FirstOrDefault(c =>
                string.Equals(c.sceneName, sceneName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.name, sceneName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Update discovered fields
                if (detectedCoords.HasValue && !IsInvalidCoords(detectedCoords))
                {
                    existing.coords = new float[] { detectedCoords.Value.x, detectedCoords.Value.y, detectedCoords.Value.z };
                    TBLog.Info($"StoreVisitedSceneToJson: updated coords for '{existing.name}' to [{existing.coords[0]}, {existing.coords[1]}, {existing.coords[2]}].");
                }

                if (!string.IsNullOrEmpty(detectedTarget))
                {
                    existing.targetGameObjectName = detectedTarget;
                }

                if (!string.IsNullOrEmpty(sceneDesc))
                {
                    // optional: if you store desc on City, set it here
                    try
                    {
                        var descField = existing.GetType().GetField("desc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (descField != null) descField.SetValue(existing, sceneDesc);
                        else
                        {
                            var descProp = existing.GetType().GetProperty("desc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (descProp != null && descProp.CanWrite) descProp.SetValue(existing, sceneDesc);
                        }
                    }
                    catch { }
                }

                // mark visited and persist the single city (persist price/visited/lastKnownVariant changes if any)
                try
                {
                    existing.visited = true;
                    // ensure variant keys present in memory before persisting
                    if (existing.lastKnownVariant == null) existing.lastKnownVariant = "";
                    TravelButton.AppendOrUpdateCityInJsonAndSave(existing);
                }
                catch (Exception ex)
                {
                    TBLog.Warn("StoreVisitedSceneToJson: failed to persist updated existing city: " + ex.Message);
                }
            }
            else
            {
                // New city discovered — construct City and persist it via canonical writer
                try
                {
                    var newName = sceneName;

                    var city = new City(newName)
                    {
                        sceneName = sceneName,
                        coords = (detectedCoords.HasValue && !IsInvalidCoords(detectedCoords)) ? new float[] { detectedCoords.Value.x, detectedCoords.Value.y, detectedCoords.Value.z } : null,
                        targetGameObjectName = !string.IsNullOrEmpty(detectedTarget) ? detectedTarget : (sceneName + "_Location"),
                        price = ResolveDefaultCityPrice(newName),
                        enabled = true,
                        visited = true,
                        lastKnownVariant = ""
                    };

                    TravelButton.Cities.Add(city);

                    try
                    {
                        // Ensure runtime config bindings and legacy overrides are applied for the new city
                        try
                        {
                            // TravelButtonPlugin.Instance is the BepInEx plugin instance; may be null in some unit tests
                            var pluginInstance = TravelButtonPlugin.Instance;
                            if (pluginInstance != null)
                            {
                                pluginInstance.EnsureBepInExConfigBindingsForCity(city);
                            }
                            else
                            {
                                TBLog.Warn("StoreVisitedSceneToJson: TravelButtonPlugin.Instance is null; cannot apply per-city config bindings for new city.");
                            }
                        }
                        catch (Exception exBind)
                        {
                            TBLog.Warn("StoreVisitedSceneToJson: failed to ensure bex bindings for new city: " + exBind.Message);
                        }

                        // Now persist (AppendOrUpdateCityInJsonAndSave will record the final city.price/enabled/visited)
                        TravelButton.AppendOrUpdateCityInJsonAndSave(city);
                        TBLog.Info($"StoreVisitedSceneToJson: appended new scene '{sceneName}' and persisted city entry.");
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("StoreVisitedSceneToJson: failed during post-create binding/persist: " + ex.Message);
                    }
                    
                    // after TravelButton.AppendOrUpdateCityInJsonAndSave(city);
/*                    var plugin = TravelButtonPlugin.Instance;
                    if (plugin != null)
                    {
                        try
                        {
                            plugin.StartCoroutine(plugin.DetectAndPersistVariantsForCityCoroutine(city, 0.25f, 1.5f));
                            TBLog.Info($"StoreVisitedSceneToJson: started variant detection coroutine for '{city.name}'.");
                        }
                        catch (Exception exStart)
                        {
                            TBLog.Warn($"StoreVisitedSceneToJson: failed to start variant detection coroutine for '{city.name}': {exStart.Message}");
                        }
                    }
                    else
                    {
                        TBLog.Info("StoreVisitedSceneToJson: plugin instance not available to start variant detection coroutine.");
                    }*/
                }
                catch (Exception ex)
                {
                    TBLog.Warn("StoreVisitedSceneToJson: failed to create/persist new city entry: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("StoreVisitedSceneToJson: unexpected: " + ex.Message);
        }
    }

    private static bool IsInvalidCoords(Vector3? v)
    {
        if (!v.HasValue) return true;
        var p = v.Value;
        if (p == Vector3.zero) return true;

        // If any axis is close to -5000 (placeholder), consider the coords invalid.
        if (Math.Abs(p.x + 5000f) < 200f || Math.Abs(p.y + 5000f) < 200f || Math.Abs(p.z + 5000f) < 200f)
            return true;

        return false;
    }

    // Attempt to resolve the configured default city price:
    // - First try TravelButton.cfgTravelCost (ConfigEntry with .Value) if present,
    // - Then try any static field named 'globalTeleportPrice' or 'cfgGlobalTeleportPrice',
    // - Finally return a safe fallback (200).
    // ResolveDefaultCityPrice: prefer per-city cfg Price (plugin config), then legacy cfg entries on disk, then plugin-level global price, then fallback 200.
    // Improved ResolveDefaultCityPrice with verbose debug logging to trace why a value is/n't found.
    // ResolveDefaultCityPrice: check legacy on-disk cfg per-city entries first, then plugin config, then plugin globals, then fallback.
    private int ResolveDefaultCityPrice(string cityName = null)
    {
        const int fallback = 200;
        TBLog.Info($"ResolveDefaultCityPrice: enter (cityName='{cityName ?? "(null)"}')");

        // Build candidate legacy cfg paths (best-effort)
        string cfgFile;
        cfgFile = Path.Combine(Paths.ConfigPath, LegacyCfgFileName);

        // 0) If we have a cityName, try legacy on-disk cfgs first (explicit per-city lines)
        if (!string.IsNullOrEmpty(cityName) && cfgFile.Length > 0)
        {
            var priceRegex = new System.Text.RegularExpressions.Regex(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(cityName) + @"\.Price\s*=\s*(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            try
            {
                bool exists = File.Exists(cfgFile);
                TBLog.Info($"ResolveDefaultCityPrice: checking legacy cfg file '{cfgFile}' exists={exists}");

                var lines = File.ReadAllLines(cfgFile);
                TBLog.Info($"ResolveDefaultCityPrice: read {lines.Length} lines from '{cfgFile}'");
                for (int i = 0; i < lines.Length; i++)
                {
                    var ln = lines[i];
                    if (string.IsNullOrWhiteSpace(ln)) continue;
                    var m = priceRegex.Match(ln);
                    if (m.Success)
                    {
                        if (int.TryParse(m.Groups[1].Value, out var found))
                        {
                            TBLog.Info($"ResolveDefaultCityPrice: FOUND legacy cfg price for '{cityName}' in '{cfgFile}' (line {i + 1}) => {found}");
                            return found;
                        }
                        else
                        {
                            TBLog.Warn($"ResolveDefaultCityPrice: regex matched but parse failed for '{ln}' in '{cfgFile}' (line {i + 1})");
                        }
                    }
                }
            }
            catch (Exception exRead)
            {
                TBLog.Warn($"ResolveDefaultCityPrice: reading/parsing legacy cfg '{cfgFile}' failed: {exRead.Message}");
            }
        }
        else
        {
            TBLog.Info("ResolveDefaultCityPrice: skipping legacy cfg search (no cityName or no candidate paths)");
        }

        // 1) Plugin Config.Bind per-city (section=cityName). If present, use it.
        if (!string.IsNullOrEmpty(cityName))
        {
            try
            {
                TBLog.Info($"ResolveDefaultCityPrice: attempting plugin Config.Bind for section='{cityName}', key='Price' (fallback={fallback})");
                var entry = this.Config.Bind(cityName, "Price", fallback, $"Travel price for city '{cityName}'");
                if (entry != null)
                {
                    TBLog.Info($"ResolveDefaultCityPrice: plugin config returned Price = {entry.Value} for section '{cityName}'");
                    // If the value equals the fallback it means no explicit plugin config existed;
                    // but we already tried legacy files above, so if it's fallback here we treat as not found.
                    if (entry.Value != fallback)
                    {
                        return entry.Value;
                    }
                    TBLog.Info($"ResolveDefaultCityPrice: plugin config for '{cityName}' equals fallback ({fallback}), continuing to global checks");
                }
                else
                {
                    TBLog.Info($"ResolveDefaultCityPrice: Config.Bind returned null for '{cityName}.Price'");
                }
            }
            catch (Exception exCfgCity)
            {
                TBLog.Warn($"ResolveDefaultCityPrice: reading plugin config for '{cityName}' failed: {exCfgCity.Message}");
            }
        }

        // 2) Try plugin-level global price via plugin Config root
        try
        {
            TBLog.Info("ResolveDefaultCityPrice: checking plugin root config key TravelButton.GlobalTravelPrice");
            var globalEntry = this.Config.Bind("TravelButton", "GlobalTravelPrice", fallback, "Global travel price fallback");
            if (globalEntry != null && globalEntry.Value != fallback)
            {
                TBLog.Info($"ResolveDefaultCityPrice: plugin GlobalTravelPrice = {globalEntry.Value}");
                return globalEntry.Value;
            }
            TBLog.Info("ResolveDefaultCityPrice: plugin GlobalTravelPrice missing or equals fallback; continuing to reflection checks");
        }
        catch (Exception exGlobalCfg)
        {
            TBLog.Warn("ResolveDefaultCityPrice: reading GlobalTravelPrice config failed: " + exGlobalCfg.Message);
        }

        // 3) Reflection-based plugin instance fallbacks (fields/properties)
        var candidateNames = new[] { "GlobalTravelPrice", "globalTeleportPrice", "bex_globalPrice", "cfgGlobalTeleportPrice", "globalPrice" };
        var pluginType = this.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                TBLog.Info($"ResolveDefaultCityPrice: reflection check for plugin member '{name}'");
                var f = pluginType.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (f != null)
                {
                    var fv = f.GetValue(this);
                    TBLog.Info($"ResolveDefaultCityPrice: found field '{name}', valueType={(fv?.GetType().FullName ?? "null")}");
                    if (fv != null)
                    {
                        var valProp = fv.GetType().GetProperty("Value");
                        if (valProp != null)
                        {
                            var v = valProp.GetValue(fv);
                            TBLog.Info($"ResolveDefaultCityPrice: field '{name}' has .Value = {v}");
                            if (v is int vi) return vi;
                            if (v is long vl) return (int)vl;
                        }
                        if (fv is int fi) { TBLog.Info($"ResolveDefaultCityPrice: using field '{name}' = {fi}"); return fi; }
                        if (fv is long fl) { TBLog.Info($"ResolveDefaultCityPrice: using field '{name}' = {fl}"); return (int)fl; }
                    }
                }

                var p = pluginType.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (p != null)
                {
                    var pv = p.GetValue(this);
                    TBLog.Info($"ResolveDefaultCityPrice: found property '{name}', valueType={(pv?.GetType().FullName ?? "null")}");
                    if (pv != null)
                    {
                        var valProp = pv.GetType().GetProperty("Value");
                        if (valProp != null)
                        {
                            var v = valProp.GetValue(pv);
                            TBLog.Info($"ResolveDefaultCityPrice: property '{name}'.Value = {v}");
                            if (v is int vi2) return vi2;
                            if (v is long vl2) return (int)vl2;
                        }
                        if (pv is int pi) { TBLog.Info($"ResolveDefaultCityPrice: using property '{name}' = {pi}"); return pi; }
                        if (pv is long pl) { TBLog.Info($"ResolveDefaultCityPrice: using property '{name}' = {pl}"); return (int)pl; }
                    }
                }
            }
            catch (Exception exRef)
            {
                TBLog.Warn($"ResolveDefaultCityPrice: reflection check for '{name}' failed: {exRef.Message}");
            }
        }

        TBLog.Info($"ResolveDefaultCityPrice: falling back to default {fallback}");
        return fallback;
    }

}
