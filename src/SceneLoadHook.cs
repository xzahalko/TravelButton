using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

// Place this class in your plugin project. Make sure the assembly compiles into your plugin DLL.
// Usage: if TravelButton (or your plugin) initiates the scene load, set SceneLoadHook.LastRequestedPlacement = correctedCoords before you call the scene load.
public class SceneLoadHook : MonoBehaviour
{
    // Optional: set this before requesting the scene load so the stabilizer knows where the player will be placed.
    public static Vector3? LastRequestedPlacement = null;

    private EventInfo sceneLoadedEventInfo;
    private FieldInfo reinputBackingField;
    private Delegate eventHandlerDelegate;

    void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
        TrySubscribeToGameSceneLoadedEvent();
        // fallback to Unity event also (safe, may trigger twice but stabilizer is idempotent-ish)
        SceneManager.sceneLoaded += OnUnitySceneLoaded;
    }

    void OnDestroy()
    {
        TryUnsubscribeFromGameSceneLoadedEvent();
        SceneManager.sceneLoaded -= OnUnitySceneLoaded;
    }

    // Try to subscribe to the game's internal SceneLoadedEvent via the event accessor if possible,
    // otherwise subscribe directly to the ReInput backing field (obfuscated name), otherwise fall back to SceneManager.sceneLoaded.
    private void TrySubscribeToGameSceneLoadedEvent()
    {
        try
        {
            // 1) Search for a type that declares an event named "SceneLoadedEvent"
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    var ei = t.GetEvent("SceneLoadedEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (ei != null)
                    {
                        sceneLoadedEventInfo = ei;
                        // Create a delegate of the event handler type (expected to be Action)
                        var handlerType = ei.EventHandlerType ?? typeof(Action);
                        eventHandlerDelegate = Delegate.CreateDelegate(handlerType, this, nameof(OnGameSceneLoaded), false);
                        if (eventHandlerDelegate == null)
                        {
                            // fallback: create an Action and convert (works if signature compatible)
                            eventHandlerDelegate = new Action(OnGameSceneLoaded);
                        }
                        sceneLoadedEventInfo.AddEventHandler(null, eventHandlerDelegate);
                        Debug.Log("[SceneLoadHook] Subscribed to SceneLoadedEvent via event accessor on type: " + t.FullName);
                        return;
                    }
                }
            }

            // 2) Fallback: find ReInput and the obfuscated backing field by name
            var reInputType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(tt => string.Equals(tt.Name, "ReInput", StringComparison.OrdinalIgnoreCase) || tt.FullName?.IndexOf("ReInput") >= 0);
            if (reInputType != null)
            {
                reinputBackingField = reInputType.GetField("ikHxmghZXdNsIJvkwwyccBXRpPd", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (reinputBackingField != null && reinputBackingField.FieldType == typeof(Action))
                {
                    var old = (Action)reinputBackingField.GetValue(null);
                    Action newHandler = OnGameSceneLoaded;
                    var combined = (Action)Delegate.Combine(old, newHandler);
                    reinputBackingField.SetValue(null, combined);
                    Debug.Log("[SceneLoadHook] Subscribed to ReInput backing field directly (ik...).");
                    return;
                }
            }

            Debug.LogWarning("[SceneLoadHook] Could not find game SceneLoadedEvent or ReInput backing field; relying on SceneManager.sceneLoaded only.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneLoadHook] TrySubscribeToGameSceneLoadedEvent exception: " + ex);
        }
    }

    private void TryUnsubscribeFromGameSceneLoadedEvent()
    {
        try
        {
            if (sceneLoadedEventInfo != null && eventHandlerDelegate != null)
            {
                sceneLoadedEventInfo.RemoveEventHandler(null, eventHandlerDelegate);
                eventHandlerDelegate = null;
            }
            if (reinputBackingField != null)
            {
                var old = (Action)reinputBackingField.GetValue(null);
                var removed = (Action)Delegate.Remove(old, new Action(OnGameSceneLoaded));
                reinputBackingField.SetValue(null, removed);
            }
        }
        catch { }
    }

    // Invoked by game internal event (no parameters)
    private void OnGameSceneLoaded()
    {
        // start coroutine on Unity main thread
        StartCoroutine(DelayedStabilizeCoroutine());
    }

    // Unity SceneManager fallback
    private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StartCoroutine(DelayedStabilizeCoroutine());
    }

    private IEnumerator DelayedStabilizeCoroutine()
    {
        // allow Awake/Start/OnEnable to run
        yield return null;
        yield return null;

        // Determine the placement point to use for FX clearing.
        Vector3 placement = Vector3.zero;
        if (LastRequestedPlacement.HasValue)
        {
            placement = LastRequestedPlacement.Value;
        }
        else
        {
            // fallback: try to find local player by common names/tags used in this project
            var playerObj = GameObject.FindWithTag("Player") ?? GameObject.Find("PlayerChar zhR2yix65UGintN787WPZg_zhR2yix65UGintN787WPZg");
            if (playerObj != null) placement = playerObj.transform.position;
            else
            {
                var roots = SceneManager.GetActiveScene().GetRootGameObjects();
                if (roots != null && roots.Length > 0) placement = roots[0].transform.position;
            }
        }

        try
        {
            var summary = SceneStabilizer.StabilizeSceneBeforePlacement(SceneManager.GetActiveScene(), placement, 20f);
            Debug.Log("[SceneLoadHook] SceneStabilizer: " + summary);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneLoadHook] Stabilizer error: " + ex);
        }

        yield return null;
    }
}