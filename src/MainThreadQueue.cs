using System;
using System.Collections.Concurrent;
using UnityEngine;

/// <summary>
/// Simple main-thread queue for enqueuing actions from background threads (e.g. FileSystemWatcher).
/// Creates a persistent GameObject at runtime to host the Update loop and drains the queue each frame.
/// Use MainThreadQueue.Enqueue(() => { ... }) to schedule work on Unity's main thread.
/// </summary>
public class MainThreadQueue : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
    private static bool initialized = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InitOnLoad()
    {
        if (initialized) return;
        try
        {
            var go = new GameObject("TravelButton_MainThreadQueue");
            DontDestroyOnLoad(go);
            go.AddComponent<MainThreadQueue>();
            initialized = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TravelButton] MainThreadQueue initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Enqueue an action to run on the Unity main thread on the next Update cycle.
    /// Safe to call from worker threads.
    /// </summary>
    public static void Enqueue(Action action)
    {
        if (action == null) return;
        queue.Enqueue(action);
    }

    private void Update()
    {
        try
        {
            while (queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogWarning("[TravelButton] Enqueued action failed: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TravelButton] MainThreadQueue.Update exception: " + ex.Message);
        }
    }
}