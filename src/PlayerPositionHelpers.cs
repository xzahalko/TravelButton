using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small helper methods to nudge the player's world position by fixed offsets.
/// This file adds optional safe-teleport invocation after the transform move,
/// and provides a built-in fallback safe placement coroutine that targets the
/// actual player transform (to avoid shims picking the wrong GameObject).
/// </summary>
public static class PlayerPositionHelpers
{
    public static bool MovePlayerByX5(bool safeTeleportAfter = false) => MovePlayerBy(new Vector3(5f, 0f, 0f), safeTeleportAfter);
    public static bool MovePlayerByY5(bool safeTeleportAfter = false) => MovePlayerBy(new Vector3(0f, 5f, 0f), safeTeleportAfter);
    public static bool MovePlayerByZ5(bool safeTeleportAfter = false) => MovePlayerBy(new Vector3(0f, 0f, 5f), safeTeleportAfter);

    private static bool MovePlayerBy(Vector3 delta, bool safeTeleportAfter)
    {
        try
        {
            var t = TryGetPlayerTransform();
            if (t == null)
            {
                TBLog.Warn($"PlayerPositionHelpers: could not find player transform to move by {delta}.");
                return false;
            }

            Vector3 before = t.position;
            Vector3 after = before + delta;

            TBLog.Info($"PlayerPositionHelpers: moving player '{t.name}' by {delta} (before={before}, after={after})");

            // Direct transform set (must be on Unity main thread)
            t.position = after;

            // Confirm new pos
            try { TBLog.Info($"PlayerPositionHelpers: move applied. currentPos={t.position}"); } catch { }

            if (!safeTeleportAfter) return true;

            // --- try to finalize placement using preferred systems ---
            try
            {
                // 1) Preferred: TeleportManager.Instance
                var tm = TeleportManager.Instance;
                if (tm != null)
                {
                    TBLog.Info("PlayerPositionHelpers: invoking TeleportManager.PlacePlayerUsingSafeRoutine (via Instance).");
                    tm.StartCoroutine(tm.PlacePlayerUsingSafeRoutine(after, moved =>
                    {
                        try { TBLog.Info($"PlayerPositionHelpers: TeleportManager.PlacePlayerUsingSafeRoutine completed moved={moved}"); } catch { }
                    }));
                    return true;
                }

                // 2) Fallback: Find existing TeleportManager component
                var tmObj = UnityEngine.Object.FindObjectOfType<TeleportManager>();
                if (tmObj != null)
                {
                    TBLog.Info("PlayerPositionHelpers: invoking TeleportManager.PlacePlayerUsingSafeRoutine (via FindObjectOfType fallback).");
                    tmObj.StartCoroutine(tmObj.PlacePlayerUsingSafeRoutine(after, moved =>
                    {
                        try { TBLog.Info($"PlayerPositionHelpers: TeleportManager (found) PlacePlayerUsingSafeRoutine completed moved={moved}"); } catch { }
                    }));
                    return true;
                }

                // 3) Last-resort: run internal fallback safe-placement coroutine on a host MonoBehaviour,
                //    explicitly targeting the player transform (avoids shims picking random objects).
                try
                {
                    var host = TeleportHelpersBehaviour.GetOrCreateHost();
                    if (host != null)
                    {
                        TBLog.Info("PlayerPositionHelpers: TeleportManager not found; starting internal fallback safe-placement on host.");
                        host.StartCoroutine(FallbackSafePlaceCoroutine(TryGetPlayerTransform(), after));
                        return true;
                    }
                    else
                    {
                        TBLog.Info("PlayerPositionHelpers: no host available to start fallback coroutine.");
                    }
                }
                catch (Exception exShim)
                {
                    TBLog.Warn("PlayerPositionHelpers: fallback shim invocation threw: " + exShim.ToString());
                }

                TBLog.Info("PlayerPositionHelpers: could not start any safe-placement coroutine; transform was still set.");
            }
            catch (Exception ex)
            {
                TBLog.Warn("PlayerPositionHelpers: failed to start safe-teleport coroutine: " + ex.ToString());
            }

            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers: exception while moving player: " + ex.ToString());
            return false;
        }
    }

    // Fallback coroutine: run on a host MonoBehaviour; performs a simple safe placement targeted at the player
    // - disables CharacterController and Rigidbody (best-effort)
    // - sets transform.position
    // - performs small overlap-raise steps
    // - waits a couple frames and restores components
    private static IEnumerator FallbackSafePlaceCoroutine(Transform playerTransform, Vector3 targetPos)
    {
        TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: enter targetPos={targetPos}, playerTransform={(playerTransform != null ? playerTransform.name : "<null>")}");
        if (playerTransform == null)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: playerTransform null; aborting fallback routine.");
            yield break;
        }

        // save state
        CharacterController cc = null;
        Rigidbody rb = null;
        bool ccWasEnabled = false;
        bool rbWasKinematic = false;

        try
        {
            cc = playerTransform.GetComponentInChildren<CharacterController>(true);
            var rbcands = playerTransform.GetComponentsInChildren<Rigidbody>(true);
            if (rbcands != null && rbcands.Length > 0) rb = rbcands[0];

            ccWasEnabled = cc != null ? cc.enabled : false;
            rbWasKinematic = rb != null ? rb.isKinematic : false;

            TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: cc={(cc != null ? "YES" : "NO")} ccWasEnabled={ccWasEnabled}, rb={(rb != null ? "YES" : "NO")} rbWasKinematic={rbWasKinematic}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: error reading components: " + ex.ToString());
        }

        // disable
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
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: error disabling components: " + ex.ToString());
        }

        // set position
        try
        {
            playerTransform.position = targetPos;
            TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: set player position to {playerTransform.position}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: set position failed: " + ex.ToString());
        }

        // small overlap-raise
        try
        {
            const float OVERLAP_RAISE_STEP = 0.25f;
            const float OVERLAP_MAX_RAISE = 2.0f;
            const float OVERLAP_CHECK_RADIUS = 0.5f;

            int maxSteps = Mathf.CeilToInt(OVERLAP_MAX_RAISE / OVERLAP_RAISE_STEP);
            bool fixedUp = false;
            for (int i = 0; i <= maxSteps; i++)
            {
                Vector3 checkPos = playerTransform.position + Vector3.up * (i * OVERLAP_RAISE_STEP);
                Collider[] hits = Physics.OverlapSphere(checkPos + Vector3.up * 0.5f, OVERLAP_CHECK_RADIUS, ~0, QueryTriggerInteraction.Ignore);
                bool overlapping = false;
                if (hits != null && hits.Length > 0)
                {
                    foreach (var h in hits)
                    {
                        if (h == null || h.transform == null) continue;
                        if (h.transform.IsChildOf(playerTransform)) continue;
                        overlapping = true;
                        break;
                    }
                }

                TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: overlap step {i}, hits={(hits?.Length ?? 0)}, overlapping={overlapping}, checkPos={checkPos}");

                if (!overlapping)
                {
                    if (i > 0)
                    {
                        playerTransform.position = checkPos;
                        TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: raised player to {playerTransform.position}");
                    }
                    fixedUp = true;
                    break;
                }
            }

            if (!fixedUp)
            {
                TBLog.Warn($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: could not find non-overlapping spot within {OVERLAP_MAX_RAISE}m above {playerTransform.position}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: overlap/raise failed: " + ex.ToString());
        }

        // wait a few frames
        yield return null;
        yield return null;

        // restore
        try
        {
            if (rb != null)
            {
                try { rb.isKinematic = rbWasKinematic; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
            }
            if (cc != null) cc.enabled = ccWasEnabled;
            TBLog.Info($"PlayerPositionHelpers.FallbackSafePlaceCoroutine: restored components; final pos={playerTransform.position}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers.FallbackSafePlaceCoroutine: restore failed: " + ex.ToString());
        }

        yield break;
    }

    private static Transform TryGetPlayerTransform()
    {
        try
        {
            var go = GameObject.FindWithTag("Player");
            if (go != null) return go.transform;

            foreach (var g in GameObject.FindObjectsOfType<GameObject>())
            {
                if (g == null || string.IsNullOrEmpty(g.name)) continue;
                if (g.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                    return g.transform;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlayerPositionHelpers: error while locating player transform: " + ex.ToString());
        }

        return null;
    }
}