using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class NodeCanvasVariantHelper
{
    // Replace Destroyed variant references in NodeCanvas Blackboards inside loadedScene.
    // normalName/destroyedName defaults chosen from your dumps.
    public static void PreferNormalVariantInBlackboards(Scene loadedScene, string normalName = "NormalCierzo", string destroyedName = "DestroyedCierzo")
    {
        try
        {
            // Resolve Blackboard type (NodeCanvas)
            Type bbType = Type.GetType("NodeCanvas.Framework.Blackboard, Assembly-CSharp", false, true);
            if (bbType == null)
            {
                Debug.LogWarning("[NodeCanvasVariantHelper] Blackboard type not found.");
                return;
            }

            // Find normal/destroyed GameObjects in that scene (root-level)
            GameObject normalGO = null;
            GameObject destroyedGO = null;
            foreach (var root in loadedScene.GetRootGameObjects())
            {
                if (root == null) continue;
                if (string.Equals(root.name, normalName, StringComparison.Ordinal)) normalGO = root;
                if (string.Equals(root.name, destroyedName, StringComparison.Ordinal)) destroyedGO = root;
                if (normalGO != null && destroyedGO != null) break;
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
                    if (objRefsField != null)
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
                                        if (item is GameObject go && go.name == destroyedName)
                                        {
                                            list[i] = normalGO ?? null;
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
                            // variable names observed: "CierzoNormal", "CierzoDestroyed"
                            try { setValueMethod.Invoke(c, new object[] { "CierzoNormal", normalGO }); } catch { }
                            try { setValueMethod.Invoke(c, new object[] { "CierzoDestroyed", null }); } catch { }
                            Debug.Log("[NodeCanvasVariantHelper] attempted SetValue on blackboard for CierzoNormal/CierzoDestroyed");
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