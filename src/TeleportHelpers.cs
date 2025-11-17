using System;
using UnityEngine;

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
}
