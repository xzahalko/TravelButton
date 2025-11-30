using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static CharacterVitalDisplay; // kept per your request (may be unused but included for parity)

/// <summary>
/// Helper utilities + safe-place coroutine used by TeleportManager.
/// </summary>
public static class TeleportManagerSafePlace
{
    // TEMPORARY: disable the one-shot skip so first attempt runs as normal.
    public static bool TemporarilySkipNextPlacement = false;

    private static string GetTransformPath(Transform t)
    {
        if (t == null) return "<null>";
        var sb = new StringBuilder();
        var cur = t;
        while (cur != null)
        {
            if (sb.Length == 0) sb.Insert(0, cur.name);
            else sb.Insert(0, cur.name + "/");
            cur = cur.parent;
        }
        return sb.ToString();
    }

    // Option A helper: try to raise the chosen finalPos in small steps (matching fallback behavior)
    // to avoid overlaps. Returns adjusted position and outputs how much Y was raised via raisedBy.
    private static Vector3 AdjustForOverlapLikeFallback(Vector3 basePos, Transform playerRoot, out float raisedBy)
    {
        const float CHECK_RADIUS = 0.5f;     // radius to check overlaps
        const float START_OFFSET = 0.5f;     // center offset above basePos when checking
        const float STEP_RAISE = 0.25f;      // step to raise each attempt
        const float MAX_RAISE = 2.0f;        // maximum raise to attempt
        const float LARGE_RAISE = 1.5f;      // fallback large raise if small steps fail

        raisedBy = 0f;

        Vector3 checkCenter = basePos + Vector3.up * START_OFFSET;
        Collider[] hits0 = Physics.OverlapSphere(checkCenter, CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
        bool overlapping0 = false;
        if (hits0 != null && hits0.Length > 0)
        {
            var names0 = new StringBuilder();
            foreach (var h in hits0)
            {
                if (h == null || h.transform == null) continue;
                names0.Append(h.name).Append(",");
                if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                overlapping0 = true;
            }
            TBLog.Info($"TeleportManagerSafePlace: AdjustForOverlap initial check hits={hits0.Length} names=[{names0}] overlapping0={overlapping0} checkCenter={checkCenter}");
        }
        else
        {
            TBLog.Info($"TeleportManagerSafePlace: AdjustForOverlap initial check - no hits at checkCenter={checkCenter}");
        }

        if (!overlapping0)
        {
            // nothing to adjust
            TBLog.Info("TeleportManagerSafePlace: AdjustForOverlapLikeFallback - no overlap detected, returning original basePos");
            return basePos;
        }

        int maxSteps = Mathf.CeilToInt(MAX_RAISE / STEP_RAISE);
        for (int i = 1; i <= maxSteps; i++)
        {
            Vector3 candidate = basePos + Vector3.up * (i * STEP_RAISE);
            Vector3 cCenter = candidate + Vector3.up * START_OFFSET;
            Collider[] hits = Physics.OverlapSphere(cCenter, CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
            bool overlapping = false;
            var hitNames = new StringBuilder();
            if (hits != null && hits.Length > 0)
            {
                for (int hi = 0; hi < hits.Length; hi++)
                {
                    var h = hits[hi];
                    if (h == null || h.transform == null) continue;
                    hitNames.Append(h.name).Append(",");
                    if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                    overlapping = true;
                }
            }

            TBLog.Info($"TeleportManagerSafePlace: AdjustForOverlap step {i}/{maxSteps} candidateY={candidate.y} hits={(hits?.Length ?? 0)} hitNames=[{hitNames}] overlapping={overlapping} cCenter={cCenter}");
            if (!overlapping)
            {
                raisedBy = candidate.y - basePos.y;
                TBLog.Info($"TeleportManagerSafePlace: AdjustForOverlapLikeFallback selected candidate Y {candidate.y} (raisedBy={raisedBy:F2})");
                return candidate;
            }
        }

        // nothing found - use large raise fallback similar to old fallback behaviour
        Vector3 fallback = basePos + Vector3.up * LARGE_RAISE;
        raisedBy = LARGE_RAISE;
        TBLog.Warn($"TeleportManagerSafePlace: AdjustForOverlapLikeFallback could not find free spot with small steps; using large raise {LARGE_RAISE} -> {fallback}");
        return fallback;
    }

    /// <summary>
    /// Robust placement coroutine with extra debug logging.
    /// - host: MonoBehaviour used to StartCoroutine if needed (for logging/name).
    /// - requestedTarget: world-space target to place player near/at.
    /// - preserveRequestedY: if true, try to keep requestedTarget.y unless it overlaps geometry (then fall back to grounded position).
    /// - onComplete: callback invoked with true on success, false on failure/timeout.
    /// </summary>
    public static IEnumerator PlacePlayerUsingSafeRoutine_Internal(MonoBehaviour host, Vector3 requestedTarget, bool preserveRequestedY, Action<bool> onComplete)
    {
        float entryTime = Time.realtimeSinceStartup;
        TBLog.Info($"PlacePlayerUsingSafeRoutine_Internal: Enter (t={entryTime:F3}) requestedTarget={requestedTarget}, preserveRequestedY={preserveRequestedY}, host={(host != null ? host.name : "<null>")}");

        // ---- ENABLED: small pause before the first enforcement attempt ----
        // This gives the scene and any TeleportHelpers/Reenable routines a short moment
        // to complete any delayed changes (e.g. reparenting, rigidbody resets) before we
        // set the player's transform for the first time.

        const float preEnforcementDelay = 2.0f; // seconds (tuneable - reduced from 10s for better UX)
        TBLog.Info($"TeleportManagerSafePlace: waiting {preEnforcementDelay:F3}s before first enforcement attempt to let scene/physics stabilize");
        yield return new WaitForSecondsRealtime(preEnforcementDelay);

        if (host == null)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine_Internal: host is null; cannot run coroutine.");
            onComplete?.Invoke(false);
            yield break;
        }

        // TEMPORARY one-shot skip (disabled by default in this version).
        if (TemporarilySkipNextPlacement)
        {
            TemporarilySkipNextPlacement = false;
            TBLog.Info("PlacePlayerUsingSafeRoutine_Internal: Temporarily skipping repositioning for this invocation (TemporarilySkipNextPlacement was true). Returning failure so caller can fallback.");
            onComplete?.Invoke(false);
            yield break;
        }

        // Helper to resolve player root/CC/rb robustly
        Transform playerRoot = null;
        Transform ResolveCC(out CharacterController outCc, out Rigidbody outRb)
        {
            outCc = null;
            outRb = null;
            try
            {
                var ccFound = UnityEngine.Object.FindObjectOfType<CharacterController>();
                if (ccFound != null)
                {
                    outCc = ccFound;
                    var root = ccFound.transform;
                    var rbs = root.GetComponentsInChildren<Rigidbody>(true);
                    if (rbs != null && rbs.Length > 0) outRb = rbs[0];
                    TBLog.Info($"PlacePlayerUsingSafeRoutine_Internal.ResolveCC: found CC on '{GetTransformPath(root)}' pos={root.position}");
                    return root;
                }

                var go = GameObject.FindWithTag("Player");
                if (go != null)
                {
                    var cc = go.GetComponentInChildren<CharacterController>(true);
                    if (cc != null) outCc = cc;
                    var rbs2 = go.GetComponentsInChildren<Rigidbody>(true);
                    if (rbs2 != null && rbs2.Length > 0) outRb = rbs2[0];
                    TBLog.Info($"PlacePlayerUsingSafeRoutine_Internal.ResolveCC: found GameObject.tag='Player' -> '{GetTransformPath(go.transform)}' pos={go.transform.position}");
                    return go.transform;
                }

                var all = GameObject.FindObjectsOfType<GameObject>();
                foreach (var g in all)
                {
                    if (g == null || string.IsNullOrEmpty(g.name)) continue;
                    if (!g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)) continue;
                    if (g.name.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var cc2 = g.GetComponentInChildren<CharacterController>(true);
                    if (cc2 != null) outCc = cc2;
                    var rbs3 = g.GetComponentsInChildren<Rigidbody>(true);
                    if (rbs3 != null && rbs3.Length > 0) outRb = rbs3[0];
                    TBLog.Info($"PlacePlayerUsingSafeRoutine_Internal.ResolveCC: heuristic found '{GetTransformPath(g.transform)}' pos={g.transform.position}");
                    return g.transform;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlacePlayerUsingSafeRoutine_Internal: ResolveCC threw: " + ex.ToString());
            }

            TBLog.Info("PlacePlayerUsingSafeRoutine_Internal.ResolveCC: nothing found this iteration");
            return null;
        }

        // 1) Wait for player root/CC to exist (bounded timeout)
        const float WAIT_TIMEOUT = 8.0f;
        float startWait = Time.realtimeSinceStartup;
        int scanIteration = 0;
        CharacterController cc = null;
        Rigidbody rb = null;

        TBLog.Info($"TeleportManagerSafePlace: waiting up to {WAIT_TIMEOUT:F1}s for player root (start t={startWait:F3})");

        while (Time.realtimeSinceStartup - startWait < WAIT_TIMEOUT)
        {
            scanIteration++;
            playerRoot = ResolveCC(out cc, out rb);
            if (playerRoot != null)
            {
                string ccPath = cc != null ? GetTransformPath(cc.transform) : "<none>";
                string rbPath = rb != null ? GetTransformPath(rb.transform) : "<none>";
                TBLog.Info($"TeleportManagerSafePlace: resolved playerRoot='{GetTransformPath(playerRoot)}' pos={playerRoot.position} (iteration={scanIteration}) CC={ccPath} RB={rbPath}");

                // Extra debug: show CC properties (if available)
                try
                {
                    if (cc != null)
                    {
                        TBLog.Info($"TeleportManagerSafePlace: CC properties center={cc.center} radius={cc.radius} height={cc.height} skinWidth={cc.skinWidth} enabled={cc.enabled}");
                    }
                    if (rb != null)
                    {
                        TBLog.Info($"TeleportManagerSafePlace: RB properties mass={rb.mass} useGravity={rb.useGravity} isKinematic={rb.isKinematic} velocity={rb.velocity} angularVelocity={rb.angularVelocity}");
                    }
                    TBLog.Info($"TeleportManagerSafePlace: playerRoot.parent={(playerRoot.parent != null ? GetTransformPath(playerRoot.parent) : "<null>")} activeInHierarchy={playerRoot.gameObject.activeInHierarchy} layer={playerRoot.gameObject.layer} childCount={playerRoot.childCount}");
                }
                catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: logging CC/RB properties threw: " + ex.ToString()); }

                break;
            }
            yield return null;
        }

        if (playerRoot == null)
        {
            TBLog.Warn($"TeleportManagerSafePlace: timed out waiting for player root in scene after {WAIT_TIMEOUT}s.");
            onComplete?.Invoke(false);
            yield break;
        }

        // Log scene root count and a small sample (helps detect if scene still populating)
        try
        {
            var roots = playerRoot.gameObject.scene.GetRootGameObjects();
            TBLog.Info($"TeleportManagerSafePlace: scene '{playerRoot.gameObject.scene.name}' root count={roots.Length} (sample root[0]={(roots.Length > 0 ? roots[0].name : "<none>")})");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: scene root sample threw: " + ex.ToString());
        }

        // 2) Compute grounded candidate position (groundedPos) via raycast -> NavMesh -> overlap/raise fallback
        Vector3 groundedPos = requestedTarget;
        bool groundedByRaycast = false;

        float tGroundStart = Time.realtimeSinceStartup;
        TBLog.Info($"TeleportManagerSafePlace: starting grounding computation (t={tGroundStart:F3}) requestedTarget={requestedTarget}");

        try
        {
            const float RAY_UP = 4.0f;
            const float RAY_DOWN = 20.0f;
            Vector3 rayStart = requestedTarget + Vector3.up * RAY_UP;
            RaycastHit hit;
            float rayLen = RAY_UP + RAY_DOWN;
            TBLog.Info($"TeleportManagerSafePlace: performing Raycast from {rayStart} down {rayLen}");
            if (Physics.Raycast(rayStart, Vector3.down, out hit, rayLen, ~0, QueryTriggerInteraction.Ignore))
            {
                // FIXED: Increase ground clearance from 0.1 to 1.0 to account for CharacterController height
                // CharacterController typically has height ~2.0 and center at ~1.0, so we need proper clearance
                groundedPos = new Vector3(requestedTarget.x, hit.point.y + 1.0f, requestedTarget.z);
                groundedByRaycast = true;
                string colliderName = hit.collider != null ? hit.collider.name : "<none>";
                string colliderTag = hit.collider != null ? hit.collider.tag : "<none>";
                int layer = hit.collider != null ? hit.collider.gameObject.layer : -1;
                TBLog.Info($"TeleportManagerSafePlace: raycast hit at point={hit.point}, normal={hit.normal}, distance={hit.distance:F3}, collider='{colliderName}', colliderTag={colliderTag}, layer={layer} => groundedPos={groundedPos}");
            }
            else
            {
                TBLog.Info("TeleportManagerSafePlace: raycast found no ground under requestedTarget.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: raycast threw: " + ex.ToString());
        }

        float tAfterRay = Time.realtimeSinceStartup;
        TBLog.Info($"TeleportManagerSafePlace: raycast stage finished (t={tAfterRay:F3}, dt={(tAfterRay - tGroundStart):F3}s, groundedByRaycast={groundedByRaycast})");

        if (!groundedByRaycast)
        {
            try
            {
                const float NAV_RADIUS = 6.0f;
                NavMeshHit navHit;
                TBLog.Info($"TeleportManagerSafePlace: attempting NavMesh.SamplePosition around {requestedTarget} radius={NAV_RADIUS}");
                if (NavMesh.SamplePosition(requestedTarget, out navHit, NAV_RADIUS, NavMesh.AllAreas))
                {
                    groundedPos = navHit.position;
                    TBLog.Info($"TeleportManagerSafePlace: NavMesh.SamplePosition success pos={navHit.position}, distance={navHit.distance:F3}");
                }
                else
                {
                    TBLog.Info("TeleportManagerSafePlace: NavMesh.SamplePosition failed to find a nearby nav position.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManagerSafePlace: NavMesh.SamplePosition threw: " + ex.ToString());
            }
        }

        float tAfterNav = Time.realtimeSinceStartup;
        TBLog.Info($"TeleportManagerSafePlace: nav stage finished (t={tAfterNav:F3}, dt={(tAfterNav - tAfterRay):F3}s), groundedPos={groundedPos}");

        if (!groundedByRaycast)
        {
            try
            {
                TBLog.Info("TeleportManagerSafePlace: attempting overlap/raise fallback to find non-overlapping placement.");
                const float STEP = 0.25f;
                const float MAX_RAISE = 2.0f;
                const float RADIUS = 0.5f;
                bool fixedUp = false;
                int maxSteps = Mathf.CeilToInt(MAX_RAISE / STEP);
                for (int i = 0; i <= maxSteps; i++)
                {
                    Vector3 checkPos = requestedTarget + Vector3.up * (i * STEP);
                    Collider[] hits = Physics.OverlapSphere(checkPos + Vector3.up * 0.5f, RADIUS, ~0, QueryTriggerInteraction.Ignore);
                    bool overlapping = false;
                    if (hits != null && hits.Length > 0)
                    {
                        var hitNames = new StringBuilder();
                        for (int hi = 0; hi < hits.Length; hi++)
                        {
                            var h = hits[hi];
                            if (h == null || h.transform == null) continue;
                            hitNames.Append(h.name).Append(",");
                            if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                            overlapping = true;
                        }
                        TBLog.Info($"TeleportManagerSafePlace: overlap step {i} - totalHits={hits.Length} hitNames=[{hitNames}] overlapping={overlapping} checkPos={checkPos}");
                    }
                    else
                    {
                        TBLog.Info($"TeleportManagerSafePlace: overlap step {i} - no hits at checkPos={checkPos}");
                    }

                    if (!overlapping)
                    {
                        groundedPos = checkPos;
                        fixedUp = true;
                        TBLog.Info($"TeleportManagerSafePlace: overlap fallback selected groundedPos={groundedPos} at step {i}");
                        break;
                    }
                }

                if (!fixedUp)
                {
                    TBLog.Warn("TeleportManagerSafePlace: overlap fallback could not find free spot; using requestedTarget for groundedPos");
                    groundedPos = requestedTarget;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManagerSafePlace: overlap fallback threw: " + ex.ToString());
                groundedPos = requestedTarget;
            }
        }

        // 3) Decide finalPos, respecting preserveRequestedY when requested.
        TBLog.Info($"TeleportManagerSafePlace: Enforcement preview: AdjustForOverlapLikeFallback ad3, currentPlayerPos={(playerRoot != null ? playerRoot.position.ToString() : "<none>")}");

        Vector3 finalPos;
        bool requestedOverlaps = false;
        if (preserveRequestedY)
        {
            finalPos = requestedTarget;
            // quick overlap-check at requestedTarget
            const float CHECK_RADIUS = 0.5f;
            Vector3 overlapCheckPos = finalPos + Vector3.up * 0.5f;
            Collider[] overlapHits = Physics.OverlapSphere(overlapCheckPos, CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
            if (overlapHits != null && overlapHits.Length > 0)
            {
                TBLog.Info($"TeleportManagerSafePlace: Enforcement preview: AdjustForOverlapLikeFallback if, currentPlayerPos={(playerRoot != null ? playerRoot.position.ToString() : "<none>")}");
                var names = new StringBuilder();
                foreach (var h in overlapHits)
                {
                    if (h == null || h.transform == null) continue;
                    names.Append(h.name).Append(",");
                    if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                    requestedOverlaps = true;
                    break;
                }
                TBLog.Info($"TeleportManagerSafePlace: preserveRequestedY overlap hits [{names}]");
            }
            TBLog.Info($"TeleportManagerSafePlace: preserveRequestedY={preserveRequestedY}, requestedOverlaps={requestedOverlaps}, requestedTarget={requestedTarget}, groundedPos={groundedPos}");
            if (requestedOverlaps)
            {
                TBLog.Warn("TeleportManagerSafePlace: requestedTarget overlaps geometry -> falling back to groundedPos");
                finalPos = groundedPos;
            }
        }
        else
        {
            finalPos = groundedPos;
        }

        TBLog.Info($"TeleportManagerSafePlace: chosen finalPos={finalPos} (preserveRequestedY={preserveRequestedY})");

        // NEW: apply the fallback-style overlap-adjustment BEFORE enforcement, to match the "second saviour" behaviour.
        try
        {
            float raised;
            TBLog.Info($"TeleportManagerSafePlace: Enforcement preview: AdjustForOverlapLikeFallback before, currentPlayerPos={(playerRoot != null ? playerRoot.position.ToString() : "<none>")}");
            Vector3 adjusted = AdjustForOverlapLikeFallback(finalPos, playerRoot, out raised);
            TBLog.Info($"TeleportManagerSafePlace: Enforcement preview: AdjustForOverlapLikeFallback sfter, currentPlayerPos={(playerRoot != null ? playerRoot.position.ToString() : "<none>")}");
            if (Mathf.Abs(raised) > 0.001f)
            {
                TBLog.Info($"TeleportManagerSafePlace: finalPos adjusted by fallback-like overlap fix: original={finalPos}, adjusted={adjusted}, raisedBy={raised:F2}");
            }
            finalPos = adjusted;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: AdjustForOverlapLikeFallback threw: " + ex.ToString());
        }


        // ----------------------------------------------------------------

        // 4) Enforcement + post-monitoring loop
        const int maxEnforceAttempts = 4;
        const float enforceThreshold = 0.6f; // meters allowed difference
        const float postMonitorDuration = 0.8f; // seconds to watch for external overrides after success

        int enforceAttempt = 0;
        bool overallSuccess = false;

        // Extra debug: snapshot before any enforcement
        try
        {
            TBLog.Info($"TeleportManagerSafePlace: Enforcement preview: finalPos={finalPos}, playerRoot={(playerRoot != null ? GetTransformPath(playerRoot) : "<none>")}, currentPlayerPos={(playerRoot != null ? playerRoot.position.ToString() : "<none>")}");
            if (cc != null) TBLog.Info($"TeleportManagerSafePlace: Enforcement preview CC.enabled={cc.enabled}, CC.isGrounded={cc.isGrounded}, CC.center={cc.center}");
            if (rb != null) TBLog.Info($"TeleportManagerSafePlace: Enforcement preview RB.isKinematic={rb.isKinematic}, RB.velocity={rb.velocity}, RB.angular={rb.angularVelocity}");
        }
        catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: enforcement preview logging threw: " + ex.ToString()); }

        while (enforceAttempt < maxEnforceAttempts && !overallSuccess)
        {
            enforceAttempt++;
            TBLog.Info($"TeleportManagerSafePlace: enforcement loop attempt {enforceAttempt}/{maxEnforceAttempts} for finalPos={finalPos} (time={Time.realtimeSinceStartup:F3})");

            // Re-resolve current playerRoot/cc/rb before each attempt (some systems reparent or recreate)
            Transform prevRoot = playerRoot;
            playerRoot = ResolveCC(out cc, out rb);
            if (playerRoot == null)
            {
                TBLog.Warn("TeleportManagerSafePlace: could not resolve player root at enforcement attempt; aborting.");
                break;
            }
            if (prevRoot != playerRoot)
            {
                TBLog.Info($"TeleportManagerSafePlace: playerRoot changed since previous resolution: prev='{GetTransformPath(prevRoot)}' now='{GetTransformPath(playerRoot)}'");
            }

            // Extra detailed debug about resolved objects
            try
            {
                TBLog.Info($"TeleportManagerSafePlace: enforcement resolved playerRoot='{GetTransformPath(playerRoot)}' pos={playerRoot.position} parent={(playerRoot.parent != null ? GetTransformPath(playerRoot.parent) : "<null>")} activeInHierarchy={playerRoot.gameObject.activeInHierarchy}");
                if (cc != null)
                    TBLog.Info($"TeleportManagerSafePlace: enforcement resolved CC center={cc.center} radius={cc.radius} height={cc.height} enabled={cc.enabled} isGrounded={cc.isGrounded}");
                if (rb != null)
                    TBLog.Info($"TeleportManagerSafePlace: enforcement resolved RB mass={rb.mass} isKinematic={rb.isKinematic} useGravity={rb.useGravity} velocity={rb.velocity} angular={rb.angularVelocity}");
            }
            catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: enforcement resolved logging threw: " + ex.ToString()); }

            string enabledCC = cc != null ? cc.enabled.ToString() : "<none>";
            string isKinematicRB = rb != null ? rb.isKinematic.ToString() : "<none>";

            bool attemptSucceeded = false;

            // Inner retry small loop for immediate set/sync/enable/nudge
            const int innerAttempts = 2;
            for (int inner = 0; inner < innerAttempts; inner++)
            {
                TBLog.Info($"TeleportManagerSafePlace: inner apply attempt {inner + 1}/{innerAttempts} (t={Time.realtimeSinceStartup:F3})");

                bool ccWasEnabled = (cc != null) ? cc.enabled : false;
                bool rbWasKinematic = (rb != null) ? rb.isKinematic : false;

                // Disable CC before setting transform
                if (cc != null && cc.enabled)
                {
                    try
                    {
                        TBLog.Info($"TeleportManagerSafePlace: disabling CC (wasEnabled={ccWasEnabled})");
                        cc.enabled = false;
                        TBLog.Info($"TeleportManagerSafePlace: CC.enabled now={cc.enabled}");
                    }
                    catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: disabling CC threw: " + ex.ToString()); }
                }

                // Log RB velocities before change
                try
                {
                    if (rb != null)
                        TBLog.Info($"TeleportManagerSafePlace: before set rb.velocity={rb.velocity} rb.angular={rb.angularVelocity} rb.isKinematic={rb.isKinematic}");
                }
                catch { }

                // Apply transform
                // <-- THIS is the exact line where the first (and subsequent) teleport is performed:
                //     playerRoot.position = finalPos;
                // If you want to delay just the first actual set, you can gate this assignment
                // with a boolean that is set after the pre-enforcement delay above.
                try
                {
                    TBLog.Info($"TeleportManagerSafePlace: setting playerRoot.position to {finalPos} (before {playerRoot.position})");
                    playerRoot.position = finalPos;
                    TBLog.Info($"TeleportManagerSafePlace: playerRoot.position set, immediate readback={playerRoot.position}");
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TeleportManagerSafePlace: setting position threw: " + ex.ToString());
                }

                // Wait physics ticks
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                yield return null;

                // Sync transforms
                try { Physics.SyncTransforms(); TBLog.Info("TeleportManagerSafePlace: Physics.SyncTransforms() called"); } catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: Physics.SyncTransforms threw: " + ex.ToString()); }

                // Force RB kinematic while settling
                if (rb != null)
                {
                    try
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    catch { }
                    try
                    {
                        rb.isKinematic = true;
                        TBLog.Info($"TeleportManagerSafePlace: rb.isKinematic set true for settling (prevWasKinematic={rbWasKinematic}) current={rb.isKinematic}");
                    }
                    catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: setting RB kinematic threw: " + ex.ToString()); }
                }

                // Re-enable CC
                if (cc != null && !cc.enabled)
                {
                    try { cc.enabled = true; TBLog.Info("TeleportManagerSafePlace: re-enabled CC after transform set"); } catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: enabling CC threw: " + ex.ToString()); }
                }

                // Nudge CC downward if available
                if (cc != null && cc.enabled)
                {
                    try { cc.Move(Vector3.down * 0.25f); TBLog.Info("TeleportManagerSafePlace: CC.Move(nudge) invoked"); } catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: CC nudge threw: " + ex.ToString()); }
                    yield return new WaitForFixedUpdate();
                    yield return null;

                    // Extra debug after nudge
                    try
                    {
                        TBLog.Info($"TeleportManagerSafePlace: post-nudge CC.enabled={cc.enabled} CC.isGrounded={cc.isGrounded} CC.center={cc.center} playerRoot.pos={playerRoot.position}");
                        if (rb != null) TBLog.Info($"TeleportManagerSafePlace: post-nudge RB.isKinematic={rb.isKinematic} RB.velocity={rb.velocity}");
                    }
                    catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: post-nudge logging threw: " + ex.ToString()); }
                }
                else
                {
                    // If no CC available, small frame wait
                    yield return null;
                }

                // Check actual distance
                Vector3 actualPos = Vector3.zero;
                try { actualPos = playerRoot.position; } catch { actualPos = Vector3.zero; }
                float dist = Vector3.Distance(actualPos, finalPos);
                TBLog.Info($"TeleportManagerSafePlace: after apply actualPos={actualPos}, distToDesired={dist:F3}m");

                if (dist <= enforceThreshold)
                {
                    TBLog.Info("TeleportManagerSafePlace: apply succeeded within threshold.");
                    attemptSucceeded = true;
                    break;
                }
                else
                {
                    TBLog.Warn($"TeleportManagerSafePlace: inner apply did not reach target (dist={dist:F3}). Will retry inner/apply or next enforcement attempt.");
                    // Additional debug: dump colliders near both desired and actual positions
                    try
                    {
                        Collider[] nearDesired = Physics.OverlapSphere(finalPos + Vector3.up * 0.5f, 0.6f, ~0, QueryTriggerInteraction.Ignore);
                        Collider[] nearActual = Physics.OverlapSphere(actualPos + Vector3.up * 0.5f, 0.6f, ~0, QueryTriggerInteraction.Ignore);
                        var namesDesired = new StringBuilder();
                        var namesActual = new StringBuilder();
                        if (nearDesired != null) foreach (var n in nearDesired) if (n != null) namesDesired.Append(n.name).Append(",");
                        if (nearActual != null) foreach (var n in nearActual) if (n != null) namesActual.Append(n.name).Append(",");
                        TBLog.Info($"TeleportManagerSafePlace: overlap check - nearDesired[{namesDesired}] nearActual[{namesActual}]");
                    }
                    catch (Exception ex) { TBLog.Warn("TeleportManagerSafePlace: extra overlap debug threw: " + ex.ToString()); }
                }
            } // end inner attempts

            if (!attemptSucceeded)
            {
                TBLog.Warn($"TeleportManagerSafePlace: enforcement attempt {enforceAttempt} failed to place within threshold.");
                // continue to next enforcement attempt
                yield return null;
                continue;
            }


            // If we got here, we achieved target; now monitor for a short period for external overrides
            float monitorStart = Time.realtimeSinceStartup;
            bool wasOverridden = false;
            TBLog.Info($"TeleportManagerSafePlace: entering post-monitor for {postMonitorDuration:F3}s to detect external overrides (start t={monitorStart:F3}).");
            while (Time.realtimeSinceStartup - monitorStart < postMonitorDuration)
            {
                Vector3 curPos = Vector3.zero;
                try { curPos = playerRoot.position; } catch { curPos = Vector3.zero; }
                float d = Vector3.Distance(curPos, finalPos);
                TBLog.Info($"TeleportManagerSafePlace: post-monitor frame pos={curPos} d={d:F3} (t={Time.realtimeSinceStartup:F3})");
                if (d > enforceThreshold)
                {
                    TBLog.Warn($"TeleportManagerSafePlace: external override detected during monitor (d={d:F3}) - will retry enforcement (attempt {enforceAttempt})\nStack:\n{Environment.StackTrace}");
                    wasOverridden = true;
                    break;
                }
                yield return null;
            }

            if (wasOverridden)
            {
                // loop to next enforcementAttempt
                continue;
            }
            else
            {
                // Final success
                overallSuccess = true;
                break;
            }

        } // end enforcement loop

        // Final restore of RB kinematic if we changed it
        try
        {
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // Note: we do not forcibly restore cc enabled flag here (keeping CC enabled for player control)
                TBLog.Info($"TeleportManagerSafePlace: final RB state vel={rb.velocity} ang={rb.angularVelocity} isKinematic={rb.isKinematic} useGravity={rb.useGravity}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: final RB restore threw: " + ex.ToString());
        }

        TBLog.Info($"TeleportManagerSafePlace: finished enforcement. final player pos={(playerRoot != null ? playerRoot.position.ToString() : "<none>")}, success={overallSuccess}");
        onComplete?.Invoke(overallSuccess);
        yield break;
    }

    // Insert into existing TeleportManagerSafePlace class (near other helpers).
    // Synchronous "compute-only" variant: does NOT move the player. Returns true if computation succeeded and
    // correctedCoords contains suggested placement; returns false on failure and correctedCoords will be requestedTarget.
    //
    // Usage:
    //   Vector3 corrected;
    //   bool ok = TeleportManagerSafePlace.ComputeSafePlacementCoords(this, requestedTarget, true, out corrected);
    //   if (ok) { /* use corrected */ } else { /* fallback */ }
    public static bool ComputeSafePlacementCoords(MonoBehaviour host, Vector3 requestedTarget, bool preserveRequestedY, out Vector3 correctedCoords)
    {
        correctedCoords = requestedTarget;
        float entryTime = Time.realtimeSinceStartup;
        TBLog.Info($"ComputeSafePlacementCoords: Enter (t={entryTime:F3}) requestedTarget={requestedTarget}, preserveRequestedY={preserveRequestedY}, host={(host != null ? host.name : "<null>")}");

        if (host == null)
        {
            TBLog.Warn("ComputeSafePlacementCoords: host is null; returning requestedTarget");
            correctedCoords = requestedTarget;
            return false;
        }

        // Helper: attempt a non-blocking resolve of player root/CC/rb for overlap filtering.
        // We do a single scan (no waiting) to avoid blocking main thread.
        Transform playerRoot = null;
        CharacterController cc = null;
        Rigidbody rb = null;
        try
        {
            cc = UnityEngine.Object.FindObjectOfType<CharacterController>();
            if (cc != null)
            {
                playerRoot = cc.transform;
                var rbs = playerRoot.GetComponentsInChildren<Rigidbody>(true);
                if (rbs != null && rbs.Length > 0) rb = rbs[0];
                TBLog.Info($"ComputeSafePlacementCoords: ResolveCC immediate found CC on '{GetTransformPath(playerRoot)}' pos={playerRoot.position}");
            }
            else
            {
                var go = GameObject.FindWithTag("Player");
                if (go != null)
                {
                    playerRoot = go.transform;
                    cc = go.GetComponentInChildren<CharacterController>(true);
                    var rbs2 = go.GetComponentsInChildren<Rigidbody>(true);
                    if (rbs2 != null && rbs2.Length > 0) rb = rbs2[0];
                    TBLog.Info($"ComputeSafePlacementCoords: ResolveCC immediate found GameObject.tag='Player' -> '{GetTransformPath(playerRoot)}' pos={playerRoot.position}");
                }
                else
                {
                    // heuristic scan (single pass)
                    var all = GameObject.FindObjectsOfType<GameObject>();
                    foreach (var g in all)
                    {
                        if (g == null || string.IsNullOrEmpty(g.name)) continue;
                        if (!g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)) continue;
                        if (g.name.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        var cc2 = g.GetComponentInChildren<CharacterController>(true);
                        if (cc2 != null) cc = cc2;
                        var rbs3 = g.GetComponentsInChildren<Rigidbody>(true);
                        if (rbs3 != null && rbs3.Length > 0) rb = rbs3[0];
                        playerRoot = g.transform;
                        TBLog.Info($"ComputeSafePlacementCoords: ResolveCC heuristic found '{GetTransformPath(playerRoot)}' pos={playerRoot.position}");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ComputeSafePlacementCoords: ResolveCC threw: " + ex);
        }

        if (playerRoot == null)
        {
            TBLog.Info("ComputeSafePlacementCoords: playerRoot not found on immediate scan; proceeding without playerRoot for overlap filtering.");
        }

        // STEP A: Raycast grounding
        Vector3 groundedPos = requestedTarget;
        bool groundedByRaycast = false;
        try
        {
            const float RAY_UP = 4.0f;
            const float RAY_DOWN = 20.0f;
            Vector3 rayStart = requestedTarget + Vector3.up * RAY_UP;
            RaycastHit hit;
            float rayLen = RAY_UP + RAY_DOWN;
            TBLog.Info($"ComputeSafePlacementCoords: performing Raycast from {rayStart} down {rayLen}");
            if (Physics.Raycast(rayStart, Vector3.down, out hit, rayLen, ~0, QueryTriggerInteraction.Ignore))
            {
                // FIXED: Increase ground clearance from 0.1 to 1.0 to account for CharacterController height
                // CharacterController typically has height ~2.0 and center at ~1.0, so we need proper clearance
                groundedPos = new Vector3(requestedTarget.x, hit.point.y + 1.0f, requestedTarget.z);
                groundedByRaycast = true;
                string colliderName = hit.collider != null ? hit.collider.name : "<none>";
                TBLog.Info($"ComputeSafePlacementCoords: raycast hit point={hit.point}, normal={hit.normal}, collider='{colliderName}' => groundedPos={groundedPos}");
            }
            else
            {
                TBLog.Info("ComputeSafePlacementCoords: raycast found no ground under requestedTarget.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ComputeSafePlacementCoords: raycast threw: " + ex);
        }

        // STEP B: NavMesh fallback if raycast not used
        if (!groundedByRaycast)
        {
            try
            {
                const float NAV_RADIUS = 6.0f;
                NavMeshHit navHit;
                TBLog.Info($"ComputeSafePlacementCoords: attempting NavMesh.SamplePosition around {requestedTarget} radius={NAV_RADIUS}");
                if (NavMesh.SamplePosition(requestedTarget, out navHit, NAV_RADIUS, NavMesh.AllAreas))
                {
                    groundedPos = navHit.position;
                    TBLog.Info($"ComputeSafePlacementCoords: NavMesh.SamplePosition success pos={navHit.position}, distance={navHit.distance:F3}");
                }
                else
                {
                    TBLog.Info("ComputeSafePlacementCoords: NavMesh.SamplePosition failed to find a nearby nav position.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("ComputeSafePlacementCoords: NavMesh.SamplePosition threw: " + ex);
            }
        }

        // STEP C: overlap/raise fallback if still not raycast-grounded
        if (!groundedByRaycast)
        {
            try
            {
                TBLog.Info("ComputeSafePlacementCoords: attempting overlap/raise fallback to find non-overlapping placement.");
                const float STEP = 0.25f;
                const float MAX_RAISE = 2.0f;
                const float RADIUS = 0.5f;
                int maxSteps = Mathf.CeilToInt(MAX_RAISE / STEP);
                bool fixedUp = false;
                for (int i = 0; i <= maxSteps; i++)
                {
                    Vector3 checkPos = requestedTarget + Vector3.up * (i * STEP);
                    Collider[] hits = Physics.OverlapSphere(checkPos + Vector3.up * 0.5f, RADIUS, ~0, QueryTriggerInteraction.Ignore);
                    bool overlapping = false;
                    if (hits != null && hits.Length > 0)
                    {
                        var names = new StringBuilder();
                        for (int hi = 0; hi < hits.Length; hi++)
                        {
                            var h = hits[hi];
                            if (h == null || h.transform == null) continue;
                            names.Append(h.name).Append(",");
                            if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                            overlapping = true;
                        }
                        TBLog.Info($"ComputeSafePlacementCoords: overlap step {i} - hits={hits.Length}, names=[{names}], overlapping={overlapping} checkPos={checkPos}");
                    }
                    else
                    {
                        TBLog.Info($"ComputeSafePlacementCoords: overlap step {i} - no hits at checkPos={checkPos}");
                    }

                    if (!overlapping)
                    {
                        groundedPos = checkPos;
                        fixedUp = true;
                        TBLog.Info($"ComputeSafePlacementCoords: overlap fallback selected groundedPos={groundedPos} at step {i}");
                        break;
                    }
                }

                if (!fixedUp)
                {
                    TBLog.Warn("ComputeSafePlacementCoords: overlap fallback could not find free spot; using requestedTarget for groundedPos");
                    groundedPos = requestedTarget;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("ComputeSafePlacementCoords: overlap fallback threw: " + ex);
                groundedPos = requestedTarget;
            }
        }

        // STEP D: decide finalPos respecting preserveRequestedY
        Vector3 finalPos = groundedPos;
        try
        {
            if (preserveRequestedY)
            {
                finalPos = requestedTarget;
                const float CHECK_RADIUS = 0.5f;
                Vector3 overlapCheckPos = finalPos + Vector3.up * 0.5f;
                Collider[] overlapHits = Physics.OverlapSphere(overlapCheckPos, CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
                bool requestedOverlaps = false;
                if (overlapHits != null && overlapHits.Length > 0)
                {
                    var names = new StringBuilder();
                    foreach (var h in overlapHits)
                    {
                        if (h == null || h.transform == null) continue;
                        names.Append(h.name).Append(",");
                        if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                        requestedOverlaps = true;
                        break;
                    }
                    TBLog.Info($"ComputeSafePlacementCoords: preserveRequestedY overlap hits [{names}] requestedOverlaps={requestedOverlaps}");
                }
                TBLog.Info($"ComputeSafePlacementCoords: preserveRequestedY={preserveRequestedY}, requestedOverlaps={(finalPos == requestedTarget ? "check_done" : "n/a")}, requestedTarget={requestedTarget}, groundedPos={groundedPos}");
                if (overlapHits != null && overlapHits.Length > 0)
                {
                    bool anyNonSelf = false;
                    foreach (var h in overlapHits)
                    {
                        if (h == null || h.transform == null) continue;
                        if (playerRoot != null && h.transform.IsChildOf(playerRoot)) continue;
                        anyNonSelf = true;
                        break;
                    }
                    if (anyNonSelf)
                    {
                        TBLog.Warn("ComputeSafePlacementCoords: requestedTarget overlaps geometry -> falling back to groundedPos");
                        finalPos = groundedPos;
                    }
                }
            }
            else
            {
                finalPos = groundedPos;
            }

            TBLog.Info($"ComputeSafePlacementCoords: chosen finalPos={finalPos} (preserveRequestedY={preserveRequestedY})");
        }
        catch (Exception ex)
        {
            TBLog.Warn("ComputeSafePlacementCoords: deciding finalPos threw: " + ex);
            finalPos = groundedPos;
        }

        // STEP E: apply AdjustForOverlapLikeFallback to attempt small raises (reuse existing helper)
        try
        {
            float raised;
            Vector3 adjusted = AdjustForOverlapLikeFallback(finalPos, playerRoot, out raised);
            if (Mathf.Abs(raised) > 0.0001f)
            {
                TBLog.Info($"ComputeSafePlacementCoords: finalPos adjusted by fallback-like overlap fix: original={finalPos}, adjusted={adjusted}, raisedBy={raised:F2}");
            }
            finalPos = adjusted;
        }
        catch (Exception ex)
        {
            TBLog.Warn("ComputeSafePlacementCoords: AdjustForOverlapLikeFallback threw: " + ex);
        }

        // Done
        correctedCoords = finalPos;
        TBLog.Info($"ComputeSafePlacementCoords: completed - correctedCoords={correctedCoords}");
        return true;
    }

    // Insert near top of file (helper fade)
    private static IEnumerator ScreenFade(float fromAlpha, float toAlpha, float duration)
    {
        // Create fullscreen black Image under an overlay Canvas.
        // Important: avoid try/finally with yields (C# iterator restriction).
        var go = new GameObject("TB_ScreenFade");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // very high sorting order to make sure it is on top
        canvas.sortingOrder = 32767;
        go.AddComponent<GraphicRaycaster>();

        var imgGO = new GameObject("TB_ScreenFadeImage");
        imgGO.transform.SetParent(go.transform, false);
        var img = imgGO.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, Mathf.Clamp01(fromAlpha));

        var rect = img.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        // Keep the fade object across scene loads so the fade persists while loading.
        UnityEngine.Object.DontDestroyOnLoad(go);

        // If duration is zero or negative, set final alpha and return one frame.
        if (duration <= 0f)
        {
            img.color = new Color(0f, 0f, 0f, Mathf.Clamp01(toAlpha));
            yield return null;                // allow one frame to render the color
            UnityEngine.Object.Destroy(go);   // destroy after the frame
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float alpha = Mathf.Lerp(fromAlpha, toAlpha, Mathf.Clamp01(t / duration));
            if (img != null) img.color = new Color(0f, 0f, 0f, alpha);
            yield return null;
        }

        if (img != null) img.color = new Color(0f, 0f, 0f, Mathf.Clamp01(toAlpha));

        // keep the final color visible for one frame then destroy the object
        yield return null;
        UnityEngine.Object.Destroy(go);
        yield return null;
    }
}
