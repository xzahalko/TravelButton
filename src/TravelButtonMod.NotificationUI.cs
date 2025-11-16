using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class TravelButtonNotificationUI : MonoBehaviour
{
    private static TravelButtonNotificationUI instance;
    private Canvas canvas;
    private Text msgText;
    private Coroutine hideCoroutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Init()
    {
        if (instance != null) return;
        var go = new GameObject("TravelButton_NotificationUI");
        UnityEngine.Object.DontDestroyOnLoad(go);
        instance = go.AddComponent<TravelButtonNotificationUI>();
        instance.SetupCanvas();
    }

    private void SetupCanvas()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10000;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var ray = gameObject.AddComponent<GraphicRaycaster>();

        var goText = new GameObject("TravelButton_NotificationText");
        goText.transform.SetParent(this.transform, false);
        msgText = goText.AddComponent<Text>();
        msgText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        msgText.fontSize = 26;
        msgText.color = Color.white;
        msgText.alignment = TextAnchor.UpperCenter;
        var rt = msgText.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -60f);
        rt.sizeDelta = new Vector2(1000f, 200f);
        msgText.raycastTarget = false;
    }

    public static void Show(string text, float seconds = 3f)
    {
        try
        {
            if (instance == null)
            {
                Init();
            }
            instance.ShowInstance(text, seconds);
        }
        catch (Exception ex)
        {
            try { TBLog.Warn("[TravelButton] Notification Show failed: " + ex.Message); } catch { Debug.LogWarning("[TravelButton] Notification Show failed: " + ex); }
        }
    }

    private void ShowInstance(string text, float seconds)
    {
        if (hideCoroutine != null) StopCoroutine(hideCoroutine);
        msgText.text = text;
        msgText.color = Color.white;
        msgText.gameObject.SetActive(true);
        hideCoroutine = StartCoroutine(HideAfter(seconds));
    }

    private IEnumerator HideAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        // simple fade
        float t = 0f;
        Color start = msgText.color;
        while (t < 0.4f)
        {
            t += Time.deltaTime;
            msgText.color = Color.Lerp(start, new Color(start.r, start.g, start.b, 0f), t / 0.4f);
            yield return null;
        }
        msgText.gameObject.SetActive(false);
        hideCoroutine = null;
    }
}