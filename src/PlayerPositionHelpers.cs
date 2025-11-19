using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Helper methods to nudge the player's world position by fixed offsets.
/// Improved fallback placement: prefers physics grounding (raycast) and NavMesh.SamplePosition
/// and forces CharacterController settling so the player isn't left floating or able to walk through geometry.
/// </summary>
public static class PlayerPositionHelpers
{
    public static bool MovePlayerByX5(bool safeTeleportAfter = false) => MovePlayerBy(new Vector3(5f, 0f, 0f), safeTeleportAfter);
    public static bool MovePlayerByY5(bool safeTeleportAfter = false) => MovePlayerBy(new Vector3(0f, 5f, 0f), safeTeleportAfter);
    public static bool MovePlayerByZ5(bool safeTeleportAfter = false) => MovePlayerBy(new Vector3(0f, 0f, 5f), safeTeleportAfter);

    // internal core
    public static bool MovePlayerBy(Vector3 delta, bool safeTeleportAfter)
    {
        try
        {
            var found = TryGetPlayerTransform();
            if (found == null)
            {
                TBLog.Warn($"PlayerPositionHelpers: could not find player transform to move by {delta}.");
                return false;
            }

            var playerRoot = ResolvePlayerRoot(found) ?? found;

            Vector3 before = playerRoot.position;
            Vector3 after = before + delta;

            TBLog.Info($"PlayerPositionHelpers: moving player root '{playerRoot.name}' by {delta} (before={before}, after={after})");

            playerRoot.position = after;

            TBLog.Info($"PlayerPositionHelpers: move applied. currentPos={playerRoot.position}");

            if (!safeTeleportAfter) return true;

            try
            {
                var tm = TeleportManager.Instance;
                if (tm != null)
                {
                    TBLog.Info("PlayerPositionHelpers: invoking TeleportManager.PlacePlayerUsingSafeRoutine (via Instance).");
                    tm.StartCoroutine(tm.PlacePlayerUsingSafeRoutine(after, moved =>
                    {
                        try { TBLog.Info($"PlayerPositionHelpers: TeleportManager.PlacePlayerUsingSafeRoutine completed moved={moved}"); } catch { }
                    }));
                    return true;
                }

                var tmObj = UnityEngine.Object.FindObjectOfType<TeleportManager>();
                if (tmObj != null)
                {
                    TBLog.Info("PlayerPositionHelpers: invoking TeleportManager.PlacePlayerUsingSafeRoutine (via FindObjectOfType fallback).");
                    tmObj.StartCoroutine(tmObj.PlacePlayerUsingSafeRoutine(after, moved =>
                    {
                        try { TBLog.Info($"PlayerPositionHelpers: TeleportManager (found) PlacePlayerUsingSafeRoutine completed moved={moved}"); } catch { }
                    }));
                    return true;
                }

                var host = TeleportHelpersBehaviour.GetOrCreateHost();
                if (host != null)
                {
                    TBLog.Info("PlayerPositionHelpers: TeleportManager not found; starting internal fallback safe-placement on host targeting player root.");
                    host.StartCoroutine(FallbackSafePlaceCoroutine(playerRoot, after));
                    return true;
                }

                TBLog.Info("PlayerPositionHelpers: TeleportManager.Instance not found; skipping safe-teleport attempt.");
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlayerPositionHelpers: failed to start safe-teleport coroutine: " + ex.ToString());
            }

            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers: exception while moving player: " + ex.ToString());
            return false;
        }
    }

    // Add this method to your existing PlayerPositionHelpers class (or replace MovePlayerBy usage where needed).
    public static bool MovePlayerTo(Vector3 target, bool safeTeleportAfter)
    {
        try
        {
            var found = TryGetPlayerTransform();
            if (found == null)
            {
                TBLog.Warn($"PlayerPositionHelpers: could not find player transform to move to {target}.");
                return false;
            }

            var playerRoot = ResolvePlayerRoot(found) ?? found;

            Vector3 before = playerRoot.position;
            Vector3 after = target;

            TBLog.Info($"PlayerPositionHelpers: moving player root '{playerRoot.name}' to {after} (before={before})");

            try
            {
                playerRoot.position = after;
                TBLog.Info($"PlayerPositionHelpers: move applied. currentPos={playerRoot.position}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlayerPositionHelpers: setting player position threw: " + ex.ToString());
                return false;
            }

            if (!safeTeleportAfter) return true;

            try
            {
                var tm = TeleportManager.Instance;
                if (tm != null)
                {
                    TBLog.Info("PlayerPositionHelpers: invoking TeleportManager.PlacePlayerUsingSafeRoutine (via Instance).");
                    tm.StartCoroutine(tm.PlacePlayerUsingSafeRoutine(after, moved =>
                    {
                        try { TBLog.Info($"PlayerPositionHelpers: TeleportManager.PlacePlayerUsingSafeRoutine completed moved={moved}"); } catch { }
                    }));
                    return true;
                }

                var tmObj = UnityEngine.Object.FindObjectOfType<TeleportManager>();
                if (tmObj != null)
                {
                    TBLog.Info("PlayerPositionHelpers: invoking TeleportManager.PlacePlayerUsingSafeRoutine (via FindObjectOfType fallback).");
                    tmObj.StartCoroutine(tmObj.PlacePlayerUsingSafeRoutine(after, moved =>
                    {
                        try { TBLog.Info($"PlayerPositionHelpers: TeleportManager (found) PlacePlayerUsingSafeRoutine completed moved={moved}"); } catch { }
                    }));
                    return true;
                }

                var host = TeleportHelpersBehaviour.GetOrCreateHost();
                if (host != null)
                {
                    TBLog.Info("PlayerPositionHelpers: TeleportManager not found; starting internal fallback safe-placement on host targeting player root.");
                    host.StartCoroutine(FallbackSafePlaceCoroutine(playerRoot, after));
                    return true;
                }

                TBLog.Info("PlayerPositionHelpers: TeleportManager.Instance not found; skipping safe-teleport attempt.");
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlayerPositionHelpers: failed to start safe-teleport coroutine: " + ex.ToString());
            }

            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers: exception while moving player: " + ex.ToString());
            return false;
        }
    }

    // Attempt to find the most likely transform representing the player.
    private static Transform TryGetPlayerTransform()
    {
        try
        {
            try
            {
                var cc = UnityEngine.Object.FindObjectOfType<CharacterController>();
                if (cc != null)
                {
                    TBLog.Info($"PlayerPositionHelpers: found player by CharacterController on '{cc.gameObject.name}'");
                    return cc.transform;
                }
            }
            catch (Exception exCc)
            {
                TBLog.Warn("PlayerPositionHelpers: FindObjectOfType<CharacterController>() threw: " + exCc);
            }

            try
            {
                var go = GameObject.FindWithTag("Player");
                if (go != null)
                {
                    TBLog.Info($"PlayerPositionHelpers: found player by tag 'Player' -> '{go.name}'");
                    return go.transform;
                }
            }
            catch (Exception exTag)
            {
                TBLog.Warn("PlayerPositionHelpers: GameObject.FindWithTag('Player') threw: " + exTag);
            }

            try
            {
                Transform fallbackCam = null;
                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (g == null || string.IsNullOrEmpty(g.name)) continue;
                    if (!g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)) continue;

                    if (g.name.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0
                        || g.GetComponent<Camera>() != null)
                    {
                        if (fallbackCam == null) fallbackCam = g.transform;
                        continue;
                    }

                    var ccChild = g.GetComponentInChildren<CharacterController>(true);
                    if (ccChild != null)
                    {
                        TBLog.Info($"PlayerPositionHelpers: found player heuristic by PlayerChar name + CharacterController child on '{g.name}'");
                        return ccChild.transform;
                    }

                    var rbChild = g.GetComponentInChildren<Rigidbody>(true);
                    if (rbChild != null)
                    {
                        TBLog.Info($"PlayerPositionHelpers: found player heuristic by PlayerChar name + Rigidbody child on '{g.name}'");
                        return g.transform;
                    }

                    TBLog.Info($"PlayerPositionHelpers: choosing PlayerChar candidate '{g.name}' (no Camera present)");
                    return g.transform;
                }

                if (fallbackCam != null)
                {
                    TBLog.Info($"PlayerPositionHelpers: only camera-like PlayerChar found '{fallbackCam.name}' - returning as last-resort");
                    return fallbackCam;
                }
            }
            catch (Exception exScan)
            {
                TBLog.Warn("PlayerPositionHelpers: heuristic scan threw: " + exScan);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers: error locating player transform: " + ex.ToString());
        }

        return null;
    }

    private static Transform ResolvePlayerRoot(Transform t)
    {
        if (t == null) return null;

        try
        {
            if (t.GetComponent<CharacterController>() != null || t.GetComponent<Rigidbody>() != null)
                return t;

            var ccChild = t.GetComponentInChildren<CharacterController>(true);
            if (ccChild != null)
                return ccChild.transform;

            var rbChild = t.GetComponentInChildren<Rigidbody>(true);
            if (rbChild != null)
                return rbChild.transform;

            Transform cur = t;
            int hops = 0;
            while (cur != null && hops++ < 8)
            {
                if (cur.GetComponent<CharacterController>() != null || cur.GetComponent<Rigidbody>() != null)
                    return cur;
                cur = cur.parent;
            }

            if (t.name.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0 || t.GetComponent<Camera>() != null)
            {
                var cc = UnityEngine.Object.FindObjectOfType<CharacterController>();
                if (cc != null) return cc.transform;

                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (g == null || string.IsNullOrEmpty(g.name)) continue;
                    if (!g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase)) continue;
                    if (g.name.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    return g.transform;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.ResolvePlayerRoot threw: " + ex.ToString());
        }

        return t;
    }

    // Robust fallback coroutine with forced CharacterController settling to avoid leaving the player "floating" or walking through geometry.
    private static IEnumerator FallbackSafePlaceCoroutine(Transform playerTransform, Vector3 requestedTarget)
    {
        TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: enter requestedTarget={requestedTarget}, playerTransform={(playerTransform != null ? playerTransform.name : "<null>")}");
        if (playerTransform == null)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: playerTransform null; aborting.");
            yield break;
        }

        CharacterController cc = null;
        Rigidbody rb = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;

        try
        {
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
            var rbcands = playerTransform.GetComponentsInChildren<Rigidbody>(true);
            if (rbcands != null && rbcands.Length > 0) rb = rbcands[0];

            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;

            TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: cc={(cc != null ? "YES" : "NO")} ccWasEnabled={ccWasEnabled}, rb={(rb != null ? "YES" : "NO")} rbWasKinematic={rbWasKinematic}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: error reading components: " + ex.ToString());
        }

        // disable motion/physics while we reposition
        try
        {
            if (cc != null) cc.enabled = false;
            if (rb != null)
            {
                rb.isKinematic = true; // force kinematic during reposition to avoid conflicts
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: error disabling components: " + ex.ToString());
        }

        Vector3 finalPos = requestedTarget;
        bool groundedViaRaycast = false;
        bool groundedViaNavmesh = false;

        // 1) Try raycast down from above the requested target to find ground beneath
        try
        {
            const float RAYCAST_UP_OFFSET = 4.0f;   // how far above target we start ray
            const float RAYCAST_MAX_DOWN = 20.0f;   // max distance to look down
            Vector3 rayStart = requestedTarget + Vector3.up * RAYCAST_UP_OFFSET;
            RaycastHit hit;
            if (Physics.Raycast(rayStart, Vector3.down, out hit, RAYCAST_UP_OFFSET + RAYCAST_MAX_DOWN, ~0, QueryTriggerInteraction.Ignore))
            {
                // Use hit.point as ground; add small offset to avoid interpenetration
                finalPos = new Vector3(requestedTarget.x, hit.point.y + 0.1f, requestedTarget.z);
                groundedViaRaycast = true;
                TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: grounded by raycast to {hit.point} => finalPos={finalPos} (collider='{hit.collider?.name}')");
            }
            else
            {
                TBLog.Info("PlayerPositionHelpers.FallbackSafePlaceCoroutine: raycast down found no ground under requested target.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: raycast grounding threw: " + ex.ToString());
        }

        // 2) If no raycast ground, try NavMesh.SamplePosition near requestedTarget
        if (!groundedViaRaycast)
        {
            try
            {
                const float NAV_SAMPLE_RADIUS = 6.0f;
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(requestedTarget, out navHit, NAV_SAMPLE_RADIUS, NavMesh.AllAreas))
                {
                    finalPos = navHit.position;
                    groundedViaNavmesh = true;
                    TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: snapped to NavMesh at {navHit.position} (distance={navHit.distance})");
                }
                else
                {
                    TBLog.Info("PlayerPositionHelpers.FallbackSafePlaceCoroutine: NavMesh.SamplePosition failed to find nearby nav position.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: NavMesh.SamplePosition threw: " + ex.ToString());
            }
        }

        // 3) If neither raycast nor navmesh found anything, fall back to previous overlap/raise approach
        if (!groundedViaRaycast && !groundedViaNavmesh)
        {
            try
            {
                TBLog.Info("PlayerPositionHelpers.FallbackSafePlaceCoroutine: attempting overlap/raise fallback to avoid intersections.");
                const float OVERLAP_RAISE_STEP = 0.25f;
                const float OVERLAP_MAX_RAISE = 2.0f;
                const float OVERLAP_CHECK_RADIUS = 0.5f;

                // start at requestedTarget and raise if overlapping
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
                            if (h.transform.IsChildOf(playerTransform)) continue;
                            overlapping = true;
                            break;
                        }
                    }

                    TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: overlap step {i}, hits={(hits?.Length ?? 0)}, overlapping={overlapping}, checkPos={checkPos}");

                    if (!overlapping)
                    {
                        finalPos = checkPos;
                        fixedUp = true;
                        break;
                    }
                }

                if (!fixedUp)
                {
                    TBLog.Warn($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: could not find non-overlapping spot within fallback raise range; using requestedTarget {requestedTarget}");
                    finalPos = requestedTarget;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: overlap fallback threw: " + ex.ToString());
                finalPos = requestedTarget;
            }
        }

        // 4) Apply the final position. Do not yield inside this try/catch (C# iterator restriction).
        bool appliedPosition = false;
        try
        {
            TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: applying finalPos={finalPos}");
            playerTransform.position = finalPos;
            appliedPosition = true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: failed to set position: " + ex.ToString());
        }

        // 5) Wait a few physics frames AFTER the try/catch (yields cannot be inside a try with catch).
        if (appliedPosition)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return null; // allow Update to run once as well
        }
        else
        {
            // still yield one frame so caller's coroutine scheduling remains sane
            yield return null;
        }

        // 6) Force transforms to sync and force CC settling to ensure collisions are active.
        try
        {
            Physics.SyncTransforms();
        }
        catch (Exception exSync)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: Physics.SyncTransforms threw: " + exSync);
        }

        // Force-enable CharacterController so collision probes run (this fixes the 'walking through terrain' symptom).
        bool ccForced = false;
        try
        {
            if (cc != null)
            {
                if (!cc.enabled)
                {
                    cc.enabled = true;
                    ccForced = true;
                    TBLog.Info("PlayerPositionHelpers.FallbackSafePlaceCoroutine: CharacterController force-enabled for settling.");
                }
                else
                {
                    TBLog.Info("PlayerPositionHelpers.FallbackSafePlaceCoroutine: CharacterController enabled=True");
                }
            }

            // Keep Rigidbody kinematic while CC probes run; clear velocities.
            if (rb != null)
            {
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
                rb.isKinematic = true;
            }
        }
        catch (Exception exRestoreStart)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: error during CC/RB initial restore: " + exRestoreStart);
        }

        // Nudge the CharacterController downward slightly to trigger grounding collision probes.
        if (cc != null && cc.enabled)
        {
            try
            {
                cc.Move(Vector3.down * 0.25f);
            }
            catch (Exception exNudge)
            {
                TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: CharacterController nudge threw: " + exNudge);
            }

            // Wait one physics tick for the CharacterController/physics to settle.
            yield return new WaitForFixedUpdate();
            yield return null;
        }

        // After CC has settled, ensure Rigidbody is consistent: keep kinematic if it was originally kinematic,
        // otherwise restore dynamic mode and clear velocities.
        try
        {
            if (rb != null)
            {
                try
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = rbWasKinematic;
                }
                catch { }
            }

            // If we forced CC on but it was originally disabled, keep it enabled because many game systems expect CC active.
            TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: restored components; final pos={playerTransform.position} ccEnabled={(cc != null ? cc.enabled.ToString() : "<none>")} rbIsKinematic={(rb != null ? rb.isKinematic.ToString() : "<none>")}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: restore failed: " + ex.ToString());
        }

        yield break;
    }
}