using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// TravelDialog: lists configured cities and initiates teleport+post-charge flow.
/// Behavior:
///  - Shows all cities from TravelButtonMod.Cities
///  - City button is interactable only when (visited OR enabled in config) AND coords/target exist
///  - On click: if detected player money is known and insufficient -> show "not enough resources to travel"
///            otherwise start teleport coroutine (TeleportHelpersBehaviour) and AFTER successful teleport attempt to deduct currency
///  - Close button at bottom
/// </summary>
public class TravelDialog : MonoBehaviour
{
    private static TravelDialog instance;
    private GameObject panel;

    public static void Open()
    {
        if (instance == null)
        {
            var go = new GameObject("TravelDialog");
            instance = go.AddComponent<TravelDialog>();
            instance.CreateUI();
        }
        instance.panel.SetActive(true);
        instance.RefreshList();
    }

    public static void Close()
    {
        if (instance != null && instance.panel != null)
            instance.panel.SetActive(false);
    }

    private void CreateUI()
    {
        panel = new GameObject("Panel");
        panel.transform.SetParent(this.transform, false);
        var canvas = panel.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        panel.AddComponent<CanvasScaler>();
        panel.AddComponent<GraphicRaycaster>();

        var bgGO = new GameObject("Background");
        bgGO.transform.SetParent(panel.transform, false);
        var bg = bgGO.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.6f);
        var bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;

        // centered window
        var windowGO = new GameObject("Window");
        windowGO.transform.SetParent(panel.transform, false);
        var windowImg = windowGO.AddComponent<Image>();
        windowImg.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        var wRect = windowGO.GetComponent<RectTransform>();
        wRect.sizeDelta = new Vector2(600, 400);
        wRect.anchorMin = new Vector2(0.5f, 0.5f);
        wRect.anchorMax = new Vector2(0.5f, 0.5f);
        wRect.pivot = new Vector2(0.5f, 0.5f);
        wRect.anchoredPosition = Vector2.zero;

        // scroll area
        var scrollGO = new GameObject("Scroll");
        scrollGO.transform.SetParent(windowGO.transform, false);
        var scrollRect = scrollGO.AddComponent<RectTransform>();
        scrollRect.anchorMin = new Vector2(0, 0.2f);
        scrollRect.anchorMax = new Vector2(1, 0.9f);
        scrollRect.offsetMin = new Vector2(10, 10);
        scrollRect.offsetMax = new Vector2(-10, -10);
        var scroll = scrollGO.AddComponent<ScrollRect>();
        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(scrollGO.transform, false);
        var vpImage = viewport.AddComponent<Image>();
        vpImage.color = new Color(1, 1, 1, 0f);
        var vpRect = viewport.GetComponent<RectTransform>();
        vpRect.anchorMin = Vector2.zero;
        vpRect.anchorMax = Vector2.one;
        vpRect.offsetMin = Vector2.zero;
        vpRect.offsetMax = Vector2.zero;
        scroll.viewport = vpRect;

        var content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var contentRect = content.AddComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0, 0);
        scroll.content = contentRect;

        var vlayout = content.AddComponent<VerticalLayoutGroup>();
        vlayout.childControlHeight = true;
        vlayout.childForceExpandHeight = false;
        vlayout.childControlWidth = true;
        vlayout.spacing = 6;
        var csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.horizontal = false;
        scroll.vertical = true;

        this.panel = panel;
        panel.SetActive(false);
    }

    private void RefreshList()
    {
        var content = panel.GetComponentInChildren<ScrollRect>().content;
        // clear previous
        foreach (Transform t in content) Destroy(t.gameObject);

        var cities = TravelButton.Cities;
        if (cities == null || cities.Count == 0)
        {
            TBLog.Warn("TravelDialog.RefreshList: no cities available");
            return;
        }

        // create one entry per configured city
        foreach (var city in cities)
        {
            if (city == null) continue;
            var cityName = city.name;

            var itemGO = new GameObject("CityItem_" + cityName);
            itemGO.transform.SetParent(content, false);
            var itemRect = itemGO.AddComponent<RectTransform>();
            itemRect.sizeDelta = new Vector2(0, 40);

            var btn = itemGO.AddComponent<Button>();
            var img = itemGO.AddComponent<Image>();
            img.color = Color.white;

            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(itemGO.transform, false);
            var txt = labelGO.AddComponent<Text>();
            txt.text = cityName;
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.color = Color.black;
            txt.alignment = TextAnchor.MiddleLeft;
            var lRect = labelGO.GetComponent<RectTransform>();
            lRect.anchorMin = new Vector2(0, 0);
            lRect.anchorMax = new Vector2(0.6f, 1);
            lRect.offsetMin = new Vector2(10, 0);
            lRect.offsetMax = new Vector2(0, 0);

            var priceGO = new GameObject("Price");
            priceGO.transform.SetParent(itemGO.transform, false);
            var ptxt = priceGO.AddComponent<Text>();
            var priceToShow = city.price ?? TravelButton.cfgTravelCost.Value;
            ptxt.text = priceToShow.ToString();
            ptxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            ptxt.color = Color.black;
            ptxt.alignment = TextAnchor.MiddleRight;
            var pRect = priceGO.GetComponent<RectTransform>();
            pRect.anchorMin = new Vector2(0.6f, 0);
            pRect.anchorMax = new Vector2(1, 1);
            pRect.offsetMin = new Vector2(-10, 0);
            pRect.offsetMax = new Vector2(-10, 0);

            bool visited = VisitedTracker.HasVisited(cityName);
            bool allowedByConfig = city.enabled;
            bool coordsAvailable = city.coords != null && city.coords.Length >= 3;
            bool targetGOAvailable = false;
            if (!coordsAvailable && !string.IsNullOrEmpty(city.targetGameObjectName))
            {
                var tg = GameObject.Find(city.targetGameObjectName);
                targetGOAvailable = tg != null;
            }

            bool interactable = (visited || allowedByConfig) && (coordsAvailable || targetGOAvailable);
            btn.interactable = interactable;
            if (!interactable) img.color = new Color(0.8f, 0.8f, 0.8f, 1f);

            btn.onClick.AddListener(() => OnCityClicked(cityName));
        }

        // Close button (bottom center)
        var closeBtnGO = new GameObject("CloseButton");
        closeBtnGO.transform.SetParent(panel.transform, false);
        var closeRt = closeBtnGO.AddComponent<RectTransform>();
        closeRt.anchorMin = new Vector2(0.5f, 0f);
        closeRt.anchorMax = new Vector2(0.5f, 0f);
        closeRt.pivot = new Vector2(0.5f, 0f);
        closeRt.anchoredPosition = new Vector2(0, 10);
        closeRt.sizeDelta = new Vector2(160, 36);
        var closeImg = closeBtnGO.AddComponent<Image>();
        closeImg.color = new Color(0.9f, 0.2f, 0.2f);
        var closeBtn = closeBtnGO.AddComponent<Button>();
        var cTextGO = new GameObject("Text");
        cTextGO.transform.SetParent(closeBtnGO.transform, false);
        var cText = cTextGO.AddComponent<Text>();
        cText.text = "Close";
        cText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        cText.color = Color.white;
        cText.alignment = TextAnchor.MiddleCenter;
        var ctRect = cTextGO.GetComponent<RectTransform>();
        ctRect.anchorMin = Vector2.zero;
        ctRect.anchorMax = Vector2.one;
        ctRect.offsetMin = Vector2.zero;
        ctRect.offsetMax = Vector2.zero;
        closeBtn.onClick.AddListener(() => { if (panel != null) panel.SetActive(false); });
    }

    private void OnCityClicked(string cityName)
    {
        var city = TravelButton.Cities?.Find(c => string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase));
        if (city == null)
        {
            ShowMessage($"City {cityName} not found.");
            return;
        }
        
        int price = city.price ?? TravelButton.cfgTravelCost.Value;

        if ((city.coords == null || city.coords.Length < 3) && string.IsNullOrEmpty(city.targetGameObjectName))
        {
            ShowMessage($"Location for {cityName} is not configured.");
            return;
        }

        if (!string.IsNullOrEmpty(city.targetGameObjectName))
        {
            var tg = GameObject.Find(city.targetGameObjectName);
            if (tg == null && (city.coords == null || city.coords.Length < 3))
            {
                ShowMessage($"Location for {cityName} is not configured.");
                return;
            }
        }

        // Check player's inventory/currency (best-effort). If known and insufficient -> message.
        long pm = CurrencyHelpers.GetPlayerCurrencyAmountOrMinusOne();
        if (pm >= 0 && pm < price)
        {
            ShowMessage("not enough resources to travel");
            return;
        }

        // Build a small stub object that contains the fields the teleport helper expects.
        var stub = new CityStub
        {
            name = cityName,
            coords = city.coords,
            targetGameObjectName = city.targetGameObjectName
        };

        // Use TeleportHelpersBehaviour to perform the teleport coroutine (it accepts object and uses reflection)
        TeleportHelpersBehaviour host = TeleportHelpersBehaviour.GetOrCreateHost();
        Vector3 hint = (city.coords != null && city.coords.Length >= 3) ? new Vector3(city.coords[0], city.coords[1], city.coords[2]) : Vector3.zero;

        host.StartCoroutine(host.EnsureSceneAndTeleport(stub, hint, city.coords != null && city.coords.Length >= 3, success =>
        {
            if (success)
            {
                // Mark visited
                try { VisitedTracker.MarkVisited(cityName); } catch { }

                // attempt to deduct using reflection heuristics and the configured currency item name
                string currencyItem = TravelButton.cfgCurrencyItem.Value;
                bool charged = AttemptDeductAfterTeleport(price, currencyItem);
                if (!charged)
                {
                    ShowMessage($"Teleported to {cityName} (failed to charge {price} {currencyItem})");
                }
                else
                {
                    ShowMessage($"Teleported to {cityName}");
                }
            }
            else
            {
                ShowMessage("Teleport failed");
            }
        }));
    }

    // Small city-like stub used only to pass data into EnsureSceneAndTeleport (which accepts object and uses reflection).
    private class CityStub
    {
        public string name;
        public float[] coords;
        public string targetGameObjectName;
    }

    // Attempt to deduct currency after a successful teleport.
    // Uses reflection heuristics; `currencyItemName` is taken from TravelButtonMod.cfgCurrencyItem.Value.
    public bool AttemptDeductAfterTeleport(int amount, string currencyItemName)
    {
        try
        {
            var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMonoBehaviours)
            {
                var t = mb.GetType();
                // Try inventory-like methods (string, int) then (int)
                string[] methodNames = new string[] {
                    "RemoveItems", "RemoveItem", "ConsumeItem", "TryRemoveItem", "RemoveAmount", "RemoveItemAmount",
                    "SpendItem", "TakeItem", "UseItem", "RemoveMoney", "SpendMoney", "RemoveSilver", "TakeSilver"
                };
                foreach (var mn in methodNames)
                {
                    var mi_sig_si = t.GetMethod(mn, new Type[] { typeof(string), typeof(int) });
                    if (mi_sig_si != null)
                    {
                        try
                        {
                            var res = mi_sig_si.Invoke(mb, new object[] { currencyItemName, amount });
                            if (res is bool b) return b;
                            return true;
                        }
                        catch { /* ignore and continue */ }
                    }

                    var mi_sig_i = t.GetMethod(mn, new Type[] { typeof(int) });
                    if (mi_sig_i != null)
                    {
                        try
                        {
                            mi_sig_i.Invoke(mb, new object[] { amount });
                            return true;
                        }
                        catch { /* ignore */ }
                    }
                }

                // Try numeric fields/properties named like money/silver
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    var name = fi.Name.ToLower();
                    if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency"))
                    {
                        try
                        {
                            if (fi.FieldType == typeof(int))
                            {
                                int cur = (int)fi.GetValue(mb);
                                if (cur >= amount)
                                {
                                    fi.SetValue(mb, cur - amount);
                                    return true;
                                }
                                else return false;
                            }
                            else if (fi.FieldType == typeof(long))
                            {
                                long cur = (long)fi.GetValue(mb);
                                if (cur >= amount)
                                {
                                    fi.SetValue(mb, cur - amount);
                                    return true;
                                }
                                else return false;
                            }
                        }
                        catch { /* ignore */ }
                    }
                }

                foreach (var pi in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    var name = pi.Name.ToLower();
                    if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency"))
                    {
                        try
                        {
                            if (pi.PropertyType == typeof(int) && pi.CanRead && pi.CanWrite)
                            {
                                int cur = (int)pi.GetValue(mb);
                                if (cur >= amount)
                                {
                                    pi.SetValue(mb, cur - amount, null);
                                    return true;
                                }
                                else return false;
                            }
                            else if (pi.PropertyType == typeof(long) && pi.CanRead && pi.CanWrite)
                            {
                                long cur = (long)pi.GetValue(mb);
                                if (cur >= amount)
                                {
                                    pi.SetValue(mb, cur - amount, null);
                                    return true;
                                }
                                else return false;
                            }
                        }
                        catch { /* ignore */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptDeductAfterTeleport exception: " + ex);
        }

        // Nothing found / deducted
        return false;
    }

    private void ShowMessage(string msg)
    {
        Debug.Log("[TravelButton] " + msg);
    }

    /// <summary>
    /// Simple full-screen fade helper that creates (once) a fullscreen black Image on a Canvas
    /// and animates its alpha from fromAlpha to toAlpha over duration (seconds).
    /// - fromAlpha / toAlpha in range [0,1]
    /// - Uses unscaled time so fades continue even if Time.timeScale changes.
    /// - This is a coroutine returning IEnumerator so callers can StartCoroutine(TeleportManager.ScreenFade(...))
    /// </summary>
    public static IEnumerator ScreenFade(float fromAlpha, float toAlpha, float duration)
    {
        // Ensure duration positive
        if (duration <= 0f)
        {
            // instant set: create/ensure overlay and set target alpha then exit
            var inst0 = EnsureFadeOverlay();
            SetOverlayAlpha(inst0.canvasGroup, toAlpha);
            yield break;
        }

        var inst = EnsureFadeOverlay();
        var cg = inst.canvasGroup;
        // initialize
        SetOverlayAlpha(cg, fromAlpha);
        inst.root.SetActive(true);

        float t = 0f;
        float start = Time.realtimeSinceStartup;
        while (t < 1f)
        {
            float now = Time.realtimeSinceStartup;
            t = Mathf.Clamp01((now - start) / duration);
            float a = Mathf.Lerp(fromAlpha, toAlpha, t);
            SetOverlayAlpha(cg, a);
            yield return null;
        }

        SetOverlayAlpha(cg, toAlpha);

        // if fully transparent, hide overlay to avoid blocking raycasts; if opaque keep visible.
        if (toAlpha <= 0f + 1e-6f)
        {
            inst.root.SetActive(false);
        }
        else
        {
            inst.root.SetActive(true);
        }
        yield break;
    }

    // Internal helper types / methods for the fade overlay
    private class FadeOverlayInstance
    {
        public GameObject root;
        public Canvas canvas;
        public CanvasGroup canvasGroup;
        public Image image;
    }

    private static FadeOverlayInstance _fadeOverlayInstance;
    private static FadeOverlayInstance EnsureFadeOverlay()
    {
        if (_fadeOverlayInstance != null) return _fadeOverlayInstance;

        // Create a persistent fullscreen canvas with an Image child and CanvasGroup
        var go = new GameObject("TeleportFadeOverlay");
        GameObject.DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10000; // ensure on top

        var cg = go.AddComponent<CanvasGroup>();
        cg.blocksRaycasts = true; // while visible block raycasts to prevent interactions

        // Create full-screen Image
        var imgGO = new GameObject("FadeImage");
        imgGO.transform.SetParent(go.transform, false);
        var img = imgGO.AddComponent<Image>();
        img.color = Color.black;
        // stretch to full screen
        var rect = img.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _fadeOverlayInstance = new FadeOverlayInstance
        {
            root = go,
            canvas = canvas,
            canvasGroup = cg,
            image = img
        };

        // start hidden
        go.SetActive(false);
        SetOverlayAlpha(cg, 0f);

        return _fadeOverlayInstance;
    }

    private static void SetOverlayAlpha(CanvasGroup cg, float alpha)
    {
        if (cg == null) return;
        cg.alpha = Mathf.Clamp01(alpha);
    }
}
