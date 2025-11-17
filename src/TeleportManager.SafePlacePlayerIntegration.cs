using System;
using System.Collections;
using UnityEngine;

public partial class TeleportManager : MonoBehaviour
{
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

        // If we found TravelButtonUI, delegate to its SafeTeleportRoutine (yield is outside any try/catch)
        if (tbui != null)
        {
            TBLog.Info("SafePlacePlayerCoroutine: delegating to TravelButtonUI.SafeTeleportRoutine");
            yield return tbui.SafeTeleportRoutine(null, finalTarget);
            yield break;
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

        // Determine a grounded/safe position (no yields)
        Vector3 safe;
        string chosenSource;
        try
        {
            safe = FindSafeLanding(finalTarget, playerTransform, out chosenSource);
            TBLog.Info($"SafePlacePlayerCoroutine: chosen safe pos {safe} (source={chosenSource})");
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafePlacePlayerCoroutine: FindSafeLanding threw: " + ex);
            safe = finalTarget;
            chosenSource = "error";
        }

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

        TBLog.Info($"SafePlacePlayerCoroutine: placement complete at {safe} for player {playerTransform.name}");
        yield break;
    }

    // Helper: find a safe landing near hint. Returns chosen source (Raycast/Anchor/Clamp).
    private Vector3 FindSafeLanding(Vector3 hint, Transform playerTransform, out string source)
    {
        source = "none";
        try
        {
            const float upSearch = 150f;
            const float downMax = 400f;
            const float sampleRadius = 8f;
            const int radialSteps = 12;

            RaycastHit hit;
            Vector3 top = hint + Vector3.up * upSearch;

            // Direct downward raycast from above hint
            if (Physics.Raycast(top, Vector3.down, out hit, upSearch + downMax, ~0, QueryTriggerInteraction.Ignore))
            {
                Vector3 found = hit.point;
                if (IsVerticalDeltaAcceptable(playerTransform, found))
                {
                    source = "raycast";
                    return found;
                }
            }

            // small radial sampling around hint
            for (int i = 0; i < radialSteps; i++)
            {
                float ang = (360f / radialSteps) * i * Mathf.Deg2Rad;
                Vector3 offs = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * sampleRadius;
                top = hint + offs + Vector3.up * upSearch;
                if (Physics.Raycast(top, Vector3.down, out hit, upSearch + downMax, ~0, QueryTriggerInteraction.Ignore))
                {
                    Vector3 found = hit.point;
                    if (IsVerticalDeltaAcceptable(playerTransform, found))
                    {
                        source = "radial";
                        return found;
                    }
                }
            }

#if UNITY_NAVMESH_PICK
            // optional: NavMesh.SamplePosition if your project uses NavMesh
            UnityEngine.AI.NavMeshHit navHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(hint, out navHit, 100f, UnityEngine.AI.NavMesh.AllAreas))
            {
                if (IsVerticalDeltaAcceptable(playerTransform, navHit.position))
                {
                    source = "navmesh";
                    return navHit.position;
                }
            }
#endif

            // Try anchors by name (scene spawn, PlayerStart, or city anchor). Adjust names as needed.
            string[] anchorNames = new[] { "PlayerStart", "PlayerSpawn", "SpawnPoint", "PlayerAnchor", "LocationAnchor" };
            foreach (var name in anchorNames)
            {
                try
                {
                    var go = GameObject.Find(name);
                    if (go != null)
                    {
                        var pos = go.transform.position;
                        if (IsVerticalDeltaAcceptable(playerTransform, pos))
                        {
                            source = $"anchor:{name}";
                            return pos;
                        }
                    }
                }
                catch { }
            }

            // Last resort: clamp hint.y to within +/-100m of player's current y
            float safeY = (playerTransform != null) ? playerTransform.position.y : 0f;
            float clampedY = Mathf.Clamp(hint.y, safeY - 100f, safeY + 100f);
            source = "clamped";
            return new Vector3(hint.x, clampedY, hint.z);
        }
        catch (Exception ex)
        {
            TBLog.Warn("FindSafeLanding: error: " + ex);
            source = "error";
            return hint;
        }
    }

    private bool IsVerticalDeltaAcceptable(Transform playerTransform, Vector3 candidate)
    {
        try
        {
            if (playerTransform == null) return true;
            float curY = playerTransform.position.y;
            float delta = Mathf.Abs(candidate.y - curY);
            const float maxVerticalDelta = 100f;
            return delta <= maxVerticalDelta;
        }
        catch { return true; }
    }

    /// <summary>
    /// Helper that wraps SafePlacePlayerCoroutine and tracks before/after position to report success.
    /// Call this from another coroutine: yield return StartCoroutine(PlacePlayerUsingSafeRoutine(pos, moved => {...}));
    /// </summary>
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 finalTarget, Action<bool> onComplete)
    {
        TBLog.Info($"PlacePlayerUsingSafeRoutine: starting for target={finalTarget}");
        
        // Snapshot before position
        Vector3 beforePos = Vector3.zero;
        bool haveBeforePos = false;
        try
        {
            Transform playerTransform = null;
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
            
            if (playerTransform != null)
            {
                beforePos = playerTransform.position;
                haveBeforePos = true;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: failed to get before position: " + ex);
        }

        // Run the safe placement coroutine
        yield return SafePlacePlayerCoroutine(finalTarget);

        // Snapshot after position
        Vector3 afterPos = Vector3.zero;
        bool haveAfterPos = false;
        try
        {
            Transform playerTransform = null;
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
            
            if (playerTransform != null)
            {
                afterPos = playerTransform.position;
                haveAfterPos = true;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: failed to get after position: " + ex);
        }

        // Determine if movement occurred
        bool moved = false;
        if (haveBeforePos && haveAfterPos)
        {
            moved = (afterPos - beforePos).sqrMagnitude > 0.01f;
            TBLog.Info($"PlacePlayerUsingSafeRoutine: moved={moved} (before={beforePos}, after={afterPos})");
        }
        else if (haveAfterPos && !haveBeforePos)
        {
            // If we couldn't get before but can after, assume success
            moved = true;
            TBLog.Info($"PlacePlayerUsingSafeRoutine: assuming moved=true (after={afterPos}, no before position)");
        }
        else
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: could not determine movement (no position data)");
        }

        // Invoke callback
        try
        {
            onComplete?.Invoke(moved);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: callback threw: " + ex);
        }
        
        yield break;
    }
}