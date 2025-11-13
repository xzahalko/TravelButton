using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Lightweight helper MonoBehaviour that runs teleport-related coroutines for the UI.
/// TravelButtonUI and TravelDialog use EnsureSceneAndTeleport to perform anchor lookup / grounding and call TeleportHelpers.AttemptTeleportToPositionSafe.
/// </summary>
public class TeleportHelpersBehaviour : MonoBehaviour
{
    private void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
    }

    /// <summary>
    /// EnsureSceneAndTeleport:
    /// - Finds target position by targetGameObjectName (waiting briefly), or uses explicit coords.
    /// - Tries TeleportHelpers.GetGroundedPosition/EnsureClearance and calls TeleportHelpers.AttemptTeleportToPositionSafe.
    /// - Invokes callback(success).
    /// </summary>
    public IEnumerator EnsureSceneAndTeleport(object cityLike, Vector3 coordsHint, bool haveCoordsHint, Action<bool> callback)
    {
        if (cityLike == null)
        {
            TravelButtonMod.LogWarning("EnsureSceneAndTeleport: cityLike is null.");
            callback?.Invoke(false);
            yield break;
        }

        string cityName = null;
        string targetName = null;
        float[] coordsArray = null;

        try
        {
            var t = cityLike.GetType();
            var nameProp = t.GetField("name") ?? (System.Reflection.FieldInfo)null;
            if (nameProp == null)
            {
                var namePropInfo = t.GetProperty("name");
                if (namePropInfo != null) cityName = namePropInfo.GetValue(cityLike) as string;
            }
            else
            {
                cityName = nameProp.GetValue(cityLike) as string;
            }

            var tgField = t.GetField("targetGameObjectName");
            if (tgField != null) targetName = tgField.GetValue(cityLike) as string;
            else
            {
                var tgProp = t.GetProperty("targetGameObjectName");
                if (tgProp != null) targetName = tgProp.GetValue(cityLike) as string;
            }

            var coordsField = t.GetField("coords");
            if (coordsField != null) coordsArray = coordsField.GetValue(cityLike) as float[];
            else
            {
                var coordsProp = t.GetProperty("coords");
                if (coordsProp != null) coordsArray = coordsProp.GetValue(cityLike) as float[];
            }
        }
        catch { }

        Vector3 targetPos = Vector3.zero;
        bool found = false;

        // If a named target exists, wait shortly for it to appear
        if (!string.IsNullOrEmpty(targetName))
        {
            const float timeout = 5.0f;
            const float poll = 0.1f;
            float waited = 0f;
            GameObject foundGO = null;
            while (waited < timeout)
            {
                try
                {
                    foundGO = GameObject.Find(targetName);
                    if (foundGO != null) break;
                }
                catch { }
                waited += poll;
                yield return new WaitForSeconds(poll);
            }

            if (foundGO != null)
            {
                targetPos = foundGO.transform.position;
                found = true;
                TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: found target '{targetName}' at {targetPos} for '{cityName}'");
            }
            else
            {
                TravelButtonMod.LogWarning($"EnsureSceneAndTeleport: target '{targetName}' not found for '{cityName}' - will try coords fallback.");
            }
        }

        if (!found && haveCoordsHint)
        {
            targetPos = coordsHint;
            found = true;
            TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: using coordsHint for '{cityName}' = {targetPos}");
        }
        else if (!found && coordsArray != null && coordsArray.Length >= 3)
        {
            targetPos = new Vector3(coordsArray[0], coordsArray[1], coordsArray[2]);
            found = true;
            TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: using explicit coords for '{cityName}' = {targetPos}");
        }

        if (!found)
        {
            // fallback: search transforms
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var tr in all)
                {
                    if (tr == null || string.IsNullOrEmpty(tr.name)) continue;
                    if (!string.IsNullOrEmpty(cityName) && tr.name.IndexOf(cityName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetPos = tr.position;
                        found = true;
                        TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: fallback matched transform '{tr.name}' -> {targetPos}");
                        break;
                    }
                }
            }
            catch { }
        }

        if (!found)
        {
            TravelButtonMod.LogError($"EnsureSceneAndTeleport: no target position could be determined for '{cityName}'. Aborting.");
            callback?.Invoke(false);
            yield break;
        }

        // Ground/clear the position
        try
        {
            targetPos = TeleportHelpers.GetGroundedPosition(targetPos);
        }
        catch { targetPos = TeleportHelpers.EnsureClearance(targetPos); }

        // yield one frame and attempt teleport
        yield return null;

        bool relocated = false;
        try
        {
            relocated = TeleportHelpers.AttemptTeleportToPositionSafe(targetPos);
            if (relocated) TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: teleport to '{cityName}' succeeded at {targetPos}");
            else TravelButtonMod.LogWarning($"EnsureSceneAndTeleport: teleport to '{cityName}' failed at {targetPos}");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("EnsureSceneAndTeleport: teleport exception: " + ex);
            relocated = false;
        }

        callback?.Invoke(relocated);
    }

    public static TeleportHelpersBehaviour GetOrCreateHost()
    {
        var existing = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
        if (existing != null) return existing;
        var go = new GameObject("TeleportHelpersHost");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<TeleportHelpersBehaviour>();
    }
}