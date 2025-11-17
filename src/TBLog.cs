using System;

/// Small wrapper to gate TravelButtonPlugin's logging behind DebugConfig.IsDebug.
/// Use TBLog.Info(...) and TBLog.Warn(...) throughout the codebase.
public static class TBLog
{
    public static void Info(string message)
    {
        try
        {
            if (DebugConfig.IsDebug)
                TravelButtonPlugin.LogInfo(message);
        }
        catch (Exception) { /* swallow to avoid logging crash */ }
    }

    public static void Warn(string message)
    {
        try
        {
            // Keep warnings visible even if DebugConfig.IsDebug==false, but optionally
            // you can gate warnings as well; for now call LogWarning unconditionally.
            TravelButtonPlugin.LogWarning(message);
        }
        catch (Exception) { /* swallow */ }
    }

    // Optional helper for unconditional logging (bypass debug gate)
    public static void ForceInfo(string message)
    {
        try { TravelButtonPlugin.LogInfo(message); } catch { }
    }

    // Debug-level logging action. Null if debug logging is disabled.
    // Use: TBLog.Debug?.Invoke("message") for extra diagnostic logging.
    public static Action<string> Debug
    {
        get
        {
            try
            {
                if (DebugConfig.IsDebug)
                    return (msg) => TravelButtonPlugin.LogInfo("[DEBUG] " + msg);
            }
            catch { }
            return null;
        }
    }
}