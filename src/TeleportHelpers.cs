using System;
using UnityEngine;

/// <summary>
/// Static helper class for teleport-related operations.
/// These methods forward to TravelButtonUI static methods for compatibility.
/// </summary>
public static class TeleportHelpers
{
    // Flag indicating a reenable-components coroutine is in progress (used for caller synchronization)
    public static bool ReenableInProgress { get; set; } = false;
    
    // Flag indicating a teleport is in progress
    public static bool TeleportInProgress { get; set; } = false;
    
    // Ground clearance offset for teleport placement
    public const float TeleportGroundClearance = 0.5f;

    /// <summary>
    /// Find the player root GameObject.
    /// Forwards to TravelButtonUI.FindPlayerRoot().
    /// </summary>
    public static GameObject FindPlayerRoot()
    {
        return TravelButtonUI.FindPlayerRoot();
    }

    /// <summary>
    /// Attempt to teleport player to a safe position.
    /// Forwards to TravelButtonUI.AttemptTeleportToPositionSafe(target).
    /// </summary>
    public static bool AttemptTeleportToPositionSafe(Vector3 target)
    {
        return TravelButtonUI.AttemptTeleportToPositionSafe(target);
    }

    /// <summary>
    /// Get a grounded position for the given target.
    /// Simple implementation that returns the target position.
    /// </summary>
    public static Vector3 GetGroundedPosition(Vector3 target)
    {
        // Simple passthrough - the actual grounding is done in AttemptTeleportToPositionSafe
        return target;
    }

    /// <summary>
    /// Try to pick the best coordinates permutation for safe teleport.
    /// Returns false as permutation logic is handled in AttemptTeleportToPositionSafe.
    /// </summary>
    public static bool TryPickBestCoordsPermutation(Vector3 target, out Vector3 safePos)
    {
        safePos = target;
        return false; // Let AttemptTeleportToPositionSafe handle this
    }

    /// <summary>
    /// Resolve the actual player GameObject for movement (handles camera/child cases).
    /// Returns the input GameObject as-is.
    /// </summary>
    public static GameObject ResolveActualPlayerGameObject(GameObject initialRoot)
    {
        return initialRoot; // Logic is in AttemptTeleportToPositionSafe
    }

    /// <summary>
    /// Ensure clearance for the given position.
    /// Returns the target position with a small Y offset.
    /// </summary>
    public static Vector3 EnsureClearance(Vector3 target)
    {
        return new Vector3(target.x, target.y + TeleportGroundClearance, target.z);
    }
}
