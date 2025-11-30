using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Helpers to obtain an exact player position (transform.position) from the active Unity scene.
/// Use this when you need the authoritative Unity world coordinates for teleportation or checks.
/// 
/// Usage:
///   if (PlayerPositionExact.TryGetExactPlayerWorldPosition(out Vector3 playerPos)) { /* use playerPos */ }
/// </summary>
public static class PlayerPositionExact
{
    /// <summary>
    /// Attempt to find the player's GameObject / Transform and return its transform.position.
    /// This prefers direct transform lookup (tag/name/prefab heuristics) and also tries well-known runtime types
    /// (LocalPlayer, PlayerCharacter, PlayerEntity) via reflection if present. The returned position is the
    /// exact Unity world coordinates (transform.position) used by teleportation code.
    /// </summary>
    public static bool TryGetExactPlayerWorldPosition(out Vector3 pos)
    {
        pos = Vector3.zero;

        try
        {
            // 1) Common fast path: look for GameObject tagged "Player"
            try
            {
                var tagged = GameObject.FindWithTag("Player");
                if (IsValidPlayerObject(tagged))
                {
                    pos = tagged.transform.position;
                    return true;
                }
            }
            catch { /* ignore tag lookup exceptions */ }

            // 2) Look for common name patterns used in Outward / mods: "PlayerChar...", "Player", "LocalPlayer"
            try
            {
                var all = GameObject.FindObjectsOfType<GameObject>();
                var byName = all.FirstOrDefault(g =>
                    !string.IsNullOrEmpty(g.name) &&
                    (g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)
                     || g.name.StartsWith("Player", StringComparison.OrdinalIgnoreCase)
                     || g.name.IndexOf("LocalPlayer", StringComparison.OrdinalIgnoreCase) >= 0));
                if (IsValidPlayerObject(byName))
                {
                    pos = byName.transform.position;
                    return true;
                }

                // Prefer an object that contains a CharacterController (very likely the player)
                var byController = all.FirstOrDefault(g => g.GetComponentInChildren<CharacterController>(true) != null);
                if (IsValidPlayerObject(byController))
                {
                    pos = byController.transform.position;
                    return true;
                }

                // Fallback: object that has a Camera child and a Rigidbody (common player prefab)
                var byCamRb = all.FirstOrDefault(g => g.GetComponentInChildren<Camera>(true) != null && g.GetComponentInChildren<Rigidbody>(true) != null);
                if (IsValidPlayerObject(byCamRb))
                {
                    pos = byCamRb.transform.position;
                    return true;
                }
            }
            catch { /* ignore enumeration errors */ }

            // 3) Try well-known runtime types via reflection and extract transform or position
            string[] candidateTypes = new[] { "LocalPlayer", "PlayerCharacter", "PlayerEntity", "PlayerController" };
            foreach (var typeName in candidateTypes)
            {
                try
                {
                    var t = GetTypeByName(typeName);
                    if (t == null) continue;

                    // static Instance field or property
                    object instance = null;
                    var f = t.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) instance = f.GetValue(null);

                    var p = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instance == null && p != null && p.CanRead)
                    {
                        try { instance = p.GetValue(null, null); } catch { instance = null; }
                    }

                    // other static properties returning same type
                    if (instance == null)
                    {
                        var staticProps = t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        foreach (var sp in staticProps)
                        {
                            if (!sp.CanRead) continue;
                            if (sp.PropertyType != t) continue;
                            try { instance = sp.GetValue(null, null); } catch { instance = null; }
                            if (instance != null) break;
                        }
                    }

                    if (instance != null)
                    {
                        if (TryExtractPositionFromObject(instance, out pos))
                            return true;
                    }

                    // If no static instance, try any live component of that type
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0)
                    {
                        var comp = objs[0];
                        if (TryExtractPositionFromObject(comp, out pos))
                            return true;
                    }
                }
                catch { /* ignore reflection errors */ }
            }

            // 4) Fallback: main camera position (not ideal - camera may be offset), but return it if present
            try
            {
                if (Camera.main != null)
                {
                    pos = Camera.main.transform.position;
                    return true;
                }
            }
            catch { /* ignore camera issues */ }
        }
        catch { /* swallow any unexpected errors to avoid crashing the game */ }

        return false;
    }

    // Return true for objects that are non-null and have a transform
    private static bool IsValidPlayerObject(GameObject go)
    {
        if (go == null) return false;
        try { return go.transform != null; } catch { return false; }
    }

    // Extract a position from an arbitrary instance: prefer transform.position, then vector properties/fields.
    // Returns true and writes pos when successful.
    private static bool TryExtractPositionFromObject(object obj, out Vector3 pos)
    {
        pos = Vector3.zero;
        if (obj == null) return false;

        try
        {
            // If it's a Transform / GameObject / Component
            if (obj is Transform t)
            {
                pos = t.position;
                return true;
            }
            if (obj is GameObject go)
            {
                if (go.transform != null) { pos = go.transform.position; return true; }
            }
            if (obj is Component comp)
            {
                if (comp.transform != null) { pos = comp.transform.position; return true; }
            }

            var type = obj.GetType();

            // transform property/field
            var tfProp = type.GetProperty("transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (tfProp != null)
            {
                try
                {
                    var tf = tfProp.GetValue(obj, null) as Transform;
                    if (tf != null) { pos = tf.position; return true; }
                }
                catch { }
            }
            var tfField = type.GetField("transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (tfField != null)
            {
                try
                {
                    var tf = tfField.GetValue(obj) as Transform;
                    if (tf != null) { pos = tf.position; return true; }
                }
                catch { }
            }

            // common Vector3 property/field names
            var posProp = type.GetProperty("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                          ?? type.GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                          ?? type.GetProperty("WorldPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (posProp != null && posProp.PropertyType == typeof(Vector3))
            {
                try { pos = (Vector3)posProp.GetValue(obj, null); return true; } catch { }
            }

            var posField = type.GetField("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                           ?? type.GetField("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                           ?? type.GetField("WorldPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (posField != null && posField.FieldType == typeof(Vector3))
            {
                try { pos = (Vector3)posField.GetValue(obj); return true; } catch { }
            }

            // coords float[] property/field
            var coordsProp = type.GetProperty("coords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? type.GetProperty("Coords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (coordsProp != null)
            {
                try
                {
                    var val = coordsProp.GetValue(obj, null);
                    if (val is float[] fa && fa.Length >= 3) { pos = new Vector3(fa[0], fa[1], fa[2]); return true; }
                }
                catch { }
            }
            var coordsField = type.GetField("coords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? type.GetField("Coords", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (coordsField != null)
            {
                try
                {
                    var val = coordsField.GetValue(obj);
                    if (val is float[] fa && fa.Length >= 3) { pos = new Vector3(fa[0], fa[1], fa[2]); return true; }
                }
                catch { }
            }
        }
        catch { /* swallow reflection errors */ }

        return false;
    }

    // Try to resolve a Type by short name from currently-loaded assemblies.
    private static Type GetTypeByName(string shortTypeName)
    {
        try
        {
            var t = Type.GetType(shortTypeName);
            if (t != null) return t;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                try
                {
                    var type = asm.GetTypes().FirstOrDefault(x => string.Equals(x.Name, shortTypeName, StringComparison.OrdinalIgnoreCase));
                    if (type != null) return type;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public static Vector3 TryGetExactPlayerWorldPosition()
    {
        Vector3 pos = Vector3.zero;
        try
        {
            TBLog.Info("TryGetExactPlayerWorldPosition: enter");

            // 1) Common fast path: look for GameObject tagged "Player"
            try
            {
                TBLog.Info("TryGetExactPlayerWorldPosition: trying GameObject.FindWithTag(\"Player\")");
                var tagged = GameObject.FindWithTag("Player");
                if (IsValidPlayerObject(tagged))
                {
                    pos = tagged.transform.position;
                    TBLog.Info($"TryGetExactPlayerWorldPosition: found player by tag at {pos}");
//                    return pos;
                }
                else
                {
                    TBLog.Info("TryGetExactPlayerWorldPosition: no valid GameObject with tag 'Player' found");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryGetExactPlayerWorldPosition: exception during tag lookup: " + ex);
            }

            // 2) Look for common name patterns used in Outward / mods: "PlayerChar...", "Player", "LocalPlayer"
            try
            {
                TBLog.Info("TryGetExactPlayerWorldPosition: enumerating GameObject instances for name/component heuristics");
                var all = GameObject.FindObjectsOfType<GameObject>();
                TBLog.Info($"TryGetExactPlayerWorldPosition: scanned {all?.Length ?? 0} GameObjects");

                var byName = all.FirstOrDefault(g =>
                    !string.IsNullOrEmpty(g.name) &&
                    (g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)
                     || g.name.StartsWith("Player", StringComparison.OrdinalIgnoreCase)
                     || g.name.IndexOf("LocalPlayer", StringComparison.OrdinalIgnoreCase) >= 0));
                if (IsValidPlayerObject(byName))
                {
                    pos = byName.transform.position;
                    TBLog.Info($"TryGetExactPlayerWorldPosition: found player by name '{byName.name}' at {pos}");
//                    return pos;
                }
                else
                {
                    TBLog.Info("TryGetExactPlayerWorldPosition: no player GameObject matched name heuristics");
                }

                // Prefer an object that contains a CharacterController (very likely the player)
                var byController = all.FirstOrDefault(g => g.GetComponentInChildren<CharacterController>(true) != null);
                if (IsValidPlayerObject(byController))
                {
                    pos = byController.transform.position;
                    TBLog.Info($"TryGetExactPlayerWorldPosition: found player by CharacterController on '{byController.name}' at {pos}");
//                    return pos;
                }
                else
                {
                    TBLog.Info("TryGetExactPlayerWorldPosition: no GameObject with CharacterController found");
                }

                // Fallback: object that has a Camera child and a Rigidbody (common player prefab)
                var byCamRb = all.FirstOrDefault(g => g.GetComponentInChildren<Camera>(true) != null && g.GetComponentInChildren<Rigidbody>(true) != null);
                if (IsValidPlayerObject(byCamRb))
                {
                    pos = byCamRb.transform.position;
                    TBLog.Info($"TryGetExactPlayerWorldPosition: found player by Camera+Rigidbody on '{byCamRb.name}' at {pos}");
//                    return pos;
                }
                else
                {
                    TBLog.Info("TryGetExactPlayerWorldPosition: no GameObject with Camera+Rigidbody found");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryGetExactPlayerWorldPosition: exception while scanning GameObjects: " + ex);
            }

            // 3) Try well-known runtime types via reflection and extract transform or position
            string[] candidateTypes = new[] { "LocalPlayer", "PlayerCharacter", "PlayerEntity", "PlayerController" };
            foreach (var typeName in candidateTypes)
            {
                try
                {
                    TBLog.Info($"TryGetExactPlayerWorldPosition: reflection: looking for type '{typeName}'");
                    var t = GetTypeByName(typeName);
                    if (t == null)
                    {
                        TBLog.Info($"TryGetExactPlayerWorldPosition: type '{typeName}' not found in loaded assemblies");
                        continue;
                    }

                    // static Instance field or property
                    object instance = null;
                    var f = t.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null)
                    {
                        try { instance = f.GetValue(null); TBLog.Info($"TryGetExactPlayerWorldPosition: found static field 'Instance' on {typeName}"); } catch { TBLog.Info($"TryGetExactPlayerWorldPosition: could not read field Instance on {typeName}"); }
                    }

                    var p = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instance == null && p != null && p.CanRead)
                    {
                        try { instance = p.GetValue(null, null); TBLog.Info($"TryGetExactPlayerWorldPosition: found static property 'Instance' on {typeName}"); } catch { TBLog.Info($"TryGetExactPlayerWorldPosition: could not read property Instance on {typeName}"); instance = null; }
                    }

                    // other static properties returning same type
                    if (instance == null)
                    {
                        var staticProps = t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        foreach (var sp in staticProps)
                        {
                            if (!sp.CanRead) continue;
                            if (sp.PropertyType != t) continue;
                            try { instance = sp.GetValue(null, null); } catch { instance = null; }
                            if (instance != null) { TBLog.Info($"TryGetExactPlayerWorldPosition: found static property '{sp.Name}' on {typeName} returning instance"); break; }
                        }
                    }

                    if (instance != null)
                    {
                        if (TryExtractPositionFromObject(instance, out pos))
                        {
                            TBLog.Info($"TryGetExactPlayerWorldPosition: extracted position from instance of {typeName}: {pos}");
//                            return pos;
                        }
                        else
                        {
                            TBLog.Info($"TryGetExactPlayerWorldPosition: instance of {typeName} exists but no position could be extracted");
                        }
                    }

                    // If no static instance, try any live component of that type
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    TBLog.Info($"TryGetExactPlayerWorldPosition: found {objs?.Length ?? 0} live objects of type {typeName}");
                    if (objs != null && objs.Length > 0)
                    {
                        var comp = objs[0];
                        if (TryExtractPositionFromObject(comp, out pos))
                        {
                            TBLog.Info($"TryGetExactPlayerWorldPosition: extracted position from live object of type {typeName}: {pos}");
//                            return pos;
                        }
                        else
                        {
                            TBLog.Info($"TryGetExactPlayerWorldPosition: live object of type {typeName} found but no position could be extracted");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"TryGetExactPlayerWorldPosition: reflection search for '{typeName}' threw: {ex.Message}");
                }
            }

            // 4) Fallback: main camera position (not ideal - camera may be offset), but return it if present
            try
            {
                if (Camera.main != null)
                {
                    pos = Camera.main.transform.position;
                    TBLog.Info($"TryGetExactPlayerWorldPosition: falling back to Camera.main at {pos}");
//                    return pos;
                }
                else
                {
                    TBLog.Info("TryGetExactPlayerWorldPosition: Camera.main is null");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryGetExactPlayerWorldPosition: exception when accessing Camera.main: " + ex);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryGetExactPlayerWorldPosition: unexpected exception: " + ex);
        }

        TBLog.Info("TryGetExactPlayerWorldPosition: no player position found -> returning Vector3.zero");
        return Vector3.zero;
    }
}
