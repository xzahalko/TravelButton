using System;
using System.Linq;
using System.Reflection;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// Runtime helper for attempting to re-invoke TravelButtonUI hotfix helpers.
// Usage: HotfixRetryHelper.Spawn();
public class HotfixRetryHelper : MonoBehaviour
{
    // seconds to wait before each attempt
    public float[] Delays = new float[] { 2f, 5f, 8f };

    // Create the helper gameobject and start attempts
    public static void Spawn()
    {
        try
        {
            var go = new GameObject("tb_retrigger_helper");
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<HotfixRetryHelper>();
        }
        catch (Exception ex)
        {
            Debug.LogError("[HOTFIX-RETRY] Spawn err: " + ex);
        }
    }

    private IEnumerator Start()
    {
        foreach (var d in Delays)
        {
            Debug.Log("[HOTFIX-RETRY] waiting " + d + "s");
            yield return new WaitForSeconds(d);
            TryInvokeOnce();
            yield return null;
        }

        Debug.Log("[HOTFIX-RETRY] done");
        try { Destroy(this.gameObject); } catch { }
    }

    private void TryInvokeOnce()
    {
        try
        {
            var tbType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => string.Equals(t.Name, "TravelButtonUI", StringComparison.Ordinal));

            if (tbType == null)
            {
                Debug.Log("[HOTFIX-RETRY] TravelButtonUI type not found");
                return;
            }

            InvokeRebuild(tbType);
            InvokeDetect(tbType);
        }
        catch (Exception ex)
        {
            Debug.LogError("[HOTFIX-RETRY] outer err: " + ex);
        }
    }

    private void InvokeRebuild(Type tbType)
    {
        try
        {
            var m = tbType.GetMethod("RebuildTravelDialog", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.Log("[HOTFIX-RETRY] RebuildTravelDialog not found on TravelButtonUI");
                return;
            }

            m.Invoke(null, null);
            Debug.Log("[HOTFIX-RETRY] invoked RebuildTravelDialog");
        }
        catch (Exception ex)
        {
            Debug.LogError("[HOTFIX-RETRY] RebuildTravelDialog invoke err: " + ex);
        }
    }

    private void InvokeDetect(Type tbType)
    {
        try
        {
            var m = tbType.GetMethod("DetectSceneVariant", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.Log("[HOTFIX-RETRY] DetectSceneVariant not found on TravelButtonUI");
                return;
            }

            var scene = SceneManager.GetActiveScene();
            object result = null;
            var ps = m.GetParameters();

            if (ps.Length == 1 && ps[0].ParameterType == typeof(Scene))
            {
                result = m.Invoke(null, new object[] { scene });
            }
            else if (ps.Length == 3)
            {
                // signature (Scene, string normalName, string destroyedName)
                result = m.Invoke(null, new object[] { scene, null, null });
            }
            else
            {
                // best-effort: try (Scene, null, null) then (Scene)
                try { result = m.Invoke(null, new object[] { scene, null, null }); }
                catch { try { result = m.Invoke(null, new object[] { scene }); } catch { result = null; } }
            }

            Debug.Log("[HOTFIX-RETRY] DetectSceneVariant result: " + (result?.ToString() ?? "null"));
        }
        catch (Exception ex)
        {
            Debug.LogError("[HOTFIX-RETRY] DetectSceneVariant invoke err: " + ex);
        }
    }
}
