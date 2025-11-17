using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public partial class TravelButtonUI : MonoBehaviour
{
    // Compatibility forwarder: one-arg overload
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj)
    {
        // Forward to the 4-arg overload with defaults
        return LoadSceneAndTeleportCoroutine(cityObj, 0, Vector3.zero, false);
    }

    // Compatibility forwarder: 4-arg overload (object cityObj, cost, coordsHint, haveCoordsHint)
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        // Resolved values (filled in below, before any yield)
        string sceneName = null;
        string targetName = null;
        Vector3 coordsToUse = Vector3.zero;
        bool haveCoordsFinal = false;
        int priceToUse = cost;
        bool resolved = false;

        try
        {
            // Try to resolve typed data (no yields here)
            if (TryResolveCityDataFromObject(cityObj, out string resolvedScene, out string resolvedTarget, out Vector3 resolvedCoords, out bool resolvedHaveCoords, out int resolvedPrice))
            {
                sceneName = resolvedScene;
                targetName = resolvedTarget;
                // prefer explicit args where provided
                priceToUse = (cost > 0) ? cost : (resolvedPrice > 0 ? resolvedPrice : 0);

                if (haveCoordsHint)
                {
                    coordsToUse = coordsHint;
                    haveCoordsFinal = true;
                }
                else if (resolvedHaveCoords)
                {
                    coordsToUse = resolvedCoords;
                    haveCoordsFinal = true;
                }

                resolved = !string.IsNullOrEmpty(sceneName) || haveCoordsFinal;
            }
            else
            {
                // Could not resolve at all
                string typeName = cityObj == null ? "<null>" : cityObj.GetType().FullName;
                string toStr = "<ToString failed>";
                try { toStr = cityObj?.ToString() ?? "<null>"; } catch { }
                TBLog.Warn($"LoadSceneAndTeleportCoroutine(compat): couldn't resolve city data from object (type={typeName}, tostring='{toStr}'). Aborting.");
                try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
                yield break; // safe: this yield is outside any try with catch (we are still inside the method but not inside the try/catch block)
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine(compat): unexpected error: " + ex);
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        if (!resolved)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine(compat): resolved nothing for provided cityObj; aborting.");
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        // Now it's safe to yield (we are outside the try/catch scope)
        yield return StartCoroutine(TryTeleportThenChargeExplicit(sceneName, targetName, coordsToUse, haveCoordsFinal, priceToUse));
    }

    // Helper: if explicit coordsHint/haveCoordsHint provided prefer them, otherwise use resolved coords
    private static Vector3 haveCoordsHintOrResolved(Vector3 coordsHint, bool haveCoordsHint, Vector3 resolvedCoords, bool resolvedHaveCoords, out bool haveCoordsFinal)
    {
        if (haveCoordsHint)
        {
            haveCoordsFinal = true;
            return coordsHint;
        }
        if (resolvedHaveCoords)
        {
            haveCoordsFinal = true;
            return resolvedCoords;
        }
        haveCoordsFinal = false;
        return Vector3.zero;
    }

    // Try to resolve sceneName/target/coords/price from the supplied object.
    // Returns true if at least one of the destination fields (sceneName or coords) was found (or price/target).
    // Replace the existing TryResolveCityDataFromObject method with this enhanced resolver.
    private bool TryResolveCityDataFromObject(object cityObj, out string outSceneName, out string outTargetName, out Vector3 outCoords, out bool outHaveCoords, out int outPrice)
    {
        outSceneName = null;
        outTargetName = null;
        outCoords = Vector3.zero;
        outHaveCoords = false;
        outPrice = 0;

        if (cityObj == null) return false;

        // 1) Fast-path: if it's already a CityEntry
        if (cityObj is CityEntry ce)
        {
            outSceneName = ce.sceneName;
            outTargetName = ce.targetGameObjectName;
            if (ce.coords != null && ce.coords.Length >= 3)
            {
                outCoords = new Vector3(ce.coords[0], ce.coords[1], ce.coords[2]);
                outHaveCoords = true;
            }
            outPrice = ce.price;
            return !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
        }

        // 2) If it's a string, try lookup by name/scene
        if (cityObj is string s)
        {
            if (TryFindCityByNameOrScene(s, out CityEntry foundS))
            {
                outSceneName = foundS.sceneName;
                outTargetName = foundS.targetGameObjectName;
                if (foundS.coords != null && foundS.coords.Length >= 3)
                {
                    outCoords = new Vector3(foundS.coords[0], foundS.coords[1], foundS.coords[2]);
                    outHaveCoords = true;
                }
                outPrice = foundS.price;
                return !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
            }
            outSceneName = s; // interpret as direct scene name
            return true;
        }

        // 3) Try to match by ToString() or 'name' property against loadedCities
        try
        {
            string candidateName = null;
            var nameProp = cityObj.GetType().GetProperty("name", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                         ?? cityObj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp != null) candidateName = nameProp.GetValue(cityObj) as string;
            if (string.IsNullOrEmpty(candidateName))
            {
                try { candidateName = cityObj.ToString(); } catch { candidateName = null; }
            }

            if (!string.IsNullOrEmpty(candidateName))
            {
                if (TryFindCityByNameOrScene(candidateName, out CityEntry found2))
                {
                    outSceneName = found2.sceneName;
                    outTargetName = found2.targetGameObjectName;
                    if (found2.coords != null && found2.coords.Length >= 3) { outCoords = new Vector3(found2.coords[0], found2.coords[1], found2.coords[2]); outHaveCoords = true; }
                    outPrice = found2.price;
                    return !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
                }
            }
        }
        catch { /* ignore */ }

        // 4) Enhanced reflection: inspect properties and fields (public + non-public) for likely candidates.
        try
        {
            var t = cityObj.GetType();

            // helper lists of candidate member name patterns
            string[] sceneKeys = new[] { "scenename", "scene", "scene_name", "sceneName" };
            string[] targetKeys = new[] { "targetgameobjectname", "target", "targetname", "target_gameobject_name", "targetGameObjectName" };
            string[] nameKeys = new[] { "name", "displayname", "title" };
            string[] priceKeys = new[] { "price", "cost", "fee" };
            string[] coordsKeys = new[] { "coords", "position", "pos", "location", "transform", "vector", "coordinate" };

            // scan properties first
            foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    object val = null;
                    try { val = prop.GetValue(cityObj); } catch { continue; }
                    if (val == null) continue;

                    string propName = prop.Name.ToLowerInvariant();

                    // strings
                    if (val is string sval)
                    {
                        if (Array.Exists(sceneKeys, k => propName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval;
                        }
                        else if (Array.Exists(targetKeys, k => propName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outTargetName)) outTargetName = sval;
                        }
                        else if (Array.Exists(nameKeys, k => propName.Contains(k)))
                        {
                            // try lookup by this name
                            if (TryFindCityByNameOrScene(sval, out CityEntry f))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f.targetGameObjectName;
                                if (!outHaveCoords && f.coords != null && f.coords.Length >= 3) { outCoords = new Vector3(f.coords[0], f.coords[1], f.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f.price;
                            }
                            else
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval; // fallback: treat as scene
                            }
                        }
                        else
                        {
                            // also check whether the string value itself matches a known scene/name
                            if (TryFindCityByNameOrScene(sval, out CityEntry f2))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f2.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f2.targetGameObjectName;
                                if (!outHaveCoords && f2.coords != null && f2.coords.Length >= 3) { outCoords = new Vector3(f2.coords[0], f2.coords[1], f2.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f2.price;
                            }
                        }
                    }
                    // numeric arrays or lists -> coords
                    else if (val is float[] farr && farr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3(farr[0], farr[1], farr[2]); outHaveCoords = true; }
                    }
                    else if (val is double[] darr && darr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3((float)darr[0], (float)darr[1], (float)darr[2]); outHaveCoords = true; }
                    }
                    else if (val is System.Collections.IList list && list.Count >= 3)
                    {
                        // try to convert first three items to floats
                        try
                        {
                            float a = Convert.ToSingle(list[0]);
                            float b = Convert.ToSingle(list[1]);
                            float c = Convert.ToSingle(list[2]);
                            if (!outHaveCoords) { outCoords = new Vector3(a, b, c); outHaveCoords = true; }
                        }
                        catch { }
                    }
                    else
                    {
                        // If it's a UnityEngine.GameObject or Component, try to use its name
                        var asGameObject = val as GameObject;
                        if (asGameObject != null && string.IsNullOrEmpty(outTargetName))
                            outTargetName = asGameObject.name;
                        else
                        {
                            var asComponent = val as Component;
                            if (asComponent != null && string.IsNullOrEmpty(outTargetName))
                                outTargetName = asComponent.gameObject?.name;
                        }
                    }
                }
                catch { /* ignore one property failure */ }
            }

            // scan fields as well (public + non-public)
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    object val = null;
                    try { val = field.GetValue(cityObj); } catch { continue; }
                    if (val == null) continue;

                    string fieldName = field.Name.ToLowerInvariant();

                    if (val is string sval)
                    {
                        if (Array.Exists(sceneKeys, k => fieldName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval;
                        }
                        else if (Array.Exists(targetKeys, k => fieldName.Contains(k)))
                        {
                            if (string.IsNullOrEmpty(outTargetName)) outTargetName = sval;
                        }
                        else if (Array.Exists(nameKeys, k => fieldName.Contains(k)))
                        {
                            if (TryFindCityByNameOrScene(sval, out CityEntry f))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f.targetGameObjectName;
                                if (!outHaveCoords && f.coords != null && f.coords.Length >= 3) { outCoords = new Vector3(f.coords[0], f.coords[1], f.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f.price;
                            }
                            else
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = sval;
                            }
                        }
                        else
                        {
                            if (TryFindCityByNameOrScene(sval, out CityEntry f2))
                            {
                                if (string.IsNullOrEmpty(outSceneName)) outSceneName = f2.sceneName;
                                if (string.IsNullOrEmpty(outTargetName)) outTargetName = f2.targetGameObjectName;
                                if (!outHaveCoords && f2.coords != null && f2.coords.Length >= 3) { outCoords = new Vector3(f2.coords[0], f2.coords[1], f2.coords[2]); outHaveCoords = true; }
                                if (outPrice == 0) outPrice = f2.price;
                            }
                        }
                    }
                    else if (val is float[] farr && farr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3(farr[0], farr[1], farr[2]); outHaveCoords = true; }
                    }
                    else if (val is double[] darr && darr.Length >= 3)
                    {
                        if (!outHaveCoords) { outCoords = new Vector3((float)darr[0], (float)darr[1], (float)darr[2]); outHaveCoords = true; }
                    }
                    else if (val is System.Collections.IList list && list.Count >= 3)
                    {
                        try
                        {
                            float a = Convert.ToSingle(list[0]);
                            float b = Convert.ToSingle(list[1]);
                            float c = Convert.ToSingle(list[2]);
                            if (!outHaveCoords) { outCoords = new Vector3(a, b, c); outHaveCoords = true; }
                        }
                        catch { }
                    }
                    else
                    {
                        var asGameObject = val as GameObject;
                        if (asGameObject != null && string.IsNullOrEmpty(outTargetName))
                            outTargetName = asGameObject.name;
                        else
                        {
                            var asComponent = val as Component;
                            if (asComponent != null && string.IsNullOrEmpty(outTargetName))
                                outTargetName = asComponent.gameObject?.name;
                        }
                    }
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryResolveCityDataFromObject: reflection fallback failed: " + ex);
        }

        // Last attempt: if we have loadedCities, try to pick one by matching any discovered strings
        try
        {
            if (!string.IsNullOrEmpty(outSceneName))
            {
                if (TryFindCityByNameOrScene(outSceneName, out CityEntry f3))
                {
                    // harmonize names
                    if (string.IsNullOrEmpty(outTargetName)) outTargetName = f3.targetGameObjectName;
                    if (!outHaveCoords && f3.coords != null && f3.coords.Length >= 3) { outCoords = new Vector3(f3.coords[0], f3.coords[1], f3.coords[2]); outHaveCoords = true; }
                    if (outPrice == 0) outPrice = f3.price;
                }
            }
        }
        catch { }

        // Return true if we discovered either sceneName or coords
        bool ok = !string.IsNullOrEmpty(outSceneName) || outHaveCoords;
        if (!ok)
        {
            // Diagnostic: log members (names only) to help debugging
            try
            {
                var t2 = cityObj.GetType();
                var membs = new List<string>();
                foreach (var p in t2.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) membs.Add("P:" + p.Name);
                foreach (var f in t2.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) membs.Add("F:" + f.Name);
                TBLog.Warn($"TryResolveCityDataFromObject: failed to resolve data for object type={t2.FullName}. Members: {string.Join(", ", membs)}");
            }
            catch { }
        }
        return ok;
    }

    // Helper: search loadedCities (if available) by name or sceneName (case-insensitive)
    private bool TryFindCityByNameOrScene(string candidate, out CityEntry outCity)
    {
        outCity = null;
        if (string.IsNullOrEmpty(candidate)) return false;
        try
        {
            // loadedCities is set by InitCities earlier
            var list = loadedCities;
            if (list == null || list.Count == 0) return false;

            string cLower = candidate.Trim().ToLowerInvariant();
            foreach (var c in list)
            {
                if (c == null) continue;
                if (!string.IsNullOrEmpty(c.name) && c.name.Trim().ToLowerInvariant() == cLower) { outCity = c; return true; }
                if (!string.IsNullOrEmpty(c.sceneName) && c.sceneName.Trim().ToLowerInvariant() == cLower) { outCity = c; return true; }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryFindCityByNameOrScene: lookup failed: " + ex);
        }
        return false;
    }

    // helper inside TravelButtonUI
    // (insert into the TravelButtonUI partial class)
    private IEnumerator ForwardCityObjectToExplicit(object cityObj, int cost = 0, Vector3 coordsHint = default, bool haveCoordsHint = false)
    {
        if (cityObj == null) { TBLog.Warn("ForwardCityObjectToExplicit: cityObj null"); yield break; }

        // Try to cast to CityEntry first
        if (cityObj is CityEntry ce)
        {
            Vector3 coordsVec = Vector3.zero;
            bool ceHasCoords = (ce.coords != null && ce.coords.Length >= 3);
            if (ceHasCoords) coordsVec = new Vector3(ce.coords[0], ce.coords[1], ce.coords[2]);
            yield return StartCoroutine(TryTeleportThenChargeExplicit(ce.sceneName, ce.targetGameObjectName, coordsVec, ceHasCoords, ce.price));
            yield break;
        }

        // Fallback: reflect minimal fields
        string sceneName = null;
        string target = null;
        int price = cost;
        Vector3 coords = coordsHint;
        bool haveCoords = haveCoordsHint;

        try
        {
            var t = cityObj.GetType();
            var sn = t.GetProperty("sceneName") ?? t.GetProperty("SceneName");
            if (sn != null) sceneName = sn.GetValue(cityObj) as string;
            var tg = t.GetProperty("targetGameObjectName") ?? t.GetProperty("TargetGameObjectName") ?? t.GetProperty("target");
            if (tg != null) target = tg.GetValue(cityObj) as string;
            var pr = t.GetProperty("price") ?? t.GetProperty("Price");
            if (pr != null)
            {
                var val = pr.GetValue(cityObj);
                if (val != null) price = Convert.ToInt32(val);
            }
            var coordsP = t.GetProperty("coords") ?? t.GetProperty("Coords");
            if (coordsP != null)
            {
                var v = coordsP.GetValue(cityObj);
                if (v is double[] da && da.Length >= 3) { coords = new Vector3((float)da[0], (float)da[1], (float)da[2]); haveCoords = true; }
                else if (v is float[] fa && fa.Length >= 3) { coords = new Vector3(fa[0], fa[1], fa[2]); haveCoords = true; }
                else if (v is System.Collections.IList list && list.Count >= 3)
                {
                    try { coords = new Vector3(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]), Convert.ToSingle(list[2])); haveCoords = true; } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ForwardCityObjectToExplicit: reflection failed: " + ex);
        }

        if (string.IsNullOrEmpty(sceneName) && !haveCoords)
        {
            TBLog.Warn("ForwardCityObjectToExplicit: cannot determine sceneName or coords; aborting.");
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        yield return StartCoroutine(TryTeleportThenChargeExplicit(sceneName, target, coords, haveCoords, price));
    }
}