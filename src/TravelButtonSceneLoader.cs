using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Scene loader + teleport coroutine that reliably teleports the player to a chosen finalPos
/// after the requested scene is loaded and settled. This class exposes public IEnumerator
/// LoadSceneAndTeleportCoroutine(...) which the SceneLoaderInvoker will start for you.
/// </summary>
public class TravelButtonSceneLoader : MonoBehaviour
{
    // Wrapper that matches UI callsite: StartCoroutine(LoadSceneAndTeleportCoroutine(city, cost, coordsHint, haveCoordsHint));
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        if (haveCoordsHint && cityObj != null)
        {
            try
            {
                TrySetFloatArrayFieldOrProp(cityObj, new string[] { "coords", "Coords", "position", "Position" }, new float[] { coordsHint.x, coordsHint.y, coordsHint.z });
                TBLog.Info($"LoadSceneAndTeleportCoroutine(wrapper): applied coordsHint [{coordsHint.x}, {coordsHint.y}, {coordsHint.z}] via reflection (if target field existed).");
            }
            catch (Exception ex)
            {
                TBLog.Warn("LoadSceneAndTeleportCoroutine(wrapper): could not apply coordsHint to city (reflection): " + ex.Message);
            }
        }

        // Delegate to main coroutine
        yield return StartCoroutine(LoadSceneAndTeleportCoroutine(cityObj));
    }

    // Main coroutine: loads scene and picks a safe final position, with improved grounding, waiting and safeguards
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj)
    {
        // Extract sceneName and coordsHint (if present) from the city object using reflection helpers.
        string sceneName = TryGetStringFieldOrProp(cityObj, new[] { "sceneName", "SceneName", "scene", "Scene", "targetScene", "TargetScene" });
        float[] coordsArr = TryGetFloatArrayFieldOrProp(cityObj, new[] { "coords", "Coords", "position", "Position" });
        Vector3 coordsHint = Vector3.zero;
        bool haveCoordsHint = false;
        if (coordsArr != null && coordsArr.Length >= 3)
        {
            coordsHint = new Vector3(coordsArr[0], coordsArr[1], coordsArr[2]);
            haveCoordsHint = true;
        }

        if (string.IsNullOrEmpty(sceneName))
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine: sceneName is null/empty — aborting.");
            yield break;
        }

        TBLog.Info($"LoadSceneAndTeleportCoroutine: ENTER scene='{sceneName}', haveCoordsHint={haveCoordsHint}, coords={coordsHint}");

        // Start async load
        AsyncOperation async;
        try
        {
            async = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (async == null)
            {
                TBLog.Warn($"LoadSceneAndTeleportCoroutine: SceneManager.LoadSceneAsync returned null for '{sceneName}'.");
                TryNotifyPlayer("Teleport failed: could not load destination scene.");
                yield break;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine: exception while starting LoadSceneAsync: " + ex.Message);
            TryNotifyPlayer("Teleport failed: could not start scene load.");
            yield break;
        }

        // Wait for ready-to-activate / activation with bounded timeouts
        float lastLoggedProgress = -1f;
        float activationTimeout = 12.0f;
        float progressWatchStart = Time.realtimeSinceStartup;
        while (async.progress < 0.9f && !async.isDone)
        {
            if (Mathf.Abs(async.progress - lastLoggedProgress) > 0.01f)
            {
                TBLog.Info($"LoadSceneAndTeleportCoroutine: loading '{sceneName}' progress={async.progress:F2}");
                lastLoggedProgress = async.progress;
            }

            if (Time.realtimeSinceStartup - progressWatchStart > 60f)
            {
                TBLog.Warn($"LoadSceneAndTeleportCoroutine: loading '{sceneName}' taking >60s at progress={async.progress:F2}; continuing to wait.");
                progressWatchStart = Time.realtimeSinceStartup;
            }

            yield return null;
        }

        TBLog.Info($"LoadSceneAndTeleportCoroutine: scene '{sceneName}' reached ready-to-activate (progress={async.progress:F2}).");

        float activateStart = Time.realtimeSinceStartup;
        while (!async.isDone)
        {
            if (Time.realtimeSinceStartup - activateStart > activationTimeout)
            {
                TBLog.Warn($"LoadSceneAndTeleportCoroutine: scene activation for '{sceneName}' did not complete within {activationTimeout}s. Proceeding to checks anyway.");
                break;
            }
            yield return null;
        }

        // Ensure SceneManager reports the scene loaded/active
        Scene loadedScene = SceneManager.GetSceneByName(sceneName);
        bool sceneLoaded = loadedScene.isLoaded;
        bool sceneActive = SceneManager.GetActiveScene().name == sceneName;
        TBLog.Info($"LoadSceneAndTeleportCoroutine: requested='{sceneName}', loaded.name='{loadedScene.name}', isLoaded={sceneLoaded}, isActive={sceneActive}");

        // Small grace period allowing Awake/Start
        float graceWait = 0.5f;
        float graceStart = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - graceStart < graceWait)
            yield return null;

        // --- REPLACED SECTION: prefer coordsHint over anchor lookups ---

        Vector3 finalPos = Vector3.zero;
        bool haveFinalPos = false;

        if (haveCoordsHint)
        {
            // Prefer explicit coordinates when provided; do not attempt GameObject anchor lookups.
            TBLog.Info($"LoadSceneAndTeleportCoroutine: using coordsHint {coordsHint} as teleport target.");
            finalPos = coordsHint;
            haveFinalPos = true;
        }
        else
        {
            // No coords hint: run the existing heuristic spawn-anchor search (by names, scene roots).
            TBLog.Info("LoadSceneAndTeleportCoroutine: no coords hint provided; performing heuristic spawn-anchor search.");
            string[] spawnNames = new[] { "PlayerSpawn", "PlayerSpawnPoint", "PlayerStart", "StartPosition", "SpawnPoint", "Spawn", "PlayerStartPoint", "Anchor" };
            foreach (var s in spawnNames)
            {
                try
                {
                    var go = GameObject.Find(s);
                    if (go != null)
                    {
                        finalPos = go.transform.position;
                        haveFinalPos = true;
                        TBLog.Info($"LoadSceneAndTeleportCoroutine: heuristic anchor '{s}' found at {finalPos}; using as teleport target.");
                        break;
                    }
                }
                catch { }
            }

            if (!haveFinalPos)
            {
                try
                {
                    var roots = SceneManager.GetSceneByName(sceneName).GetRootGameObjects();
                    if (roots != null && roots.Length > 0)
                    {
                        foreach (var r in roots)
                        {
                            if (r == null) continue;
                            var name = r.name ?? "";
                            if (name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            finalPos = r.transform.position;
                            haveFinalPos = true;
                            TBLog.Info($"LoadSceneAndTeleportCoroutine: using root GameObject '{r.name}' at {finalPos} as fallback teleport target.");
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        if (!haveFinalPos)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine: could not determine any plausible teleport target; aborting teleport.");
            TryNotifyPlayer("Teleport failed: destination not ready.");
            yield break;
        }

        // Ground probe attempt (try to prefer nearby NavMesh/ground if helper exists)
        try
        {
            bool grounded = false;
            Vector3 groundedPos = Vector3.zero;
            // Try TravelButtonUI.TryFindNearestNavMeshOrGround if present
            var tbuiType = typeof(TravelButtonUI);
            var tryFindMethod = tbuiType.GetMethod("TryFindNearestNavMeshOrGround", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (tryFindMethod != null)
            {
                var parameters = new object[] { finalPos, null, 15f, 400f };
                // Some overloads may use out Vector3; use dynamic invocation pattern via reflection
                var methodParams = tryFindMethod.GetParameters();
                if (methodParams.Length >= 2)
                {
                    // We'll try to call signature: bool TryFindNearestNavMeshOrGround(Vector3 in, out Vector3 outPos, float navSearchRadius = 15f, float maxGroundRay = 400f)
                    var outPos = new object[] { finalPos, Vector3.zero, 15f, 400f };
                    var result = tryFindMethod.Invoke(null, outPos);
                    if (result is bool ok && ok)
                    {
                        grounded = true;
                        groundedPos = (Vector3)outPos[1];
                    }
                }
            }
            else
            {
                // fallback: if TravelButtonUI has a public static wrapper we can call directly, attempt it
                try
                {
                    if (TravelButtonUI.TryFindNearestNavMeshOrGround(finalPos, out Vector3 g, navSearchRadius: 15f, maxGroundRay: 400f))
                    {
                        grounded = true;
                        groundedPos = g;
                    }
                }
                catch { /* ignore */ }
            }

            if (grounded)
            {
                TBLog.Info($"LoadSceneAndTeleportCoroutine: grounding probe adjusted finalPos to {groundedPos} (raw {finalPos}).");
                finalPos = groundedPos;
            }
            else
            {
                TBLog.Info($"LoadSceneAndTeleportCoroutine: grounding probe did not find nearby NavMesh/ground for {finalPos}.");
            }
        }
        catch (Exception exProbe)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine: grounding probe threw: " + exProbe.Message);
        }

        // Teleport attempts with bounded retries using TeleportManager's safe placement if available
        // Teleport attempts with bounded retries using TeleportManager's safe placement if available
        const int maxTeleportAttempts = 3;
        int attempt = 0;
        bool teleported = false;

        while (attempt < maxTeleportAttempts && !teleported)
        {
            attempt++;
            TBLog.Info($"LoadSceneAndTeleportCoroutine: Attempting safe placement to {finalPos} (attempt {attempt}/{maxTeleportAttempts}).");

            bool placementSucceeded = false;

            // Obtain TeleportManager.Instance outside of any yield-producing scope.
            TeleportManager tmRef = null;
            try
            {
                tmRef = TeleportManager.Instance;
            }
            catch (Exception ex)
            {
                TBLog.Warn("LoadSceneAndTeleportCoroutine: error reading TeleportManager.Instance: " + ex.Message);
                tmRef = null;
            }

            if (tmRef != null)
            {
                // Yield OUTSIDE of any try/catch that has a catch clause to satisfy the compiler.
                yield return StartCoroutine(tmRef.PlacePlayerUsingSafeRoutine(finalPos, moved => placementSucceeded = moved));
            }
            else
            {
                // Fallback: try legacy synchronous placement (no yields here)
                try
                {
                    bool legacyOk = false;
                    try
                    {
                        legacyOk = TravelButtonUI.AttemptTeleportToPositionSafe(finalPos);
                    }
                    catch
                    {
                        legacyOk = false;
                    }

                    placementSucceeded = legacyOk;
                    if (legacyOk)
                    {
                        TBLog.Info("LoadSceneAndTeleportCoroutine: legacy AttemptTeleportToPositionSafe reported success.");
                    }
                    else
                    {
                        TBLog.Warn($"LoadSceneAndTeleportCoroutine: legacy AttemptTeleportToPositionSafe returned false on attempt {attempt}.");
                    }
                }
                catch (Exception exLegacy)
                {
                    TBLog.Warn("LoadSceneAndTeleportCoroutine: legacy placement threw: " + exLegacy);
                }
            }

            if (placementSucceeded)
            {
                teleported = true;
                TBLog.Info("LoadSceneAndTeleportCoroutine: Safe placement succeeded.");
                break;
            }
            else
            {
                TBLog.Warn($"LoadSceneAndTeleportCoroutine: safe placement attempt {attempt} failed.");
            }

            float retryDelay = 0.25f + 0.15f * attempt;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < retryDelay)
                yield return null;
        }

        if (!teleported)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine: all placement attempts failed. Notifying player.");
            TryNotifyPlayer("Teleport failed: could not place player safely in destination.");
            yield break;
        }

        // small stabilization delay
        float postDelay = 0.35f;
        float pstart = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - pstart < postDelay)
            yield return null;

        TBLog.Info("LoadSceneAndTeleportCoroutine: teleport completed and scene stabilized.");

        yield break;
    }

    // ---- Reflection helpers (copy your existing implementations) ----
    private static string TryGetStringFieldOrProp(object obj, string[] candidateNames)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    if (v != null) return v.ToString();
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead)
                {
                    var v = p.GetValue(obj, null);
                    if (v != null) return v.ToString();
                }
            }
            catch { }
        }
        return null;
    }

    private static float[] TryGetFloatArrayFieldOrProp(object obj, string[] candidateNames)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    if (v is float[] fa) return fa;
                    if (v is Vector3 vv) return new float[] { vv.x, vv.y, vv.z };
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead)
                {
                    var v = p.GetValue(obj, null);
                    if (v is float[] pa) return pa;
                    if (v is Vector3 pv) return new float[] { pv.x, pv.y, pv.z };
                }
            }
            catch { }
        }
        return null;
    }

    private static bool TrySetFloatArrayFieldOrProp(object obj, string[] candidateNames, float[] value)
    {
        if (obj == null || value == null || value.Length < 3) return false;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(float[]))
                {
                    f.SetValue(obj, new float[] { value[0], value[1], value[2] });
                    return true;
                }
                if (f != null && f.FieldType == typeof(Vector3))
                {
                    f.SetValue(obj, new Vector3(value[0], value[1], value[2]));
                    return true;
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite)
                {
                    if (p.PropertyType == typeof(float[]))
                    {
                        p.SetValue(obj, new float[] { value[0], value[1], value[2] }, null);
                        return true;
                    }
                    if (p.PropertyType == typeof(Vector3))
                    {
                        p.SetValue(obj, new Vector3(value[0], value[1], value[2]), null);
                        return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }

    private void TryNotifyPlayer(string msg)
    {
        try { TravelButtonPlugin.ShowPlayerNotification?.Invoke(msg); } catch { }
    }
}