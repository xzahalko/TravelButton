using System;
using UnityEngine;

/// <summary>
/// Static helper class containing teleport-related utility methods.
/// These are lightweight helpers used by various teleport flows.
/// </summary>
public static class TeleportHelpers
{
    // Constants
    public const float TeleportGroundClearance = 0.5f;
    
    // State flags for re-enable coordination
    public static bool ReenableInProgress = false;
    public static bool TeleportInProgress = false;

    /// <summary>
    /// Find the player root GameObject using various heuristics.
    /// Returns null if player cannot be found.
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
    /// Resolve the actual player GameObject that should be moved.
    /// Handles cases where the initial root might be a camera or other child.
    /// </summary>
    public static GameObject ResolveActualPlayerGameObject(GameObject initial)
    {
        if (initial == null) return null;

        try
        {
            // Check if we have a Character component (game-specific)
            var charType = ReflectionUtils.SafeGetType("Character, Assembly-CSharp") ?? ReflectionUtils.SafeGetType("Character");
            if (charType != null)
            {
                var comp = initial.GetComponent(charType);
                if (comp != null) return initial;

                // Try root
                var root = initial.transform.root;
                if (root != null)
                {
                    var rootComp = root.GetComponent(charType);
                    if (rootComp != null) return root.gameObject;
                }
            }

            // Fallback: prefer root with "PlayerChar" name
            var rootGO = initial.transform.root != null ? initial.transform.root.gameObject : initial;
            if (rootGO.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
            {
                return rootGO;
            }
        }
        catch { /* ignore */ }

        return initial;
    }

    /// <summary>
    /// Apply ground clearance to a position (simple version).
    /// </summary>
    public static Vector3 GetGroundedPosition(Vector3 pos)
    {
        try
        {
            // Simple raycast from above
            RaycastHit hit;
            Vector3 origin = new Vector3(pos.x, pos.y + 200f, pos.z);
            if (Physics.Raycast(origin, Vector3.down, out hit, 400f, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(pos.x, hit.point.y + TeleportGroundClearance, pos.z);
            }
        }
        catch { /* ignore */ }

        return pos;
    }

    /// <summary>
    /// Ensure clearance above a position.
    /// </summary>
    public static Vector3 EnsureClearance(Vector3 pos)
    {
        try
        {
            // Raycast from below to find ground
            RaycastHit hit;
            Vector3 origin = new Vector3(pos.x, pos.y - 100f, pos.z);
            if (Physics.Raycast(origin, Vector3.up, out hit, 200f, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(pos.x, hit.point.y + TeleportGroundClearance, pos.z);
            }
        }
        catch { /* ignore */ }

        return pos;
    }

    /// <summary>
    /// Try to pick the best coordinates from permutations.
    /// Returns true if a good candidate was found.
    /// </summary>
    public static bool TryPickBestCoordsPermutation(Vector3 target, out Vector3 best)
    {
        best = target;

        try
        {
            // Try NavMesh sampling if available
            var navType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule") 
                ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh");
            var navHitType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit, UnityEngine.AIModule") 
                ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit");
            
            if (navType != null && navHitType != null)
            {
                var sampleMethod = navType.GetMethod("SamplePosition", 
                    new Type[] { typeof(Vector3), navHitType, typeof(float), typeof(int) });
                    
                if (sampleMethod != null)
                {
                    object navHit = Activator.CreateInstance(navHitType);
                    var args = new object[] { target, navHit, 25f, -1 };
                    var ok = (bool)sampleMethod.Invoke(null, args);
                    
                    if (ok)
                    {
                        var posProp = navHitType.GetProperty("position");
                        if (posProp != null)
                        {
                            best = (Vector3)posProp.GetValue(navHit);
                            return true;
                        }
                    }
                }
            }

            // Fallback: simple raycast from above
            RaycastHit hit;
            Vector3 origin = new Vector3(target.x, target.y + 200f, target.z);
            if (Physics.Raycast(origin, Vector3.down, out hit, 400f, ~0, QueryTriggerInteraction.Ignore))
            {
                best = new Vector3(target.x, hit.point.y + TeleportGroundClearance, target.z);
                return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>
    /// [OBSOLETE] Legacy synchronous teleport method.
    /// Use SafePlacePlayerCoroutine or PlacePlayerUsingSafeRoutine instead.
    /// </summary>
    [Obsolete("Use TeleportManager.SafePlacePlayerCoroutine or PlacePlayerUsingSafeRoutine instead")]
    public static bool AttemptTeleportToPositionSafe(Vector3 target)
    {
        TBLog.Warn("AttemptTeleportToPositionSafe is obsolete and should not be called. Use SafePlacePlayerCoroutine instead.");
        return false;
    }
}
