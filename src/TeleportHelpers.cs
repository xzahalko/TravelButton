using System;
using System.Reflection;
using UnityEngine;

public static class TeleportHelpers
{
    // Raycast + Terrain (reflection) grounded position finder (runtime-safe).
    public static Vector3 GetGroundedPosition(Vector3 desiredPosition, float maxUp = 50f, float maxDown = 200f, float groundOffset = 1.2f)
    {
        try
        {
            Vector3 rayStart = desiredPosition + Vector3.up * maxUp;
            Ray ray = new Ray(rayStart, Vector3.down);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, maxUp + maxDown, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(desiredPosition.x, hit.point.y + groundOffset, desiredPosition.z);
            }

            // Try Terrain via reflection (if Terrain module is present)
            try
            {
                var terrainType = Type.GetType("UnityEngine.Terrain, UnityEngine.TerrainModule");
                if (terrainType != null)
                {
                    var activeTerrainProp = terrainType.GetProperty("activeTerrain", BindingFlags.Public | BindingFlags.Static);
                    var activeTerrain = activeTerrainProp?.GetValue(null);
                    if (activeTerrain != null)
                    {
                        var sampleHeightMethod = terrainType.GetMethod("SampleHeight", new Type[] { typeof(Vector3) });
                        if (sampleHeightMethod != null)
                        {
                            var terrainLocalHeightObj = sampleHeightMethod.Invoke(activeTerrain, new object[] { desiredPosition });
                            if (terrainLocalHeightObj is float terrainLocalHeight)
                            {
                                var transformProp = terrainType.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                                var terrainTransform = transformProp?.GetValue(activeTerrain) as Transform;
                                float terrainWorldY = (terrainTransform != null) ? terrainTransform.position.y : 0f;
                                float worldHeight = terrainLocalHeight + terrainWorldY;
                                return new Vector3(desiredPosition.x, worldHeight + groundOffset, desiredPosition.z);
                            }
                        }
                    }
                }
            }
            catch { /* ignore */ }

            // last-resort short downward ray from just above desiredPosition
            rayStart = desiredPosition + Vector3.up * 1.0f;
            ray = new Ray(rayStart, Vector3.down);
            if (Physics.Raycast(ray, out hit, maxUp + 1f, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(desiredPosition.x, hit.point.y + groundOffset, desiredPosition.z);
            }

            // Nothing found - return desired with small offset
            return new Vector3(desiredPosition.x, desiredPosition.y + groundOffset, desiredPosition.z);
        }
        catch
        {
            return new Vector3(desiredPosition.x, desiredPosition.y + groundOffset, desiredPosition.z);
        }
    }

    // Ensure the capsule at pos is not overlapping; nudge up if it is.
    public static Vector3 EnsureClearance(Vector3 pos, float capsuleHeight = 1.8f, float capsuleRadius = 0.35f, int maxAttempts = 12, float step = 0.5f)
    {
        try
        {
            // bottom point of capsule (approx)
            Vector3 bottom = pos + Vector3.up * capsuleRadius;
            for (int i = 0; i < maxAttempts; i++)
            {
                bool overlap = Physics.CheckCapsule(bottom, bottom + Vector3.up * (capsuleHeight - capsuleRadius), capsuleRadius, ~0, QueryTriggerInteraction.Ignore);
                if (!overlap) return pos;
                pos += Vector3.up * step;
                bottom = pos + Vector3.up * capsuleRadius;
            }
            return pos;
        }
        catch
        {
            return pos;
        }
    }

    // Reflection-safe helpers for NavMeshAgent in case AIModule isn't referenced at compile-time
    private static Component GetNavMeshAgentComponent(GameObject root)
    {
        try
        {
            var agentType = Type.GetType("UnityEngine.AI.NavMeshAgent, UnityEngine.AIModule") ?? Type.GetType("UnityEngine.AI.NavMeshAgent");
            if (agentType == null) return null;
            var comp = root.GetComponent(agentType);
            return comp as Component;
        }
        catch { return null; }
    }

    private static void SetNavMeshAgentEnabled(Component agentComp, bool enabled)
    {
        if (agentComp == null) return;
        try
        {
            var t = agentComp.GetType();
            var prop = t.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite) prop.SetValue(agentComp, enabled);
        }
        catch { }
    }

    // Robust teleport that disables controllers/agents temporarily, sets position, then restores.
    public static bool AttemptTeleportToPositionSafe(Vector3 targetPos)
    {
        try
        {
            // Find player component: prefer known runtime player types, fallback to tag or camera
            Component playerComp = null;
            string[] typeCandidates = new string[] { "PlayerCharacter", "PlayerEntity", "Character", "PC_Player", "PlayerController", "LocalPlayer", "Player" };
            foreach (var tn in typeCandidates)
            {
                try
                {
                    var t = Type.GetType(tn + ", Assembly-CSharp");
                    if (t == null) continue;
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0)
                    {
                        playerComp = objs[0] as Component;
                        break;
                    }
                }
                catch { }
            }

            if (playerComp == null)
            {
                try
                {
                    var tagged = GameObject.FindWithTag("Player");
                    if (tagged != null) playerComp = tagged.transform as Component;
                }
                catch { }
            }

            if (playerComp == null && Camera.main != null)
            {
                playerComp = Camera.main.transform as Component;
            }

            if (playerComp == null)
            {
                TravelButtonMod.LogWarning("AttemptTeleportToPosition: could not find a player object to move.");
                return false;
            }

            var go = playerComp.gameObject;
            var root = go.transform.root.gameObject;

            TravelButtonMod.LogInfo($"AttemptTeleportToPosition: moving object '{go.name}' (id={go.GetInstanceID()}) root='{root.name}' (id={root.GetInstanceID()})");
            var beforePos = root.transform.position;
            TravelButtonMod.LogInfo($"AttemptTeleportToPosition: BEFORE pos = {beforePos}");

            // Temporarily disable movement components
            CharacterController cc = root.GetComponentInChildren<CharacterController>();
            Component navAgentComp = GetNavMeshAgentComponent(root);
            Rigidbody rb = root.GetComponentInChildren<Rigidbody>();

            bool ccWasEnabled = false;
            bool agentWasEnabled = false;
            bool rbWasKinematic = false;

            if (cc != null)
            {
                try { ccWasEnabled = cc.enabled; cc.enabled = false; } catch { }
            }
            if (navAgentComp != null)
            {
                try
                {
                    var prop = navAgentComp.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null) agentWasEnabled = (bool)prop.GetValue(navAgentComp);
                    SetNavMeshAgentEnabled(navAgentComp, false);
                }
                catch { }
            }
            if (rb != null)
            {
                try { rbWasKinematic = rb.isKinematic; rb.velocity = Vector3.zero; rb.isKinematic = true; } catch { }
            }

            // Move
            root.transform.position = targetPos;
            if (playerComp.transform != root.transform)
            {
                playerComp.transform.position = targetPos;
            }

            // restore physics
            try
            {
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = rbWasKinematic;
                }
            }
            catch { }

            // re-enable nav agent and char controller
            if (navAgentComp != null)
            {
                try { SetNavMeshAgentEnabled(navAgentComp, agentWasEnabled); } catch { }
            }
            if (cc != null)
            {
                try { cc.enabled = ccWasEnabled; } catch { }
            }

            var afterPos = root.transform.position;
            TravelButtonMod.LogInfo($"AttemptTeleportToPosition: AFTER pos = {afterPos}");

            return true;
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("AttemptTeleportToPosition: unexpected exception: " + ex);
            return false;
        }
    }
}