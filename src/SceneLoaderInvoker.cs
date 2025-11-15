using System.Collections;
using UnityEngine;

/// <summary>
/// Small convenience wrapper to start the TravelButtonSceneLoader coroutine on the persistent TeleportHelpersBehaviour host.
/// This wrapper also ensures a global TeleportInProgress flag is set while the loader runs and is always cleared in a finally block.
/// Use SceneLoaderInvoker.StartLoad(...) instead of manually creating components everywhere.
/// </summary>
public static class SceneLoaderInvoker
{
    /// <summary>
    /// Start the LoadSceneAndTeleportCoroutine on a persistent host.
    /// cityObj: the object your existing UI code passes (the same object you used before)
    /// cost, coordsHint, haveCoordsHint: same parameters your UI expects
    /// </summary>
    public static void StartLoad(object cityObj, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        // create or get the persistent host
        var host = TeleportHelpersBehaviour.GetOrCreateHost();

        // ensure a TravelButtonSceneLoader component is attached to the host GameObject
        var loader = host.gameObject.GetComponent<TravelButtonSceneLoader>();
        if (loader == null) loader = host.gameObject.AddComponent<TravelButtonSceneLoader>();

        // Start the loader coroutine in a safe wrapper that sets/clears the TeleportInProgress flag
        host.StartCoroutine(RunLoaderSafely(loader, cityObj, cost, coordsHint, haveCoordsHint));
    }

    // Wrapper coroutine that guarantees TeleportInProgress is cleared on all exit paths
    private static IEnumerator RunLoaderSafely(TravelButtonSceneLoader loader, object cityObj, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        // If TeleportHelpers.TeleportInProgress exists, set it. If not, this will be a no-op (wrap in try).
        try { TeleportHelpers.TeleportInProgress = true; } catch { }

        try
        {
            // Delegate to the loader coroutine and yield until it completes
            yield return loader.LoadSceneAndTeleportCoroutine(cityObj, cost, coordsHint, haveCoordsHint);
        }
        finally
        {
            // Always clear the flag when done (no yield here)
            try { TeleportHelpers.TeleportInProgress = false; } catch { }
        }
    }
}