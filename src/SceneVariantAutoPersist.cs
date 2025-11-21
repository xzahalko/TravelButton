using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// SceneVariantAutoPersist
/// - Subscribe to SceneManager.sceneLoaded and run detection/persist after a short delay (1 frame + optional additional wait).
/// - Avoids Harmony; works for natural scene entry and other non-teleport loads.
/// - Uses ExtraSceneVariantDetection / ExtraSceneVariantDiagnostics and CitiesJsonManager.UpdateCityVariantData.
/// 
/// Usage:
/// - Add this file to your plugin project and ensure the assembly contains ExtraSceneVariantDetection,
///   ExtraSceneVariantDiagnostics, and CitiesJsonManager (from earlier helper code).
/// - Create/attach an instance by calling SceneVariantAutoPersist.EnsureExists() from your plugin Awake() or similar.
/// </summary>
public class SceneVariantAutoPersist : MonoBehaviour
{
    // configure blacklist here (scene names that should not be persisted)
    static readonly string[] SceneBlacklist = new[] {
        "MainMenu_Empty",
        "LowMemory_TransitionScene",
        "LowMemory_TransitionScene(Clone)"
    };

    // additional wait seconds after a frame to let managers settle (set small, e.g. 0.05)
    const float extraWaitSeconds = 0.05f;

    // singleton instance helper
    static SceneVariantAutoPersist instance;

    public static void EnsureExists()
    {
        if (instance != null) return;
        var go = new GameObject("SceneVariantAutoPersist");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<SceneVariantAutoPersist>();
    }

    void Awake()
    {
        // subscribe
        SceneManager.sceneLoaded += OnSceneLoaded;
        Debug.Log("[SceneVariantAutoPersist] Awake: subscribed to SceneManager.sceneLoaded");
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Debug.Log("[SceneVariantAutoPersist] OnDestroy: unsubscribed from SceneManager.sceneLoaded");
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            if (IsBlacklisted(scene.name))
            {
                Debug.Log($"[SceneVariantAutoPersist] Scene '{scene.name}' is blacklisted; skipping detection/persist.");
                return;
            }

            // start coroutine so we can wait one frame and a small delay for stabilization
            StartCoroutine(HandleSceneLoadedCoroutine(scene));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneVariantAutoPersist] OnSceneLoaded exception: " + ex);
        }
    }

    public static IEnumerator HandleSceneLoadedCoroutine(Scene scene)
    {
        // wait one frame so Awake/Start finish; some objects set up on first frame
        yield return null;

        // optional tiny extra wait to let managers finish initialization
        if (extraWaitSeconds > 0f) yield return new WaitForSecondsRealtime(extraWaitSeconds);

        try
        {
            Debug.Log($"[SceneVariantAutoPersist] Running detection for scene '{scene.name}'");

            // Attempt detection using the lightweight detector
            var (normalName, destroyedName) = ExtraSceneVariantDetection.DetectVariantNames(scene, scene.name);

            // If you want more verbose diagnostics (writes a file) use DetectAndDump:
            // var variantEnum = ExtraSceneVariantDiagnostics.DetectAndDump(scene);
            // But DetectAndDump writes diagnostics files; prefer the lightweight detection here.
            var diagVariant = ExtraSceneVariantDiagnostics.DetectAndDump(scene); // this also writes diagnostic file & returns final variant
            string finalVariantStr = diagVariant.ToString(); // "Normal"/"Destroyed"/"Unknown"

            Debug.Log($"[SceneVariantAutoPersist] Detected pair Normal='{normalName ?? "<null>"}' Destroyed='{destroyedName ?? "<null>"}' FinalVariant={finalVariantStr}");

            // Persist to JSON only when we have a matching city entry and values differ.
            bool persisted = CitiesJsonManager.UpdateCityVariantData(scene.name, normalName, destroyedName, finalVariantStr);
            Debug.Log($"[SceneVariantAutoPersist] CitiesJsonManager.UpdateCityVariantData returned persisted={persisted} for scene '{scene.name}'");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SceneVariantAutoPersist] HandleSceneLoadedCoroutine exception: " + ex);
        }
    }

    static bool IsBlacklisted(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return true;
        foreach (var s in SceneBlacklist)
            if (string.Equals(s, sceneName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}