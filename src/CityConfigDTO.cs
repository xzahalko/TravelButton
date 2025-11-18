using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

//
// CityConfigDTO.cs
// - DTOs for the cities JSON: JsonCityConfig (per-city) and JsonTravelConfig (root).
// - JSON load/save helpers using Unity's JsonUtility (no Newtonsoft dependency).
//
// IMPORTANT:
// - This file is a pure DTO + JSON helpers. It MUST NOT contain seeded default city data.
// - Canonical seeded defaults belong only in ConfigManager.Default() (ConfigManager.cs).
// - Filename is not hard-coded here; this file uses TravelButtonPlugin.CitiesJsonFileName for the filename constant.
//

/// <summary>
/// Per-city configuration DTO for the cities JSON.
/// Represents metadata loaded from JSON file.
/// Does NOT include 'enabled' or 'price' fields - those are managed by BepInEx config.
/// </summary>
[Serializable]
public class JsonCityConfig
{
    public string name;
    public int? price;                  // Price can be null to indicate "use global/default"
    public float[] coords;              // [x, y, z] teleport coordinates
    public string targetGameObjectName; // GameObject name to find in scene
    public string sceneName;            // Unity scene name
    public string desc;                 // Optional description
    public bool visited = false;        // persisted visited flag (single field is used for JSON)

    public JsonCityConfig() { }

    public JsonCityConfig(string name)
    {
        this.name = name;
    }
}

/// <summary>
/// Root configuration DTO for the cities JSON.
/// Contains a list of JsonCityConfig objects.
/// </summary>
[Serializable]
public class JsonTravelConfig
{
    public List<JsonCityConfig> cities = new List<JsonCityConfig>();

    /// <summary>
    /// Returns an empty JsonTravelConfig.
    /// Do NOT seed defaults here — canonical defaults live in ConfigManager.Default().
    /// </summary>
    public static JsonTravelConfig Default()
    {
        // Intentionally return an empty config. Seeded defaults must come from ConfigManager.Default().
        return new JsonTravelConfig();
    }

    /// <summary>
    /// Load JsonTravelConfig from JSON file at the specified path.
    /// Returns null if file does not exist or cannot be parsed.
    /// Strips lines starting with // to allow for documentation comments.
    /// </summary>
    public static JsonTravelConfig LoadFromJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            // Remove comment lines (starting with //) before parsing
            var lines = new List<string>();
            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.TrimStart();
                // Only remove lines that start with // (not // inside a string value)
                if (!trimmed.StartsWith("//"))
                {
                    lines.Add(line);
                }
            }
            json = string.Join("\n", lines);

            // Use Unity's JsonUtility for simple parsing
            var config = JsonUtility.FromJson<JsonTravelConfig>(json);
            if (config != null && config.cities != null)
            {
                return config;
            }

            return null;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"LoadFromJson failed for {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Save this JsonTravelConfig to JSON file at the specified path.
    /// Creates parent directories if needed.
    /// </summary>
    public void SaveToJson(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Add a header comment to the JSON file (using a workaround since JSON doesn't support comments)
            string fileName = TravelButtonPlugin.CitiesJsonFileName;
            var sb = new StringBuilder();
            sb.AppendLine("// " + fileName);
            sb.AppendLine("// Schema: { \"cities\": [ { \"name\": \"...\", \"coords\": [x,y,z], \"targetGameObjectName\": \"...\", \"sceneName\": \"...\", \"desc\": \"...\" }, ... ] }");
            sb.AppendLine("// Note: 'enabled' and 'price' are managed by BepInEx config, not this file.");
            sb.AppendLine();

            // Use Unity's JsonUtility for serialization
            string json = JsonUtility.ToJson(this, prettyPrint: true);
            sb.Append(json);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            TBLog.Info($"SaveToJson: wrote {Path.GetFileName(path)} to {path}");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"SaveToJson failed for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempt to find and load the cities JSON from common locations.
    /// Returns null if not found in any candidate path.
    /// </summary>
    public static JsonTravelConfig TryLoadFromCommonLocations()
    {
        var candidatePaths = GetCandidatePaths();
        foreach (var path in candidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var config = LoadFromJson(path);
                    if (config != null)
                    {
                        TBLog.Info($"TryLoadFromCommonLocations: loaded from {path}");
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TryLoadFromCommonLocations: error checking {path}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// Get candidate file paths for the cities JSON in priority order.
    /// Uses TravelButtonPlugin.CitiesJsonFileName for the filename.
    /// </summary>
    private static List<string> GetCandidatePaths()
    {
        var paths = new List<string>();
        string fileName = TravelButtonPlugin.CitiesJsonFileName;

        // BepInEx config directory
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (!string.IsNullOrEmpty(baseDir))
            {
                paths.Add(Path.Combine(baseDir, "BepInEx", "config", fileName));
                paths.Add(Path.Combine(baseDir, "config", fileName));
            }
        }
        catch { }

        // Plugin assembly directory
        try
        {
            var asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                paths.Add(Path.Combine(asmDir, fileName));
            }
        }
        catch { }

        // Current working directory
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrEmpty(cwd))
            {
                paths.Add(Path.Combine(cwd, fileName));
            }
        }
        catch { }

        // Unity data path
        try
        {
            var dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                paths.Add(Path.Combine(dataPath, fileName));
            }
        }
        catch { }

        return paths;
    }

    /// <summary>
    /// Get the best candidate path for writing the cities JSON.
    /// Prefers BepInEx config dir, then plugin folder, then CWD.
    /// Uses TravelButtonPlugin.CitiesJsonFileName for filename.
    /// </summary>
    public static string GetPreferredWritePath()
    {
        string fileName = TravelButtonPlugin.CitiesJsonFileName;

        // Prefer BepInEx config directory
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (!string.IsNullOrEmpty(baseDir))
            {
                var configDir = Path.Combine(baseDir, "BepInEx", "config");
                if (Directory.Exists(configDir))
                {
                    return Path.Combine(configDir, fileName);
                }
                // Try to create the config dir if BepInEx folder exists
                var bepInExDir = Path.Combine(baseDir, "BepInEx");
                if (Directory.Exists(bepInExDir))
                {
                    try
                    {
                        Directory.CreateDirectory(configDir);
                        return Path.Combine(configDir, fileName);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Fallback to plugin assembly directory
        try
        {
            var asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                return Path.Combine(asmDir, fileName);
            }
        }
        catch { }

        // Last resort: current working directory
        return Path.Combine(Directory.GetCurrentDirectory(), fileName);
    }
}