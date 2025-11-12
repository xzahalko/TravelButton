using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Simple modal dialog. For production you should reuse the game's modal/dialog system.
// This dialog lists all cities from ConfigManager.Config and handles click/price checks.
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
        // fullscreen panel
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

        // dialog window centered
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

        // Scroll area for city list
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

        // Store references
        this.panel = panel;
        panel.SetActive(false);
    }

    private void RefreshList()
    {
        // Find content container
        var content = panel.GetComponentInChildren<ScrollRect>().content;
        // clear existing children
        foreach (Transform t in content) Destroy(t.gameObject);

        var cfg = ConfigManager.Config;
        foreach (var kv in cfg.cities)
        {
            var cityName = kv.Key;
            var cityCfg = kv.Value;
            var itemGO = new GameObject("CityItem");
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

            // Set interactable only if (visited OR enabled in config) AND coords available
            bool interactable = (visited || allowedByConfig) && coordsAvailable;

            btn.interactable = interactable;
            if (!interactable)
            {
                img.color = new Color(0.8f, 0.8f, 0.8f, 1f); // disabled look
            }

            // Add click handler
            btn.onClick.AddListener(() => OnCityClicked(cityName));

            // tooltip or small label for locked/unlocked
            if (!coordsAvailable)
            {
                // mark unavailable
                var warnGO = new GameObject("Warn");
                warnGO.transform.SetParent(itemGO.transform, false);
                var warnTxt = warnGO.AddComponent<Text>();
                warnTxt.text = " (coords missing)";
                warnTxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                warnTxt.color = Color.red;
                warnTxt.alignment = TextAnchor.MiddleCenter;
                var wr = warnGO.GetComponent<RectTransform>();
                wr.anchorMin = new Vector2(0.4f, 0);
                wr.anchorMax = new Vector2(0.6f, 1);
                wr.offsetMin = Vector2.zero;
                wr.offsetMax = Vector2.zero;
            }
        }

        // Close button at bottom
        var window = panel.GetComponentInChildren<Image>(); // window image
        var closeBtnGO = new GameObject("CloseButton");
        closeBtnGO.transform.SetParent(window.transform, false);
        var closeBtn = closeBtnGO.AddComponent<Button>();
        var closeImg = closeBtnGO.AddComponent<Image>();
        closeImg.color = new Color(0.9f, 0.2f, 0.2f);
        var cRect = closeBtnGO.GetComponent<RectTransform>();
        cRect.anchorMin = new Vector2(0.5f, 0f);
        cRect.anchorMax = new Vector2(0.5f, 0f);
        cRect.pivot = new Vector2(0.5f, 0f);
        cRect.anchoredPosition = new Vector2(0, 10);
        cRect.sizeDelta = new Vector2(160, 36);

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

        closeBtn.onClick.AddListener(Close);
    }

    private void OnCityClicked(string cityName)
    {
        var cfg = ConfigManager.Config;
        if (!cfg.cities.ContainsKey(cityName)) return;
        var cityCfg = cfg.cities[cityName];
        int price = cityCfg.price ?? cfg.globalTeleportPrice;

        // Check coords
        if (cityCfg.coords == null || cityCfg.coords.Length < 3)
        {
            Debug.LogWarning($"[TravelButton] City {cityName} has no coordinates configured.");
            // keep dialog open, maybe show a message
            ShowMessage($"Location for {cityName} is not configured.");
            return;
        }

        // Check player's inventory/currency. Replace this with your real inventory API.
        if (!PlayerHasCurrency(cfg.currencyItem, price))
        {
            ShowMessage("not enough resources to travel");
            return;
        }

        // Deduct currency
        if (!RemovePlayerCurrency(cfg.currencyItem, price))
        {
            ShowMessage("not enough resources to travel");
            return;
        }

        // Teleport
        var success = TeleportManager.TeleportPlayerTo(cityCfg.coords);
        if (success)
        {
            VisitedTracker.MarkVisited(cityName);
            Close();
        }
        else
        {
            ShowMessage("Teleport failed");
        }
    }

    private void ShowMessage(string msg)
    {
        // Use the game's message UI if you have one. Here we log and also show a simple on-screen label.
        Debug.Log("[TravelButton] " + msg);
        // TODO: integrate with the game's message system; as fallback can display a temporary label.
    }

    // Placeholder inventory check. Replace with actual game inventory API.
    private bool PlayerHasCurrency(string currencyItem, int amount)
    {
        // Example: check PlayerInventory for count of item named currencyItem
        var player = GameObject.FindWithTag("Player");
        if (player == null) return false;

        // Replace the following logic with the actual inventory lookup
        var inv = player.GetComponent<PlayerInventory>();
        if (inv == null) return false;
        return inv.GetItemCount(currencyItem) >= amount;
    }

    private bool RemovePlayerCurrency(string currencyItem, int amount)
    {
        var player = GameObject.FindWithTag("Player");
        if (player == null) return false;
        var inv = player.GetComponent<PlayerInventory>();
        if (inv == null) return false;
        return inv.RemoveItems(currencyItem, amount);
    }
}