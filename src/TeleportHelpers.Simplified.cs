using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class TeleportHelpersSimplified
{
    // Small, safe helper that teleports player to a GameObject anchor identified by name.
    // - Tries exact find, (Clone) stripped find, then case-insensitive search.
    // - Performs a single grounding raycast downward from above the anchor.
    // - Performs a small overlap check and incremental upward nudge if needed.
    // - Temporarily disables common movement components and makes child rigidbodies kinematic,
    //   then restores them via the host coroutine.
    //
    // Returns true if the teleport sequence completed and stabilization coroutines were started.
    // This intentionally avoids heavy NavMesh logic and large grid searches — keep it simple.
    public static bool AttemptTeleportToTargetAnchor(string targetGameObjectName, float groundClearance = 0.5f)
    {
        try
        {
            if (string.IsNullOrEmpty(targetGameObjectName))
            {
                TBLog.Warn("AttemptTeleportToTargetAnchor: targetGameObjectName is null/empty.");
                return false;
            }

            // Resolve GameObject by a few common heuristics
            GameObject targetGO = null;
            try { targetGO = GameObject.Find(targetGameObjectName); } catch { targetGO = null; }
            if (targetGO == null && targetGameObjectName.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = targetGameObjectName.Replace("(Clone)", "").Trim();
                try { targetGO = GameObject.Find(stripped); } catch { targetGO = null; }
            }
            if (targetGO == null)
            {
                // case-insensitive search fallback
                try
                {
                    foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                    {
                        if (string.Equals(go.name, targetGameObjectName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetGO = go;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (targetGO == null)
            {
                TBLog.Info($"AttemptTeleportToTargetAnchor: no GameObject named '{targetGameObjectName}' found in scene.");
                return false;
            }

            Vector3 desired = targetGO.transform.position;
            TBLog.Info($"AttemptTeleportToTargetAnchor: anchor '{targetGO.name}' at {desired}");

            // Simple grounding: raycast from above anchor down to find floor
            Vector3 grounded = desired;
            try
            {
                RaycastHit hit;
                Vector3 rayOrigin = new Vector3(desired.x, desired.y + 50f, desired.z);
                if (Physics.Raycast(rayOrigin, Vector3.down, out hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                {
                    grounded.y = hit.point.y + groundClearance;
                    TBLog.Info($"AttemptTeleportToTargetAnchor: grounded anchor at y={grounded.y:F3} (hit {hit.point.y:F3}).");
                }
                else
                {
                    TBLog.Info("AttemptTeleportToTargetAnchor: grounding raycast found no hit; using anchor Y.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("AttemptTeleportToTargetAnchor: grounding raycast failed: " + ex.Message);
            }

            // Find player root and object to move
            var initialRoot = FindPlayerRoot();
            if (initialRoot == null)
            {
                TBLog.Warn("AttemptTeleportToTargetAnchor: player root not found.");
                return false;
            }
            var moveGO = TeleportHelpers.ResolveActualPlayerGameObject(initialRoot) ?? initialRoot;

            // Suspend movement-ish components and make rigidbodies kinematic (simple set)
            var disabledList = new List<Behaviour>();
            var changedRigidbodies = new List<(Rigidbody rb, bool original)>();
            try
            {
                foreach (var comp in moveGO.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    try
                    {
                        if (comp is Rigidbody rb)
                        {
                            changedRigidbodies.Add((rb, rb.isKinematic));
                            rb.isKinematic = true;
                        }

                        if (comp is Behaviour b)
                        {
                            string tn = b.GetType().Name;
                            // simple, common names only
                            if (tn.IndexOf("CharacterController", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                tn.IndexOf("LocalCharacterControl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                tn.IndexOf("AdvancedMover", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                tn.IndexOf("CharacterFastTraveling", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                tn.IndexOf("PlayerMovement", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (b.enabled)
                                {
                                    try { b.enabled = false; disabledList.Add(b); } catch { }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("AttemptTeleportToTargetAnchor: error while suspending components: " + ex.Message);
            }

            // small overlap safety check and small raises
            Vector3 finalPos = grounded;
            try
            {
                const float overlapRadius = 0.45f;
                const float raiseStep = 0.25f;
                const float maxRaise = 2.0f;

                bool Overlaps(Vector3 p)
                {
                    try
                    {
                        var cols = Physics.OverlapSphere(p, overlapRadius, ~0, QueryTriggerInteraction.Ignore);
                        if (cols != null)
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

                if (Overlaps(finalPos))
                {
                    bool found = false;
                    for (float r = raiseStep; r <= maxRaise; r += raiseStep)
                    {
                        var tryPos = new Vector3(finalPos.x, finalPos.y + r, finalPos.z);
                        if (!Overlaps(tryPos))
                        {
                            finalPos = tryPos;
                            TBLog.Info($"AttemptTeleportToTargetAnchor: raised {r:F2}m to avoid overlap -> {finalPos}");
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        TBLog.Warn("AttemptTeleportToTargetAnchor: overlap resolution failed; teleport may embed player.");
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("AttemptTeleportToTargetAnchor: overlap check failed: " + ex.Message);
            }

            // Perform move (simple direct transform set, safe with CC handling)
            bool moved = false;
            try
            {
                var cc = moveGO.GetComponent<CharacterController>();
                if (cc != null)
                {
                    try
                    {
                        cc.enabled = false;
                        moveGO.transform.position = finalPos;
                        cc.enabled = true;
                        moved = true;
                    }
                    catch { moved = false; }
                }
                else
                {
                    try
                    {
                        moveGO.transform.position = finalPos;
                        moved = true;
                    }
                    catch { moved = false; }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("AttemptTeleportToTargetAnchor: move failed: " + ex.Message);
                moved = false;
            }

            // Ensure active scene is the target's scene if different
            try
            {
                var targetScene = targetGO.scene;
                if (targetScene.IsValid() && SceneManager.GetActiveScene().name != targetScene.name)
                {
                    try
                    {
                        SceneManager.SetActiveScene(targetScene);
                        TBLog.Info($"AttemptTeleportToTargetAnchor: SetActiveScene('{targetScene.name}')");
                    }
                    catch { TBLog.Warn("AttemptTeleportToTargetAnchor: SetActiveScene failed."); }
                }
            }
            catch { }

            // Start small stabilization coroutines: restore components after delay and optionally watch position
            try
            {
                var host = TeleportHelpersBehaviour.GetOrCreateHost();
                host.StartCoroutine(host.ReenableComponentsAfterDelay(moveGO, disabledList, changedRigidbodies, 0.3f));
                host.StartCoroutine(host.WatchPositionAfterTeleport(moveGO, finalPos, 1.5f));
            }
            catch { }

            TBLog.Info($"AttemptTeleportToTargetAnchor: completed (moved={moved}) to {finalPos} using anchor '{targetGO.name}'");
            return moved;
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToTargetAnchor: exception: " + ex.Message);
            return false;
        }
    }

    // Small helper - adapts to your existing codebase's FindPlayerRoot if present; fallback simple find by tag/name.
    static GameObject FindPlayerRoot()
    {
        try
        {
            var root = GameObject.FindWithTag("Player");
            if (root != null) return root;
        }
        catch { }
        try
        {
            // fallback common PlayerChar naming used in logs
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                if (go.name != null && go.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                    return go;
        }
        catch { }
        return null;
    }
}