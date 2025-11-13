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
    public static GameObject ResolveActualPlayerGameObject(GameObject root)
    {
        if (root == null) return null;
        if ((root.name ?? "").StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) ||
            (root.name ?? "").IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (root.name ?? "").IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return root;
        }
        try
        {
            var childs = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in childs)
            {
                if (t == null) continue;
                string n = t.name ?? "";
                if (n.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) ||
                    n.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return t.root.gameObject;
                }
                if (t.GetComponent("PlayerCharacter") != null || t.GetComponent("PlayerEntity") != null)
                {
                    return t.root.gameObject;
                }
            }
        }
        catch { }
        return root;
    }

    // AttemptTeleportToPositionSafe: move the correct player object (not a house root) to target
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

            // Resolve to the actual player GameObject (PlayerChar)
            var playerGO = ResolveActualPlayerGameObject(initialRoot) ?? initialRoot;
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: chosen player object = '{playerGO.name}' (root id={playerGO.GetInstanceID()})");

            var before = playerGO.transform.position;
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: BEFORE pos = ({before.x:F3}, {before.y:F3}, {before.z:F3})");

            // Ensure we clear any physics velocity if present
            try
            {
                var rb = playerGO.GetComponentInChildren<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch { }

            // Actually move the player's transform. Doing transform.position is usually fine for Outward.
            try
            {
                playerGO.transform.position = target;
            }
            catch (Exception exMove)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: exception while setting position: " + exMove);
                return false;
            }

            var after = playerGO.transform.position;
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: AFTER pos = ({after.x:F3}, {after.y:F3}, {after.z:F3})");
            return true;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: exception: " + ex);
            return false;
        }
    }

    // additional helper: ground a position (simple raycast down)
    public static Vector3 GetGroundedPosition(Vector3 pos)
    {
        try
        {
            RaycastHit hit;
            var origin = pos + Vector3.up * 5f;
            if (Physics.Raycast(origin, Vector3.down, out hit, 50f, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(pos.x, hit.point.y + 0.1f, pos.z);
            }
        }
        catch { }
        return pos;
    }

    public static Vector3 EnsureClearance(Vector3 pos)
    {
        // ensure we don't place player deep in terrain; add small upward offset
        return pos + Vector3.up * 0.1f;
    }
}