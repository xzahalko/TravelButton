using System;
using System.Collections;
using UnityEngine;

public partial class TravelButtonUI : MonoBehaviour
{
    // Find a plausible player transform if caller doesn't already have one.
    private Transform FindLocalPlayerTransform()
    {
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go != null) return go.transform;

            foreach (var g in GameObject.FindObjectsOfType<GameObject>())
            {
                if (g.name != null && g.name.StartsWith("PlayerChar"))
                    return g.transform;
            }

            if (Camera.main != null && Camera.main.transform.parent != null) return Camera.main.transform.parent;
            return Camera.main?.transform;
        }
        catch (Exception ex)
        {
            TBLog.Warn("FindLocalPlayerTransform: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Coroutine that teleports the player to a safe landing near desiredPos.
    /// Yields are performed outside of try/catch blocks to avoid CS1626.
    /// </summary>
    public IEnumerator SafeTeleportRoutine(Transform playerTransform, Vector3 desiredPos)
    {
        TBLog.Info($"SafeTeleportRoutine: start for desiredPos={desiredPos} playerTransform={playerTransform?.name ?? "<null>"}");

        if (playerTransform == null)
        {
            playerTransform = FindLocalPlayerTransform();
            TBLog.Info($"SafeTeleportRoutine: resolved playerTransform to {playerTransform?.name ?? "<null>"}");
            if (playerTransform == null)
            {
                TBLog.Warn("SafeTeleportRoutine: no player transform found; aborting.");
                yield break;
            }
        }

        // Find a safe landing position (no yields here)
        Vector3 safePos = FindSafeLandingBeforeTeleport(desiredPos, playerTransform);
        TBLog.Info($"SafeTeleportRoutine: chosen safePos={safePos} (desired={desiredPos})");

        // Gather physics/controller components and store their states (no yields)
        Rigidbody rb = null;
        CharacterController cc = null;
        try
        {
            var rbCandidates = playerTransform.GetComponentsInChildren<Rigidbody>(true);
            if (rbCandidates != null && rbCandidates.Length > 0) rb = rbCandidates[0];
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafeTeleportRoutine: error detecting components: " + ex);
        }

        bool ccEnabledPrev = cc != null ? cc.enabled : false;
        bool rbKinematicPrev = rb != null ? rb.isKinematic : false;

        // Try to disable movement/physics (no yields inside this try)
        try
        {
            if (cc != null) cc.enabled = false;
            if (rb != null)
            {
                rb.isKinematic = true;
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
            // Disable any custom controllers here if necessary (optional)
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafeTeleportRoutine: error disabling physics/controllers: " + ex);
            // continue — we still proceed to set position
        }

        // Set position inside a try/catch but DO NOT yield inside this try
        try
        {
            playerTransform.position = safePos;
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafeTeleportRoutine: error while setting position: " + ex);
        }

        // Now it's safe to yield (we are outside any try/catch that contains a catch)
        yield return null;
        yield return null;

        // Re-enable physics/controllers and reset velocities to zero for safety (no yields inside this try)
        try
        {
            if (rb != null)
            {
                try
                {
                    rb.isKinematic = rbKinematicPrev;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                catch { }
            }
            if (cc != null) cc.enabled = ccEnabledPrev;
            // Re-enable custom controllers here if you disabled them earlier
        }
        catch (Exception ex)
        {
            TBLog.Warn("SafeTeleportRoutine: error restoring physics/controllers: " + ex);
        }

        TBLog.Info($"SafeTeleportRoutine: completed safe teleport to {safePos} for player {playerTransform.name}");
        yield break;
    }

    // The helper below is unchanged from prior version; it must not contain yields in a try/catch either.
    private Vector3 FindSafeLandingBeforeTeleport(Vector3 hint, Transform playerTransform)
    {
        try
        {
            const float upSearch = 150f;
            const float downMax = 400f;
            const float sampleRadius = 6f;
            const int radialSteps = 12;

            RaycastHit hit;
            Vector3 top = hint + Vector3.up * upSearch;
            if (Physics.Raycast(top, Vector3.down, out hit, upSearch + downMax, ~0, QueryTriggerInteraction.Ignore))
            {
                Vector3 found = hit.point;
                if (IsVerticalDeltaAcceptable(playerTransform, found))
                    return found;
            }

            for (int i = 0; i < radialSteps; i++)
            {
                float ang = (360f / radialSteps) * i * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * sampleRadius;
                top = hint + offset + Vector3.up * upSearch;
                if (Physics.Raycast(top, Vector3.down, out hit, upSearch + downMax, ~0, QueryTriggerInteraction.Ignore))
                {
                    Vector3 found = hit.point;
                    if (IsVerticalDeltaAcceptable(playerTransform, found))
                        return found;
                }
            }

#if UNITY_NAVMESH_PICK
            UnityEngine.AI.NavMeshHit navHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(hint, out navHit, 100f, UnityEngine.AI.NavMesh.AllAreas))
            {
                if (IsVerticalDeltaAcceptable(playerTransform, navHit.position))
                    return navHit.position;
            }
#endif

            string[] anchors = new[] { "PlayerStart", "PlayerSpawn", "SpawnPoint", "Berg", "Berg_Location", "LocationAnchor" };
            foreach (var name in anchors)
            {
                try
                {
                    var go = GameObject.Find(name);
                    if (go != null)
                    {
                        var pos = go.transform.position;
                        if (IsVerticalDeltaAcceptable(playerTransform, pos))
                            return pos;
                    }
                }
                catch { }
            }

            float safeY = (playerTransform != null) ? playerTransform.position.y : 0f;
            float clampedY = Mathf.Clamp(hint.y, safeY - 100f, safeY + 100f);
            return new Vector3(hint.x, clampedY, hint.z);
        }
        catch (Exception ex)
        {
            TBLog.Warn("FindSafeLandingBeforeTeleport: " + ex);
            return hint;
        }
    }

    private bool IsVerticalDeltaAcceptable(Transform playerTransform, Vector3 candidate)
    {
        try
        {
            if (playerTransform == null) return true;
            float curY = playerTransform.position.y;
            float delta = Mathf.Abs(candidate.y - curY);
            const float maxVerticalDelta = 100f;
            return delta <= maxVerticalDelta;
        }
        catch { return true; }
    }
}
