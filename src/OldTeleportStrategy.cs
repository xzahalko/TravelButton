using System;
using System.Collections;
using UnityEngine;

public class OldTeleportStrategy : ITeleportStrategy
{
    public string Name => "OldAttemptTeleportToPositionSafe";

    public IEnumerator PlacePlayer(Vector3 target, Action<bool> resultCallback)
    {
        TBLog.Info($"[OldTeleportStrategy] invoking AttemptTeleportToPositionSafe -> target={target}");
        // TeleportHelpers.AttemptTeleportToPositionSafe is the restored method from cb10ef3
        // It has signature IEnumerator AttemptTeleportToPositionSafe(Vector3 target, Action<bool> resultCallback)
        var host = TeleportHelpersBehaviour.GetOrCreateHost();
        yield return host.StartCoroutine(TeleportHelpers.AttemptTeleportToPositionSafe(target, moved => {
            TBLog.Info($"[OldTeleportStrategy] AttemptTeleportToPositionSafe returned moved={moved}");
            resultCallback?.Invoke(moved);
        }));
    }
}
