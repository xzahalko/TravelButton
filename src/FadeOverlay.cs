using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal singleton full-screen black overlay used to hide scene activation/teleport flashes.
/// Usage:
///   FadeOverlay.Instance.ShowInstant();    // show black immediately
///   FadeOverlay.Instance.HideInstant();    // hide immediately
///   StartCoroutine(FadeOverlay.Instance.FadeIn(0.2f));  // fade to black
///   StartCoroutine(FadeOverlay.Instance.FadeOut(0.2f)); // fade to transparent
/// This creates a Canvas in DontDestroyOnLoad so it persists across scene loads.
/// </summary>
public class FadeOverlay : MonoBehaviour
{
    private static FadeOverlay _instance;
    public static FadeOverlay Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("TravelButton_FadeOverlay");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<FadeOverlay>();
                _instance.CreateUI();
            }
            return _instance;
        }
    }

    private Canvas _canvas;
    private Image _image;
    private CanvasGroup _group;
    private Coroutine _running;

    private void CreateUI()
    {
        // Canvas
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = short.MaxValue; // topmost

        gameObject.AddComponent<CanvasScaler>();
        gameObject.AddComponent<GraphicRaycaster>();

        // Root panel
        var panelGO = new GameObject("Overlay");
        panelGO.transform.SetParent(transform, false);
        _image = panelGO.AddComponent<Image>();
        _image.color = Color.black;

        var rect = panelGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _group = panelGO.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = true;
        _group.interactable = true;

        // start hidden
        _group.alpha = 0f;
        _image.enabled = true;
    }

    // Immediate show (no fade)
    public void ShowInstant()
    {
        EnsureInstance();
        if (_running != null) StopCoroutine(_running);
        _group.alpha = 1f;
        _group.blocksRaycasts = true;
        _group.interactable = true;
    }

    // Immediate hide (no fade)
    public void HideInstant()
    {
        EnsureInstance();
        if (_running != null) StopCoroutine(_running);
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;
    }

    // Fade to black (alpha -> 1)
    public IEnumerator FadeIn(float duration)
    {
        EnsureInstance();
        if (_running != null) StopCoroutine(_running);
        _running = StartCoroutine(FadeRoutine(1f, duration));
        yield return _running;
    }

    // Fade out to transparent (alpha -> 0)
    public IEnumerator FadeOut(float duration)
    {
        EnsureInstance();
        if (_running != null) StopCoroutine(_running);
        _running = StartCoroutine(FadeRoutine(0f, duration));
        yield return _running;
    }

    private IEnumerator FadeRoutine(float targetAlpha, float duration)
    {
        _group.blocksRaycasts = true;
        _group.interactable = true;

        float start = _group.alpha;
        float elapsed = 0f;
        if (duration <= 0f)
        {
            _group.alpha = targetAlpha;
            _group.blocksRaycasts = targetAlpha > 0.5f;
            _group.interactable = targetAlpha > 0.5f;
            yield break;
        }

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            _group.alpha = Mathf.Lerp(start, targetAlpha, t);
            yield return null;
        }

        _group.alpha = targetAlpha;
        _group.blocksRaycasts = targetAlpha > 0.5f;
        _group.interactable = targetAlpha > 0.5f;
    }

    private void EnsureInstance()
    {
        if (_instance == null)
            _instance = this;
        if (_group == null) CreateUI();
    }
}