using System;
using System.Collections;
using UnityEngine;

public partial class TeleportManager : MonoBehaviour
{
    /// <summary>
    /// Helper coroutine that wraps SafePlacePlayerCoroutine and invokes a callback with success status.
    /// Snapshots player position before and after placement to determine if movement occurred.
    /// </summary>
    /// <param name="finalTarget">Target position for player placement</param>
    /// <param name="onComplete">Callback invoked with true if player moved, false otherwise</param>
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 finalTarget, Action<bool> onComplete)
    {
        TBLog.Info($"PlacePlayerUsingSafeRoutine: start for finalTarget={finalTarget}");

        // Snapshot player position before placement
        Vector3 beforePos = Vector3.zero;
        bool haveBeforePos = false;
        try
        {
            Transform playerTransform = null;
            var go = GameObject.FindWithTag("Player");
            if (go != null)
            {
                playerTransform = go.transform;
            }
            else
            {
                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                    {
                        playerTransform = g.transform;
                        break;
                    }
                }
            }

            if (playerTransform != null)
            {
                beforePos = playerTransform.position;
                haveBeforePos = true;
                TBLog.Info($"PlacePlayerUsingSafeRoutine: beforePos={beforePos}");
            }
            else
            {
                TBLog.Warn("PlacePlayerUsingSafeRoutine: could not find player transform for before position");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: error getting before position: " + ex);
        }

        // Delegate to SafePlacePlayerCoroutine
        yield return StartCoroutine(SafePlacePlayerCoroutine(finalTarget));

        // Snapshot player position after placement
        Vector3 afterPos = Vector3.zero;
        bool haveAfterPos = false;
        try
        {
            Transform playerTransform = null;
            var go = GameObject.FindWithTag("Player");
            if (go != null)
            {
                playerTransform = go.transform;
            }
            else
            {
                foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                    {
                        playerTransform = g.transform;
                        break;
                    }
                }
            }

            if (playerTransform != null)
            {
                afterPos = playerTransform.position;
                haveAfterPos = true;
                TBLog.Info($"PlacePlayerUsingSafeRoutine: afterPos={afterPos}");
            }
            else
            {
                TBLog.Warn("PlacePlayerUsingSafeRoutine: could not find player transform for after position");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: error getting after position: " + ex);
        }

        // Determine if movement occurred
        bool moved = false;
        if (haveBeforePos && haveAfterPos)
        {
            float distanceSq = (afterPos - beforePos).sqrMagnitude;
            moved = distanceSq > 0.01f; // threshold: 0.1m
            TBLog.Info($"PlacePlayerUsingSafeRoutine: moved={moved} (distanceSq={distanceSq:F4})");
        }
        else if (haveAfterPos && !haveBeforePos)
        {
            // If we couldn't get before position but can get after, assume success
            moved = true;
            TBLog.Info("PlacePlayerUsingSafeRoutine: assuming success (have after pos but not before)");
        }
        else
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: cannot determine if moved (missing position data)");
        }

        // Invoke callback
        try
        {
            onComplete?.Invoke(moved);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: callback threw exception: " + ex);
        }

        TBLog.Info($"PlacePlayerUsingSafeRoutine: complete (moved={moved})");
        yield break;
    }
}
