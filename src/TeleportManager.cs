using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// TeleportManager: dedicated DontDestroyOnLoad MonoBehaviour that owns all scene-load + teleport lifecycle.
/// - Prevents overlapping transitions with a single guard.
/// - Exposes StartTeleport(sceneName, targetName, coordsHint, haveCoordsHint, cost).
/// - Runs the robust scene-ready + grounding + teleport coroutine (bounded retries/timeouts).
/// - Emits OnTeleportFinished(success) so callers (UI) can re-enable/restore state.
/// - Relies on static helpers in TravelButton (AttemptTeleportToPositionSafe, TryFindNearestNavMeshOrGround) and
///   TravelButtonPlugin.ShowPlayerNotification / TBLog for logging and player notifications.
/// </summary>
public partial class TeleportManager : MonoBehaviour
{
    public static TeleportManager Instance { get; private set; }

    // Event fired when a teleport attempt finished (success = true if teleport placed player).
    // Subscribers should not block; invoked on Unity main thread.
    public event Action<bool> OnTeleportFinished;

    private bool _isSceneTransitionInProgress = false;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Request a teleport. Returns true if the manager accepted the request and started a transition.
    /// If false is returned, the request was rejected (another transition in progress).
    /// </summary>
    public bool StartTeleport(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost = 0)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            TBLog.Warn("TeleportManager.StartTeleport: sceneName is null/empty — rejecting request.");
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured.");
            return false;
        }

        if (_isSceneTransitionInProgress)
        {
            TBLog.Warn("TeleportManager.StartTeleport: another scene transition is in progress; rejecting request.");
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport is already in progress. Please wait.");
            return false;
        }

        StartCoroutine(LoadSceneAndTeleportCoroutine(sceneName, targetGameObjectName, coordsHint, haveCoordsHint, cost));
        return true;
    }

    private IEnumerator LoadSceneAndTeleportCoroutine(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost)
    {
        _isSceneTransitionInProgress = true;
        bool teleported = false;

        try
        {
            TBLog.Info($"TeleportManager: starting async load for scene '{sceneName}' (cost={cost}).");

            // Begin async load
            AsyncOperation async;
            try
            {
                async = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                if (async == null)
                {
                    TBLog.Warn($"TeleportManager: SceneManager.LoadSceneAsync returned null for '{sceneName}'.");
                    TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not load destination scene.");
                    yield break;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManager: exception while starting LoadSceneAsync: " + ex.Message);
                TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not start scene load.");
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
                    TBLog.Info($"TeleportManager: loading '{sceneName}' progress={async.progress:F2}");
                    lastLoggedProgress = async.progress;
                }

                if (Time.realtimeSinceStartup - progressWatchStart > 60f)
                {
                    TBLog.Warn($"TeleportManager: loading '{sceneName}' taking >60s at progress={async.progress:F2}; continuing to wait.");
                    progressWatchStart = Time.realtimeSinceStartup;
                }

                yield return null;
            }

            TBLog.Info($"TeleportManager: scene '{sceneName}' reached ready-to-activate (progress={async.progress:F2}).");

            float activateStart = Time.realtimeSinceStartup;
            while (!async.isDone)
            {
                if (Time.realtimeSinceStartup - activateStart > activationTimeout)
                {
                    TBLog.Warn($"TeleportManager: scene activation for '{sceneName}' did not complete within {activationTimeout}s. Proceeding to checks anyway.");
                    break;
                }
                yield return null;
            }

            // Ensure SceneManager reports the scene loaded/active
            Scene loadedScene = SceneManager.GetSceneByName(sceneName);
            bool sceneLoaded = loadedScene.isLoaded;
            bool sceneActive = SceneManager.GetActiveScene().name == sceneName;
            TBLog.Info($"TeleportManager: requested='{sceneName}', loaded.name='{loadedScene.name}', isLoaded={sceneLoaded}, isActive={sceneActive}");

            // Small grace period allowing Awake/Start
            float graceWait = 0.5f;
            float graceStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - graceStart < graceWait)
                yield return null;

            // Wait for anchor if provided
            float readyWaitMax = 5.0f;
            float readyStart = Time.realtimeSinceStartup;
            GameObject anchor = null;
            if (!string.IsNullOrEmpty(targetGameObjectName))
            {
                TBLog.Info($"TeleportManager: attempting to find anchor '{targetGameObjectName}' in scene.");
                while (Time.realtimeSinceStartup - readyStart < readyWaitMax)
                {
                    try { anchor = GameObject.Find(targetGameObjectName); } catch { anchor = null; }
                    if (anchor != null) break;
                    yield return null;
                }
                if (anchor != null) TBLog.Info($"TeleportManager: found anchor '{targetGameObjectName}' at {anchor.transform.position}.");
                else TBLog.Info($"TeleportManager: anchor '{targetGameObjectName}' not found within {readyWaitMax}s.");
            }

            Vector3 finalPos = Vector3.zero;
            bool haveFinalPos = false;

            if (anchor != null)
            {
                finalPos = anchor.transform.position;
                haveFinalPos = true;
            }
            else if (haveCoordsHint)
            {
                TBLog.Info($"TeleportManager: using coordsHint {coordsHint} as teleport target.");
                finalPos = coordsHint;
                haveFinalPos = true;
            }
            else
            {
                TBLog.Info("TeleportManager: doing heuristic spawn-anchor search.");
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
                            TBLog.Info($"TeleportManager: heuristic anchor '{s}' found at {finalPos}; using as teleport target.");
                            break;
                        }
                    }
                    catch { }
                }

                if (!haveFinalPos)
                {
                    try
                    {
                        var roots = loadedScene.GetRootGameObjects();
                        if (roots != null && roots.Length > 0)
                        {
                            foreach (var r in roots)
                            {
                                if (r == null) continue;
                                var name = r.name ?? "";
                                if (name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                finalPos = r.transform.position;
                                haveFinalPos = true;
                                TBLog.Info($"TeleportManager: using root GameObject '{r.name}' at {finalPos} as fallback teleport target.");
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (!haveFinalPos)
            {
                TBLog.Warn("TeleportManager: could not determine any plausible teleport target; aborting teleport.");
                TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: destination not ready.");
                yield break;
            }

            // Ground probe attempt
            try
            {
                // Prefer TravelButton.TryFindNearestNavMeshOrGround if available
                if (TravelButtonUI.TryFindNearestNavMeshOrGround(finalPos, out Vector3 grounded, navSearchRadius: 15f, maxGroundRay: 400f))
                {
                    TBLog.Info($"TeleportManager: immediate probe grounded to {grounded} (raw {finalPos}). Using that as final target.");
                    finalPos = grounded;
                }
                else
                {
                    TBLog.Info($"TeleportManager: immediate probe did not find nearby NavMesh/ground for {finalPos}.");
                }
            }
            catch (Exception exProbe)
            {
                TBLog.Warn("TeleportManager: grounding probe threw: " + exProbe.Message);
            }

            // Debug logging for teleport decision
            bool anchorFound = anchor != null;
            TBLog.Debug?.Invoke($"[TeleportManager] haveCoordsHint={haveCoordsHint}, targetName={targetGameObjectName}, anchorFound={anchorFound}, finalPos={finalPos}");

            // Teleport attempts with bounded retries using coroutine-based safe placement
            const int maxTeleportAttempts = 3;
            int attempt = 0;
            while (attempt < maxTeleportAttempts && !teleported)
            {
                attempt++;
                TBLog.Info($"TeleportManager: Attempting teleport to {finalPos} (attempt {attempt}/{maxTeleportAttempts}) using SafePlacePlayerCoroutine.");
                
                bool placementSucceeded = false;
                yield return StartCoroutine(PlacePlayerUsingSafeRoutine(finalPos, moved =>
                {
                    placementSucceeded = moved;
                }));
                
                if (placementSucceeded)
                {
                    TBLog.Info("TeleportManager: PlacePlayerUsingSafeRoutine reported success.");
                    teleported = true;
                    break;
                }
                else
                {
                    TBLog.Warn($"TeleportManager: attempt {attempt} failed (PlacePlayerUsingSafeRoutine reported no movement).");
                }

                if (attempt < maxTeleportAttempts)
                {
                    float retryDelay = 0.25f + 0.15f * attempt;
                    float start = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - start < retryDelay)
                        yield return null;
                }
            }

            if (!teleported)
            {
                // Fallback: if we have coords and either no anchor was found OR the primary placement failed,
                // try the compatibility shim as last resort
                if (haveCoordsHint || !anchorFound)
                {
                    TBLog.Info($"[TeleportManager] primary placement failed; invoking coords fallback shim with finalPos={finalPos}");
                    
                    // Record player position before fallback
                    Vector3 beforeFallback = Vector3.zero;
                    bool haveBeforeFallback = TryGetPlayerPosition(out beforeFallback);
                    if (haveBeforeFallback)
                    {
                        TBLog.Debug?.Invoke($"[TeleportManager] player position before fallback: {beforeFallback}");
                    }
                    
                    var host = TeleportHelpersBehaviour.GetOrCreateHost();
                    yield return host.StartCoroutine(TeleportCompatShims.PlacePlayerViaCoords(finalPos));
                    
                    TBLog.Info($"[TeleportManager] returned from coords fallback shim");
                    
                    // Record player position after fallback
                    Vector3 afterFallback = Vector3.zero;
                    bool haveAfterFallback = TryGetPlayerPosition(out afterFallback);
                    if (haveAfterFallback)
                    {
                        TBLog.Debug?.Invoke($"[TeleportManager] player position after fallback: {afterFallback}");
                        
                        // Consider it a success if player moved significantly
                        if (haveBeforeFallback && (afterFallback - beforeFallback).sqrMagnitude > 0.01f)
                        {
                            teleported = true;
                            TBLog.Info("[TeleportManager] fallback shim succeeded - player moved");
                        }
                        else if (!haveBeforeFallback)
                        {
                            // We found player after fallback but not before - consider success
                            teleported = true;
                            TBLog.Info("[TeleportManager] fallback shim succeeded - player now located");
                        }
                    }
                }
                
                if (!teleported)
                {
                    TBLog.Warn("TeleportManager: all teleport attempts failed. Notifying player.");
                    TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not place player safely in destination.");
                    yield break;
                }
            }

            // small stabilization delay
            float postDelay = 0.35f;
            float pstart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - pstart < postDelay)
                yield return null;

            TBLog.Info("TeleportManager: teleport completed and scene stabilized.");
        }
        finally
        {
            _isSceneTransitionInProgress = false;
            try { OnTeleportFinished?.Invoke(teleported); } catch { }
            TBLog.Info("TeleportManager: transition flag cleared and OnTeleportFinished invoked.");
        }
    }
}