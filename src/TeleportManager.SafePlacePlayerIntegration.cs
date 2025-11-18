using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// TeleportManagerSafePlace: Static class providing robust post-load safe-placement integration.
/// Implements grounding, CharacterController settlement, and reliable player placement after scene load.
/// </summary>
public static class TeleportManagerSafePlace
{
    // Constants for grounding and overlap detection
    private const float RAYCAST_UP_OFFSET = 5.0f;
    private const float RAYCAST_MAX_DOWN = 30.0f;
    private const float NAV_SAMPLE_RADIUS = 10.0f;
    private const float OVERLAP_CHECK_RADIUS = 0.5f;
    private const float OVERLAP_RAISE_STEP = 0.25f;
    private const float OVERLAP_MAX_RAISE = 3.0f;
    private const float PLAYER_DETECTION_TIMEOUT = 8.0f;

    /// <summary>
    /// Primary safe placement routine. Call via: yield return host.StartCoroutine(PlacePlayerUsingSafeRoutine_Internal(...))
    /// </summary>
    public static IEnumerator PlacePlayerUsingSafeRoutine_Internal(MonoBehaviour host, Vector3 requestedTarget, Action<bool> onComplete)
    {
        TBLog.Info($"TeleportManagerSafePlace: requestedTarget={requestedTarget}");

        // Step 1: Wait for player root/CharacterController to exist (bounded timeout)
        Transform playerRoot = null;
        CharacterController cc = null;
        Rigidbody rb = null;
        
        float detectionStart = Time.realtimeSinceStartup;
        bool playerFound = false;

        while (Time.realtimeSinceStartup - detectionStart < PLAYER_DETECTION_TIMEOUT && !playerFound)
        {
            // Try FindObjectOfType<CharacterController>
            try
            {
                cc = UnityEngine.Object.FindObjectOfType<CharacterController>();
                if (cc != null)
                {
                    playerRoot = cc.transform;
                    playerFound = true;
                    TBLog.Info($"TeleportManagerSafePlace: found player by CharacterController on '{cc.gameObject.name}'");
                    break;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManagerSafePlace: FindObjectOfType<CharacterController> threw: " + ex.ToString());
            }

            // Try GameObject.FindWithTag("Player")
            if (!playerFound)
            {
                try
                {
                    var go = GameObject.FindWithTag("Player");
                    if (go != null)
                    {
                        playerRoot = go.transform;
                        playerFound = true;
                        TBLog.Info($"TeleportManagerSafePlace: found player by tag 'Player' -> '{go.name}'");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TeleportManagerSafePlace: GameObject.FindWithTag threw: " + ex.ToString());
                }
            }

            // Heuristic search for GameObjects named PlayerChar* (skip camera candidates)
            if (!playerFound)
            {
                try
                {
                    foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                    {
                        if (g == null || string.IsNullOrEmpty(g.name)) continue;
                        if (!g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)) continue;
                        
                        // Skip camera candidates
                        if (g.name.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0 || g.GetComponent<Camera>() != null)
                            continue;

                        playerRoot = g.transform;
                        playerFound = true;
                        TBLog.Info($"TeleportManagerSafePlace: found player by name heuristic -> '{g.name}'");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TeleportManagerSafePlace: heuristic search threw: " + ex.ToString());
                }
            }

            if (!playerFound)
            {
                yield return null; // Wait one frame and retry
            }
        }

        if (!playerFound || playerRoot == null)
        {
            TBLog.Warn("TeleportManagerSafePlace: timeout waiting for player; placement failed");
            try { onComplete?.Invoke(false); } catch { }
            yield break;
        }

        TBLog.Info($"TeleportManagerSafePlace: player found: '{playerRoot.name}'");

        // Step 2: Compute grounded final position
        Vector3 finalPos = requestedTarget;
        bool grounded = false;
        string groundingMethod = "none";

        // Try raycast down from above target (preferred method)
        try
        {
            Vector3 rayStart = requestedTarget + Vector3.up * RAYCAST_UP_OFFSET;
            RaycastHit hit;
            if (Physics.Raycast(rayStart, Vector3.down, out hit, RAYCAST_UP_OFFSET + RAYCAST_MAX_DOWN, ~0, QueryTriggerInteraction.Ignore))
            {
                finalPos = new Vector3(requestedTarget.x, hit.point.y + 0.1f, requestedTarget.z);
                grounded = true;
                groundingMethod = "grounded by raycast";
                TBLog.Info($"TeleportManagerSafePlace: {groundingMethod} to {hit.point} => finalPos={finalPos} (collider='{hit.collider?.name}')");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: raycast grounding threw: " + ex.ToString());
        }

        // Try NavMesh.SamplePosition if raycast failed
        if (!grounded)
        {
            try
            {
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(requestedTarget, out navHit, NAV_SAMPLE_RADIUS, NavMesh.AllAreas))
                {
                    finalPos = navHit.position;
                    grounded = true;
                    groundingMethod = "snapped to NavMesh";
                    TBLog.Info($"TeleportManagerSafePlace: {groundingMethod} at {navHit.position} (distance={navHit.distance})");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManagerSafePlace: NavMesh.SamplePosition threw: " + ex.ToString());
            }
        }

        // Overlap/raise fallback
        if (!grounded)
        {
            try
            {
                TBLog.Info("TeleportManagerSafePlace: attempting overlap/raise fallback");
                int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
                bool foundClear = false;

                for (int i = 0; i <= maxSteps; i++)
                {
                    Vector3 checkPos = requestedTarget + Vector3.up * (i * OVERLAP_RAISE_STEP);
                    Collider[] hits = Physics.OverlapSphere(checkPos + Vector3.up * 0.5f, OVERLAP_CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
                    bool overlapping = false;

                    if (hits != null && hits.Length > 0)
                    {
                        foreach (var h in hits)
                        {
                            if (h == null || h.transform == null) continue;
                            if (h.transform.IsChildOf(playerRoot)) continue;
                            overlapping = true;
                            break;
                        }
                    }

                    if (!overlapping)
                    {
                        finalPos = checkPos;
                        foundClear = true;
                        groundingMethod = "overlap/raise fallback";
                        TBLog.Info($"TeleportManagerSafePlace: {groundingMethod} found clear position at step {i}: {checkPos}");
                        break;
                    }
                }

                if (!foundClear)
                {
                    TBLog.Warn("TeleportManagerSafePlace: overlap/raise could not find clear spot; using requestedTarget as-is");
                    finalPos = requestedTarget;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManagerSafePlace: overlap/raise fallback threw: " + ex.ToString());
                finalPos = requestedTarget;
            }
        }

        TBLog.Info($"TeleportManagerSafePlace: applying finalPos={finalPos} (method: {groundingMethod})");

        // Gather components and remember state (outside try/catch for yields)
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;

        try
        {
            cc = playerRoot.GetComponentInChildren<CharacterController>(true);
            var rbcands = playerRoot.GetComponentsInChildren<Rigidbody>(true);
            if (rbcands != null && rbcands.Length > 0) rb = rbcands[0];

            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;

            TBLog.Info($"TeleportManagerSafePlace: components: cc={(cc != null ? "YES" : "NO")}, ccWasEnabled={ccWasEnabled}, rb={(rb != null ? "YES" : "NO")}, rbWasKinematic={rbWasKinematic}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: error reading components: " + ex.ToString());
        }

        // Disable controllers/physics before positioning (outside try/catch)
        try
        {
            if (cc != null) cc.enabled = false;
            if (rb != null)
            {
                rb.isKinematic = true;
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: error disabling components: " + ex.ToString());
        }

        // Step 3: Apply finalPos (no yields inside this try/catch to avoid CS1626)
        bool positionApplied = false;
        try
        {
            playerRoot.position = finalPos;
            positionApplied = true;
            TBLog.Info($"TeleportManagerSafePlace: set playerRoot.position to {finalPos}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: failed to set position: " + ex.ToString());
        }

        // Step 4: Wait for physics to settle (yields outside try/catch)
        if (positionApplied)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return null;
        }
        else
        {
            yield return null;
        }

        // Step 5: Physics.SyncTransforms
        try
        {
            Physics.SyncTransforms();
            TBLog.Info("TeleportManagerSafePlace: Physics.SyncTransforms() called");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: Physics.SyncTransforms threw: " + ex.ToString());
        }

        // Step 6: Force-enable CharacterController for settlement (recommended default)
        try
        {
            if (cc != null)
            {
                cc.enabled = true;
                TBLog.Info("TeleportManagerSafePlace: CharacterController force-enabled for settling.");

                // Remember original Rigidbody state and set kinematic during CC probe
                if (rb != null)
                {
                    rb.isKinematic = true;
                    try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: error force-enabling CharacterController: " + ex.ToString());
        }

        // Step 7: Nudge CharacterController downward to trigger grounding
        if (cc != null && cc.enabled)
        {
            try
            {
                cc.Move(Vector3.down * 0.25f);
                TBLog.Info("TeleportManagerSafePlace: CharacterController nudged downward");
            }
            catch (Exception ex)
            {
                TBLog.Warn("TeleportManagerSafePlace: CharacterController nudge threw: " + ex.ToString());
            }

            // Wait for physics tick and frame
            yield return new WaitForFixedUpdate();
            yield return null;
        }

        // Step 8: Restore Rigidbody.isKinematic to original value
        try
        {
            if (rb != null)
            {
                rb.isKinematic = rbWasKinematic;
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
                TBLog.Info($"TeleportManagerSafePlace: restored Rigidbody.isKinematic={rbWasKinematic}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: error restoring Rigidbody state: " + ex.ToString());
        }

        // Log final state
        try
        {
            Vector3 pos = playerRoot.position;
            bool ccEnabled = cc != null ? cc.enabled : false;
            bool rbIsKinematic = rb != null ? rb.isKinematic : false;
            TBLog.Info($"TeleportManagerSafePlace: final pos={pos} ccEnabled={ccEnabled} rbIsKinematic={rbIsKinematic}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: error reading final state: " + ex.ToString());
        }

        // Step 9: Invoke onComplete
        bool success = positionApplied && playerFound;
        try
        {
            onComplete?.Invoke(success);
        }
        catch (Exception ex)
        {
            TBLog.Warn("TeleportManagerSafePlace: onComplete threw: " + ex.ToString());
        }

        yield break;
    }
}
