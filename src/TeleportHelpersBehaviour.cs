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
    // Enhanced debug versions of EnsureSceneAndTeleport and ReenableComponentsAfterDelay
    // Replace existing implementations with these to get richer runtime diagnostics.

    public IEnumerator EnsureSceneAndTeleport(object cityLike, Vector3 coordsHint, bool haveCoordsHint, Action<bool> callback)
    {
        if (cityLike == null)
        {
            TBLog.Warn("EnsureSceneAndTeleport: cityLike is null.");
            callback?.Invoke(false);
            yield break;
        }

        TBLog.Info($"EnsureSceneAndTeleport: ENTER. cityLike type={(cityLike?.GetType().FullName ?? "<null>")}, haveCoordsHint={haveCoordsHint}, coordsHint={coordsHint}");

        string cityName = null;
        string targetName = null;
        float[] coordsArray = null;

        // Reflection to extract fields/properties (no yields here)
        try
        {
            var t = cityLike.GetType();
            TBLog.Info($"EnsureSceneAndTeleport: reflecting members of type {t.FullName}");

            var nameField = t.GetField("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (nameField != null)
            {
                cityName = nameField.GetValue(cityLike) as string;
                TBLog.Info($"EnsureSceneAndTeleport: read field 'name' = '{cityName}'");
            }
            else
            {
                var nameProp = t.GetProperty("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (nameProp != null) cityName = nameProp.GetValue(cityLike) as string;
                TBLog.Info($"EnsureSceneAndTeleport: read property 'name' = '{cityName}'");
            }

            var tgField = t.GetField("targetGameObjectName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (tgField != null)
            {
                targetName = tgField.GetValue(cityLike) as string;
                TBLog.Info($"EnsureSceneAndTeleport: read field 'targetGameObjectName' = '{targetName}'");
            }
            else
            {
                var tgProp = t.GetProperty("targetGameObjectName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (tgProp != null) targetName = tgProp.GetValue(cityLike) as string;
                TBLog.Info($"EnsureSceneAndTeleport: read property 'targetGameObjectName' = '{targetName}'");
            }

            var coordsField = t.GetField("coords", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (coordsField != null)
            {
                coordsArray = coordsField.GetValue(cityLike) as float[];
                TBLog.Info($"EnsureSceneAndTeleport: read field 'coords' = {(coordsArray == null ? "<null>" : $"[{string.Join(", ", coordsArray)}]")}");
            }
            else
            {
                var coordsProp = t.GetProperty("coords", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (coordsProp != null) coordsArray = coordsProp.GetValue(cityLike) as float[];
                TBLog.Info($"EnsureSceneAndTeleport: read property 'coords' = {(coordsArray == null ? "<null>" : $"[{string.Join(", ", coordsArray)}]")}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("EnsureSceneAndTeleport: reflection read failed: " + ex.ToString());
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
            int pollCount = 0;

            TBLog.Info($"EnsureSceneAndTeleport: attempting to resolve target GameObject by name '{targetName}' (timeout={timeout}s).");

            while (waited < timeout)
            {
                try
                {
                    foundGO = GameObject.Find(targetName);
                    pollCount++;
                    if (foundGO != null)
                    {
                        TBLog.Info($"EnsureSceneAndTeleport: GameObject.Find returned a match on attempt #{pollCount} after {waited:F2}s.");
                        break;
                    }
                }
                catch (Exception exFind)
                {
                    TBLog.Warn($"EnsureSceneAndTeleport: GameObject.Find threw for '{targetName}' on attempt #{pollCount}: {exFind}");
                }

                waited += poll;
                yield return new WaitForSeconds(poll);
            }

            if (foundGO != null)
            {
                try
                {
                    targetPos = foundGO.transform.position;
                    found = true;
                    TBLog.Info($"EnsureSceneAndTeleport: found target '{targetName}' at {targetPos} for '{cityName}' after {pollCount} polls.");
                }
                catch (Exception exPos)
                {
                    TBLog.Warn("EnsureSceneAndTeleport: failed reading position from found GameObject: " + exPos);
                    found = false;
                }
            }
            else
            {
                TBLog.Warn($"EnsureSceneAndTeleport: target '{targetName}' not found for '{cityName}' after {pollCount} polls - will try coords fallback.");
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
            TBLog.Info("EnsureSceneAndTeleport: fallback transform search started (FindObjectsOfType<Transform>).");
            var swFallback = TBPerf.StartTimer();
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                int matchCount = 0;
                int checkedCount = 0;
                foreach (var tr in all)
                {
                    checkedCount++;
                    if (tr == null || string.IsNullOrEmpty(tr.name)) continue;
                    if (!string.IsNullOrEmpty(cityName) && tr.name.IndexOf(cityName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetPos = tr.position;
                        found = true;
                        matchCount++;
                        // existing selection logic...
                    }
                }
                TBPerf.Log("FallbackTransformSearch", swFallback, $"checked={checkedCount}, matches={matchCount}");
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogError("EnsureSceneAndTeleport: fallback transform exception: " + ex.ToString());
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
            var beforeGround = targetPos;
            targetPos = TeleportHelpers.GetGroundedPosition(targetPos);
            TBLog.Info($"EnsureSceneAndTeleport: TeleportHelpers.GetGroundedPosition: before={beforeGround} after={targetPos}");
        }
        catch (Exception exGround)
        {
            TBLog.Warn("EnsureSceneAndTeleport: GetGroundedPosition threw: " + exGround.ToString());
            try
            {
                var beforeClear = targetPos;
                targetPos = TeleportHelpers.EnsureClearance(targetPos);
                TBLog.Info($"EnsureSceneAndTeleport: TeleportHelpers.EnsureClearance: before={beforeClear} after={targetPos}");
            }
            catch (Exception exClear)
            {
                TBLog.Warn("EnsureSceneAndTeleport: EnsureClearance threw: " + exClear.ToString());
            }
        }

        // yield one frame and attempt teleport using coroutine-based safe placement
        yield return null;

        bool relocated = false;
        double mgrCallStart = 0.0;
        // Try to use TeleportManager's safe placement routine if available
        TeleportManager mgr = null;
        try
        {
            mgr = TeleportManager.Instance;
            TBLog.Info($"EnsureSceneAndTeleport: TeleportManager.Instance lookup returned {(mgr != null ? "FOUND" : "null")}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("EnsureSceneAndTeleport: TeleportManager.Instance threw: " + ex.ToString());
            mgr = null;
        }

        if (mgr != null)
        {
            // Use coroutine-based placement
            TBLog.Info($"EnsureSceneAndTeleport: invoking TeleportManager.PlacePlayerUsingSafeRoutine for targetPos={targetPos}");
            mgrCallStart = Time.realtimeSinceStartup;
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
            double mgrCallDur = Time.realtimeSinceStartup - mgrCallStart;
            TBLog.Info($"EnsureSceneAndTeleport: TeleportManager.PlacePlayerUsingSafeRoutine completed in {mgrCallDur:F2}s, relocated={relocated}");
        }
        else
        {
            TBLog.Info("EnsureSceneAndTeleport: TeleportManager not available; using TravelButtonUI.AttemptTeleportToPositionSafe fallback.");
            // Fallback: try to use TravelButtonUI.AttemptTeleportToPositionSafe if TeleportManager not available
            try
            {
                var fallbackStart = Time.realtimeSinceStartup;
                relocated = TravelButtonUI.AttemptTeleportToPositionSafe(targetPos);
                var fallbackDur = Time.realtimeSinceStartup - fallbackStart;
                if (relocated)
                {
                    TBLog.Info($"EnsureSceneAndTeleport: teleport to '{cityName}' succeeded at {targetPos} (fallback) in {fallbackDur:F2}s");
                }
                else
                {
                    TBLog.Warn($"EnsureSceneAndTeleport: teleport to '{cityName}' failed at {targetPos} (fallback) in {fallbackDur:F2}s");
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogError("EnsureSceneAndTeleport: teleport exception: " + ex.ToString());
                relocated = false;
            }
        }

        try
        {
            TBLog.Info($"EnsureSceneAndTeleport: invoking callback with result={relocated} for '{cityName}'");
            callback?.Invoke(relocated);
        }
        catch (Exception exCb)
        {
            TBLog.Warn("EnsureSceneAndTeleport: callback threw: " + exCb.ToString());
        }

        TBLog.Info("EnsureSceneAndTeleport: EXIT.");
        yield break;
    }

    // Place this inside TeleportHelpersBehaviour (or add to an existing partial class).
    // Re-enable components and restore rigidbody flags after a short delay.
    // This coroutine toggles TeleportHelpers.ReenableInProgress for caller synchronization.
    public IEnumerator ReenableComponentsAfterDelay(GameObject moved, List<Behaviour> disabledBehaviours, List<(Rigidbody rb, bool originalIsKinematic)> changedRigidbodies, float delay)
    {
        try
        {
            TeleportHelpers.ReenableInProgress = true;
            TBLog.Info($"ReenableComponentsAfterDelay: starting. moved='{moved?.name}', disabledBehaviours={(disabledBehaviours?.Count ?? 0)}, changedRigidbodies={(changedRigidbodies?.Count ?? 0)}, delay={delay:F2}");
        }
        catch (Exception exFlag)
        {
            TBLog.Warn("ReenableComponentsAfterDelay: failed setting ReenableInProgress: " + exFlag.ToString());
        }

        // Wait the configured delay (real time to avoid being paused)
        if (delay > 0f)
        {
            TBLog.Info($"ReenableComponentsAfterDelay: waiting realtime delay {delay:F2}s");
            yield return new WaitForSecondsRealtime(delay);
        }
        else
        {
            yield return null;
        }

        // Re-enable behaviours (reverse order for safety)
        try
        {
            if (disabledBehaviours != null)
            {
                TBLog.Info("ReenableComponentsAfterDelay: re-enabling behaviours...");
                int reenabled = 0;
                foreach (var b in disabledBehaviours)
                {
                    if (b == null) continue;
                    try
                    {
                        var goName = b.gameObject != null ? b.gameObject.name : "<unknown>";
                        b.enabled = true;
                        reenabled++;
                        TBLog.Info($"ReenableComponentsAfterDelay: re-enabled {b.GetType().Name} on '{goName}'.");
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"ReenableComponentsAfterDelay: error re-enabling behaviour {b?.GetType().Name}: {ex}");
                    }
                }
                TBLog.Info($"ReenableComponentsAfterDelay: re-enabled {reenabled}/{disabledBehaviours.Count} behaviours.");
            }
            else
            {
                TBLog.Info("ReenableComponentsAfterDelay: no disabled behaviours to re-enable.");
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
                TBLog.Info("ReenableComponentsAfterDelay: restoring rigidbody flags...");
                int restored = 0;
                foreach (var tup in changedRigidbodies)
                {
                    try
                    {
                        if (tup.rb != null)
                        {
                            var goName = tup.rb.gameObject != null ? tup.rb.gameObject.name : "<unknown>";
                            tup.rb.isKinematic = tup.originalIsKinematic;
                            restored++;
                            TBLog.Info($"ReenableComponentsAfterDelay: Restored Rigidbody.isKinematic={tup.originalIsKinematic} on '{goName}'.");
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("ReenableComponentsAfterDelay: error restoring a rigidbody: " + ex.ToString());
                    }
                }
                TBLog.Info($"ReenableComponentsAfterDelay: restored {restored}/{changedRigidbodies.Count} rigidbodies.");
            }
            else
            {
                TBLog.Info("ReenableComponentsAfterDelay: no rigidbodies to restore.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ReenableComponentsAfterDelay: error restoring rigidbodies: " + ex.Message);
        }

        try
        {
            TeleportHelpers.ReenableInProgress = false;
            TBLog.Info("ReenableComponentsAfterDelay: completed and TeleportHelpers.ReenableInProgress cleared.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("ReenableComponentsAfterDelay: failed clearing ReenableInProgress: " + ex.ToString());
        }

        yield break;
    }
}
