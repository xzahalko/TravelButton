using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

//
// CityConfigDTO.cs
// - DTOs for TravelButton_Cities.json: JsonCityConfig (per-city) and JsonTravelConfig (root).
// - JSON load/save helpers using Unity's JsonUtility (no Newtonsoft dependency).
// - JsonTravelConfig.Default() provides default cities when JSON is missing.
//
// NOTE: JsonCityConfig does NOT include 'enabled' or 'price' fields - those are managed by BepInEx config.
//       JSON only contains city metadata: coords, targetGameObjectName, sceneName, desc.
//

/// <summary>
/// Per-city configuration DTO for TravelButton_Cities.json.
/// Represents metadata loaded from JSON file.
/// Does NOT include 'enabled' or 'price' fields - those are managed by BepInEx config.
/// </summary>
[Serializable]
public class JsonCityConfig
{
    public string name;
    public float[] coords;              // [x, y, z] teleport coordinates
    public string targetGameObjectName; // GameObject name to find in scene
    public string sceneName;            // Unity scene name
    public string desc;                 // Optional description

    public JsonCityConfig() { }

    public JsonCityConfig(string name)
    {
        this.name = name;
    }
}

/// <summary>
/// Root configuration DTO for TravelButton_Cities.json.
/// Contains a list of JsonCityConfig objects.
/// </summary>
[Serializable]
public class JsonTravelConfig
{
    public List<JsonCityConfig> cities = new List<JsonCityConfig>();

    /// <summary>
    /// Returns a default JsonTravelConfig with predefined cities.
    /// Used when TravelButton_Cities.json does not exist.
    /// </summary>
    public static JsonTravelConfig Default()
    {
        var config = new JsonTravelConfig();
        config.cities = new List<JsonCityConfig>
        {
            new JsonCityConfig("Cierzo")
            {
                coords = new float[] { 1410.388f, 6.786f, 1665.642f },
                targetGameObjectName = "Cierzo",
                sceneName = "CierzoNewTerrain",
                desc = "Starting city in the Chersonese region"
            },
            new JsonCityConfig("Levant")
            {
                coords = new float[] { -55.212f, 10.056f, 79.379f },
                targetGameObjectName = "WarpLocation_HM",
                sceneName = "Levant",
                desc = "Desert city in the Abrassar region"
            },
            new JsonCityConfig("Monsoon")
            {
                coords = new float[] { 61.553f, -3.743f, 167.599f },
                targetGameObjectName = "Monsoon_Location",
                sceneName = "Monsoon",
                desc = "Coastal city in the Hallowed Marsh"
            },
            new JsonCityConfig("Berg")
            {
                coords = new float[] { 1202.414f, -13.071f, 1378.836f },
                targetGameObjectName = "Berg",
                sceneName = "Berg",
                desc = "Mountain city in the Enmerkar Forest"
            },
            new JsonCityConfig("Harmattan")
            {
                coords = new float[] { 93.757f, 65.474f, 767.849f },
                targetGameObjectName = "Harmattan_Location",
                sceneName = "Harmattan",
                desc = "Desert city in the Antique Plateau"
            },
            new JsonCityConfig("Sirocco")
            {
                coords = new float[] { 600.0f, 1.2f, -300.0f },
                targetGameObjectName = "Sirocco_Location",
                sceneName = "NewSirocco",
                desc = "Caldera settlement"
            }
        };
        return config;
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
            // This is a best-effort approach to allow documentation in JSON files
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
            var sb = new StringBuilder();
            sb.AppendLine("// TravelButton_Cities.json");
            sb.AppendLine("// Schema: { \"cities\": [ { \"name\": \"...\", \"coords\": [x,y,z], \"targetGameObjectName\": \"...\", \"sceneName\": \"...\", \"desc\": \"...\" }, ... ] }");
            sb.AppendLine("// Note: 'enabled' and 'price' are managed by BepInEx config, not this file.");
            sb.AppendLine();

            // Use Unity's JsonUtility for serialization
            string json = JsonUtility.ToJson(this, prettyPrint: true);
            sb.Append(json);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            TBLog.Info($"SaveToJson: wrote TravelButton_Cities.json to {path}");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"SaveToJson failed for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempt to find and load TravelButton_Cities.json from common locations.
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
    /// Get candidate file paths for TravelButton_Cities.json in priority order.
    /// </summary>
    private static List<string> GetCandidatePaths()
    {
        var paths = new List<string>();

        // BepInEx config directory
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (!string.IsNullOrEmpty(baseDir))
            {
                paths.Add(Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities.json"));
                paths.Add(Path.Combine(baseDir, "config", "TravelButton_Cities.json"));
            }
        }
        catch { }

        // Plugin assembly directory
        try
        {
            var asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                paths.Add(Path.Combine(asmDir, "TravelButton_Cities.json"));
            }
        }
        catch { }

        // Current working directory
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrEmpty(cwd))
            {
                paths.Add(Path.Combine(cwd, "TravelButton_Cities.json"));
            }
        }
        catch { }

        // Unity data path
        try
        {
            var dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                paths.Add(Path.Combine(dataPath, "TravelButton_Cities.json"));
            }
        }
        catch { }

        return paths;
    }

    /// <summary>
    /// Get the best candidate path for writing TravelButton_Cities.json.
    /// Prefers BepInEx config dir, then plugin folder, then CWD.
    /// </summary>
    public static string GetPreferredWritePath()
    {
        // Prefer BepInEx config directory
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (!string.IsNullOrEmpty(baseDir))
            {
                var configDir = Path.Combine(baseDir, "BepInEx", "config");
                if (Directory.Exists(configDir))
                {
                    return Path.Combine(configDir, "TravelButton_Cities.json");
                }
                // Try to create the config dir if BepInEx folder exists
                var bepInExDir = Path.Combine(baseDir, "BepInEx");
                if (Directory.Exists(bepInExDir))
                {
                    try
                    {
                        Directory.CreateDirectory(configDir);
                        return Path.Combine(configDir, "TravelButton_Cities.json");
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
                return Path.Combine(asmDir, "TravelButton_Cities.json");
            }
        }
        catch { }

        // Last resort: current working directory
        return Path.Combine(Directory.GetCurrentDirectory(), "TravelButton_Cities.json");
    }
}
