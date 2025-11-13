using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Lightweight helper MonoBehaviour that runs teleport-related coroutines for the UI.
/// TravelButtonUI and TravelDialog use EnsureSceneAndTeleport to perform scene/anchor waiting
/// and to call TeleportHelpers.AttemptTeleportToPositionSafe safely from a coroutine context.
/// </summary>
public class TeleportHelpersBehaviour : MonoBehaviour
{
    private void Awake()
    {
        // keep alive across scene loads so coroutines can continue
        DontDestroyOnLoad(this.gameObject);
    }

    /// <summary>
    /// EnsureSceneAndTeleport:
    /// - Attempts to resolve a target world position for the given city.
    /// - If city.targetGameObjectName is provided, waits briefly (configurable timeout) for the object to appear.
    /// - If explicit coords exist, uses them as a fallback.
    /// - Calls TeleportHelpers.AttemptTeleportToPositionSafe to perform the teleport.
    /// - Invokes callback(success) when finished.
    /// </summary>
    /// <param name="city">City object (TravelButtonMod.City) - must expose name, targetGameObjectName and coords fields/properties as in the project.</param>
    /// <param name="coordsHint">Optional coords hint (may be Vector3.zero if none)</param>
    /// <param name="haveCoordsHint">True if coordsHint is valid</param>
    /// <param name="callback">Callback invoked with true on success, false on failure</param>
    public IEnumerator EnsureSceneAndTeleport(TravelButtonMod.City city, Vector3 coordsHint, bool haveCoordsHint, Action<bool> callback)
    {
        if (city == null)
        {
            TravelButtonMod.LogWarning("EnsureSceneAndTeleport: city is null.");
            callback?.Invoke(false);
            yield break;
        }

        Vector3 targetPos = Vector3.zero;
        bool foundPos = false;

        // First: if targetGameObjectName is set, try to find the object, waiting a short time if necessary.
        string targetName = null;
        try
        {
            // Use reflection-safe access in case city definition differs slightly
            var t = city.GetType();
            var tgField = t.GetField("targetGameObjectName");
            if (tgField != null)
                targetName = tgField.GetValue(city) as string;
            else
            {
                var tgProp = t.GetProperty("targetGameObjectName");
                if (tgProp != null)
                    targetName = tgProp.GetValue(city) as string;
            }
        }
        catch { targetName = null; }

        if (!string.IsNullOrEmpty(targetName))
        {
            // Wait for object to appear in scene (use short timeout so UI doesn't hang indefinitely)
            const float timeout = 5.0f;
            const float pollInterval = 0.1f;
            float waited = 0f;
            GameObject found = null;

            while (waited < timeout)
            {
                try
                {
                    found = GameObject.Find(targetName);
                    if (found != null)
                        break;
                }
                catch { /* ignore find exceptions */ }

                waited += pollInterval;
                yield return new WaitForSeconds(pollInterval);
            }

            if (found != null)
            {
                targetPos = found.transform.position;
                foundPos = true;
                TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: found target GameObject '{targetName}' at {targetPos} for city '{city.name}'.");
            }
            else
            {
                TravelButtonMod.LogWarning($"EnsureSceneAndTeleport: target GameObject '{targetName}' not found after waiting; will try coords fallback for city '{city.name}'.");
            }
        }

        // If we didn't find a GameObject target, use explicit coords if available (from coordsHint or city.coords)
        if (!foundPos)
        {
            if (haveCoordsHint)
            {
                targetPos = coordsHint;
                foundPos = true;
                TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: using coordsHint for city '{city.name}' = {targetPos}");
            }
            else
            {
                // Try to read coords from the city object via reflection
                try
                {
                    var t = city.GetType();
                    var coordsField = t.GetField("coords");
                    object rawCoords = null;
                    if (coordsField != null)
                    {
                        rawCoords = coordsField.GetValue(city);
                    }
                    else
                    {
                        var coordsProp = t.GetProperty("coords");
                        if (coordsProp != null)
                            rawCoords = coordsProp.GetValue(city);
                    }

                    if (rawCoords is float[] arr && arr.Length >= 3)
                    {
                        targetPos = new Vector3(arr[0], arr[1], arr[2]);
                        foundPos = true;
                        TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: using reflected coords for city '{city.name}' = {targetPos}");
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonMod.LogWarning("EnsureSceneAndTeleport: reflection while reading coords failed: " + ex);
                }
            }
        }

        // As extra fallback: if no coords and no target GO, attempt to find a scene object whose name contains the city name
        if (!foundPos)
        {
            try
            {
                var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var tr in allTransforms)
                {
                    if (tr == null || string.IsNullOrEmpty(tr.name)) continue;
                    if (tr.name.IndexOf(city.name ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetPos = tr.position;
                        foundPos = true;
                        TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: fallback found transform '{tr.name}' for city '{city.name}' at {targetPos}");
                        break;
                    }
                }
            }
            catch { /* ignore */ }
        }

        if (!foundPos)
        {
            TravelButtonMod.LogError($"EnsureSceneAndTeleport: could not determine a target position for city '{city.name}'. Aborting teleport.");
            callback?.Invoke(false);
            yield break;
        }

        // Ensure clearances/grounding if helpful
        try
        {
            // Prefer TeleportHelpers.GetGroundedPosition if available; else use EnsureClearance
            Vector3 grounded = targetPos;
            try
            {
                grounded = TeleportHelpers.GetGroundedPosition(targetPos);
            }
            catch
            {
                grounded = TeleportHelpers.EnsureClearance(targetPos);
            }
            targetPos = grounded;
        }
        catch { /* ignore grounding errors */ }

        // Allow one frame to settle before teleport (helps with scene load timing)
        yield return null;

        // Attempt teleport using the shared TeleportHelpers method
        bool success = false;
        try
        {
            success = TeleportHelpers.AttemptTeleportToPositionSafe(targetPos);
            if (success)
                TravelButtonMod.LogInfo($"EnsureSceneAndTeleport: teleport to '{city.name}' succeeded at {targetPos}.");
            else
                TravelButtonMod.LogWarning($"EnsureSceneAndTeleport: teleport to '{city.name}' failed.");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("EnsureSceneAndTeleport: exception while teleporting: " + ex);
            success = false;
        }

        // invoke callback with result (safe null-check)
        try
        {
            callback?.Invoke(success);
        }
        catch (Exception cbEx)
        {
            TravelButtonMod.LogWarning("EnsureSceneAndTeleport: callback threw exception: " + cbEx);
        }
    }

    /// <summary>
    /// Convenience factory method: find or create a singleton TeleportHelpersBehaviour host GameObject.
    /// TravelButtonUI currently uses a similar approach; this helper lets other code reuse it.
    /// </summary>
    public static TeleportHelpersBehaviour GetOrCreateHost()
    {
        var existing = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
        if (existing != null) return existing;

        var go = new GameObject("TeleportHelpersHost");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<TeleportHelpersBehaviour>();
    }
}