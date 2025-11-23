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

    // Reflection bookkeeping so we can unsubscribe later
    private EventInfo sceneLoadedEventInfo;
    private Delegate eventHandlerDelegate;
    private object eventTargetInstance;

    void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
        TrySubscribeToGameSceneLoadedEvent();
        // fallback to Unity event (subscribe once here)
        SceneManager.sceneLoaded += OnUnitySceneLoaded;
    }

    void OnDestroy()
    {
        TryUnsubscribeFromGameSceneLoadedEvent();
        SceneManager.sceneLoaded -= OnUnitySceneLoaded;
    }

    // Try to subscribe to the game's internal SceneLoadedEvent via the event accessor if possible.
    // If no suitable event is found or subscription can't be done, we rely on the Unity SceneManager.sceneLoaded fallback (subscribed in Awake).
    private void TrySubscribeToGameSceneLoadedEvent()
    {
        const string eventNameToFind = "GameSceneLoaded"; // adjust if your target event has a different name

        try
        {
            // Find the EventInfo by name across loaded assemblies (first match)
            var foundEvent = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return new Type[0]; }
                })
                .SelectMany(t => {
                    try { return t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); } catch { return new EventInfo[0]; }
                })
                .FirstOrDefault(e => string.Equals(e.Name, eventNameToFind, StringComparison.Ordinal));

            if (foundEvent == null)
            {
                TBLog.Warn($"SceneLoadHook: event '{eventNameToFind}' not found by name; using SceneManager.sceneLoaded fallback.");
                return;
            }

            // Get add accessor (allow non-public)
            var addMethod = foundEvent.GetAddMethod(true);
            if (addMethod == null)
            {
                TBLog.Warn($"SceneLoadHook: event '{eventNameToFind}' has no add accessor visible; using SceneManager.sceneLoaded fallback.");
                return;
            }

            // Determine target for instance events (null for static add)
            object targetInstance = null;
            if (!addMethod.IsStatic)
            {
                var declaringType = foundEvent.DeclaringType;
                // prefer a UnityEngine.Object derived instance in scene
                if (typeof(UnityEngine.Object).IsAssignableFrom(declaringType))
                {
                    try
                    {
                        var findMethod = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "FindObjectOfType" && m.IsGenericMethod && m.GetParameters().Length == 0);
                        if (findMethod != null)
                        {
                            var generic = findMethod.MakeGenericMethod(declaringType);
                            targetInstance = generic.Invoke(null, null);
                        }
                    }
                    catch { targetInstance = null; }
                }

                // try static Instance property
                if (targetInstance == null)
                {
                    try
                    {
                        var instProp = declaringType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (instProp != null) targetInstance = instProp.GetValue(null);
                    }
                    catch { }
                }

                // as last resort try FindObjectsOfType and pick first
                if (targetInstance == null)
                {
                    try
                    {
                        var findAll = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "FindObjectsOfType" && m.IsGenericMethod && m.GetParameters().Length == 0);
                        if (findAll != null)
                        {
                            var gen = findAll.MakeGenericMethod(declaringType);
                            var arr = gen.Invoke(null, null) as System.Array;
                            if (arr != null && arr.Length > 0) targetInstance = arr.GetValue(0);
                        }
                    }
                    catch { targetInstance = null; }
                }

                if (targetInstance == null)
                {
                    TBLog.Warn($"SceneLoadHook: could not locate an instance of '{foundEvent.DeclaringType?.FullName}' required to subscribe to '{eventNameToFind}'; using SceneManager.sceneLoaded fallback.");
                    return;
                }
            }

            // Find a handler method on this class that is compatible with the event's delegate type.
            var handlerType = foundEvent.EventHandlerType;
            MethodInfo handlerMethod = null;

            // Try to find a method named "OnGameSceneLoaded" whose signature matches the event delegate
            // We'll search methods on this class and test compatibility by attempting to create a delegate.
            var candidateMethods = this.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.Name == "OnGameSceneLoaded").ToArray();

            Delegate del = null;
            foreach (var m in candidateMethods)
            {
                try
                {
                    del = Delegate.CreateDelegate(handlerType, this, m, false);
                    if (del != null)
                    {
                        handlerMethod = m;
                        break;
                    }
                }
                catch
                {
                    del = null;
                }
            }

            // If we couldn't find a compatible OnGameSceneLoaded, try a no-arg method and create delegate if event type is Action/Action-like
            if (del == null)
            {
                var noArg = this.GetType().GetMethod("OnGameSceneLoaded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (noArg != null)
                {
                    try
                    {
                        // If the event handler type is parameterless Action or System.Action, bind it.
                        if (handlerType == typeof(Action) || handlerType == typeof(System.Action))
                        {
                            del = Delegate.CreateDelegate(handlerType, this, noArg);
                            handlerMethod = noArg;
                        }
                    }
                    catch { del = null; }
                }
            }

            if (del == null)
            {
                TBLog.Warn("SceneLoadHook: no compatible OnGameSceneLoaded handler found; using SceneManager.sceneLoaded fallback.");
                return;
            }

            // Invoke the add accessor to attach our delegate
            try
            {
                addMethod.Invoke(addMethod.IsStatic ? null : targetInstance, new object[] { del });

                // store for later unsubscription
                this.sceneLoadedEventInfo = foundEvent;
                this.eventHandlerDelegate = del;
                this.eventTargetInstance = addMethod.IsStatic ? null : targetInstance;

                TBLog.Info($"SceneLoadHook: successfully subscribed to '{foundEvent.Name}' on '{foundEvent.DeclaringType?.FullName}'");
                return;
            }
            catch (Exception exAttach)
            {
                TBLog.Warn($"SceneLoadHook: failed to attach handler to event '{foundEvent.Name}': {exAttach.Message}; using SceneManager.sceneLoaded fallback.");
                // don't try to add fallback here; Awake already subscribes to SceneManager.sceneLoaded
                return;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"SceneLoadHook: unexpected exception while subscribing to game event: {ex.Message}; using SceneManager.sceneLoaded fallback.");
            return;
        }
    }

    private void TryUnsubscribeFromGameSceneLoadedEvent()
    {
        try
        {
            if (sceneLoadedEventInfo != null && eventHandlerDelegate != null)
            {
                try
                {
                    sceneLoadedEventInfo.RemoveEventHandler(eventTargetInstance, eventHandlerDelegate);
                }
                catch
                {
                    // attempt to use remove accessor
                    var remove = sceneLoadedEventInfo.GetRemoveMethod(true);
                    if (remove != null)
                    {
                        try { remove.Invoke(eventTargetInstance, new object[] { eventHandlerDelegate }); } catch { }
                    }
                }
                eventHandlerDelegate = null;
                sceneLoadedEventInfo = null;
                eventTargetInstance = null;
            }
        }
        catch { }
    }

    // Invoked by game internal event (if compatible)
    // You may have existing overloads named OnGameSceneLoaded; keep them as needed. This code prefers any OnGameSceneLoaded method on this type that can be bound to the game's event.
    private void OnGameSceneLoaded()
    {
        // start coroutine on Unity main thread
        StartCoroutine(DelayedStabilizeCoroutine());
    }

    // Unity SceneManager fallback - single definition only
    private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StartCoroutine(DelayedStabilizeCoroutine());
    }

    // Adapter method invoked from fallback or from reflected event handler if necessary
    private void OnGameSceneLoadedFallback(string sceneName)
    {
        TBLog.Info("SceneLoadHook: OnGameSceneLoadedFallback called for scene: " + sceneName);
        // ... your existing code here ...
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

        // small delay to let spawners/SideLoader finish their work
        yield return new UnityEngine.WaitForSeconds(2.0f);

        // It waits a short delay and then tries (by reflection) to invoke TravelButton's detection routine for the active scene.
        // This is defensive and logs failure cases; it won't crash if TravelButton is absent/different.
        try
        {
            var tbType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name == "TravelButton");
            if (tbType == null)
            {
                UnityEngine.Debug.Log("[REDETECT] TravelButton type not found");
                yield break;
            }

            var detectMethod = tbType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name.IndexOf("DetectAndPersistVariants", System.StringComparison.OrdinalIgnoreCase) >= 0
                                  || m.Name.IndexOf("DetectVariants", System.StringComparison.OrdinalIgnoreCase) >= 0
                                  || m.Name.IndexOf("Detect", System.StringComparison.OrdinalIgnoreCase) >= 0 && m.GetParameters().Length <= 1);
            if (detectMethod == null)
            {
                UnityEngine.Debug.Log("[REDETECT] TravelButton detection method not found by reflection.");
                yield break;
            }

            // prepare args if possible
            object[] args = null;
            var parameters = detectMethod.GetParameters();
            var sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            if (parameters.Length == 0) args = new object[] { };
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string)) args = new object[] { sceneName };
            else
            {
                // fallback: try to pass the city object from TravelButton.Cities if param expects that type
                object citiesObj = tbType.GetField("Cities", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null)
                                 ?? tbType.GetProperty("Cities", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                if (citiesObj != null)
                {
                    object cityObj = null;
                    foreach (var c in (System.Collections.IEnumerable)citiesObj)
                    {
                        if (c == null) continue;
                        var ct = c.GetType();
                        var sname = (ct.GetField("sceneName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(c) as string)
                                 ?? (ct.GetProperty("sceneName")?.GetValue(c) as string);
                        if (!string.IsNullOrEmpty(sname) && string.Equals(sname, sceneName, System.StringComparison.OrdinalIgnoreCase)) { cityObj = c; break; }
                    }
                    if (cityObj != null && parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(cityObj.GetType()))
                        args = new object[] { cityObj };
                }
            }

            // If method is instance-based, try to find a TravelButton instance
            object target = null;
            if (!detectMethod.IsStatic)
            {
                try
                {
                    var findAll = typeof(UnityEngine.Object).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "FindObjectsOfType" && m.IsGenericMethod && m.GetParameters().Length == 0);
                    if (findAll != null)
                    {
                        var gen = findAll.MakeGenericMethod(tbType);
                        var arr = gen.Invoke(null, null) as System.Array;
                        if (arr != null && arr.Length > 0) target = arr.GetValue(0);
                    }
                }
                catch { target = null; }
            }

            if (args != null)
            {
                try
                {
                    detectMethod.Invoke(target, args);
                    UnityEngine.Debug.Log("[REDETECT] TravelButton detection invoked (method: " + detectMethod.Name + ")");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[REDETECT] failed to invoke TravelButton detection: " + ex);
                }
            }
            else
            {
                UnityEngine.Debug.Log("[REDETECT] Detection method found but could not assemble compatible args; method=" + detectMethod.Name);
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[REDETECT] error during re-detection attempt: " + ex);
        }

        yield return null;
    }
}