using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static class SceneVisitHelper
{
    public static void MarkSceneVisitedPreload(string sceneName, string targetGameObjectName = null)
    {
        try
        {
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(sceneName)) keys.Add(sceneName);
            if (!string.IsNullOrEmpty(targetGameObjectName)) keys.Add(targetGameObjectName);
            if (!string.IsNullOrEmpty(sceneName))
                keys.Add(sceneName.Replace("NewTerrain", ""));
            // also add short names (dedupe)
            foreach (var k in keys.ToArray())
                if (!string.IsNullOrEmpty(k) && !keys.Contains(k))
                    keys.Add(k);

            var asmCandidates = AppDomain.CurrentDomain.GetAssemblies();

            // If there's a known type with helpful methods, call them first
            string[] candidateMethodNames = new[] { "MarkVisited", "SetVisited", "AddVisited", "AddVisitedScene", "SetVisitedScene" };

            foreach (var asm in asmCandidates)
            {
                System.Type[] types = null;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    // direct method on static type
                    foreach (var mname in candidateMethodNames)
                    {
                        try
                        {
                            var mi = t.GetMethod(mname, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new System.Type[] { typeof(string) }, null);
                            if (mi != null)
                            {
                                foreach (var key in keys)
                                    try { mi.Invoke(null, new object[] { key }); } catch { }
                                // done for this type
                                goto NextType;
                            }
                        }
                        catch { }
                    }

                    // instance singleton Instance / Instance field
                    object inst = null;
                    try
                    {
                        var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                        if (prop != null) inst = prop.GetValue(null);
                        else
                        {
                            var fld = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                            if (fld != null) inst = fld.GetValue(null);
                        }
                    }
                    catch { inst = null; }

                    if (inst != null)
                    {
                        foreach (var mname in candidateMethodNames)
                        {
                            try
                            {
                                var mi = t.GetMethod(mname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new System.Type[] { typeof(string) }, null);
                                if (mi != null)
                                {
                                    foreach (var key in keys)
                                        try { mi.Invoke(inst, new object[] { key }); } catch { }
                                    goto NextType;
                                }
                            }
                            catch { }
                        }
                    }

                    // static collections fallback in this type
                    try
                    {
                        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            var ft = f.FieldType;
                            if (!ft.IsGenericType) continue;
                            var g = ft.GetGenericTypeDefinition();
                            if (g != typeof(System.Collections.Generic.HashSet<>) && g != typeof(System.Collections.Generic.List<>)) continue;
                            var elem = ft.GetGenericArguments()[0];
                            if (elem != typeof(string)) continue;
                            object coll = null;
                            try { coll = f.GetValue(null); } catch { coll = null; }
                            if (coll == null) continue;
                            var add = ft.GetMethod("Add", new System.Type[] { typeof(string) }) ?? ft.GetMethod("Add");
                            if (add == null) continue;
                            foreach (var key in keys)
                                try { add.Invoke(coll, new object[] { key }); } catch { }
                        }
                    }
                    catch { }

                NextType:
                    continue;
                }
            }

            // Extra explicit known targets discovered in your logs — try to set them directly if present
            TryAddToStaticCollection("VisitedStore", "visited", keys);
            TryAddToStaticCollection("VisitedTracker", "visited", keys);
            TryAddToStaticCollection("TravelButton", "_visitedLookup", keys);
            TryAddToStaticCollection("TravelButtonPlugin", "s_pluginVisitedNames", keys);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[SceneVisitHelper] MarkSceneVisitedPreload exception: " + ex);
        }
    }

    private static void TryAddToStaticCollection(string typeName, string fieldName, IEnumerable<string> keys)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = null;
                try { t = asm.GetType(typeName, false, true); } catch { t = null; }
                if (t == null) continue;
                var f = t.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) continue;
                var coll = f.GetValue(null);
                if (coll == null) continue;
                var ft = f.FieldType;
                if (ft.IsGenericType)
                {
                    var add = ft.GetMethod("Add", new System.Type[] { typeof(string) }) ?? ft.GetMethod("Add");
                    if (add != null)
                    {
                        foreach (var k in keys) try { add.Invoke(coll, new object[] { k }); } catch { }
                        return;
                    }
                }
            }
        }
        catch { }
    }
}

public static class SceneLoadStateHelper
{
    // Set m_lastLoadedScene / m_lastLoadedLevelName on known persistent components (NetworkEntity)
    // and set the loading label text (lblAreaName) if present.
    // Usage: call before tm.StartSceneLoad(...) and as an early backup in the load callback.
    public static void SetLastLoadedSceneStrings(string sceneName, string areaLabel = null)
    {
        try
        {
            int changed = 0;

            // Preferred: target known persistent object "NetworkEntity"
            var netEntity = GameObject.Find("NetworkEntity");
            if (netEntity != null)
            {
                var mbs = netEntity.GetComponents<MonoBehaviour>();
                foreach (var mb in mbs)
                {
                    if (mb == null) continue;
                    changed += TrySetFieldsOnInstance(mb, sceneName);
                }
            }
            else
            {
                // Fallback: scan active MonoBehaviours (no includeInactive overload available in this Unity)
                var activeMbs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in activeMbs)
                {
                    if (mb == null) continue;
                    changed += TrySetFieldsOnInstance(mb, sceneName);
                }

                // If you need to include inactive scene instances (heavier), uncomment and use the block below:
                /*
                var all = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)) as UnityEngine.Object[];
                foreach (var o in all)
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null) continue;
                    // ignore assets / prefabs: ensure it belongs to a scene
                    if (!mb.gameObject.scene.IsValid()) continue;
                    changed += TrySetFieldsOnInstance(mb, sceneName);
                }
                */
            }

            // Set loading label if present
            if (!string.IsNullOrEmpty(areaLabel))
            {
                var lblGO = GameObject.Find("lblAreaName");
                if (lblGO != null)
                {
                    var txt = lblGO.GetComponent<UnityEngine.UI.Text>();
                    if (txt != null)
                    {
                        txt.text = areaLabel;
                        UnityEngine.Debug.LogWarning($"[SceneLoadStateHelper] Set lblAreaName.text = '{areaLabel}'");
                    }
                }
            }

            UnityEngine.Debug.LogWarning($"[SceneLoadStateHelper] SetLastLoadedSceneStrings done, changed={changed} scene={sceneName}");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("[SceneLoadStateHelper] exception: " + ex);
        }
    }

    // return 1 if any field set, 0 otherwise
    private static int TrySetFieldsOnInstance(MonoBehaviour mb, string sceneName)
    {
        int setCount = 0;
        try
        {
            var t = mb.GetType();
            var f1 = t.GetField("m_lastLoadedScene", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f1 != null && f1.FieldType == typeof(string))
            {
                try { f1.SetValue(mb, sceneName); setCount++; UnityEngine.Debug.LogWarning($"SET {t.FullName}.m_lastLoadedScene on GO='{mb.gameObject.name}' to '{sceneName}'"); } catch { }
            }
            var f2 = t.GetField("m_lastLoadedLevelName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f2 != null && f2.FieldType == typeof(string))
            {
                try { f2.SetValue(mb, sceneName); setCount++; UnityEngine.Debug.LogWarning($"SET {t.FullName}.m_lastLoadedLevelName on GO='{mb.gameObject.name}' to '{sceneName}'"); } catch { }
            }
        }
        catch { }
        return setCount;
    }
}
