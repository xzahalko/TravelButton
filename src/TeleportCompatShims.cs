using System;
using System.Collections;
using UnityEngine;

/// Minimal compatibility shim: safe, compact placement by coordinates.
/// - Finds a likely player transform (tag 'Player', name containing 'PlayerChar', CharacterController).
/// - Temporarily disables CharacterController / sets Rigidbody kinematic, sets position,
///   performs one small overlap check + iterative raise to avoid embedding, then restores.
/// - Intended as a small, low-risk fallback to regain coords-based teleports quickly.
public static class TeleportCompatShims
{
    private const float OVERLAP_CHECK_RADIUS = 0.45f;
    private const float OVERLAP_RAISE_STEP = 0.25f;
    private const float OVERLAP_MAX_RAISE = 3.0f;

    public static IEnumerator PlacePlayerViaCoords(Vector3 target)
    {
        // Find candidate transform
        Transform playerTransform = FindPlayerTransform();
        if (playerTransform == null)
        {
            TBLog.Warn("PlacePlayerViaCoords: could not find player transform.");
            yield break;
        }

        TBLog.Info($"PlacePlayerViaCoords: attempting to place player '{playerTransform.name}' to {target}");

        // Remember component states
        CharacterController cc = null;
        Rigidbody rb = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;

        try
        {
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
            rb = playerTransform.GetComponentInChildren<Rigidbody>(true);
            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;
        }
        catch { /* ignore */ }

        // Disable controllers / physics
        try
        {
            if (cc != null) cc.enabled = false;
            if (rb != null)
            {
                rb.isKinematic = true;
                try { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerViaCoords: disabling components failed: " + ex);
        }

        // Apply immediate position
        try
        {
            playerTransform.position = target;
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerViaCoords: setting position failed: " + ex);
        }

        // Small overlap check and iterative raise
        try
        {
            bool foundFree = false;
            int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
            for (int step = 0; step <= maxSteps; step++)
            {
                Vector3 checkPos = playerTransform.position + Vector3.up * (step * OVERLAP_RAISE_STEP);
                Vector3 overlapCenter = checkPos + Vector3.up * 0.5f;
                Collider[] hits = Physics.OverlapSphere(overlapCenter, OVERLAP_CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);

                bool overlapping = false;
                if (hits != null && hits.Length > 0)
                {
                    foreach (var h in hits)
                    {
                        if (h == null || h.transform == null) continue;
                        if (h.transform.IsChildOf(playerTransform)) continue; // skip self
                        overlapping = true;
                        break;
                    }
                }

                if (!overlapping)
                {
                    if (step > 0) playerTransform.position = checkPos;
                    foundFree = true;
                    if (step > 0) TBLog.Info($"PlacePlayerViaCoords: raised by {step * OVERLAP_RAISE_STEP:F2}m to avoid overlap -> {playerTransform.position}");
                    break;
                }
            }

            if (!foundFree)
            {
                TBLog.Warn($"PlacePlayerViaCoords: could not find non-overlapping spot within {OVERLAP_MAX_RAISE}m above target.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerViaCoords: overlap/raise check failed: " + ex);
        }

        // Wait a frame or two to let physics/scripts settle
        yield return null;
        yield return null;

        // Restore components
        try
        {
            if (rb != null)
            {
                try
                {
                    rb.isKinematic = rbWasKinematic;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                catch { }
            }
            if (cc != null) cc.enabled = ccWasEnabled;
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerViaCoords: restoring components failed: " + ex);
        }

        TBLog.Info($"PlacePlayerViaCoords: finished placement at {playerTransform.position} for '{playerTransform.name}'");
        yield break;
    }

    private static Transform FindPlayerTransform()
    {
        // 1) try tag
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go != null && go.transform != null) return go.transform;
        }
        catch { }

        // 2) name pattern
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                var n = t.name;
                if (n.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase) || n.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return t;
                }
            }
        }
        catch { }

        // 3) CharacterController presence
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<CharacterController>();
            foreach (var c in all)
            {
                if (c == null || c.transform == null) continue;
                return c.transform;
            }
        }
        catch { }

        return null;
    }
}
