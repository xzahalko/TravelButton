using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static TravelButton;

//
// TravelButtonMod.cs
// - BepInEx plugin bootstrap (TravelButtonPlugin) + runtime static helpers (TravelButtonMod).
// - Integrates with an optional external ConfigManager (safely, via reflection) and with BepInEx config system
//   so Configuration Manager displays editable settings.
// - Provides City model used by TravelButtonUI and helpers to map/persist configuration.
// - Adds diagnostics helpers DumpTravelButtonState and ForceShowTravelButton for runtime inspection.
//
[BepInPlugin("cz.valheimskal.travelbutton", "TravelButton", "1.0.1")]
public class TravelButtonPlugin : BaseUnityPlugin
{

    // BepInEx config entries (top-level)
    private BepInEx.Configuration.ConfigEntry<bool> bex_enableMod;
    private BepInEx.Configuration.ConfigEntry<int> bex_globalPrice;
    private BepInEx.Configuration.ConfigEntry<string> bex_currencyItem;

    // per-city config entries
    private Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> bex_cityEnabled = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BepInEx.Configuration.ConfigEntry<int>> bex_cityPrice = new Dictionary<string, BepInEx.Configuration.ConfigEntry<int>>(StringComparer.InvariantCultureIgnoreCase);

    // Optional prefix to make entries easy to find in BepInEx logs
    // Set by the plugin during Awake: e.g. TravelButtonPlugin.Initialize(this.Logger);
    public static ManualLogSource LogSource { get; private set; }
    private const string Prefix = "[TravelButton] ";

    private DateTime _lastConfigChange = DateTime.MinValue;

    public static void Initialize(ManualLogSource manualLogSource)
    {
        if (manualLogSource == null) throw new ArgumentNullException(nameof(manualLogSource));
        LogSource = manualLogSource;
        try { LogSource.LogInfo(Prefix + "TravelButtonPlugin initialized with BepInEx ManualLogSource."); } catch { /* swallow */ }
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
                candidatePaths.Add(Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities.json"));
                candidatePaths.Add(Path.Combine(baseDir, "config", "TravelButton_Cities.json"));
            }
            catch { }

            try
            {
                var asmLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmLocation))
                    candidatePaths.Add(Path.Combine(asmLocation, "TravelButton_Cities.json"));
            }
            catch { }

            try { candidatePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "TravelButton_Cities.json")); } catch { }
            try { if (!string.IsNullOrEmpty(Application.dataPath)) candidatePaths.Add(Path.Combine(Application.dataPath, "TravelButton_Cities.json")); } catch { }

            try
            {
                var cfgPath = TravelButton.ConfigFilePath;
                if (!string.IsNullOrEmpty(cfgPath) && cfgPath != "(unknown)")
                {
                    var dir = cfgPath;
                    try { if (File.Exists(cfgPath)) dir = Path.GetDirectoryName(cfgPath); } catch { }
                    if (!string.IsNullOrEmpty(dir)) candidatePaths.Add(Path.Combine(dir, "TravelButton_Cities.json"));
                }
            }
            catch { }

            try { candidatePaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "TravelButton_Cities.json")); } catch { }

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

            // If not found or parsing failed, create default file at preferred candidate path
            if (loaded == null)
            {
                var defaults = TravelConfig.Default();
                string writePath = candidatePaths.Count > 0 ? candidatePaths[0] : Path.Combine(Directory.GetCurrentDirectory(), "TravelButton_Cities.json");
                try
                {
                    if (defaults.SaveToFile(writePath))
                    {
                        LInfo("Wrote default TravelButton_Cities.json to: " + writePath);
                        loaded = defaults;
                        foundPath = writePath;
                    }
                    else
                    {
                        LWarn("Failed to write default TravelButton_Cities.json to: " + writePath);
                    }
                }
                catch (Exception ex)
                {
                    LWarn("Error writing default TravelButton_Cities.json: " + ex.Message);
                }
            }

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
                        c.price = cc.price.HasValue ? cc.price.Value : (int?)null;

                        // Do not set enabled from JSON; keep BepInEx authoritative.
                        c.enabled = false;

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
        DebugConfig.IsDebug = true;

        try { TravelButtonPlugin.Initialize(this.Logger); } catch { /* swallow */
        }

        this.Logger.LogInfo("[TravelButton] direct Logger test (should appear in LogOutput.log)");
        TBLog.Info("TravelButtonPlugin test (should appear in LogOutput.log)");

        // sanity checks to confirm BepInEx receives logs:
        TBLog.Info("[TravelButton] BepInEx Logger is available (this.Logger) - test message");

        
        // Attempt to load TravelButton_Cities.json from likely locations and populate TravelButtonMod.Cities.
        // This is a best-effort load for deterministic defaults so that other initialization steps can observe cities.
        try
        {
            TryLoadCitiesJsonIntoTravelButtonMod();
        }
        catch (Exception ex)
        {
            try { LogSource?.LogWarning(Prefix + "Failed to load TravelButton_Cities.json during Initialize: " + ex.Message); } catch { }
        }
        
        try
        {
            TBLog.Info("TravelButton: startup - loaded cities:");
            if (TravelButton.Cities == null) TBLog.Info(" - Cities == null");
            else
            {
                foreach (var c in TravelButton.Cities)
                {
                    try
                    {
                        TBLog.Info($" - '{c.name}' sceneName='{c.sceneName ?? ""}' coords=[{(c.coords != null ? string.Join(", ", c.coords) : "")}]");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("Startup city log failed: " + ex);
        }

        try
        {
            TBLog.Info("TravelButtonPlugin.Awake: plugin initializing.");
            TravelButton.InitFromConfig();
            if (TravelButton.Cities != null && TravelButton.Cities.Count > 0)
            {
                TBLog.Info($"Successfully loaded {TravelButton.Cities.Count} cities from TravelButton_Cities.json.");
            }
            else
            {
                TBLog.Warn("Failed to load cities from TravelButton_Cities.json or the file is empty.");
            }
            // Start coroutine that will attempt to initialize config safely (may call ConfigManager.Load when safe)
            StartCoroutine(TryInitConfigCoroutine());
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TravelButtonPlugin.Awake exception: " + ex);
        }

        try
        {
            // existing initialization (logger, config, etc.)
            // ensure BepInEx bindings are created (this populates bex entries and sets city runtime values)
            EnsureBepInExConfigBindings();

            // start the file watcher so external edits to the config file are detected
            StartConfigWatcher();
        }
        catch (Exception ex)
        {
            TBLog.Warn("Awake initialization failed: " + ex);
        }

        ShowPlayerNotification = (msg) =>
        {
            // enqueue to main thread if required; Show uses Unity main thread anyway
            TravelButtonNotificationUI.Show(msg, 3f);
        };
    }

    // Add OnDestroy to clean up the watcher and any resources:
    private void OnDestroy()
    {
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
            var configPath = ConfigManager.ConfigPathForLog();
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

                        // Fallback: search for a travelbutton-related cfg in the BepInEx config dir
                        if (string.IsNullOrEmpty(cfgFile))
                        {
                            try
                            {
                                var files = Directory.GetFiles(Paths.ConfigPath, "*travelbutton*.cfg", SearchOption.TopDirectoryOnly);
                                if (files != null && files.Length > 0)
                                    cfgFile = files[0];
                            }
                            catch { /* ignore */ }
                        }

                        // Final fallback: use the filename observed in logs (adjust if your install differs)
                        if (string.IsNullOrEmpty(cfgFile))
                        {
                            cfgFile = Path.Combine(Paths.ConfigPath, "cz.valheimskal.travelbutton.cfg");
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
    private void EnsureBepInExConfigBindings()
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

            // Iterate cities and create bindings
            foreach (var city in TravelButton.Cities)
            {
                try
                {
                    if (string.IsNullOrEmpty(city?.name)) continue;

                    // Special-case "Sirocco": hide the Enabled toggle (and Price) from ConfigurationManager by NOT binding any ConfigEntry.
                    // Do NOT remove lines from the cfg file — the file remains authoritative for manual edits.
                    if (string.Equals(city.name, "Sirocco", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Default in-game value: hidden/disabled. We'll override from the cfg file if a value exists.
                        city.enabled = false;

                        try
                        {
                            // Determine cfg filename (adjust if your plugin uses a different filename)
                            string cfgFile = Path.Combine(Paths.ConfigPath, "cz.valheimskal.travelbutton.cfg");

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
                        }
                        catch (Exception exRead)
                        {
                            TBLog.Warn("EnsureBepInExConfigBindings: reading Sirocco values from cfg failed: " + exRead.Message);
                        }

                        // IMPORTANT: do NOT bind any ConfigEntry for Sirocco (neither Enabled nor Price).
                        // This prevents ConfigurationManager from showing Sirocco in the in-game config GUI.
                        continue;
                    }

                    // Normal cities: create Enabled and Price bindings
                    var enabledKey = Config.Bind(section, $"{city.name}.Enabled", city.enabled,
                        new ConfigDescription($"Enable teleport destination {city.name}"));
                    bex_cityEnabled[city.name] = enabledKey;

                    int defaultPriceNormal = city.price ?? 0;
                    var priceKeyNormal = Config.Bind<int>(section, $"{city.name}.Price", defaultPriceNormal,
                        new ConfigDescription($"Price for {city.name}"));
                    bex_cityPrice[city.name] = priceKeyNormal;

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

                    // Attach SettingChanged handlers that update runtime values and refresh UI
                    {
                        var localCity = city;
                        var localEnabledKey = enabledKey;
                        localEnabledKey.SettingChanged += (s, e) =>
                        {
                            try
                            {
                                localCity.enabled = localEnabledKey.Value;
                                TBLog.Info($"EnsureBepInExConfigBindings: applied {localCity.name}.Enabled = {localEnabledKey.Value}");
                                try { TravelButtonUI.RebuildTravelDialog(); } catch { }
                                try { TravelButton.PersistCitiesToConfig(); } catch { }
                            }
                            catch (Exception ex) { TBLog.Warn("Enabled SettingChanged handler failed: " + ex.Message); }
                        };
                    }

                    {
                        var localCity = city;
                        var localPriceKey = priceKeyNormal;
                        localPriceKey.SettingChanged += (s, e) =>
                        {
                            try
                            {
                                localCity.price = localPriceKey.Value;
                                TBLog.Info($"EnsureBepInExConfigBindings: applied {localCity.name}.Price = {localPriceKey.Value}");
                                try { TravelButtonUI.RebuildTravelDialog(); } catch { }
                                try { TravelButton.PersistCitiesToConfig(); } catch { }
                            }
                            catch (Exception ex) { TBLog.Warn("Price SettingChanged handler failed: " + ex.Message); }
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
    private void BindCityConfigsForNewCities()
    {
        try
        {
            if (TravelButton.Cities == null) return;
            foreach (var city in TravelButton.Cities)
            {
                if (bex_cityEnabled.ContainsKey(city.name)) continue;
                string section = "TravelButton.Cities";
                
                var priceDefault = city.price ?? TravelButton.cfgTravelCost.Value;
                var enabledDefault = city.enabled;
                
                var enabledKey = Config.Bind(section, $"{city.name}.Enabled", enabledDefault, $"Enable teleport destination {city.name}");
//                var priceKey = Config.Bind(section, $"{city.name}.Price", priceDefault, $"Price to teleport to {city.name} (overrides global)");
                var priceKey = Config.Bind<int>(section, $"{city.name}.Price", (int)city.price,
                    new ConfigDescription($"Price for {city.name}"));

                bex_cityEnabled[city.name] = enabledKey;
                bex_cityPrice[city.name] = priceKey;

                // Log source of values
                bool priceFromJson = city.price.HasValue && city.price.Value == priceDefault;
                string priceSource = priceFromJson ? "JSON-seed" : "BepInEx";
                string enabledSource = enabledKey.Value == enabledDefault ? "default" : "BepInEx";
                
                TBLog.Info($"New city '{city.name}': enabled={enabledKey.Value} (source: {enabledSource}), price={priceKey.Value} (source: {priceSource})");

                // sync initial runtime (BepInEx is authoritative)
                city.enabled = enabledKey.Value;
                city.price = priceKey.Value;

                enabledKey.SettingChanged += (s, e) =>
                {
                    city.enabled = enabledKey.Value;
                    TravelButton.PersistCitiesToConfig();
                };
                priceKey.SettingChanged += (s, e) =>
                {
                    city.price = priceKey.Value;
                    TravelButton.PersistCitiesToConfig();
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
}

public static class TravelButton
{
    public static bool TeleportInProgress = false;

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
                TBLog.Warn("MapConfigInstanceToLocal: top-level map failed: " + ex.Message);
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
                                TBLog.Warn("MapConfigInstanceToLocal: error mapping city entry: " + inner.Message);
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
                        TBLog.Info("PersistCitiesToConfig: persisted cities into external ConfigManager.Config and called Save().");
                        break;
                    }
                }
                catch { }
            }

            if (!persisted)
            {
                TBLog.Warn("PersistCitiesToConfig: Could not persist cities because external ConfigManager not found or not writable.");
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

    // City-aware visited check with caching.
    // Uses HasPlayerVisitedFast for candidate string checks first (cheap),
    // then falls back to legacy delegate (IsCityVisitedFallback) only if needed.
    // Results are cached per-city name to avoid repeated legacy fallback calls during refresh loops.
    // Diagnostic HasPlayerVisited (temporary). Logs fallback method info and attempts to call VisitedTracker.HasVisited for comparison.
    public static bool HasPlayerVisited(TravelButton.City city)
    {
        if (city == null) return false;
        string cacheKey = city.name ?? string.Empty;
        if (string.IsNullOrEmpty(cacheKey)) return false;

        // fast cache hit
        lock (s_cityVisitedLock)
        {
            if (s_cityVisitedCache.TryGetValue(cacheKey, out bool cached))
                return cached;
        }

        bool result = false;
        try
        {
            // Build candidate set
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string s) { if (!string.IsNullOrWhiteSpace(s)) { var t = s.Trim(); candidates.Add(t); candidates.Add(t.ToLowerInvariant()); candidates.Add(t.Replace(" ", "").ToLowerInvariant()); } }
            Add(city.name); Add(city.sceneName); Add(city.targetGameObjectName);

            // Try fast string-based check first
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

            // If no fast match, call legacy fallback once (and log method info)
            if (!result)
            {
                var fallback = TravelButtonUI.IsCityVisitedFallback;
                if (fallback != null)
                {
                    try
                    {
                        var mi = fallback.Method;
                        var target = fallback.Target;
                        TBLog.Info($"HasPlayerVisited: calling legacy fallback method '{mi?.Name}' on target='{target?.GetType().FullName ?? "static"}' for city='{city.name}'");

                        // call fallback
                        bool fb = fallback(city);
                        TBLog.Info($"HasPlayerVisited: legacy fallback returned {fb} for '{city.name}'");
                        result = fb;

                        // Also attempt to call VisitedTracker.HasVisited(name) via reflection for comparison (if present)
                        try
                        {
                            var vtType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                                .FirstOrDefault(t => t.Name == "VisitedTracker");
                            if (vtType != null)
                            {
                                var hv = vtType.GetMethod("HasVisited", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase)
                                         ?? vtType.GetMethod("HasVisited", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                                if (hv != null)
                                {
                                    object vtTarget = null;
                                    if (!hv.IsStatic)
                                    {
                                        // try to find an instance
                                        vtTarget = UnityEngine.Object.FindObjectOfType(vtType);
                                    }
                                    var hvRes = hv.Invoke(vtTarget, new object[] { city.name });
                                    TBLog.Info($"HasPlayerVisited: VisitedTracker.HasVisited('{city.name}') => {hvRes}");
                                }
                                else
                                {
                                    TBLog.Info("HasPlayerVisited: VisitedTracker type found but HasVisited method not found.");
                                }
                            }
                            else
                            {
                                TBLog.Info("HasPlayerVisited: VisitedTracker type not found in loaded assemblies.");
                            }
                        }
                        catch (Exception ex2)
                        {
                            TBLog.Warn("HasPlayerVisited: reflection call to VisitedTracker.HasVisited failed: " + ex2.Message);
                        }
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

        // cache and return
        lock (s_cityVisitedLock)
        {
            try { s_cityVisitedCache[cacheKey] = result; } catch { }
        }
        return result;
    }

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

    // Build or reuse a HashSet of visited identifiers. Call once per dialog open.
    public static void PrepareVisitedLookup()
    {
        try
        {
            var saveRoot = FindSaveRootInstance();
            if (saveRoot == null)
            {
                // no save available -> clear cache so fallback behaviour can be used if desired
                lock (s_visitedLock)
                {
                    s_cachedVisitedKeys = null;
                    s_cachedSaveRootRef = null;
                }
                TBLog.Info("PrepareVisitedLookup: no save root found; cleared cached visited keys.");
                return;
            }

            // If cache already built for this exact save root object, reuse it
            lock (s_visitedLock)
            {
                if (ReferenceEquals(s_cachedSaveRootRef, saveRoot) && s_cachedVisitedKeys != null)
                {
                    // already prepared for this save root instance
                    TBLog.Info("PrepareVisitedLookup: reuse existing visited lookup cache.");
                    return;
                }

                // Build a new visited set
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1) per-character save (first char)
                try
                {
                    var charInner = GetFirstCharacterInnerSave();
                    if (charInner != null)
                    {
                        var list = GetVisitedCollectionFromSaveObject(charInner);
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                if (item == null) continue;
                                var s = item.ToString().Trim();
                                if (s.Length == 0) continue;
                                // add several normalized variants for matching
                                set.Add(s);
                                set.Add(s.ToLowerInvariant());
                                set.Add(s.Replace(" ", "").ToLowerInvariant());
                            }
                        }
                    }
                }
                catch (Exception exChar)
                {
                    TBLog.Warn("PrepareVisitedLookup: error reading per-character visited collection: " + exChar.Message);
                }

                // 2) world save
                try
                {
                    var rootType = saveRoot.GetType();
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;
                    var worldSaveProp = rootType.GetProperty("WorldSave", flags) ?? rootType.GetProperty("worldSave", flags);
                    if (worldSaveProp != null)
                    {
                        var worldSave = worldSaveProp.GetValue(saveRoot, null);
                        if (worldSave != null)
                        {
                            var list = GetVisitedCollectionFromSaveObject(worldSave);
                            if (list != null)
                            {
                                foreach (var item in list)
                                {
                                    if (item == null) continue;
                                    var s = item.ToString().Trim();
                                    if (s.Length == 0) continue;
                                    set.Add(s);
                                    set.Add(s.ToLowerInvariant());
                                    set.Add(s.Replace(" ", "").ToLowerInvariant());
                                }
                            }
                        }
                    }
                }
                catch (Exception exWorld)
                {
                    TBLog.Warn("PrepareVisitedLookup: error reading WorldSave visited collection: " + exWorld.Message);
                }

                // Save the cache and reference
                s_cachedVisitedKeys = set;
                s_cachedSaveRootRef = saveRoot;
                TBLog.Info($"PrepareVisitedLookup: built visited lookup with {s_cachedVisitedKeys.Count} entries.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PrepareVisitedLookup failed: " + ex.Message);
            lock (s_visitedLock)
            {
                s_cachedVisitedKeys = null;
                s_cachedSaveRootRef = null;
            }
        }
    }

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
    private static HashSet<string> s_visitedKeysSet = null; // normalized lowercase keys extracted from save
    private static bool s_visitedKeysSetInitialized = false;
    private static bool s_visitedTrackerReflectionFailed = false;

    /// <summary>
    /// Fast, cached visited check. Uses a one-time-built visited key set (from SaveManager / character/world save)
    /// to answer repeated queries quickly. Falls back to a single (quiet) attempt at VisitedTracker.HasVisited
    /// only if no save-based visited keys were found.
    /// </summary>
    public static bool HasPlayerVisitedFast(string cityId)
    {
        if (string.IsNullOrEmpty(cityId)) return false;

        // normalize lookup key
        string key = cityId.Trim();

        // quick cache hit
        lock (s_visitedCacheLock)
        {
            if (s_hasVisitedCache.TryGetValue(key, out bool cachedValue))
                return cachedValue;
        }

        // If visited keys set hasn't been initialized, build it once (cheap on repeated calls)
        if (!s_visitedKeysSetInitialized)
        {
            try
            {
                // Build set from SaveManager / character world save in a single pass
                s_visitedKeysSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Attempt to find save root once
                object saveRoot = null;
                try
                {
                    saveRoot = FindSaveRootInstance();
                }
                catch
                {
                    saveRoot = null;
                }

                if (saveRoot != null)
                {
                    try
                    {
                        // Try to obtain per-character visited and world visited collections in a few common shapes.
                        // Use a helper to extract IEnumerable from objects (works with arrays, IList, IEnumerable).
                        void AddItemsFromObject(object obj)
                        {
                            if (obj == null) return;
                            // If it's a string -> add as single key
                            if (obj is string s)
                            {
                                s_visitedKeysSet.Add(s.Trim());
                                return;
                            }
                            // If it's a IDictionary, add keys and/or values
                            var dict = obj as System.Collections.IDictionary;
                            if (dict != null)
                            {
                                foreach (var k in dict.Keys)
                                    if (k != null) s_visitedKeysSet.Add(k.ToString().Trim());
                                foreach (var v in dict.Values)
                                    if (v != null) s_visitedKeysSet.Add(v.ToString().Trim());
                                return;
                            }
                            // IEnumerable fallback
                            var ie = obj as System.Collections.IEnumerable;
                            if (ie != null)
                            {
                                foreach (var it in ie)
                                {
                                    if (it == null) continue;
                                    try { s_visitedKeysSet.Add(it.ToString().Trim()); } catch { }
                                }
                                return;
                            }

                            // Otherwise, reflectively try to find common fields/properties like 'visited', 'visitedLocations', 'visitedCities', etc.
                            try
                            {
                                var t = obj.GetType();
                                var candidates = new[] { "visited", "visitedLocations", "visitedCities", "visitedList", "visitedIds", "Visited" };
                                foreach (var name in candidates)
                                {
                                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                    if (f != null)
                                    {
                                        AddItemsFromObject(f.GetValue(obj));
                                    }
                                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                                    if (p != null)
                                    {
                                        AddItemsFromObject(p.GetValue(obj, null));
                                    }
                                }
                            }
                            catch { /* ignore reflective probing errors */ }
                        }

                        // Try common property names on save root
                        try
                        {
                            var srType = saveRoot.GetType();
                            var charProp = srType.GetProperty("CharacterSave", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                         ?? srType.GetProperty("characterSave", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

                            if (charProp != null)
                            {
                                var charSave = charProp.GetValue(saveRoot, null);
                                AddItemsFromObject(charSave);
                            }

                            // world save
                            var worldProp = srType.GetProperty("WorldSave", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                         ?? srType.GetProperty("worldSave", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                            if (worldProp != null)
                            {
                                var worldSave = worldProp.GetValue(saveRoot, null);
                                AddItemsFromObject(worldSave);
                            }

                            // generic fields - scan for anything that looks like a visited collection
                            var allProps = srType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                            foreach (var p in allProps)
                            {
                                if (p.PropertyType == typeof(string) || typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))
                                {
                                    try { AddItemsFromObject(p.GetValue(saveRoot, null)); } catch { }
                                }
                            }
                        }
                        catch { /* ignore saveRoot reflection errors */ }
                    }
                    catch { /* ignore */ }
                }

                // Mark as initialized even if set is empty (prevents repeated expensive attempts)
                s_visitedKeysSetInitialized = true;
            }
            catch (Exception ex)
            {
                TBLog.Warn("HasPlayerVisitedFast: failed to initialize visited keys set: " + ex.Message);
                // ensure we don't retry too aggressively
                s_visitedKeysSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                s_visitedKeysSetInitialized = true;
            }
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
                // Also try matching against common city identifiers: name, sceneName and known variants.
                // If caller passed a city name, we may also try lower-case/underscore variants.
                string keyLower = key.ToLowerInvariant();

                // direct substring match: sometimes visited keys contain the city name as part of string
                if (s_visitedKeysSet != null)
                {
                    foreach (var k in s_visitedKeysSet)
                    {
                        if (string.IsNullOrEmpty(k)) continue;
                        try
                        {
                            var kl = k.ToLowerInvariant();
                            if (kl == keyLower || kl.Contains(keyLower) || keyLower.Contains(kl))
                            {
                                result = true;
                                break;
                            }
                        }
                        catch { /* ignore string errors */ }
                    }
                }

                // As a fallback, if the key looks like a sceneName or has underscores, try replacing underscores/spaces
                if (!result)
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

        // Log only sparse diagnostics (not for every call)
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
        lock (s_visitedCacheLock)
        {
            s_hasVisitedCache.Clear();
        }
        s_visitedKeysSetInitialized = false;
        s_visitedKeysSet = null;
        s_visitedTrackerReflectionFailed = false;
        TBLog.Info("ClearVisitedCache: cleared visited caches.");
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

    // Improved matching & debug helpers for visited detection.
    // Paste into same class where HasPlayerVisitedFast / HasPlayerVisited live.

    private static string NormalizeVisitedKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // Lower-case
        s = s.ToLowerInvariant().Trim();
        // Remove common separators and punctuation
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s_\-\.\:\/\\]+", "");
        // Remove long numeric tokens likely to be timestamps/ids (8+ digits)
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\d{6,}", "");
        // Remove any non-alphanumeric leftover
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9]", "");
        return s;
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

}
