using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Lightweight helper MonoBehaviour that runs teleport-related coroutines for the UI.
/// Includes conservative tracing to Desktop so we can see which teleport code runs before a crash.
/// </summary>
public class TeleportHelpersBehaviour : MonoBehaviour
{
    private void Awake()
    {
        try { DontDestroyOnLoad(this.gameObject); } catch { }
    }

    // Watch a GameObject for post-teleport changes.
    // Logs if the world position changes during 'durationSec' seconds, checking every frame.
    public IEnumerator WatchPositionAfterTeleport(GameObject go, Vector3 expectedPosition, float durationSec = 2.0f)
    {
        if (go == null)
        {
            TravelButtonPlugin.LogInfo("WatchPositionAfterTeleport: go is null, aborting.");
            yield break;
        }

        TravelButtonPlugin.LogInfo($"WatchPositionAfterTeleport: starting watch for '{go.name}' (id={go.GetInstanceID()}) expecting {expectedPosition} for {durationSec:F2}s.");

        Vector3 last = go.transform.position;
        float elapsed = 0f;
        bool changed = false;

        while (elapsed < durationSec)
        {
            yield return null;
            elapsed += Time.deltaTime;

            Vector3 cur = go.transform.position;
            if (!Mathf.Approximately(cur.x, last.x) || !Mathf.Approximately(cur.y, last.y) || !Mathf.Approximately(cur.z, last.z))
            {
                changed = true;
            }
        }

        if (!changed)
            TravelButtonPlugin.LogInfo($"WatchPositionAfterTeleport: no position changes detected for '{go.name}' during {durationSec:F2}s. Expected pos was {expectedPosition}.");
        else
            TravelButtonPlugin.LogInfo($"WatchPositionAfterTeleport: finished monitoring '{go.name}' - changes were detected.");

        yield break;
    }

    /// <summary>
    /// Reflection-friendly coroutine that resolves a position and then attempts to teleport.
    /// Designed to avoid yields inside catch/finally blocks and to log progress to desktop.
    /// </summary>
    public IEnumerator EnsureSceneAndTeleport(object cityLike, Vector3 coordsHint, bool haveCoordsHint, Action<bool> callback)
    {
        if (cityLike == null)
        {
            TravelButtonPlugin.LogWarning("EnsureSceneAndTeleport: cityLike is null.");
            callback?.Invoke(false);
            yield break;
        }

        string cityName = null;
        string targetName = null;
        float[] coordsArray = null;

        // Reflection to extract fields/properties (no yields here)
        try
        {
            var t = cityLike.GetType();

            var nameField = t.GetField("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (nameField != null)
                cityName = nameField.GetValue(cityLike) as string;
            else
            {
                var nameProp = t.GetProperty("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (nameProp != null) cityName = nameProp.GetValue(cityLike) as string;
            }

            var tgField = t.GetField("targetGameObjectName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (tgField != null)
                targetName = tgField.GetValue(cityLike) as string;
            else
            {
                var tgProp = t.GetProperty("targetGameObjectName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (tgProp != null) targetName = tgProp.GetValue(cityLike) as string;
            }

            var coordsField = t.GetField("coords", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (coordsField != null)
                coordsArray = coordsField.GetValue(cityLike) as float[];
            else
            {
                var coordsProp = t.GetProperty("coords", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (coordsProp != null) coordsArray = coordsProp.GetValue(cityLike) as float[];
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("EnsureSceneAndTeleport: reflection read failed: " + ex);
        }

        Vector3 targetPos = Vector3.zero;
        bool found = false;

        // Wait for a named target if provided
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
                catch { /* ignore */ }

                waited += poll;
                yield return new WaitForSeconds(poll);
            }

            if (foundGO != null)
            {
                targetPos = foundGO.transform.position;
                found = true;
                TravelButtonPlugin.LogInfo($"EnsureSceneAndTeleport: found target '{targetName}' at {targetPos} for '{cityName}'");
            }
            else
            {
                TravelButtonPlugin.LogWarning($"EnsureSceneAndTeleport: target '{targetName}' not found for '{cityName}' - will try coords fallback.");
            }
        }

        // Use coords hint or reflected coords
        if (!found && haveCoordsHint)
        {
            targetPos = coordsHint;
            found = true;
            TravelButtonPlugin.LogInfo($"EnsureSceneAndTeleport: using coordsHint for '{cityName}' = {targetPos}");
        }
        else if (!found && coordsArray != null && coordsArray.Length >= 3)
        {
            targetPos = new Vector3(coordsArray[0], coordsArray[1], coordsArray[2]);
            found = true;
            TravelButtonPlugin.LogInfo($"EnsureSceneAndTeleport: using explicit coords for '{cityName}' = {targetPos}");
        }

        // Fallback: search transforms
        if (!found)
        {
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
                        TravelButtonPlugin.LogInfo($"EnsureSceneAndTeleport: fallback matched transform '{tr.name}' -> {targetPos}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("EnsureSceneAndTeleport: transform search failed: " + ex);
            }
        }

        if (!found)
        {
            TravelButtonPlugin.LogError($"EnsureSceneAndTeleport: no target position could be determined for '{cityName}'. Aborting.");
            callback?.Invoke(false);
            yield break;
        }

        // Ground/clear the position (no yields)
        try
        {
            targetPos = TeleportHelpers.GetGroundedPosition(targetPos);
        }
        catch
        {
            try { targetPos = TeleportHelpers.EnsureClearance(targetPos); } catch { }
        }

        // yield one frame and attempt teleport
        yield return null;

        bool relocated = false;
        try
        {
            relocated = TeleportHelpers.AttemptTeleportToPositionSafe(targetPos);
            if (relocated)
            {
                TravelButtonPlugin.LogInfo($"EnsureSceneAndTeleport: teleport to '{cityName}' succeeded at {targetPos}");
            }
            else
            {
                TravelButtonPlugin.LogWarning($"EnsureSceneAndTeleport: teleport to '{cityName}' failed at {targetPos}");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("EnsureSceneAndTeleport: teleport exception: " + ex);
            relocated = false;
        }

        callback?.Invoke(relocated);
    }

    // Place this inside TeleportHelpersBehaviour (or add to an existing partial class).
    public IEnumerator ReenableComponentsAfterDelay(GameObject go, List<Behaviour> disabled, List<(Rigidbody rb, bool originalIsKinematic)> changedRigidbodies, float delaySec = 0.25f)
    {
        TeleportHelpers.ReenableInProgress = true;

        if (go == null)
            yield break;

        float waited = 0f;
        while (waited < delaySec)
        {
            yield return null;
            waited += Time.deltaTime;
        }

        try
        {
            // Re-enable previously disabled behaviour scripts
            if (disabled != null)
            {
                foreach (var b in disabled)
                {
                    try { if (b != null) b.enabled = true; } catch { }
                }
            }

            // Restore original isKinematic for rigidbodies
            if (changedRigidbodies != null)
            {
                foreach (var pair in changedRigidbodies)
                {
                    try
                    {
                        if (pair.rb != null)
                        {
                            pair.rb.isKinematic = pair.originalIsKinematic;
                            TravelButtonPlugin.LogInfo($"ReenableComponentsAfterDelay: Restored Rigidbody.isKinematic={pair.originalIsKinematic} on '{pair.rb.gameObject.name}'.");
                        }
                    }
                    catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            TravelButtonPlugin.LogWarning("ReenableComponentsAfterDelay: exception while re-enabling: " + ex);
        }

        TeleportHelpers.ReenableInProgress = false;
        yield break;
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