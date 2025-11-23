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

        // Step 1: Diagnostic city initialization
        try
        {
            CityMappingHelpers.InitCities();
            TBLog.Info("InitializeCitiesAndConfig: CityMappingHelpers.InitCities() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: CityMappingHelpers.InitCities() failed: " + ex);
        }

        // Step 2: Load and map TravelButton_Cities.json into runtime with variants/lastKnownVariant
        try
        {
            TryLoadCitiesJsonIntoTravelButtonMod();
            TBLog.Info("InitializeCitiesAndConfig: TryLoadCitiesJsonIntoTravelButtonMod() completed.");
            DumpRuntimeCitiesState("After TryLoadCitiesJsonIntoTravelButtonMod");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: TryLoadCitiesJsonIntoTravelButtonMod() failed: " + ex);
        }

        // Step 3: Attempt external config initialization
        try
        {
            TravelButton.InitFromConfig();
            if (TravelButton.Cities != null && TravelButton.Cities.Count > 0)
            {
                TBLog.Info($"InitializeCitiesAndConfig: TravelButton.InitFromConfig() loaded {TravelButton.Cities.Count} cities.");
            }
            else
            {
                TBLog.Warn("InitializeCitiesAndConfig: TravelButton.InitFromConfig() did not produce cities or file is empty.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: TravelButton.InitFromConfig() failed: " + ex);
        }

        // Step 4: Ensure cities initialized from JSON or defaults & persist canonical JSON if missing
        try
        {
            CityMappingHelpers.EnsureCitiesInitializedFromJsonOrDefaults();
            TBLog.Info("InitializeCitiesAndConfig: CityMappingHelpers.EnsureCitiesInitializedFromJsonOrDefaults() completed.");
            DumpRuntimeCitiesState("After EnsureCitiesInitializedFromJsonOrDefaults");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: EnsureCitiesInitializedFromJsonOrDefaults() failed: " + ex);
        }

        // Step 5: Create BepInEx config bindings with SettingChanged handlers (write-only)
        try
        {
            EnsureBepInExConfigBindings();
            TBLog.Info("InitializeCitiesAndConfig: EnsureBepInExConfigBindings() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: EnsureBepInExConfigBindings() failed: " + ex);
        }

        // Step 6: Start config file watcher for external edits to legacy cfg
        try
        {
            StartConfigWatcher();
            TBLog.Info("InitializeCitiesAndConfig: StartConfigWatcher() completed.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("InitializeCitiesAndConfig: StartConfigWatcher() failed: " + ex);
        }

        // Step 7: Start the existing TryInitConfigCoroutine as a retrier
        try
        {
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
    private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        try
        {
            string sceneName = scene.name ?? "";
            if (string.IsNullOrEmpty(sceneName)) return;

            // Log active scene info (diagnostic)
            try
            {
                LogActiveSceneInfo();
            }
            catch (Exception exLog)
            {
                TBLog.Warn("OnSceneLoaded: LogActiveSceneInfo failed: " + exLog.Message);
            }

            // Gather best-effort player position
            UnityEngine.Vector3? playerPos = null;
            try
            {
                playerPos = TravelButton.GetPlayerPositionInScene();
            }
            catch (Exception exPos)
            {
                TBLog.Warn("OnSceneLoaded: GetPlayerPositionInScene failed: " + exPos.Message);
            }

            // Best-effort detect a target GameObject name for the scene
            string detectedTarget = null;
            try
            {
                detectedTarget = TravelButton.DetectTargetGameObjectName(sceneName);
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
                TravelButton.StoreVisitedSceneToJson(sceneName, playerPos, detectedTarget, sceneDesc);
            }
            catch (Exception exStore)
            {
                TBLog.Warn("OnSceneLoaded: StoreVisitedSceneToJson failed: " + exStore.Message);
            }

            // Existing visit marking logic
            try
            {
                MarkCityVisitedByScene(sceneName);
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
    }

    // Add these snippets into src/TravelButton.cs in the TravelButton class.
    // 1) Add a plugin fallback set (near other static fields)
    private static System.Collections.Generic.HashSet<string> s_pluginVisitedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    // 2) If TravelButton.City does not already have a persisted visited field, add it to the City definition:
    // Insert this inside the TravelButton.City class (near other serializable fields)
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
            TravelButton.StoreVisitedSceneToJson(sceneName, acceptedPos, detectedTarget, null);
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
    private static void TryLoadCitiesJsonIntoTravelButtonMod()
    {
        try
        {
            var logger = LogSource;
            void LInfo(string m) { try { logger?.LogInfo(Prefix + m); } catch { } }
            void LWarn(string m) { try { logger?.LogWarning(Prefix + m); } catch { } }

            var candidatePaths = new List<string>();

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                //                candidatePaths.Add(Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities.json"));
                //                candidatePaths.Add(Path.Combine(baseDir, "config", "TravelButton_Cities.json"));
            }
            catch { }

            try
            {
                var asmLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmLocation))
                    candidatePaths.Add(Path.Combine(asmLocation, TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json"));
            }
            catch { }

            //            try { candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "TravelButton_Cities.json")); } catch { }
            //            try { if (!string.IsNullOrEmpty(Application.dataPath)) candidatePaths.Add(Path.Combine(Application.dataPath, "TravelButton_Cities.json")); } catch { }

            try
            {
                var cfgPath = TravelButton.ConfigFilePath;
                if (!string.IsNullOrEmpty(cfgPath) && cfgPath != "(unknown)")
                {
                    var dir = cfgPath;
                    try { if (File.Exists(cfgPath)) dir = Path.GetDirectoryName(cfgPath); } catch { }
                    //                    if (!string.IsNullOrEmpty(dir)) candidatePaths.Add(Path.Combine(dir, "TravelButton_Cities.json"));
                }
            }
            catch { }

            //          try { candidatePaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "TravelButton_Cities.json")); } catch { }

            // Find first candidate that exists and parses successfully
            string foundPath = null;
            TravelConfig loaded = null;
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in candidatePaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                string full;
                try { full = Path.GetFullPath(p); } catch { full = p; }
                if (tried.Contains(full)) continue;
                tried.Add(full);

                try
                {
                    if (!File.Exists(full)) continue;
                    var cfg = TravelConfig.LoadFromFile(full);
                    if (cfg != null)
                    {
                        foundPath = full;
                        loaded = cfg;
                        break;
                    }
                    else
                    {
                        LWarn($"TravelButton_Cities.json present at {full} but parsing failed.");
                    }
                }
                catch (Exception ex)
                {
                    LWarn($"Error while attempting to load TravelButton_Cities.json at {full}: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(foundPath))
            {
                LInfo("Found and parsed TravelButton_Cities.json at: " + foundPath);
            }
            else
            {
                LInfo("No valid TravelButton_Cities.json found in candidate locations.");
            }

            TBLog.Info("TryLoadCitiesJsonIntoTravelButtonMod: skipping implicit write of default JSON (loader should not persist).");

            // Map CityConfig entries into TravelButtonMod.City instances (metadata only)
            if (loaded != null && loaded.cities != null)
            {
                var map = new Dictionary<string, TravelButton.City>(StringComparer.OrdinalIgnoreCase);
                foreach (var cc in loaded.cities)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(cc.name)) continue;
                        var c = new TravelButton.City(cc.name);

                        if (cc.coords != null && cc.coords.Length >= 3)
                            c.coords = new float[] { cc.coords[0], cc.coords[1], cc.coords[2] };

                        c.targetGameObjectName = cc.targetGameObjectName;
                        c.sceneName = cc.sceneName;

                        // If JSON provides a price, set city.price so we can seed the Config.Bind default.
                        // BepInEx remains authoritative: EnsureBepInExConfigBindings will overwrite with the actual Config value.
                        if (cc.price >= 0)
                            c.price = cc.price;      // assign actual price
                        else
                            c.price = (int?)null;    // no price present in JSON

                        // Do not set enabled from JSON; keep BepInEx authoritative.
                        c.enabled = false;

                        // Apply visited if present in the JSON seed. City.visited property setter will mark visited via VisitedTracker.
                        try
                        {
                            if (cc.visited)
                            {
                                // setter on TravelButton.City will call VisitedTracker.MarkVisited(...)
                                c.visited = true;
                            }
                        }
                        catch { /* ignore errors from visited mapping */ }

                        // Map variants and lastKnownVariant (new fields) - ALWAYS set even if null/empty
                        try
                        {
                            // Use reflection to support different runtime City implementations
                            var cityType = c.GetType();
                            
                            // ALWAYS set variants field/property (default to empty array if null)
                            try
                            {
                                string[] variantsToSet = (cc.variants != null && cc.variants.Length > 0) ? cc.variants : new string[0];
                                
                                var variantsField = cityType.GetField("variants", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (variantsField != null && (variantsField.FieldType == typeof(string[]) || variantsField.FieldType.IsAssignableFrom(typeof(IEnumerable<string>))))
                                {
                                    variantsField.SetValue(c, variantsToSet);
                                }
                                else
                                {
                                    var variantsProp = cityType.GetProperty("variants", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (variantsProp != null && variantsProp.CanWrite)
                                    {
                                        variantsProp.SetValue(c, variantsToSet);
                                    }
                                }
                            }
                            catch { /* ignore reflection errors */ }
                            
                            // ALWAYS set lastKnownVariant field/property (default to empty string if null)
                            try
                            {
                                string lastKnownVariantToSet = cc.lastKnownVariant ?? "";
                                
                                var lastKnownVariantField = cityType.GetField("lastKnownVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (lastKnownVariantField != null && lastKnownVariantField.FieldType == typeof(string))
                                {
                                    lastKnownVariantField.SetValue(c, lastKnownVariantToSet);
                                }
                                else
                                {
                                    var lastKnownVariantProp = cityType.GetProperty("lastKnownVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (lastKnownVariantProp != null && lastKnownVariantProp.CanWrite)
                                    {
                                        lastKnownVariantProp.SetValue(c, lastKnownVariantToSet);
                                    }
                                }
                            }
                            catch { /* ignore reflection errors */ }
                        }
                        catch (Exception exVariants)
                        {
                            LWarn($"Error mapping variants/lastKnownVariant for city '{cc?.name}': {exVariants.Message}");
                        }

                        map[c.name] = c;
                    }
                    catch (Exception ex)
                    {
                        LWarn($"Error mapping city '{cc?.name ?? "(null)"}': {ex.Message}");
                    }
                }

                if (map.Count > 0)
                {
                    TravelButton.Cities = new List<TravelButton.City>(map.Values);
                    LInfo($"Loaded {TravelButton.Cities.Count} cities from TravelButton_Cities.json (metadata only).");

                    // --- Migration logic: migrate legacy .cfg visited flags only if JSON contains no visited=true entries ---
                    bool jsonHadVisited = false;
                    try
                    {
                        foreach (var cc in loaded.cities)
                        {
                            if (cc != null && cc.visited)
                            {
                                jsonHadVisited = true;
                                break;
                            }
                        }
                    }
                    catch { jsonHadVisited = false; }

                    if (jsonHadVisited)
                    {
                        LInfo("TravelButton_Cities.json already contains visited flags; skipping migration from legacy .cfg.");
                    }
                    else
                    {
                        // Determine candidate legacy cfg paths to check
                        var candidateCfgs = new List<string>();
                        try
                        {
                            // Primary best-effort value reported by the plugin
                            if (!string.IsNullOrEmpty(TravelButton.ConfigFilePath))
                                candidateCfgs.Add(TravelButton.ConfigFilePath);

                            // Common BepInEx config location
                            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                            candidateCfgs.Add(Path.Combine(baseDir, "BepInEx", "config", LegacyCfgFileName));
                            candidateCfgs.Add(Path.Combine(baseDir, "BepInEx", "config", "TravelButton.cfg"));

                            // r2modman / profile locations (from LogOutput earlier)
                            try
                            {
                                var userRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                                if (!string.IsNullOrEmpty(userRoaming))
                                {
                                    candidateCfgs.Add(Path.Combine(userRoaming, "r2modmanPlus-local", "OutwardDe", "profiles", "Default", "BepInEx", "config", LegacyCfgFileName));
                                    candidateCfgs.Add(Path.Combine(userRoaming, "r2modmanPlus-local", "OutwardDe", "profiles", "Default", "BepInEx", "config", "TravelButton.cfg"));
                                }
                            }
                            catch { /* ignore fallback generation errors */ }

                            // also try TravelButton.ConfigFilePath directory if it's a file path
                            try
                            {
                                var cfgPath = TravelButton.ConfigFilePath;
                                if (!string.IsNullOrEmpty(cfgPath) && File.Exists(cfgPath))
                                {
                                    // we'll include it above; else try its directory for TravelButton_Cities.json sibling names
                                    candidateCfgs.Add(cfgPath);
                                }
                                else if (!string.IsNullOrEmpty(cfgPath))
                                {
                                    try { var d = Path.GetDirectoryName(cfgPath); if (!string.IsNullOrEmpty(d)) candidateCfgs.Add(Path.Combine(d, LegacyCfgFileName)); } catch { }
                                }
                            }
                            catch { }
                        }
                        catch { }

                        // De-duplicate and check existence
                        var triedCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string foundCfg = null;
                        foreach (var cp in candidateCfgs)
                        {
                            if (string.IsNullOrEmpty(cp)) continue;
                            string full;
                            try { full = Path.GetFullPath(cp); } catch { full = cp; }
                            if (triedCfg.Contains(full)) continue;
                            triedCfg.Add(full);

                            bool exists = false;
                            try { exists = File.Exists(full); } catch { exists = false; }
                            LInfo($"Migration: checking legacy cfg candidate: '{full}' exists={exists}");

                            if (exists)
                            {
                                foundCfg = full;
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(foundCfg))
                        {
                            LInfo("No legacy .cfg present to migrate visited flags from (checked TravelButton.ConfigFilePath and fallback locations).");
                        }
                        else
                        {
                            LInfo($"Legacy config detected at '{foundCfg}'. Applying visited flags from legacy .cfg into loaded JSON data (one-time migration).");

                            bool applied = false;
                            try
                            {
                                // If ApplyVisitedFlagsFromCfg reads TravelButton.ConfigFilePath internally, set it to foundCfg temporarily:
                                try
                                {
                                    // only try if TravelButton exposes writable ConfigFilePath
                                    var tbType = typeof(TravelButton);
                                    var pf = tbType.GetField("ConfigFilePath", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (pf != null)
                                    {
                                        try { pf.SetValue(null, foundCfg); LInfo("Migration: temporarily set TravelButton.ConfigFilePath to found cfg path for ApplyVisitedFlagsFromCfg."); } catch { /* ignore */ }
                                    }
                                }
                                catch { /* ignore reflection attempt */ }

                                ApplyVisitedFlagsFromCfg();
                                applied = true;
                                LInfo("ApplyVisitedFlagsFromCfg completed; now persisting migrated TravelButton_Cities.json to disk.");
                            }
                            catch (Exception ex)
                            {
                                applied = false;
                                LWarn("ApplyVisitedFlagsFromCfg failed during migration: " + ex.Message);
                            }

                            if (applied)
                            {
                                try
                                {
                                    PersistCitiesToPluginFolder();
                                    LInfo("Persisted migrated TravelButton_Cities.json to disk.");
                                }
                                catch (Exception ex)
                                {
                                    LWarn("PersistCitiesToPluginFolder failed while persisting migrated JSON: " + ex.Message);
                                }
                            }
                        }
                    }
                }
                else
                {
                    LWarn("TravelButton_Cities.json parsed but no valid city entries found.");
                }
            }
        }
        catch (Exception ex)
        {
            try { LogSource?.LogWarning(Prefix + "TryLoadCitiesJsonIntoTravelButtonMod unexpected failure: " + ex.Message); } catch { }
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

                    // Apply: set property; TravelButton.City.visited setter will call VisitedTracker.MarkVisited when true.
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
    private static void MarkCityVisitedByScene(string sceneName)
    {
        try
        {
            TBLog.Info($"MarkCityVisitedByScene: enter sceneName='{sceneName}'");

            // --- Debug: log player position at method entry (best-effort) ---
            try
            {
                Vector3 beforePos = Vector3.zero;
                bool foundBefore = false;
                var pgo = GameObject.FindWithTag("Player");
                if (pgo != null)
                {
                    beforePos = pgo.transform.position;
                    foundBefore = true;
                    TBLog.Info($"MarkCityVisitedByScene: player position (before) found by tag 'Player' = {beforePos}");
                }
                else
                {
                    int scanned = 0;
                    foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                    {
                        scanned++;
                        if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                        {
                            beforePos = g.transform.position;
                            foundBefore = true;
                            TBLog.Info($"MarkCityVisitedByScene: player position (before) found by name '{g.name}' after scanning {scanned} objects = {beforePos}");
                            break;
                        }
                    }
                    if (!foundBefore)
                        TBLog.Info($"MarkCityVisitedByScene: player position (before) not found after scanning {scanned} objects.");
                }
            }
            catch (Exception exPlayerBefore)
            {
                TBLog.Warn("MarkCityVisitedByScene: failed to read player position at entry: " + exPlayerBefore);
            }
            // --- end debug player-before ---

            if (TravelButton.Cities == null)
            {
                TBLog.Info("MarkCityVisitedByScene: TravelButton.Cities == null; nothing to do.");
                return;
            }

            TBLog.Info($"MarkCityVisitedByScene: Cities.Count = {TravelButton.Cities.Count}");
            bool anyChange = false;

            foreach (var city in TravelButton.Cities)
            {
                try
                {
                    if (city == null)
                    {
                        TBLog.Info("MarkCityVisitedByScene: skipped null city entry");
                        continue;
                    }

                    TBLog.Info($"MarkCityVisitedByScene: checking city='{city.name}' sceneName='{city.sceneName}' targetGameObjectName='{city.targetGameObjectName}'");

                    // compare against city.sceneName and targetGameObjectName (case-insensitive)
                    if (!string.Equals(city.sceneName, sceneName, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(city.targetGameObjectName, sceneName, StringComparison.OrdinalIgnoreCase))
                    {
                        // not a match -> continue
                        continue;
                    }

                    TBLog.Info($"MarkCityVisitedByScene: scene matches city '{city.name}' (sceneName='{city.sceneName}' target='{city.targetGameObjectName}')");

                    var type = city.GetType();

                    // Try property first (Visited / visited)
                    var prop = type.GetProperty("Visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                            ?? type.GetProperty("visited", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

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

                                // Mirror MarkCityVisited behavior: persist per-city visited in cfg (best-effort)
                                try
                                {
                                    WriteVisitedFlagToCfg(city.name, true);
                                }
                                catch (Exception exCfg)
                                {
                                    TBLog.Warn($"MarkCityVisitedByScene: WriteVisitedFlagToCfg failed for '{city.name}': {exCfg.Message}");
                                }
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

                                // Mirror MarkCityVisited behavior: persist per-city visited in cfg (best-effort)
                                try
                                {
                                    WriteVisitedFlagToCfg(city.name, true);
                                }
                                catch (Exception exCfg)
                                {
                                    TBLog.Warn($"MarkCityVisitedByScene: WriteVisitedFlagToCfg failed for '{city.name}': {exCfg.Message}");
                                }
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

                        continue;
                    }

                    // If neither property nor field found, log for diagnostics
                    TBLog.Info($"MarkCityVisitedByScene: no 'visited' property/field found on city type '{type.FullName}' for city '{city.name}'");
                }
                catch (Exception exCity)
                {
                    TBLog.Warn($"MarkCityVisitedByScene: per-city handler threw for city '{city?.name ?? "(null)"}': {exCity.Message}");
                    // continue to next city
                }
            }

            if (anyChange)
            {
                try
                {
                    // Persist canonical JSON next to plugin DLL using the centralized method
                    TravelButton.PersistCitiesToPluginFolder();
                    TBLog.Info($"MarkCityVisitedByScene: marked and persisted visited for scene '{sceneName}'");

                    // Ensure UI and lookup are refreshed now that visited flags changed
                    try
                    {
                        NotifyVisitedFlagsChanged();
                    }
                    catch (Exception exNotify)
                    {
                        TBLog.Warn("MarkCityVisitedByScene: NotifyVisitedFlagsChanged threw: " + exNotify.Message);
                    }
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
                Vector3 afterPos = Vector3.zero;
                bool foundAfter = false;
                var pgo2 = GameObject.FindWithTag("Player");
                if (pgo2 != null)
                {
                    afterPos = pgo2.transform.position;
                    foundAfter = true;
                    TBLog.Info($"MarkCityVisitedByScene: player position (after) found by tag 'Player' = {afterPos}");
                }
                else
                {
                    int scanned2 = 0;
                    foreach (var g2 in GameObject.FindObjectsOfType<GameObject>())
                    {
                        scanned2++;
                        if (!string.IsNullOrEmpty(g2.name) && g2.name.StartsWith("PlayerChar"))
                        {
                            afterPos = g2.transform.position;
                            foundAfter = true;
                            TBLog.Info($"MarkCityVisitedByScene: player position (after) found by name '{g2.name}' after scanning {scanned2} objects = {afterPos}");
                            break;
                        }
                    }
                    if (!foundAfter)
                        TBLog.Info($"MarkCityVisitedByScene: player position (after) not found after scanning {scanned2} objects.");
                }
            }
            catch (Exception exPlayerAfter)
            {
                TBLog.Warn("MarkCityVisitedByScene: failed to read player position at exit: " + exPlayerAfter);
            }
            // --- end debug player-after ---
        }
        catch (Exception ex)
        {
            TBLog.Warn("MarkCityVisitedByScene: unexpected error: " + ex.Message);
        }
    }

    // 3) Consolidated idempotent marker + source-of-mark logging helper.
    // Add this inside TravelButton class (near other helpers):
    private static void MarkCityVisited(TravelButton.City city, string source)
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

}

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
    public static bool HasPlayerVisited(TravelButton.City city)
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


    // Vrací true pokud by tlačítko města mělo být interaktivní (clickable) právě teď
    private static bool ShouldBeInteractableNow(TravelButton.City city, Vector3 playerPos)
    {
        if (city == null) return false;

        // 1) enabled in config
        bool enabledByConfig = GetBoolMemberOrDefault(city, true, "enabled", "Enabled"); // nebo city.enabled

        // 2) visited in history (from PrepareVisitedLookup / _visitedLookup)
        // Normalize stejné klíče jako PrepareVisitedLookup používá
        bool visitedInHistory = HasPlayerVisited(city); // adaptovat na vaši funkci (fast match + legacy fallback)

        // 3) player has enough money
        int price = GetCityPrice(city); // získejte cenu (reflexně nebo property), vrací >=0
        long playerMoney = TravelButtonUI.GetPlayerCurrencyAmountOrMinusOne();
        bool hasEnoughMoney = (price <= 0) || (playerMoney >= price);

        // 4) coords available
        bool coordsAvailable = (city.coords != null && city.coords.Length >= 3) || !string.IsNullOrEmpty(city.targetGameObjectName);

        // 5) is current scene?
        bool sceneMatches = IsPlayerInScene(city.sceneName, city.targetGameObjectName); // vaše existující pomocná funkce

        // final: initialInteractable podle pravidel, přeloženo z vašeho popisu:
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

        public bool visited;

        public string sceneName;
        
        // New fields for multi-variant support
        public string[] variants;
        public string lastKnownVariant;

        public City(string name)
        {
            this.name = name;
            this.coords = null;
            this.targetGameObjectName = null;
            this.price = null;
            this.enabled = false;
            bool visited = false; 
            this.sceneName = null;
            // Initialize variants and lastKnownVariant with safe defaults
            this.variants = Array.Empty<string>();  // Use Array.Empty for better performance
            this.lastKnownVariant = "";  // Empty string, not null
        }

        // Compatibility properties expected by older code:
        // property 'visited' (lowercase) → maps to VisitedTracker if available
        public bool setVisited
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
            return TravelButton.IsCityEnabled(this.name);
        }
    }

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
                                if (mapped != null) Cities.Add(mapped);
                            }
                            catch (Exception inner)
                            {
                                TBLog.Warn("MapConfigInstanceToLocal: error mapping city entry: " + inner.Message);
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
    public static void PersistCitiesToPluginFolder(bool forceWrite = false)
    {
        try
        {
            var path = TravelButtonPlugin.GetCitiesJsonPath();
            if (!forceWrite && File.Exists(path))
            {
                TBLog.Info($"PersistCitiesToPluginFolder: canonical JSON already exists at {path}; skipping write.");
                return;
            }

            var dto = JsonTravelConfig.Default();
            int count = dto?.cities?.Count ?? 0;
            TBLog.Info($"PersistCitiesToPluginFolder: JsonTravelConfig.Default() produced {count} entries.");

            if (count == 0)
            {
                TBLog.Warn("PersistCitiesToPluginFolder: no entries to write; skipping write to avoid producing an empty JSON object.");
                return;
            }

            dto.SaveToJson(path);
            TBLog.Info($"PersistCitiesToPluginFolder: wrote {count} cities to: {path}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PersistCitiesToPluginFolder failed: " + ex);
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

    /// <summary>
    /// Store a newly discovered/visited scene into the canonical TravelButton_Cities.json.
    /// Safe-guards:
    /// - single .bak backup preserved/used
    /// - aborts if both canonical json and .bak are invalid (to avoid accidental overwrite)
    /// - defers coords detection by starting plugin coroutine if coords invalid/missing
    /// - verifies written file kept previous entries and restores .bak on verification failure
    /// </summary>
    public static void StoreVisitedSceneToJson(string newSceneName, Vector3? playerPos = null, string detectedTarget = null, string sceneDesc = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newSceneName))
            {
                TBLog.Warn("StoreVisitedSceneToJson: newSceneName is null/empty; skipping.");
                return;
            }

            // 0) Blacklist: do not store transient/system scenes
            var blacklist = new[] { "MainMenu_Empty", "LowMemory_TransitionScene" };
            if (blacklist.Any(b => string.Equals(b, newSceneName, StringComparison.OrdinalIgnoreCase)))
            {
                TBLog.Info($"StoreVisitedSceneToJson: scene '{newSceneName}' is blacklisted; not storing.");
                return;
            }

            string jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
            if (string.IsNullOrEmpty(jsonPath))
            {
                TBLog.Warn("StoreVisitedSceneToJson: canonical json path unknown; skipping.");
                return;
            }

            // ensure directory exists
            try
            {
                var dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception exDir)
            {
                TBLog.Warn("StoreVisitedSceneToJson: failed creating json directory: " + exDir.Message);
            }

            string backupPath = jsonPath + ".bak";

            // Ensure single .bak exists: create if missing by copying the canonical JSON
            try
            {
                if (File.Exists(jsonPath) && !File.Exists(backupPath))
                {
                    File.Copy(jsonPath, backupPath, overwrite: false);
                    TBLog.Info($"StoreVisitedSceneToJson: created single backup: {backupPath}");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("StoreVisitedSceneToJson: failed creating .bak (will continue): " + ex.Message);
            }

            // Load root from canonical json or bak (require at least one to parse)
            JObject root = null;
            JArray cities = null;

            bool parsedFromJson = false;
            if (File.Exists(jsonPath))
            {
                try
                {
                    var text = File.ReadAllText(jsonPath);
                    root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                    parsedFromJson = true;
                    TBLog.Info("StoreVisitedSceneToJson: parsed canonical JSON successfully.");
                }
                catch (Exception exParse)
                {
                    TBLog.Warn("StoreVisitedSceneToJson: failed parsing existing JSON: " + exParse.Message);
                }
            }

            bool parsedFromBak = false;
            if (!parsedFromJson && File.Exists(backupPath))
            {
                try
                {
                    var bakText = File.ReadAllText(backupPath);
                    root = string.IsNullOrWhiteSpace(bakText) ? new JObject() : JObject.Parse(bakText);
                    parsedFromBak = true;
                    TBLog.Info("StoreVisitedSceneToJson: parsed .bak successfully and will use it as source.");
                }
                catch (Exception exBakParse)
                {
                    TBLog.Warn("StoreVisitedSceneToJson: existing .bak parse failed: " + exBakParse.Message);
                }
            }

            // Abort if neither JSON nor .bak were parsable; avoid overwriting correct data.
            if (!parsedFromJson && !parsedFromBak)
            {
                TBLog.Warn("StoreVisitedSceneToJson: both canonical JSON and .bak are invalid - aborting write to avoid data loss.");
                return;
            }

            if (root["cities"] == null || root["cities"].Type != JTokenType.Array)
            {
                cities = new JArray();
                root["cities"] = cities;
            }
            else
            {
                cities = (JArray)root["cities"];
            }

            // Capture pre-existing sceneNames for post-write verification
            var preExistingScenes = cities.Children<JObject>()
                .Select(c => ((string)(c["sceneName"] ?? c["name"]))?.Trim().ToLowerInvariant())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            string normScene = newSceneName.Trim();

            // Check if scene already exists (case-insensitive)
            bool exists = cities.Children<JObject>().Any(c =>
            {
                var sceneVal = (string)(c["sceneName"] ?? c["name"]);
                return !string.IsNullOrEmpty(sceneVal) && string.Equals(sceneVal.Trim(), normScene, StringComparison.OrdinalIgnoreCase);
            });

            if (exists)
            {
                TBLog.Info($"StoreVisitedSceneToJson: scene '{newSceneName}' already present in JSON; skipping add.");
                return;
            }

            // If coords missing/invalid, defer detection using plugin coroutine (if available)
            if (IsInvalidCoords(playerPos))
            {
                if (TravelButtonPlugin.Instance != null)
                {
                    TBLog.Info($"StoreVisitedSceneToJson: coords absent/invalid for '{newSceneName}', deferring detection to coroutine.");
                    try
                    {
                        TravelButtonPlugin.Instance.StartWaitForPlayerPlacementAndStore(newSceneName);
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

            // Build new city object using the standard helper to ensure consistent structure
            var tempCity = new City(normScene);
            tempCity.sceneName = normScene;
            if (playerPos.HasValue && !IsInvalidCoords(playerPos))
            {
                tempCity.coords = new float[] { playerPos.Value.x, playerPos.Value.y, playerPos.Value.z };
            }
            tempCity.price = 200;
            tempCity.targetGameObjectName = !string.IsNullOrEmpty(detectedTarget) ? detectedTarget : (normScene + "_Location");
            tempCity.visited = true;
            // variants and lastKnownVariant are already initialized to empty array and empty string by constructor
            
            var cleanedCity = BuildJObjectForCity(tempCity);

            cities.Add(cleanedCity);

            // Basic validation
            bool valid = true;
            try
            {
                if (root["cities"] == null || root["cities"].Type != JTokenType.Array) valid = false;
                else
                {
                    foreach (var token in (JArray)root["cities"])
                    {
                        if (!(token is JObject jo))
                        {
                            valid = false; break;
                        }
                        var nm = (string)(jo["name"] ?? jo["sceneName"]);
                        if (string.IsNullOrWhiteSpace(nm))
                        {
                            valid = false; break;
                        }
                    }
                }
            }
            catch
            {
                valid = false;
            }

            if (!valid)
            {
                TBLog.Warn("StoreVisitedSceneToJson: constructed JSON failed validation; aborting write. .bak preserved.");
                return;
            }

            // Write to temp file then replace (prefer File.Replace to update .bak)
            try
            {
                string tempPath = jsonPath + ".tmp";
                File.WriteAllText(tempPath, root.ToString(Formatting.Indented), System.Text.Encoding.UTF8);

                try
                {
                    if (File.Exists(backupPath))
                    {
                        // Replace jsonPath with tempPath, preserving previous json into backupPath
                        File.Replace(tempPath, jsonPath, backupPath, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        // Create bak from existing json if present, then copy temp over
                        if (File.Exists(jsonPath))
                        {
                            try { File.Copy(jsonPath, backupPath, overwrite: true); } catch { /* ignore */ }
                        }
                        File.Copy(tempPath, jsonPath, overwrite: true);
                    }

                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    TBLog.Info($"StoreVisitedSceneToJson: appended new scene '{newSceneName}' to {jsonPath}");

                    try { TravelButton.PersistCitiesToPluginFolder(); } catch { /* ignore */ }

                    // --- Post-write verification: ensure preExistingScenes still present ---
                    if (preExistingScenes != null && preExistingScenes.Count > 0)
                    {
                        try
                        {
                            var newText = File.ReadAllText(jsonPath);
                            var verifyRoot = JObject.Parse(newText);
                            var verifyCities = (verifyRoot["cities"] as JArray) ?? new JArray();
                            var newSceneSet = new HashSet<string>(verifyCities.Children<JObject>()
                                .Select(c => ((string)(c["sceneName"] ?? c["name"]))?.Trim().ToLowerInvariant())
                                .Where(s => !string.IsNullOrEmpty(s)));

                            var missing = preExistingScenes.Where(ps => !newSceneSet.Contains(ps)).ToList();
                            if (missing.Count > 0)
                            {
                                TBLog.Warn($"StoreVisitedSceneToJson: verification failed - missing pre-existing scenes: {string.Join(", ", missing)}. Restoring .bak.");
                                if (File.Exists(backupPath))
                                {
                                    try
                                    {
                                        File.Copy(backupPath, jsonPath, overwrite: true);
                                        TBLog.Info("StoreVisitedSceneToJson: restored .bak to recover previous data.");
                                    }
                                    catch (Exception exRestore)
                                    {
                                        TBLog.Warn("StoreVisitedSceneToJson: failed to restore .bak during verification step: " + exRestore.Message);
                                    }
                                }
                            }
                        }
                        catch (Exception exVerify)
                        {
                            TBLog.Warn("StoreVisitedSceneToJson: post-write verification failed: " + exVerify.Message);
                            if (File.Exists(backupPath))
                            {
                                try
                                {
                                    File.Copy(backupPath, jsonPath, overwrite: true);
                                    TBLog.Info("StoreVisitedSceneToJson: restored .bak after verification parse failure.");
                                }
                                catch (Exception exRestore)
                                {
                                    TBLog.Warn("StoreVisitedSceneToJson: failed to restore .bak after verification parse failure: " + exRestore.Message);
                                }
                            }
                        }
                    }
                }
                catch (Exception exReplace)
                {
                    TBLog.Warn("StoreVisitedSceneToJson: atomic replace/write failed: " + exReplace.Message);
                    // try fallback direct copy, then attempt restore
                    try
                    {
                        File.Copy(tempPath, jsonPath, overwrite: true);
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        TBLog.Info($"StoreVisitedSceneToJson: wrote JSON to {jsonPath} using fallback direct copy.");
                    }
                    catch (Exception exFallback)
                    {
                        TBLog.Warn("StoreVisitedSceneToJson: fallback direct write failed: " + exFallback.Message);
                        if (File.Exists(backupPath))
                        {
                            try
                            {
                                File.Copy(backupPath, jsonPath, overwrite: true);
                                TBLog.Info("StoreVisitedSceneToJson: restored .bak after write failures.");
                            }
                            catch (Exception exFinal)
                            {
                                TBLog.Warn("StoreVisitedSceneToJson: failed to restore .bak after write failures: " + exFinal.Message);
                            }
                        }
                    }
                }
            }
            catch (Exception exWrite)
            {
                TBLog.Warn("StoreVisitedSceneToJson: final write failed: " + exWrite.Message);
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Copy(backupPath, jsonPath, overwrite: true);
                        TBLog.Info("StoreVisitedSceneToJson: restored .bak after final write failure.");
                    }
                    catch (Exception exRestore2)
                    {
                        TBLog.Warn("StoreVisitedSceneToJson: failed to restore .bak after final write failure: " + exRestore2.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("StoreVisitedSceneToJson: unexpected error: " + ex.Message);
        }
    }

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

    /// <summary>
    /// Build a JObject representing a city with all required keys including variants and lastKnownVariant.
    /// Ensures consistent JSON structure for atomic writes.
    /// </summary>
    /// <param name="city">The city object to serialize</param>
    /// <returns>JObject with name, sceneName, coords, price, targetGameObjectName, desc, visited, variants, lastKnownVariant</returns>
    public static JObject BuildJObjectForCity(City city)
    {
        if (city == null)
        {
            TBLog.Warn("BuildJObjectForCity: city is null, returning empty JObject");
            return new JObject();
        }

        try
        {
            var jobj = new JObject();
            
            // Always include name (use empty string if null for consistency)
            string cityName = city.name ?? "";
            jobj["name"] = cityName;
            
            // sceneName (fallback to name if null)
            jobj["sceneName"] = city.sceneName ?? cityName;
            
            // coords array (or null if not present)
            if (city.coords != null && city.coords.Length >= 3)
            {
                jobj["coords"] = new JArray(
                    Math.Round(city.coords[0], 3),
                    Math.Round(city.coords[1], 3),
                    Math.Round(city.coords[2], 3)
                );
            }
            else
            {
                jobj["coords"] = null;
            }
            
            // price
            jobj["price"] = city.price ?? 200;
            
            // targetGameObjectName
            jobj["targetGameObjectName"] = city.targetGameObjectName ?? "";
            
            // desc
            jobj["desc"] = "";  // Keep empty as per existing behavior
            
            // visited
            jobj["visited"] = city.visited;
            
            // variants array (ALWAYS present, empty array if null)
            if (city.variants != null && city.variants.Length > 0)
            {
                jobj["variants"] = new JArray(city.variants);
            }
            else
            {
                jobj["variants"] = new JArray();  // Empty array, not null
            }
            
            // lastKnownVariant (ALWAYS present, empty string if null)
            jobj["lastKnownVariant"] = city.lastKnownVariant ?? "";
            
            return jobj;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"BuildJObjectForCity: failed to build JObject for city '{city.name ?? "(null)"}': {ex}");
            return new JObject();
        }
    }

    /// <summary>
    /// Append or update a city entry in the canonical TravelButton_Cities.json file using atomic write.
    /// Reads the existing JSON, removes any entry with matching name, adds the new city object, and writes atomically.
    /// </summary>
    /// <param name="city">The city to append or update</param>
    public static void AppendOrUpdateCityInJsonAndSave(City city)
    {
        if (city == null)
        {
            TBLog.Warn("AppendOrUpdateCityInJsonAndSave: city is null, skipping");
            return;
        }

        try
        {
            string jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
            if (string.IsNullOrEmpty(jsonPath))
            {
                TBLog.Warn("AppendOrUpdateCityInJsonAndSave: canonical json path unknown; skipping");
                return;
            }

            TBLog.Info($"AppendOrUpdateCityInJsonAndSave: updating city '{city.name}' in {jsonPath}");

            // Ensure directory exists
            try
            {
                var dir = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception exDir)
            {
                TBLog.Warn("AppendOrUpdateCityInJsonAndSave: failed creating json directory: " + exDir.Message);
            }

            // Read existing JSON or create new root
            JObject root = null;
            JArray cities = null;

            if (File.Exists(jsonPath))
            {
                try
                {
                    var text = File.ReadAllText(jsonPath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        root = JObject.Parse(text);
                    }
                }
                catch (Exception exParse)
                {
                    TBLog.Warn("AppendOrUpdateCityInJsonAndSave: failed parsing existing JSON, creating new root: " + exParse.Message);
                    root = null;
                }
            }

            if (root == null)
            {
                root = new JObject();
            }

            // Ensure cities array exists
            if (root["cities"] == null || root["cities"].Type != JTokenType.Array)
            {
                cities = new JArray();
                root["cities"] = cities;
            }
            else
            {
                cities = (JArray)root["cities"];
            }

            // Remove existing entry with same name (case-insensitive)
            JToken toRemove = null;
            foreach (var item in cities)
            {
                if (item is JObject jobj)
                {
                    var nameVal = jobj["name"]?.ToString();
                    if (!string.IsNullOrEmpty(nameVal) && 
                        string.Equals(nameVal.Trim(), city.name?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove = item;
                        break;
                    }
                }
            }
            
            if (toRemove != null)
            {
                cities.Remove(toRemove);
                TBLog.Info($"AppendOrUpdateCityInJsonAndSave: removed existing entry for '{city.name}'");
            }

            // Add new city object
            var newCityObj = BuildJObjectForCity(city);
            cities.Add(newCityObj);

            // Atomic write: write to temp file then replace
            string tempPath = jsonPath + ".tmp";
            try
            {
                string json = root.ToString(Formatting.Indented);
                
                // Write to temp file
                File.WriteAllText(tempPath, json, System.Text.Encoding.UTF8);
                
                // Atomic replace
                if (File.Exists(jsonPath))
                {
                    string backupPath = jsonPath + ".bak";
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                    File.Move(jsonPath, backupPath);
                }
                File.Move(tempPath, jsonPath);
                
                TBLog.Info($"AppendOrUpdateCityInJsonAndSave: successfully updated {jsonPath}");
            }
            catch (Exception exWrite)
            {
                TBLog.Warn($"AppendOrUpdateCityInJsonAndSave: atomic write failed: {exWrite.Message}");
                
                // Cleanup temp file if it exists
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"AppendOrUpdateCityInJsonAndSave: unexpected error for city '{city?.name}': {ex}");
        }
    }


}
