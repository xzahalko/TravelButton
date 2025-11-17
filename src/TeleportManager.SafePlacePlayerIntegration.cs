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
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: FindObjectOfType<TravelButtonUI> threw: " + ex);
            tbui = null;
        }

        // If we do NOT have a supplied finalTarget (zero), allow TravelButtonUI to decide.
        // If a finalTarget was provided (coords hint present), prefer our internal placement
        // so clamping and overlap-raise remain authoritative.
        bool haveSuppliedTarget = (finalTarget != Vector3.zero);
        if (tbui != null && !haveSuppliedTarget)
        {
            TBLog.Info("SafePlacePlayerCoroutine: delegating to TravelButtonUI.SafeTeleportRoutine (no explicit finalTarget)");
            yield return tbui.SafeTeleportRoutine(null, finalTarget); // TravelButtonUI handles its own safety
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
            if (go != null) playerTransform = go.transform;
            else
            {
                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                    {
                        playerTransform = g.transform;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error locating player transform: " + ex);
            playerTransform = null;
        }

        if (playerTransform == null)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: no player transform found; aborting placement");
            yield break;
        }

        // Determine a safe position to set (we already passed in finalTarget which should be pre-grounded/clamped)
        Vector3 safe = finalTarget;
        TBLog.Info($"SafePlacePlayerCoroutine: chosen pre-placement pos {safe}");

        // Gather physics/controller comps and remember state (no yields)
        Rigidbody rb = null;
        CharacterController cc = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;
        try
        {
            var rbcands = playerTransform.GetComponentsInChildren<Rigidbody>(true);
            if (rbcands != null && rbcands.Length > 0) rb = rbcands[0];
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error reading player components: " + ex);
        }

        // Disable movement/physics as best-effort (no yields)
        try
        {
            if (cc != null) cc.enabled = false;
            if (rb != null)
            {
                rb.isKinematic = true;
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
            // NOTE: if your game has custom controllers (LocalPlayer/PlayerController), disable them here similarly.
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error disabling controllers/physics: " + ex);
        }

        // Set the position (no yields inside try)
        try
        {
            playerTransform.position = safe;
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: set position failed: " + ex);
        }

        // Small immediate overlap-check and raise if embedded (D)
        try
        {
            // If player is overlapping geometry at the placed position, try raising in small steps.
            bool fixedUp = false;
            int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
            for (int i = 0; i <= maxSteps; i++)
            {
                Vector3 checkPos = playerTransform.position + Vector3.up * (i * OVERLAP_RAISE_STEP);
                // Perform an overlap check at the player's feet area
                Collider[] hits = Physics.OverlapSphere(checkPos + Vector3.up * 0.5f, OVERLAP_CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
                bool overlapping = false;
                if (hits != null && hits.Length > 0)
                {
                    // If all hits belong to the player's own hierarchy, ignore them
                    foreach (var h in hits)
                    {
                        if (h == null || h.transform == null) continue;
                        if (h.transform.IsChildOf(playerTransform)) continue; // skip self
                        overlapping = true;
                        break;
                    }
                }

                if (!overlapping)
                {
                    // Move player up to this non-overlapping checkPos
                    if (i > 0) playerTransform.position = checkPos;
                    fixedUp = true;
                    if (i > 0) TBLog.Info($"SafePlacePlayerCoroutine: raised player by {i * OVERLAP_RAISE_STEP:F2}m to avoid overlap -> {playerTransform.position}");
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
            TBLog.Warn("SafePlacePlayerCoroutine: overlap/raise check failed: " + exOverlap);
        }

        // Wait two frames to let scene scripts and physics settle (yields are outside try/catch)
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
                }
                catch { }
            }
            if (cc != null) cc.enabled = ccWasEnabled;
            // Re-enable custom controllers here (if you disabled any)
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: error restoring controllers/physics: " + ex);
        }

        TBLog.Info($"SafePlacePlayerCoroutine: placement complete at {playerTransform.position} for player {playerTransform.name}");
        yield break;
    }

    // Helper: attempt to place the player safely using SafePlacePlayerCoroutine
    // Invokes onComplete(true) if player's position changed (movement detected)
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 finalTarget, Action<bool> onComplete)
    {
        Vector3 beforePos = Vector3.zero;
        bool haveBefore = TryGetPlayerPosition(out beforePos);

        // Delegate to safe placement (which will yield TravelButtonUI.SafeTeleportRoutine if available)
        yield return StartCoroutine(SafePlacePlayerCoroutine(finalTarget));

        Vector3 afterPos = Vector3.zero;
        bool haveAfter = TryGetPlayerPosition(out afterPos);

        bool moved = false;
        if (haveBefore && haveAfter)
            moved = (afterPos - beforePos).sqrMagnitude > 0.01f;
        else if (!haveBefore && haveAfter)
            moved = true;

        try { onComplete?.Invoke(moved); } catch { }
    }

    // Helper: try to find player world position for movement detection
    private bool TryGetPlayerPosition(out Vector3 pos)
    {
        pos = Vector3.zero;
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go != null) { pos = go.transform.position; return true; }
            foreach (var g in GameObject.FindObjectsOfType<GameObject>())
            {
                if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                {
                    pos = g.transform.position;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
}