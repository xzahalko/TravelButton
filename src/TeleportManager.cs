using System;
using UnityEngine;

// Wrapper around teleportation logic.
// NOTE: Replace PlayerLocator/GetPlayer calls with your game's API.
// If your mod already has teleport code, adapt this class to call it.
public static class TeleportManager
{
    public static bool TeleportPlayerTo(float[] coords)
    {
        if (coords == null || coords.Length < 3)
        {
            Debug.LogWarning("[TravelButton] Teleport requested but coords are missing.");
            return false;
        }

        try
        {
            Vector3 dest = new Vector3(coords[0], coords[1], coords[2]);

            // Example using a hypothetical player singleton. Replace as needed:
            var playerGO = GameObject.FindWithTag("Player");
            if (playerGO == null)
            {
                Debug.LogError("[TravelButton] Player object not found for teleport.");
                return false;
            }

            // Option 1: Set position directly
            playerGO.transform.position = dest;

            // Option 2: If your game has a proper teleport/wrap API, call it instead
            // GameAPI.TeleportPlayerTo(dest);

            Debug.Log($"[TravelButton] Teleported player to {dest}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[TravelButton] Teleport failed: " + ex);
            return false;
        }
    }
}