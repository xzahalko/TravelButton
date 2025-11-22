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

            // 4) Replace Destroyed variant references: derive normal/destroyed names from scene
            string normalVariant = null;
            string destroyedVariant = null;
            try
            {
                DeriveNormalDestroyedNamesFromScene(scene, out normalVariant, out destroyedVariant);
                TBLog.Info($"[ExtraSceneStateSetter] Derived variants: normal='{normalVariant}', destroyed='{destroyedVariant}'");
            }
            catch (Exception exDerive)
            {
                TBLog.Warn("[ExtraSceneStateSetter] failed deriving variant names: " + exDerive);
            }

            int replacedCount = ReplaceObjectReferencesAndSerializedBlackboards(scene, normalName: normalVariant, destroyedName: destroyedVariant);
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
                    var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => !string.IsNullOrEmpty(t.name) &&
                        (!string.IsNullOrEmpty(normalName) ? string.Equals(t.name, normalName, StringComparison.Ordinal) : t.name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (cand != null) normalGO = cand.gameObject;
                }
                if (destroyedGO == null)
                {
                    var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => !string.IsNullOrEmpty(t.name) &&
                        (!string.IsNullOrEmpty(destroyedName) ? string.Equals(t.name, destroyedName, StringComparison.Ordinal) : (t.name.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0)));
                    if (cand != null) destroyedGO = cand.gameObject;
                }
                if (normalGO != null && destroyedGO != null) break;
            }

            Debug.Log($"[ReplaceObjectReferences] scene='{scene.name}' found Normal={(normalGO == null ? "<null>" : normalGO.name)} Destroyed={(destroyedGO == null ? "<null>" : destroyedGO.name)}");

            if (normalGO == null)
            {
                Debug.LogWarning($"[ReplaceObjectReferences] Normal '{normalName ?? "<derived>"}' not found; nothing to replace.");
                // It's acceptable to continue — some scenes might not have explicit Normal object names.
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
                return normalGO?.GetComponent(compToMatch.GetType());
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
                            || typeof(System.Collections.IList).IsAssignableFrom(f.FieldType)
                            || f.FieldType.IsArray)
                        {
                            var val = f.GetValue(comp);
                            if (val == null) continue;

                            // handle IList
                            if (val is System.Collections.IList list)
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
                                            if (uo is GameObject goItem && !string.IsNullOrEmpty(destroyedName) && string.Equals(goItem.name, destroyedName, StringComparison.Ordinal))
                                            {
                                                // replace by normalGO if available
                                                if (normalGO != null) list[i] = normalGO;
                                                else list[i] = uo;
                                                changes++;
                                                any = true;
                                                Debug.Log($"[ReplaceObjectReferences] field '{t.FullName}.{f.Name}' on '{GetGameObjectPath(comp.gameObject)}' replaced object entry index={i} Destroyed->{normalName}");
                                            }
                                            else if (uo is Component compItem && compItem.gameObject != null && !string.IsNullOrEmpty(destroyedName) && string.Equals(compItem.gameObject.name, destroyedName, StringComparison.Ordinal))
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
                                                else if (normalGO != null)
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
                                            if (!string.IsNullOrEmpty(destroyedName) && sItem.IndexOf(destroyedName, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                var newVal = !string.IsNullOrEmpty(normalName) ? sItem.Replace(destroyedName, normalName) : sItem;
                                                list[i] = newVal;
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
                                            var elementType = f.FieldType.GetElementType();
                                            var newArr = Array.CreateInstance(elementType, list.Count);
                                            for (int i = 0; i < list.Count; i++)
                                                newArr.SetValue(list[i], i);
                                            f.SetValue(comp, newArr);
                                        }
                                        else
                                        {
                                            f.SetValue(comp, val);
                                        }
                                    }
                                    catch { }
                                }
                            }
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
                            if (!string.IsNullOrEmpty(str) && !string.IsNullOrEmpty(destroyedName) && str.IndexOf(destroyedName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var newStr = !string.IsNullOrEmpty(normalName) ? str.Replace(destroyedName, normalName) : str;
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
                                var listObj = objRefsField.GetValue(comp) as System.Collections.IList;
                                if (listObj != null)
                                {
                                    for (int i = 0; i < listObj.Count; i++)
                                    {
                                        try
                                        {
                                            var item = listObj[i] as UnityEngine.Object;
                                            if (item is GameObject go && !string.IsNullOrEmpty(destroyedName) && string.Equals(go.name, destroyedName, StringComparison.Ordinal))
                                            {
                                                listObj[i] = normalGO ?? null;
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
                        if (setValueMethod != null && normalGO != null)
                        {
                            try
                            {
                                object[] attempts = new object[] { new object[] { "Normal", normalGO }, new object[] { "Destroyed", null } };
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

    // Helper: heuristics to derive Normal/Destroyed names from the scene (best-effort)
    public static void DeriveNormalDestroyedNamesFromScene(Scene scene, out string normalName, out string destroyedName)
    {
        normalName = null;
        destroyedName = null;
        try
        {
            if (!scene.IsValid()) return;

            // Derive base token
            string baseToken = null;
            try
            {
                baseToken = scene.name;
                if (string.IsNullOrEmpty(baseToken))
                {
                    normalName = null; destroyedName = null; return;
                }
                // strip known suffixes
                string[] suffixes = new[] { "NewTerrain", "Terrain", "Map" };
                foreach (var sfx in suffixes)
                {
                    if (baseToken.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
                    {
                        baseToken = baseToken.Substring(0, baseToken.Length - sfx.Length);
                        break;
                    }
                }
            }
            catch { baseToken = scene.name; }

            // Form common variants
            var candidates = new List<(string normal, string destroyed)>();
            if (!string.IsNullOrEmpty(baseToken))
            {
                candidates.Add(($"Normal{baseToken}", $"Destroyed{baseToken}"));
                candidates.Add(($"{baseToken}Normal", $"{baseToken}Destroyed"));
                candidates.Add(($"Normal_{baseToken}", $"Destroyed_{baseToken}"));
            }

            // Try to find any pair that exists in scene
            var namesInScene = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (!string.IsNullOrEmpty(t.name)) namesInScene.Add(t.name);
            }

            foreach (var cand in candidates)
            {
                bool nExists = namesInScene.Contains(cand.normal);
                bool dExists = namesInScene.Contains(cand.destroyed);
                if (nExists || dExists)
                {
                    normalName = cand.normal;
                    destroyedName = cand.destroyed;
                    return;
                }
            }

            // fallback: try to find any names containing Normal/Destroyed tokens and include baseToken
            foreach (var name in namesInScene)
            {
                if (string.IsNullOrEmpty(normalName) && name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 && (string.IsNullOrEmpty(baseToken) || name.IndexOf(baseToken, StringComparison.OrdinalIgnoreCase) >= 0))
                    normalName = name;
                if (string.IsNullOrEmpty(destroyedName) && (name.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0) && (string.IsNullOrEmpty(baseToken) || name.IndexOf(baseToken, StringComparison.OrdinalIgnoreCase) >= 0))
                    destroyedName = name;
                if (!string.IsNullOrEmpty(normalName) && !string.IsNullOrEmpty(destroyedName)) break;
            }

            // final fallback: any Normal-like / Destroyed-like present
            if (string.IsNullOrEmpty(normalName))
            {
                normalName = namesInScene.FirstOrDefault(n => n.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (string.IsNullOrEmpty(destroyedName))
            {
                destroyedName = namesInScene.FirstOrDefault(n => n.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }
        catch { /* swallow – best-effort */ }
    }
}