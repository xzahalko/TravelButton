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

    // Helper: best-effort safe reader for logging the player's current position.
    // Keep this private and simple — it's only for debug messages.
    private Vector3 GetPlayerPositionDebug()
    {
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go != null) return go.transform.position;

            var cc = UnityEngine.Object.FindObjectOfType<CharacterController>();
            if (cc != null && cc.transform != null) return cc.transform.position;

            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                if (t.name.IndexOf("Player", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.position;
            }
        }
        catch { /* swallow to avoid throwing from a debug helper */ }

        return Vector3.zero;
    }

    private void FinishTeleport(bool success)
    {
        // Safe cleanup helper to replace the previous finally block.
        _isSceneTransitionInProgress = false;
        try { OnTeleportFinished?.Invoke(success); } catch { }
        TBLog.Info("TeleportManager: transition flag cleared and OnTeleportFinished invoked.");
    }

    private IEnumerator LoadSceneAndTeleportCoroutine(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost)
    {
        _isSceneTransitionInProgress = true;
        bool teleported = false;

        TBLog.Info($"TeleportManager: starting async load for scene '{sceneName}' (cost={cost}). haveCoordsHint={haveCoordsHint}, coordsHint={coordsHint}");

        // Begin async load
        AsyncOperation async;
        try
        {
            async = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (async == null)
            {
                TBLog.Warn($"TeleportManager: SceneManager.LoadSceneAsync returned null for '{sceneName}'.");
                TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not load destination scene.");
                FinishTeleport(false);
                yield break;
            }

            TBLog.Info($"TeleportManager: initiated LoadSceneAsync for '{sceneName}'. allowSceneActivation={async.allowSceneActivation}, initialProgress={async.progress:F2}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManager: exception while starting LoadSceneAsync: " + ex.ToString());
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not start scene load.");
            FinishTeleport(false);
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

        // Log root GameObjects for diagnostics
        try
        {
            var roots = loadedScene.GetRootGameObjects();
            TBLog.Info($"TeleportManager: scene '{sceneName}' root object count = {roots?.Length ?? 0}");
            if (roots != null && roots.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                int sample = Math.Min(12, roots.Length);
                for (int i = 0; i < sample; i++)
                {
                    var r = roots[i];
                    if (r != null) sb.Append(r.name).Append(", ");
                }
                if (roots.Length > sample) sb.Append("...");
                TBLog.Info($"TeleportManager: scene roots sample: {sb}");
            }
        }
        catch (Exception exRoot)
        {
            TBLog.Warn("TeleportManager: failed enumerating root GameObjects: " + exRoot.ToString());
        }

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
            TBLog.Info($"TeleportManager: attempting to find anchor '{targetGameObjectName}' in scene (timeout {readyWaitMax}s).");
            while (Time.realtimeSinceStartup - readyStart < readyWaitMax)
            {
                try { anchor = GameObject.Find(targetGameObjectName); } catch (Exception exFind) { anchor = null; TBLog.Warn($"TeleportManager: GameObject.Find threw for '{targetGameObjectName}': {exFind.Message}"); }
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
            TBLog.Info($"TeleportManager: using anchor position {finalPos} as final target.");
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
                catch (Exception exFind2)
                {
                    TBLog.Warn($"TeleportManager: exception finding spawn anchor '{s}': {exFind2}");
                }
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
                catch (Exception exFallback)
                {
                    TBLog.Warn("TeleportManager: exception during root fallback search: " + exFallback.ToString());
                }
            }
        }

        if (!haveFinalPos)
        {
            TBLog.Warn("TeleportManager: could not determine any plausible teleport target; aborting teleport.");
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: destination not ready.");
            FinishTeleport(false);
            yield break;
        }

        TBLog.Info($"TeleportManager: preliminary finalPos = {finalPos} (haveCoordsHint={haveCoordsHint})");

        // Ground probe attempt
        try
        {
            bool grounded = false;
            Vector3 groundedPos = Vector3.zero;
            string groundingMethod = "<none>";

            // Try TravelButtonUI.TryFindNearestNavMeshOrGround (safe usage)
            try
            {
                groundingMethod = "TravelButtonUI.TryFindNearestNavMeshOrGround";
                if (TravelButtonUI.TryFindNearestNavMeshOrGround(finalPos, out Vector3 g, navSearchRadius: 15f, maxGroundRay: 400f))
                {
                    grounded = true;
                    groundedPos = g;
                    TBLog.Info($"TeleportManager: grounding helper returned grounded={grounded}, groundedPos={groundedPos}");
                }
                else
                {
                    TBLog.Info("TeleportManager: grounding helper did not find nearby NavMesh/ground.");
                }
            }
            catch (Exception exGroundCall)
            {
                TBLog.Warn("TeleportManager: grounding helper call threw: " + exGroundCall.ToString());
            }

            if (grounded)
            {
                // If have coords hint, respect Y from hint (skip replacing with groundedPos)
                if (!haveCoordsHint)
                {
                    TBLog.Info($"TeleportManager: applying groundedPos={groundedPos} to finalPos (no coords hint).");
                    finalPos = groundedPos;
                }
                else
                {
                    TBLog.Info("TeleportManager: haveCoordsHint=true - keeping coordsHint Y and not overriding with groundedPos.");
                }
            }
        }
        catch (Exception exProbe)
        {
            TBLog.Warn("TeleportManager: grounding probe threw: " + exProbe.ToString());
        }

        TBLog.Info($"TeleportManager: final chosen teleport target = {finalPos}");

        // NEW: Use TeleportService (strategy chooser) as first attempt for placing the player.
        {
            TBLog.Info("[TeleportManager] preparing TeleportService call to attempt placement (strategy chooser).");
            bool serviceMoved = false;
            var host = TeleportHelpersBehaviour.GetOrCreateHost();
            TBLog.Info($"TeleportManager: TeleportService host present? {(host != null ? "YES" : "NO")}");

            IEnumerator serviceEnumerator = null;
            try
            {
                serviceEnumerator = TeleportService.Instance?.PlacePlayer(finalPos, moved => { serviceMoved = moved; });
                TBLog.Info($"TeleportManager: TeleportService.Instance is {(TeleportService.Instance != null ? "AVAILABLE" : "NULL")}, enumerator={(serviceEnumerator != null ? "OK" : "null")}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("[TeleportManager] TeleportService instantiation threw: " + ex.ToString());
                serviceEnumerator = null;
            }

            if (serviceEnumerator != null)
            {
                float svcStart = Time.realtimeSinceStartup;
                if (host != null)
                {
                    yield return host.StartCoroutine(serviceEnumerator);
                }
                else
                {
                    yield return StartCoroutine(serviceEnumerator);
                }
                float svcDur = Time.realtimeSinceStartup - svcStart;
                TBLog.Info($"TeleportManager: TeleportService completed, serviceMoved={serviceMoved}, duration={svcDur:F2}s");
            }
            else
            {
                TBLog.Warn("[TeleportManager] TeleportService enumerator is null; skipping service run.");
            }

            if (serviceMoved)
            {
                TBLog.Info("[TeleportManager] TeleportService placed the player successfully.");
                teleported = true;
            }
            else
            {
                TBLog.Warn("[TeleportManager] TeleportService did not report movement; falling back to SafePlacePlayerCoroutine loop.");
            }
        }

        // Fallback safe-placement loop
        if (!teleported)
        {
            const int maxTeleportAttempts = 3;
            int attempt = 0;
            while (attempt < maxTeleportAttempts && !teleported)
            {
                attempt++;
                TBLog.Info($"TeleportManager: Attempting teleport to {finalPos} (attempt {attempt}/{maxTeleportAttempts}) using PlacePlayerUsingSafeRoutine.");

                bool placementSucceeded = false;
                float callStart = Time.realtimeSinceStartup;
                yield return StartCoroutine(PlacePlayerUsingSafeRoutine(finalPos, moved =>
                {
                    placementSucceeded = moved;
                }));
                float callDur = Time.realtimeSinceStartup - callStart;

                TBLog.Info($"TeleportManager: PlacePlayerUsingSafeRoutine returned placementSucceeded={placementSucceeded} (duration={callDur:F2}s)");

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
        }

        // If all safe attempts failed, try coords-first shim fallback when appropriate
        if (!teleported)
        {
            try
            {
                TBLog.Info($"TeleportManager: safe placement exhausted. haveCoordsHint={haveCoordsHint}, anchorPresent={(anchor != null)}");
            }
            catch { }

            if (haveCoordsHint || anchor == null)
            {
                TBLog.Info("[TeleportManager] attempting coords-first fallback shim (TeleportCompatShims).");
                Vector3 before = Vector3.zero;
                Vector3 after = Vector3.zero;
                try { before = GetPlayerPositionDebug(); TBLog.Info($"TeleportManager: player position before shim: {before}"); } catch { }

                var host = TeleportHelpersBehaviour.GetOrCreateHost();
                if (host != null)
                {
                    yield return host.StartCoroutine(TeleportCompatShims.PlacePlayerViaCoords(finalPos));
                }
                else
                {
                    yield return StartCoroutine(TeleportCompatShims.PlacePlayerViaCoords(finalPos));
                }

                try { after = GetPlayerPositionDebug(); TBLog.Info($"TeleportManager: player position after shim: {after}"); } catch { }

                float movedDistance = Vector3.Distance(before, after);
                TBLog.Info($"TeleportManager: coords-first shim completed; player moved distance {movedDistance:F3}.");

                if (movedDistance > 0.05f)
                {
                    teleported = true;
                    TBLog.Info("[TeleportManager] coords-first shim appears to have moved the player; marking teleport as successful.");
                }
                else
                {
                    TBLog.Warn("[TeleportManager] coords-first shim did not appreciably move the player.");
                }
            }
            else
            {
                TBLog.Info("[TeleportManager] coords-first shim not attempted (no coords hint and anchor present).");
            }
        }

        if (!teleported)
        {
            TBLog.Warn("TeleportManager: all teleport attempts failed. Notifying player.");
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not place player safely in destination.");
            FinishTeleport(false);
            yield break;
        }

        // small stabilization delay
        float postDelay = 0.35f;
        float pstart = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - pstart < postDelay)
            yield return null;

        TBLog.Info("TeleportManager: teleport completed and scene stabilized.");

        // final cleanup and notify
        FinishTeleport(true);

        // For diagnostics - report player position after FinishTeleport
        try
        {
            var pos = GetPlayerPositionDebug();
            TBLog.Info($"TeleportManager: player position after FinishTeleport = {pos}");
        }
        catch (Exception exPos)
        {
            TBLog.Warn("TeleportManager: error reading player position after FinishTeleport: " + exPos.ToString());
        }

        yield break;
    }
}