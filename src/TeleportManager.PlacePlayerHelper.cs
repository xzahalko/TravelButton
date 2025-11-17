using System;
using System.Collections;
using UnityEngine;

public partial class TeleportManager : MonoBehaviour
{
    /// <summary>
    /// Helper coroutine that calls SafePlacePlayerCoroutine and reports
    /// whether the player actually moved via callback.
    /// Used by attempt loops to determine placement success.
    /// </summary>
    public IEnumerator PlacePlayerUsingSafeRoutine(Vector3 finalTarget, Action<bool> onComplete)
    {
        TBLog.Info($"PlacePlayerUsingSafeRoutine: starting placement at {finalTarget}");

        Vector3 beforePos = Vector3.zero;
        bool haveBeforePos = false;

        // Capture before position
        try
        {
            var playerRoot = TeleportHelpers.FindPlayerRoot();
            if (playerRoot != null)
            {
                beforePos = playerRoot.transform.position;
                haveBeforePos = true;
                TBLog.Info($"PlacePlayerUsingSafeRoutine: before pos = {beforePos}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: error getting before position: " + ex);
        }

        // Yield to SafePlacePlayerCoroutine
        yield return StartCoroutine(SafePlacePlayerCoroutine(finalTarget));

        // Capture after position
        Vector3 afterPos = Vector3.zero;
        bool haveAfterPos = false;
        try
        {
            var playerRoot = TeleportHelpers.FindPlayerRoot();
            if (playerRoot != null)
            {
                afterPos = playerRoot.transform.position;
                haveAfterPos = true;
                TBLog.Info($"PlacePlayerUsingSafeRoutine: after pos = {afterPos}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: error getting after position: " + ex);
        }

        // Determine if player moved
        bool moved = false;
        if (haveBeforePos && haveAfterPos)
        {
            float distSq = (afterPos - beforePos).sqrMagnitude;
            moved = distSq > 0.01f;
            TBLog.Info($"PlacePlayerUsingSafeRoutine: moved={moved} (distSq={distSq:F3})");
        }
        else if (haveAfterPos && !haveBeforePos)
        {
            // If we couldn't get before but can after, assume success
            moved = true;
            TBLog.Info("PlacePlayerUsingSafeRoutine: no before pos, assuming moved=true");
        }

        // Invoke callback
        try
        {
            onComplete?.Invoke(moved);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlacePlayerUsingSafeRoutine: callback threw: " + ex);
        }

        yield break;
    }
}
