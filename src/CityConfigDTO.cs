using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;

[Serializable]
public class JsonCityConfig
{
    public string name;
    public int? price;
    public float[] coords;
    public string targetGameObjectName;
    public string sceneName;
    public string desc;
    public bool visited = false;

    public JsonCityConfig() { }
    public JsonCityConfig(string name) { this.name = name; }
}

[Serializable]
public class JsonTravelConfig
{
    // ensure non-null list so JsonUtility serializes an array (not an empty object)
    public List<JsonCityConfig> cities = new List<JsonCityConfig>();

    public static JsonTravelConfig Default()
    {
        var result = new JsonTravelConfig();
        TBLog.Info("JsonTravelConfig.Default: invoked to build defaults.");

        try
        {
            object defaultsObj = null;

            // Try direct call first (if accessible)
            try
            {
                defaultsObj = ConfigManager.Default();
                TBLog.Info("JsonTravelConfig.Default: ConfigManager.Default() called directly.");
            }
            catch (Exception exDirect)
            {
                TBLog.Info("JsonTravelConfig.Default: direct call to ConfigManager.Default() failed: " + exDirect.Message);
                defaultsObj = null;
            }

            // Fallback reflective call
            if (defaultsObj == null)
            {
                try
                {
                    var cmType = Type.GetType("ConfigManager");
                    if (cmType != null)
                    {
                        var mi = cmType.GetMethod("Default", BindingFlags.Public | BindingFlags.Static);
                        if (mi != null)
                        {
                            defaultsObj = mi.Invoke(null, null);
                            TBLog.Info("JsonTravelConfig.Default: obtained defaults via reflection from ConfigManager.Default().");
                        }
                    }
                }
                catch (Exception exRef)
                {
                    TBLog.Warn("JsonTravelConfig.Default: reflection call failed: " + exRef);
                    defaultsObj = null;
                }
            }

            if (defaultsObj == null)
            {
                TBLog.Warn("JsonTravelConfig.Default: no defaults object available from ConfigManager.Default(); returning empty list.");
                return result;
            }

            // Obtain 'cities' member (field or property) via reflection
            object citiesVal = null;
            try
            {
                var t = defaultsObj.GetType();
                var prop = t.GetProperty("cities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (prop != null) citiesVal = prop.GetValue(defaultsObj);
                else
                {
                    var field = t.GetField("cities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                    if (field != null) citiesVal = field.GetValue(defaultsObj);
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("JsonTravelConfig.Default: failed to get 'cities' member from defaultsObj: " + ex);
                citiesVal = null;
            }

            if (citiesVal == null)
            {
                TBLog.Warn("JsonTravelConfig.Default: 'cities' member null on defaults object; returning empty list.");
                return result;
            }

            // Map dictionary keyed entries
            if (citiesVal is IDictionary dict)
            {
                foreach (DictionaryEntry de in dict)
                {
                    try
                    {
                        string key = de.Key as string;
                        var src = de.Value;
                        if (string.IsNullOrEmpty(key))
                            key = TryGetStringFromObject(src, "name") ?? TryGetStringFromObject(src, "Name") ?? "<unknown>";

                        var jc = new JsonCityConfig(key)
                        {
                            price = TryGetIntFromObject(src, "price"),
                            coords = TryGetFloatArrayFromObject(src, "coords"),
                            targetGameObjectName = TryGetStringFromObject(src, "targetGameObjectName") ?? TryGetStringFromObject(src, "target"),
                            sceneName = TryGetStringFromObject(src, "sceneName") ?? TryGetStringFromObject(src, "scene"),
                            desc = TryGetStringFromObject(src, "desc") ?? TryGetStringFromObject(src, "description"),
                            visited = TryGetBoolFromObject(src, "visited") ?? false
                        };
                        result.cities.Add(jc);
                    }
                    catch { /* skip problematic entry */ }
                }

                TBLog.Info($"JsonTravelConfig.Default: mapped {result.cities.Count} cities from dictionary defaults.");
                return result;
            }

            // Map list/enumerable entries
            if (citiesVal is IEnumerable ie)
            {
                int mapped = 0;
                foreach (var item in ie)
                {
                    try
                    {
                        if (item == null) continue;
                        string name = TryGetStringFromObject(item, "name") ?? TryGetStringFromObject(item, "Name") ?? item.ToString();
                        var jc = new JsonCityConfig(name ?? string.Empty)
                        {
                            price = TryGetIntFromObject(item, "price"),
                            coords = TryGetFloatArrayFromObject(item, "coords"),
                            targetGameObjectName = TryGetStringFromObject(item, "targetGameObjectName") ?? TryGetStringFromObject(item, "target"),
                            sceneName = TryGetStringFromObject(item, "sceneName") ?? TryGetStringFromObject(item, "scene"),
                            desc = TryGetStringFromObject(item, "desc") ?? TryGetStringFromObject(item, "description"),
                            visited = TryGetBoolFromObject(item, "visited") ?? false
                        };
                        result.cities.Add(jc);
                        mapped++;
                    }
                    catch { }
                }

                TBLog.Info($"JsonTravelConfig.Default: mapped {mapped} cities from enumerable defaults (total {result.cities.Count}).");
                return result;
            }

            TBLog.Warn("JsonTravelConfig.Default: 'cities' member exists but is not IDictionary or IEnumerable; returning whatever was collected.");
            return result;
        }
        catch (Exception ex)
        {
            TBLog.Warn("JsonTravelConfig.Default: unexpected error: " + ex);
            return result;
        }
    }

    private static int? TryGetIntFromObject(object obj, string propName)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        try
        {
            var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (p != null && p.CanRead)
            {
                var v = p.GetValue(obj);
                if (v is int i) return i;
                try { return Convert.ToInt32(v); } catch { }
            }
            var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null)
            {
                var v = f.GetValue(obj);
                if (v is int i2) return i2;
                try { return Convert.ToInt32(v); } catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool? TryGetBoolFromObject(object obj, string propName)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        try
        {
            var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (p != null && p.CanRead)
            {
                var v = p.GetValue(obj);
                if (v is bool b) return b;
                if (v != null) { if (bool.TryParse(v.ToString(), out var parsed)) return parsed; }
            }
            var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null)
            {
                var v = f.GetValue(obj);
                if (v is bool b2) return b2;
                if (v != null) { if (bool.TryParse(v.ToString(), out var parsed2)) return parsed2; }
            }
        }
        catch { }
        return null;
    }

    private static string TryGetStringFromObject(object obj, string propName)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        try
        {
            var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (p != null && p.CanRead && p.PropertyType == typeof(string)) return p.GetValue(obj) as string;
            var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (f != null && f.FieldType == typeof(string)) return f.GetValue(obj) as string;
        }
        catch { }
        return null;
    }

    private static float[] TryGetFloatArrayFromObject(object obj, string propName)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        try
        {
            var p = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            object val = null;
            if (p != null && p.CanRead) val = p.GetValue(obj);
            else
            {
                var f = t.GetField(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null) val = f.GetValue(obj);
            }
            if (val == null) return null;
            if (val is float[] fa) return fa;
            if (val is IEnumerable e)
            {
                var list = new List<float>();
                foreach (var it in e)
                {
                    try { list.Add(Convert.ToSingle(it)); } catch { }
                }
                if (list.Count > 0) return list.ToArray();
            }
        }
        catch { }
        return null;
    }

    public static JsonTravelConfig LoadFromJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;

            // strip comment lines starting with //
            var lines = new List<string>();
            foreach (var line in json.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("//")) lines.Add(line);
            }
            json = string.Join("\n", lines);

            var config = JsonUtility.FromJson<JsonTravelConfig>(json);
            if (config != null && config.cities != null) return config;
            return null;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"LoadFromJson failed for {path}: {ex.Message}");
            return null;
        }
    }

    public void SaveToJson(string path)
    {
        try
        {
            TBLog.Info($"JsonTravelConfig.SaveToJson: begin write to '{path}'");

            // Report existing target file info (if any)
            try
            {
                if (File.Exists(path))
                {
                    var fiOld = new FileInfo(path);
                    TBLog.Info($"JsonTravelConfig.SaveToJson: existing file detected. FullName='{fiOld.FullName}', Length={fiOld.Length}, LastWriteTime={fiOld.LastWriteTimeUtc:O}");
                }
                else
                {
                    TBLog.Info("JsonTravelConfig.SaveToJson: no existing file at target path.");
                }
            }
            catch (Exception exInfo)
            {
                TBLog.Warn("JsonTravelConfig.SaveToJson: failed querying existing file info: " + exInfo);
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (this.cities == null) this.cities = new List<JsonCityConfig>();

            // Log per-city summary
            try
            {
                TBLog.Info($"JsonTravelConfig.SaveToJson: cities.Count = {this.cities.Count}");
                int i = 0;
                foreach (var c in this.cities)
                {
                    if (c == null)
                    {
                        TBLog.Info($"  city[{i}]: <null>");
                    }
                    else
                    {
                        string coords = "<null>";
                        try { coords = c.coords != null ? $"[{string.Join(", ", c.coords)}]" : "<null>"; } catch { coords = "<err>"; }
                        TBLog.Info($"  city[{i}] name='{c.name ?? ""}' scene='{c.sceneName ?? ""}' coords={coords} price={c.price} visited={c.visited}");
                    }
                    i++;
                    if (i >= 50)
                    {
                        TBLog.Info("  ...stopping city listing after 50 entries");
                        break;
                    }
                }
            }
            catch (Exception exCities)
            {
                TBLog.Warn("JsonTravelConfig.SaveToJson: failed to enumerate cities for debug: " + exCities);
            }

            // Callers stack (kept)
            try
            {
                int maxFrames = 12;
                var st = new System.Diagnostics.StackTrace(1, true);
                var frames = st.GetFrames();
                var sbStack = new System.Text.StringBuilder();
                sbStack.AppendLine($"JsonTravelConfig.SaveToJson: writing {this.cities.Count} entries to {path}. Callers:");
                if (frames != null)
                {
                    int count = Math.Min(maxFrames, frames.Length);
                    for (int j = 0; j < count; j++)
                    {
                        var f = frames[j];
                        var method = f.GetMethod();
                        var declaringType = method?.DeclaringType;
                        string methodName = declaringType != null ? declaringType.FullName + "." + method.Name : method?.Name ?? "<unknown>";
                        string file = f.GetFileName();
                        int line = f.GetFileLineNumber();
                        sbStack.AppendLine($"  at {methodName} (file: {file ?? "<unknown>"}:{(line > 0 ? line.ToString() : "0")})");
                    }
                    if (frames.Length > maxFrames) sbStack.AppendLine($"  ... ({frames.Length - maxFrames} more frames)");
                }
                else sbStack.AppendLine("  <no stack frames available>");
                TBLog.Info(sbStack.ToString());
            }
            catch (Exception exStack)
            {
                TBLog.Warn("JsonTravelConfig.SaveToJson: failed to build trimmed stack trace: " + exStack.Message);
                TBLog.Info("JsonTravelConfig.SaveToJson full stack:\n" + Environment.StackTrace);
            }

            string fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";

            // Use Newtonsoft.Json here instead of Unity JsonUtility because JsonUtility doesn't serialize properties
            string json;
            try
            {
                json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception exSer)
            {
                TBLog.Warn("JsonTravelConfig.SaveToJson: Newtonsoft serialization failed, falling back to JsonUtility: " + exSer);
                json = UnityEngine.JsonUtility.ToJson(this, prettyPrint: true) ?? "{}";
            }

            // Create full content with headers
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// " + fileName);
            sb.AppendLine("// Schema: { \"cities\": [ { \"name\": \"...\", \"coords\": [x,y,z], \"targetGameObjectName\": \"...\", \"sceneName\": \"...\", \"desc\": \"...\" }, ... ] }");
            sb.AppendLine("// Note: 'enabled' and 'price' are managed by BepInEx config, not this file.");
            sb.AppendLine();
            sb.Append(json);

            // Log JSON payload size and preview
            try
            {
                int jsonLen = sb.Length;
                TBLog.Info($"JsonTravelConfig.SaveToJson: serialized JSON length = {jsonLen} chars");
                int previewLen = Math.Min(1024, jsonLen);
                if (previewLen > 0)
                {
                    string preview = sb.ToString(0, previewLen).Replace("\r\n", "\\n");
                    TBLog.Info($"JsonTravelConfig.SaveToJson: JSON preview (first {previewLen} chars):\n{preview}");
                }
            }
            catch (Exception exPreview)
            {
                TBLog.Warn("JsonTravelConfig.SaveToJson: failed to log JSON preview: " + exPreview);
            }

            // Write to disk
            try
            {
                File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch (Exception exWrite)
            {
                TBLog.Warn($"JsonTravelConfig.SaveToJson: File.WriteAllText failed for {path}: {exWrite}");
                throw;
            }

            // Post-write verification
            try
            {
                var fi = new FileInfo(path);
                TBLog.Info($"SaveToJson: wrote {Path.GetFileName(path)} to {path} (length={fi.Length}, lastWriteUtc={fi.LastWriteTimeUtc:O})");
            }
            catch (Exception exAfter)
            {
                TBLog.Warn("JsonTravelConfig.SaveToJson: failed to stat file after write: " + exAfter);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"SaveToJson failed for {path}: {ex}");
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
        string fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";

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
        string fileName = TravelButtonPlugin.CitiesJsonFileName ?? "TravelButton_Cities.json";

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