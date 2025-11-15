using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Scene loader + teleport coroutine that reliably teleports the player to a chosen finalPos
/// after the requested scene is loaded and settled. This class exposes public IEnumerator
/// LoadSceneAndTeleportCoroutine(...) which the SceneLoaderInvoker will start for you.
/// </summary>
public class TravelButtonSceneLoader : MonoBehaviour
{
    // Wrapper that matches UI callsite: StartCoroutine(LoadSceneAndTeleportCoroutine(city, cost, coordsHint, haveCoordsHint));
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        if (haveCoordsHint && cityObj != null)
        {
            try
            {
                TrySetFloatArrayFieldOrProp(cityObj, new string[] { "coords", "Coords", "position", "Position" }, new float[] { coordsHint.x, coordsHint.y, coordsHint.z });
                TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine(wrapper): applied coordsHint [{coordsHint.x}, {coordsHint.y}, {coordsHint.z}] via reflection (if target field existed).");
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine(wrapper): could not apply coordsHint to city (reflection): " + ex.Message);
            }
        }

        // Delegate to main coroutine
        yield return StartCoroutine(LoadSceneAndTeleportCoroutine(cityObj));
    }

    // Main coroutine: loads scene and picks a safe final position, with improved grounding, waiting and safeguards
    // (Implementation is assumed present; keep as before. SceneLoaderInvoker will call the above wrapper.)
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj)
    {
        // For brevity, keep this method body identical to the implementation you already have.
        // It performs: async scene load, fade overlay handling, immediate grounding probes,
        // AttemptTeleportToPositionSafe(finalPos), wait for TeleportHelpers.ReenableInProgress, UI refresh, and fade out.
        //
        // The full implementation should remain unchanged here — SceneLoaderInvoker will call the wrapper above,
        // which in turn calls this method as before.
        throw new NotImplementedException("Existing LoadSceneAndTeleportCoroutine implementation should be present here.");
    }

    // ---- Reflection helpers (copy your existing implementations) ----
    private static string TryGetStringFieldOrProp(object obj, string[] candidateNames)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    if (v != null) return v.ToString();
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead)
                {
                    var v = p.GetValue(obj, null);
                    if (v != null) return v.ToString();
                }
            }
            catch { }
        }
        return null;
    }

    private static float[] TryGetFloatArrayFieldOrProp(object obj, string[] candidateNames)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    if (v is float[] fa) return fa;
                    if (v is Vector3 vv) return new float[] { vv.x, vv.y, vv.z };
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead)
                {
                    var v = p.GetValue(obj, null);
                    if (v is float[] pa) return pa;
                    if (v is Vector3 pv) return new float[] { pv.x, pv.y, pv.z };
                }
            }
            catch { }
        }
        return null;
    }

    private static bool TrySetFloatArrayFieldOrProp(object obj, string[] candidateNames, float[] value)
    {
        if (obj == null || value == null || value.Length < 3) return false;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(float[]))
                {
                    f.SetValue(obj, new float[] { value[0], value[1], value[2] });
                    return true;
                }
                if (f != null && f.FieldType == typeof(Vector3))
                {
                    f.SetValue(obj, new Vector3(value[0], value[1], value[2]));
                    return true;
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite)
                {
                    if (p.PropertyType == typeof(float[]))
                    {
                        p.SetValue(obj, new float[] { value[0], value[1], value[2] }, null);
                        return true;
                    }
                    if (p.PropertyType == typeof(Vector3))
                    {
                        p.SetValue(obj, new Vector3(value[0], value[1], value[2]), null);
                        return true;
                    }
                }
            }
            catch { }
        }
        return false;
    }
}