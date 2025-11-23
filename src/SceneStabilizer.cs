using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SceneStabilizer
{
    // Disable MonoBehaviour components whose type name contains any of the provided keywords.
    // Returns number disabled.
    public static int DisableSpawnersByKeywordsInScene(Scene scene, IEnumerable<string> keywords)
    {
        int disabled = 0;
        try
        {
            var roots = scene.GetRootGameObjects();
            var kwArray = keywords.Select(k => k ?? string.Empty).ToArray();
            foreach (var root in roots)
            {
                foreach (var mb in root.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    string tn = mb.GetType().Name;
                    foreach (var kw in kwArray)
                    {
                        if (string.IsNullOrEmpty(kw)) continue;
                        if (tn.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                if (mb is Behaviour b && b.enabled)
                                {
                                    b.enabled = false;
                                    disabled++;
                                }
                                else
                                {
                                    // try disabling as GameObject fallback (rare)
                                    mb.gameObject.SetActive(false);
                                    disabled++;
                                }
                            }
                            catch { }
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneStabilizer] DisableSpawnersByKeywordsInScene exception: " + ex);
        }
        return disabled;
    }

    // Stop/clear particle-like and VFX-like components within radius around a position.
    // Returns number of root GameObjects attempted.
    public static int StopNearbyFXAroundPositionInScene(Scene scene, Vector3 pos, float radiusMeters = 20f)
    {
        int attempted = 0;
        float radiusSqr = radiusMeters * radiusMeters;
        try
        {
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                foreach (var child in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = child.gameObject;
                    try
                    {
                        if ((go.transform.position - pos).sqrMagnitude > radiusSqr) continue;

                        bool sawFx = false;

                        // ParticleSystem quick handling
                        foreach (var ps in go.GetComponents<ParticleSystem>())
                        {
                            try
                            {
                                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                sawFx = true;
                            }
                            catch { }
                        }

                        // Generic reflection-based handling of other types named like VFX/Fire
                        foreach (var comp in go.GetComponents(typeof(Component)))
                        {
                            if (comp == null) continue;
                            string tn = comp.GetType().Name;
                            if (tn.IndexOf("VFX", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0
                                || tn.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                sawFx = true;
                                // try Stop/Clear/Simulate reflection methods
                                try
                                {
                                    var mStop = comp.GetType().GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (mStop != null)
                                    {
                                        var pCount = mStop.GetParameters().Length;
                                        if (pCount == 1) mStop.Invoke(comp, new object[] { true });
                                        else mStop.Invoke(comp, null);
                                    }
                                }
                                catch { }
                                try
                                {
                                    var mClear = comp.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (mClear != null) mClear.Invoke(comp, new object[] { true });
                                }
                                catch { }
                                try
                                {
                                    var mSim = comp.GetType().GetMethod("Simulate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(float), typeof(bool), typeof(bool) }, null)
                                               ?? comp.GetType().GetMethod("Simulate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(float), typeof(bool) }, null);
                                    if (mSim != null)
                                    {
                                        if (mSim.GetParameters().Length == 3) mSim.Invoke(comp, new object[] { 0f, true, true });
                                        else mSim.Invoke(comp, new object[] { 0f, true });
                                    }
                                }
                                catch { }
                            }
                        }

                        if (sawFx) attempted++;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneStabilizer] StopNearbyFXAroundPositionInScene exception: " + ex);
        }
        return attempted;
    }

    // High-level convenience: perform stabilization pass after scene activation and before placement.
    // - disable spawners (keywords)
    // - stop nearby FX around placementPos
    // Returns summary string for logging.
    public static string StabilizeSceneBeforePlacement(Scene loadedScene, Vector3 placementPos, float fxRadius = 20f)
    {
        int disabled = 0, stopped = 0;
        try
        {
            // Common spawner keywords; tweak/add more as you learn scene specifics
            var spawnerKeywords = new[] { "Bonfire", "BonfireSpawner", "FireSpawner", "SpawnFire", "TurnOffTime", "TurnOff", "SpawnFX" };
            disabled = DisableSpawnersByKeywordsInScene(loadedScene, spawnerKeywords);

            stopped = StopNearbyFXAroundPositionInScene(loadedScene, placementPos, fxRadius);

            // Small optional GC/unload — often unnecessary but helpful if you're unloading transition scene
            // var op = Resources.UnloadUnusedAssets(); while (!op.isDone) {}
            // System.GC.Collect();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneStabilizer] StabilizeSceneBeforePlacement exception: " + ex);
        }

        return $"disabledSpawners={disabled}, stoppedFXRoots={stopped}, placement={placementPos}, radius={fxRadius}";
    }
}