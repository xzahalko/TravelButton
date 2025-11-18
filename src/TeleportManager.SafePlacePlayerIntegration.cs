using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// TeleportManagerSafePlace: Static helper class that provides robust post-load safe-placement integration.
/// This ensures TeleportMode=New teleports reliably place the player on ground/navmesh and the 
/// CharacterController/Rigidbody settle correctly after scene load.
/// </summary>
public static class TeleportManagerSafePlace
{
    // Tune these constants for grounding behavior
    private const float RAYCAST_UP_OFFSET = 4.0f;      // how far above target we start raycast
    private const float RAYCAST_MAX_DOWN = 20.0f;      // max distance to look down for ground
    private const float NAV_SAMPLE_RADIUS = 6.0f;      // NavMesh search radius
    private const float OVERLAP_CHECK_RADIUS = 0.5f;   // approximate player radius for overlap checks
    private const float OVERLAP_RAISE_STEP = 0.25f;    // amount to raise per iteration when embedded
    private const float OVERLAP_MAX_RAISE = 2.0f;      // maximum upward correction to escape embedding
    private const float PLAYER_WAIT_TIMEOUT = 5.0f;    // max time to wait for player to exist in scene
    private const float CC_NUDGE_AMOUNT = 0.25f;       // CharacterController downward nudge distance

    /// <summary>
    /// Primary safe placement coroutine to be called after scene load.
    /// Finds the player, computes grounded position, applies it, and settles physics.
    /// </summary>
    /// <param name="host">MonoBehaviour host to run this coroutine on</param>
    /// <param name="requestedTarget">Target position for player placement</param>
    /// <param name="onComplete">Callback invoked with true if placement succeeded, false otherwise</param>
    public static IEnumerator PlacePlayerUsingSafeRoutine_Internal(MonoBehaviour host, Vector3 requestedTarget, Action<bool> onComplete)
    {
        TBLog.Info($"TeleportManagerSafePlace: requestedTarget={requestedTarget}");

        // Step 1: Wait for player root/CharacterController to exist in the newly loaded scene
        Transform playerRoot = null;
        CharacterController cc = null;
        float waitStart = Time.realtimeSinceStartup;
        bool playerFound = false;

        while (Time.realtimeSinceStartup - waitStart < PLAYER_WAIT_TIMEOUT)
        {
            // Try finding by CharacterController first
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
                TBLog.Warn($"TeleportManagerSafePlace: FindObjectOfType<CharacterController> threw: {ex.Message}");
            }

            // Fallback: try tag "Player"
            try
            {
                GameObject playerGo = GameObject.FindWithTag("Player");
                if (playerGo != null)
                {
                    playerRoot = playerGo.transform;
                    playerFound = true;
                    TBLog.Info($"TeleportManagerSafePlace: found player by tag 'Player' -> '{playerGo.name}'");
                    break;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TeleportManagerSafePlace: FindWithTag threw: {ex.Message}");
            }

            // Fallback: try name prefix "PlayerChar" (non-Cam objects)
            try
            {
                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (g == null || string.IsNullOrEmpty(g.name)) continue;
                    if (!g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)) continue;
                    
                    // Skip camera-like objects
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
                TBLog.Warn($"TeleportManagerSafePlace: heuristic scan threw: {ex.Message}");
            }

            if (playerFound) break;

            // Wait a frame before trying again
            yield return null;
        }

        if (!playerFound || playerRoot == null)
        {
            TBLog.Warn($"TeleportManagerSafePlace: timeout waiting for player (waited {PLAYER_WAIT_TIMEOUT}s); aborting placement");
            try { onComplete?.Invoke(false); } catch (Exception ex) { TBLog.Warn($"TeleportManagerSafePlace: onComplete callback threw: {ex}"); }
            yield break;
        }

        TBLog.Info($"TeleportManagerSafePlace: player root found: '{playerRoot.name}' at position {playerRoot.position}");

        // Step 2: Compute grounded final position using raycast → NavMesh → overlap/raise fallback
        Vector3 finalPos = requestedTarget;
        bool groundedViaRaycast = false;
        bool groundedViaNavmesh = false;
        string groundingMethod = "none";

        // 2a) Try raycast down from above the requested target
        try
        {
            Vector3 rayStart = requestedTarget + Vector3.up * RAYCAST_UP_OFFSET;
            RaycastHit hit;
            if (Physics.Raycast(rayStart, Vector3.down, out hit, RAYCAST_UP_OFFSET + RAYCAST_MAX_DOWN, ~0, QueryTriggerInteraction.Ignore))
            {
                // Use hit.point as ground; add small offset to avoid interpenetration
                finalPos = new Vector3(requestedTarget.x, hit.point.y + 0.1f, requestedTarget.z);
                groundedViaRaycast = true;
                groundingMethod = "raycast";
                TBLog.Info($"TeleportManagerSafePlace: grounded by raycast to {hit.point} => finalPos={finalPos} (collider='{hit.collider?.name}')");
            }
            else
            {
                TBLog.Info("TeleportManagerSafePlace: raycast down found no ground under requested target.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: raycast grounding threw: {ex}");
        }

        // 2b) If no raycast ground, try NavMesh.SamplePosition
        if (!groundedViaRaycast)
        {
            try
            {
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(requestedTarget, out navHit, NAV_SAMPLE_RADIUS, NavMesh.AllAreas))
                {
                    finalPos = navHit.position;
                    groundedViaNavmesh = true;
                    groundingMethod = "navmesh";
                    TBLog.Info($"TeleportManagerSafePlace: snapped to NavMesh at {navHit.position} (distance={navHit.distance})");
                }
                else
                {
                    TBLog.Info("TeleportManagerSafePlace: NavMesh.SamplePosition failed to find nearby nav position.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TeleportManagerSafePlace: NavMesh.SamplePosition threw: {ex}");
            }
        }

        // 2c) If neither raycast nor navmesh, use overlap/raise fallback
        if (!groundedViaRaycast && !groundedViaNavmesh)
        {
            try
            {
                TBLog.Info("TeleportManagerSafePlace: attempting overlap/raise fallback to avoid intersections.");
                bool fixedUp = false;
                int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
                
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

                    TBLog.Info($"TeleportManagerSafePlace: overlap step {i}, hits={(hits?.Length ?? 0)}, overlapping={overlapping}, checkPos={checkPos}");

                    if (!overlapping)
                    {
                        finalPos = checkPos;
                        fixedUp = true;
                        groundingMethod = "overlap-raise";
                        break;
                    }
                }

                if (!fixedUp)
                {
                    TBLog.Warn($"TeleportManagerSafePlace: could not find non-overlapping spot; using requestedTarget {requestedTarget}");
                    finalPos = requestedTarget;
                    groundingMethod = "fallback-requested";
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TeleportManagerSafePlace: overlap fallback threw: {ex}");
                finalPos = requestedTarget;
                groundingMethod = "fallback-exception";
            }
        }

        TBLog.Info($"TeleportManagerSafePlace: grounding strategy used: {groundingMethod}, finalPos={finalPos}");

        // Step 3: Get CharacterController and Rigidbody components, remember their states
        Rigidbody rb = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;

        try
        {
            // Re-get CharacterController from playerRoot if we didn't find it initially
            if (cc == null)
            {
                cc = playerRoot.GetComponentInChildren<CharacterController>(true);
            }
            
            var rbcands = playerRoot.GetComponentsInChildren<Rigidbody>(true);
            if (rbcands != null && rbcands.Length > 0) rb = rbcands[0];

            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;

            TBLog.Info($"TeleportManagerSafePlace: cc={(cc != null ? "YES" : "NO")} ccWasEnabled={ccWasEnabled}, rb={(rb != null ? "YES" : "NO")} rbWasKinematic={rbWasKinematic}");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: error reading components: {ex}");
        }

        // Step 4: Disable CharacterController and set Rigidbody kinematic, clear velocities
        try
        {
            if (cc != null)
            {
                cc.enabled = false;
                TBLog.Info("TeleportManagerSafePlace: CharacterController disabled for repositioning");
            }
            
            if (rb != null)
            {
                rb.isKinematic = true;
                try
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                catch (Exception exVel)
                {
                    TBLog.Warn($"TeleportManagerSafePlace: failed to zero rigidbody velocities: {exVel}");
                }
                TBLog.Info($"TeleportManagerSafePlace: Rigidbody set to kinematic, velocities cleared");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: error disabling components: {ex}");
        }

        // Step 5: Apply the final position (do not yield inside this try block)
        bool appliedPosition = false;
        try
        {
            TBLog.Info($"TeleportManagerSafePlace: applying finalPos={finalPos}");
            playerRoot.position = finalPos;
            appliedPosition = true;
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: failed to set position: {ex}");
        }

        // Step 6: Wait for physics to notice the transform change (yields outside try/catch)
        if (appliedPosition)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return null; // allow Update to run once as well
        }
        else
        {
            // Still yield one frame so caller's coroutine scheduling remains sane
            yield return null;
        }

        // Step 7: Call Physics.SyncTransforms to ensure physics uses the new transform immediately
        try
        {
            Physics.SyncTransforms();
            TBLog.Info("TeleportManagerSafePlace: Physics.SyncTransforms called");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: Physics.SyncTransforms threw: {ex}");
        }

        // Step 8: Force-enable CharacterController (recommended default) and keep RB kinematic while CC settles
        bool ccForced = false;
        try
        {
            if (cc != null)
            {
                if (!cc.enabled)
                {
                    cc.enabled = true;
                    ccForced = true;
                    TBLog.Info("TeleportManagerSafePlace: CharacterController force-enabled for settling");
                }
                else
                {
                    TBLog.Info("TeleportManagerSafePlace: CharacterController already enabled");
                }
            }

            // Keep Rigidbody kinematic while CC settles; clear velocities again
            if (rb != null)
            {
                try
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                catch { }
                rb.isKinematic = true;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: error during CC/RB force-enable: {ex}");
        }

        // Step 9: Nudge CharacterController down to trigger grounding/collision probes
        if (cc != null && cc.enabled)
        {
            try
            {
                cc.Move(Vector3.down * CC_NUDGE_AMOUNT);
                TBLog.Info($"TeleportManagerSafePlace: CharacterController nudged down by {CC_NUDGE_AMOUNT}");
            }
            catch (Exception ex)
            {
                TBLog.Warn($"TeleportManagerSafePlace: CharacterController nudge threw: {ex}");
            }

            // Wait one physics tick for the CharacterController to settle
            yield return new WaitForFixedUpdate();
            yield return null;
        }

        // Step 10: Restore Rigidbody.isKinematic to original value, zero velocities again
        try
        {
            if (rb != null)
            {
                try
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = rbWasKinematic;
                    TBLog.Info($"TeleportManagerSafePlace: Rigidbody.isKinematic restored to {rbWasKinematic}");
                }
                catch { }
            }

            // If we forced CC on but it was originally disabled, keep it enabled (recommended for reliability)
            // Most game systems expect CC to be active after placement
            Vector3 finalPlayerPos = playerRoot.position;
            TBLog.Info($"TeleportManagerSafePlace: final pos={finalPlayerPos} ccEnabled={(cc != null ? cc.enabled.ToString() : "<none>")} rbIsKinematic={(rb != null ? rb.isKinematic.ToString() : "<none>")}");
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: restore failed: {ex}");
        }

        // Step 11: Invoke onComplete callback with success=true (best-effort)
        try
        {
            TBLog.Info("TeleportManagerSafePlace: placement succeeded (best-effort)");
            onComplete?.Invoke(true);
        }
        catch (Exception ex)
        {
            TBLog.Warn($"TeleportManagerSafePlace: onComplete callback threw: {ex}");
        }

        yield break;
    }
}
