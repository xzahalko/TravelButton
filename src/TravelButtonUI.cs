// NOTE: This is the full TravelButtonUI.cs with additional debug logging and sanity checks
// added to help diagnose bad/incorrect teleport target positions.
//
// Key changes:
// - More detailed logging in TryTeleportThenCharge to show which target source is used:
//   - targetGameObjectName present but GAMEOBJECT not found => logged explicitly
//   - explicit coords present => logged (and validated)
//   - if neither present, logged (helper may rely on sceneName / heuristics)
// - Added IsCoordsReasonable() to detect obviously bogus coords and warn/avoid using them
// - TryGetTargetPosition now returns whether it could find a GameObject or coords and logs details
// - No behavioural changes to the teleport flow (still uses TeleportHelpersBehaviour.EnsureSceneAndTeleport),
//   but it will log why a coordsHint is used so you can fix config/anchors.
//
// Use these logs to check travel_config.json coordinates and city.targetGameObjectName values,
// and to correlate TravelButtonMod.LogCityAnchorsFromLoadedScenes() output to anchor names in scenes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using uNature.Core.Terrains;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Linq;

/// <summary>
/// UI helper MonoBehaviour responsible for injecting a Travel button into the Inventory UI.
/// - Polls for the inventory container and reparents the button there when it appears.
/// - Detects the inventory's actual visibility target (window/panel/canvasgroup) and syncs the button's active state to it.
/// - Copies layout from an existing button template where possible so the Travel button matches inventory buttons (with clamping).
/// - Creates dialog in a dedicated top-most Canvas so it's never occluded and Close works.
/// - Shows all configured cities (visible in dialog). Buttons are interactable only when player has visited OR city is enabled in config,
///   and coordinates are configured (or a targetGameObject exists).
/// - Buttons are also disabled if the player doesn't have enough currency (and show the exact message "not enough resources to travel" on click).
/// - Clicking a city will now immediately attempt to pay and teleport the player (no extra confirm).
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

    // Prevent multiple teleport attempts at the same time
    private bool isTeleporting = false;

    void Start()
    {
        TravelButtonPlugin.LogInfo("TravelButtonUI.Start called.");
        CreateTravelButton();
        EnsureInputSystems();
        // start polling for inventory container (will reparent once found)
        StartCoroutine(PollForInventoryParent());
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.BackQuote))
        {
            TravelButtonPlugin.LogInfo("BackQuote key pressed - opening travel dialog.");
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
                TravelButtonPlugin.LogWarning("Visibility sync error: " + ex);
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
                    TravelButtonPlugin.LogInfo($"PollForInventoryParent: found inventory parent '{name}', reparenting button.");
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

            // Find a template button under the container to copy visuals/layout from
            Button templateButton = null;
            try
            {
                var buttons = container.GetComponentsInChildren<Button>(true);
                if (buttons != null && buttons.Length > 0)
                {
                    // prefer a top-level sibling style button (heuristic)
                    templateButton = buttons[0];
                }
            }
            catch { /* ignore */ }

            // Parent and configure layout participation
            buttonObject.transform.SetParent(container, false);
            buttonObject.transform.SetAsLastSibling();

            // Ensure the button participates in layout groups correctly
            var layoutElement = buttonObject.GetComponent<LayoutElement>();
            if (layoutElement == null)
                layoutElement = buttonObject.AddComponent<LayoutElement>();

            var rt = buttonObject.GetComponent<RectTransform>();
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
                    // clamp sizes (adjust if you prefer other limits)
                    float maxWidth = 220f;
                    float maxHeight = 44f;
                    copied.x = Mathf.Clamp(copied.x, 60f, maxWidth);
                    copied.y = Mathf.Clamp(copied.y, 20f, maxHeight);
                    rt.sizeDelta = copied;

                    // place next to template
                    rt.anchoredPosition = tRt.anchoredPosition + new Vector2(tRt.sizeDelta.x + 4f, 0f);

                    // set preferred size so layout group uses it
                    layoutElement.preferredWidth = rt.sizeDelta.x;
                    layoutElement.preferredHeight = rt.sizeDelta.y;
                    layoutElement.flexibleWidth = 0;
                    layoutElement.flexibleHeight = 0;

                    TravelButtonPlugin.LogInfo("ReparentButtonToInventory: copied layout from template button (clamped).");
                }

                // copy image sprite if template uses one (keeps brown tint applied)
                try
                {
                    var templImg = templateButton.GetComponent<Image>();
                    var ourImg = buttonObject.GetComponent<Image>();
                    if (templImg != null && ourImg != null && templImg.sprite != null)
                    {
                        ourImg.sprite = templImg.sprite;
                        ourImg.type = templImg.type;
                        ourImg.preserveAspect = templImg.preserveAspect;
                        // keep our color tint
                    }
                }
                catch { /* ignore */ }
            }
            else
            {
                // no template found: use reasonable defaults and clamp
                rt.sizeDelta = new Vector2(Mathf.Min(rt.sizeDelta.x, 160f), Mathf.Min(rt.sizeDelta.y, 34f));
                layoutElement.preferredWidth = rt.sizeDelta.x;
                layoutElement.preferredHeight = rt.sizeDelta.y;
                layoutElement.flexibleWidth = 0;
                layoutElement.flexibleHeight = 0;
                TravelButtonPlugin.LogInfo("ReparentButtonToInventory: no template button found, used default layout sizes.");
            }

            // Find the real visibility target of the inventory UI so we can show/hide the button with the window
            TryFindInventoryVisibilityTarget(container);

            // If TryFindInventoryVisibilityTarget found something, sync to that target;
            // otherwise fall back to monitoring the container active state (less precise but more robust across mods).
            if (inventoryVisibilityTarget != null)
            {
                // Sync initial visibility using the found target
                try
                {
                    bool visible = inventoryVisibilityTarget.activeInHierarchy;
                    var cg = inventoryVisibilityTarget.GetComponent<CanvasGroup>();
                    if (cg != null) visible = cg.alpha > 0.01f && cg.interactable;
                    buttonObject.SetActive(visible);
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("ReparentButtonToInventory: failed to sync visibility from found inventoryVisibilityTarget: " + ex);
                    buttonObject.SetActive(true);
                }
            }
            else
            {
                // Fallback: use container.activeInHierarchy as a visibility heuristic and monitor it
                try
                {
                    bool visible = container.gameObject.activeInHierarchy;
                    buttonObject.SetActive(visible);
                    TravelButtonPlugin.LogInfo($"ReparentButtonToInventory: no explicit visibility target found; using container '{container.name}' active state as fallback (visible={visible}).");
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("ReparentButtonToInventory: fallback visibility check failed: " + ex);
                    // show button by default to aid debugging if fallback failed
                    buttonObject.SetActive(true);
                }

                // Start a monitor that toggles the button when the container's active state or CanvasGroup changes.
                visibilityMonitorCoroutine = StartCoroutine(MonitorInventoryContainerVisibility(container));
            }

            TravelButtonPlugin.LogInfo("ReparentButtonToInventory: button reparented and visibility synced with inventory.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("ReparentButtonToInventory: " + ex);
        }
    }

    // Monitor container active state periodically and update button visibility when no explicit visibility target was detected.
    private IEnumerator MonitorInventoryContainerVisibility(Transform container)
    {
        if (container == null || buttonObject == null) yield break;

        while (true)
        {
            try
            {
                bool visible = container.gameObject.activeInHierarchy;
                // If container has a CanvasGroup child that seems to control visibility, prefer that
                var cg = container.GetComponentInChildren<CanvasGroup>(true);
                if (cg != null)
                {
                    visible = cg.alpha > 0.01f && cg.interactable && cg.gameObject.activeInHierarchy;
                }

                if (buttonObject.activeSelf != visible)
                {
                    buttonObject.SetActive(visible);
                    TravelButtonPlugin.LogDebug($"MonitorInventoryContainerVisibility: set TravelButton active={visible} (container='{container.name}').");
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("MonitorInventoryContainerVisibility exception: " + ex);
            }

            // low frequency: check twice per second
            yield return new WaitForSeconds(0.5f);
        }
    }

    // Best-effort: look for the GameObject that is actually toggled when inventory opens:
    // - prefer an object whose name contains "Window" or "Panel",
    // - or any descendant/ancestor that has a CanvasGroup (we treat its alpha/interactable as visibility)
    private void TryFindInventoryVisibilityTarget(Transform container)
    {
        try
        {
            // 1) search up the ancestor chain for "Window" or CanvasGroup
            var t = container;
            while (t != null)
            {
                if (t.name.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inventoryVisibilityTarget = t.gameObject;
                    TravelButtonPlugin.LogInfo($"TryFindInventoryVisibilityTarget: using ancestor '{t.name}' as visibility target.");
                    return;
                }

                if (t.GetComponent<CanvasGroup>() != null)
                {
                    inventoryVisibilityTarget = t.gameObject;
                    TravelButtonPlugin.LogInfo($"TryFindInventoryVisibilityTarget: using ancestor with CanvasGroup '{t.name}' as visibility target.");
                    return;
                }
                t = t.parent;
            }

            // 2) look for children under container that look like a window/panel (common names)
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
                        TravelButtonPlugin.LogInfo($"TryFindInventoryVisibilityTarget: using child '{cname}' as visibility target.");
                        return;
                    }
                }

                var cg = child.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    inventoryVisibilityTarget = child.gameObject;
                    TravelButtonPlugin.LogInfo($"TryFindInventoryVisibilityTarget: using child with CanvasGroup '{child.name}' as visibility target.");
                    return;
                }
            }

            // 3) fallback: try to find a sibling window object named InventoryWindow
            var sibling = GameObject.Find("InventoryWindow") ?? GameObject.Find("Inventory_Window");
            if (sibling != null)
            {
                inventoryVisibilityTarget = sibling;
                TravelButtonPlugin.LogInfo($"TryFindInventoryVisibilityTarget: using sibling '{sibling.name}' as visibility target.");
                return;
            }

            // If we reach here, no explicit target found
            TravelButtonPlugin.LogInfo("TryFindInventoryVisibilityTarget: no explicit visibility target found for inventory.");
            inventoryVisibilityTarget = null;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryFindInventoryVisibilityTarget exception: " + ex);
            inventoryVisibilityTarget = null;
        }
    }

    // Ensure EventSystem + GraphicRaycaster exist
    private void EnsureInputSystems()
    {
        try
        {
            if (EventSystem.current == null)
            {
                TravelButtonPlugin.LogInfo("No EventSystem found - creating one.");
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
                    TravelButtonPlugin.LogInfo("Canvas found but missing GraphicRaycaster - adding one.");
                    anyCanvas.gameObject.AddComponent<GraphicRaycaster>();
                }
            }
            else
            {
                TravelButtonPlugin.LogWarning("No Canvas found when ensuring input systems. UI may not be interactable until a Canvas exists.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("EnsureInputSystems exception: " + ex);
        }
    }

    void CreateTravelButton()
    {
        TravelButtonPlugin.LogInfo("CreateTravelButton: beginning UI creation.");
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
            // default reasonable size (may be adjusted by template when reparented)
            rt.sizeDelta = new Vector2(140, 32);

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1) buttonObject.layer = uiLayer;

            // initially parent to first available Canvas (so it's created in UI space)
            var canvas = FindCanvas();
            if (canvas != null)
            {
                buttonObject.transform.SetParent(canvas.transform, false);
                // put near top center by default (will be reparented to inventory when found)
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0, -40);
            }
            else
            {
                TravelButtonPlugin.LogWarning("CreateTravelButton: no Canvas found at creation time; button created at scene root.");
            }

            // Label
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(buttonObject.transform, false);
            var txt = labelGO.AddComponent<Text>();
            txt.text = "Travel";
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = new Color(0.98f, 0.94f, 0.87f, 1.0f);
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = 14;
            txt.raycastTarget = false;

            var labelRt = labelGO.GetComponent<RectTransform>();
            if (labelRt != null)
            {
                labelRt.anchorMin = new Vector2(0f, 0f);
                labelRt.anchorMax = new Vector2(1f, 1f);
                labelRt.offsetMin = Vector2.zero;
                labelRt.offsetMax = Vector2.zero;
            }

            // Ensure input systems and ensure button gets pointer events
            EnsureInputSystems();

            var logger = buttonObject.GetComponent<ClickLogger>();
            if (logger == null) logger = buttonObject.AddComponent<ClickLogger>();

            travelButton.onClick.AddListener(OpenTravelDialog);

            // Hide the button until we reparent to the inventory UI; prevents showing on main HUD
            buttonObject.SetActive(false);

            TravelButtonPlugin.LogInfo("CreateTravelButton: Travel button created, ClickLogger attached, and listener attached.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("CreateTravelButton: exception: " + ex);
        }
    }

    void OpenTravelDialog()
    {
        TravelButtonPlugin.LogInfo("OpenTravelDialog: invoked via click or keyboard.");

        // Insert this into TravelButtonUI.OpenTravelDialog() right after dialogRoot is assigned.
        // Defensive debug button that sets sceneName values in-memory (no file writes).
        try
        {
            // Find a safe parent for the debug UI
            Transform parentTransform = null;
            if (dialogRoot != null)
                parentTransform = dialogRoot.transform;
            else
            {
                var canvas = UnityEngine.Object.FindObjectOfType<UnityEngine.Canvas>();
                if (canvas != null) parentTransform = canvas.transform;
            }

            if (parentTransform == null)
            {
                TravelButtonPlugin.LogWarning("Debug: no suitable parent found for debug button. Skipping debug button creation.");
            }
            else
            {
                // Create container
                var debugGo = new GameObject("TravelDebug_SetSceneNames", typeof(RectTransform));
                debugGo.transform.SetParent(parentTransform, false);
                var rt = debugGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.sizeDelta = new Vector2(180f, 28f);
                rt.anchoredPosition = new Vector2(-12f, -12f);

                // Add Image (background)
                var image = debugGo.AddComponent<UnityEngine.UI.Image>();
                image.color = new Color(0.15f, 0.4f, 0.85f, 0.95f);

                // Add Button
                var button = debugGo.AddComponent<UnityEngine.UI.Button>();
                var colors = button.colors;
                colors.normalColor = image.color;
                button.colors = colors;

                // Text child
                var txtGo = new GameObject("Text", typeof(RectTransform));
                txtGo.transform.SetParent(debugGo.transform, false);
                var txtRt = txtGo.GetComponent<RectTransform>();
                txtRt.anchorMin = Vector2.zero;
                txtRt.anchorMax = Vector2.one;
                txtRt.offsetMin = Vector2.zero;
                txtRt.offsetMax = Vector2.zero;

                var txt = txtGo.AddComponent<UnityEngine.UI.Text>();
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = Color.white;
                txt.fontSize = 14;
                // Safe font acquisition
                try
                {
                    txt.font = UnityEngine.Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
                catch
                {
                    txt.font = UnityEngine.Object.FindObjectOfType<UnityEngine.Canvas>()?.GetComponentInChildren<UnityEngine.UI.Text>()?.font;
                }
                txt.text = "Debug: Set SceneNames";

                // Click handler (uses reflection, avoids dynamic)
                button.onClick.AddListener(() =>
                {
                    try
                    {
                        TravelButtonPlugin.LogInfo("Debug: SetSceneNames button clicked - attempting to set sceneName values in-memory.");

                        var citiesField = typeof(TravelButtonMod).GetField("Cities", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        var citiesObj = citiesField?.GetValue(null);
                        if (citiesObj == null)
                        {
                            TravelButtonPlugin.LogWarning("Debug: Cities field is null.");
                            return;
                        }

                        if (!(citiesObj is System.Collections.IEnumerable enumerable))
                        {
                            TravelButtonPlugin.LogWarning("Debug: Cities field is not enumerable.");
                            return;
                        }

                        int changed = 0;
                        foreach (var item in enumerable)
                        {
                            if (item == null) continue;
                            var t = item.GetType();
                            var nameProp = t.GetProperty("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (nameProp == null) continue;
                            var nameVal = nameProp.GetValue(item) as string;
                            if (string.IsNullOrEmpty(nameVal)) continue;

                            var sceneProp = t.GetProperty("sceneName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (sceneProp == null || !sceneProp.CanWrite) continue;

                            string guess = null;
                            if (string.Equals(nameVal, "Cierzo", StringComparison.OrdinalIgnoreCase)) guess = "CierzoNewTerrain";
                            else if (string.Equals(nameVal, "Berg", StringComparison.OrdinalIgnoreCase)) guess = "BergNewTerrain";
                            else if (string.Equals(nameVal, "Levant", StringComparison.OrdinalIgnoreCase)) guess = "LevantNewTerrain";
                            else if (string.Equals(nameVal, "Monsoon", StringComparison.OrdinalIgnoreCase)) guess = "MonsoonNewTerrain";
                            else if (string.Equals(nameVal, "Harmattan", StringComparison.OrdinalIgnoreCase)) guess = "HarmattanNewTerrain";
                            else if (string.Equals(nameVal, "Sirocco", StringComparison.OrdinalIgnoreCase)) guess = "SiroccoNewTerrain";

                            if (!string.IsNullOrEmpty(guess))
                            {
                                sceneProp.SetValue(item, guess);
                                TravelButtonPlugin.LogInfo($"Debug: set {nameVal}.sceneName = '{guess}'");
                                changed++;
                            }
                        }

                        TravelButtonPlugin.LogInfo($"Debug: finished setting sceneNames in-memory (changed={changed}).");
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning("Debug SetSceneNames handler exception: " + ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("Failed to add debug SetSceneNames button: " + ex);
        }

        try
        {
            TravelButtonMod.AutoAssignSceneNamesFromLoadedScenes();

            // Log all loaded scenes and root objects (useful to confirm scene names)
            TravelButtonMod.LogLoadedScenesAndRootObjects();

            // Scan currently loaded scenes for city anchors and print candidates (exact transform paths + positions)
            TravelButtonMod.LogCityAnchorsFromLoadedScenes();
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("OpenTravelDialog: auto-scan for anchors failed: " + ex.Message);
        }

        try
        {
            // Stop any previous refresh coroutine
            if (refreshButtonsCoroutine != null)
            {
                StopCoroutine(refreshButtonsCoroutine);
                refreshButtonsCoroutine = null;
            }

            if (dialogRoot != null)
            {
                dialogRoot.SetActive(true);
                // bring to top
                var canvas = dialogCanvas != null ? dialogCanvas.GetComponent<Canvas>() : dialogRoot.GetComponentInParent<Canvas>();
                if (canvas != null) canvas.sortingOrder = 2000;
                dialogRoot.transform.SetAsLastSibling();
                TravelButtonPlugin.LogInfo("OpenTravelDialog: re-activated existing dialogRoot.");
                // prevent click-through for a frame when reactivating
                StartCoroutine(TemporarilyDisableDialogRaycasts());
                // start refreshing buttons while open
                refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));
                return;
            }

            // Create (or reuse) a dedicated top-level Canvas for the dialog so it's never occluded
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
                TravelButtonPlugin.LogInfo("OpenTravelDialog: created dedicated TravelDialogCanvas (top-most).");
            }

            dialogRoot = new GameObject("TravelDialog");
            dialogRoot.transform.SetParent(dialogCanvas.transform, false);
            dialogRoot.transform.SetAsLastSibling();
            dialogRoot.AddComponent<CanvasRenderer>();
            var rootRt = dialogRoot.AddComponent<RectTransform>();

            // center the dialog explicitly (use anchored center)
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.localScale = Vector3.one;
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
            // note: keep showing global cost in title but list per-city cost individually below
            titleText.text = $"Select destination (default cost {TravelButtonMod.cfgTravelCost.Value} silver)";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.fontSize = 18;
            titleText.color = Color.white;

            // Inline message area (for "not enough resources to travel" and similar)
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

            // ScrollRect + viewport for city list
            var scrollGO = new GameObject("ScrollArea");
            scrollGO.transform.SetParent(dialogRoot.transform, false);
            var scrollRt = scrollGO.AddComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0f, 0f);
            scrollRt.anchorMax = new Vector2(1f, 1f);
            scrollRt.offsetMin = new Vector2(10, 60);  // leave room for title/top and close bottom
            scrollRt.offsetMax = new Vector2(-10, -70);

            var scrollRect = scrollGO.AddComponent<ScrollRect>();
            scrollGO.AddComponent<CanvasRenderer>();
            // nicer defaults
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
            // Use RectMask2D which is generally more reliable for dynamically created viewports
            var vImg = viewport.AddComponent<Image>();
            vImg.color = Color.clear;
            viewport.AddComponent<UnityEngine.UI.RectMask2D>();

            // Content container
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

            // --- Populate items (with logging) ---
            TravelButtonPlugin.LogInfo($"OpenTravelDialog: TravelButtonMod.Cities.Count = {(TravelButtonMod.Cities == null ? 0 : TravelButtonMod.Cities.Count)}");
            bool anyCity = false;
            if (TravelButtonMod.Cities == null || TravelButtonMod.Cities.Count == 0)
            {
                TravelButtonPlugin.LogWarning("OpenTravelDialog: No cities configured (TravelButtonMod.Cities empty).");
            }
            else
            {
                // read player currency now to set initial interactable state
                long playerMoney = GetPlayerCurrencyAmountOrMinusOne();
                bool haveMoneyInfo = playerMoney >= 0;

                foreach (var city in TravelButtonMod.Cities)
                {
                    // Always show the city in the list (the user requested that all cities appear)
                    anyCity = true;

                    var bgo = new GameObject("CityButton_" + city.name);
                    bgo.transform.SetParent(content.transform, false);
                    bgo.AddComponent<CanvasRenderer>();
                    var brt = bgo.AddComponent<RectTransform>();
                    brt.sizeDelta = new Vector2(0, 44);

                    // Ensure button participates in layout by adding a LayoutElement
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

                    // Label left
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

                    // Determine per-city cost when present, otherwise fallback to global cost
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
                            // try property
                            var priceProp = city.GetType().GetProperty("price");
                            if (priceProp != null)
                            {
                                var pv = priceProp.GetValue(city);
                                if (pv is int) cost = (int)pv;
                                else if (pv is long) cost = (int)(long)pv;
                            }
                        }
                    }
                    catch { /* ignore reflection issues; fallback to global */ }

                    // Price label right
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

                    // Determine whether this city is enabled by config or visited
                    bool enabledByConfig = TravelButtonMod.IsCityEnabled(city.name);
                    bool visited = false;
                    try
                    {
                        visited = VisitedTracker.HasVisited(city.name);
                    }
                    catch { /* if VisitedTracker not available ignore; default false */ }

                    // Determine whether coordinates/target exist for this city (if not, mark unavailable)
                    bool coordsAvailable = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(city.targetGameObjectName))
                        {
                            var targetGO = GameObject.Find(city.targetGameObjectName);
                            coordsAvailable = targetGO != null;
                        }
                        if (!coordsAvailable && city.coords != null && city.coords.Length >= 3)
                        {
                            coordsAvailable = true;
                        }
                    }
                    catch { /* ignore */ }

                    // Set interactability: only if (visited OR enabled by config) AND coords available. Later refresh coroutine will also check money.
                    bool initialInteractable = (visited || enabledByConfig) && coordsAvailable;
                    if (haveMoneyInfo && playerMoney >= 0)
                    {
                        if (playerMoney < cost) initialInteractable = false;
                    }

                    bbtn.interactable = initialInteractable;
                    if (!initialInteractable)
                    {
                        bimg.color = new Color(0.18f, 0.18f, 0.18f, 1f);
                    }

                    TravelButtonPlugin.LogInfo($"OpenTravelDialog: created UI button for '{city.name}' (interactable={bbtn.interactable}, enabledByConfig={enabledByConfig}, visited={visited}, coordsAvailable={coordsAvailable}).");

                    var capturedCity = city;

                    // NEW BEHAVIOR: check funds first, then teleport, then charge (post-teleport charge)
                    bbtn.onClick.AddListener(() =>
                    {
                        try
                        {
                            if (isTeleporting)
                            {
                                TravelButtonPlugin.LogInfo("City button click ignored: teleport already in progress.");
                                return;
                            }

                            // Check player's visible currency amount (best-effort). If known and insufficient => show message.
                            long pm = GetPlayerCurrencyAmountOrMinusOne();
                            if (pm >= 0 && pm < cost)
                            {
                                ShowInlineDialogMessage("not enough resources to travel");
                                return;
                            }

                            // Disable all city buttons in the dialog to prevent duplicates while teleport is happening
                            try
                            {
                                var contentParent = dialogRoot?.transform.Find("ScrollArea/Viewport/Content");
                                if (contentParent != null)
                                {
                                    for (int ci = 0; ci < contentParent.childCount; ci++)
                                    {
                                        var childBtn = contentParent.GetChild(ci).GetComponent<Button>();
                                        if (childBtn != null) childBtn.interactable = false;
                                    }
                                }
                            }
                            catch { }

                            isTeleporting = true;

                            // Start teleport-then-charge flow. The helper callback will clear isTeleporting.
                            TryTeleportThenCharge(capturedCity, cost);
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning("City button click handler exception: " + ex);
                        }
                    });
                }
            }

            // If no enabled city was added, create visible debug placeholders so user can see something
            if (!anyCity)
            {
                TravelButtonPlugin.LogWarning("OpenTravelDialog: no enabled cities were added to the dialog - adding debug placeholders for inspection.");
                for (int i = 0; i < 3; i++)
                {
                    var dbg = new GameObject("DBG_Placeholder_" + i);
                    dbg.transform.SetParent(content.transform, false);
                    dbg.AddComponent<CanvasRenderer>();
                    var drt = dbg.AddComponent<RectTransform>();
                    drt.sizeDelta = new Vector2(0, 36);
                    var dle = dbg.AddComponent<LayoutElement>();
                    dle.preferredHeight = 36f;
                    dle.flexibleWidth = 1f;
                    var dimg = dbg.AddComponent<Image>();
                    dimg.color = new Color(0.2f, 0.2f, 0.2f, 1f);
                    var dtxtGO = new GameObject("Label");
                    dtxtGO.transform.SetParent(dbg.transform, false);
                    var dtxt = dtxtGO.AddComponent<Text>();
                    dtxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    dtxt.text = "DEBUG: no configured cities";
                    dtxt.color = Color.white;
                    dtxt.alignment = TextAnchor.MiddleCenter;
                    dtxt.raycastTarget = false;
                }
            }

            // Defer final layout fix to a coroutine (wait a frame for Unity to calculate rects, then force rebuilds)
            StartCoroutine(FinishDialogLayoutAndShow(scrollRect, viewport.GetComponent<RectTransform>(), contentRt));

            // start refreshing buttons while open
            refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));

            // Close button (bottom center) - ensure clickable
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
                    // stop refresh coroutine when dialog closed
                    if (refreshButtonsCoroutine != null)
                    {
                        StopCoroutine(refreshButtonsCoroutine);
                        refreshButtonsCoroutine = null;
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogError("Close button click failed: " + ex);
                }
            });

            // Prevent immediate click-through: disable interactability for one frame
            StartCoroutine(TemporarilyDisableDialogRaycasts());

            TravelButtonPlugin.LogInfo("OpenTravelDialog: dialog created and centered (dialogRoot assigned).");
            TravelButtonPlugin.LogInfo($"OpenTravelDialog: dialogCanvas sortingOrder={dialogCanvas.GetComponent<Canvas>().sortingOrder}, dialogRoot size={rootRt.sizeDelta}");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("OpenTravelDialog: exception while creating dialog: " + ex);
        }
    }

    // New: Teleport first, THEN attempt to charge player currency.
    // This mirrors the TravelDialog behavior: do not deduct before teleport.
    private void TryTeleportThenCharge(TravelButtonMod.City city, int cost)
    {
        if (city == null)
        {
            TravelButtonPlugin.LogWarning("TryTeleportThenCharge: city is null.");
            isTeleporting = false;
            return;
        }

        try
        {
            TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: attempting teleport to {city.name} (post-charge flow).");

            // Figure out what we'll pass as coordsHint and why (detailed logging to help debug bad positions)
            Vector3 coordsHint = Vector3.zero;
            bool haveCoordsHint = false;
            bool haveTargetGameObject = false;
            bool targetGameObjectFound = false;

            // If the city provides a targetGameObjectName, check for the GameObject first (preferred)
            try
            {
                if (!string.IsNullOrEmpty(city.targetGameObjectName))
                {
                    haveTargetGameObject = true;
                    var tgo = GameObject.Find(city.targetGameObjectName);
                    if (tgo != null)
                    {
                        targetGameObjectFound = true;
                        coordsHint = tgo.transform.position;
                        haveCoordsHint = true; // we have a precise anchor position
                        TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: Found target GameObject '{city.targetGameObjectName}' at {coordsHint} - will prefer anchor.");
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: targetGameObjectName '{city.targetGameObjectName}' provided, but GameObject not found in scene.");
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("TryTeleportThenCharge: error checking targetGameObjectName: " + ex);
            }

            // If no anchor found, fall back to explicit coords if provided in the city config
            if (!haveCoordsHint)
            {
                if (city != null && city.coords != null && city.coords.Length >= 3)
                {
                    coordsHint = new Vector3(city.coords[0], city.coords[1], city.coords[2]);
                    haveCoordsHint = true;
                    TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: using explicit coords from config for {city.name}: {coordsHint}");
                    if (!IsCoordsReasonable(coordsHint))
                    {
                        TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: explicit coords {coordsHint} look suspicious for city '{city.name}'. Verify travel_config.json contains correct world coordinates for that scene.");
                    }
                }
                else
                {
                    TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: no explicit coords or anchor found for {city.name}. Teleport helper will rely on city.sceneName / internal heuristics.");
                }
            }

            // Prepare coords hint validity note for logs
            TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: city='{city.name}', haveTargetGameObject={haveTargetGameObject}, targetGameObjectFound={targetGameObjectFound}, haveCoordsHint={haveCoordsHint}");

            // Find or create a TeleportHelpersBehaviour to run the coroutine
            TeleportHelpersBehaviour helper = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
            if (helper == null)
            {
                var go = new GameObject("TeleportHelpersHost");
                UnityEngine.Object.DontDestroyOnLoad(go);
                helper = go.AddComponent<TeleportHelpersBehaviour>();
            }

            // Start teleport coroutine which handles scene loading / anchor wait / grounding and safe teleport
            helper.StartCoroutine(helper.EnsureSceneAndTeleport(city, coordsHint, haveCoordsHint, success =>
            {
                if (success)
                {
                    TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: teleport to '{city.name}' completed successfully.");

                    // Mark visited in the visited tracker if available
                    try { TravelButtonMod.OnSuccessfulTeleport(city.name); } catch { }

                    // Attempt to deduct currency post-teleport
                    try
                    {
                        bool charged = AttemptDeductSilver(cost);
                        if (!charged)
                        {
                            TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: Teleported to {city.name} but failed to deduct {cost} silver.");
                            ShowInlineDialogMessage($"Teleported to {city.name} (failed to charge {cost} {TravelButtonMod.cfgCurrencyItem.Value})");
                        }
                        else
                        {
                            ShowInlineDialogMessage($"Teleported to {city.name}");
                        }
                    }
                    catch (Exception exCharge)
                    {
                        TravelButtonPlugin.LogWarning("TryTeleportThenCharge: charge attempt threw: " + exCharge);
                        ShowInlineDialogMessage($"Teleported to {city.name} (charge error)");
                    }

                    // Persist visited/cities to config if desired
                    try { TravelButtonMod.PersistCitiesToConfig(); } catch { }

                    // Reset teleporting flag and close the dialog (optional UX choice)
                    try
                    {
                        isTeleporting = false;

                        if (dialogRoot != null) dialogRoot.SetActive(false);
                        if (refreshButtonsCoroutine != null)
                        {
                            StopCoroutine(refreshButtonsCoroutine);
                            refreshButtonsCoroutine = null;
                        }
                    }
                    catch { }
                }
                else
                {
                    TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: teleport to '{city.name}' failed.");
                    ShowInlineDialogMessage("Teleport failed");

                    // Re-enable city buttons so user can try again
                    try
                    {
                        isTeleporting = false;
                        var contentParent = dialogRoot?.transform.Find("ScrollArea/Viewport/Content");
                        if (contentParent != null)
                        {
                            for (int ci = 0; ci < contentParent.childCount; ci++)
                            {
                                var child = contentParent.GetChild(ci);
                                var childBtn = child.GetComponent<Button>();
                                var childImg = child.GetComponent<Image>();
                                if (childBtn != null)
                                {
                                    childBtn.interactable = true;
                                    if (childImg != null) childImg.color = new Color(0.35f, 0.20f, 0.08f, 1f);
                                }
                            }
                        }
                    }
                    catch (Exception exEnable)
                    {
                        TravelButtonPlugin.LogWarning("TryTeleportThenCharge: failed to re-enable buttons after failed teleport: " + exEnable);
                    }
                }
            }));
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TryTeleportThenCharge exception: " + ex);
            isTeleporting = false;
        }
    }

    // Try to determine target position for a city without moving anything.
    // Returns true and sets out position when found (coords or GameObject), false otherwise.
    private bool TryGetTargetPosition(TravelButtonMod.City city, out Vector3 pos)
    {
        pos = Vector3.zero;
        try
        {
            // 1) explicit target GameObject name
            if (!string.IsNullOrEmpty(city.targetGameObjectName))
            {
                var go = GameObject.Find(city.targetGameObjectName);
                if (go != null)
                {
                    pos = go.transform.position;
                    TravelButtonPlugin.LogInfo($"TryGetTargetPosition: found GameObject '{city.targetGameObjectName}' at {pos}");
                    return true;
                }
                else
                {
                    TravelButtonPlugin.LogWarning($"TryGetTargetPosition: target GameObject '{city.targetGameObjectName}' not found in scene for city '{city.name}'.");
                }
            }

            // 2) explicit coords from config / visited metadata
            if (city.coords != null && city.coords.Length >= 3)
            {
                pos = new Vector3(city.coords[0], city.coords[1], city.coords[2]);
                TravelButtonPlugin.LogInfo($"TryGetTargetPosition: using explicit coords {pos} for city '{city.name}'");
                return true;
            }

            // 3) heuristic: find any scene object with the city name in it (useful when scene or objects include the region name)
            try
            {
                var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var tr in allTransforms)
                {
                    if (tr == null) continue;
                    if (tr.name.IndexOf(city.name ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pos = tr.position;
                        TravelButtonPlugin.LogInfo($"TryGetTargetPosition: heuristic found scene object '{tr.name}' for city '{city.name}' at {pos}");
                        return true;
                    }
                }
            }
            catch { }

            // not found
            TravelButtonPlugin.LogInfo($"TryGetTargetPosition: no explicit position found for city '{city.name}'.");
            return false;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryGetTargetPosition exception: " + ex);
            pos = Vector3.zero;
            return false;
        }
    }

    // Sanity-check coords to detect obviously wrong positions (helpful to spot placeholder coords).
    // This is intentionally conservative: it only flags extremely large NaN/inf coordinates.
    private bool IsCoordsReasonable(Vector3 v)
    {
        if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;
        if (float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)) return false;

        // very large threshold to avoid false positives; tune if your world uses large coordinates
        const float MAX_REASONABLE = 200000f;
        if (Mathf.Abs(v.x) > MAX_REASONABLE || Mathf.Abs(v.y) > MAX_REASONABLE || Mathf.Abs(v.z) > MAX_REASONABLE) return false;

        return true;
    }

    // Teleport player to a specific world position. Returns true on success.
    private bool AttemptTeleportToPosition(Vector3 targetPos)
    {
        try
        {
            Transform playerTransform = null;
            var tagged = GameObject.FindWithTag("Player");
            if (tagged != null)
            {
                playerTransform = tagged.transform;
                TravelButtonPlugin.LogInfo("AttemptTeleportToPosition: found player by tag 'Player'.");
            }

            if (playerTransform == null)
            {
                string[] playerTypeCandidates = new string[] { "PlayerCharacter", "PlayerEntity", "Character", "PC_Player" };
                foreach (var tname in playerTypeCandidates)
                {
                    var t = Type.GetType(tname + ", Assembly-CSharp");
                    if (t != null)
                    {
                        var objs = UnityEngine.Object.FindObjectsOfType(t);
                        if (objs != null && objs.Length > 0)
                        {
                            var comp = objs[0] as Component;
                            if (comp != null)
                            {
                                playerTransform = comp.transform;
                                TravelButtonPlugin.LogInfo($"AttemptTeleportToPosition: found player via type {tname}.");
                                break;
                            }
                        }
                    }
                }
            }

            if (playerTransform == null)
            {
                var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var tr in allTransforms)
                {
                    if (tr.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        playerTransform = tr;
                        TravelButtonPlugin.LogInfo($"AttemptTeleportToPosition: found player by name heuristic: {tr.name}");
                        break;
                    }
                }
            }

            if (playerTransform == null)
            {
                TravelButtonPlugin.LogError("AttemptTeleportToPosition: could not locate player transform. Aborting.");
                return false;
            }

            playerTransform.position = targetPos;
            var rb = playerTransform.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPosition: teleported player to {targetPos}.");
            return true;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("AttemptTeleportToPosition: teleport failed: " + ex);
            return false;
        }
    }

    // Best-effort refund by trying to call common Add/Give methods or incrementing detected money fields/properties.
    // Returns true if a refund action was performed successfully.
    private bool AttemptRefundSilver(int amount)
    {
        TravelButtonPlugin.LogInfo($"AttemptRefundSilver: trying to refund {amount} silver.");

        var allMonoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
        foreach (var mb in allMonoBehaviours)
        {
            var t = mb.GetType();

            // Try methods that add money
            string[] addMethodNames = new string[] { "AddMoney", "GrantMoney", "GiveMoney", "AddSilver", "GiveSilver", "GrantSilver", "AddCoins" };
            foreach (var mn in addMethodNames)
            {
                var mi = t.GetMethod(mn, new Type[] { typeof(int) });
                if (mi != null)
                {
                    try
                    {
                        mi.Invoke(mb, new object[] { amount });
                        TravelButtonPlugin.LogInfo($"AttemptRefundSilver: called {t.FullName}.{mn}({amount})");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptRefundSilver: calling {t.FullName}.{mn} threw: {ex}");
                    }
                }
            }

            // Try to increment fields/properties that look like currency
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
                            fi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"AttemptRefundSilver: added {amount} to {t.FullName}.{fi.Name} (int). New value {cur + amount}.");
                            return true;
                        }
                        else if (fi.FieldType == typeof(long))
                        {
                            long cur = (long)fi.GetValue(mb);
                            fi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"AttemptRefundSilver: added {amount} to {t.FullName}.{fi.Name} (long). New value {cur + amount}.");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptRefundSilver: field access {t.FullName}.{fi.Name} threw: {ex}");
                    }
                }
            }

            foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var name = pi.Name.ToLower();
                if ((name.Contains("silver") || name.Contains("money") || name.Contains("gold") || name.Contains("coins") || name.Contains("currency")) && pi.CanRead && pi.CanWrite)
                {
                    try
                    {
                        if (pi.PropertyType == typeof(int))
                        {
                            int cur = (int)pi.GetValue(mb);
                            pi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"AttemptRefundSilver: added {amount} to {t.FullName}.{pi.Name} (int). New value {cur + amount}.");
                            return true;
                        }
                        else if (pi.PropertyType == typeof(long))
                        {
                            long cur = (long)pi.GetValue(mb);
                            pi.SetValue(mb, cur + amount);
                            TravelButtonPlugin.LogInfo($"AttemptRefundSilver: added {amount} to {t.FullName}.{pi.Name} (long). New value {cur + amount}.");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptRefundSilver: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
            }
        }

        TravelButtonPlugin.LogWarning("AttemptRefundSilver: could not find a place to refund the currency automatically.");
        return false;
    }

    // Add inside the TeleportHelpers static class
    public static GameObject FindPlayerRoot()
    {
        try
        {
            // 1) Try common runtime player component types (Assembly-CSharp)
            string[] typeNames = new string[]
            {
            "PlayerCharacter",
            "PlayerEntity",
            "LocalPlayer",
            "PlayerController",
            "Character",
            "PC_Player"
            };

            foreach (var tn in typeNames)
            {
                try
                {
                    var t = Type.GetType(tn + ", Assembly-CSharp") ?? Type.GetType(tn);
                    if (t == null) continue;
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0)
                    {
                        var comp = objs[0] as Component;
                        if (comp != null)
                            return comp.gameObject.transform.root.gameObject;
                    }
                }
                catch { /* ignore type lookup errors */ }
            }

            // 2) Try object tagged "Player"
            try
            {
                var byTag = GameObject.FindWithTag("Player");
                if (byTag != null) return byTag.transform.root.gameObject;
            }
            catch { /* ignore */ }

            // 3) Heuristic: search active scene root objects for names containing "Player" or "PlayerChar"
            try
            {
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (activeScene.IsValid())
                {
                    var roots = activeScene.GetRootGameObjects();
                    foreach (var r in roots)
                    {
                        if (r == null) continue;
                        var rn = r.name ?? "";
                        if (rn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            rn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            rn.IndexOf("PC_", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return r;
                        }

                        // deeper children
                        var transforms = r.GetComponentsInChildren<Transform>(true);
                        if (transforms != null)
                        {
                            for (int i = 0; i < transforms.Length; i++)
                            {
                                var t = transforms[i];
                                if (t == null) continue;
                                var tn = t.name ?? "";
                                if (tn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    tn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return t.root.gameObject;
                                }
                            }
                        }
                    }
                }

                // 4) Global fallback: check all loaded Transforms (expensive)
                var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
                foreach (var t in allTransforms)
                {
                    if (t == null) continue;
                    var tn = t.name ?? "";
                    if (tn.IndexOf("PlayerChar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return t.root.gameObject;
                    }
                }
            }
            catch { /* ignore */ }

            // 5) Last resort: Camera.main's root
            try
            {
                if (Camera.main != null) return Camera.main.transform.root.gameObject;
            }
            catch { /* ignore */ }
        }
        catch { /* swallow */ }

        return null;
    }

    // In TeleportHelpers static class - update AttemptTeleportToPositionSafe or the method you use to teleport
    public static bool AttemptTeleportToPositionSafe(Vector3 target)
    {
        try
        {
            var initialRoot = FindPlayerRoot();
            if (initialRoot == null)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: player root not found.");
                return false;
            }

            var playerGO = TeleportHelpers.ResolveActualPlayerGameObject(initialRoot) ?? initialRoot;

            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: chosen player object = '{playerGO.name}' (root id={playerGO.GetInstanceID()})");

            var before = playerGO.transform.position;
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: BEFORE pos = ({before.x:F3}, {before.y:F3}, {before.z:F3})");

            // Ensure target is reasonably above -100 and not obviously underground
            Vector3 candidate = target;

            // If target y is extremely low, try to find ground by raycasting down from a high point above target
            bool adjusted = false;
            if (candidate.y < -5f)
            {
                TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: target.y {candidate.y:F3} looks suspiciously low - trying raycast-ground fallback.");
                if (TryFindGroundAt(candidate, out Vector3 grounded))
                {
                    candidate = grounded;
                    adjusted = true;
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: raycast-ground found at {candidate}");
                }
                else
                {
                    // if no ground found, lift up to a safe nominal height to avoid being under level geometry
                    candidate.y = 2.0f;
                    adjusted = true;
                    TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: no ground found - raising target to y={candidate.y:F3}");
                }
            }
            else
            {
                // normal path: still try a short raycast downward from a small height above candidate to ensure we are not inside geometry
                if (TryFindGroundAt(candidate, out Vector3 grounded2))
                {
                    // If ground is reasonably different from candidate, use it (helps with small offsets)
                    if (Mathf.Abs(grounded2.y - candidate.y) > 0.5f)
                    {
                        candidate = grounded2;
                        adjusted = true;
                        TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: adjusted target to nearest ground {candidate}");
                    }
                }
            }

            if (adjusted)
                TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: final teleport target = ({candidate.x:F3}, {candidate.y:F3}, {candidate.z:F3})");

            // Clear any moving rigidbody to reduce physics teleport quirks
            try
            {
                var rb = playerGO.GetComponentInChildren<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch { /* ignore */ }

            // Perform the move
            try
            {
                playerGO.transform.position = candidate;
            }
            catch (Exception exMove)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: exception while setting position: " + exMove);
                return false;
            }

            var after = playerGO.transform.position;
            TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: AFTER pos = ({after.x:F3}, {after.y:F3}, {after.z:F3})");

            // Move camera by the same delta so the view follows the player (non-invasive)
            try
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 delta = after - before;
                    cam.transform.position = cam.transform.position + delta;
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: Camera moved by delta ({delta.x:F3}, {delta.y:F3}, {delta.z:F3}) to ({cam.transform.position.x:F3}, {cam.transform.position.y:F3}, {cam.transform.position.z:F3})");
                }
                else
                {
                    TravelButtonPlugin.LogInfo("AttemptTeleportToPositionSafe: Camera.main is null - skipping camera move.");
                }
            }
            catch (Exception exCam)
            {
                TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: camera reposition failed: " + exCam.Message);
            }

            // If after teleport the player is still obviously below reasonable level, try a backup relocation
            if (after.y < -10f)
            {
                TravelButtonPlugin.LogWarning($"AttemptTeleportToPositionSafe: AFTER.y ({after.y:F3}) still very low - attempting backup raise.");
                Vector3 backup = new Vector3(after.x, 2.0f, after.z);
                try
                {
                    playerGO.transform.position = backup;
                    var after2 = playerGO.transform.position;
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToPositionSafe: AFTER backup pos = ({after2.x:F3}, {after2.y:F3}, {after2.z:F3})");
                }
                catch (Exception exb)
                {
                    TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: backup reposition failed: " + exb.Message);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("AttemptTeleportToPositionSafe: exception: " + ex);
            return false;
        }
    }

    // Helper: raycast down from above 'pos' to find nearest ground point. Returns grounded point (with a small offset).
    private static bool TryFindGroundAt(Vector3 pos, out Vector3 grounded)
    {
        grounded = pos;
        try
        {
            // Raycast from high above the target downwards to find ground
            Vector3 origin = pos + Vector3.up * 50f;
            RaycastHit hit;
            if (Physics.Raycast(origin, Vector3.down, out hit, 200f, ~0, QueryTriggerInteraction.Ignore))
            {
                grounded = new Vector3(pos.x, hit.point.y + 0.5f, pos.z);
                return true;
            }
        }
        catch { }
        return false;
    }

    public static Vector3 GetGroundedPosition(Vector3 desiredPosition, float maxUp = 50f, float maxDown = 200f, float groundOffset = 1.2f)
    {
        try
        {
            // Raycast from above downwards to find the nearest collider/ground under the desired X/Z.
            Vector3 rayStart = desiredPosition + Vector3.up * maxUp;
            Ray ray = new Ray(rayStart, Vector3.down);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, maxUp + maxDown, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(desiredPosition.x, hit.point.y + groundOffset, desiredPosition.z);
            }

            // If no physics hit, attempt Terrain.activeTerrain.SampleHeight via reflection (if Terrain module loaded)
            try
            {
                // Try to get the Terrain type from the Unity Terrain module
                // Full name including assembly: "UnityEngine.Terrain, UnityEngine.TerrainModule"
                var terrainType = Type.GetType("UnityEngine.Terrain, UnityEngine.TerrainModule");
                if (terrainType != null)
                {
                    // Get static property activeTerrain
                    var activeTerrainProp = terrainType.GetProperty("activeTerrain", BindingFlags.Public | BindingFlags.Static);
                    var activeTerrain = activeTerrainProp?.GetValue(null);
                    if (activeTerrain != null)
                    {
                        // call SampleHeight(Vector3) on the activeTerrain instance
                        var sampleHeightMethod = terrainType.GetMethod("SampleHeight", new Type[] { typeof(Vector3) });
                        if (sampleHeightMethod != null)
                        {
                            var terrainYObj = sampleHeightMethod.Invoke(activeTerrain, new object[] { desiredPosition });
                            if (terrainYObj is float terrainLocalHeight)
                            {
                                // activeTerrain.transform.position.y may be non-zero; get transform.position.y via reflection
                                var transformProp = terrainType.GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                                var terrainTransform = transformProp?.GetValue(activeTerrain) as Transform;
                                float terrainWorldY = (terrainTransform != null) ? terrainTransform.position.y : 0f;
                                float worldHeight = terrainLocalHeight + terrainWorldY;
                                return new Vector3(desiredPosition.x, worldHeight + groundOffset, desiredPosition.z);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore terrain reflection errors; continue to fallback below
            }

            // last-resort short downward ray from a small height above desiredPosition
            rayStart = desiredPosition + Vector3.up * 1.0f;
            ray = new Ray(rayStart, Vector3.down);
            if (Physics.Raycast(ray, out hit, maxUp + 1f, ~0, QueryTriggerInteraction.Ignore))
            {
                return new Vector3(desiredPosition.x, hit.point.y + groundOffset, desiredPosition.z);
            }

            // Nothing found: return desired position with a small upward offset so player isn't at y=0 in many maps
            return new Vector3(desiredPosition.x, desiredPosition.y + groundOffset, desiredPosition.z);
        }
        catch (Exception)
        {
            // On unexpected errors, return desired position + offset
            return new Vector3(desiredPosition.x, desiredPosition.y + groundOffset, desiredPosition.z);
        }
    }

    private void TryPayAndTeleport(TravelButtonMod.City city)
    {
        // Kept for compatibility with older callers; but the new flow uses TryTeleportThenCharge.
        TryTeleportThenCharge(city, city.price ?? TravelButtonMod.cfgTravelCost.Value);
    }

    /*   // Older implementation preserved as comment for reference...
        ... (omitted) ...
    */

    private void CloseDialogAndStopRefresh()
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
            TravelButtonPlugin.LogWarning("CloseDialogAndStopRefresh failed: " + ex);
        }
    }

    // Refresh city buttons while dialog is open: re-evaluates player's currency and enables/disables buttons.
    private IEnumerator RefreshCityButtonsWhileOpen(GameObject dialog)
    {
        while (dialog != null && dialog.activeInHierarchy)
        {
            try
            {
                // fetch current player money
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

                        // extract city name from GameObject name "CityButton_<name>"
                        string objName = child.name;
                        if (objName.StartsWith("CityButton_"))
                        {
                            string cityName = objName.Substring("CityButton_".Length);
                            bool enabledByConfig = TravelButtonMod.IsCityEnabled(cityName);
                            bool visited = false;
                            try { visited = VisitedTracker.HasVisited(cityName); } catch { visited = false; }

                            // determine cost (per-city or global) by reflection similar to creation
                            int cost = TravelButtonMod.cfgTravelCost.Value;
                            try
                            {
                                // try to find the city object in TravelButtonMod.Cities
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
                                    else
                                    {
                                        var priceProp = foundCity.GetType().GetProperty("price");
                                        if (priceProp != null)
                                        {
                                            var pv = priceProp.GetValue(foundCity);
                                            if (pv is int) cost = (int)pv;
                                            else if (pv is long) cost = (int)(long)pv;
                                        }
                                    }
                                }
                            }
                            catch { /* ignore */ }

                            bool coordsAvailable = false;
                            try
                            {
                                // quick check for coords or targetGameObject
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
                TravelButtonPlugin.LogWarning("RefreshCityButtonsWhileOpen exception: " + ex);
            }

            // refresh every 1 second while open
            yield return new WaitForSeconds(1f);
        }

        refreshButtonsCoroutine = null;
    }

    // Finish layout after a short delay so Unity's RectTransforms have valid sizes
    private IEnumerator FinishDialogLayoutAndShow(ScrollRect scrollRect, RectTransform viewportRt, RectTransform contentRt)
    {
        // Wait up to two frames before doing layout work so rects have time to update.
        // These yields must be outside any try/catch to avoid CS1626.
        yield return null;
        yield return null;

        try
        {
            // Ensure content width matches viewport width so children that stretch/anchor properly will fill the width
            float viewportWidth = viewportRt.rect.width;

            if (viewportWidth > 0f)
            {
                contentRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, viewportWidth);
                TravelButtonPlugin.LogInfo($"FinishDialogLayoutAndShow: set content width to {viewportWidth}");
            }
            else
            {
                TravelButtonPlugin.LogWarning("FinishDialogLayoutAndShow: viewport width is zero after two frames - layout may be incorrect.");
            }

            // Rebuild layouts top-down
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);
            LayoutRebuilder.ForceRebuildLayoutImmediate(viewportRt);
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.GetComponent<RectTransform>());

            // Make sure ScrollRect shows top
            scrollRect.verticalNormalizedPosition = 1f;

            TravelButtonPlugin.LogInfo("FinishDialogLayoutAndShow: finished rebuild and set scroll position.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("FinishDialogLayoutAndShow exception: " + ex);
        }
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

        // wait two frames (yields must not be inside a try/catch)
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

    /// <summary>
    /// Try to detect player's currency amount. Returns -1 if could not determine.
    /// This is a best-effort reflection-based reader scanning MonoBehaviours, fields and properties.
    /// </summary>
    private long GetPlayerCurrencyAmountOrMinusOne()
    {
        try
        {
            var allMono = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allMono)
            {
                var t = mb.GetType();

                // Try common property names first (read-only or read/write)
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
                        catch (Exception) { }
                    }
                }

                // Try methods like GetMoney(), GetSilver()
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
                        catch (Exception) { }
                    }
                }

                // Fields
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
                        catch (Exception) { }
                    }
                }

                // Properties (generic scan)
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
                        catch (Exception) { }
                    }
                }
            }

            TravelButtonPlugin.LogWarning("GetPlayerCurrencyAmountOrMinusOne: could not detect a currency field/property automatically.");
            return -1;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("GetPlayerCurrencyAmountOrMinusOne exception: " + ex);
            return -1;
        }
    }

    // add inside TravelButtonUI (or a debug MonoBehaviour)
    private void DumpTravelButtonState()
    {
        try
        {
            var tb = GameObject.Find("TravelButton");
            if (tb == null)
            {
                TravelButtonPlugin.LogWarning("DumpTravelButtonState: TravelButton GameObject not found.");
                return;
            }

            var rt = tb.GetComponent<RectTransform>();
            var btn = tb.GetComponent<UnityEngine.UI.Button>();
            var img = tb.GetComponent<UnityEngine.UI.Image>();
            var cg = tb.GetComponent<CanvasGroup>();
            var root = tb.transform.root;
            TravelButtonPlugin.LogInfo($"DumpTravelButtonState: name='{tb.name}', activeSelf={tb.activeSelf}, activeInHierarchy={tb.activeInHierarchy}");
            TravelButtonPlugin.LogInfo($"DumpTravelButtonState: parent='{tb.transform.parent?.name}', root='{root?.name}'");
            if (rt != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: anchoredPosition={rt.anchoredPosition}, sizeDelta={rt.sizeDelta}, anchorMin={rt.anchorMin}, anchorMax={rt.anchorMax}");
            if (btn != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: Button.interactable={btn.interactable}");
            if (img != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: Image.color={img.color}, raycastTarget={img.raycastTarget}");
            if (cg != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: CanvasGroup alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts}");
            var canvas = tb.GetComponentInParent<Canvas>();
            if (canvas != null) TravelButtonPlugin.LogInfo($"DumpTravelButtonState: Canvas name={canvas.gameObject.name}, sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode}");
            else TravelButtonPlugin.LogWarning("DumpTravelButtonState: No parent Canvas found.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("DumpTravelButtonState exception: " + ex);
        }
    }

    // Force the button visible near top-center of the screen (temporary debug)
    private void ForceShowTravelButton()
    {
        try
        {
            var tb = GameObject.Find("TravelButton");
            if (tb == null)
            {
                TravelButtonPlugin.LogWarning("ForceShowTravelButton: TravelButton not found.");
                return;
            }

            // find or create a top-level Canvas
            var canvas = GameObject.FindObjectOfType<Canvas>();
            if (canvas == null)
            {
                var go = new GameObject("TravelButton_DebugCanvas");
                canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                UnityEngine.Object.DontDestroyOnLoad(go);
            }
            else
            {
                // ensure graphic raycaster present
                if (canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                    canvas.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            }

            tb.transform.SetParent(canvas.transform, false);

            var rt = tb.GetComponent<RectTransform>();
            if (rt == null) rt = tb.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -40);
            rt.sizeDelta = new Vector2(140, 32);

            tb.SetActive(true);

            // ensure it's visible (no dim)
            var img = tb.GetComponent<UnityEngine.UI.Image>();
            if (img != null) img.color = new Color(0.45f, 0.26f, 0.13f, 1f);

            var btn = tb.GetComponent<UnityEngine.UI.Button>();
            if (btn != null) btn.interactable = true;

            TravelButtonPlugin.LogInfo("ForceShowTravelButton: forced TravelButton onto top Canvas and made visible.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ForceShowTravelButton exception: " + ex);
        }
    }

    private bool AttemptDeductSilver(int amount)
    {
        TravelButtonPlugin.LogInfo($"AttemptDeductSilver: trying to deduct {amount} silver.");

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
                        TravelButtonPlugin.LogInfo($"AttemptDeductSilver: called {t.FullName}.{mn}({amount}) -> {res}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptDeductSilver: calling {t.FullName}.{mn} threw: {ex}");
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
                                TravelButtonPlugin.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{fi.Name} (int). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{fi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                        else if (fi.FieldType == typeof(long))
                        {
                            long cur = (long)fi.GetValue(mb);
                            if (cur >= amount)
                            {
                                fi.SetValue(mb, cur - amount);
                                TravelButtonPlugin.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{fi.Name} (long). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{fi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptDeductSilver: field access {t.FullName}.{fi.Name} threw: {ex}");
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
                                TravelButtonPlugin.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{pi.Name} (int). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{pi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                        else if (pi.PropertyType == typeof(long) && pi.CanRead && pi.CanWrite)
                        {
                            long cur = (long)pi.GetValue(mb);
                            if (cur >= amount)
                            {
                                pi.SetValue(mb, cur - amount);
                                TravelButtonPlugin.LogInfo($"AttemptDeductSilver: deducted {amount} from {t.FullName}.{pi.Name} (long). New value {cur - amount}.");
                                return true;
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning($"AttemptDeductSilver: not enough funds in {t.FullName}.{pi.Name} ({cur} < {amount}).");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning($"AttemptDeductSilver: property access {t.FullName}.{pi.Name} threw: {ex}");
                    }
                }
            }
        }

        TravelButtonPlugin.LogWarning("AttemptDeductSilver: could not find an inventory/money field or method automatically. Travel aborted.");
        return false;
    }

    private bool AttemptTeleportToCity(TravelButtonMod.City city)
    {
        TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: trying to teleport to {city.name}");

        Vector3? targetPos = null;
        if (!string.IsNullOrEmpty(city.targetGameObjectName))
        {
            var targetGO = GameObject.Find(city.targetGameObjectName);
            if (targetGO != null)
            {
                targetPos = targetGO.transform.position;
                TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: found GameObject '{city.targetGameObjectName}' at {targetPos.Value}");
            }
            else
            {
                TravelButtonPlugin.LogWarning($"AttemptTeleportToCity: target GameObject '{city.targetGameObjectName}' not found in scene.");
            }
        }

        if (targetPos == null && city.coords != null && city.coords.Length >= 3)
        {
            targetPos = new Vector3(city.coords[0], city.coords[1], city.coords[2]);
            TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: using explicit coords {targetPos.Value}");
        }
        else if (targetPos == null && city.coords != null)
        {
            TravelButtonPlugin.LogWarning($"AttemptTeleportToCity: coords provided but length < 3 for {city.name}. coords.length={city.coords.Length}");
        }

        if (targetPos == null)
        {
            // Extra attempt: try to find a scene object with the city's name (case-insensitive)
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var tr in allTransforms)
            {
                if (tr.name.IndexOf(city.name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetPos = tr.position;
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: fallback found scene object '{tr.name}' for city '{city.name}' at {targetPos.Value}");
                    break;
                }
            }
        }

        if (targetPos == null)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            TravelButtonPlugin.LogError($"AttemptTeleportToCity: no valid target for {city.name} (scene='{scene.name}'). Aborting teleport.");
            return false;
        }

        // Locate player transform more robustly
        Transform playerTransform = null;
        var tagged = GameObject.FindWithTag("Player");
        if (tagged != null)
        {
            playerTransform = tagged.transform;
            TravelButtonPlugin.LogInfo("AttemptTeleportToCity: found player by tag 'Player'.");
        }

        if (playerTransform == null)
        {
            string[] playerTypeCandidates = new string[] { "PlayerCharacter", "PlayerEntity", "Character", "PC_Player", "PlayerController", "LocalPlayer" };
            foreach (var tname in playerTypeCandidates)
            {
                try
                {
                    var t = Type.GetType(tname + ", Assembly-CSharp");
                    if (t != null)
                    {
                        var objs = UnityEngine.Object.FindObjectsOfType(t);
                        if (objs != null && objs.Length > 0)
                        {
                            var comp = objs[0] as Component;
                            if (comp != null)
                            {
                                playerTransform = comp.transform;
                                TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: found player via type {tname} (object name='{comp.name}').");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning($"AttemptTeleportToCity: exception checking type {tname}: {ex.Message}");
                }
            }
        }

        if (playerTransform == null)
        {
            var allTransforms2 = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var tr in allTransforms2)
            {
                if (tr.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tr.name.IndexOf("pc_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    playerTransform = tr;
                    TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: found player by name heuristic: {tr.name}");
                    break;
                }
            }
        }

        if (playerTransform == null)
        {
            TravelButtonPlugin.LogError("AttemptTeleportToCity: could not locate player transform. Aborting.");
            return false;
        }

        // Helper: perform teleport using the best available API
        bool TrySetTransformPosition(Transform plyTransform, Vector3 pos)
        {
            try
            {
                // Try NavMeshAgent warp first if present (use reflection to avoid compile-time dependency on UnityEngine.AIModule)
                try
                {
                    var navAgentType = Type.GetType("UnityEngine.AI.NavMeshAgent, UnityEngine.AIModule");
                    if (navAgentType != null)
                    {
                        var agentComp = plyTransform.GetComponent(navAgentType);
                        if (agentComp != null)
                        {
                            // check isOnNavMesh property
                            var isOnNavMeshProp = navAgentType.GetProperty("isOnNavMesh");
                            bool isOnNavMesh = false;
                            if (isOnNavMeshProp != null)
                            {
                                var val = isOnNavMeshProp.GetValue(agentComp);
                                if (val is bool b) isOnNavMesh = b;
                            }

                            if (isOnNavMesh)
                            {
                                var warpMethod = navAgentType.GetMethod("Warp", new Type[] { typeof(Vector3) });
                                if (warpMethod != null)
                                {
                                    warpMethod.Invoke(agentComp, new object[] { pos });
                                    TravelButtonPlugin.LogInfo("AttemptTeleportToCity: teleported using NavMeshAgent.Warp (via reflection).");
                                    return true;
                                }
                            }
                            else
                            {
                                TravelButtonPlugin.LogWarning("AttemptTeleportToCity: NavMeshAgent found but not on NavMesh. Falling back.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("AttemptTeleportToCity: NavMeshAgent reflection attempt failed: " + ex.Message);
                }

                // Try CharacterController: disable/enable around position set
                var cc = plyTransform.GetComponent<CharacterController>();
                if (cc != null)
                {
                    cc.enabled = false;
                    plyTransform.position = pos;
                    cc.enabled = true;
                    TravelButtonPlugin.LogInfo("AttemptTeleportToCity: teleported using CharacterController disable/enable.");
                    return true;
                }

                // Try Rigidbody.MovePosition / setting rigidbody position
                var rb = plyTransform.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    // If it is kinematic, set transform, otherwise set rb.position and zero velocity
                    if (rb.isKinematic)
                    {
                        plyTransform.position = pos;
                    }
                    else
                    {
                        rb.position = pos;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    TravelButtonPlugin.LogInfo("AttemptTeleportToCity: teleported using Rigidbody reposition.");
                    return true;
                }

                // Try parent's rigidbody (some setups attach movement to parent)
                if (plyTransform.parent != null)
                {
                    var parentRb = plyTransform.parent.GetComponent<Rigidbody>();
                    if (parentRb != null)
                    {
                        parentRb.position = pos;
                        parentRb.velocity = Vector3.zero;
                        parentRb.angularVelocity = Vector3.zero;
                        TravelButtonPlugin.LogInfo("AttemptTeleportToCity: teleported by moving parent Rigidbody.");
                        return true;
                    }
                }

                // Final fallback: set transform.position directly
                plyTransform.position = pos;
                TravelButtonPlugin.LogInfo("AttemptTeleportToCity: teleported by setting transform.position (fallback).");
                return true;
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogError("AttemptTeleportToCity: teleport attempt failed: " + ex);
                return false;
            }
        }

        // If the found transform is not root of character, try to use root transform (some prefabs place the visible character below a root)
        Transform effectiveTransform = playerTransform;
        if (playerTransform.root != null && playerTransform.root != playerTransform)
        {
            TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: player transform root is '{playerTransform.root.name}', using root for teleport attempts.");
            effectiveTransform = playerTransform.root;
        }

        // Try teleporting; if it fails on the effectiveTransform, try using the original transform as a last attempt
        bool teleported = TrySetTransformPosition(effectiveTransform, targetPos.Value);
        if (!teleported && effectiveTransform != playerTransform)
        {
            TravelButtonPlugin.LogWarning("AttemptTeleportToCity: teleport via root failed, trying original player transform.");
            teleported = TrySetTransformPosition(playerTransform, targetPos.Value);
        }

        if (teleported)
        {
            TravelButtonPlugin.LogInfo($"AttemptTeleportToCity: teleported player to {targetPos.Value}.");
            return true;
        }
        else
        {
            TravelButtonPlugin.LogError("AttemptTeleportToCity: teleport strategies exhausted and all failed.");
            return false;
        }
    }

    // Show a short, inline message in the open dialog (if present). Clears after a few seconds.
    private Coroutine inlineMessageClearCoroutine;
    private void ShowInlineDialogMessage(string msg)
    {
        try
        {
            TravelButtonPlugin.LogInfo("[TravelButton] Inline message: " + msg);
            if (dialogRoot == null) return;
            var inline = dialogRoot.transform.Find("InlineMessage");
            if (inline == null)
            {
                TravelButtonPlugin.LogWarning("ShowInlineDialogMessage: InlineMessage element not found in dialogRoot.");
                return;
            }
            var txt = inline.GetComponent<Text>();
            if (txt == null) return;
            txt.text = msg;

            if (inlineMessageClearCoroutine != null)
            {
                StopCoroutine(inlineMessageClearCoroutine);
                inlineMessageClearCoroutine = null;
            }
            inlineMessageClearCoroutine = StartCoroutine(ClearInlineMessageAfterDelay(3f));
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ShowInlineDialogMessage exception: " + ex);
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
        inlineMessageClearCoroutine = null;
    }

    // ClickLogger for debugging
    private class ClickLogger : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            TravelButtonPlugin.LogInfo("ClickLogger: OnPointerClick received on " + gameObject.name + " button.");
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            TravelButtonPlugin.LogInfo("ClickLogger: OnPointerEnter on " + gameObject.name);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            TravelButtonPlugin.LogInfo("ClickLogger: OnPointerExit on " + gameObject.name);
        }
    }
}