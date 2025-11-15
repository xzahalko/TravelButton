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
// and to correlate TravelButtonPlugin.LogCityAnchorsFromLoadedScenes() output to anchor names in scenes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using uNature.Core.Terrains;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
    
    private float dialogOpenedTime = 0f;

    private const string CustomIconFilename = "TravelButton_icon.png";
    private const string ResourcesIconPath = "TravelButton/icon"; // Resources/TravelButton/icon.png -> Resources.Load(ResourcesIconPath)

    void Start()
    {
        TravelButtonPlugin.LogInfo("TravelButtonUI.Start called.");
        CreateTravelButton();
        EnsureInputSystems();
        // start polling for inventory container (will reparent once found)
        StartCoroutine(PollForInventoryParentImpl());
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

    // Replacement helper that loads an image file into a Texture2D robustly.
    // Uses ImageConversion.LoadImage if available, otherwise falls back to invoking Texture2D.LoadImage via reflection.
    // Returns a Sprite or null if loading failed.
    // Replacement LoadCustomButtonSprite that avoids any direct calls to Texture2D.LoadImage
    // (so it won't trigger "LoadImage not known" compile errors). It uses reflection only.
    private Sprite LoadCustomButtonSprite()
    {
        // Try Resources first (Resources/TravelButton/icon.png -> Resources.Load("TravelButton/icon"))
        try
        {
            var res = Resources.Load<Sprite>(ResourcesIconPath);
            if (res != null) return res;
        }
        catch { }

        string asmPath = null;
        try { asmPath = System.Reflection.Assembly.GetExecutingAssembly().Location; } catch { asmPath = null; }

        string[] candidates;
        if (!string.IsNullOrEmpty(asmPath))
        {
            var dir = System.IO.Path.GetDirectoryName(asmPath);
            candidates = new string[]
            {
            System.IO.Path.Combine(dir, CustomIconFilename),
            System.IO.Path.Combine(dir, "resources", CustomIconFilename),
            System.IO.Path.Combine(Application.dataPath ?? string.Empty, CustomIconFilename)
            };
        }
        else
        {
            candidates = new string[]
            {
            System.IO.Path.Combine(Application.dataPath ?? string.Empty, CustomIconFilename)
            };
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (string.IsNullOrEmpty(candidate) || !System.IO.File.Exists(candidate)) continue;
                var bytes = System.IO.File.ReadAllBytes(candidate);
                if (bytes == null || bytes.Length == 0) continue;

                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                bool loaded = false;

                // 1) Try UnityEngine.ImageConversion.LoadImage(Texture2D, byte[]) via reflection
                try
                {
                    var imageConvType = Type.GetType("UnityEngine.ImageConversion, UnityEngine");
                    if (imageConvType != null)
                    {
                        var loadMethod = imageConvType.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                        if (loadMethod != null)
                        {
                            var result = loadMethod.Invoke(null, new object[] { tex, bytes });
                            if (result is bool b) loaded = b;
                            else loaded = true; // some Unity variants return void; assume success if no exception
                        }
                    }
                }
                catch { /* ignore and try next fallback */ }

                // 2) Try Texture2D.LoadImage(byte[]) via reflection (instance method)
                if (!loaded)
                {
                    try
                    {
                        var texType = typeof(Texture2D);
                        var mi = texType.GetMethod("LoadImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(byte[]) }, null);
                        if (mi != null)
                        {
                            var invokeResult = mi.Invoke(tex, new object[] { bytes });
                            if (invokeResult is bool b) loaded = b;
                            else loaded = true; // assume success if no exception
                        }
                    }
                    catch { /* ignore */ }
                }

                // If neither reflective API was available/successful, we cannot safely call LoadImage directly
                if (!loaded)
                {
                    UnityEngine.Object.Destroy(tex);
                    TravelButtonPlugin.LogInfo($"LoadCustomButtonSprite: could not find suitable LoadImage API for '{candidate}'");
                    continue;
                }

                try { tex.Apply(true, false); } catch { try { tex.Apply(); } catch { } }

                var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                spr.name = "TravelButton_CustomIcon";
                return spr;
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("LoadCustomButtonSprite: failed to load candidate image: " + ex);
                continue;
            }
        }

        return null;
    }

    // Call this at the end of ReparentButtonToInventory (or wherever you configure the visuals)
    private void ApplyCustomIconToButton(GameObject buttonObject)
    {
        if (buttonObject == null) return;

        try
        {
            var img = buttonObject.GetComponent<Image>();
            if (img == null)
            {
                img = buttonObject.AddComponent<Image>();
            }

            // Try to load custom sprite
            var custom = LoadCustomButtonSprite();
            if (custom != null)
            {
                img.sprite = custom;
                img.type = Image.Type.Simple;
                img.preserveAspect = true;
                img.color = Color.white; // ensure sprite shows as-is
                // if the button has a child Text label, we can hide it when using an icon
                var txt = buttonObject.GetComponentInChildren<Text>();
                if (txt != null)
                {
                    try { txt.gameObject.SetActive(false); } catch { }
                }
            }
            else
            {
                // fallback: keep existing visuals or tint (ensure visible)
                img.color = new Color(0.12f, 0.45f, 0.85f, 1f);
            }

            // Make sure button is visible on top of UI
            var parentCanvas = buttonObject.GetComponentInParent<Canvas>();
            if (parentCanvas != null)
            {
                parentCanvas.sortingOrder = Math.Max(parentCanvas.sortingOrder, 3000);
            }
            buttonObject.transform.SetAsLastSibling();
            buttonObject.SetActive(true);
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ApplyCustomIconToButton failed: " + ex);
        }
    }

    // Poll for the inventory UI and reparent as soon as we find the inventory.
    // Use StartCoroutine(PollForInventoryParentImpl()) to run this.
    private IEnumerator PollForInventoryParentImpl()
    {
        var wait = new WaitForSeconds(0.5f);
        while (buttonObject != null)
        {
            try
            {
                var invRt = FindAllRectTransformsSafeImpl()
                            .FirstOrDefault(r => string.Equals(r.name, "Inventory", StringComparison.OrdinalIgnoreCase));
                if (invRt != null)
                {
                    TravelButtonPlugin.LogInfo("PollForInventoryParentImpl: found inventory parent '" + invRt.name + "', reparenting button.");
                    ReparentButtonToInventory(invRt.transform);
                    yield break; // stop polling once reparented
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("PollForInventoryParentImpl: " + ex);
            }

            yield return wait;
        }
    }

    // Ensure inventory parenting prefers the TopPanel/Sections toolbar so the button sits inline with icons.
    private void ReparentButtonToInventory(Transform inventoryTransform)
    {
        if (buttonObject == null || inventoryTransform == null) return;
        try
        {
            // 1) Prefer exact Sections group under the inventory (top toolbar)
            var sectionsRt = inventoryTransform.GetComponentsInChildren<RectTransform>(true)
                              .FirstOrDefault(rt => string.Equals(rt.name, "Sections", StringComparison.OrdinalIgnoreCase));
            if (sectionsRt != null)
            {
                TravelButtonPlugin.LogInfo("ReparentButtonToInventory: found Sections under Inventory; using ParentButtonIntoSectionsImpl.");
                ParentButtonIntoSectionsImpl(sectionsRt, Mathf.Max(32f, buttonObject.GetComponent<RectTransform>().sizeDelta.x));
                return;
            }

            // 2) If no Sections, try to find a named toolbar button (btnInventory) and insert next to it
            var templateBtn = inventoryTransform.GetComponentsInChildren<Button>(true)
                               .FirstOrDefault(b => b != null && b.name.IndexOf("btnInventory", StringComparison.OrdinalIgnoreCase) >= 0);

            if (templateBtn == null)
            {
                // fallback: pick any visible toolbar button under inventory
                templateBtn = inventoryTransform.GetComponentsInChildren<Button>(true)
                                .FirstOrDefault(b => b != null && b.gameObject.activeInHierarchy);
            }

            if (templateBtn != null)
            {
                var templRt = templateBtn.GetComponent<RectTransform>();
                var parent = templateBtn.transform.parent ?? inventoryTransform;
                buttonObject.transform.SetParent(parent, false);

                // copy/clone LayoutElement from template if present
                var templLayout = templateBtn.GetComponent<LayoutElement>();
                var layout = buttonObject.GetComponent<LayoutElement>() ?? buttonObject.AddComponent<LayoutElement>();
                if (templLayout != null)
                {
                    layout.preferredWidth = templLayout.preferredWidth;
                    layout.preferredHeight = templLayout.preferredHeight;
                    layout.minWidth = templLayout.minWidth;
                    layout.minHeight = templLayout.minHeight;
                    layout.flexibleWidth = templLayout.flexibleWidth;
                    layout.flexibleHeight = templLayout.flexibleHeight;
                }
                else
                {
                    // conservative defaults
                    layout.preferredWidth = Mathf.Max(32f, buttonObject.GetComponent<RectTransform>().sizeDelta.x);
                    layout.preferredHeight = layout.preferredWidth;
                    layout.flexibleWidth = 0;
                    layout.flexibleHeight = 0;
                }

                // match scale & rotation & local position "reset" for layout containers
                var rt = buttonObject.GetComponent<RectTransform>();
                rt.localScale = templRt.localScale;
                rt.localRotation = templRt.localRotation;
                rt.sizeDelta = new Vector2(layout.preferredWidth > 0 ? layout.preferredWidth : rt.sizeDelta.x,
                                           layout.preferredHeight > 0 ? layout.preferredHeight : rt.sizeDelta.y);

                // place immediately after the template button so it appears inline
                try
                {
                    int insertIndex = templateBtn.transform.GetSiblingIndex() + 1;
                    if (insertIndex <= parent.childCount)
                        buttonObject.transform.SetSiblingIndex(insertIndex);
                    else
                        buttonObject.transform.SetAsLastSibling();
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("ReparentButtonToInventory: set sibling index failed: " + ex);
                    try { buttonObject.transform.SetAsLastSibling(); } catch { }
                }

                // Force layout rebuild on the parent to make the UI update immediately
                try
                {
                    var parentRt = parent as RectTransform;
                    if (parentRt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
                    Canvas.ForceUpdateCanvases();
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("ReparentButtonToInventory: layout rebuild failed: " + ex);
                }

                buttonObject.SetActive(true);
                TravelButtonPlugin.LogInfo("ReparentButtonToInventory: inserted next to template '" + templateBtn.name + "' under parent '" + (parent.name) + "'.");
                return;
            }

            // 3) Last-resort fallback: parent under inventory root itself
            TravelButtonPlugin.LogWarning("ReparentButtonToInventory: no Sections or template button found; parenting under inventory root.");
            buttonObject.transform.SetParent(inventoryTransform, false);
            Canvas.ForceUpdateCanvases();
            buttonObject.SetActive(true);
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ReparentButtonToInventory: unexpected error: " + ex);
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
            // create basic button object
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

            // small toolbar icon size
            const float smallSize = 40f;
            rt.sizeDelta = new Vector2(smallSize, smallSize);

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1) buttonObject.layer = uiLayer;

            // keep hidden until we place it
            try { buttonObject.SetActive(false); } catch { }

            // parent to canvas so we exist in UI space
            var canvas = FindCanvas();
            if (canvas != null)
            {
                buttonObject.transform.SetParent(canvas.transform, false);

                // Try immediate parent into sections if available, otherwise start waiting coroutine
                RectTransform sectionsRt = null;
                try { sectionsRt = FindSectionsGroup(canvas); } catch { sectionsRt = null; }
                TravelButtonPlugin.LogInfo($"CreateTravelButton: FindSectionsGroup returned = {(sectionsRt != null ? sectionsRt.name : "null")}");

                if (sectionsRt != null && sectionsRt.gameObject.activeInHierarchy)
                {
                    try
                    {
                        ParentButtonIntoSectionsImpl(sectionsRt, smallSize);
                        TravelButtonPlugin.LogInfo("CreateTravelButton: parented into Sections and activated.");
                        try { buttonObject.SetActive(true); } catch { }
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning("CreateTravelButton: ParentButtonIntoSectionsImpl failed: " + ex);
                        try { StartCoroutine(PlaceOnToolbarWhenAvailable(canvas, 8f)); } catch { ForceTopToolbarPlacementImpl(canvas); }
                    }
                }
                else
                {
                    // will wait for inventory to open (Sections group to appear)
                    try
                    {
                        StartCoroutine(PlaceOnToolbarWhenAvailable(canvas, 8f));
                        TravelButtonPlugin.LogInfo("CreateTravelButton: started PlaceOnToolbarWhenAvailable coroutine to wait for toolbar.");
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogWarning("CreateTravelButton: failed to start PlaceOnToolbarWhenAvailable: " + ex);
                        ForceTopToolbarPlacementImpl(canvas);
                        try { buttonObject.SetActive(true); } catch { }
                    }
                }

                // make sure on top
                try
                {
                    var parentCanvas = buttonObject.GetComponentInParent<Canvas>();
                    if (parentCanvas != null)
                    {
                        parentCanvas.sortingOrder = Math.Max(parentCanvas.sortingOrder, 3000);
                        buttonObject.transform.SetAsLastSibling();
                    }
                }
                catch { }
            }
            else
            {
                TravelButtonPlugin.LogWarning("CreateTravelButton: no Canvas found at creation time; button created at scene root.");
                try { buttonObject.SetActive(true); } catch { }
            }

            // Label (kept for accessibility; will be hidden when icon applied)
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(buttonObject.transform, false);
            var txt = labelGO.AddComponent<Text>();
            txt.text = "Travel";
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = new Color(0.98f, 0.94f, 0.87f, 1.0f);
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = 12;
            txt.raycastTarget = false;

            var labelRt = labelGO.GetComponent<RectTransform>();
            if (labelRt != null)
            {
                labelRt.anchorMin = new Vector2(0f, 0f);
                labelRt.anchorMax = new Vector2(1f, 1f);
                labelRt.offsetMin = Vector2.zero;
                labelRt.offsetMax = Vector2.zero;
            }

            EnsureInputSystems();

            var logger = buttonObject.GetComponent<ClickLogger>();
            if (logger == null) logger = buttonObject.AddComponent<ClickLogger>();

            travelButton.onClick.AddListener(OpenTravelDialog);

            // Try to apply an icon and hide text if present
            try
            {
                ApplyCustomIconToButton(buttonObject);
                var appliedImg = buttonObject.GetComponent<Image>();
                if (appliedImg != null && appliedImg.sprite != null)
                {
                    try { labelGO.SetActive(false); } catch { }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("CreateTravelButton: ApplyCustomIconToButton failed: " + ex);
            }

            // start persistent monitor to snap back if moved
            try { StartCoroutine(MonitorAndMaintainButtonParentImpl()); } catch (Exception ex) { TravelButtonPlugin.LogWarning("CreateTravelButton: failed to start monitor: " + ex); }
            TravelButtonPlugin.LogInfo("CreateTravelButton: Travel button created, ClickLogger attached, and listener attached.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("CreateTravelButton: exception: " + ex);
        }
    }

    // Improved FindSectionsGroup that looks for the inventory/top-toolbar group when inventory is open.
    private RectTransform FindSectionsGroup(Canvas canvas)
    {
        try
        {
            if (canvas == null) return null;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "btnInventory", "btnEquipment", "btnVitals", "btnEffects",
                "btnCrafting", "btnQuickSlot", "btnSkills", "btnJournal"
            };

            RectTransform fallback = null;
            var all = canvas.GetComponentsInChildren<RectTransform>(true);
            foreach (var rt in all)
            {
                if (rt == null) continue;
                var buttons = rt.GetComponentsInChildren<Button>(true);
                if (buttons == null || buttons.Length == 0) continue;

                int knownCount = 0;
                bool anyActive = false;
                foreach (var b in buttons)
                {
                    if (b == null || b.gameObject == null) continue;
                    if (known.Contains(b.name)) knownCount++;
                    if (b.gameObject.activeInHierarchy) anyActive = true;
                }

                // strong candidate requires several known button names and at least one active (inventory opened)
                if (knownCount >= 3 && anyActive)
                {
                    string path = GetTransformPath(rt);
                    if (path.IndexOf("CharacterMenus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("TopPanel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("Sections", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return rt;
                    }
                    if (fallback == null) fallback = rt;
                }
            }

            if (fallback != null) return fallback;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("FindSectionsGroup: " + ex);
        }

        return null;
    }

    // Parent the button into the toolbar group and copy layout from a template button so it flows with the icons.
    private void ParentButtonIntoSectionsImpl(RectTransform sectionsRt, float desiredSize)
    {
        if (buttonObject == null || sectionsRt == null) return;

        // Find a visible template button to copy layout from; prefer a named toolbar button (btnInventory)
        Button[] allButtons = sectionsRt.GetComponentsInChildren<Button>(true);
        Button template = allButtons.FirstOrDefault(b => b != null && b.gameObject.activeInHierarchy && b.name.IndexOf("btnInventory", StringComparison.OrdinalIgnoreCase) >= 0)
                         ?? allButtons.FirstOrDefault(b => b != null && b.gameObject.activeInHierarchy)
                         ?? allButtons.FirstOrDefault(b => b != null);

        Transform parentTransform = sectionsRt.transform;
        int insertIndex = -1;

        if (template != null)
        {
            parentTransform = template.transform.parent ?? sectionsRt.transform;
            insertIndex = template.transform.GetSiblingIndex() + 1; // place after template
        }

        // Parent without changing local transform immediately
        buttonObject.transform.SetParent(parentTransform, false);

        // Copy layout preferences from template if available
        LayoutElement templLayout = template != null ? template.GetComponent<LayoutElement>() : null;
        var layout = buttonObject.GetComponent<LayoutElement>() ?? buttonObject.AddComponent<LayoutElement>();

        if (templLayout != null)
        {
            // copy important layout fields
            layout.preferredWidth = templLayout.preferredWidth;
            layout.preferredHeight = templLayout.preferredHeight;
            layout.minWidth = templLayout.minWidth;
            layout.minHeight = templLayout.minHeight;
            layout.flexibleWidth = templLayout.flexibleWidth;
            layout.flexibleHeight = templLayout.flexibleHeight;
        }
        else
        {
            layout.preferredWidth = desiredSize;
            layout.preferredHeight = desiredSize;
            layout.flexibleWidth = 0;
            layout.flexibleHeight = 0;
        }

        // Size the rect transform to match preferred size
        var rt = buttonObject.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(layout.preferredWidth > 0 ? layout.preferredWidth : desiredSize,
                                   layout.preferredHeight > 0 ? layout.preferredHeight : desiredSize);

        // Insert at desired sibling index (so it sits beside the other icons). If insertIndex invalid, put at end.
        try
        {
            if (insertIndex >= 0 && insertIndex <= parentTransform.childCount)
                buttonObject.transform.SetSiblingIndex(insertIndex);
            else
                buttonObject.transform.SetAsLastSibling();

            // Force layout rebuild on the parent so the icon appears in the correct place immediately
            var parentRt = parentTransform as RectTransform ?? sectionsRt;
            LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);

            // Also force canvas update
            Canvas.ForceUpdateCanvases();
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ParentButtonIntoSectionsImpl: layout/index update failed: " + ex);
            try { buttonObject.transform.SetAsLastSibling(); } catch { }
        }

        // Ensure visible
        try { buttonObject.SetActive(true); } catch { }

        TravelButtonPlugin.LogInfo("ParentButtonIntoSectionsImpl: button parented under '" + (buttonObject.transform.parent != null ? buttonObject.transform.parent.name : "null") + "'");
    }

    private Canvas[] FindAllCanvasesSafe()
    {
        // 1) Try the simple generic API (no includeInactive parameter).
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (canvases != null && canvases.Length > 0)
                return canvases;
        }
        catch
        {
            // ignore and try fallback
        }

        // 2) Fallback to non-generic FindObjectsOfType(Type).
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType(typeof(Canvas));
            if (arr != null)
                return arr.Cast<Canvas>().Where(c => c != null).ToArray();
        }
        catch
        {
            // ignore and try final fallback
        }

        // 3) Final fallback: Resources.FindObjectsOfTypeAll (includes inactive and assets) — filter to scene objects.
        try
        {
            var arr2 = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(Canvas))
                        .Cast<Canvas>()
                        .Where(c => c != null && c.gameObject != null && c.gameObject.scene.IsValid())
                        .ToArray();
            if (arr2 != null && arr2.Length > 0)
                return arr2;
        }
        catch
        {
            // ignore
        }

        // Nothing found
        return new Canvas[0];
    }

    // Coroutine: wait until Sections appears (inventory opened), then parent/activate the button.
    // Improved coroutine: search all Canvases every poll and wait longer
    // Improved coroutine: search all canvases each poll and wait longer
    private IEnumerator PlaceOnToolbarWhenAvailable(Canvas startCanvas, float timeoutSeconds = 12f)
    {
        if (buttonObject != null) buttonObject.SetActive(false);
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        var wait = new WaitForSeconds(0.25f);

        while (Time.realtimeSinceStartup < deadline)
        {
            // search all canvases each loop (some UIs live under different canvases)
            var canvases = FindAllCanvasesSafeImpl(); // call your safe helper
            RectTransform foundSections = null;
            Canvas foundCanvas = null;

            foreach (var c in canvases)
            {
                if (c == null) continue;
                try
                {
                    var candidate = FindSectionsGroup(c); // your heuristic finder
                    if (candidate != null && candidate.gameObject.activeInHierarchy)
                    {
                        foundSections = candidate;
                        foundCanvas = c;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("PlaceOnToolbarWhenAvailable: FindSectionsGroup threw for canvas " + (c != null ? c.name : "null") + ": " + ex);
                }
            }

            if (foundSections != null)
            {
                try
                {
                    TravelButtonPlugin.LogInfo($"PlaceOnToolbarWhenAvailable: found Sections '{foundSections.name}' under Canvas '{(foundCanvas != null ? foundCanvas.name : "null")}' - parenting.");
                    ParentButtonIntoSectionsImpl(foundSections, Mathf.Max(32f, buttonObject.GetComponent<RectTransform>().sizeDelta.x));

                    // bring to front and ensure layout updated
                    try
                    {
                        var parentCanvas = buttonObject.GetComponentInParent<Canvas>();
                        if (parentCanvas != null) parentCanvas.sortingOrder = Math.Max(parentCanvas.sortingOrder, 3000);
                        buttonObject.transform.SetAsLastSibling();
                        Canvas.ForceUpdateCanvases();
                    }
                    catch { }

                    buttonObject.SetActive(true);
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("PlaceOnToolbarWhenAvailable: ParentButtonIntoSectionsImpl failed: " + ex);
                    ForceTopToolbarPlacementImpl(foundCanvas ?? startCanvas);
                    if (buttonObject != null) buttonObject.SetActive(true);
                }
                yield break;
            }

            yield return wait;
        }

        TravelButtonPlugin.LogInfo("PlaceOnToolbarWhenAvailable: timeout waiting for Sections; using fallback placement.");
        try
        {
            ForceTopToolbarPlacementImpl(startCanvas);
            if (buttonObject != null) buttonObject.SetActive(true);
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("PlaceOnToolbarWhenAvailable: ForceTopToolbarPlacementImpl failed: " + ex);
        }
    }

    // Fallback approximate placement on the top toolbar area (canvas-local conversion)
    private void ForceTopToolbarPlacementImpl(Canvas canvas)
    {
        if (buttonObject == null || canvas == null) return;
        try
        {
            var rt = buttonObject.GetComponent<RectTransform>();
            Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransform canvasRt = canvas.GetComponent<RectTransform>();
            Vector2 screenPoint = new Vector2(Screen.width * 0.5f + 140f, Screen.height - 60f);
            Vector2 localPoint;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, screenPoint, cam, out localPoint))
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = localPoint;
                buttonObject.SetActive(true);
                TravelButtonPlugin.LogInfo("ForceTopToolbarPlacementImpl: placed fallback at " + localPoint);
            }
            else
            {
                TravelButtonPlugin.LogWarning("ForceTopToolbarPlacementImpl: Screen->Local conversion failed, leaving default transform.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("ForceTopToolbarPlacementImpl: " + ex);
        }
    }

    // Persistent monitor: ensure the button remains parented to Sections while the game runs.
    private IEnumerator MonitorAndMaintainButtonParentImpl()
    {
        var waitShort = new WaitForSeconds(0.5f);
        var waitLong = new WaitForSeconds(0.75f);

        while (true)
        {
            if (buttonObject == null) yield break;

            var canvas = FindCanvas();
            if (canvas == null)
            {
                yield return waitShort;
                continue;
            }

            try
            {
                // perform guarded work inside TryMaintainParent (no yields there)
                TryMaintainParent(canvas);
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("MonitorAndMaintainButtonParentImpl: TryMaintainParent threw: " + ex);
            }

            yield return waitLong;
        }
    }

    // Helper that performs the guarded parent-check/reparent logic without yielding.
    private bool TryMaintainParent(Canvas canvas)
    {
        if (buttonObject == null || canvas == null) return false;
        try
        {
            RectTransform sections = null;
            try { sections = FindSectionsGroup(canvas); } catch (Exception ex) { TravelButtonPlugin.LogWarning("TryMaintainParent: FindSectionsGroup threw: " + ex); sections = null; }

            if (sections == null) return false;

            Transform currentParent = null;
            try { currentParent = buttonObject.transform.parent; } catch (Exception ex) { TravelButtonPlugin.LogWarning("TryMaintainParent: could not get current parent: " + ex); currentParent = null; }

            bool needsReparent = (currentParent == null) || !IsTransformOrAncestorImpl(currentParent, sections);

            if (!needsReparent) return false;

            try
            {
                ParentButtonIntoSectionsImpl(sections, Mathf.Max(32f, buttonObject.GetComponent<RectTransform>().sizeDelta.x));
                TravelButtonPlugin.LogInfo("TryMaintainParent: reparented button into Sections.");
                return true;
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("TryMaintainParent: reparent attempt failed: " + ex);
                return false;
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryMaintainParent: unexpected exception: " + ex);
            return false;
        }
    }

    // Helper: returns true if candidate is equal to ancestor or is a child under ancestor
    private bool IsTransformOrAncestorImpl(Transform candidate, Transform ancestor)
    {
        if (candidate == null || ancestor == null) return false;
        var cur = candidate;
        while (cur != null)
        {
            if (cur == ancestor) return true;
            cur = cur.parent;
        }
        return false;
    }

    // Helper to build readable transform path (kept from earlier diagnostics)
    private string GetTransformPath(Transform t)
    {
        if (t == null) return "";
        string path = t.name;
        var cur = t.parent;
        while (cur != null)
        {
            path = cur.name + "/" + path;
            cur = cur.parent;
        }
        return path;
    }

    // Unity-version-safe helper to find scene Canvas objects.
    // Place this inside your TravelButtonUI partial class.
    // Requires: using System.Linq; using UnityEngine;
    private Canvas[] FindAllCanvasesSafeImpl()
    {
        // 1) Try the common generic API (may only return active canvases on some Unity versions)
        try
        {
            var canvases1 = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (canvases1 != null && canvases1.Length > 0)
                return canvases1;
        }
        catch
        {
            // ignore and try fallbacks
        }

        // 2) Fallback to non-generic FindObjectsOfType(Type)
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType(typeof(Canvas));
            if (arr != null && arr.Length > 0)
                return arr.Cast<Canvas>().Where(c => c != null).ToArray();
        }
        catch
        {
            // ignore and try final fallback
        }

        // 3) Final fallback: Resources.FindObjectsOfTypeAll (includes inactive & assets) - filter to scene instances
        try
        {
            var arr2 = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(Canvas))
                        .Cast<Canvas>()
                        .Where(c => c != null && c.gameObject != null && c.gameObject.scene.IsValid())
                        .ToArray();
            if (arr2 != null && arr2.Length > 0)
                return arr2;
        }
        catch
        {
            // ignore
        }

        // Nothing found
        return new Canvas[0];
    }

    // Safe finder for RectTransform scene instances (place inside TravelButtonUI)
    private RectTransform[] FindAllRectTransformsSafeImpl()
    {
        try
        {
            var rts = UnityEngine.Object.FindObjectsOfType<RectTransform>();
            if (rts != null && rts.Length > 0) return rts;
        }
        catch { }

        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType(typeof(RectTransform));
            if (arr != null && arr.Length > 0)
                return arr.Cast<RectTransform>().Where(r => r != null).ToArray();
        }
        catch { }

        try
        {
            var arr2 = UnityEngine.Resources.FindObjectsOfTypeAll(typeof(RectTransform))
                        .Cast<RectTransform>()
                        .Where(r => r != null && r.gameObject != null && r.gameObject.scene.IsValid())
                        .ToArray();
            if (arr2 != null && arr2.Length > 0) return arr2;
        }
        catch { }

        return new RectTransform[0];
    }

    // Diagnostic: run while the inventory is open and paste the resulting log lines here
    // Corrected DebugLogToolbarCandidates — uses RectTransform list for the candidate scan
    private void DebugLogToolbarCandidates()
    {
        try
        {
            // Log canvases (existing helper)
            var canvases = FindAllCanvasesSafeImpl();
            TravelButtonPlugin.LogInfo($"DebugLogToolbarCandidates: canvases found = {canvases.Length}");
            foreach (var c in canvases)
            {
                TravelButtonPlugin.LogInfo($" Canvas '{c.name}' renderMode={c.renderMode} scale={c.scaleFactor} worldCamera={(c.worldCamera != null ? c.worldCamera.name : "null")}");
            }

            // Inspect RectTransforms across scene for likely toolbar candidates
            var allRts = FindAllRectTransformsSafeImpl(); // <-- important: RectTransform array, not Canvas array
            for (int i = 0; i < allRts.Length; i++)
            {
                var rt = allRts[i];
                if (rt == null) continue;
                string nm = rt.name ?? "";
                if (nm.IndexOf("Sections", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("TopPanel", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("btnInventory", StringComparison.OrdinalIgnoreCase) >= 0
                    || nm.IndexOf("CharacterMenus", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var btns = rt.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                    string worldTopY = "N/A";
                    try
                    {
                        Vector3[] corners = new Vector3[4];
                        rt.GetWorldCorners(corners);
                        float topY = (corners[1].y + corners[2].y) * 0.5f;
                        worldTopY = topY.ToString("F1");
                    }
                    catch { }
                    TravelButtonPlugin.LogInfo($"Candidate [{i}] '{rt.name}' active={rt.gameObject.activeInHierarchy} btnCount={(btns != null ? btns.Length : 0)} rect=({rt.rect.width:F0}x{rt.rect.height:F0}) worldTopY={worldTopY} path={GetTransformPath(rt)}");
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("DebugLogToolbarCandidates: " + ex);
        }
    }

    private void OpenTravelDialog()
    {
        TravelButtonPlugin.LogInfo("OpenTravelDialog: invoked via click or keyboard.");
        //DumpDetectedPositionsForActiveScene();
        //        LogLoadedScenes();
        //DebugLogCanvasHierarchy();
        DebugLogToolbarCandidates();

        try
        {
            // Auto-assign scene names and log anchors (best-effort diagnostic)
            try
            {
                if (dialogRoot == null)
                    TravelButtonMod.AutoAssignSceneNamesFromLoadedScenes();
//                TravelButtonMod.LogLoadedScenesAndRootObjects();
//                TravelButtonMod.LogCityAnchorsFromLoadedScenes();
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("OpenTravelDialog: auto-scan for anchors failed: " + ex.Message);
            }

            // Stop any previous refresh coroutine
            if (refreshButtonsCoroutine != null)
            {
                try { StopCoroutine(refreshButtonsCoroutine); } catch { }
                refreshButtonsCoroutine = null;
            }

            // If dialog already exists, just re-activate and restart refresh
            if (dialogRoot != null)
            {
                dialogRoot.SetActive(true);
                var canvas = dialogCanvas != null ? dialogCanvas.GetComponent<Canvas>() : dialogRoot.GetComponentInParent<Canvas>();
                if (canvas != null) canvas.sortingOrder = 2000;
                dialogRoot.transform.SetAsLastSibling();
                TravelButtonPlugin.LogInfo("OpenTravelDialog: re-activated existing dialogRoot.");
                // prevent click-through for a frame when reactivating
                StartCoroutine(TemporarilyDisableDialogRaycasts());
                // start refreshing buttons while open
                refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));
                // record open time for grace-window logic in refresh
                dialogOpenedTime = Time.time;
                return;
            }

            // Create (or reuse) top-level dialog canvas
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
            } else
            {
                TravelButtonPlugin.LogInfo("OpenTravelDialog: (dialogCanvas == null).");
            }

            // Root
            dialogRoot = new GameObject("TravelDialog");
            dialogRoot.transform.SetParent(dialogCanvas.transform, false);
            dialogRoot.transform.SetAsLastSibling();
            dialogRoot.AddComponent<CanvasRenderer>();
            var rootRt = dialogRoot.AddComponent<RectTransform>();

            TravelButtonPlugin.LogInfo("OpenTravelDialog: (dialogCanvas == null).");

            // center the dialog explicitly
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.localScale = Vector3.one;
            rootRt.sizeDelta = new Vector2(520, 360);
            rootRt.anchoredPosition = Vector2.zero;

            var bg = dialogRoot.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.95f);

            TravelButtonPlugin.LogInfo("OpenTravelDialog: (dialogCanvas == null).");

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

            TravelButtonPlugin.LogInfo("OpenTravelDialog: nastavuju hodnoty 1.");

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

            TravelButtonPlugin.LogInfo("OpenTravelDialog: nastavuju hodnoty 2.");
            // ScrollRect + viewport for city list
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

            TravelButtonPlugin.LogInfo("OpenTravelDialog: nastavuju hodnoty 3.");
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

            TravelButtonPlugin.LogInfo("OpenTravelDialog: nastavuju hodnoty 4.");
            // Content container
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            var contentRt = content.AddComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0, 0);

            TravelButtonPlugin.LogInfo("OpenTravelDialog: nastavuju hodnoty 5.");
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

            // --- Populate items ---
            TravelButtonPlugin.LogInfo($"OpenTravelDialog: TravelButtonMod.Cities.Count = {(TravelButtonMod.Cities == null ? 0 : TravelButtonMod.Cities.Count)}");
            bool anyCity = false;

            // read player money once per dialog opening
            long playerMoney = GetPlayerCurrencyAmountOrMinusOne();
            TravelButtonPlugin.LogWarning($"OpenTravelDialog: hrac ma '{playerMoney}'.");

            if (TravelButtonMod.Cities == null || TravelButtonMod.Cities.Count == 0)
            {
                TravelButtonPlugin.LogWarning("OpenTravelDialog: No cities configured (TravelButtonMod.Cities empty).");
            }
            else
            {
                foreach (var city in TravelButtonMod.Cities)
                {
                    anyCity = true;

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

                    // determine per-city cost
                    int cost = TravelButtonMod.cfgTravelCost.Value;
                    TravelButtonPlugin.LogWarning($"OpenTravelDialog: hrac ma '{playerMoney}'. cost= '{cost}'");

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
                    catch { /* ignore reflection issues; fallback to global */ }

                    // price label right
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

                    // config flag
                    bool enabledByConfig = TravelButtonMod.IsCityEnabled(city.name);

                    // visited check (robust)
                    bool visited = false;
                    try { visited = IsCityVisitedFallback(city); } catch { visited = false; }

                    // coords available?
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
                    catch { coordsAvailable = false; }

                    // player money for initial display (treat unknown as permissive)
                    bool playerMoneyKnown = playerMoney >= 0;
                    TravelButtonPlugin.LogInfo($"OpenTravelDialog: computing hasEnoughMoney='{playerMoneyKnown}', playerMoney='{playerMoney}', hasEnoughMoney='{cost}'");
                    bool hasEnoughMoney = !playerMoneyKnown || (playerMoney >= cost);

                    // scene-aware coords allowance
                    bool targetSceneSpecified = !string.IsNullOrEmpty(city.sceneName);
                    var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    bool sceneMatches = !targetSceneSpecified || string.Equals(city.sceneName, activeScene.name, StringComparison.OrdinalIgnoreCase);
                    bool allowWithoutCoords = targetSceneSpecified && !sceneMatches;

                    // New rule for initial interactability
                    // ONZA
                    bool initialInteractable = enabledByConfig && visited && (coordsAvailable || allowWithoutCoords) && hasEnoughMoney;

                    bbtn.interactable = initialInteractable;
                    if (!initialInteractable)
                    {
                        bimg.color = new Color(0.18f, 0.18f, 0.18f, 1f);
                    }

                    TravelButtonPlugin.LogInfo($"OpenTravelDialog: created UI button for '{city.name}' (interactable={bbtn.interactable}, enabledByConfig={enabledByConfig}, visited={visited}, coordsAvailable={coordsAvailable}, allowWithoutCoords={allowWithoutCoords}, hasEnoughMoney={hasEnoughMoney}, playerMoney={playerMoney}, price={cost}, targetGameObjectName='{city.targetGameObjectName}', sceneName='{city.sceneName}')");

                    var capturedCity = city;

                    // Click handler: re-check config, visited and funds immediately before attempting teleport
                    bbtn.onClick.AddListener(() =>
                    {
                        try
                        {
                            if (isTeleporting)
                            {
                                TravelButtonPlugin.LogInfo("City button click ignored: teleport already in progress.");
                                return;
                            }

                            bool cfgEnabled = TravelButtonMod.IsCityEnabled(capturedCity.name);
                            bool visitedNow = false;
                            try { visitedNow = IsCityVisitedFallback(capturedCity); } catch { visitedNow = false; }
                            long pm = GetPlayerCurrencyAmountOrMinusOne();

                            TravelButtonPlugin.LogInfo($"City click: '{capturedCity.name}' cfgEnabled={cfgEnabled}, visitedNow={visitedNow}, playerMoney={pm}");

                            if (!cfgEnabled)
                            {
                                ShowInlineDialogMessage("Destination disabled by config");
                                return;
                            }

                            if (!visitedNow)
                            {
                                ShowInlineDialogMessage("Destination not discovered yet");
                                return;
                            }

                            // Money check (strict on click)
                            if (pm < 0)
                            {
                                ShowInlineDialogMessage("Could not determine your currency amount; travel blocked");
                                return;
                            }
                            if (!CurrencyHelpers.AttemptDeductSilverDirect(cost, true))
                            {
                                ShowInlineDialogMessage("not enough resources to travel");
                                return;
                            }
                            // disable all city buttons while teleporting
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

                            TryTeleportThenCharge(capturedCity, cost);
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning("City button click handler exception: " + ex);
                        }
                    });
                }
            }

            if (!anyCity)
            {
                TravelButtonPlugin.LogWarning("OpenTravelDialog: no enabled cities were added to the dialog - adding debug placeholders.");
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

            // Layout and refresh
            StartCoroutine(FinishDialogLayoutAndShow(scrollRect, viewport.GetComponent<RectTransform>(), contentRt));
            refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));
            dialogOpenedTime = Time.time;

            // Close button
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
                    TravelButtonPlugin.LogError("Close button click failed: " + ex);
                }
            });

            // Prevent immediate click-through
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

            // 1) Determine coords/anchor availability
            Vector3 coordsHint = Vector3.zero;
            bool haveCoordsHint = false;
            bool haveTargetGameObject = false;
            bool targetGameObjectFound = false;

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
                        haveCoordsHint = true;
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

            if (!haveCoordsHint)
            {
                if (city.coords != null && city.coords.Length >= 3)
                {
                    coordsHint = new Vector3(city.coords[0], city.coords[1], city.coords[2]);
                    // ONZA
/*                    var cd = UnityEngine.Object.FindObjectOfType<CityDiscovery>();
                    Vector3? posNullable = cd.GetCityPosition(city);
                    coordsHint = posNullable.Value;
*/
                    haveCoordsHint = true;
                    TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: using explicit coords from config for {city.name}: {coordsHint}");
                    if (!IsCoordsReasonable(coordsHint))
                    {
                        TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: explicit coords {coordsHint} look suspicious for city '{city.name}'. Verify travel_config.json contains correct world coords.");
                    }
                }
            }

            // 2) If sceneName not provided, try to guess it from build settings BEFORE deciding immediate vs load
            if (string.IsNullOrEmpty(city.sceneName))
            {
                try
                {
                    var guessed = GuessSceneNameFromBuildSettings(city.name);
                    if (!string.IsNullOrEmpty(guessed))
                    {
                        TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: guessed sceneName='{guessed}' from build settings for city '{city.name}'");
                        city.sceneName = guessed; // in-memory assignment only
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("TryTeleportThenCharge: GuessSceneNameFromBuildSettings failed: " + ex);
                }
            }

            TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: city='{city.name}', haveTargetGameObject={haveTargetGameObject}, targetGameObjectFound={targetGameObjectFound}, haveCoordsHint={haveCoordsHint}, sceneName='{city.sceneName}'");

            // 3) Decide whether target scene is specified and whether it matches active scene
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool targetSceneSpecified = !string.IsNullOrEmpty(city.sceneName);
            bool sceneMatches = !targetSceneSpecified || string.Equals(city.sceneName, activeScene.name, StringComparison.OrdinalIgnoreCase);

            // 4) FAST PATH: same-scene or unspecified-scene + coords available => immediate teleport
            if (haveCoordsHint && sceneMatches)
            {
                try
                {
                    TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: performing immediate teleport (coords available in active scene). Initial coords: {coordsHint}");
                    Vector3 groundedCoords = TeleportHelpers.GetGroundedPosition(coordsHint);
                    TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: Coords after GetGroundedPosition: {groundedCoords}");
                    bool ok = AttemptTeleportToPositionSafe(groundedCoords);

                    if (ok)
                    {
                        TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: immediate teleport to '{city.name}' completed successfully.");
                        try { TravelButtonMod.OnSuccessfulTeleport(city.name); } catch { }

                        try
                        {
                            bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
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

                        try { TravelButtonMod.PersistCitiesToConfig(); } catch { }

                        try
                        {
                            isTeleporting = false;
                            // ONZA
                            if (dialogRoot != null) dialogRoot.SetActive(false);
                            if (refreshButtonsCoroutine != null)
                            {
                                StopCoroutine(refreshButtonsCoroutine);
                                refreshButtonsCoroutine = null;
                            }
                        }
                        catch { }
                        return;
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: immediate teleport to '{city.name}' failed - will try loading the correct scene or helper fallback.");
                        // continue to fallback below
                    }
                }
                catch (Exception exImmediate)
                {
                    TravelButtonPlugin.LogWarning("TryTeleportThenCharge: immediate teleport attempt exception: " + exImmediate);
                    // fallthrough to fallback
                }
            }

            // 5) If a target scene is specified and it differs from active, load it and teleport there
            if (targetSceneSpecified && !sceneMatches)
            {
                TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: target scene '{city.sceneName}' differs from active '{activeScene.name}' - loading scene then teleporting.");
                try
                {
                    StartCoroutine(LoadSceneAndTeleportCoroutine(city, cost, coordsHint, haveCoordsHint));
                    return;
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("TryTeleportThenCharge: failed to start LoadSceneAndTeleportCoroutine: " + ex);
                    // fall back to helper below
                }
            }

            // 6) Fallback: use existing TeleportHelpersBehaviour coroutine (keeps previous robust behavior)
            try
            {
                TeleportHelpersBehaviour helper = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
                if (helper == null)
                {
                    var go = new GameObject("TeleportHelpersHost");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    helper = go.AddComponent<TeleportHelpersBehaviour>();
                }

                helper.StartCoroutine(helper.EnsureSceneAndTeleport(city, coordsHint, haveCoordsHint, success =>
                {
                    if (success)
                    {
                        TravelButtonPlugin.LogInfo($"TryTeleportThenCharge: teleport to '{city.name}' completed successfully (helper).");
                        try { TravelButtonMod.OnSuccessfulTeleport(city.name); } catch { }

                        try
                        {
                            bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
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

                        try { TravelButtonMod.PersistCitiesToConfig(); } catch { }

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
                        TravelButtonPlugin.LogWarning($"TryTeleportThenCharge: teleport to '{city.name}' failed (helper).");
                        ShowInlineDialogMessage("Teleport failed");
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
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TryTeleportThenCharge exception: " + ex);
            isTeleporting = false;
        }
    }

    // Coroutine to load a target scene (map) and teleport the player there.
    // This version avoids yielding inside try/catch blocks (C# restriction).
    //podle souradnic

    // Replace the existing LoadSceneAndTeleportCoroutine with the version below.
    // Added robust fallback grounding logic (TryFindSafePosition) that attempts:
    //  - TeleportHelpers.GetGroundedPosition (existing)
    //  - A downward Physics.Raycast from a high altitude
    //  - A spiral search of nearby XZ samples with downward raycasts
    // This increases the chance of landing on valid ground when the configured coords are inaccurate.

    private IEnumerator LoadSceneAndTeleportCoroutine(TravelButtonMod.City city, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        if (city == null)
        {
            isTeleporting = false;
            yield break;
        }

        // display inline message to inform user
        ShowInlineDialogMessage("Loading map...");

        AsyncOperation op = null;
        bool loadFailed = false;

        // Start the async load - keep try/catch that does not contain any yields
        try
        {
            TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: starting async load for scene '{city.sceneName}'.");
            op = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(city.sceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
            if (op == null)
            {
                TravelButtonPlugin.LogWarning($"LoadSceneAndTeleportCoroutine: LoadSceneAsync returned null for '{city.sceneName}'.");
                loadFailed = true;
            }
        }
        catch (Exception exLoad)
        {
            TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine: exception while initiating scene load: " + exLoad);
            loadFailed = true;
        }

        // If we successfully obtained an AsyncOperation, wait for it (yields are not inside a try/catch here)
        if (!loadFailed && op != null)
        {
            while (!op.isDone)
            {
                yield return null;
            }

            // Give a couple frames for scene initialization
            yield return null;
            yield return null;
        }

        TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: after test op != null.");

        if (loadFailed)
        {
            ShowInlineDialogMessage("Map load failed");
            isTeleporting = false;
            // re-enable buttons
            try
            {
                var contentParent = dialogRoot?.transform.Find("ScrollArea/Viewport/Content");
                if (contentParent != null)
                {
                    for (int ci = 0; ci < contentParent.childCount; ci++)
                    {
                        var childBtn = contentParent.GetChild(ci).GetComponent<Button>();
                        if (childBtn != null) childBtn.interactable = true;
                    }
                }
            }
            catch { }
            yield break;
        }

        TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: after test loadFailed.");

        // Resolve final desired position as before (prefer city.coords, then coordsHint, then named GameObject, then heuristic)
        Vector3 finalPos = Vector3.zero;
        bool haveFinalPos = false;

        try
        {
            // 1) Prefer configured coords in the city entry
            if (city.coords != null && city.coords.Length >= 3)
            {
                finalPos = new Vector3(city.coords[0], city.coords[1], city.coords[2]);
                haveFinalPos = true;
                TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: using configured city.coords for '{city.name}' -> {finalPos}");
            }

            // 2) If no city.coords, fall back to coordsHint passed by caller (if present)
            if (!haveFinalPos && haveCoordsHint)
            {
                finalPos = coordsHint;
                haveFinalPos = true;
                TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: using coordsHint for '{city.name}' -> {finalPos}");
            }

            // 3) If still no final pos, try to find a target GameObject by name (legacy fallback)
            if (!haveFinalPos && !string.IsNullOrEmpty(city.targetGameObjectName))
            {
                try
                {
                    var tgo = GameObject.Find(city.targetGameObjectName);
                    if (tgo != null)
                    {
                        finalPos = tgo.transform.position;
                        haveFinalPos = true;
                        TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: found target GameObject '{city.targetGameObjectName}' at {finalPos} after load (fallback).");
                    }
                    else
                    {
                        TravelButtonPlugin.LogWarning($"LoadSceneAndTeleportCoroutine: target GameObject '{city.targetGameObjectName}' not found after scene load (fallback).");
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine: error searching for target GameObject after load: " + ex);
                }
            }

            // 4) Final heuristic: look for any transform whose name contains the city name
            if (!haveFinalPos)
            {
                try
                {
                    var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                    foreach (var tr in allTransforms)
                    {
                        if (tr == null) continue;
                        if (!string.IsNullOrEmpty(tr.name) && !string.IsNullOrEmpty(city.name) &&
                            tr.name.IndexOf(city.name, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            finalPos = tr.position;
                            haveFinalPos = true;
                            TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: heuristic found scene object '{tr.name}' for city '{city.name}' at {finalPos}");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine: heuristic search failed: " + ex);
                }
            }
        }
        catch (Exception exOuter)
        {
            TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine: unexpected error while resolving final position: " + exOuter);
        }

        TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: after resolving finalPos; haveFinalPos={haveFinalPos}");

        if (!haveFinalPos)
        {
            TravelButtonPlugin.LogError($"LoadSceneAndTeleportCoroutine: could not determine a teleport target for '{city.name}' after loading scene '{city.sceneName}'.");
            ShowInlineDialogMessage("Teleport target not found in map");
            isTeleporting = false;
            yield break;
        }

        TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: before teleporting to {finalPos}");

        // Enhanced grounding/fallback procedure:
        // Try a best-effort to find a safe grounded point close to finalPos.
        bool foundSafe = TryFindSafePosition(finalPos, out Vector3 safePos);

        // If we couldn't find a good ground at the chosen coords, but the coords were user-provided (city.coords),
        // try swapping Y/Z interpretation as a last resort (handles mis-ordered coords).
        if (!foundSafe && city.coords != null && city.coords.Length >= 3)
        {
            var swapped = new Vector3(city.coords[0], city.coords[2], city.coords[1]);
            TravelButtonPlugin.LogWarning($"LoadSceneAndTeleportCoroutine: primary grounding failed for '{city.name}', trying swapped coords interpretation {swapped}.");
            foundSafe = TryFindSafePosition(swapped, out safePos);

            if (foundSafe)
            {
                TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: swapped coords interpretation succeeded for '{city.name}' -> {safePos}. Updating in-memory coords.");
                try { city.coords = new float[] { safePos.x, safePos.y, safePos.z }; } catch { }
            }
        }

        if (!foundSafe)
        {
            TravelButtonPlugin.LogError($"LoadSceneAndTeleportCoroutine: could not find a safe ground position near {finalPos} for '{city.name}' after attempts.");
            ShowInlineDialogMessage("Could not find safe teleport position");
            isTeleporting = false;
            yield break;
        }

        TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: teleporting to safe position {safePos}");

        // Do the actual teleport
        bool teleported = false;
        try
        {
            teleported = AttemptTeleportToPositionSafe(safePos);
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine: AttemptTeleportToPositionSafe threw: " + ex);
            teleported = false;
        }

        if (teleported)
        {
            TravelButtonPlugin.LogInfo($"LoadSceneAndTeleportCoroutine: teleported player to '{city.name}' at {safePos}.");

            try { TravelButtonMod.OnSuccessfulTeleport(city.name); } catch { }

            try
            {
                bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
                if (!charged)
                {
                    TravelButtonPlugin.LogWarning($"LoadSceneAndTeleportCoroutine: Teleported to {city.name} but failed to deduct {cost} silver.");
                    ShowInlineDialogMessage($"Teleported to {city.name} (failed to charge {cost} {TravelButtonMod.cfgCurrencyItem.Value})");
                }
                else
                {
                    ShowInlineDialogMessage($"Teleported to {city.name}");
                }
            }
            catch (Exception exCharge)
            {
                TravelButtonPlugin.LogWarning("LoadSceneAndTeleportCoroutine: charge attempt threw: " + exCharge);
                ShowInlineDialogMessage($"Teleported to {city.name} (charge error)");
            }

            try { TravelButtonMod.PersistCitiesToConfig(); } catch { }

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
            TravelButtonPlugin.LogWarning($"LoadSceneAndTeleportCoroutine: teleport to '{city.name}' failed after scene load.");
            ShowInlineDialogMessage("Teleport failed");
            isTeleporting = false;

            // Re-enable buttons
            try
            {
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
            catch { }
        }

        yield break;
    }

    /// <summary>
    /// Try to find a safe grounded position near 'pos'.
    /// Strategy:
    ///  - use TeleportHelpers.GetGroundedPosition(pos) (may use game-specific grounding)
    ///  - if that fails, do a downward Physics.Raycast from high above pos
    ///  - if that fails, perform a spiral search on XZ plane, raycasting down at each candidate
    /// Returns true and sets 'outPos' when a candidate ground is found.
    /// </summary>
    private bool TryFindSafePosition(Vector3 pos, out Vector3 outPos)
    {
        outPos = Vector3.zero;
        try
        {
            // 1) TeleportHelpers grounding (existing helper)
            try
            {
                var gp = TeleportHelpers.GetGroundedPosition(pos);
                // treat any returned value as candidate; but verify ground by a short downward raycast if possible
                // We'll accept gp if a raycast from gp+0.5 down hits within small distance or gp.y is not 0 with reasonable check.
                RaycastHit hit;
                Vector3 rayStart = gp + Vector3.up * 0.5f;
                if (Physics.Raycast(rayStart, Vector3.down, out hit, 5f, Physics.DefaultRaycastLayers))
                {
                    outPos = hit.point;
                    TravelButtonPlugin.LogInfo($"TryFindSafePosition: TeleportHelpers returned grounded pos {outPos} (via raycast verification).");
                    return true;
                }
                else
                {
                    // Accept gp if it's not exactly zero and not obviously invalid
                    if (!Mathf.Approximately(gp.sqrMagnitude, 0f))
                    {
                        outPos = gp;
                        TravelButtonPlugin.LogInfo($"TryFindSafePosition: TeleportHelpers returned pos {gp} (no raycast hit but accepting).");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogWarning("TryFindSafePosition: TeleportHelpers.GetGroundedPosition threw: " + ex);
            }

            // 2) Downward raycast from high above pos
            {
                float startY = pos.y + 50f;
                Vector3 start = new Vector3(pos.x, startY, pos.z);
                RaycastHit hit;
                if (Physics.Raycast(start, Vector3.down, out hit, 200f, Physics.DefaultRaycastLayers))
                {
                    outPos = hit.point;
                    TravelButtonPlugin.LogInfo($"TryFindSafePosition: downward raycast hit at {outPos} (startY={startY}).");
                    return true;
                }
            }

            // 3) Spiral search on XZ plane
            int maxRadius = 20; // meters
            float step = 1.0f; // meter steps
            for (int r = 1; r <= maxRadius; r++)
            {
                // sample a full circle at this radius with a few steps
                int steps = Mathf.Max(8, (int)(6 * r));
                for (int s = 0; s < steps; s++)
                {
                    float angle = (s / (float)steps) * Mathf.PI * 2f;
                    var candidate = new Vector3(pos.x + Mathf.Cos(angle) * r, pos.y + 50f, pos.z + Mathf.Sin(angle) * r);
                    RaycastHit hit;
                    if (Physics.Raycast(candidate, Vector3.down, out hit, 200f, Physics.DefaultRaycastLayers))
                    {
                        outPos = hit.point;
                        TravelButtonPlugin.LogInfo($"TryFindSafePosition: spiral raycast hit at {outPos} (radius={r}, step={s}).");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("TryFindSafePosition unexpected error: " + ex);
        }

        // Give up
        return false;
    }

    /// <summary>
    /// Heuristic to decide whether a GameObject is part of UI (RectTransform, Canvas parent, or UI layer).
    /// This keeps TravelButtonUI self-contained and avoids referencing TravelButtonMod.IsUiGameObject.
    /// </summary>
    private static bool IsUiGameObject(GameObject go)
    {
        if (go == null) return false;
        try
        {
            // RectTransform indicates a UI element
            if (go.GetComponent<RectTransform>() != null) return true;

            // If any parent has a Canvas, treat as UI
            if (go.GetComponentInParent<Canvas>() != null) return true;

            // If there's a layer named "UI" and the object uses it, treat as UI
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1 && go.layer == uiLayer) return true;
        }
        catch
        {
            // On error, be conservative and consider it non-UI
        }
        return false;
    }

    private void DumpDetectedPositionsForActiveScene()
    {
        try
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                TravelButtonPlugin.LogInfo("DumpDetectedPositionsForActiveScene: active scene invalid, skipping.");
                return;
            }

            if (TravelButtonMod.Cities == null || TravelButtonMod.Cities.Count == 0)
            {
                TravelButtonPlugin.LogInfo("DumpDetectedPositionsForActiveScene: no cities configured, skipping.");
                return;
            }

            TravelButtonPlugin.LogInfo($"DumpDetectedPositionsForActiveScene: scanning scene '{scene.name}' for candidate anchors...");

            // Prepare map of cityName -> list of positions
            var detected = new Dictionary<string, List<Vector3>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in TravelButtonMod.Cities)
                detected[c.name] = new List<Vector3>();

            // Find all transforms in loaded scenes (includes inactive)
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();

            foreach (var tr in allTransforms)
            {
                if (tr == null) continue;
                var go = tr.gameObject;
                if (go == null) continue;

                // Skip objects that are not in a loaded scene
                if (!go.scene.IsValid() || !go.scene.isLoaded) continue;

                // Skip UI elements
                if (IsUiGameObject(go)) continue;

                string objName = tr.name ?? "";
                if (string.IsNullOrEmpty(objName)) continue;

                // For each city, check exact targetGameObjectName match, then substring match
                foreach (var city in TravelButtonMod.Cities)
                {
                    try
                    {
                        bool matched = false;
                        if (!string.IsNullOrEmpty(city.targetGameObjectName) &&
                            string.Equals(objName, city.targetGameObjectName, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = true;
                        }
                        else if (!string.IsNullOrEmpty(city.name) &&
                                 objName.IndexOf(city.name, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matched = true;
                        }

                        if (matched)
                        {
                            // Record the world position
                            detected[city.name].Add(tr.position);
                            TravelButtonPlugin.LogInfo($"DumpDetectedPositionsForActiveScene: matched '{objName}' -> city '{city.name}' pos=({tr.position.x:F3},{tr.position.y:F3},{tr.position.z:F3})");
                        }
                    }
                    catch { /* per-city errors ignored */ }
                }
            }

            // Build JSON content
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"scene\":\"").Append(EscapeForJson(scene.name)).Append("\",");
            sb.Append("\"detected\":{");

            bool firstCity = true;
            foreach (var kv in detected)
            {
                var list = kv.Value;
                if (list == null || list.Count == 0) continue;
                if (!firstCity) sb.Append(",");
                firstCity = false;

                sb.Append("\"").Append(EscapeForJson(kv.Key)).Append("\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    sb.Append("[");
                    sb.Append(v.x.ToString("F3", CultureInfo.InvariantCulture)).Append(",");
                    sb.Append(v.y.ToString("F3", CultureInfo.InvariantCulture)).Append(",");
                    sb.Append(v.z.ToString("F3", CultureInfo.InvariantCulture));
                    sb.Append("]");
                    if (i < list.Count - 1) sb.Append(",");
                }
                sb.Append("]");
            }

            sb.Append("}}");

            // Determine output path: prefer BepInEx config folder, fallback to plugin folder
            string outPath = null;
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                var candidate = Path.Combine(baseDir, "BepInEx", "config", "TravelButton_Cities_detected_positions.json");
                outPath = candidate;
            }
            catch { outPath = "TravelButton_Cities_detected_positions.json"; }

            try
            {
                File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
                TravelButtonPlugin.LogInfo($"DumpDetectedPositionsForActiveScene: wrote detected positions for scene '{scene.name}' to '{outPath}'");
            }
            catch (Exception exWrite)
            {
                TravelButtonPlugin.LogWarning("DumpDetectedPositionsForActiveScene: failed to write file: " + exWrite.Message);
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("DumpDetectedPositionsForActiveScene: unexpected error: " + ex);
        }
    }

    private static string EscapeForJson(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string GuessSceneNameFromBuildSettings(string cityName)
    {
        try
        {
            if (string.IsNullOrEmpty(cityName)) return null;
            int count = UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
                    if (string.IsNullOrEmpty(path)) continue;
                    // Use case-insensitive matching against path or file name
                    if (path.IndexOf(cityName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string file = System.IO.Path.GetFileNameWithoutExtension(path);
                        TravelButtonPlugin.LogInfo($"GuessSceneNameFromBuildSettings: matched build-scene '{file}' (path='{path}') for city '{cityName}'.");
                        return file;
                    }

                    // also attempt matching with common suffix/prefix variants
                    // e.g., cityName + "NewTerrain", cityName + "Terrain", cityName + "Map"
                    string[] variants = new[] { cityName + "NewTerrain", cityName + "Terrain", cityName + "Map" };
                    foreach (var v in variants)
                    {
                        if (path.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string file = System.IO.Path.GetFileNameWithoutExtension(path);
                            TravelButtonPlugin.LogInfo($"GuessSceneNameFromBuildSettings: matched variant '{v}' -> build-scene '{file}' for city '{cityName}'.");
                            return file;
                        }
                    }
                }
                catch { /* ignore individual index errors */ }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("GuessSceneNameFromBuildSettings exception: " + ex);
        }
        return null;
    }

    // Helper: more robust visited detection with fallbacks.
    // Returns true if any reasonable indicator suggests the player has visited the city.
    private bool IsCityVisitedFallback(TravelButtonMod.City city)
    {
        try
        {
            if (city == null) return false;

            // Primary: the official visited tracker by city name
            try
            {
                if (VisitedTracker.HasVisited(city.name))
                {
                    TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: VisitedTracker.HasVisited(city.name) => true for '{city.name}'");
                    return true;
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: VisitedTracker.HasVisited(city.name) threw: {ex}");
            }

            // Secondary: try sceneName (some systems mark visited by scene id)
            if (!string.IsNullOrEmpty(city.sceneName))
            {
                try
                {
                    if (VisitedTracker.HasVisited(city.sceneName))
                    {
                        TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: VisitedTracker.HasVisited(sceneName) => true for '{city.sceneName}' (city='{city.name}')");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: VisitedTracker.HasVisited(city.sceneName) threw: {ex}");
                }
            }

            // Tertiary: if a target GameObject is present it's likely the map/anchor is loaded (treat as visited for UI)
            if (!string.IsNullOrEmpty(city.targetGameObjectName))
            {
                try
                {
                    var go = GameObject.Find(city.targetGameObjectName);
                    if (go != null)
                    {
                        TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: target GameObject '{city.targetGameObjectName}' found -> treat '{city.name}' as visited.");
                        return true;
                    }
                }
                catch { /* ignore */ }
            }

            // Last resort heuristic: any transform with city.name substring (helps when anchor names differ)
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var t in all)
                {
                    if (t == null || string.IsNullOrEmpty(t.name)) continue;
                    if (t.name.IndexOf(city.name ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: found scene transform '{t.name}' containing city name '{city.name}' -> treat as visited.");
                        return true;
                    }
                }
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("IsCityVisitedFallback exception: " + ex);
        }

        return false;
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
                    var t = ReflectionUtils.SafeGetType(tname + ", Assembly-CSharp");
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
                    var t = ReflectionUtils.SafeGetType(tn + ", Assembly-CSharp") ?? ReflectionUtils.SafeGetType(tn);
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

    private IEnumerator RefreshCityButtonsWhileOpen(GameObject dialog)
    {
        TravelButtonPlugin.LogDebug("RefreshCityButtonsWhileOpen start");
        while (dialog != null && dialog.activeInHierarchy)
        {
            TravelButtonPlugin.LogDebug("RefreshCityButtonsWhileOpen activeInHierarchy");
            try
            {
                // --- Player position logging (best-effort multi-strategy) ---
                Vector3 playerPos = Vector3.zero;
                bool havePlayerPos = false;
                try
                {
                    // local helper to try several common strategies for locating the local player
                    bool TryGetPlayerPosition(out Vector3 outPos)
                    {
                        outPos = Vector3.zero;
                        try
                        {
                            // 1) Common Unity tag
                            try
                            {
                                var go = GameObject.FindWithTag("Player");
                                if (go != null && go.transform != null)
                                {
                                    outPos = go.transform.position;
                                    return true;
                                }
                            }
                            catch { /* ignore tag errors */ }

                            // 2) Common object name(s)
                            string[] candidateNames = new[] { "Player", "player", "LocalPlayer", "localPlayer", "Character" };
                            foreach (var nm in candidateNames)
                            {
                                try
                                {
                                    var go = GameObject.Find(nm);
                                    if (go != null && go.transform != null)
                                    {
                                        outPos = go.transform.position;
                                        return true;
                                    }
                                }
                                catch { }
                            }

                            // 3) Try to find a game-specific "local player" static (e.g., Player.m_localPlayer or LocalPlayer.Instance)
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    Type playerType = null;
                                    try { playerType = asm.GetTypes().FirstOrDefault(t => t.Name == "Player" || t.Name == "LocalPlayer"); } catch { }
                                    if (playerType == null) continue;

                                    // look for common static fields/properties
                                    var fld = playerType.GetField("m_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                              ?? playerType.GetField("localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                    if (fld != null)
                                    {
                                        var inst = fld.GetValue(null);
                                        if (inst != null)
                                        {
                                            // if instance is a Component or has a 'transform' property
                                            var comp = inst as UnityEngine.Component;
                                            if (comp != null)
                                            {
                                                outPos = comp.transform.position;
                                                return true;
                                            }
                                            // attempt to get a 'transform' property or field via reflection
                                            var tProp = inst.GetType().GetProperty("transform");
                                            if (tProp != null)
                                            {
                                                var t = tProp.GetValue(inst) as Transform;
                                                if (t != null) { outPos = t.position; return true; }
                                            }
                                            var tField = inst.GetType().GetField("transform");
                                            if (tField != null)
                                            {
                                                var t = tField.GetValue(inst) as Transform;
                                                if (t != null) { outPos = t.position; return true; }
                                            }
                                        }
                                    }

                                    var prop = playerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                               ?? playerType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                               ?? playerType.GetProperty("LocalPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                    if (prop != null)
                                    {
                                        var inst = prop.GetValue(null);
                                        if (inst != null)
                                        {
                                            var comp = inst as UnityEngine.Component;
                                            if (comp != null)
                                            {
                                                outPos = comp.transform.position;
                                                return true;
                                            }
                                            var tProp = inst.GetType().GetProperty("transform");
                                            if (tProp != null)
                                            {
                                                var t = tProp.GetValue(inst) as Transform;
                                                if (t != null) { outPos = t.position; return true; }
                                            }
                                            var tField = inst.GetType().GetField("transform");
                                            if (tField != null)
                                            {
                                                var t = tField.GetValue(inst) as Transform;
                                                if (t != null) { outPos = t.position; return true; }
                                            }
                                        }
                                    }
                                }
                                catch { /* ignore assembly/type reflection errors */ }
                            }

                            // 4) Fallback: search all Transforms for likely player-like names (inefficient but safe)
                            try
                            {
                                var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                                foreach (var t in allTransforms)
                                {
                                    if (t == null || string.IsNullOrEmpty(t.name)) continue;
                                    var nmLower = t.name.ToLowerInvariant();
                                    if (nmLower.Contains("player") || nmLower.Contains("local") || nmLower.Contains("character"))
                                    {
                                        outPos = t.position;
                                        return true;
                                    }
                                }
                            }
                            catch { }
                        }
                        catch { }
                        return false;
                    }

                    havePlayerPos = TryGetPlayerPosition(out playerPos);
                }
                catch { havePlayerPos = false; }

                // Log player coordinates (if found) or note that we couldn't locate player
                try
                {
                    if (havePlayerPos)
                    {
                        TravelButtonPlugin.LogInfo($"Player position: ({playerPos.x:F3}, {playerPos.y:F3}, {playerPos.z:F3})");
                    }
                    else
                    {
                        TravelButtonPlugin.LogInfo("Player position: (not found)");
                    }
                }
                catch { /* swallow logging errors */ }

                // fetch current player money (best-effort)
                long currentMoney = GetPlayerCurrencyAmountOrMinusOne();
                bool haveMoneyInfo = currentMoney >= 0;

                // If dialog was just opened, give the game a small grace period to update inventory after a scene load.
                bool enforceMoneyNow = true;
                try
                {
                    enforceMoneyNow = (Time.time - dialogOpenedTime) > 0.15f;
                }
                catch { enforceMoneyNow = true; }

                var content = dialog.transform.Find("ScrollArea/Viewport/Content");
                if (content != null)
                {
                    TravelButtonPlugin.LogDebug("RefreshCityButtonsWhileOpen content found");
                    for (int i = 0; i < content.childCount; i++)
                    {
                        var child = content.GetChild(i);
                        var btn = child.GetComponent<Button>();
                        var img = child.GetComponent<Image>();
                        if (btn == null || img == null) continue;

                        // extract city name from GameObject name "CityButton_<name>"
                        string objName = child.name;
                        if (!objName.StartsWith("CityButton_")) continue;
                        string cityName = objName.Substring("CityButton_".Length);

                        // 1) config flag
                        bool enabledByConfig = TravelButtonMod.IsCityEnabled(cityName);

                        // 2) find the TravelButtonMod.City entry for this city (if any)
                        TravelButtonMod.City foundCity = null;
                        if (TravelButtonMod.Cities != null)
                        {
                            foreach (var c in TravelButtonMod.Cities)
                            {
                                if (string.Equals(c.name, cityName, StringComparison.OrdinalIgnoreCase))
                                {
                                    foundCity = c;
                                    break;
                                }
                            }
                        }

                        // 3) determine per-city cost (fallback to global)
                        int cost = TravelButtonMod.cfgTravelCost.Value;
                        if (foundCity != null)
                        {
                            TravelButtonPlugin.LogDebug("RefreshCityButtonsWhileOpen foundCity found");

                            try
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
                            catch { /* ignore reflection issues; fallback to global */ }
                        }

                        // 4) coords/anchor availability (configuration check only)
                        bool coordsAvailable = false;
                        if (foundCity != null)
                        {
                            // This check now only verifies that coordinates are *configured*, not if the target GameObject is currently loaded.
                            // This is the key change to prevent buttons from becoming disabled after a scene change.
                            coordsAvailable = !string.IsNullOrEmpty(foundCity.targetGameObjectName) || (foundCity.coords != null && foundCity.coords.Length >= 3);
                        }

                        // 5) money checks
                        // If we cannot read money, do not treat it as a hard "not enough" while dialog is fresh.
                        bool hasEnoughMoney;
                        if (!haveMoneyInfo)
                        {
                            // If we are enforcing money now (after the grace window), be conservative and require money;
                            // otherwise allow while unknown (so transient -1 does not disable UI).
                            hasEnoughMoney = !enforceMoneyNow || (currentMoney >= cost);
                        }
                        else
                        {
                            hasEnoughMoney = currentMoney >= cost;
                        }

                        // 6) visited check (use the robust fallback helper if available)
                        bool visitedNow = false;
                        if (foundCity != null)
                        {
                            try { visitedNow = IsCityVisitedFallback(foundCity); } catch { visitedNow = false; }
                        }

                        // 7) scene-aware coords logic & current location check
                        bool targetSceneSpecified = foundCity != null && !string.IsNullOrEmpty(foundCity.sceneName);
                        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                        bool isCurrentScene = targetSceneSpecified && (foundCity != null && string.Equals(foundCity.sceneName, activeScene.name, StringComparison.OrdinalIgnoreCase));

                        // A city is visitable if it has coordinates OR if it's in a different scene (where coords will be found after load)
                        bool canVisit = coordsAvailable || (targetSceneSpecified && !isCurrentScene);

                        // final interactable decision: mesto je aktivni v pripade, ze je povoleno v configu (enabledByConfig) a zaroven
                        // mesto bylo navstiveno v minulosti hracem ve hre (visited) a zaroven
                        // hrac ma dostatek prostredku v inventari ( hasEnoughMoney ) a zaroven
                        // existuji souradnice pro teleport (canVisit) a zaroven
                        // mesto neni aktivni v pripade, ze se v nem hrac nachazi (!isCurrentScene)
                        bool shouldBeInteractableNow = enabledByConfig && visitedNow && hasEnoughMoney && canVisit && !isCurrentScene;

                        // Detailed debug log for each condition (include player coords if known)
                        TravelButtonPlugin.LogInfo($"Debug Refresh '{cityName}': " +
                                                   $"enabledByConfig={enabledByConfig}, " +
                                                   $"visitedNow={visitedNow}, " +
                                                   $"hasEnoughMoney={hasEnoughMoney}, " +
                                                   $"coordsAvailable={coordsAvailable}, " +
                                                   $"isCurrentScene={isCurrentScene}, " +
                                                   $"playerPos={(havePlayerPos ? $"({playerPos.x:F1},{playerPos.y:F1},{playerPos.z:F1})" : "unknown")} " +
                                                   $"-> shouldBeInteractableNow={shouldBeInteractableNow}");

                        if (btn.interactable != shouldBeInteractableNow)
                        {
                            btn.interactable = shouldBeInteractableNow;
                            img.color = shouldBeInteractableNow ? new Color(0.35f, 0.20f, 0.08f, 1f) : new Color(0.18f, 0.18f, 0.18f, 1f);
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

        Type canvasType = ReflectionUtils.SafeGetType("UnityEngine.Canvas, UnityEngine.UIModule");
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

    private void LogLoadedScenes()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            TravelButtonPlugin.LogInfo($"Loaded Scene[{i}] name='{s.name}' path='{s.path}' isLoaded={s.isLoaded}");
        }
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



    // Helper: try to invoke likely UI/inventory refresh methods on the given MB and a small set of other objects.
    private void TryInvokeRefreshMethods(MonoBehaviour sourceMb)
    {
        try
        {
            // Common candidate substrings for refresh/update methods
            string[] refreshCandidates = new string[] { "Refresh", "Update", "Sync", "OnCurrency", "OnMoney", "NotifyCurrency", "InventoryUpdated", "Rebuild" };

            // Try on the source object first
            var t = sourceMb.GetType();
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string n = mi.Name.ToLower();
                foreach (var cand in refreshCandidates)
                {
                    if (n.Contains(cand.ToLower()) && mi.GetParameters().Length == 0)
                    {
                        try
                        {
                            TravelButtonPlugin.LogInfo($"AttemptDeductSilver: invoking refresh method {t.FullName}.{mi.Name}() on '{sourceMb.gameObject?.name}'");
                            mi.Invoke(sourceMb, null);
                        }
                        catch (Exception ex)
                        {
                            TravelButtonPlugin.LogWarning($"AttemptDeductSilver: invoking {t.FullName}.{mi.Name}() threw: {ex}");
                        }
                    }
                }
            }

            // Also try a few broad-scope MonoBehaviours for UI/inventory refresh
            var allMB = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            int invoked = 0;
            foreach (var mb in allMB)
            {
                var mt = mb.GetType();
                foreach (var mi in mt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    string n = mi.Name.ToLower();
                    if (mi.GetParameters().Length != 0) continue;
                    foreach (var cand in refreshCandidates)
                    {
                        if (n.Contains(cand.ToLower()))
                        {
                            try
                            {
                                mi.Invoke(mb, null);
                                TravelButtonPlugin.LogInfo($"AttemptDeductSilver: invoked potential refresh {mt.FullName}.{mi.Name}() on '{mb.gameObject?.name}'");
                                invoked++;
                                if (invoked > 6) break; // don't spam too many calls
                            }
                            catch { /* ignore */ }
                        }
                    }
                    if (invoked > 6) break;
                }
                if (invoked > 6) break;
            }
            TravelButtonPlugin.LogInfo($"AttemptDeductSilver: attempted to invoke {invoked} potential refresh methods after deduction.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("AttemptDeductSilver: TryInvokeRefreshMethods exception: " + ex);
        }
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
                    var t = ReflectionUtils.SafeGetType(tname + ", Assembly-CSharp");
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
                    var navAgentType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshAgent, UnityEngine.AIModule");
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
