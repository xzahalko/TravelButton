using System;
using System.Collections;
using UnityEngine;

public class TeleportService
{
    private static TeleportService _instance;
    public static TeleportService Instance => _instance ?? (_instance = new TeleportService());

    public TeleportMode Mode { get; set; } = TeleportMode.Auto;

    private readonly ITeleportStrategy _oldStrategy = new OldTeleportStrategy();
    private readonly ITeleportStrategy _newStrategy = new NewTeleportStrategy();

    // Public coroutine to place player; does strategy selection + fallback.
    public IEnumerator PlacePlayer(Vector3 target, Action<bool> resultCallback)
    {
        bool moved = false;
        // Decide preferred and fallback
        ITeleportStrategy preferred = _newStrategy;
        ITeleportStrategy fallback = _oldStrategy;

        if (Mode == TeleportMode.Old)
        {
            preferred = _oldStrategy; fallback = _newStrategy;
        }
        else if (Mode == TeleportMode.New)
        {
            preferred = _newStrategy; fallback = _oldStrategy;
        }
        else // Auto: keep new as preferred by default
        {
            preferred = _newStrategy; fallback = _oldStrategy;
        }

        TBLog.Info($"[TeleportService] Mode={Mode}, preferred={preferred.Name}, fallback={fallback.Name}");

        // Try preferred
        bool prefMoved = false;
        yield return TeleportHelpersBehaviour.GetOrCreateHost().StartCoroutine(preferred.PlacePlayer(target, movedFlag => { prefMoved = movedFlag; }));
        TBLog.Info($"[TeleportService] preferred strategy {preferred.Name} moved={prefMoved}");

        if (prefMoved)
        {
            moved = true;
            resultCallback?.Invoke(true);
            yield break;
        }

        // Try fallback strategy if different
        if (fallback != null && fallback != preferred)
        {
            TBLog.Info($"[TeleportService] preferred failed; trying fallback {fallback.Name}");
            bool fallbackMoved = false;
            yield return TeleportHelpersBehaviour.GetOrCreateHost().StartCoroutine(fallback.PlacePlayer(target, movedFlag => { fallbackMoved = movedFlag; }));
            TBLog.Info($"[TeleportService] fallback strategy {fallback.Name} moved={fallbackMoved}");

            if (fallbackMoved)
            {
                resultCallback?.Invoke(true);
                yield break;
            }
        }

        // Final resort: coords-first shim
        TBLog.Info("[TeleportService] both strategies failed; invoking TeleportCompatShims fallback.");
        yield return TeleportHelpersBehaviour.GetOrCreateHost().StartCoroutine(TeleportCompatShims.PlacePlayerViaCoords(target));
        // Best-effort determine movement by sampling before/after isn't available here, so return false but TeleportManager can check the player position if needed.
        resultCallback?.Invoke(false);
    }
}
