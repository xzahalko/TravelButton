using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;


/// <summary>
/// Static helper class for teleport-related utilities.
/// This class provides methods used by legacy teleport code.
/// </summary>
public static class TeleportHelpers
{
    // Flag to track if re-enable coroutine is in progress
    public static bool ReenableInProgress = false;
    
    // Flag to track if teleport is in progress
    public static bool TeleportInProgress = false;
    
    // Clearance amount for grounding
    public static float TeleportGroundClearance = 0.5f;

    private const float OVERLAP_CHECK_RADIUS = 0.45f;
    private const float OVERLAP_RAISE_STEP = 0.25f;
    private const float OVERLAP_MAX_RAISE = 3.0f;
    private const float GROUNDED_RAY_MAX = 400f;
    private const float MOVED_EPSILON = 0.05f;

    // Coroutine that attempts a robust safe teleport to `target`. When finished it invokes resultCallback(true/false).
    // Usage: yield return host.StartCoroutine(TeleportHelpers.AttemptTeleportToPositionSafe(target, moved => { /* ... */ }));
    public static IEnumerator AttemptTeleportToPositionSafe(Vector3 target, Action<bool> resultCallback)
    {
        bool moved = false;
        Transform playerTransform = null;
        try
        {
            playerTransform = FindPlayerTransform();
            if (playerTransform == null)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: could not find player transform.");
                resultCallback?.Invoke(false);
                yield break;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: FindPlayerTransform threw: " + ex);
            resultCallback?.Invoke(false);
            yield break;
        }

        Vector3 initialPos = playerTransform.position;
        TBLog.Info($"AttemptTeleportToPositionSafe: requested target={target}, initialPlayerPos={initialPos}");
        try { TBLog.Info($"[TeleportHelpers] initial player position: {initialPos}"); } catch { }

        // Remember component states
        CharacterController cc = null;
        Rigidbody rb = null;
        NavMeshAgent agent = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;
        bool agentWasEnabled = false;
        bool agentWasOnNavMesh = false;

        try
        {
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
            rb = playerTransform.GetComponentInChildren<Rigidbody>(true);
            agent = playerTransform.GetComponentInChildren<NavMeshAgent>(true);

            ccWasEnabled = cc != null ? cc.enabled : false;
            if (rb != null) rbWasKinematic = rb.isKinematic;
            agentWasEnabled = agent != null ? agent.enabled : false;
            if (agent != null)
            {
                try { agentWasOnNavMesh = agent.isOnNavMesh; } catch { agentWasOnNavMesh = false; }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: error reading components: " + ex);
        }

        // Disable interfering components
        try
        {
            if (cc != null) cc.enabled = false;
            if (agent != null) agent.enabled = false;
            if (rb != null)
            {
                rb.isKinematic = true;
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: disabling components failed: " + ex);
        }

        // If NavMeshAgent is present and on navmesh, try Warp first (preferred)
        bool warpTried = false;
        bool warpSucceeded = false;
        if (agent != null && agentWasOnNavMesh)
        {
            try
            {
                warpTried = true;
                TBLog.Info("AttemptTeleportToPositionSafe: NavMeshAgent present; attempting Warp.");
                agent.enabled = true; // enable temporarily to call Warp reliably
                agent.Warp(target);
            }
            catch (Exception ex)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: Warp attempt threw: " + ex);
                warpSucceeded = false;
            }
            finally
            {
                try { agent.enabled = agentWasEnabled; } catch { }
            }

            // yield outside try/catch (allowed)
            yield return null;

            try
            {
                Vector3 afterWarp = playerTransform.position;
                float dist = Vector3.Distance(afterWarp, target);
                warpSucceeded = dist <= 1.0f || (agent != null && agent.isOnNavMesh);
                TBLog.Info($"AttemptTeleportToPositionSafe: Warp completed; player at {afterWarp}; distToTarget={dist:F3}; isOnNavMesh={(agent != null ? agent.isOnNavMesh : false)}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: Warp post-check threw: " + ex);
                warpSucceeded = false;
            }

            if (warpSucceeded)
            {
                moved = Vector3.Distance(initialPos, playerTransform.position) > MOVED_EPSILON;
                try
                {
                    if (rb != null)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.isKinematic = rbWasKinematic;
                    }
                    if (cc != null) cc.enabled = ccWasEnabled;
                }
                catch { }
                TBLog.Info($"AttemptTeleportToPositionSafe: warp succeeded; moved={moved}");
                resultCallback?.Invoke(moved);
                yield break;
            }
        }

        // Transform-based placement (grounding + overlap/raise)
        Vector3 probeTarget = target;
        try
        {
            // Prefer NavMesh if available near the target
            NavMeshHit navHit = new NavMeshHit(); // initialize to avoid CS0165
            bool navFound = false;
            try
            {
                // small radius sample first (5m); adjust if needed
                navFound = NavMesh.SamplePosition(target, out navHit, 5.0f, NavMesh.AllAreas);
            }
            catch (Exception exNav)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: NavMesh.SamplePosition threw: " + exNav);
                navFound = false;
            }

            if (navFound)
            {
                TBLog.Info($"AttemptTeleportToPositionSafe: NavMesh.SamplePosition found nearest nav at {navHit.position} (dist={Vector3.Distance(navHit.position, target):F2})");
                probeTarget = new Vector3(navHit.position.x, navHit.position.y, navHit.position.z);
            }
            else
            {
                // Detailed raycast probe from high above the target straight down.
                RaycastHit[] hits;
                Vector3 rayStart = new Vector3(target.x, target.y + 200f, target.z);
                try
                {
                    hits = Physics.RaycastAll(rayStart, Vector3.down, 400f, ~0, QueryTriggerInteraction.Ignore);
                }
                catch (Exception exRay)
                {
                    TBLog.Warn("AttemptTeleportToPositionSafe: RaycastAll threw: " + exRay);
                    hits = null;
                }

                if (hits != null && hits.Length > 0)
                {
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    var hit = hits[0];
                    string colliderInfo = hit.collider != null ? $"{hit.collider.gameObject.name} (layer={hit.collider.gameObject.layer})" : "null";
                    TBLog.Info($"AttemptTeleportToPositionSafe: RaycastAll hit closest collider '{colliderInfo}' at point {hit.point} normal={hit.normal}");
                    probeTarget = new Vector3(target.x, hit.point.y, target.z);

                    // Extra debug for other hits
                    for (int i = 0; i < hits.Length; i++)
                    {
                        var h = hits[i];
                        TBLog.Info($"AttemptTeleportToPositionSafe: RaycastAll [{i}] collider={(h.collider != null ? h.collider.gameObject.name : "null")} point={h.point} dist={h.distance:F2}");
                    }
                }
                else
                {
                    TBLog.Info($"AttemptTeleportToPositionSafe: RaycastAll did not hit any collider below {target} (rayStart={rayStart}).");
                }
            }

            // Plausibility check
            float yDiff = Math.Abs(probeTarget.y - target.y);
            if (yDiff > 50f)
            {
                TBLog.Warn($"AttemptTeleportToPositionSafe: ground Y {probeTarget.y:F2} differs from requested Y {target.y:F2} by {yDiff:F2}m — suspicious. Consider inspecting collider logs or clamping.");
            }
            else
            {
                TBLog.Info($"AttemptTeleportToPositionSafe: ground probe chose y={probeTarget.y:F2} (diff {yDiff:F2}m)");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: ground probe threw: " + ex);
        }

        // set position and yield outside of the try/catch
        try
        {
            playerTransform.position = probeTarget;
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: setting position threw: " + ex);
        }

        // yield outside try/catch
        yield return null;

        // Overlap check and iterative raise to avoid embedding
        try
        {
            bool foundFree = false;
            int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
            for (int step = 0; step <= maxSteps; step++)
            {
                Vector3 checkPos = playerTransform.position + Vector3.up * (step * OVERLAP_RAISE_STEP);
                Vector3 overlapCenter = checkPos + Vector3.up * 0.5f;
                Collider[] hits = Physics.OverlapSphere(overlapCenter, OVERLAP_CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);

                bool overlapping = false;
                if (hits != null && hits.Length > 0)
                {
                    foreach (var h in hits)
                    {
                        if (h == null || h.transform == null) continue;
                        if (h.transform.IsChildOf(playerTransform)) continue; // ignore self collisions
                        overlapping = true;
                        break;
                    }
                }

                if (!overlapping)
                {
                    if (step > 0) playerTransform.position = checkPos;
                    foundFree = true;
                    if (step > 0) TBLog.Info($"AttemptTeleportToPositionSafe: raised by {step * OVERLAP_RAISE_STEP:F2}m to avoid overlap -> {playerTransform.position}");
                    break;
                }
            }

            if (!foundFree)
            {
                TBLog.Warn($"AttemptTeleportToPositionSafe: could not find non-overlapping spot within {OVERLAP_MAX_RAISE}m above target; leaving at {playerTransform.position}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: overlap/raise pass threw: " + ex);
        }

        // Ensure velocities zeroed and small settle frames
        try
        {
            if (rb != null)
            {
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
        }
        catch { }

        // wait outside of try/catch
        yield return null;
        yield return null;

        Vector3 finalPos = Vector3.zero;
        try
        {
            finalPos = playerTransform.position;
            moved = Vector3.Distance(initialPos, finalPos) > MOVED_EPSILON;
            TBLog.Info($"AttemptTeleportToPositionSafe: placement complete. initial={initialPos}, final={finalPos}, moved={moved}");
            try { TBLog.Info($"[TeleportHelpers] final player position: {finalPos}"); } catch { }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: error measuring final position: " + ex);
            moved = false;
        }

        // Restore components
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
            if (agent != null) agent.enabled = agentWasEnabled;
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: restoring components failed: " + ex);
        }

        resultCallback?.Invoke(moved);
        yield break;
    }

    // Heuristic find for authoritative player transform (tag, name heuristics, CharacterController)
    public static Transform FindPlayerTransform()
    {
        try
        {
            try
            {
                var go = GameObject.FindWithTag("Player");
                if (go != null && go.transform != null) return go.transform;
            }
            catch { /* ignore missing tag */ }

            // Name heuristics
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in allTransforms)
            {
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                var n = t.name;
                if (n.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) || n.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return t;
                }
            }

            // CharacterController fallback
            var controllers = UnityEngine.Object.FindObjectsOfType<CharacterController>();
            foreach (var c in controllers)
            {
                if (c != null && c.transform != null) return c.transform;
            }
        }
        catch { /* swallow */ }
        return null;
    }

    /// <summary>
    /// Find the player root GameObject using various heuristics.
    /// </summary>
    public static GameObject FindPlayerRoot()
    {
        try
        {
            // 1) Try common runtime player component types (Assembly-CSharp)
            string[] typeNames = new string[]
            {
                "PlayerCharacter",
                "PlayerEntity",
                "LocalPlayer",
                "PlayerController",
                "Character",
                "PC_Player"
            };

            foreach (var tn in typeNames)
            {
                try
                {
                    var t = ReflectionUtils.SafeGetType(tn + ", Assembly-CSharp") ?? ReflectionUtils.SafeGetType(tn);
                    if (t == null) continue;
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0)
                    {
                        var comp = objs[0] as Component;
                        if (comp != null)
                            return comp.gameObject.transform.root.gameObject;
                    }
                }
                catch { /* ignore type lookup errors */ }
            }

            // 2) Try object tagged "Player"
            try
            {
                var byTag = GameObject.FindWithTag("Player");
                if (byTag != null) return byTag.transform.root.gameObject;
            }
            catch { /* ignore */ }

            // 3) Heuristic: search active scene root objects for names containing "Player" or "PlayerChar"
            try
            {
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (activeScene.IsValid())
                {
                    var roots = activeScene.GetRootGameObjects();
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        var rn = r.name ?? "";
                        if (rn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            rn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            rn.IndexOf("PC_", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return r;
                        }

                        // deeper children
                        var transforms = r.GetComponentsInChildren<Transform>(true);
                        if (transforms != null)
                        {
                            for (int i = 0; i < transforms.Length; i++)
                            {
                                var t = transforms[i];
                                if (t == null) continue;
                                var tn = t.name ?? "";
                                if (tn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return t.root.gameObject;
                                }
                            }
                        }
                    }
                }

                // 4) Global fallback: check all loaded Transforms (expensive)
                var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
                foreach (var t in allTransforms)
                {
                    if (t == null) continue;
                    var tn = t.name ?? "";
                    if (tn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return t.root.gameObject;
                    }
                }
            }
            catch { /* ignore */ }

            // 5) Last resort: Camera.main's root
            try
            {
                if (Camera.main != null) return Camera.main.transform.root.gameObject;
            }
            catch { /* ignore */ }
        }
        catch { /* swallow */ }

        return null;
    }

    /// <summary>
    /// Resolve the actual player GameObject from a candidate root.
    /// </summary>
    public static GameObject ResolveActualPlayerGameObject(GameObject candidate)
    {
        if (candidate == null) return null;
        
        try
        {
            // If the candidate has "PlayerChar" in its name, use it directly
            if (candidate.name != null && candidate.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                return candidate;
            
            // Otherwise try to find Character component or similar
            var charType = ReflectionUtils.SafeGetType("Character, Assembly-CSharp") ?? ReflectionUtils.SafeGetType("Character");
            if (charType != null)
            {
                var comp = candidate.GetComponent(charType);
                if (comp != null) return candidate;
                
                // Check children
                var childComps = candidate.GetComponentsInChildren(charType, true);
                if (childComps != null && childComps.Length > 0)
                {
                    var c = childComps[0] as Component;
                    if (c != null) return c.gameObject;
                }
            }
            
            return candidate;
        }
        catch
        {
            return candidate;
        }
    }

    /// <summary>
    /// Get a grounded position using raycast or fallback.
    /// </summary>
    public static Vector3 GetGroundedPosition(Vector3 position)
    {
        try
        {
            const float maxRayUp = 150f;
            const float maxRayDown = 400f;
            Vector3 origin = position + Vector3.up * maxRayUp;
            
            RaycastHit hit;
            if (Physics.Raycast(origin, Vector3.down, out hit, maxRayUp + maxRayDown, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point + Vector3.up * TeleportGroundClearance;
            }
            
            // Fallback: return original position
            return position;
        }
        catch
        {
            return position;
        }
    }

    /// <summary>
    /// Ensure clearance above ground.
    /// </summary>
    public static Vector3 EnsureClearance(Vector3 position)
    {
        try
        {
            // Try to raycast down to find ground and add clearance
            RaycastHit hit;
            Vector3 checkOrigin = position + Vector3.up * 5f;
            if (Physics.Raycast(checkOrigin, Vector3.down, out hit, 100f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point + Vector3.up * TeleportGroundClearance;
            }
            
            return position;
        }
        catch
        {
            return position;
        }
    }

    /// <summary>
    /// Try to pick the best coordinates permutation.
    /// </summary>
    public static bool TryPickBestCoordsPermutation(Vector3 target, out Vector3 result)
    {
        result = target;
        
        try
        {
            // Try small perturbations around the target
            float[] offsets = new float[] { 0f, 0.5f, 1.0f, 2.0f, -0.5f, -1.0f, -2.0f };
            
            foreach (var xOff in offsets)
            {
                foreach (var zOff in offsets)
                {
                    Vector3 candidate = new Vector3(target.x + xOff, target.y, target.z + zOff);
                    
                    // Try to ground this candidate
                    Vector3 grounded = GetGroundedPosition(candidate);
                    
                    // Check if it's not too far vertically
                    if (Mathf.Abs(grounded.y - target.y) < 50f)
                    {
                        result = grounded;
                        return true;
                    }
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void StopNearbyFXAroundPlayer(float radiusMeters = 20f)
    {
        try
        {
            var player = GameObject.FindWithTag("Player")
                         ?? Array.Find(UnityEngine.Object.FindObjectsOfType<GameObject>(), g => g.name != null && g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase));
            if (player == null)
            {
                TBLog.Warn("StopNearbyFXAroundPlayer: player not found.");
                return;
            }

            Vector3 ppos = player.transform.position;
            int attempted = 0;

            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (go == null) continue;
                try
                {
                    if (Vector3.Distance(ppos, go.transform.position) > radiusMeters) continue;

                    bool hasParticleLike = false;
                    foreach (var comp in go.GetComponents(typeof(Component)))
                    {
                        if (comp == null) continue;
                        var tn = comp.GetType().Name;
                        if (tn.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                            || tn.IndexOf("VFX", StringComparison.OrdinalIgnoreCase) >= 0
                            || tn.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasParticleLike = true;
                            break;
                        }
                    }

                    if (!hasParticleLike) continue;

                    // Stop / Clear particle-like comps under this root
                    foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                    {
                        foreach (var comp in tr.gameObject.GetComponents(typeof(Component)))
                        {
                            if (comp == null) continue;
                            var tn = comp.GetType().Name;
                            if (tn.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("VFX", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { var mClear = comp.GetType().GetMethod("Clear", new Type[] { typeof(bool) }); if (mClear != null) mClear.Invoke(comp, new object[] { true }); } catch { }
                                try
                                {
                                    var mSim = comp.GetType().GetMethod("Simulate", new Type[] { typeof(float), typeof(bool), typeof(bool) })
                                               ?? comp.GetType().GetMethod("Simulate", new Type[] { typeof(float), typeof(bool) });
                                    if (mSim != null)
                                    {
                                        var pcount = mSim.GetParameters().Length;
                                        if (pcount == 3) mSim.Invoke(comp, new object[] { 0f, true, true });
                                        else mSim.Invoke(comp, new object[] { 0f, true });
                                    }
                                }
                                catch { }
                                try { var mStop = comp.GetType().GetMethod("Stop", new Type[] { typeof(bool) }) ?? comp.GetType().GetMethod("Stop"); if (mStop != null) { if (mStop.GetParameters().Length == 1) mStop.Invoke(comp, new object[] { true }); else mStop.Invoke(comp, null); } } catch { }
                            }
                        }
                    }
                    attempted++;
                }
                catch { /* ignore individual GO errors */ }
            }

            TBLog.Info($"StopNearbyFXAroundPlayer: attempted to clear particle-like comps on {attempted} roots within {radiusMeters}m.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("StopNearbyFXAroundPlayer: exception: " + ex.Message);
        }
    }
}
