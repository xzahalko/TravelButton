using System;
using System.IO;
using UnityEngine;

// ConfigManager: Minimal compatibility stub
// The actual configuration is now handled by TravelConfig in CityConfig.cs

public static class ConfigManager
{
    // Runtime-held config instance (now returns null - deprecated)
    public static TravelConfig Config { get; set; } = null;

    public static string ConfigPathForLog()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            return Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities.json");
        }
        catch
        {
            return "(unknown)";
        }
    }

    // Deprecated - configuration is now handled via TravelConfig.LoadFromFile
    public static void Load()
    {
        try
        {
            TBLog.Info("ConfigManager.Load() called (deprecated; using TravelConfig instead)");
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            TBLog.Info("ConfigManager.Save() called (deprecated; using TravelConfig instead)");
        }
        catch { }
    }

    // Return null to indicate this ConfigManager is deprecated
    public static TravelConfig Default()
    {
        return null;
    }
}
