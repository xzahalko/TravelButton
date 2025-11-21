using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ExtraSceneStateSetter: improved version that also replaces Destroyed variant references inside
/// _objectReferences collections and serialized blackboard strings (NodeCanvas).
/// Call ExtraSceneStateSetter.Apply(loadedScene, "CierzoNewTerrain", "Cierzo") from your
/// pre-activation hook (Harmony prefix on SceneManager.SetActiveScene or StartSceneLoad callback
/// if it runs before activation).
/// 
/// Testing approach:
/// - Apply only the _objectReferences replacement first and test (fastest).
/// - If still broken, enable serialized-blackboard string replacement and NodeCanvas SetValue fallbacks.
/// </summary>
public static class ExtraSceneStateSetter
{
    // Public entry
    public static void Apply(Scene scene, string sceneNameCanonical, string sceneDisplayName)
    {
        try
        {
            Debug.Log($"[ExtraSceneStateSetter] Apply called for scene='{scene.name}', canonical='{sceneNameCanonical}', display='{sceneDisplayName}'");

            // 1) NetworkEntity: try set m_lastLoadedLevelName and m_previousScene (and fallbacks)
            TrySetNetworkEntityStrings(sceneNameCanonical);

            // 2) BuildingResourcesManager.m_lastLoadedScene
            TrySetBuildingResourcesLastLoaded(sceneNameCanonical);

            // 3) lblAreaName UI text (Text or TMP)
            TrySetLblAreaName(scene, sceneDisplayName ?? sceneNameCanonical);

            // 4) Replace Destroyed variant references inside NodeCanvas/MonoBehaviour object reference collections
            int replacedCount = ReplaceObjectReferencesAndSerializedBlackboards(scene, normalName: "NormalCierzo", destroyedName: "DestroyedCierzo");
            Debug.Log($"[ExtraSceneStateSetter] ReplaceObjectReferencesAndSerializedBlackboards completed: replacements={replacedCount}");

            Debug.Log("[ExtraSceneStateSetter] Apply completed.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExtraSceneStateSetter] top-level exception: " + ex);
        }
    }

    // Try set the network-level loader related string fields on GameObject 'NetworkEntity'
    static void TrySetNetworkEntityStrings(string sceneNameCanonical)
    {
        try
        {
            var netGo = GameObject.Find("NetworkEntity");
            if (netGo == null)
            {
                Debug.Log("[ExtraSceneStateSetter] GameObject 'NetworkEntity' not found.");
                return;
            }

            var comps = netGo.GetComponents<Component>();
            bool anySet = false;
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();

                // common field names
                var tryNames = new[] { "m_lastLoadedLevelName", "m_previousScene", "lastLoadedLevelName", "lastLoadedLevel", "previousScene" };

                foreach (var name in tryNames)
                {
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null && f.FieldType == typeof(string))
                    {
                        try
                        {
                            f.SetValue(c, sceneNameCanonical);
                            Debug.Log($"[ExtraSceneStateSetter] Set '{t.FullName}.{name}' = '{sceneNameCanonical}' on NetworkEntity (component {t.Name}).");
                            anySet = true;
                        }
                        catch (Exception ex) { Debug.LogWarning($"[ExtraSceneStateSetter] Failed set {name} on {t.FullName}: {ex}"); }
                    }
                }
            }

            if (!anySet) Debug.Log("[ExtraSceneStateSetter] No network-level string fields found on components of NetworkEntity.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExtraSceneStateSetter] Failed setting NetworkLevelLoader fields: " + ex);
        }
    }

    // Try set BuildingResourcesManager.m_lastLoadedScene
    static void TrySetBuildingResourcesLastLoaded(string sceneNameCanonical)
    {
        try
        {
            var brmType = Type.GetType("BuildingResourcesManager, Assembly-CSharp", false, true);
            Component brmComp = null;

            if (brmType != null)
            {
                // find instance by type name match
                foreach (var o in GameObject.FindObjectsOfType<Component>())
                {
                    if (o == null) continue;
                    if (o.GetType().FullName == brmType.FullName)
                    {
                        brmComp = o;
                        break;
                    }
                }
            }
            else
            {
                // fallback: look for any component with a m_lastLoadedScene string field
                foreach (var o in GameObject.FindObjectsOfType<Component>())
                {
                    if (o == null) continue;
                    var f = o.GetType().GetField("m_lastLoadedScene", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null && f.FieldType == typeof(string))
                    {
                        brmComp = o;
                        break;
                    }
                }
            }

            if (brmComp != null)
            {
                var f = brmComp.GetType().GetField("m_lastLoadedScene", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null && f.FieldType == typeof(string))
                {
                    try
                    {
                        f.SetValue(brmComp, sceneNameCanonical);
                        Debug.Log($"[ExtraSceneStateSetter] Set '{brmComp.GetType().FullName}.m_lastLoadedScene' = '{sceneNameCanonical}' on GameObject '{brmComp.gameObject.name}'.");
                    }
                    catch (Exception ex) { Debug.LogWarning("[ExtraSceneStateSetter] Failed set m_lastLoadedScene: " + ex); }
                }
                else Debug.Log("[ExtraSceneStateSetter] Found BuildingResources-like component but no m_lastLoadedScene field.");
            }
            else Debug.Log("[ExtraSceneStateSetter] No BuildingResourcesManager-like component found.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExtraSceneStateSetter] Failed setting BuildingResourcesManager.m_lastLoadedScene: " + ex);
        }
    }

    // Try set lblAreaName UI text (search roots, then global)
    static void TrySetLblAreaName(Scene scene, string text)
    {
        try
        {
            GameObject lblGo = null;

            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null) continue;
                var trans = root.transform.Find("MenuManager/GeneralMenus/MasterLoading/BlackFade/lblAreaName");
                if (trans != null) { lblGo = trans.gameObject; break; }

                var children = root.GetComponentsInChildren<Transform>(true);
                foreach (var tr in children)
                {
                    if (tr == null) continue;
                    if (string.Equals(tr.name, "lblAreaName", StringComparison.Ordinal))
                    {
                        lblGo = tr.gameObject;
                        break;
                    }
                }
                if (lblGo != null) break;
            }

            if (lblGo == null) lblGo = GameObject.Find("lblAreaName");

            if (lblGo == null)
            {
                Debug.Log("[ExtraSceneStateSetter] lblAreaName GameObject not found.");
                return;
            }

            // Try UnityEngine.UI.Text
            var textComp = lblGo.GetComponent<Text>();
            if (textComp != null)
            {
                textComp.text = text;
                Debug.Log($"[ExtraSceneStateSetter] Set UI Text on '{GetGameObjectPath(lblGo)}' to '{text}'.");
                return;
            }

            // Try TMP or reflection fallback
            foreach (var comp in lblGo.GetComponents<Component>())
            {
                if (comp == null) continue;
                var compType = comp.GetType();
                if (compType.FullName == "TMPro.TMP_Text" || compType.FullName == "TMPro.TextMeshProUGUI")
                {
                    var txtProp = compType.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (txtProp != null)
                    {
                        try
                        {
                            txtProp.SetValue(comp, text);
                            Debug.Log($"[ExtraSceneStateSetter] Set TMP text via property on '{GetGameObjectPath(lblGo)}' to '{text}'.");
                            return;
                        }
                        catch { }
                    }
                }

                var mTextField = compType.GetField("m_Text", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (mTextField != null && mTextField.FieldType == typeof(string))
                {
                    try
                    {
                        mTextField.SetValue(comp, text);
                        Debug.Log($"[ExtraSceneStateSetter] Reflection: set '{compType.FullName}.m_Text' = '{text}' on '{GetGameObjectPath(lblGo)}'.");
                        return;
                    }
                    catch { }
                }
            }

            Debug.Log("[ExtraSceneStateSetter] lblAreaName found but no known Text/TMP component updated it.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExtraSceneStateSetter] Failed setting lblAreaName text: " + ex);
        }
    }

    // Replace Destroyed variant references inside collection fields such as _objectReferences
    // and also attempt to fix serialized blackboard strings (JSON-style).
    // Returns number of replacements attempted.
    public static int ReplaceObjectReferencesAndSerializedBlackboards(Scene scene, string normalName, string destroyedName)
    {
        int changes = 0;
        try
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning($"[ReplaceObjectReferences] scene '{scene.name}' invalid or not loaded.");
                return 0;
            }

            // find Normal GO and Destroyed GO in scene
            GameObject normalGO = null;
            GameObject destroyedGO = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                if (normalGO == null)
                {
                    var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, normalName, StringComparison.Ordinal));
                    if (cand != null) normalGO = cand.gameObject;
                }
                if (destroyedGO == null)
                {
                    var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, destroyedName, StringComparison.Ordinal));
                    if (cand != null) destroyedGO = cand.gameObject;
                }
                if (normalGO != null && destroyedGO != null) break;
            }

            Debug.Log($"[ReplaceObjectReferences] scene='{scene.name}' found Normal={(normalGO == null ? "<null>" : normalGO.name)} Destroyed={(destroyedGO == null ? "<null>" : destroyedGO.name)}");

            if (normalGO == null)
            {
                Debug.LogWarning($"[ReplaceObjectReferences] Normal '{normalName}' not found; nothing to replace.");
                return 0;
            }

            // collect all components in scene
            var allComps = new List<Component>();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                allComps.AddRange(root.GetComponentsInChildren<Component>(true));
            }

            // Helper: try to get matching component on normalGO for a given component type
            Func<Component, Component> findMatchingOnNormal = (compToMatch) =>
            {
                if (compToMatch == null) return null;
                return normalGO.GetComponent(compToMatch.GetType());
            };

            // 1) Scan fields for collections/arrays named _objectReferences or of IList types and replace entries
            foreach (var comp in allComps)
            {
                if (comp == null) continue;
                var t = comp.GetType();

                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var f in fields)
                {
                    try
                    {
                        // target likely names
                        if (string.Equals(f.Name, "_objectReferences", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(f.Name, "objectReferences", StringComparison.OrdinalIgnoreCase)
                            || typeof(IList).IsAssignableFrom(f.FieldType)
                            || f.FieldType.IsArray)
                        {
                            var val = f.GetValue(comp);
                            if (val == null) continue;

                            // handle IList
                            if (val is IList list)
                            {
                                bool any = false;
                                for (int i = 0; i < list.Count; i++)
                                {
                                    try
                                    {
                                        var item = list[i];
                                        if (item == null) continue;

                                        // UnityEngine.Object references (GameObject / Component)
                                        if (item is UnityEngine.Object uo)
                                        {
                                            if (uo is GameObject goItem && string.Equals(goItem.name, destroyedName, StringComparison.Ordinal))
                                            {
                                                // replace by normalGO
                                                list[i] = normalGO;
                                                changes++;
                                                any = true;
                                                Debug.Log($"[ReplaceObjectReferences] field '{t.FullName}.{f.Name}' on '{GetGameObjectPath(comp.gameObject)}' replaced object entry index={i} Destroyed->{normalName}");
                                            }
                                            else if (uo is Component compItem && compItem.gameObject != null && string.Equals(compItem.gameObject.name, destroyedName, StringComparison.Ordinal))
                                            {
                                                // attempt to find matching component on normalGO
                                                var match = findMatchingOnNormal(compItem);
                                                if (match != null)
                                                {
                                                    list[i] = match;
                                                    changes++;
                                                    any = true;
                                                    Debug.Log($"[ReplaceObjectReferences] replaced component entry index={i} on '{GetGameObjectPath(comp.gameObject)}' with matching component on {normalName}");
                                                }
                                                else
                                                {
                                                    // fallback: replace with normalGO
                                                    list[i] = normalGO;
                                                    changes++;
                                                    any = true;
                                                    Debug.Log($"[ReplaceObjectReferences] fallback replaced component entry index={i} with normalGO on '{GetGameObjectPath(comp.gameObject)}'");
                                                }
                                            }
                                        }
                                        else if (item is string sItem)
                                        {
                                            // if string equals or contains destroyedName, replace with normalName
                                            if (sItem.IndexOf(destroyedName, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                list[i] = sItem.Replace(destroyedName, normalName);
                                                changes++;
                                                any = true;
                                                Debug.Log($"[ReplaceObjectReferences] replaced string entry '{sItem}' -> '{list[i]}' in field '{t.FullName}.{f.Name}' on '{GetGameObjectPath(comp.gameObject)}'");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                if (any)
                                {
                                    try
                                    {
                                        // write back modified list for arrays (if array type)
                                        if (f.FieldType.IsArray && val is Array arr)
                                        {
                                            // nothing special needed; we modified list instance if it's a List<T>. For arrays, attempt to copy back:
                                            var elementType = f.FieldType.GetElementType();
                                            var newArr = Array.CreateInstance(elementType, list.Count);
                                            for (int i = 0; i < list.Count; i++)
                                                newArr.SetValue(list[i], i);
                                            f.SetValue(comp, newArr);
                                        }
                                        else
                                        {
                                            // for List<T> or IList-field, set the field back (some types have private backing)
                                            f.SetValue(comp, val);
                                        }
                                    }
                                    catch { }
                                }
                            }
                            // handle arrays that are not IList (should be covered), otherwise skip
                        }
                    }
                    catch { /* per-field defensive */ }
                }

                // 2) Replace serialized-blackboard JSON/text fields if present
                foreach (var f in fields)
                {
                    try
                    {
                        if (f.FieldType == typeof(string) && (string.Equals(f.Name, "_serializedBlackboard", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(f.Name, "serializedBlackboard", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(f.Name, "_serialized", StringComparison.OrdinalIgnoreCase)))
                        {
                            var str = f.GetValue(comp) as string;
                            if (!string.IsNullOrEmpty(str) && str.IndexOf(destroyedName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var newStr = str.Replace(destroyedName, normalName);
                                f.SetValue(comp, newStr);
                                changes++;
                                Debug.Log($"[ReplaceObjectReferences] Replaced serialized blackboard string on '{GetGameObjectPath(comp.gameObject)}' field '{f.Name}' (len {str.Length} -> {newStr.Length})");
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2b) NodeCanvas.Blackboard special handling: try to update _objectReferences list and SetValue calls
            try
            {
                Type bbType = Type.GetType("NodeCanvas.Framework.Blackboard, Assembly-CSharp", false, true);
                if (bbType != null)
                {
                    FieldInfo objRefsField = bbType.GetField("_objectReferences", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    MethodInfo setValueMethod = bbType.GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var comp in allComps)
                    {
                        if (comp == null) continue;
                        if (comp.GetType().FullName != bbType.FullName) continue;

                        // update _objectReferences list if present
                        if (objRefsField != null)
                        {
                            try
                            {
                                var listObj = objRefsField.GetValue(comp) as IList;
                                if (listObj != null)
                                {
                                    for (int i = 0; i < listObj.Count; i++)
                                    {
                                        try
                                        {
                                            var item = listObj[i] as UnityEngine.Object;
                                            if (item is GameObject go && string.Equals(go.name, destroyedName, StringComparison.Ordinal))
                                            {
                                                listObj[i] = normalGO;
                                                changes++;
                                                Debug.Log($"[ReplaceObjectReferences] NodeCanvas blackboard _objectReferences[{i}] replaced with {normalName} on '{GetGameObjectPath(comp.gameObject)}'");
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }

                        // fallback: call SetValue for common variable names (try to set Normal object)
                        if (setValueMethod != null)
                        {
                            try
                            {
                                // Try common variable names; these calls are best-effort and may throw for mismatched types
                                object[] attempts = new object[] { new object[] { "CierzoNormal", normalGO }, new object[] { "CierzoDestroyed", null } };
                                foreach (object[] attempt in attempts)
                                {
                                    try
                                    {
                                        setValueMethod.Invoke(comp, attempt);
                                        changes++;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception exBB)
            {
                Debug.LogWarning("[ReplaceObjectReferences] NodeCanvas replacement threw: " + exBB);
            }

            // 3) Optionally deactivate DestroyedGO to be safe
            // (we do this as a final aggressive fallback if we found a Destroyed GO)
            if (destroyedGO != null)
            {
                try
                {
                    if (destroyedGO.activeSelf)
                    {
                        destroyedGO.SetActive(false);
                        changes++;
                        Debug.Log($"[ReplaceObjectReferences] Deactivated Destroyed GO '{GetGameObjectPath(destroyedGO)}'");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ReplaceObjectReferences] top-level exception: " + ex);
        }

        return changes;
    }

    static string GetGameObjectPath(GameObject go)
    {
        if (go == null) return "<null>";
        try
        {
            var t = go.transform;
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch { return go.name; }
    }
}