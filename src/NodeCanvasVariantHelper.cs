using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class NodeCanvasVariantHelper
{
    // Replace Destroyed variant references in NodeCanvas Blackboards inside loadedScene.
    // normalName/destroyedName defaults chosen from your dumps.
    public static void PreferNormalVariantInBlackboards(Scene loadedScene, string normalName = null, string destroyedName = null)
    {
        try
        {
            // If no explicit names supplied, derive candidate names heuristically from the scene
            if (string.IsNullOrEmpty(normalName) || string.IsNullOrEmpty(destroyedName))
            {
                try
                {
                    string derivedNormal = null;
                    string derivedDestroyed = null;
                    // best-effort derive using same helper logic as ExtraSceneStateSetter
                    // DeriveNormalDestroyedNamesFromScene expects (Scene scene, string preferredToken, out string normalName, out string destroyedName)
                    ExtraSceneStateSetter.DeriveNormalDestroyedNamesFromScene(loadedScene, null, out derivedNormal, out derivedDestroyed);
                    if (!string.IsNullOrEmpty(derivedNormal) && string.IsNullOrEmpty(normalName)) normalName = derivedNormal;
                    if (!string.IsNullOrEmpty(derivedDestroyed) && string.IsNullOrEmpty(destroyedName)) destroyedName = derivedDestroyed;
                }
                catch { /* ignore derivation failure, continue with nulls */ }
            }

            // Resolve Blackboard type (NodeCanvas)
            Type bbType = Type.GetType("NodeCanvas.Framework.Blackboard, Assembly-CSharp", false, true);
            if (bbType == null)
            {
                Debug.LogWarning("[NodeCanvasVariantHelper] Blackboard type not found.");
                return;
            }

            // Find normal/destroyed GameObjects in that scene (search transforms)
            GameObject normalGO = null;
            GameObject destroyedGO = null;
            foreach (var root in loadedScene.GetRootGameObjects())
            {
                if (root == null) continue;
                // If names provided, look for exacts first
                if (!string.IsNullOrEmpty(normalName))
                {
                    var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, normalName, StringComparison.Ordinal));
                    if (cand != null) normalGO = cand.gameObject;
                }
                if (!string.IsNullOrEmpty(destroyedName))
                {
                    var cand = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, destroyedName, StringComparison.Ordinal));
                    if (cand != null) destroyedGO = cand.gameObject;
                }
            }

            // If exacts not found, attempt to find any Normal/Destroyed-like object related to scene
            if (normalGO == null)
            {
                normalGO = loadedScene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t.name) && t.name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)?.gameObject;
            }
            if (destroyedGO == null)
            {
                destroyedGO = loadedScene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t.name) && (t.name.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("Ruin", StringComparison.OrdinalIgnoreCase) >= 0))?.gameObject;
            }

            FieldInfo objRefsField = bbType.GetField("_objectReferences", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo setValueMethod = bbType.GetMethod("SetValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var root in loadedScene.GetRootGameObjects())
            {
                if (root == null) continue;
                var components = root.GetComponentsInChildren<Component>(true);
                foreach (var c in components)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (t.FullName != bbType.FullName) continue;

                    // Update serialized object references list if present
                    if (objRefsField != null && normalGO != null)
                    {
                        try
                        {
                            var objRefs = objRefsField.GetValue(c);
                            if (objRefs is System.Collections.IList list)
                            {
                                for (int i = 0; i < list.Count; i++)
                                {
                                    try
                                    {
                                        var item = list[i] as UnityEngine.Object;
                                        if (item is GameObject go && (string.Equals(go.name, destroyedName, StringComparison.Ordinal) || go.name.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0))
                                        {
                                            list[i] = normalGO;
                                            Debug.Log("[NodeCanvasVariantHelper] Replaced destroyed reference in blackboard on " + c.gameObject.name);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch (Exception ex) { Debug.LogWarning("[NodeCanvasVariantHelper] _objectReferences update failed: " + ex); }
                    }

                    // Use SetValue fallback if available
                    if (setValueMethod != null && normalGO != null)
                    {
                        try
                        {
                            // variable names observed: "<Base>Normal", "<Base>Destroyed", also try simple "Normal"/"Destroyed"
                            var attempts = new List<(string varName, object value)>();
                            attempts.Add(("Normal", normalGO));
                            attempts.Add(("Destroyed", null));
                            if (!string.IsNullOrEmpty(normalName)) attempts.Add((normalName, normalGO));
                            if (!string.IsNullOrEmpty(destroyedName)) attempts.Add((destroyedName, null));

                            foreach (var at in attempts)
                            {
                                try { setValueMethod.Invoke(c, new object[] { at.varName, at.value }); } catch { }
                            }

                            Debug.Log("[NodeCanvasVariantHelper] attempted SetValue on blackboard for derived normal/destroyed variables");
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[NodeCanvasVariantHelper] PreferNormalVariantInBlackboards exception: " + ex);
        }
    }
}