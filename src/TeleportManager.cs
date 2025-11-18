using NodeCanvas.Tasks.Actions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
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
    /// Ensure there is a TeleportManager instance. If not present, create a GameObject and add the component.
    /// Returns the instance (new or existing).
    /// </summary>
    public static TeleportManager EnsureInstance()
    {
        if (Instance != null) return Instance;

        try
        {
            var go = new GameObject("TeleportManager");
            // Create component; Awake() on the component will run immediately and set Instance + DontDestroyOnLoad.
            var tm = go.AddComponent<TeleportManager>();
            // Defensive: if Awake didn't run for some reason, set Instance and DontDestroyOnLoad here.
            if (Instance == null) Instance = tm;
            try { GameObject.DontDestroyOnLoad(go); } catch { /* swallow in case of editor/runtime differences */ }

            TBLog.Info("TeleportManager: created singleton instance at runtime (EnsureInstance).");
            return Instance;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManager: EnsureInstance failed to create instance: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Static safe entry point to request a scene teleport without requiring TeleportManager.Instance to already exist.
    /// Returns true if request accepted (transition started).
    /// </summary>
    public static bool RequestSceneTeleport(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost = 0)
    {
        var tm = EnsureInstance();
        if (tm == null)
        {
            TBLog.Warn("TeleportManager.RequestSceneTeleport: could not ensure TeleportManager instance.");
            return false;
        }

        return tm.StartSceneTeleport(sceneName, targetGameObjectName, coordsHint, haveCoordsHint, cost);
    }

    // Add these instance wrappers into your TeleportManager MonoBehaviour class.

    /// <summary>
    /// Instance wrapper that returns the IEnumerator from the safe-place integration so callers can StartCoroutine on the TeleportManager instance.
    /// This forwards to TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(this, ...).
    /// </summary>
    // Backwards-compatible wrapper: old signature (requestedTarget, onComplete)
    // Defaults preserveRequestedY to false (keeps previous behavior).
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 requestedTarget, Action<bool> onComplete)
    {
        return TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(this, requestedTarget, false, onComplete);
    }

    // New overload that lets callers pass preserveRequestedY.
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 requestedTarget, bool preserveRequestedY, Action<bool> onComplete)
    {
        return TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(this, requestedTarget, preserveRequestedY, onComplete);
    }

    // Backwards-compatible wrapper with historical name used elsewhere.
    public IEnumerator PlacePlayerUsingSafeRoutineWrapper(Vector3 requestedTarget, Action<bool> onComplete)
    {
        return TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(this, requestedTarget, false, onComplete);
    }

    // New historical-name overload with preserve flag.
    public IEnumerator PlacePlayerUsingSafeRoutineWrapper(Vector3 requestedTarget, bool preserveRequestedY, Action<bool> onComplete)
    {
        return TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(this, requestedTarget, preserveRequestedY, onComplete);
    }

    /// <summary>
    /// Request a teleport. Returns true if the manager accepted the request and started a transition.
    /// If false is returned, the request was rejected (another transition in progress).
    /// </summary>
    public bool StartSceneTeleport(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost = 0)
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

        Scene loadedScene = SceneManager.GetSceneByName(sceneName);
        bool teleportSuccess = false;
        try
        {
            StartCoroutine(TeleportInLoadedSceneCoroutine(loadedScene, targetGameObjectName, coordsHint, haveCoordsHint, cost, moved =>
            {
                try
                {
                    teleportSuccess = moved;

                    if (moved)
                    {
                        // Debug on success
                        TBLog.Info($"TeleportManager: teleport to '{sceneName}' succeeded. final moved={moved}");
                    }
                    else
                    {
                        // Warn on failure
                        TBLog.Warn($"TeleportManager: teleport to '{sceneName}' failed.");
                        TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not place you at the destination.");
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TeleportManager.StartTeleport: onComplete callback threw: " + ex.ToString());
                }
            }));

            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManager.StartTeleport: failed to start teleport coroutine: " + ex.ToString());
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed to start.");
            return false;
        }
    }

    public bool StartSceneLoad(string sceneName, Vector3 coordsHint, Action<Scene, AsyncOperation, bool> onComplete)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            TBLog.Warn("TeleportManager.StartSceneLoad: sceneName is null/empty — rejecting request.");
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Scene load cancelled: destination not configured.");
            return false;
        }

        if (_isSceneTransitionInProgress)
        {
            TBLog.Warn("TeleportManager.StartSceneLoad: another scene transition is in progress; rejecting request.");
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Scene load is already in progress. Please wait.");
            return false;
        }

        // Mark transition in progress and start the loader coroutine. We will clear the flag when
        // the onComplete callback fires (wrapped below) so that callers don't accidentally start
        // other transitions concurrently.
        _isSceneTransitionInProgress = true;

        Action<Scene, AsyncOperation, bool> wrapped = (scene, asyncOp, ok) =>
        {
            try
            {
                onComplete?.Invoke(scene, asyncOp, ok);
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManager.StartSceneLoad: onComplete callback threw: " + ex);
            }
            finally
            {
                // Clear transition flag regardless of onComplete outcome so manager is usable again.
                _isSceneTransitionInProgress = false;
            }
        };

        try
        {
            StartCoroutine(LoadSceneCoroutine(sceneName, coordsHint, wrapped));
            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManager.StartSceneLoad: failed to start LoadSceneCoroutine: " + ex);
            TravelButtonPlugin.ShowPlayerNotification?.Invoke("Scene load failed to start.");
            _isSceneTransitionInProgress = false;
            return false;
        }
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

    // NOTE: This file contains three coroutines:
    //  - LoadSceneAndTeleportCoroutine (orchestrator) — thin wrapper that runs the loader then the teleporter
    //  - LoadSceneCoroutine (loader) — handles only the SceneManager.LoadSceneAsync lifecycle and reporting
    //  - TeleportInLoadedSceneCoroutine (teleporter) — handles anchor/heuristic selection + safe placement in the loaded scene
    //
    // The code keeps the original logging and behavior but splits scene loading and placement responsibilities.

    private IEnumerator LoadSceneAndTeleportCoroutine(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost)
    {
        _isSceneTransitionInProgress = true;
        bool loadSuccess = false;
        Scene loadedScene = default;
        AsyncOperation loadOp = null;

        TBLog.Info($"TeleportManager: starting LoadSceneAndTeleportCoroutine for '{sceneName}' (cost={cost}) haveCoordsHint={haveCoordsHint} coordsHint={coordsHint}");

        if (CoordsConvertor.TryConvertToVector3(coordsHint, out Vector3 destCords))
        {
            TBLog.Info($"Converted coords -> {destCords}");            
            // use coords
        }
        else
        {
            TBLog.Warn("Could not convert coordsVal to Vector3");
            yield break;
        }

        // 1) Load scene (separate coroutine) -> results returned via callback
        yield return StartCoroutine(LoadSceneCoroutine(sceneName, destCords, (scene, asyncOp, ok) =>
        {
            loadSuccess = ok;
            loadedScene = scene;
            loadOp = asyncOp;
        }));

        if (!loadSuccess)
        {
            TBLog.Warn($"TeleportManager: LoadSceneAndTeleportCoroutine: scene '{sceneName}' failed to load. Aborting teleport.");
            _isSceneTransitionInProgress = false;
            yield break;
        }

        // 2) Teleport inside the loaded scene (separate coroutine)
        bool teleportSuccess = false;
        yield return StartCoroutine(TeleportInLoadedSceneCoroutine(loadedScene, targetGameObjectName, coordsHint, haveCoordsHint, cost, moved =>
        {
            teleportSuccess = moved;
        }));

        if (!teleportSuccess)
        {
            TBLog.Warn("TeleportManager: LoadSceneAndTeleportCoroutine: teleportation in loaded scene failed.");
            _isSceneTransitionInProgress = false;
            yield break;
        }

        // Done
        TBLog.Info("TeleportManager: LoadSceneAndTeleportCoroutine completed successfully.");
        _isSceneTransitionInProgress = false;
        yield break;
    }

    /// <summary>
    /// Loads the requested scene asynchronously and reports back the Scene and AsyncOperation via the onComplete callback.
    /// The callback parameters are: (Scene loadedScene, AsyncOperation asyncOp, bool success).
    /// This coroutine only manages loading lifecycle: starting the async op, waiting for progress/isDone, and basic diagnostics.
    /// </summary>
    // Replace the existing LoadSceneCoroutine with this corrected version
    private IEnumerator LoadSceneCoroutine(string sceneName, Vector3 coordsHint, Action<Scene, AsyncOperation, bool> onComplete)
    {
        if (onComplete == null) yield break;

        TBLog.Info($"TeleportManager: starting async load for scene '{sceneName}'.");

        AsyncOperation async = null;
        bool startOk = true;
        try
        {
            // Start the async load.
            async = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (async == null)
            {
                TBLog.Warn($"TeleportManager: SceneManager.LoadSceneAsync returned null for '{sceneName}'.");
                startOk = false;
            }
            else
            {
                // Ensure activation is allowed. If some other code set allowSceneActivation = false,
                // the load will stall at progress ~0.9. Explicitly set it to true and log it.
                try
                {
                    async.allowSceneActivation = true;
                    TBLog.Info($"TeleportManager: initiated LoadSceneAsync for '{sceneName}'. allowSceneActivation={async.allowSceneActivation}, initialProgress={async.progress:F2}");
                }
                catch (Exception exSet)
                {
                    TBLog.Warn($"TeleportManager: warning when setting allowSceneActivation on load op for '{sceneName}': {exSet}");
                    TBLog.Info($"TeleportManager: initiated LoadSceneAsync for '{sceneName}'. initialProgress={async.progress:F2}");
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManager: exception while starting LoadSceneAsync: " + ex.ToString());
            startOk = false;
        }

        if (!startOk)
        {
            onComplete(default, async, false);
            yield break;
        }

        // Wait for ready-to-activate / activation with bounded timeouts
        float lastLoggedProgress = -1f;
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

        float activationTimeout = 12.0f;
        float activateStart = Time.realtimeSinceStartup;
        while (!async.isDone)
        {
            // If activation is taking too long, force allowSceneActivation and continue
            if (Time.realtimeSinceStartup - activateStart > activationTimeout)
            {
                TBLog.Warn($"TeleportManager: scene activation for '{sceneName}' did not complete within {activationTimeout}s. Forcing allowSceneActivation=true and continuing to wait.");
                try
                {
                    async.allowSceneActivation = true;
                }
                catch (Exception ex) { TBLog.Warn("TeleportManager: failed to set allowSceneActivation=true: " + ex); }
                // Extend the timeout window so we don't spam this block repeatedly
                activateStart = Time.realtimeSinceStartup;
            }

            yield return null;
        }

        // At this point the SceneManager should have the scene loaded/active.
        Scene loadedScene = SceneManager.GetSceneByName(sceneName);
        bool sceneLoaded = loadedScene.isLoaded;
        bool sceneActive = SceneManager.GetActiveScene().name == sceneName;
        TBLog.Info($"TeleportManager: LoadSceneCoroutine: requested='{sceneName}', loaded.name='{loadedScene.name}', isLoaded={sceneLoaded}, isActive={sceneActive}");

        onComplete(loadedScene, async, true);

//        bool relocated = TravelButtonUI.AttemptTeleportToPositionSafe(coordsHint);
        yield break;
    }

    /// <summary>
    /// Handles anchor search, heuristic fallback, grounding probe, and safe placement inside the already-loaded scene.
    /// Accepts the loaded Scene struct (returned from LoadSceneCoroutine) and performs the teleport steps there.
    /// Calls onComplete(true) when successful, onComplete(false) when failed.
    /// </summary>
    private IEnumerator TeleportInLoadedSceneCoroutine(Scene loadedScene, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int cost, Action<bool> onComplete)
    {
        bool teleported = false;

        TBLog.Info($"TeleportManager: TeleportInLoadedSceneCoroutine starting for scene='{loadedScene.name}' targetGameObjectName='{targetGameObjectName}' haveCoordsHint={haveCoordsHint} coordsHint={coordsHint}");

        // Log root GameObjects for diagnostics (no yields inside try)
        try
        {
            var roots = loadedScene.GetRootGameObjects();
            TBLog.Info($"TeleportManager: scene '{loadedScene.name}' root object count = {roots?.Length ?? 0}");
            if (roots != null && roots.Length > 0)
            {
                var sb = new StringBuilder();
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
                try
                {
                    // GameObject.Find is non-yielding and safe to call inside try
                    anchor = GameObject.Find(targetGameObjectName);
                }
                catch (Exception exFind)
                {
                    anchor = null;
                    TBLog.Warn($"TeleportManager: GameObject.Find threw for '{targetGameObjectName}': {exFind.Message}");
                }

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
                GameObject go = null;
                try { go = GameObject.Find(s); } catch (Exception exFind2) { TBLog.Warn($"TeleportManager: exception finding spawn anchor '{s}': {exFind2}"); }
                if (go != null)
                {
                    finalPos = go.transform.position;
                    haveFinalPos = true;
                    TBLog.Info($"TeleportManager: heuristic anchor '{s}' found at {finalPos}; using as teleport target.");
                    break;
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
            onComplete?.Invoke(false);
            yield break;
        }

        TBLog.Info($"TeleportManager: preliminary finalPos = {finalPos} (haveCoordsHint={haveCoordsHint})");

        // Ground probe attempt
        try
        {
            bool grounded = false;
            Vector3 groundedPos = Vector3.zero;

            // Try TravelButtonUI.TryFindNearestNavMeshOrGround (safe usage)
            try
            {
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
                // PlacePlayer returns an IEnumerator that signals movement via callback
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

                var safeHost = TeleportHelpersBehaviour.GetOrCreateHost();

                if (safeHost != null)
                {
                    yield return safeHost.StartCoroutine(
                        TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(safeHost, finalPos, haveCoordsHint, moved =>
                        {
                            placementSucceeded = moved;
                        })
                    );
                }
                else
                {
                    yield return StartCoroutine(
                        TeleportManagerSafePlace.PlacePlayerUsingSafeRoutine_Internal(this, finalPos, haveCoordsHint, moved =>
                        {
                            placementSucceeded = moved;
                        })
                    );
                }

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
            onComplete?.Invoke(false);
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

        onComplete?.Invoke(true);
        yield break;
    }
}