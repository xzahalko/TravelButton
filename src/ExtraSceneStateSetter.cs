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

            // 4) Replace Destroyed variant references: derive normal/destroyed names from scene,
            //    prefer the provided sceneDisplayName or canonical token so we don't pick unrelated "Normal" names.
            string preferredToken = null;
            if (!string.IsNullOrEmpty(sceneDisplayName))
                preferredToken = SanitizeToken(sceneDisplayName);
            else if (!string.IsNullOrEmpty(sceneNameCanonical))
                preferredToken = SanitizeToken(sceneNameCanonical);

            string normalVariant = null;
            string destroyedVariant = null;
            try
            {
                DeriveNormalDestroyedNamesFromScene(scene, preferredToken, out normalVariant, out destroyedVariant);
                TBLog.Info($"[ExtraSceneStateSetter] Derived variants (preferred='{preferredToken}'): normal='{normalVariant}', destroyed='{destroyedVariant}'");
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
                Debug.Log($"[ExtraSceneStateSetter] Set UI Text on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(lblGo)}' to '{text}'.");
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
                            Debug.Log($"[ExtraSceneStateSetter] Set TMP text via property on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(lblGo)}' to '{text}'.");
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
                        Debug.Log($"[ExtraSceneStateSetter] Reflection: set '{compType.FullName}.m_Text' = '{text}' on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(lblGo)}'.");
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

            // find Normal GO and Destroyed GO in scene, but only by exact names if provided.
            GameObject normalGO = null;
            GameObject destroyedGO = null;

            // If normalName/destroyedName are provided, prefer exact matches by name.
            if (!string.IsNullOrEmpty(normalName) || !string.IsNullOrEmpty(destroyedName))
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    if (normalGO == null && !string.IsNullOrEmpty(normalName))
                    {
                        var cand = root.GetComponentsInChildren<Transform>(true)
                                       .FirstOrDefault(t => string.Equals(t.name, normalName, StringComparison.Ordinal));
                        if (cand != null) normalGO = cand.gameObject;
                    }
                    if (destroyedGO == null && !string.IsNullOrEmpty(destroyedName))
                    {
                        var cand = root.GetComponentsInChildren<Transform>(true)
                                       .FirstOrDefault(t => string.Equals(t.name, destroyedName, StringComparison.Ordinal));
                        if (cand != null) destroyedGO = cand.gameObject;
                    }
                    if (normalGO != null && destroyedGO != null) break;
                }
            }

            // If we couldn't find exact matches and a preferred name was supplied, try the alternative token patterns for that preferred token.
            // But do NOT fall back to "any object that contains 'Normal'" because that picks generic unrelated objects.
            if (normalGO == null && !string.IsNullOrEmpty(normalName))
            {
                // try common alternates only when the normalName was a token (e.g., "Cierzo") and we passed "Normal{token}" originally.
                var alt = new[] { normalName, $"Normal{normalName}", $"{normalName}Normal", $"Normal_{normalName}", $"{normalName}_Normal" };
                foreach (var candidateName in alt)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, candidateName, StringComparison.Ordinal));
                        if (cand != null) { normalGO = cand.gameObject; break; }
                    }
                    if (normalGO != null) break;
                }
            }

            if (destroyedGO == null && !string.IsNullOrEmpty(destroyedName))
            {
                var altD = new[] { destroyedName, $"Destroyed{destroyedName}", $"{destroyedName}Destroyed", $"Destroyed_{destroyedName}", $"{destroyedName}_Destroyed" };
                foreach (var candidateName in altD)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, candidateName, StringComparison.Ordinal));
                        if (cand != null) { destroyedGO = cand.gameObject; break; }
                    }
                    if (destroyedGO != null) break;
                }
            }

            Debug.Log($"[ReplaceObjectReferences] scene='{scene.name}' chosen Normal={(normalGO == null ? "<null>" : normalGO.name)} Destroyed={(destroyedGO == null ? "<null>" : destroyedGO.name)}");

            // If we don't have a normalGO, we are not confident to perform object reference swapping — skip heavy replacements.
            if (normalGO == null)
            {
                Debug.LogWarning($"[ReplaceObjectReferences] No confident Normal GO found (normalName='{normalName}'). Skipping replacements to avoid incorrect swaps.");
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
                                                // replace by normalGO
                                                list[i] = normalGO;
                                                changes++;
                                                any = true;
                                                Debug.Log($"[ReplaceObjectReferences] field '{t.FullName}.{f.Name}' on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(comp.gameObject)}' replaced object entry index={i} Destroyed('{destroyedName}')->Normal('{normalGO.name}')");
                                            }
                                            else if (uo is Component compItem && compItem.gameObject != null && !string.IsNullOrEmpty(destroyedName) && string.Equals(compItem.gameObject.name, destroyedName, StringComparison.Ordinal))
                                            {
                                                var match = findMatchingOnNormal(compItem);
                                                if (match != null)
                                                {
                                                    list[i] = match;
                                                    changes++;
                                                    any = true;
                                                    Debug.Log($"[ReplaceObjectReferences] replaced component entry index={i} on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(comp.gameObject)}' with matching component on {normalGO.name}");
                                                }
                                                else
                                                {
                                                    // fallback: replace with normalGO
                                                    list[i] = normalGO;
                                                    changes++;
                                                    any = true;
                                                    Debug.Log($"[ReplaceObjectReferences] fallback replaced component entry index={i} with normalGO on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(comp.gameObject)}'");
                                                }
                                            }
                                        }
                                        else if (item is string sItem)
                                        {
                                            // if string equals or contains destroyedName, replace with normalName (only if destroyedName provided)
                                            if (!string.IsNullOrEmpty(destroyedName) && sItem.IndexOf(destroyedName, StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                var newVal = !string.IsNullOrEmpty(normalName) ? sItem.Replace(destroyedName, normalName) : sItem;
                                                list[i] = newVal;
                                                changes++;
                                                any = true;
                                                Debug.Log($"[ReplaceObjectReferences] replaced string entry '{sItem}' -> '{list[i]}' in field '{t.FullName}.{f.Name}' on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(comp.gameObject)}'");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                if (any)
                                {
                                    try
                                    {
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
                                Debug.Log($"[ReplaceObjectReferences] Replaced serialized blackboard string on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(comp.gameObject)}' field '{f.Name}' (len {str.Length} -> {newStr.Length})");
                            }
                        }
                    }
                    catch { }
                }
            }

            // NodeCanvas Blackboard special handling: update only if we have meaningful normalGO
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
                                                listObj[i] = normalGO;
                                                changes++;
                                                Debug.Log($"[ReplaceObjectReferences] NodeCanvas blackboard _objectReferences[{i}] replaced with {normalName} on '{ExtraSceneVariantDiagnostics.GetGameObjectPath(comp.gameObject)}'");
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }

                        if (setValueMethod != null && normalGO != null)
                        {
                            try
                            {
                                // Only attempt safe, high-probability assignments
                                var attempts = new List<(string varName, object value)>();
                                attempts.Add(("Normal", normalGO));
                                if (!string.IsNullOrEmpty(normalName)) attempts.Add((normalName, normalGO));
                                foreach (var at in attempts)
                                {
                                    try { setValueMethod.Invoke(comp, new object[] { at.varName, at.value }); } catch { }
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

            // Deactivate destroyedGO only if we actually replaced references pointing at it (safe-guard)
            try
            {
                if (destroyedGO != null)
                {
                    // Only deactivate if we replaced something and destroyed differs from normal
                    if (changes > 0 && destroyedGO != normalGO)
                    {
                        if (destroyedGO.activeSelf)
                        {
                            destroyedGO.SetActive(false);
                            changes++;
                            Debug.Log($"[ReplaceObjectReferences] Deactivated Destroyed GO '{ExtraSceneVariantDiagnostics.GetGameObjectPath(destroyedGO)}'");
                        }
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ReplaceObjectReferences] top-level exception: " + ex);
        }

        return changes;
    }

    // Improved derivation: prefer preferredToken if provided; limit fallbacks to avoid picking unrelated 'Normal' objects.
    public static void DeriveNormalDestroyedNamesFromScene(Scene scene, string preferredToken, out string normalName, out string destroyedName)
    {
        normalName = null;
        destroyedName = null;
        try
        {
            if (!scene.IsValid()) return;

            // Build name index for exact lookup
            var namesInScene = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (!string.IsNullOrEmpty(t.name)) namesInScene.Add(t.name);
            }

            // First: if a preferredToken is supplied, try strong candidates based on that token
            if (!string.IsNullOrEmpty(preferredToken))
            {
                // sanitize token for comparisons (strip spaces/invalid chars)
                var baseToken = SanitizeToken(preferredToken);
                var candidatePairs = new List<(string normal, string destroyed)>();

                // Candidate pairs to try in order of decreasing confidence
                candidatePairs.Add(($"Normal{baseToken}", $"Destroyed{baseToken}"));
                candidatePairs.Add(($"{baseToken}Normal", $"{baseToken}Destroyed"));
                candidatePairs.Add(($"Normal_{baseToken}", $"Destroyed_{baseToken}"));
                candidatePairs.Add(($"Normal{baseToken}v1", $"{baseToken}Destroyed")); // extra attempts
                candidatePairs.Add(($"Normal{baseToken} (Clone)", $"{baseToken}Destroyed")); // clones sometimes present

                foreach (var p in candidatePairs)
                {
                    bool nExists = !string.IsNullOrEmpty(p.normal) && namesInScene.Contains(p.normal);
                    bool dExists = !string.IsNullOrEmpty(p.destroyed) && namesInScene.Contains(p.destroyed);
                    if (nExists || dExists)
                    {
                        // Accept pair even if only one side present — but prefer normal presence
                        normalName = nExists ? p.normal : (namesInScene.FirstOrDefault(n => n.IndexOf(baseToken, StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0));
                        destroyedName = dExists ? p.destroyed : (namesInScene.FirstOrDefault(n => n.IndexOf(baseToken, StringComparison.OrdinalIgnoreCase) >= 0 && (n.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0)));
                        if (!string.IsNullOrEmpty(normalName) || !string.IsNullOrEmpty(destroyedName))
                            return;
                    }
                }
            }

            // Second: try to find explicit names containing both Normal-like and destroyed-like tokens that share a base.
            // This will only run if preferredToken didn't yield confident results.
            var normals = namesInScene.Where(n => n.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var destroyeds = namesInScene.Where(n => n.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Ruined", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // attempt pair by stripping Normal/Destroyed and matching base
            foreach (var n in normals)
            {
                var baseN = RemoveTokenVariant(n, "Normal");
                var matchD = destroyeds.FirstOrDefault(d => RemoveTokenVariant(d, "Destroyed").Equals(baseN, StringComparison.OrdinalIgnoreCase) || RemoveTokenVariant(d, "Ruin").Equals(baseN, StringComparison.OrdinalIgnoreCase));
                if (matchD != null)
                {
                    normalName = n;
                    destroyedName = matchD;
                    return;
                }
            }

            // Final conservative fallback: if any Normal-like exists, pick the one that contains the scene name (if present) else the first Normal-like.
            if (normals.Count > 0)
            {
                var chosen = normals.FirstOrDefault(n => !string.IsNullOrEmpty(scene.name) && n.IndexOf(scene.name, StringComparison.OrdinalIgnoreCase) >= 0) ?? normals.First();
                normalName = chosen;
            }
            if (destroyeds.Count > 0)
            {
                var chosenD = destroyeds.FirstOrDefault(d => !string.IsNullOrEmpty(scene.name) && d.IndexOf(scene.name, StringComparison.OrdinalIgnoreCase) >= 0) ?? destroyeds.First();
                destroyedName = chosenD;
            }
        }
        catch { /* swallow — best-effort */ }
    }

    // Utility helpers used above
    static string SanitizeToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        // remove non-alphanumeric, collapse spaces, preserve case as originally given
        var sb = new System.Text.StringBuilder();
        foreach (var c in token)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        var s = sb.ToString();
        return s;
    }

    // Replace the RemoveTokenVariant method with this implementation (uses Regex.Replace for case-insensitive removal)
    static string RemoveTokenVariant(string name, string token)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        if (string.IsNullOrEmpty(token)) return name;
        try
        {
            // Use Regex.Replace to remove the token case-insensitively (Regex.Escape in case token has special chars)
            var pattern = System.Text.RegularExpressions.Regex.Escape(token);
            var result = System.Text.RegularExpressions.Regex.Replace(name, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return result.Trim(new char[] { '_', ' ', '-' });
        }
        catch
        {
            // on error, return original name to be safe
            return name;
        }
    }
}
