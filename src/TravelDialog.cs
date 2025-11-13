using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// TravelDialog: lists configured cities and initiates teleport+post-charge flow.
/// Behavior:
///  - Shows all cities from ConfigManager.Config
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

        var cfg = ConfigManager.Config;
        // create one entry per configured city (dictionary preserves names)
        foreach (var kv in cfg.cities)
        {
            var cityName = kv.Key;
            var cityCfg = kv.Value;

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
            var priceToShow = cityCfg.price ?? cfg.globalTeleportPrice;
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
            bool allowedByConfig = cityCfg.enabled;
            bool coordsAvailable = cityCfg.coords != null && cityCfg.coords.Length >= 3;
            bool targetGOAvailable = false;
            if (!coordsAvailable && !string.IsNullOrEmpty(cityCfg.targetGameObjectName))
            {
                var tg = GameObject.Find(cityCfg.targetGameObjectName);
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
        var cfg = ConfigManager.Config;
        if (!cfg.cities.ContainsKey(cityName)) return;
        var cityCfg = cfg.cities[cityName];
        int price = cityCfg.price ?? cfg.globalTeleportPrice;

        if ((cityCfg.coords == null || cityCfg.coords.Length < 3) && string.IsNullOrEmpty(cityCfg.targetGameObjectName))
        {
            ShowMessage($"Location for {cityName} is not configured.");
            return;
        }

        if (!string.IsNullOrEmpty(cityCfg.targetGameObjectName))
        {
            var tg = GameObject.Find(cityCfg.targetGameObjectName);
            if (tg == null && (cityCfg.coords == null || cityCfg.coords.Length < 3))
            {
                ShowMessage($"Location for {cityName} is not configured.");
                return;
            }
        }

        // Check player's inventory/currency (best-effort). If known and insufficient -> message.
        long pm = GetPlayerCurrencyAmountOrMinusOne();
        if (pm >= 0 && pm < price)
        {
            ShowMessage("not enough resources to travel");
            return;
        }

        // Build a small stub object that contains the fields the teleport helper expects.
        var stub = new CityStub
        {
            name = cityName,
            coords = cityCfg.coords,
            targetGameObjectName = cityCfg.targetGameObjectName
        };

        // Use TeleportHelpersBehaviour to perform the teleport coroutine (it accepts object and uses reflection)
        TeleportHelpersBehaviour host = TeleportHelpersBehaviour.GetOrCreateHost();
        Vector3 hint = (cityCfg.coords != null && cityCfg.coords.Length >= 3) ? new Vector3(cityCfg.coords[0], cityCfg.coords[1], cityCfg.coords[2]) : Vector3.zero;

        host.StartCoroutine(host.EnsureSceneAndTeleport(stub, hint, cityCfg.coords != null && cityCfg.coords.Length >= 3, success =>
        {
            if (success)
            {
                // Mark visited
                try { VisitedTracker.MarkVisited(cityName); } catch { }

                // attempt to deduct using reflection heuristics and the configured currency item name
                bool charged = AttemptDeductAfterTeleport(price, cfg.currencyItem);
                if (!charged)
                {
                    ShowMessage($"Teleported to {cityName} (failed to charge {price} {cfg.currencyItem})");
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
    // Uses reflection heuristics; `currencyItemName` is taken from ConfigManager.Config.currencyItem.
    private bool AttemptDeductAfterTeleport(int amount, string currencyItemName)
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
            TravelButtonMod.LogWarning("AttemptDeductAfterTeleport exception: " + ex);
        }

        // Nothing found / deducted
        return false;
    }

    // Best-effort currency amount detection used to show early "not enough resources"
    private long GetPlayerCurrencyAmountOrMinusOne()
    {
        try
        {
            var allMono = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMono)
            {
                var t = mb.GetType();
                string[] propNames = new string[] { "Silver", "Money", "Gold", "Coins", "Currency", "CurrentMoney", "SilverAmount", "MoneyAmount" };
                foreach (var pn in propNames)
                {
                    var pi = t.GetProperty(pn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (pi != null && pi.CanRead)
                    {
                        try
                        {
                            var val = pi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                        catch { }
                    }
                }

                string[] methodNames = new string[] { "GetMoney", "GetSilver", "GetCoins", "GetCurrency" };
                foreach (var mn in methodNames)
                {
                    var mi = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (mi != null && mi.GetParameters().Length == 0)
                    {
                        try
                        {
                            var res = mi.Invoke(mb, null);
                            if (res is int) return (int)res;
                            if (res is long) return (long)res;
                            if (res is float) return (long)((float)res);
                            if (res is double) return (long)((double)res);
                        }
                        catch { }
                    }
                }

                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    var name = fi.Name.ToLower();
                    if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency"))
                    {
                        try
                        {
                            var val = fi.GetValue(mb);
                            if (val is int) return (int)val;
                            if (val is long) return (long)val;
                            if (val is float) return (long)((float)val);
                            if (val is double) return (long)((double)val);
                        }
                        catch { }
                    }
                }
            }

            TravelButtonMod.LogWarning("GetPlayerCurrencyAmountOrMinusOne: could not detect a currency field/property automatically.");
            return -1;
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("GetPlayerCurrencyAmountOrMinusOne exception: " + ex);
            return -1;
        }
    }

    private void ShowMessage(string msg)
    {
        Debug.Log("[TravelButton] " + msg);
    }
}