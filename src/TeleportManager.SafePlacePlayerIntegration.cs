using System;
using System.Collections;
using UnityEngine;

public partial class TeleportManager : MonoBehaviour
{
    // Tune these constants for overlap/raise behavior
    private const float OVERLAP_CHECK_RADIUS = 0.45f; // approximate player radius for overlap checks
    private const float OVERLAP_RAISE_STEP = 0.25f;   // amount to raise per iteration when embedded
    private const float OVERLAP_MAX_RAISE = 3.0f;     // maximum upward correction to escape embedding

    // Primary safe placement entrypoint used after scene activation.
    // Call: yield return StartCoroutine(SafePlacePlayerCoroutine(finalTarget));
    public IEnumerator SafePlacePlayerCoroutine(Vector3 finalTarget)
    {
        TBLog.Info($"SafePlacePlayerCoroutine: requested finalTarget={finalTarget}");

        // Detection phase: try to locate TravelButtonUI (do not yield inside try/catch)
        TravelButtonUI tbui = null;
        try
        {
            tbui = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            TBLog.Info($"SafePlacePlayerCoroutine: TravelButtonUI lookup -> {(tbui != null ? "FOUND" : "null")}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: FindObjectOfType<TravelButtonUI> threw: " + ex.ToString());
            tbui = null;
        }

        // If we do NOT have a supplied finalTarget (zero), allow TravelButtonUI to decide.
        // If a finalTarget was provided (coords hint present), prefer our internal placement
        // so clamping and overlap-raise remain authoritative.
        bool haveSuppliedTarget = (finalTarget != Vector3.zero);
        TBLog.Info($"SafePlacePlayerCoroutine: haveSuppliedTarget={haveSuppliedTarget}");

        if (tbui != null && !haveSuppliedTarget)
        {
            TBLog.Info("SafePlacePlayerCoroutine: delegating to TravelButtonUI.SafeTeleportRoutine (no explicit finalTarget)");
            // Log before delegation
            float delStart = Time.realtimeSinceStartup;
            yield return tbui.SafeTeleportRoutine(null, finalTarget); // TravelButtonUI handles its own safety
            float delDur = Time.realtimeSinceStartup - delStart;
            TBLog.Info($"SafePlacePlayerCoroutine: returned from TravelButtonUI.SafeTeleportRoutine (duration={delDur:F2}s)");
            yield break;
        }
        else if (tbui != null && haveSuppliedTarget)
        {
            TBLog.Info("SafePlacePlayerCoroutine: TravelButtonUI present but explicit finalTarget supplied — using internal safe placement to respect coordsHint.");
        }

        // Fallback detection: try to find player transform (no yields inside try/catch)
        Transform playerTransform = null;
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go != null)
            {
                playerTransform = go.transform;
                TBLog.Info($"SafePlacePlayerCoroutine: found player by tag 'Player' -> name='{go.name}'");
            }
            else
            {
                TBLog.Info("SafePlacePlayerCoroutine: GameObject.FindWithTag('Player') returned null; scanning for PlayerChar* names");
                int scanned = 0;
                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    scanned++;
                    if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                    {
                        playerTransform = g.transform;
                        TBLog.Info($"SafePlacePlayerCoroutine: found player by name heuristic -> '{g.name}' (scanned {scanned} GameObjects)");
                        break;
                    }
                }
                if (playerTransform == null) TBLog.Info($"SafePlacePlayerCoroutine: player search scanned {scanned} GameObjects and found no candidate.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error locating player transform: " + ex.ToString());
            playerTransform = null;
        }

        if (playerTransform == null)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: no player transform found; aborting placement");
            yield break;
        }

        // Determine a safe position to set (we already passed in finalTarget which should be pre-grounded/clamped)
        Vector3 safe = finalTarget;
        TBLog.Info($"SafePlacePlayerCoroutine: chosen pre-placement pos {safe} (player transform root='{playerTransform.root?.name ?? "<null>"}')");

        // Gather physics/controller comps and remember state (no yields)
        Rigidbody rb = null;
        CharacterController cc = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;
        try
        {
            var rbcands = playerTransform.GetComponentsInChildren<Rigidbody>(true);
            TBLog.Info($"SafePlacePlayerCoroutine: found {rbcands?.Length ?? 0} Rigidbody(s) under player transform.");
            if (rbcands != null && rbcands.Length > 0) rb = rbcands[0];
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
            TBLog.Info($"SafePlacePlayerCoroutine: CharacterController present? {(cc != null ? "YES" : "NO")}");
            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;
            TBLog.Info($"SafePlacePlayerCoroutine: recorded ccWasEnabled={ccWasEnabled}, rbWasKinematic={rbWasKinematic}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error reading player components: " + ex.ToString());
        }

        // Disable movement/physics as best-effort (no yields)
        try
        {
            if (cc != null)
            {
                TBLog.Info("SafePlacePlayerCoroutine: disabling CharacterController");
                cc.enabled = false;
            }
            if (rb != null)
            {
                TBLog.Info($"SafePlacePlayerCoroutine: setting Rigidbody.isKinematic=true on '{rb.gameObject.name}' (was {rbWasKinematic})");
                rb.isKinematic = true;
                try
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                catch (Exception exVel)
                {
                    TBLog.Warn("SafePlacePlayerCoroutine: failed to zero rigidbody velocities: " + exVel.ToString());
                }
            }
            // NOTE: if your game has custom controllers (LocalPlayer/PlayerController), disable them here similarly.
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error disabling controllers/physics: " + ex.ToString());
        }

        // Record pre-move position for diagnostics
        Vector3 beforePos = Vector3.zero;
        try
        {
            beforePos = playerTransform.position;
            TBLog.Info($"SafePlacePlayerCoroutine: player position before move = {beforePos}");
        }
        catch { }

        // Set the position (no yields inside try)
        try
        {
            playerTransform.position = safe;
            TBLog.Info($"SafePlacePlayerCoroutine: set player position to {safe}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: set position failed: " + ex.ToString());
        }

        // Small immediate overlap-check and raise if embedded (D)
        try
        {
            // If player is overlapping geometry at the placed position, try raising in small steps.
            bool fixedUp = false;
            int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
            TBLog.Info($"SafePlacePlayerCoroutine: overlap check will try up to {maxSteps} steps (step={OVERLAP_RAISE_STEP}, maxRaise={OVERLAP_MAX_RAISE}, radius={OVERLAP_CHECK_RADIUS})");
            for (int i = 0; i <= maxSteps; i++)
            {
                Vector3 checkPos = playerTransform.position + Vector3.up * (i * OVERLAP_RAISE_STEP);
                // Perform an overlap check at the player's feet area
                Collider[] hits = Physics.OverlapSphere(checkPos + Vector3.up * 0.5f, OVERLAP_CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
                int hitsCount = hits?.Length ?? 0;
                int nonSelfHits = 0;
                var hitNames = new System.Text.StringBuilder();

                bool overlapping = false;
                if (hits != null && hits.Length > 0)
                {
                    foreach (var h in hits)
                    {
                        if (h == null || h.transform == null) continue;
                        bool isSelf = h.transform.IsChildOf(playerTransform);
                        if (!isSelf)
                        {
                            nonSelfHits++;
                            hitNames.Append(h.name).Append(",");
                            overlapping = true;
                        }
                    }
                }

                TBLog.Info($"SafePlacePlayerCoroutine: overlap step {i}: hits={hitsCount}, nonSelfHits={nonSelfHits}, overlapping={overlapping}, checkPos={checkPos}");

                if (!overlapping)
                {
                    // Move player up to this non-overlapping checkPos
                    if (i > 0)
                    {
                        playerTransform.position = checkPos;
                        TBLog.Info($"SafePlacePlayerCoroutine: moved player to non-overlapping position at step {i}: {playerTransform.position}");
                    }
                    fixedUp = true;
                    break;
                }
            }

            if (!fixedUp)
            {
                TBLog.Warn($"SafePlacePlayerCoroutine: could not find non-overlapping spot within {OVERLAP_MAX_RAISE}m above {playerTransform.position}. Player may still be embedded.");
            }
        }
        catch (Exception exOverlap)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: overlap/raise check failed: " + exOverlap.ToString());
        }

        // Wait two frames to let scene scripts and physics settle (yields are outside try/catch)
        TBLog.Info("SafePlacePlayerCoroutine: waiting two frames for physics to settle");
        yield return null;
        yield return null;

        // Restore physics/controllers (no yields)
        try
        {
            if (rb != null)
            {
                try
                {
                    rb.isKinematic = rbWasKinematic;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    TBLog.Info($"SafePlacePlayerCoroutine: restored Rigidbody.isKinematic={rbWasKinematic} on '{rb.gameObject.name}'");
                }
                catch (Exception exRestoreRb)
                {
                    TBLog.Warn("SafePlacePlayerCoroutine: failed restoring rigidbody state: " + exRestoreRb.ToString());
                }
            }
            if (cc != null)
            {
                cc.enabled = ccWasEnabled;
                TBLog.Info($"SafePlacePlayerCoroutine: restored CharacterController.enabled={ccWasEnabled}");
            }
            // Re-enable custom controllers here (if you disabled any)
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error restoring controllers/physics: " + ex.ToString());
        }

        Vector3 afterPos = Vector3.zero;
        try
        {
            afterPos = playerTransform.position;
            TBLog.Info($"SafePlacePlayerCoroutine: placement complete. before={beforePos}, after={afterPos}, playerName='{playerTransform.name}'");
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error reading final player position: " + ex.ToString());
        }

        yield break;
    }

    // Helper: attempt to place the player safely using SafePlacePlayerCoroutine
    // Invokes onComplete(true) if player's position changed (movement detected)
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 finalTarget, Action<bool> onComplete)
    {
        TBLog.Info($"PlacePlayerUsingSafeRoutine: ENTER finalTarget={finalTarget}");

        Vector3 beforePos = Vector3.zero;
        bool haveBefore = false;
        try
        {
            haveBefore = TryGetPlayerPosition(out beforePos);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: TryGetPlayerPosition threw: " + ex.ToString());
            haveBefore = false;
        }
        TBLog.Info($"PlacePlayerUsingSafeRoutine: before position present={haveBefore} pos={(haveBefore ? beforePos.ToString() : "<no-before>")}");

        // Delegate to safe placement (which will yield TravelButtonUI.SafeTeleportRoutine if available)
        float startTime = Time.realtimeSinceStartup;

        TBLog.Info("PlacePlayerUsingSafeRoutine: starting SafePlacePlayerCoroutine...");
        yield return StartCoroutine(SafePlacePlayerCoroutine(finalTarget));
        float dur = Time.realtimeSinceStartup - startTime;
        TBLog.Info($"PlacePlayerUsingSafeRoutine: SafePlacePlayerCoroutine completed in {dur:F2}s");

        Vector3 afterPos = Vector3.zero;
        bool haveAfter = false;
        try
        {
            haveAfter = TryGetPlayerPosition(out afterPos);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: TryGetPlayerPosition (after) threw: " + ex.ToString());
            haveAfter = false;
        }
        TBLog.Info($"PlacePlayerUsingSafeRoutine: after position present={haveAfter} pos={(haveAfter ? afterPos.ToString() : "<no-after>")}");

        bool moved = false;
        if (haveBefore && haveAfter)
        {
            var delta = afterPos - beforePos;
            float sq = delta.sqrMagnitude;
            moved = sq > 0.01f;
            TBLog.Info($"PlacePlayerUsingSafeRoutine: computed movement delta={delta} sqrMagnitude={sq:F6} -> moved={moved}");
        }
        else if (!haveBefore && haveAfter)
        {
            moved = true;
            TBLog.Info("PlacePlayerUsingSafeRoutine: no before position but have after position -> moved=true");
        }
        else
        {
            moved = false;
            TBLog.Info("PlacePlayerUsingSafeRoutine: insufficient position info -> moved=false");
        }

        try
        {
            TBLog.Info("PlacePlayerUsingSafeRoutine: invoking onComplete callback with moved=" + moved);
            onComplete?.Invoke(moved);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: onComplete threw: " + ex.ToString());
        }

        TBLog.Info("PlacePlayerUsingSafeRoutine: EXIT");
    }

    // TeleportManager.cs
    // public wrapper so external code can request the existing placement coroutine
    public IEnumerator PlacePlayerUsingSafeRoutineWrapper(Vector3 finalPos, Action<bool> resultCallback)
    {
        // call your existing internal method (which may be private)
        // this yields the same behavior but exposes a public entrypoint
        yield return StartCoroutine(PlacePlayerUsingSafeRoutine(finalPos, resultCallback));
    }

    // Helper: try to find player world position for movement detection
    private bool TryGetPlayerPosition(out Vector3 pos)
    {
        pos = Vector3.zero;
        try
        {
            TBLog.Info("TryGetPlayerPosition: attempting to find player by tag 'Player'");
            var go = GameObject.FindWithTag("Player");
            if (go != null)
            {
                pos = go.transform.position;
                TBLog.Info($"TryGetPlayerPosition: found by tag 'Player' -> name='{go.name}', pos={pos}");
                return true;
            }

            TBLog.Info("TryGetPlayerPosition: no GameObject with tag 'Player' found; scanning all GameObjects for 'PlayerChar*' names");
            int scanned = 0;
            foreach (var g in GameObject.FindObjectsOfType<GameObject>())
            {
                scanned++;
                try
                {
                    if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                    {
                        pos = g.transform.position;
                        TBLog.Info($"TryGetPlayerPosition: found by name heuristic '{g.name}' after scanning {scanned} objects -> pos={pos}");
                        return true;
                    }
                }
                catch (Exception exInner)
                {
                    TBLog.Warn($"TryGetPlayerPosition: exception reading GameObject '{g?.name ?? "<null>"}' during scan: {exInner}");
                }
            }

            TBLog.Info($"TryGetPlayerPosition: scanned {scanned} GameObjects; no player found");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryGetPlayerPosition: exception while locating player: " + ex.ToString());
        }

        return false;
    }
}