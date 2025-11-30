using System;
using System.Collections;
using UnityEngine;

public class NewTeleportStrategy : ITeleportStrategy
{
    public string Name => "NewSafePlacePlayerCoroutine";

    public IEnumerator PlacePlayer(Vector3 target, Action<bool> resultCallback)
    {
        TBLog.Info($"[NewTeleportStrategy] invoking PlacePlayerUsingSafeRoutine -> target={target}");
        // PlacePlayerUsingSafeRoutine(finalPos, callback) is your current wrapper that runs SafePlacePlayerCoroutine
        bool moved = false;
        yield return TeleportHelpersBehaviour.GetOrCreateHost().StartCoroutine(PlacePlayerUsingSafeRoutineAdapter(target, movedResult => {
            moved = movedResult;
        }));

        TBLog.Info($"[NewTeleportStrategy] PlacePlayerUsingSafeRoutine returned moved={moved}");
        resultCallback?.Invoke(moved);
    }

    // Adapter to match your existing API – if your PlacePlayerUsingSafeRoutine lives on TeleportManager,
    // move this helper to call the correct static/instance method. Example below calls a global helper StartCoroutine.
    private IEnumerator PlacePlayerUsingSafeRoutineAdapter(Vector3 finalPos, Action<bool> cb)
    {
        // If PlacePlayerUsingSafeRoutine is an instance method on TeleportManager, get its host and call it.
        // Assuming you already have an accessible coroutine wrapper like PlacePlayerUsingSafeRoutine(finalPos, callback)
        var tm = TeleportManager.Instance ?? TeleportHelpersBehaviour.GetOrCreateHost().GetComponent<TeleportManager>();
        if (tm == null) { TBLog.Warn("[NewTeleportStrategy] TeleportManager not available"); cb(false); yield break; }
        yield return tm.StartCoroutine(tm.PlacePlayerUsingSafeRoutineWrapper(finalPos, cb));
    }

}
