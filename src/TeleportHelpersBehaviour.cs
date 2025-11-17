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
    private static TeleportHelpersBehaviour _instance;
    public static TeleportHelpersBehaviour GetOrCreateHost()
    {
        if (_instance != null) return _instance;
        var go = new GameObject("TeleportHelpersHost");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TeleportHelpersBehaviour>();
        return _instance;
    }
    private void Awake()
    {
        try { DontDestroyOnLoad(this.gameObject); } catch { }
    }

    // Watch a GameObject for post-teleport changes.
    // Logs if the world position changes during 'durationSec' seconds, checking every frame.
    // Watch the moved object's position for T seconds and log if it changes
    public IEnumerator WatchPositionAfterTeleport(GameObject moved, Vector3 expected, float watchSeconds)
    {
        if (moved == null) yield break;
        float end = Time.realtimeSinceStartup + watchSeconds;
        Vector3 last = moved.transform.position;
        while (Time.realtimeSinceStartup < end)
        {
            if (moved == null) yield break;
            Vector3 cur = moved.transform.position;
            if ((cur - expected).sqrMagnitude > 0.01f && (cur - last).sqrMagnitude > 0.001f)
            {
//                TBLog.Warn($"WatchPositionAfterTeleport: detected external change of '{moved.name}' from expected {expected} to {cur}");
                last = cur;
            }
            yield return null;
        }
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
            TBLog.Warn("EnsureSceneAndTeleport: cityLike is null.");
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
            TBLog.Warn("EnsureSceneAndTeleport: reflection read failed: " + ex);
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
                TBLog.Info($"EnsureSceneAndTeleport: found target '{targetName}' at {targetPos} for '{cityName}'");
            }
            else
            {
                TBLog.Warn($"EnsureSceneAndTeleport: target '{targetName}' not found for '{cityName}' - will try coords fallback.");
            }
        }

        // Use coords hint or reflected coords
        if (!found && haveCoordsHint)
        {
            targetPos = coordsHint;
            found = true;
            TBLog.Info($"EnsureSceneAndTeleport: using coordsHint for '{cityName}' = {targetPos}");
        }
        else if (!found && coordsArray != null && coordsArray.Length >= 3)
        {
            targetPos = new Vector3(coordsArray[0], coordsArray[1], coordsArray[2]);
            found = true;
            TBLog.Info($"EnsureSceneAndTeleport: using explicit coords for '{cityName}' = {targetPos}");
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
                        TBLog.Info($"EnsureSceneAndTeleport: fallback matched transform '{tr.name}' -> {targetPos}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("EnsureSceneAndTeleport: transform search failed: " + ex);
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

        // yield one frame and attempt teleport using coroutine-based safe placement
        yield return null;

        bool relocated = false;
        
        // Try to use TeleportManager's safe placement routine if available
        TeleportManager mgr = null;
        try
        {
            mgr = TeleportManager.Instance;
        }
        catch (Exception ex)
        {
            TBLog.Warn("EnsureSceneAndTeleport: TeleportManager.Instance threw: " + ex);
        }

        if (mgr != null)
        {
            // Use coroutine-based placement
            yield return mgr.StartCoroutine(mgr.PlacePlayerUsingSafeRoutine(targetPos, moved =>
            {
                relocated = moved;
                if (relocated)
                {
                    TBLog.Info($"EnsureSceneAndTeleport: teleport to '{cityName}' succeeded at {targetPos}");
                }
                else
                {
                    TBLog.Warn($"EnsureSceneAndTeleport: teleport to '{cityName}' failed at {targetPos}");
                }
            }));
        }
        else
        {
            // Fallback: try to use TravelButtonUI.AttemptTeleportToPositionSafe if TeleportManager not available
            try
            {
                relocated = TravelButtonUI.AttemptTeleportToPositionSafe(targetPos);
                if (relocated)
                {
                    TBLog.Info($"EnsureSceneAndTeleport: teleport to '{cityName}' succeeded at {targetPos} (fallback)");
                }
                else
                {
                    TBLog.Warn($"EnsureSceneAndTeleport: teleport to '{cityName}' failed at {targetPos} (fallback)");
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogError("EnsureSceneAndTeleport: teleport exception: " + ex);
                relocated = false;
            }
        }

        callback?.Invoke(relocated);
    }

    // Place this inside TeleportHelpersBehaviour (or add to an existing partial class).
    // Re-enable components and restore rigidbody flags after a short delay.
    // This coroutine toggles TeleportHelpers.ReenableInProgress for caller synchronization.
    public IEnumerator ReenableComponentsAfterDelay(GameObject moved, List<Behaviour> disabledBehaviours, List<(Rigidbody rb, bool originalIsKinematic)> changedRigidbodies, float delay)
    {
        try
        {
            TeleportHelpers.ReenableInProgress = true;
        }
        catch { }

        // Wait the configured delay (real time to avoid being paused)
        if (delay > 0f)
            yield return new WaitForSecondsRealtime(delay);
        else
            yield return null;

        // Re-enable behaviours (reverse order for safety)
        try
        {
            if (disabledBehaviours != null)
            {
                foreach (var b in disabledBehaviours)
                {
                    if (b == null) continue;
                    try
                    {
                        b.enabled = true;
                        //TBLog.Info($"ReenableComponentsAfterDelay: re-enabled {b.GetType().Name} on '{b.gameObject.name}'.");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ReenableComponentsAfterDelay: error re-enabling behaviours: " + ex.Message);
        }

        // Restore rigidbody isKinematic flags
        try
        {
            if (changedRigidbodies != null)
            {
                foreach (var tup in changedRigidbodies)
                {
                    try
                    {
                        if (tup.rb != null)
                        {
                            tup.rb.isKinematic = tup.originalIsKinematic;
                            //TBLog.Info($"ReenableComponentsAfterDelay: Restored Rigidbody.isKinematic={tup.originalIsKinematic} on '{tup.rb.gameObject.name}'.");
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ReenableComponentsAfterDelay: error restoring rigidbodies: " + ex.Message);
        }

        try
        {
            TeleportHelpers.ReenableInProgress = false;
        }
        catch { }

        yield break;
    }
}
