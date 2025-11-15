using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class TeleportHelpers
{
    // Robust player-finder that prefers the actual player character GameObject.
    public static GameObject FindPlayerRoot()
    {
        try
        {
            // 1) Quick: look for active transforms with names starting with "PlayerChar"
            try
            {
                var activeTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var tr in activeTransforms)
                {
                    if (tr == null) continue;
                    string n = tr.name ?? "";
                    if (n.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                    {
                        return tr.root.gameObject;
                    }
                }
            }
            catch { }

            // 2) Try known runtime player component types (Assembly-CSharp)
            string[] typeNames = new string[] { "PlayerCharacter", "PlayerEntity", "LocalPlayer", "PlayerController", "Character", "PC_Player" };
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
                        {
                            return comp.gameObject.transform.root.gameObject;
                        }
                    }
                }
                catch { }
            }

            // 3) Tag "Player"
            try
            {
                var byTag = GameObject.FindWithTag("Player");
                if (byTag != null) return byTag.transform.root.gameObject;
            }
            catch { }

            // 4) Scene roots / heuristics (search root objects for a child named PlayerChar)
            try
            {
                var activeScene = SceneManager.GetActiveScene();
                if (activeScene.IsValid())
                {
                    var roots = activeScene.GetRootGameObjects();
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        var transforms = r.GetComponentsInChildren<Transform>(true);
                        foreach (var t in transforms)
                        {
                            if (t == null) continue;
                            if ((t.name ?? "").StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) ||
                                (t.name ?? "").IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return t.root.gameObject;
                            }
                        }
                    }
                }
            }
            catch { }

            // 5) Fallback to camera root
            try
            {
                if (Camera.main != null) return Camera.main.transform.root.gameObject;
            }
            catch { }
        }
        catch { }

        return null;
    }

    // Find the actual GameObject for the "player character" (a more precise selection).
    // If the given root is a PlayerHouse (or other container), this tries to find the PlayerChar descendant.
    // IMPORTANT: return the actual child GameObject that corresponds to the player character (not the scene root),
    // so that teleport operations move the correct object.
    // Replace existing ResolveActualPlayerGameObject implementation with this version
    public static GameObject ResolveActualPlayerGameObject(GameObject root)
    {
        if (root == null) return null;

        // 1) If the root itself already looks like the player, return it.
        var rn = root.name ?? "";
        if (rn.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) ||
            rn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return root;
        }

        try
        {
            // 2) Search descendants: prefer a GameObject named 'PlayerChar...' or one that has CharacterController,
            //    PlayerCharacter or PlayerEntity components. Return the actual child GameObject (not root).
            var childs = root.GetComponentsInChildren<Transform>(true);
            GameObject fallbackByComponent = null;
            foreach (var t in childs)
            {
                if (t == null) continue;
                var name = t.name ?? "";

                // strong preference: explicit PlayerChar named object
                if (name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return t.gameObject;
                }

                // if CharacterController attached to this transform, prefer this
                try
                {
                    if (t.GetComponent<CharacterController>() != null)
                        return t.gameObject;
                }
                catch { }

                // reflection-based fallback: PlayerCharacter/PlayerEntity
                try
                {
                    if (t.GetComponent("PlayerCharacter") != null || t.GetComponent("PlayerEntity") != null)
                        fallbackByComponent = t.gameObject;
                }
                catch { }
            }

            if (fallbackByComponent != null) return fallbackByComponent;
        }
        catch { /* ignore */ }

        // 3) If nothing better, return the original root
        return root;
    }

    // --- helper: zkusí opravit bìžné prohození souøadnic (Y<->Z) pomocí permutací a probe ---
    public static Vector3 TryAutoFixCoordsPermutations(Vector3 original)
    {
        try
        {
            const float minAllowedY = -200f;
            const float maxAllowedY = 500f;

            var candidates = new List<Vector3>
        {
            original,
            new Vector3(original.x, original.z, original.y) // swap Y <-> Z
        };

            bool anyValid = false;
            Vector3 bestSafe = original;
            float bestScore = float.MaxValue;

            foreach (var cand in candidates)
            {
                try
                {
                    if (TryFindNearestNavMeshOrGround(cand, out Vector3 safe, navSearchRadius: 15f, maxGroundRay: 400f))
                    {
                        float score = Mathf.Abs(safe.y - cand.y);
                        bool withinWorld = safe.y >= minAllowedY && safe.y <= maxAllowedY;

                        TravelButtonPlugin.LogInfo($"TryAutoFixCoordsPermutations: perm candidate {cand} -> safe {safe} (score={score:F3}, withinWorld={withinWorld})");

                        if (withinWorld)
                        {
                            if (!anyValid || score < bestScore)
                            {
                                anyValid = true;
                                bestScore = score;
                                bestSafe = safe;
                            }
                        }
                        else if (!anyValid)
                        {
                            if (score < bestScore)
                            {
                                bestScore = score;
                                bestSafe = safe;
                            }
                        }
                    }
                    else
                    {
                        TravelButtonPlugin.LogInfo($"TryAutoFixCoordsPermutations: perm candidate {cand} -> TryFindNearestNavMeshOrGround returned false");
                    }
                }
                catch (Exception exCand)
                {
                    TravelButtonPlugin.LogWarning("TryAutoFixCoordsPermutations: probe exception: " + exCand.Message);
                }
            }

            if (!Mathf.Approximately(bestSafe.x, original.x) || !Mathf.Approximately(bestSafe.y, original.y) || !Mathf.Approximately(bestSafe.z, original.z))
            {
                TravelButtonPlugin.LogInfo($"TryAutoFixCoordsPermutations: picked corrected safe pos {bestSafe} (original {original})");
            }
            else
            {
                TravelButtonPlugin.LogInfo($"TryAutoFixCoordsPermutations: no better permutation found for {original}");
            }

            return bestSafe;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryAutoFixCoordsPermutations: unexpected error: " + ex);
            return original;
        }
    }

    // --- hlavní metoda s integrovanou opravou a bezpeènostními kontrolami ---
    // Main method: Attempt teleport with multiple safety checks and component suspension.
    public static bool AttemptTeleportToPositionSafe(Vector3 target)
    {
        try
        {
            var initialRoot = FindPlayerRoot();
            if (initialRoot == null)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: player root not found.");
                return false;
            }

            // Resolve the actual GameObject that should be moved
            var moveGO = ResolveActualPlayerGameObject(initialRoot) ?? initialRoot;

            try
            {
                // initialRoot je ten, co našla FindPlayerRoot(); použijeme jeho pozici k selekci
                Vector3 initialPos = Vector3.zero;
                try { initialPos = initialRoot.transform.position; } catch { initialPos = Vector3.zero; }

                GameObject authoritative = null;

                // Sbìr kandidátù: všechny transformy zaèínající na "PlayerChar"
                Transform[] allTransforms = null;
                try { allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>(); } catch { allTransforms = new Transform[0]; }

                var candidates = new System.Collections.Generic.List<GameObject>();
                foreach (var t in allTransforms)
                {
                    if (t == null || string.IsNullOrEmpty(t.name)) continue;
                    if (t.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(t.gameObject);
                }

                // Priorita 1: najít kandidáta s CharacterInventory / Character componentou (pokud existují)
                if (candidates.Count > 0)
                {
                    Type charInvType = ReflectionUtils.SafeGetType("CharacterInventory, Assembly-CSharp") ?? ReflectionUtils.SafeGetType("CharacterInventory");
                    Type charType = ReflectionUtils.SafeGetType("Character, Assembly-CSharp") ?? ReflectionUtils.SafeGetType("Character");

                    foreach (var go in candidates)
                    {
                        try
                        {
                            if (charInvType != null && go.GetComponent(charInvType) != null) { authoritative = go; break; }
                            if (charType != null && go.GetComponent(charType) != null) { authoritative = go; break; }
                        }
                        catch { /* ignore per-candidate errors */ }
                    }
                }

                // Priorita 2: pokud nic s komponentou, vyber kandidáta s nenulovou pozicí nejblíže initialPos
                if (authoritative == null && candidates.Count > 0)
                {
                    float bestDist = float.MaxValue;
                    GameObject best = null;
                    foreach (var go in candidates)
                    {
                        try
                        {
                            var pos = go.transform.position;
                            // ignoruj zjevné defaultní rooty na (0,0,0)
                            if (Vector3.SqrMagnitude(pos) < 0.0001f) continue;
                            float d = Vector3.SqrMagnitude(pos - initialPos);
                            if (d < bestDist) { bestDist = d; best = go; }
                        }
                        catch { }
                    }
                    if (best != null) authoritative = best;
                }

                // Priorita 3: fallback na moveGO.transform.root, pokud vypadá rozumnì (není Cam/UI/FX)
                if (authoritative == null)
                {
                    try
                    {
                        var rc = moveGO.transform.root != null ? moveGO.transform.root.gameObject : null;
                        if (rc != null && rc != moveGO)
                        {
                            var lname = (rc.name ?? "").ToLowerInvariant();
                            if (!lname.Contains("cam") && !lname.Contains("ui") && !lname.Contains("fx"))
                                authoritative = rc;
                        }
                    }
                    catch { /* ignore */ }
                }

                // Pokud máme autoritativní root a liší se od souèasného moveGO, použij ho
                if (authoritative != null && authoritative != moveGO)
                {
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: switching move target from '{moveGO.name}' to authoritative root '{authoritative.name}'.");
                    moveGO = authoritative;
                }
            }
            catch { /* ignore overall selection errors */ }
            // --- end replacement block ---

            // Diagnostics: hierarchy and player candidates
            string parentName = "(null)";
            try { parentName = moveGO.transform.parent != null ? moveGO.transform.parent.name : "(null)"; } catch { parentName = "(unknown)"; }
            string rootName = "(unknown)";
            try { rootName = moveGO.transform.root != null ? moveGO.transform.root.name : "(unknown)"; } catch { }
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: moving object '{moveGO.name}' (instance id={moveGO.GetInstanceID()}) parent='{parentName}' root='{rootName}'");
            TravelButtonPlugin.LogInfo($"  localPos={moveGO.transform.localPosition}, worldPos={moveGO.transform.position}");

            try
            {
                var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var t in allTransforms)
                {
                    if (t == null) continue;
                    var n = t.name ?? "";
                    if (n.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = "(null)";
                        try { p = t.parent != null ? t.parent.name : "(null)"; } catch { p = "(unknown)"; }
                        TravelButtonPlugin.LogInfo($"  Detected PlayerChar candidate: '{t.name}' pos={t.position} parent='{p}'");
                    }
                }
            }
            catch { /* ignore diagnostics errors */ }

            var before = moveGO.transform.position;
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: BEFORE pos = ({before.x:F3}, {before.y:F3}, {before.z:F3})");

            // --- Permutation / NavMesh/Ground probe to pick a safe target ---
            try
            {
                if (TryPickBestCoordsPermutation(target, out Vector3 permSafe))
                {
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: TryPickBestCoordsPermutation selected {permSafe} (original {target}).");
                    target = permSafe;
                }
                else
                {
                    // fallback: at least try normal probe on original
                    if (TryFindNearestNavMeshOrGround(target, out Vector3 safeFinal, navSearchRadius: 15f, maxGroundRay: 400f))
                    {
                        TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: Using safe position {safeFinal} (was {target})");
                        target = safeFinal;
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: Could not find a nearby NavMesh or ground for target {target}. Proceeding with original target (may be unsafe).");
                    }
                }
            }
            catch (Exception exSafe)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: TryPickBestCoordsPermutation/TryFindNearestNavMeshOrGround threw: " + exSafe.Message);
            }

            // --- Safety: prevent huge vertical jumps (reject or conservative-ground) ---
            try
            {
                const float maxVerticalDelta = 100f;   // adjust to taste (meters)
                const float extraGroundClearance = 0.25f;

                float verticalDelta = Mathf.Abs(target.y - before.y);
                if (verticalDelta > maxVerticalDelta)
                {
                    TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: rejected target.y {target.y:F3} because it differs from current player.y {before.y:F3} by {verticalDelta:F3}m (max {maxVerticalDelta}). Trying conservative grounding...");

                    try
                    {
                        RaycastHit hit;
                        Vector3 origin = new Vector3(target.x, before.y + 200f, target.z); // raycast from relative height
                        if (Physics.Raycast(origin, Vector3.down, out hit, 400f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            float candidateY = hit.point.y + extraGroundClearance;
                            float candDelta = Mathf.Abs(candidateY - before.y);
                            if (candDelta <= maxVerticalDelta)
                            {
                                TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: conservative grounding succeeded: hit.y={hit.point.y:F3}, using y={candidateY:F3} (delta {candDelta:F3}).");
                                target = new Vector3(target.x, candidateY, target.z);
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: grounding hit at y={hit.point.y:F3} but delta {candDelta:F3} still > maxVerticalDelta. Aborting teleport.");
                                return false;
                            }
                        }
                        else
                        {
                            TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: conservative grounding found NO hit. Aborting teleport to avoid sending player into sky.");
                            return false;
                        }
                    }
                    catch (Exception exGround)
                    {
                        TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: conservative grounding failed: " + exGround);
                        return false;
                    }
                }
            }
            catch (Exception exSafety)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: safety-check exception: " + exSafety);
                return false;
            }

            // Zero any rigidbody velocities (child rigidbody)
            try
            {
                var rb0 = moveGO.GetComponentInChildren<Rigidbody>(true);
                if (rb0 != null)
                {
                    rb0.velocity = Vector3.zero;
                    rb0.angularVelocity = Vector3.zero;
                }
            }
            catch { }

            // --- Detect NavMeshAgent (reflection) and temporarily disable updates so agent won't fight the warp ---
            object navAgentObj = null;
            Type navAgentType = null;
            try
            {
                navAgentType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshAgent, UnityEngine.AIModule") ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshAgent");
                if (navAgentType != null)
                {
                    var getAgent = typeof(GameObject).GetMethod("GetComponentInChildren", new Type[] { typeof(Type), typeof(bool) });
                    if (getAgent != null)
                    {
                        try { navAgentObj = getAgent.Invoke(moveGO, new object[] { navAgentType, true }); } catch { navAgentObj = null; }
                    }
                    else
                    {
                        try
                        {
                            var objs = UnityEngine.Object.FindObjectsOfType(navAgentType);
                            foreach (var o in objs)
                            {
                                var comp = o as Component;
                                if (comp != null && comp.gameObject != null && comp.gameObject.transform.IsChildOf(moveGO.transform))
                                {
                                    navAgentObj = comp;
                                    break;
                                }
                            }
                        }
                        catch { navAgentObj = null; }
                    }

                    if (navAgentObj != null)
                    {
                        try
                        {
                            var updatePosProp = navAgentType.GetProperty("updatePosition");
                            var updateRotProp = navAgentType.GetProperty("updateRotation");
                            if (updatePosProp != null && updatePosProp.CanWrite) updatePosProp.SetValue(navAgentObj, false);
                            if (updateRotProp != null && updateRotProp.CanWrite) updateRotProp.SetValue(navAgentObj, false);
                            TravelButtonPlugin.LogInfo("AttemptTeleportToPositionSafe: Temporarily disabled NavMeshAgent.updatePosition/updateRotation.");
                        }
                        catch { }
                    }
                }
            }
            catch { navAgentObj = null; navAgentType = null; }

            // --- Suspend likely movement scripts and make child rigidbodies kinematic ---
            var suspendPatterns = new string[]
            {
            "LocalCharacterControl","AdvancedMover","CharacterFastTraveling","RigidbodySuspender",
            "CharacterResting","CharacterMovement","PlayerMovement","PlayerController","Movement","Motor","AI"
            };

            var disabledBehaviours = new List<Behaviour>();
            var changedRigidbodies = new List<(Rigidbody rb, bool originalIsKinematic)>();

            try
            {
                var comps = moveGO.GetComponentsInChildren<Component>(true);
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c is Rigidbody rb)
                        {
                            try
                            {
                                changedRigidbodies.Add((rb, rb.isKinematic));
                                rb.isKinematic = true;
                                TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: set Rigidbody.isKinematic=true on '{rb.gameObject.name}'.");
                            }
                            catch { }
                        }

                        if (c is Behaviour b)
                        {
                            var tname = b.GetType().Name ?? "";
                            foreach (var p in suspendPatterns)
                            {
                                if (tname.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try
                                    {
                                        if (b.enabled)
                                        {
                                            b.enabled = false;
                                            disabledBehaviours.Add(b);
                                            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: temporarily disabled {tname} on '{b.gameObject.name}'.");
                                        }
                                    }
                                    catch { }
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception exSuspend)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: error while suspending components: " + exSuspend.Message);
            }

            // --- Perform the move (prefer NavMeshAgent.Warp if available) ---
            bool moveSucceeded = false;
            try
            {
                if (navAgentObj != null && navAgentType != null)
                {
                    try
                    {
                        var warpMethod = navAgentType.GetMethod("Warp", new Type[] { typeof(Vector3) });
                        if (warpMethod != null)
                        {
                            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: warping NavMeshAgent to {target}.");
                            var warped = (bool)warpMethod.Invoke(navAgentObj, new object[] { target });
                            moveSucceeded = warped;
                            try { moveGO.transform.position = target; } catch { }
                        }
                        else
                        {
                            moveGO.transform.position = target;
                            moveSucceeded = true;
                        }
                    }
                    catch (Exception exWarp)
                    {
                        TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: NavMeshAgent.Warp failed: " + exWarp.Message);
                        try { moveGO.transform.position = target; moveSucceeded = true; } catch { moveSucceeded = false; }
                    }
                    finally
                    {
                        try
                        {
                            var updatePosProp = navAgentType.GetProperty("updatePosition");
                            var updateRotProp = navAgentType.GetProperty("updateRotation");
                            if (updatePosProp != null && updatePosProp.CanWrite) updatePosProp.SetValue(navAgentObj, true);
                            if (updateRotProp != null && updateRotProp.CanWrite) updateRotProp.SetValue(navAgentObj, true);
                        }
                        catch { }
                    }
                }
                else
                {
                    var localCC = moveGO.GetComponent<CharacterController>();
                    if (localCC != null)
                    {
                        try
                        {
                            localCC.enabled = false;
                            moveGO.transform.position = target;
                            localCC.enabled = true;
                            moveSucceeded = true;
                            TravelButtonPlugin.LogInfo("AttemptTeleportToPositionSafe: Teleported using CharacterController on moved GameObject.");
                        }
                        catch (Exception exCC)
                        {
                            TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: CharacterController move failed: " + exCC.Message);
                            try { moveGO.transform.position = target; moveSucceeded = true; } catch { moveSucceeded = false; }
                        }
                    }
                    else
                    {
                        try
                        {
                            moveGO.transform.position = target;
                            moveSucceeded = true;
                        }
                        catch (Exception exMove)
                        {
                            TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: direct transform set failed: " + exMove.Message);
                            moveSucceeded = false;
                        }
                    }
                }
            }
            catch (Exception exAll)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: unexpected move exception: " + exAll.Message);
                moveSucceeded = false;
            }

            // --- Ground-first + overlap-safety (raycast then small raises) ---
            try
            {
                Vector3 FindGroundY(Vector3 samplePos, float startAbove = 200f, float maxDistance = 400f, float clearance = 0.5f)
                {
                    try
                    {
                        RaycastHit hit;
                        Vector3 origin = new Vector3(samplePos.x, samplePos.y + startAbove, samplePos.z);
                        if (Physics.Raycast(origin, Vector3.down, out hit, maxDistance, ~0, QueryTriggerInteraction.Ignore))
                        {
                            Vector3 g = new Vector3(samplePos.x, hit.point.y + clearance, samplePos.z);
                            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: Ground raycast hit at y={hit.point.y:F3} for XZ=({samplePos.x:F3},{samplePos.z:F3}), returning grounded pos {g}");
                            return g;
                        }
                        else
                        {
                            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: Ground raycast found NO hit for XZ=({samplePos.x:F3},{samplePos.z:F3}) (origin Y={samplePos.y + startAbove:F3}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: Ground raycast failed: " + ex.Message);
                    }
                    return samplePos; // fallback
                }

                try { Physics.SyncTransforms(); } catch { }

                const float overlapCheckRadius = 0.4f;
                const float raiseStep = 0.25f;
                const float maxRaiseFallback = 2.0f;
                const float maxAllowedRaise = 12.0f;

                bool CheckOverlapsAt(Vector3 pos)
                {
                    try
                    {
                        Collider[] cols = Physics.OverlapSphere(pos, overlapCheckRadius, ~0, QueryTriggerInteraction.Ignore);
                        if (cols != null && cols.Length > 0)
                        {
                            foreach (var c in cols)
                            {
                                if (c == null) continue;
                                if (c.isTrigger) continue;
                                if (c.transform.IsChildOf(moveGO.transform)) continue;
                                return true;
                            }
                        }
                    }
                    catch { }
                    return false;
                }

                var after = moveGO.transform.position;
                var grounded = FindGroundY(after, startAbove: 200f, maxDistance: 400f, clearance: 0.5f);

                bool usedGrounding = false;
                if (!Mathf.Approximately(grounded.y, after.y))
                {
                    float deltaY = grounded.y - after.y;
                    if (Mathf.Abs(deltaY) <= maxAllowedRaise)
                    {
                        try
                        {
                            moveGO.transform.position = grounded;
                            try { Physics.SyncTransforms(); } catch { }
                            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: Applied grounding: moved from y={after.y:F3} to grounded y={grounded.y:F3}");
                            usedGrounding = true;
                            after = moveGO.transform.position;
                        }
                        catch (Exception exG) { TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: failed to apply grounding: " + exG.Message); }
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: grounding would move by {deltaY:F3}m which exceeds maxAllowedRaise={maxAllowedRaise}. Skipping auto-grounding.");
                    }
                }
                else
                {
                    TravelButtonPlugin.LogInfo("AttemptTeleportToPositionSafe: grounding did not change Y (no reliable hit).");
                }

                // overlap checking and conservative raising
                after = moveGO.transform.position;
                bool isOverlapping = CheckOverlapsAt(after);
                if (isOverlapping)
                {
                    TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: detected overlap at pos {after}. usedGrounding={usedGrounding}. Attempting incremental raise.");
                    bool foundFree = false;
                    float maxRaise = usedGrounding ? 3.0f : maxRaiseFallback;
                    for (float raise = raiseStep; raise <= maxRaise; raise += raiseStep)
                    {
                        Vector3 check = new Vector3(after.x, after.y + raise, after.z);
                        if (!CheckOverlapsAt(check))
                        {
                            try
                            {
                                moveGO.transform.position = check;
                                try { Physics.SyncTransforms(); } catch { }
                            }
                            catch { }
                            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: raised player by {raise:F2}m to avoid overlap -> {check}");
                            foundFree = true;
                            after = check;
                            break;
                        }
                    }
                    if (!foundFree)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: could not find non-overlapping spot within {maxRaise}m above {after}. Player may still be embedded or in open air.");
                    }
                }

                // Clamp extreme heights relative to requested target
                after = moveGO.transform.position;
                float allowedDeltaFromTarget = 20.0f;
                if (Mathf.Abs(after.y - target.y) > allowedDeltaFromTarget)
                {
                    Vector3 clampPos = new Vector3(after.x, target.y + 1.0f, after.z);
                    try
                    {
                        moveGO.transform.position = clampPos;
                        try { Physics.SyncTransforms(); } catch { }
                        TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: final pos was {after.y:F3} which is >{allowedDeltaFromTarget}m from target.y={target.y:F3}; clamped to {clampPos}.");
                        after = moveGO.transform.position;
                    }
                    catch { }
                }

                TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: final verified pos after grounding/overlap checks = ({after.x:F3}, {after.y:F3}, {after.z:F3})");
            }
            catch (Exception exOverlap)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: grounding/overlap-safety check failed: " + exOverlap.Message);
            }

            // Start monitoring coroutine (logs if anything moves the object after teleport)
            try
            {
                var host = TeleportHelpersBehaviour.GetOrCreateHost();
                host.StartCoroutine(host.WatchPositionAfterTeleport(moveGO, moveGO.transform.position, 2.0f));
                // re-enable suspended components and restore rigidbody kinematic flags after short delay
                host.StartCoroutine(host.ReenableComponentsAfterDelay(moveGO, disabledBehaviours, changedRigidbodies, 0.4f));
            }
            catch { }

            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: completed teleport (moveSucceeded={moveSucceeded}).");

            return true;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: exception: " + ex.Message);
            return false;
        }
    }

    // Robustnìjší permutace / validace coords — zkouší originál a Y<->Z a vybírá tu, která dává rozumnou "grounded" pozici.
    // Preference: 1) NavMesh.SamplePosition (pokud dostupné) 2) raycast grounding; výbìr preferuje pozici uvnitø rozumné výšky.
    public static bool TryPickBestCoordsPermutation(Vector3 original, out Vector3 chosenSafe)
    {
        chosenSafe = original;
        try
        {
            // Rozsahy považované za "rozumné" pro herní svìt - uprav dle potøeby (u tebe hráè okolo -20)
            const float minReasonableY = -200f;
            const float maxReasonableY = 300f;

            // Permutace k otestování (originál a Y<->Z)
            var perms = new List<Vector3>
            {
                original,
                new Vector3(original.x, original.z, original.y) // swap Y<->Z
            };

            bool foundAny = false;
            Vector3 best = original;
            float bestScore = float.MaxValue;

            foreach (var cand in perms)
            {
                try
                {
                    if (!TryFindNearestNavMeshOrGround(cand, out Vector3 safe, navSearchRadius: 15f, maxGroundRay: 400f))
                    {
                        TravelButtonPlugin.LogInfo($"TryPickBestCoordsPermutation: TryFindNearestNavMeshOrGround returned false for candidate {cand}");
                        continue;
                    }

                    bool withinWorld = safe.y >= minReasonableY && safe.y <= maxReasonableY;
                    float score = Mathf.Abs(safe.y - cand.y);

                    TravelButtonPlugin.LogInfo($"TryPickBestCoordsPermutation: candidate {cand} -> safe {safe} (withinWorld={withinWorld}, score={score:F3})");

                    if (withinWorld)
                    {
                        if (!foundAny || score < bestScore)
                        {
                            foundAny = true;
                            bestScore = score;
                            best = safe;
                        }
                    }
                    else if (!foundAny)
                    {
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = safe;
                        }
                    }
                }
                catch (Exception exCand)
                {
                    TravelButtonPlugin.LogWarning("TryPickBestCoordsPermutation: probe exception: " + exCand.Message);
                }
            }

            if (bestScore < float.MaxValue)
            {
                chosenSafe = best;
                TravelButtonPlugin.LogInfo($"TryPickBestCoordsPermutation: selected safe pos {chosenSafe} for original {original}");
                return true;
            }

            TravelButtonPlugin.LogWarning($"TryPickBestCoordsPermutation: no acceptable permutation for {original} — leaving original");
            chosenSafe = original;
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryPickBestCoordsPermutation: unexpected: " + ex.Message);
            chosenSafe = original;
            return false;
        }
    }

    // Najde bezpeèné místo blízko požadované pozice:
    //  - nejdøíve zkusí NavMesh.SamplePosition (pokud je NavMesh v projektu),
    //  - pak nahoøe-dolu raycast pro zjištìní povrchu,
    //  - nakonec vrátí false pokud nic není bezpeèné.
    // returns true + outPos when found safe candidate.
    public static bool TryFindNearestNavMeshOrGround(Vector3 desired, out Vector3 outPos, float navSearchRadius = 10f, float maxGroundRay = 400f)
    {
        outPos = desired;

        try
        {
            // 1) NavMesh.SamplePosition (reflection-safe)
            try
            {
                var navMeshType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule")
                                  ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh");
                var navMeshHitType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit, UnityEngine.AIModule")
                                  ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit");
                if (navMeshType != null && navMeshHitType != null)
                {
                    var sampleMethod = navMeshType.GetMethod("SamplePosition", new Type[] { typeof(Vector3), navMeshHitType, typeof(float), typeof(int) });
                    if (sampleMethod != null)
                    {
                        object hitBox = Activator.CreateInstance(navMeshHitType);
                        object[] args = new object[] { desired, hitBox, navSearchRadius, -1 };
                        bool found = false;
                        try
                        {
                            found = (bool)sampleMethod.Invoke(null, args);
                        }
                        catch
                        {
                            found = false;
                        }
                        if (found)
                        {
                            // navMeshHit.position field or property
                            var posField = navMeshHitType.GetField("position");
                            if (posField != null)
                            {
                                var posVal = (Vector3)posField.GetValue(args[1]);
                                outPos = posVal;
                                TravelButtonPlugin.LogInfo($"TryFindNearestNavMeshOrGround: NavMesh.SamplePosition found {outPos} (radius={navSearchRadius}).");
                                return true;
                            }
                            var posProp = navMeshHitType.GetProperty("position");
                            if (posProp != null)
                            {
                                var posVal = (Vector3)posProp.GetValue(args[1], null);
                                outPos = posVal;
                                TravelButtonPlugin.LogInfo($"TryFindNearestNavMeshOrGround: NavMesh.SamplePosition found {outPos} (radius={navSearchRadius}).");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception exNav)
            {
                TravelButtonPlugin.LogInfo("TryFindNearestNavMeshOrGround: NavMesh probe failed or not present: " + exNav.Message);
            }

            // 2) Ground raycast shora dolù - pokud navmesh nenalezen
            try
            {
                Vector3 origin = new Vector3(desired.x, desired.y + 200f, desired.z);
                RaycastHit hit;
                if (Physics.Raycast(origin, Vector3.down, out hit, maxGroundRay, ~0, QueryTriggerInteraction.Ignore))
                {
                    Vector3 grounded = new Vector3(desired.x, hit.point.y + 0.5f, desired.z); // clearance 0.5m
                    outPos = grounded;
                    TravelButtonPlugin.LogInfo($"TryFindNearestNavMeshOrGround: Raycast grounded to {outPos} (hit y={hit.point.y:F3}).");
                    return true;
                }
                else
                {
                    TravelButtonPlugin.LogInfo($"TryFindNearestNavMeshOrGround: Ground raycast found NO hit for XZ=({desired.x:F3},{desired.z:F3}).");
                }
            }
            catch (Exception exRay)
            {
                TravelButtonPlugin.LogWarning("TryFindNearestNavMeshOrGround: ground raycast failed: " + exRay.Message);
            }

            // 3) Fallback: zkusíme malé zvednutí a hledání nezapadajících pozic (minimální záchrana)
            try
            {
                const float step = 0.25f;
                const float maxUp = 2.0f;
                for (float yoff = step; yoff <= maxUp; yoff += step)
                {
                    var cand = new Vector3(desired.x, desired.y + yoff, desired.z);
                    Collider[] cols = Physics.OverlapSphere(cand, 0.4f, ~0, QueryTriggerInteraction.Ignore);
                    bool blocked = false;
                    if (cols != null && cols.Length > 0)
                    {
                        foreach (var c in cols)
                        {
                            if (c == null) continue;
                            if (c.isTrigger) continue;
                            blocked = true;
                            break;
                        }
                    }
                    if (!blocked)
                    {
                        outPos = cand;
                        TravelButtonPlugin.LogInfo($"TryFindNearestNavMeshOrGround: fallback free spot at {outPos}.");
                        return true;
                    }
                }
            }
            catch { /* ignore */ }

            TravelButtonPlugin.LogWarning("TryFindNearestNavMeshOrGround: no safe pos found near desired position.");
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryFindNearestNavMeshOrGround: unexpected: " + ex);
            outPos = desired;
            return false;
        }
    }

    // additional helper: ground a position (more robust raycast down)
    public static Vector3 GetGroundedPosition(Vector3 pos)
    {
        try
        {
            RaycastHit hit;

            // Start higher to improve hit chances in tall scenes / structures
            const float baseStartAbove = 200f;
            const float extraSearchAbove = 150f; // increased to be safer
            float startAbove = baseStartAbove + extraSearchAbove;

            // However, if pos.y is already very high, start relative to pos.y to prevent excessively high origins
            float originHeight = Mathf.Max(startAbove, pos.y + 50f);
            var origin = pos + Vector3.up * originHeight;

            // Allow a generous ray distance: origin down to well below pos
            float maxDistance = originHeight + 400f;

            // Layer mask: all layers
            int layerMask = ~0;

            // First try a straight raycast
            if (Physics.Raycast(origin, Vector3.down, out hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore))
            {
                // base candidate Y slightly above hit point
                float baseY = hit.point.y + 0.5f; // increase clearance from 0.2 -> 0.5 to avoid embedding
                Vector3 candidate = new Vector3(pos.x, baseY, pos.z);

                // Verify the candidate is not intersecting geometry (non-trigger colliders).
                // If it is, raise gradually (0.25m steps) up to a limit (e.g., 5m) to find a free spot.
                const float raiseStep = 0.25f;
                const float maxRaise = 5.0f;
                const float checkRadius = 0.4f; // radius to use for overlap check (approx player radius)
                for (float raise = 0f; raise <= maxRaise; raise += raiseStep)
                {
                    Vector3 checkPos = new Vector3(candidate.x, candidate.y + raise, candidate.z);
                    // OverlapSphere returns colliders touching this sphere; ignore triggers
                    Collider[] cols = Physics.OverlapSphere(checkPos, checkRadius, layerMask, QueryTriggerInteraction.Ignore);
                    bool hasBlocking = false;
                    if (cols != null && cols.Length > 0)
                    {
                        foreach (var c in cols)
                        {
                            if (c == null) continue;
                            if (c.isTrigger) continue;
                            // If collider belongs to terrain or a mesh, consider it blocking
                            hasBlocking = true;
                            break;
                        }
                    }

                    if (!hasBlocking)
                    {
                        // found a non-overlapping spot
                        return checkPos;
                    }
                }

                // If we couldn't find a non-overlapping spot after raising, return the slightly cleared point
                TravelButtonPlugin.LogWarning($"GetGroundedPosition: ground found at {hit.point.y:F3} but candidate positions up to {maxRaise}m remain overlapping. Returning base candidate {candidate} (may be unsafe).");
                return candidate;
            }

            // Fallback: SphereCast for wider check (helps with falling through cracks)
            if (Physics.SphereCast(origin, 0.5f, Vector3.down, out hit, maxDistance, layerMask, QueryTriggerInteraction.Ignore))
            {
                float baseY = hit.point.y + 0.5f;
                Vector3 candidate = new Vector3(pos.x, baseY, pos.z);

                const float raiseStep = 0.25f;
                const float maxRaise = 5.0f;
                const float checkRadius = 0.4f;
                for (float raise = 0f; raise <= maxRaise; raise += raiseStep)
                {
                    Vector3 checkPos = new Vector3(candidate.x, candidate.y + raise, candidate.z);
                    Collider[] cols = Physics.OverlapSphere(checkPos, checkRadius, layerMask, QueryTriggerInteraction.Ignore);
                    bool hasBlocking = false;
                    if (cols != null && cols.Length > 0)
                    {
                        foreach (var c in cols)
                        {
                            if (c == null) continue;
                            if (c.isTrigger) continue;
                            hasBlocking = true;
                            break;
                        }
                    }

                    if (!hasBlocking)
                    {
                        return checkPos;
                    }
                }

                TravelButtonPlugin.LogWarning($"GetGroundedPosition: sphere-ground found at {hit.point.y:F3} but candidate positions up to {maxRaise}m remain overlapping. Returning base candidate {candidate} (may be unsafe).");
                return candidate;
            }

            // If we reach here, no ground was found. Log a warning.
            TravelButtonPlugin.LogWarning($"GetGroundedPosition: Could not find ground for position {pos}. Teleport may be unsafe.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning($"GetGroundedPosition: Exception while finding ground for {pos}: {ex.Message}");
        }

        return pos; // Return original position as a last resort
    }

    public static Vector3 EnsureClearance(Vector3 pos)
    {
        // ensure we don't place player deep in terrain; add small upward offset
        return pos + Vector3.up * 0.1f;
    }
}