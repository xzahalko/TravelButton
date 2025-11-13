using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Linq;

/// <summary>
/// UI helper MonoBehaviour responsible for injecting a Travel button into the Inventory UI.
/// - Travel button is hidden on main screen and only appears when the inventory UI is found and visible.
/// - Reparents to inventory so it appears inside inventory layout (top/right by template heuristics).
/// - Dialog lists all cities. Buttons are interactable only when (visited OR enabled in config) AND coordinates (or target GameObject) exist.
/// - Clicking a city: check funds -> teleport -> deduct funds (post-teleport). If funds insufficient -> show "not enough resources to travel".
/// </summary>
public class TravelButtonUI : MonoBehaviour
{
    private Button travelButton;
    private GameObject buttonObject;

    // Dialog UI root (created at runtime)
    private GameObject dialogRoot;
    private GameObject dialogCanvas; // dedicated canvas for dialogs

    // Inventory parenting tracking
    private Transform inventoryContainer;
    private bool inventoryParentFound = false;

    // The real GameObject we watch for visibility changes (window, panel, or an object with CanvasGroup)
    private GameObject inventoryVisibilityTarget;

    // Coroutine that refreshes city button interactability while dialog is open
    private Coroutine refreshButtonsCoroutine;

    // Fallback visibility monitor coroutine when inventoryVisibilityTarget is not found
    private Coroutine visibilityMonitorCoroutine;

    void Start()
    {
        TravelButtonMod.LogInfo("TravelButtonUI.Start called.");
        CreateTravelButton();
        EnsureInputSystems();
        // start polling for inventory container (will reparent once found)
        StartCoroutine(PollForInventoryParent());
    }

    void Update()
    {
        // hotkey to open dialog for debugging
        if (Input.GetKeyDown(KeyCode.BackQuote))
        {
            TravelButtonMod.LogInfo("BackQuote key pressed - opening travel dialog.");
            OpenTravelDialog();
        }

        // If we have an explicit visibility target, sync the button active state to it
        if (inventoryParentFound && inventoryVisibilityTarget != null && buttonObject != null)
        {
            try
            {
                bool visible = inventoryVisibilityTarget.activeInHierarchy;
                var cg = inventoryVisibilityTarget.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    visible = cg.alpha > 0.01f && cg.interactable;
                }

                if (buttonObject.activeSelf != visible)
                    buttonObject.SetActive(visible);
            }
            catch (Exception ex)
            {
                TravelButtonMod.LogWarning("Visibility sync error: " + ex);
            }
        }
    }

    // Poll every 0.5s for the inventory GameObject by common names
    private IEnumerator PollForInventoryParent()
    {
        string[] inventoryNames = new string[] {
            "InventoryUI", "Inventory", "InventoryCanvas", "UI Inventory", "Inventory_Window", "InventoryWindow", "InventoryPanel"
        };

        while (!inventoryParentFound)
        {
            foreach (var name in inventoryNames)
            {
                var go = GameObject.Find(name);
                if (go != null)
                {
                    inventoryParentFound = true;
                    inventoryContainer = go.transform;
                    TravelButtonMod.LogInfo($"PollForInventoryParent: found inventory parent '{name}', reparenting button.");
                    ReparentButtonToInventory(inventoryContainer);
                    yield break;
                }
            }
            // small delay
            yield return new WaitForSeconds(0.5f);
        }
    }

    private void ReparentButtonToInventory(Transform container)
    {
        try
        {
            if (buttonObject == null) return;

            // Stop any existing visibility monitor (we'll start a new one if needed)
            if (visibilityMonitorCoroutine != null)
            {
                try { StopCoroutine(visibilityMonitorCoroutine); } catch { }
                visibilityMonitorCoroutine = null;
            }

            // Parent and configure layout participation
            buttonObject.transform.SetParent(container, false);
            buttonObject.transform.SetAsLastSibling();

            // Ensure the button participates in layout groups correctly
            var layoutElement = buttonObject.GetComponent<LayoutElement>();
            if (layoutElement == null)
                layoutElement = buttonObject.AddComponent<LayoutElement>();

            var rt = buttonObject.GetComponent<RectTransform>();

            // Try to find a template button to copy layout; otherwise apply defaults
            Button templateButton = null;
            try
            {
                var buttons = container.GetComponentsInChildren<Button>(true);
                if (buttons != null && buttons.Length > 0)
                    templateButton = buttons[0];
            }
            catch { /* ignore */ }

            if (templateButton != null)
            {
                var tRt = templateButton.GetComponent<RectTransform>();
                if (tRt != null)
                {
                    // copy anchors/pivot/size but clamp to sane maxima to avoid giant buttons
                    rt.anchorMin = tRt.anchorMin;
                    rt.anchorMax = tRt.anchorMax;
                    rt.pivot = tRt.pivot;
                    var copied = tRt.sizeDelta;
                    float maxWidth = 220f;
                    float maxHeight = 44f;
                    copied.x = Mathf.Clamp(copied.x, 60f, maxWidth);
                    copied.y = Mathf.Clamp(copied.y, 20f, maxHeight);
                    rt.sizeDelta = copied;

                    // place to the right of the template (so it shows on the same row near the top/right)
                    rt.anchoredPosition = tRt.anchoredPosition + new Vector2(tRt.sizeDelta.x + 8f, 0f);

                    layoutElement.preferredWidth = rt.sizeDelta.x;
                    layoutElement.preferredHeight = rt.sizeDelta.y;
                    layoutElement.flexibleWidth = 0;
                    layoutElement.flexibleHeight = 0;
                }

                // copy image sprite if template uses one
                try
                {
                    var templImg = templateButton.GetComponent<Image>();
                    var ourImg = buttonObject.GetComponent<Image>();
                    if (templImg != null && ourImg != null && templImg.sprite != null)
                    {
                        ourImg.sprite = templImg.sprite;
                        ourImg.type = templImg.type;
                        ourImg.preserveAspect = templImg.preserveAspect;
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                // default fallback size, positioned near top-right of container
                rt.sizeDelta = new Vector2(Mathf.Min(rt.sizeDelta.x, 160f), Mathf.Min(rt.sizeDelta.y, 34f));
                layoutElement.preferredWidth = rt.sizeDelta.x;
                layoutElement.preferredHeight = rt.sizeDelta.y;
                layoutElement.flexibleWidth = 0;
                layoutElement.flexibleHeight = 0;
                TravelButtonMod.LogInfo("ReparentButtonToInventory: no template button found, used default layout sizes.");
            }

            // Determine inventory visibility target (ancestor/child with CanvasGroup or named window/panel)
            TryFindInventoryVisibilityTarget(container);

            // Sync visibility based on the discovered target (or fallback)
            if (inventoryVisibilityTarget != null)
            {
                try
                {
                    bool visible = inventoryVisibilityTarget.activeInHierarchy;
                    var cg = inventoryVisibilityTarget.GetComponent<CanvasGroup>();
                    if (cg != null) visible = cg.alpha > 0.01f && cg.interactable;
                    buttonObject.SetActive(visible);
                }
                catch (Exception ex)
                {
                    TravelButtonMod.LogWarning("ReparentButtonToInventory: failed to sync visibility from found inventoryVisibilityTarget: " + ex);
                    buttonObject.SetActive(true);
                }
            }
            else
            {
                // fallback: use container.activeInHierarchy and monitor
                try
                {
                    bool visible = container.gameObject.activeInHierarchy;
                    buttonObject.SetActive(visible);
                    visibilityMonitorCoroutine = StartCoroutine(MonitorInventoryContainerVisibility(container));
                    TravelButtonMod.LogInfo($"ReparentButtonToInventory: using container '{container.name}' active state as fallback (visible={visible}).");
                }
                catch (Exception ex)
                {
                    TravelButtonMod.LogWarning("ReparentButtonToInventory: fallback visibility check failed: " + ex);
                    buttonObject.SetActive(false); // safer default - do not show on main hud
                }
            }

            TravelButtonMod.LogInfo("ReparentButtonToInventory: button reparented and visibility synced with inventory.");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("ReparentButtonToInventory: " + ex);
        }
    }

    private IEnumerator MonitorInventoryContainerVisibility(Transform container)
    {
        if (container == null || buttonObject == null) yield break;

        while (true)
        {
            try
            {
                bool visible = container.gameObject.activeInHierarchy;
                var cg = container.GetComponentInChildren<CanvasGroup>(true);
                if (cg != null)
                    visible = cg.alpha > 0.01f && cg.interactable && cg.gameObject.activeInHierarchy;

                if (buttonObject.activeSelf != visible)
                {
                    buttonObject.SetActive(visible);
                    TravelButtonMod.LogDebug($"MonitorInventoryContainerVisibility: set TravelButton active={visible} (container='{container.name}').");
                }
            }
            catch (Exception ex)
            {
                TravelButtonMod.LogWarning("MonitorInventoryContainerVisibility exception: " + ex);
            }

            yield return new WaitForSeconds(0.5f);
        }
    }

    private void TryFindInventoryVisibilityTarget(Transform container)
    {
        try
        {
            var t = container;
            while (t != null)
            {
                if (t.name.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inventoryVisibilityTarget = t.gameObject;
                    TravelButtonMod.LogInfo($"TryFindInventoryVisibilityTarget: using ancestor '{t.name}' as visibility target.");
                    return;
                }

                if (t.GetComponent<CanvasGroup>() != null)
                {
                    inventoryVisibilityTarget = t.gameObject;
                    TravelButtonMod.LogInfo($"TryFindInventoryVisibilityTarget: using ancestor with CanvasGroup '{t.name}' as visibility target.");
                    return;
                }
                t = t.parent;
            }

            string[] childCandidates = new string[] { "Window", "Panel", "Root", "Background", "Content", "Main" };
            foreach (Transform child in container)
            {
                if (child == null) continue;
                var cname = child.name;
                foreach (var cand in childCandidates)
                {
                    if (cname.IndexOf(cand, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        inventoryVisibilityTarget = child.gameObject;
                        TravelButtonMod.LogInfo($"TryFindInventoryVisibilityTarget: using child '{cname}' as visibility target.");
                        return;
                    }
                }

                var cg = child.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    inventoryVisibilityTarget = child.gameObject;
                    TravelButtonMod.LogInfo($"TryFindInventoryVisibilityTarget: using child with CanvasGroup '{child.name}' as visibility target.");
                    return;
                }
            }

            var sibling = GameObject.Find("InventoryWindow") ?? GameObject.Find("Inventory_Window");
            if (sibling != null)
            {
                inventoryVisibilityTarget = sibling;
                TravelButtonMod.LogInfo($"TryFindInventoryVisibilityTarget: using sibling '{sibling.name}' as visibility target.");
                return;
            }

            TravelButtonMod.LogInfo("TryFindInventoryVisibilityTarget: no explicit visibility target found for inventory.");
            inventoryVisibilityTarget = null;
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("TryFindInventoryVisibilityTarget exception: " + ex);
            inventoryVisibilityTarget = null;
        }
    }

    private void EnsureInputSystems()
    {
        try
        {
            if (EventSystem.current == null)
            {
                TravelButtonMod.LogInfo("No EventSystem found - creating one.");
                var esGO = new GameObject("EventSystem");
                esGO.AddComponent<EventSystem>();
                esGO.AddComponent<StandaloneInputModule>();
                UnityEngine.Object.DontDestroyOnLoad(esGO);
            }

            var anyCanvas = UnityEngine.Object.FindObjectOfType<Canvas>();
            if (anyCanvas != null)
            {
                var gr = anyCanvas.GetComponent<GraphicRaycaster>();
                if (gr == null)
                {
                    TravelButtonMod.LogInfo("Canvas found but missing GraphicRaycaster - adding one.");
                    anyCanvas.gameObject.AddComponent<GraphicRaycaster>();
                }
            }
            else
            {
                TravelButtonMod.LogWarning("No Canvas found when ensuring input systems. UI may not be interactable until a Canvas exists.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("EnsureInputSystems exception: " + ex);
        }
    }

    void CreateTravelButton()
    {
        TravelButtonMod.LogInfo("CreateTravelButton: beginning UI creation.");
        try
        {
            buttonObject = new GameObject("TravelButton");
            buttonObject.AddComponent<CanvasRenderer>();

            travelButton = buttonObject.AddComponent<Button>();

            var img = buttonObject.AddComponent<Image>();
            img.color = new Color(0.45f, 0.26f, 0.13f, 1f);
            img.raycastTarget = true;

            travelButton.targetGraphic = img;
            travelButton.interactable = true;

            var rt = buttonObject.GetComponent<RectTransform>();
            if (rt == null) rt = buttonObject.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(140, 32);

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1) buttonObject.layer = uiLayer;

            // initially parent to first available Canvas (so it's created in UI space)
            var canvas = FindCanvas();
            if (canvas != null)
            {
                buttonObject.transform.SetParent(canvas.transform, false);
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0, -40);
            }
            else
            {
                TravelButtonMod.LogWarning("CreateTravelButton: no Canvas found at creation time; button created at scene root.");
            }

            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(buttonObject.transform, false);
            var txt = labelGO.AddComponent<Text>();
            txt.text = "Travel";
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = new Color(0.98f, 0.94f, 0.87f, 1.0f);
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = 14;
            txt.raycastTarget = false;

            EnsureInputSystems();

            travelButton.onClick.AddListener(OpenTravelDialog);

            // Hide the button until reparented to inventory - do not show on main HUD
            buttonObject.SetActive(false);

            TravelButtonMod.LogInfo("CreateTravelButton: Travel button created and hidden until inventory is found.");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("CreateTravelButton: exception: " + ex);
        }
    }

    void OpenTravelDialog()
    {
        TravelButtonMod.LogInfo("OpenTravelDialog: invoked via click or keyboard.");
        // Reuse the common dialog creation flow in this class (consistent with TravelDialog)
        try
        {
            // If dialog already exists, show it and refresh
            if (dialogRoot != null)
            {
                dialogRoot.SetActive(true);
                if (refreshButtonsCoroutine != null)
                {
                    StopCoroutine(refreshButtonsCoroutine);
                    refreshButtonsCoroutine = null;
                }
                refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));
                return;
            }

            // Create dialog canvas if required
            if (dialogCanvas == null)
            {
                dialogCanvas = new GameObject("TravelDialogCanvas");
                var canvasComp = dialogCanvas.AddComponent<Canvas>();
                canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
                canvasComp.overrideSorting = true;
                canvasComp.sortingOrder = 2000;
                dialogCanvas.AddComponent<GraphicRaycaster>();
                dialogCanvas.AddComponent<CanvasGroup>();
                UnityEngine.Object.DontDestroyOnLoad(dialogCanvas);
            }

            // Build dialog (simplified layout - same UX as TravelDialog)
            dialogRoot = new GameObject("TravelDialog");
            dialogRoot.transform.SetParent(dialogCanvas.transform, false);
            dialogRoot.AddComponent<CanvasRenderer>();
            var rootRt = dialogRoot.AddComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(520, 360);
            rootRt.anchoredPosition = Vector2.zero;
            var bg = dialogRoot.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.95f);

            // Title
            var titleGO = new GameObject("Title");
            titleGO.transform.SetParent(dialogRoot.transform, false);
            var titleRt = titleGO.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0, -8);
            titleRt.sizeDelta = new Vector2(0, 32);
            var titleText = titleGO.AddComponent<Text>();
            titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            titleText.text = $"Select destination (default cost {TravelButtonMod.cfgTravelCost.Value} silver)";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.fontSize = 18;
            titleText.color = Color.white;

            // Inline message area
            var inlineMsgGO = new GameObject("InlineMessage");
            inlineMsgGO.transform.SetParent(dialogRoot.transform, false);
            var inlineRt = inlineMsgGO.AddComponent<RectTransform>();
            inlineRt.anchorMin = new Vector2(0f, 0.92f);
            inlineRt.anchorMax = new Vector2(1f, 0.99f);
            inlineRt.anchoredPosition = Vector2.zero;
            inlineRt.sizeDelta = Vector2.zero;
            var inlineText = inlineMsgGO.AddComponent<Text>();
            inlineText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            inlineText.text = "";
            inlineText.alignment = TextAnchor.MiddleCenter;
            inlineText.color = Color.yellow;
            inlineText.fontSize = 14;
            inlineText.raycastTarget = false;

            // Scroll area for cities
            var scrollGO = new GameObject("ScrollArea");
            scrollGO.transform.SetParent(dialogRoot.transform, false);
            var scrollRt = scrollGO.AddComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0f, 0f);
            scrollRt.anchorMax = new Vector2(1f, 1f);
            scrollRt.offsetMin = new Vector2(10, 60);
            scrollRt.offsetMax = new Vector2(-10, -70);
            var scrollRect = scrollGO.AddComponent<ScrollRect>();
            scrollGO.AddComponent<CanvasRenderer>();
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = true;
            scrollRect.scrollSensitivity = 20f;

            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollGO.transform, false);
            var vpRt = viewport.AddComponent<RectTransform>();
            vpRt.anchorMin = Vector2.zero;
            vpRt.anchorMax = Vector2.one;
            vpRt.offsetMin = Vector2.zero;
            vpRt.offsetMax = Vector2.zero;
            viewport.AddComponent<CanvasRenderer>();
            var vImg = viewport.AddComponent<Image>();
            vImg.color = Color.clear;
            viewport.AddComponent<UnityEngine.UI.RectMask2D>();

            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRt = content.AddComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0, 0);
            var vlayout = content.AddComponent<VerticalLayoutGroup>();
            vlayout.childControlHeight = true;
            vlayout.childForceExpandHeight = false;
            vlayout.childControlWidth = true;
            vlayout.spacing = 6;
            var csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = contentRt;
            scrollRect.viewport = vpRt;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;

            // Populate city list (shows all cities; interactable only when (visited || config enabled) && coords/target exist)
            long playerMoney = GetPlayerCurrencyAmountOrMinusOne();
            bool haveMoneyInfo = playerMoney >= 0;

            if (TravelButtonMod.Cities != null)
            {
                foreach (var city in TravelButtonMod.Cities)
                {
                    var bgo = new GameObject("CityButton_" + city.name);
                    bgo.transform.SetParent(content.transform, false);
                    bgo.AddComponent<CanvasRenderer>();
                    var brt = bgo.AddComponent<RectTransform>();
                    brt.sizeDelta = new Vector2(0, 44);
                    var ble = bgo.AddComponent<LayoutElement>();
                    ble.preferredHeight = 44f;
                    ble.minHeight = 30f;
                    ble.flexibleWidth = 1f;
                    var bimg = bgo.AddComponent<Image>();
                    bimg.color = new Color(0.35f, 0.20f, 0.08f, 1f);
                    var bbtn = bgo.AddComponent<Button>();
                    bbtn.targetGraphic = bimg;
                    bbtn.interactable = true;
                    var cb = bbtn.colors;
                    cb.normalColor = new Color(0.45f, 0.26f, 0.13f, 1f);
                    cb.highlightedColor = new Color(0.55f, 0.33f, 0.16f, 1f);
                    cb.pressedColor = new Color(0.36f, 0.20f, 0.08f, 1f);
                    bbtn.colors = cb;

                    var lgo = new GameObject("Label");
                    lgo.transform.SetParent(bgo.transform, false);
                    var lrt = lgo.AddComponent<RectTransform>();
                    lrt.anchorMin = new Vector2(0f, 0f);
                    lrt.anchorMax = new Vector2(1f, 1f);
                    lrt.offsetMin = new Vector2(8, 0);
                    lrt.offsetMax = new Vector2(-8, 0);
                    var ltxt = lgo.AddComponent<Text>();
                    ltxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    ltxt.text = city.name;
                    ltxt.color = new Color(0.98f, 0.94f, 0.87f, 1.0f);
                    ltxt.alignment = TextAnchor.MiddleLeft;
                    ltxt.fontSize = 14;
                    ltxt.raycastTarget = false;

                    int cost = TravelButtonMod.cfgTravelCost.Value;
                    try
                    {
                        var priceField = city.GetType().GetField("price");
                        if (priceField != null)
                        {
                            var pv = priceField.GetValue(city);
                            if (pv is int) cost = (int)pv;
                            else if (pv is long) cost = (int)(long)pv;
                        }
                        else
                        {
                            var priceProp = city.GetType().GetProperty("price");
                            if (priceProp != null)
                            {
                                var pv = priceProp.GetValue(city);
                                if (pv is int) cost = (int)pv;
                                else if (pv is long) cost = (int)(long)pv;
                            }
                        }
                    }
                    catch { /* ignore */ }

                    var priceGO = new GameObject("Price");
                    priceGO.transform.SetParent(bgo.transform, false);
                    var ptxt = priceGO.AddComponent<Text>();
                    ptxt.text = cost.ToString();
                    ptxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    ptxt.color = Color.white;
                    ptxt.alignment = TextAnchor.MiddleRight;
                    var pRect = priceGO.GetComponent<RectTransform>();
                    pRect.anchorMin = new Vector2(0.6f, 0);
                    pRect.anchorMax = new Vector2(1, 1);
                    pRect.offsetMin = new Vector2(-10, 0);
                    pRect.offsetMax = new Vector2(-10, 0);

                    bool enabledByConfig = TravelButtonMod.IsCityEnabled(city.name);
                    bool visited = false;
                    try { visited = VisitedTracker.HasVisited(city.name); } catch { }

                    bool coordsAvailable = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(city.targetGameObjectName))
                        {
                            var targetGO = GameObject.Find(city.targetGameObjectName);
                            coordsAvailable = targetGO != null;
                        }
                        if (!coordsAvailable && city.coords != null && city.coords.Length >= 3)
                            coordsAvailable = true;
                    }
                    catch { }

                    bool initialInteractable = (visited || enabledByConfig) && coordsAvailable;
                    if (haveMoneyInfo)
                    {
                        if (playerMoney < cost) initialInteractable = false;
                    }

                    bbtn.interactable = initialInteractable;
                    if (!initialInteractable)
                        bimg.color = new Color(0.18f, 0.18f, 0.18f, 1f);

                    var capturedCity = city;
                    bbtn.onClick.AddListener(() =>
                    {
                        try
                        {
                            long pm = GetPlayerCurrencyAmountOrMinusOne();
                            if (pm >= 0 && pm < cost)
                            {
                                ShowInlineDialogMessage("not enough resources to travel");
                                return;
                            }

                            // Use host TeleportHelpersBehaviour to run teleport coroutine and post-charge
                            TeleportHelpersBehaviour host = TeleportHelpersBehaviour.GetOrCreateHost();
                            host.StartCoroutine(host.EnsureSceneAndTeleport(capturedCity, capturedCity.coords != null && capturedCity.coords.Length >= 3 ? new Vector3(capturedCity.coords[0], capturedCity.coords[1], capturedCity.coords[2]) : Vector3.zero, capturedCity.coords != null && capturedCity.coords.Length >= 3, success =>
                            {
                                if (success)
                                {
                                    // mark visited
                                    try { VisitedTracker.MarkVisited(capturedCity.name); } catch { }

                                    // attempt to deduct
                                    bool charged = AttemptDeductSilver(cost);
                                    if (!charged)
                                    {
                                        TravelButtonMod.LogWarning($"Teleport succeeded to {capturedCity.name} but failed to deduct {cost} silver.");
                                        ShowInlineDialogMessage($"Teleported to {capturedCity.name} (failed to charge {cost} {TravelButtonMod.cfgCurrencyItem.Value})");
                                    }
                                    else
                                    {
                                        ShowInlineDialogMessage($"Teleported to {capturedCity.name}");
                                    }

                                    // close dialog after a small delay to let user read inline message
                                    StartCoroutine(CloseAfterDelayAndStopRefresh(0.2f));
                                }
                                else
                                {
                                    ShowInlineDialogMessage("Teleport failed");
                                }
                            }));
                        }
                        catch (Exception ex)
                        {
                            TravelButtonMod.LogWarning("City button click handler exception: " + ex);
                        }
                    });
                }
            }

            // Close button bottom
            var closeGO = new GameObject("Close");
            closeGO.transform.SetParent(dialogRoot.transform, false);
            closeGO.AddComponent<CanvasRenderer>();
            var closeRt = closeGO.AddComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(0.5f, 0f);
            closeRt.anchorMax = new Vector2(0.5f, 0f);
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.anchoredPosition = new Vector2(0, 12);
            closeRt.sizeDelta = new Vector2(120, 34);
            var cimg = closeGO.AddComponent<Image>();
            cimg.color = new Color(0.25f, 0.25f, 0.25f, 1f);
            var cbtn = closeGO.AddComponent<Button>();
            cbtn.targetGraphic = cimg;
            cbtn.interactable = true;
            closeGO.transform.SetAsLastSibling();
            var closeTxtGO = new GameObject("Label");
            closeTxtGO.transform.SetParent(closeGO.transform, false);
            var ctxt = closeTxtGO.AddComponent<Text>();
            ctxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            ctxt.text = "Close";
            ctxt.alignment = TextAnchor.MiddleCenter;
            ctxt.color = Color.white;
            ctxt.raycastTarget = false;
            var cLabelRt = closeTxtGO.GetComponent<RectTransform>();
            cLabelRt.anchorMin = Vector2.zero;
            cLabelRt.anchorMax = Vector2.one;
            cLabelRt.offsetMin = Vector2.zero;
            cLabelRt.offsetMax = Vector2.zero;
            cbtn.onClick.AddListener(() =>
            {
                try
                {
                    if (dialogRoot != null) dialogRoot.SetActive(false);
                    if (refreshButtonsCoroutine != null)
                    {
                        StopCoroutine(refreshButtonsCoroutine);
                        refreshButtonsCoroutine = null;
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonMod.LogError("Close button click failed: " + ex);
                }
            });

            // Prevent click-through for one frame
            StartCoroutine(TemporarilyDisableDialogRaycasts());
            refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));

            TravelButtonMod.LogInfo("OpenTravelDialog: dialog created and displayed.");
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogError("OpenTravelDialog: exception while creating dialog: " + ex);
        }
    }

    private IEnumerator CloseAfterDelayAndStopRefresh(float delay)
    {
        yield return new WaitForSeconds(delay);
        try
        {
            if (dialogRoot != null) dialogRoot.SetActive(false);
            if (refreshButtonsCoroutine != null)
            {
                StopCoroutine(refreshButtonsCoroutine);
                refreshButtonsCoroutine = null;
            }
        }
        catch { }
    }

    private IEnumerator CloseAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        try { if (dialogRoot != null) dialogRoot.SetActive(false); } catch { }
    }

    private void ShowInlineDialogMessage(string msg)
    {
        try
        {
            if (dialogRoot == null) return;
            var inline = dialogRoot.transform.Find("InlineMessage");
            if (inline == null) return;
            var txt = inline.GetComponent<Text>();
            if (txt == null) return;
            txt.text = msg;
            StartCoroutine(ClearInlineMessageAfterDelay(3f));
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("ShowInlineDialogMessage exception: " + ex);
        }
    }

    private IEnumerator ClearInlineMessageAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        try
        {
            if (dialogRoot != null)
            {
                var inline = dialogRoot.transform.Find("InlineMessage");
                if (inline != null)
                {
                    var txt = inline.GetComponent<Text>();
                    if (txt != null) txt.text = "";
                }
            }
        }
        catch { }
    }

    // Refresh city buttons while dialog is open: re-evaluates player's currency and enables/disables buttons.
    private IEnumerator RefreshCityButtonsWhileOpen(GameObject dialog)
    {
        while (dialog != null && dialog.activeInHierarchy)
        {
            try
            {
                long currentMoney = GetPlayerCurrencyAmountOrMinusOne();
                bool haveMoneyInfo = currentMoney >= 0;

                var content = dialog.transform.Find("ScrollArea/Viewport/Content");
                if (content != null)
                {
                    for (int i = 0; i < content.childCount; i++)
                    {
                        var child = content.GetChild(i);
                        var btn = child.GetComponent<Button>();
                        var img = child.GetComponent<Image>();
                        if (btn == null || img == null) continue;

                        string objName = child.name;
                        if (objName.StartsWith("CityButton_"))
                        {
                            string cityName = objName.Substring("CityButton_".Length);
                            bool enabledByConfig = TravelButtonMod.IsCityEnabled(cityName);
                            bool visited = false;
                            try { visited = VisitedTracker.HasVisited(cityName); } catch { visited = false; }

                            int cost = TravelButtonMod.cfgTravelCost.Value;
                            try
                            {
                                TravelButtonMod.City foundCity = null;
                                foreach (var c in TravelButtonMod.Cities)
                                {
                                    if (string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        foundCity = c;
                                        break;
                                    }
                                }
                                if (foundCity != null)
                                {
                                    var priceField = foundCity.GetType().GetField("price");
                                    if (priceField != null)
                                    {
                                        var pv = priceField.GetValue(foundCity);
                                        if (pv is int) cost = (int)pv;
                                        else if (pv is long) cost = (int)(long)pv;
                                    }
                                }
                            }
                            catch { /* ignore */ }

                            bool coordsAvailable = false;
                            try
                            {
                                TravelButtonMod.City found = null;
                                foreach (var c in TravelButtonMod.Cities)
                                {
                                    if (string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        found = c; break;
                                    }
                                }
                                if (found != null)
                                {
                                    if (!string.IsNullOrEmpty(found.targetGameObjectName))
                                    {
                                        var go = GameObject.Find(found.targetGameObjectName);
                                        coordsAvailable = go != null;
                                    }
                                    if (!coordsAvailable && found.coords != null && found.coords.Length >= 3) coordsAvailable = true;
                                }
                            }
                            catch { coordsAvailable = false; }

                            bool shouldBeInteractable = (visited || enabledByConfig) && coordsAvailable;
                            if (haveMoneyInfo)
                                shouldBeInteractable = shouldBeInteractable && (currentMoney >= cost);

                            if (btn.interactable != shouldBeInteractable)
                            {
                                btn.interactable = shouldBeInteractable;
                                img.color = shouldBeInteractable ? new Color(0.35f, 0.20f, 0.08f, 1f) : new Color(0.18f, 0.18f, 0.18f, 1f);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonMod.LogWarning("RefreshCityButtonsWhileOpen exception: " + ex);
            }

            yield return new WaitForSeconds(1f);
        }

        refreshButtonsCoroutine = null;
    }

    // minimal reflection-based currency detection (used to show "not enough resources" early)
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
                    var pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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
                    var mi = t.GetMethod(mn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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

                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
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

                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var name = pi.Name.ToLower();
                    if ((name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coin") || name.Contains("currency")) && pi.CanRead)
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

    // Attempt to deduct silver using reflection heuristics (post-teleport charge)
    private bool AttemptDeductSilver(int amount)
    {
        TravelButtonMod.LogInfo($"AttemptDeductSilver: trying to deduct {amount} silver.");

        var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
        foreach (var mb in allMonoBehaviours)
        {
            var t = mb.GetType();

            string[] methodNames = new string[] { "RemoveMoney", "SpendMoney", "RemoveSilver", "SpendSilver", "RemoveCurrency", "TakeMoney", "UseMoney" };
            foreach (var mn in methodNames)
            {
                var mi = t.GetMethod(mn, new Type[] { typeof(int) });
                if (mi != null)
                {
                    try
                    {
                        var res = mi.Invoke(mb, new object[] { amount });
                        TravelButtonMod.LogInfo($"AttemptDeductSilver: called {t.FullName}.{mn}({amount}) -> {res}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TravelButtonMod.LogWarning($"AttemptDeductSilver: calling {t.FullName}.{mn} threw: {ex}");
                    }
                }
            }

            foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var name = fi.Name.ToLower();
                if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coins") || name.Contains("currency"))
                {
                    try
                    {
                        if (fi.FieldType == typeof(int))
                        {
                            int cur = (int)fi.GetValue(mb);
                            if (cur >= amount)
                            {
                                fi.SetValue(mb, cur - amount);
                                TravelButtonMod.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{fi.Name} (int). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonMod.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{fi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                        else if (fi.FieldType == typeof(long))
                        {
                            long cur = (long)fi.GetValue(mb);
                            if (cur >= amount)
                            {
                                fi.SetValue(mb, cur - amount);
                                TravelButtonMod.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{fi.Name} (long). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonMod.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{fi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonMod.LogWarning($"AttemptDeductSilver: field access {t.FullName}.{fi.Name} threw: {ex}");
                    }
                }
            }

            foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var name = pi.Name.ToLower();
                if (name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coins") || name.Contains("currency"))
                {
                    try
                    {
                        if (pi.PropertyType == typeof(int) && pi.CanRead && pi.CanWrite)
                        {
                            int cur = (int)pi.GetValue(mb);
                            if (cur >= amount)
                            {
                                pi.SetValue(mb, cur - amount);
                                TravelButtonMod.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{pi.Name} (int). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonMod.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{pi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                        else if (pi.PropertyType == typeof(long) && pi.CanRead && pi.CanWrite)
                        {
                            long cur = (long)pi.GetValue(mb);
                            if (cur >= amount)
                            {
                                pi.SetValue(mb, cur - amount);
                                TravelButtonMod.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{pi.Name} (long). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonMod.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{pi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonMod.LogWarning($"AttemptDeductSilver: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
            }
        }

        TravelButtonMod.LogWarning("AttemptDeductSilver: could not find an inventory/money field or method automatically. Travel aborted.");
        return false;
    }

    // Prevent click-through by disabling CanvasGroup.interactable for one frame while the initial click finishes
    private IEnumerator TemporarilyDisableDialogRaycasts()
    {
        CanvasGroup cg = null;
        if (dialogCanvas != null)
        {
            cg = dialogCanvas.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                cg = dialogCanvas.AddComponent<CanvasGroup>();
            }
        }

        if (cg == null)
            yield break;

        cg.interactable = false;
        cg.blocksRaycasts = false;

        // wait two frames
        yield return null;
        yield return null;

        cg.interactable = true;
        cg.blocksRaycasts = true;
    }

    private Canvas FindCanvas()
    {
        var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
        if (canvas != null) return canvas;

        Type canvasType = Type.GetType("UnityEngine.Canvas, UnityEngine.UIModule");
        if (canvasType != null)
        {
            var objs = UnityEngine.Object.FindObjectsOfType(canvasType);
            if (objs != null && objs.Length > 0)
            {
                var comp = objs[0] as Canvas;
                return comp;
            }
        }
        return null;
    }
}