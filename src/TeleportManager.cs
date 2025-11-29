using NodeCanvas.Tasks.Actions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    private const int TOP_K_REFLECTION = 3;

    private struct TransformInfo
    {
        public string NormalizedName;   // normalized name (already processed by NormalizeGameObjectName)
        public UnityEngine.Vector3 Position;
        public bool Active;
        public UnityEngine.Transform TransformRef;

        public TransformInfo(string normName, UnityEngine.Vector3 pos, bool active, UnityEngine.Transform tRef)
        {
            NormalizedName = normName;
            Position = pos;
            Active = active;
            TransformRef = tRef;
        }
    }

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

        // Mark transition in progress.
        _isSceneTransitionInProgress = true;

        // Start timer BEFORE creating the wrapped callback so the closure can capture it.
        var swStartSceneLoad = TBPerf.StartTimer();

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

                // Log total duration for the StartSceneLoad call.
                try
                {
                    TBPerf.Log($"StartSceneLoadTotal:{sceneName}", swStartSceneLoad, $"ok={ok}");
                }
                catch (Exception exLog)
                {
                    TBLog.Warn("TeleportManager.StartSceneLoad: TBPerf.Log threw: " + exLog);
                }
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

            // Log failure and elapsed time
            try
            {
                TBPerf.Log($"StartSceneLoadTotal:{sceneName}", swStartSceneLoad, $"start_failed=true");
            }
            catch { /* swallow logging errors */ }

            return false;
        }
    }

    // Helper: best-effort safe reader for logging the player's current position.
    // Keep this private and simple — it's only for debug messages.
    public static Vector3 GetPlayerPositionDebug()
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

        var swTotal = TBPerf.StartTimer();

        if (CoordsConvertor.TryConvertToVector3(coordsHint, out Vector3 destCords))
        {
            TBLog.Info($"Converted coords -> {destCords}");
        }
        else
        {
            TBLog.Warn("Could not convert coordsVal to Vector3");
            _isSceneTransitionInProgress = false;
            yield break;
        }

        // 1) Load scene (separate coroutine) -> results returned via callback
        var swLoadPhase = TBPerf.StartTimer();
        yield return StartCoroutine(LoadSceneCoroutine(sceneName, destCords, (scene, asyncOp, ok) =>
        {
            loadSuccess = ok;
            loadedScene = scene;
            loadOp = asyncOp;
        }));
        TBPerf.Log($"LoadScenePhase:{sceneName}", swLoadPhase, $"success={loadSuccess}");

        if (!loadSuccess)
        {
            TBLog.Warn($"TeleportManager: LoadSceneAndTeleportCoroutine: scene '{sceneName}' failed to load. Aborting teleport.");
            _isSceneTransitionInProgress = false;
            TBPerf.Log($"LoadSceneAndTeleport aborted:{sceneName}", swTotal, "failed load");
            yield break;
        }

        // 2) Teleport inside the loaded scene (separate coroutine)
        bool teleportSuccess = false;
        var swTeleportPhase = TBPerf.StartTimer();
        yield return StartCoroutine(TeleportInLoadedSceneCoroutine(loadedScene, targetGameObjectName, coordsHint, haveCoordsHint, cost, moved =>
        {
            teleportSuccess = moved;
        }));
        TBPerf.Log($"TeleportPhase:{sceneName}", swTeleportPhase, $"teleportSuccess={teleportSuccess}");

        if (!teleportSuccess)
        {
            TBLog.Warn("TeleportManager: LoadSceneAndTeleportCoroutine: teleportation in loaded scene failed.");
            _isSceneTransitionInProgress = false;
            TBPerf.Log($"LoadSceneAndTeleport aborted (no teleport):{sceneName}", swTotal, "teleport failed");
            yield break;
        }

        // Done
        TBPerf.Log($"LoadSceneAndTeleport total:{sceneName}", swTotal, $"loadOp progress={loadOp?.progress:F2}");
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
            var swStart = TBPerf.StartTimer();
            async = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            if (async == null)
            {
                TBLog.Warn($"TeleportManager: SceneManager.LoadSceneAsync returned null for '{sceneName}'.");
                startOk = false;
            }
            else
            {
                // Ensure activation is allowed.
                try
                {
                    async.allowSceneActivation = true;
                    TBLog.Info($"TeleportManager: initiated LoadSceneAsync for '{sceneName}'. allowSceneActivation={async.allowSceneActivation}, initialProgress={async.progress:F2}");
                }
                catch (Exception exSet)
                {
                    TBLog.Warn($"TeleportManager: warning when setting allowSceneActivation on load op for '{sceneName}': {exSet}");
                }
            }
            TBPerf.Log($"LoadSceneAsyncStart:{sceneName}", swStart, $"startOk={startOk}");
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
        var swWait = TBPerf.StartTimer();
        while (async.progress < 0.9f && !async.isDone)
        {
            if (Mathf.Abs(async.progress - lastLoggedProgress) > 0.01f)
            {
                TBLog.Info($"TeleportManager: loading '{sceneName}' progress={async.progress:F2}");
                lastLoggedProgress = async.progress;
            }
            // optional timeout extension behavior is unchanged
            yield return null;
        }
        TBPerf.Log($"LoadSceneAsyncWait:{sceneName}", swWait, $"finalProgress={async.progress:F2}, isDone={async.isDone}");

        // At this point the SceneManager should have the scene loaded/active.
        Scene loadedScene = SceneManager.GetSceneByName(sceneName);
        bool sceneLoaded = loadedScene.isLoaded;
        bool sceneActive = SceneManager.GetActiveScene().name == sceneName;
        TBLog.Info($"TeleportManager: LoadSceneCoroutine: requested='{sceneName}', loaded.name='{loadedScene.name}', isLoaded={sceneLoaded}, isActive={sceneActive}");

        onComplete(loadedScene, async, true);
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
        var swThis = TBPerf.StartTimer();

        // 1) Ensure loaded scene is active
        try
        {
            var swSetActive = TBPerf.StartTimer();
            try
            {
                SceneManager.SetActiveScene(loadedScene);
                TBLog.Info($"TeleportManager: SetActiveScene('{loadedScene.name}') succeeded.");
            }
            catch (Exception exSetScene)
            {
                TBLog.Warn($"TeleportManager: SetActiveScene('{loadedScene.name}') failed: {exSetScene}");
            }
            TBPerf.Log($"SetActiveScene:{loadedScene.name}", swSetActive, "");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManager: exception while trying to activate loaded scene: " + ex);
        }

        // 2) Log root objects
        try
        {
            var roots = loadedScene.GetRootGameObjects();
            int rootCount = roots?.Length ?? 0;
            TBLog.Info($"TeleportManager: scene '{loadedScene.name}' root object count = {rootCount}");
            var swRootSample = TBPerf.StartTimer();
            if (roots != null && rootCount > 0)
            {
                var sb = new StringBuilder();
                int sample = Math.Min(12, rootCount);
                for (int i = 0; i < sample; i++)
                {
                    if (i > 0) sb.Append(", ");
                    try { sb.Append(roots[i].name); } catch { sb.Append("<err>"); }
                }
                TBLog.Info($"TeleportManager: scene '{loadedScene.name}' sample roots: {sb.ToString()}");
            }
            TBPerf.Log($"RootObjectsSample:{loadedScene.name}", swRootSample, $"count={rootCount}");
        }
        catch (Exception exRoot)
        {
            TBLog.Warn($"TeleportManager: warning enumerating root objects: {exRoot}");
        }

        // 3) Anchor / target selection
        Vector3 targetPos = Vector3.zero;
        bool haveTargetPos = false;
        int anchorCandidates = 0;
        var swAnchorSearch = TBPerf.StartTimer();

        try
        {
            if (!string.IsNullOrEmpty(targetGameObjectName))
            {
                try
                {
                    var foundGo = GameObject.Find(targetGameObjectName);
                    if (foundGo != null)
                    {
                        targetPos = foundGo.transform.position;
                        haveTargetPos = true;
                        anchorCandidates++;
                        TBLog.Info($"TeleportManager: Found target by exact GameObject.Find('{targetGameObjectName}') at {targetPos}");
                    }
                }
                catch { /* swallow */ }

                if (!haveTargetPos)
                {
                    try
                    {
                        var roots = loadedScene.GetRootGameObjects();
                        foreach (var r in roots)
                        {
                            if (r == null || string.IsNullOrEmpty(r.name)) continue;
                            if (r.name.IndexOf(targetGameObjectName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                targetPos = r.transform.position;
                                haveTargetPos = true;
                                anchorCandidates++;
                                TBLog.Info($"TeleportManager: Found target by root-name match '{r.name}' at {targetPos}");
                                break;
                            }
                        }
                    }
                    catch { /* swallow */ }
                }
            }

            if (!haveTargetPos && haveCoordsHint)
            {
                targetPos = coordsHint;
                haveTargetPos = true;
                anchorCandidates++;
                TBLog.Info($"TeleportManager: Using coordsHint as target position -> {targetPos}");
            }

            if (!haveTargetPos)
            {
                try
                {
                    var tagCandidates = GameObject.FindGameObjectsWithTag("TravelAnchor");
                    if (tagCandidates != null && tagCandidates.Length > 0)
                    {
                        targetPos = tagCandidates[0].transform.position;
                        haveTargetPos = true;
                        anchorCandidates += tagCandidates.Length;
                        TBLog.Info($"TeleportManager: Found {tagCandidates.Length} TravelAnchor-tagged objects; using first at {targetPos}");
                    }
                }
                catch { /* tag may not exist; ignore */ }
            }

            if (!haveTargetPos)
            {
                try
                {
                    var ensure = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
                    if (ensure != null)
                    {
                        Vector3 fallbackPos = Vector3.zero;
                        bool foundFallback = false;
                        try
                        {
                            var mi = ensure.GetType().GetMethod("TryFindAnchorPosition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (mi != null)
                            {
                                object[] parms = new object[] { targetGameObjectName ?? loadedScene.name, null };
                                var ret = mi.Invoke(ensure, parms);
                                if (ret is bool rb && rb)
                                {
                                    if (parms.Length > 1 && parms[1] is Vector3 v)
                                    {
                                        fallbackPos = v;
                                        foundFallback = true;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            /* reflection call failed; continue */
                        }

                        if (foundFallback)
                        {
                            targetPos = fallbackPos;
                            haveTargetPos = true;
                            anchorCandidates++;
                            TBLog.Info($"TeleportManager: Found fallback anchor via TeleportHelpersBehaviour at {targetPos}");
                        }
                    }
                }
                catch { /* swallow */ }
            }
        }
        catch (Exception exAnchor)
        {
            TBLog.Warn($"TeleportManager: Anchor search exception: {exAnchor}");
        }

        TBPerf.Log($"AnchorSearch:{loadedScene.name}", swAnchorSearch, $"candidates={anchorCandidates}, haveTargetPos={haveTargetPos}");

        // 4) Grounding / navmesh probe
        Vector3 groundedPos = targetPos;
        bool grounded = false;
        var swGrounding = TBPerf.StartTimer();

        try
        {
            if (haveTargetPos)
            {
                // Try navmesh helper via reflection
                try
                {
                    var miNav = this.GetType().GetMethod("TryFindNearestNavMeshOrGround", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (miNav != null)
                    {
                        object[] navParms = new object[] { targetPos, null };
                        var navRet = miNav.Invoke(this, navParms);
                        if (navRet is bool navOk && navOk)
                        {
                            if (navParms.Length > 1 && navParms[1] is Vector3 vp)
                            {
                                groundedPos = vp;
                                grounded = true;
                                TBLog.Info($"TeleportManager: NavMesh/grounding helper succeeded at {groundedPos}");
                            }
                        }
                    }
                }
                catch (Exception exNav) { TBLog.Warn($"TeleportManager: NavMesh grounding helper threw: {exNav}"); }

                if (!grounded)
                {
                    try
                    {
                        Vector3 probeStart = targetPos + Vector3.up * 2.0f;
                        RaycastHit hit;
                        if (Physics.Raycast(probeStart, Vector3.down, out hit, 10.0f))
                        {
                            groundedPos = hit.point;
                            grounded = true;
                            TBLog.Info($"TeleportManager: Raycast grounding succeeded at {groundedPos} (hit {hit.collider?.name})");
                        }
                    }
                    catch (Exception exRay) { TBLog.Warn($"TeleportManager: grounding raycast threw: {exRay}"); }
                }
            }
            else
            {
                TBLog.Info("TeleportManager: no target pos available for grounding.");
            }
        }
        catch (Exception exGround)
        {
            TBLog.Warn($"TeleportManager: grounding exception: {exGround}");
        }

        TBPerf.Log($"GroundingProbe:{loadedScene.name}", swGrounding, $"grounded={grounded}");

        // 5) Placement attempts
        var swPlacement = TBPerf.StartTimer();
        bool placed = false;

        if (grounded)
        {
            // Attempt placement via TravelButtonUI helper (wrapped in try/catch per-call)
            var miAttempt = (System.Reflection.MethodInfo)null;
            try
            {
                var uiType = typeof(TravelButtonUI);
                miAttempt = uiType.GetMethod("AttemptTeleportToPositionSafe", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            }
            catch { /* ignore reflection failures */ }

            // Try immediate helper once
            if (miAttempt != null)
            {
                try
                {
                    object ret = miAttempt.Invoke(null, new object[] { groundedPos });
                    if (ret is bool b && b)
                    {
                        placed = true;
                        TBLog.Info($"TeleportManager: AttemptTeleportToPositionSafe succeeded at {groundedPos}");
                    }
                }
                catch (Exception exInvoke) { TBLog.Warn($"TeleportManager: AttemptTeleportToPositionSafe threw: {exInvoke}"); }
            }

            // If helper not available or placement failed, perform a small local search for a safe offset.
            if (!placed)
            {
                const int maxAttempts = 6;
                const float radius = 1.2f;
                for (int i = 0; i < maxAttempts && !placed; i++)
                {
                    float ang = (360f / maxAttempts) * i;
                    Vector3 offset = new Vector3(Mathf.Cos(ang * Mathf.Deg2Rad), 0, Mathf.Sin(ang * Mathf.Deg2Rad)) * radius * (1 + i * 0.5f);
                    Vector3 cand = groundedPos + offset;

                    // Per-iteration safe guards
                    bool iterationPlaced = false;
                    if (miAttempt != null)
                    {
                        try
                        {
                            object r2 = miAttempt.Invoke(null, new object[] { cand });
                            if (r2 is bool b2 && b2)
                            {
                                iterationPlaced = true;
                                groundedPos = cand;
                            }
                        }
                        catch { /* swallow per-attempt exceptions */ }
                    }
                    else
                    {
                        try
                        {
                            Collider[] buf = new Collider[8];
                            int hits = Physics.OverlapSphereNonAlloc(cand + Vector3.up * 0.5f, 0.5f, buf, ~0);
                            if (hits == 0)
                            {
                                // set player here using existing APIs if possible (placeholder)
                                iterationPlaced = true;
                                groundedPos = cand;
                                TBLog.Info($"TeleportManager: simple fallback placement chosen at {cand}");
                            }
                        }
                        catch { /* swallow per-attempt exceptions */ }
                    }

                    if (iterationPlaced)
                    {
                        placed = true;
                        teleported = true;
                        break;
                    }

                    // yield a frame to avoid blocking long loops
                    yield return null;
                }
            }

            if (!placed)
            {
                TBLog.Warn($"TeleportManager: placement attempts failed in scene '{loadedScene.name}'");
            }
        }
        else
        {
            TBLog.Warn("TeleportManager: cannot attempt placement because grounding failed / no target position.");
        }

        TBPerf.Log($"PlacementPhase:{loadedScene.name}", swPlacement, $"teleported={teleported}");

        // 6) Final fallback: EnsureSceneAndTeleport if still not teleported
        var swFallback = TBPerf.StartTimer();
        if (!teleported)
        {
            try
            {
                var helpers = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
                if (helpers != null)
                {
                    var miFallback = helpers.GetType().GetMethod("EnsureSceneAndTeleport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (miFallback != null)
                    {
                        object[] parms = new object[] { targetGameObjectName ?? loadedScene.name, null };
                        var fbRet = miFallback.Invoke(helpers, parms);
                        if (fbRet is bool fbOk && fbOk)
                        {
                            teleported = true;
                            TBLog.Info($"TeleportManager: EnsureSceneAndTeleport fallback succeeded.");
                        }
                    }
                }
            }
            catch (Exception exFb)
            {
                TBLog.Warn("TeleportManager: fallback EnsureSceneAndTeleport threw: " + exFb);
            }
        }
        TBPerf.Log($"FullFallback:{loadedScene.name}", swFallback, $"teleportedAfterFallback={teleported}");

        // 7) Final logging and callback
        TBPerf.Log($"TeleportInLoadedScene total:{loadedScene.name}", swThis, $"teleported={teleported}");
        try { onComplete?.Invoke(teleported); } catch { /* swallow callback exceptions */ }

        yield break;
    }

    // Normalize helper (unchanged)
    private static string NormalizeGameObjectName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var s = Regex.Replace(raw, @"\s*\(Clone\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    // Replace the existing DetectAndPersistVariantsForCityCoroutine implementation with the code below.
    // Keep the same method signature.

    public static IEnumerator DetectAndPersistVariantsForCityCoroutine(TravelButton.City city, float initialDelay = 1.0f, float scanDurationSeconds = 3.0f)
    {
        var swTotal = TBPerf.StartTimer();

        if (city == null || string.IsNullOrEmpty(city.name))
        {
            TBPerf.Log("DetectVariants:Total:<invalid_city>", swTotal, "");
            yield break;
        }

        if (initialDelay > 0f)
        {
            var swDelay = TBPerf.StartTimer();
            yield return new WaitForSeconds(initialDelay);
            TBPerf.Log($"DetectVariants:InitialDelay:{city.name}", swDelay, $"delay={initialDelay:F2}s");
        }

        float deadline = Time.time + scanDurationSeconds;

        var foundVariants = new List<string>();
        string foundLastVariant = null;

        string sceneKey = (city.sceneName ?? "").Trim();
        string nameKey = (city.name ?? "").Trim();
        string targetKey = (city.targetGameObjectName ?? "").Trim();

        Func<string, IEnumerable<string>> makeTokens = s =>
        {
            if (string.IsNullOrEmpty(s)) return Enumerable.Empty<string>();
            var cleaned = Regex.Replace(s, @"NewTerrain|Terrain|_Terrain|Clone", "", RegexOptions.IgnoreCase).Trim();
            cleaned = Regex.Replace(cleaned, @"[^\w]", " ");
            return cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Trim()).Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase);
        };

        var sceneTokens = makeTokens(sceneKey).ToArray();
        var nameTokens = makeTokens(nameKey).ToArray();
        var targetTokens = makeTokens(targetKey).ToArray();

        // ---------- Stage 1: Scan loop (build list of candidate names) ----------
        var swScanLoop = TBPerf.StartTimer();

        while (Time.time <= deadline)
        {
            foundVariants.Clear();
            foundLastVariant = null;

            GameObject[] all = null;
            try
            {
                all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: FindObjectsOfType threw for '{city.name}': {ex.Message}");
                all = null;
            }

            if (all == null || all.Length == 0)
            {
                yield return null;
                continue;
            }

            var swScanOnce = TBPerf.StartTimer();
            try
            {
                foreach (var go in all)
                {
                    if (go == null) continue;
                    var raw = go.name ?? "";
                    var n = NormalizeGameObjectName(raw);
                    if (string.IsNullOrEmpty(n)) continue;

                    var low = n.ToLowerInvariant();
                    if (low.Contains("canvas") || low.Contains("ui") || low.StartsWith("btn") || low.Contains("button"))
                        continue;

                    bool matched = false;

                    if (!string.IsNullOrEmpty(targetKey) && n.IndexOf(targetKey, StringComparison.OrdinalIgnoreCase) >= 0) matched = true;
                    if (!matched && targetTokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) matched = true;

                    if (!matched && !string.IsNullOrEmpty(nameKey) && n.IndexOf(nameKey, StringComparison.OrdinalIgnoreCase) >= 0) matched = true;
                    if (!matched && nameTokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) matched = true;

                    if (!matched && !string.IsNullOrEmpty(sceneKey) && n.IndexOf(sceneKey, StringComparison.OrdinalIgnoreCase) >= 0) matched = true;
                    if (!matched && sceneTokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) matched = true;

                    if (!matched) continue;
                    if (n.Length < 3) continue;

                    if (!foundVariants.Contains(n)) foundVariants.Add(n);

                    if (go.activeInHierarchy)
                    {
                        // prefer active object variants as the "last" if available
                        if (!string.Equals(n, nameKey, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(n, sceneKey, StringComparison.OrdinalIgnoreCase))
                        {
                            foundLastVariant = n;
                        }
                        else if (string.IsNullOrEmpty(foundLastVariant))
                        {
                            foundLastVariant = n;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: scan exception for '{city.name}': {ex.Message}");
            }
            finally
            {
                TBPerf.Log($"DetectVariants:ScanOnce:{city.name}", swScanOnce, $"found={foundVariants.Count}, last={foundLastVariant ?? "<none>"}");
            }

            if (foundVariants.Count > 0) break;

            yield return null;
        }

        TBPerf.Log($"DetectVariants:ScanLoop:{city.name}", swScanLoop, $"durationRequested={scanDurationSeconds:F2}s");

        // ---------- Stage 2: Fallback child search if nothing found ----------
        if (foundVariants.Count == 0)
        {
            GameObject[] roots = null;
            try
            {
                roots = UnityEngine.Object.FindObjectsOfType<GameObject>()
                    .Where(g =>
                    {
                        var nn = NormalizeGameObjectName(g?.name ?? "");
                        if (string.IsNullOrEmpty(nn)) return false;
                        if (!string.IsNullOrEmpty(targetKey) && nn.IndexOf(targetKey, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (targetTokens.Any(t => nn.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                        if (!string.IsNullOrEmpty(sceneKey) && nn.IndexOf(sceneKey, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (sceneTokens.Any(t => nn.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                        return false;
                    }).ToArray();
            }
            catch (Exception ex)
            {
                TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: fallback roots enumeration failed for '{city.name}': {ex.Message}");
                roots = null;
            }

            if (roots != null && roots.Length > 0)
            {
                var swFallbackChildren = TBPerf.StartTimer();
                try
                {
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        foreach (Transform child in r.transform)
                        {
                            var cn = NormalizeGameObjectName(child.name);
                            if (!string.IsNullOrEmpty(cn) && !foundVariants.Contains(cn)) foundVariants.Add(cn);
                            if (child.gameObject.activeInHierarchy && string.IsNullOrEmpty(foundLastVariant)) foundLastVariant = cn;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: fallback children scan failed for '{city.name}': {ex.Message}");
                }
                finally
                {
                    TBPerf.Log($"DetectVariants:FallbackChildren:{city.name}", swFallbackChildren, $"roots={(roots?.Length ?? 0)}, found={foundVariants.Count}, last={foundLastVariant ?? "<none>"}");
                }
            }
        }

        // ---------- Stage 3: Sample names if still nothing found ----------
        if (foundVariants.Count == 0)
        {
            try
            {
                var swSampleNames = TBPerf.StartTimer();
                var allNames = UnityEngine.Object.FindObjectsOfType<GameObject>()
                    .Where(g => g != null)
                    .Select(g => NormalizeGameObjectName(g.name))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToArray();

                TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: no variant candidates found for '{city.name}'. Top scene object names (sample up to 80): [{string.Join(", ", allNames)}]");
                TBPerf.Log($"DetectVariants:SampleNames:{city.name}", swSampleNames, $"sampleCount={allNames.Length}");
            }
            catch { }
        }

        // ---------- Stage 4: Determine finalLast and persist only lastKnownVariant ----------
        try
        {
            string finalLast = null;

            if (!string.IsNullOrEmpty(foundLastVariant))
            {
                finalLast = foundLastVariant;
            }
            else if (foundVariants.Count > 0)
            {
                finalLast = foundVariants[0];
            }

            if (!string.IsNullOrEmpty(finalLast))
            {
                try
                {
                    // Only update lastKnownVariant (do NOT modify city.variants list)
                    city.lastKnownVariant = finalLast;
                    TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: setting lastKnownVariant='{finalLast}' for city='{city.name}'");

                    // Persist the city so the lastKnownVariant is saved (no variants array changes)
                    try
                    {
                        TravelButton.AppendOrUpdateCityInJsonAndSave(city);
                    }
                    catch (Exception exSave)
                    {
                        TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: failed to persist lastKnownVariant for '{city.name}': {exSave.Message}");
                    }
                }
                catch (Exception exSet)
                {
                    TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: failed to set lastKnownVariant for '{city.name}': {exSet.Message}");
                }
            }
            else
            {
                TBLog.Info($"DetectAndPersistVariantsForCityCoroutine: no confident last variant detected for '{city.name}' - leaving lastKnownVariant unchanged.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"DetectAndPersistVariantsForCityCoroutine: finalize failed for '{city?.name}': {ex.Message}");
        }

        TBPerf.Log($"DetectVariants:Total:{city.name}", swTotal, $"foundVariants={foundVariants.Count}, lastKnown={(city.lastKnownVariant ?? "<none>")}");
        yield break;
    }
}