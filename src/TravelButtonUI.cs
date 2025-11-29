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
using MapMagic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodeCanvas.Tasks.Actions;
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
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static ExtraSceneVariantDetection;
using static MapMagic.SpatialHash;
using static TravelButton;
using static UnityEngine.GUI;

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
public partial class TravelButtonUI : MonoBehaviour
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
    private Transform inventoryVisibilityTarget;

    // Fallback visibility monitor coroutine when inventoryVisibilityTarget is not found
    private Coroutine visibilityMonitorCoroutine;

    // Prevent multiple teleport attempts at the same time
    private bool isTeleporting = false;

    private float dialogOpenedTime = 0f;

    private const string CustomIconFilename = "TravelButton_icon.png";
    private const string ResourcesIconPath = "TravelButton/icon"; // Resources/TravelButton/icon.png -> Resources.Load(ResourcesIconPath)

    private Coroutine inventoryVisibilityCoroutine;
    // Prevent competing placement after final placement is done
    private volatile bool placementFinalized = false;

    private Coroutine refreshButtonsCoroutine = null;
    private volatile bool refreshRequested = false;

    private static readonly object s_cityButtonLock = new object();
    private static readonly Dictionary<string, UnityEngine.UI.Button> s_cityButtonMap =
    new Dictionary<string, UnityEngine.UI.Button>(StringComparer.OrdinalIgnoreCase);

    private static bool _isSceneTransitionInProgress = false;

    private static bool _lastAttemptedTeleportSucceeded = true;
    public static bool LastAttemptedTeleportSucceeded => _lastAttemptedTeleportSucceeded;

    //close dialog and open variables
    private GameObject _tb_savedMenuManagerGO = null;
    private CanvasGroup _tb_savedMenuManagerCanvasGroup = null;
    private float _tb_savedCanvasAlpha = 1f;
    private bool _tb_savedCanvasInteractable = true;
    private bool _tb_savedCanvasBlocksRaycasts = true;
    private bool _tb_savedMenuManagerActive = true;
    private bool _tb_menuManagerHiddenByPlugin = false;
    private bool _tb_menuManagerClosedByMethod = false;

    // Example: City data model you already have when parsing JSON. Adapt to your actual class.
    // If you already have a City class, use that instead of this sample.
    [Serializable]
    public class CityEntry
    {
        public string name;
        public string sceneName;
        public string targetGameObjectName;
        public float[] coords; // <- use float[] (no double[])
        public int price = 1;
        public bool visited = false;
    }

    [Serializable]
    public class CitiesRoot
    {
        public List<CityEntry> cities;
    }

    private void StartInventoryVisibilityMonitor()
    {
        if (inventoryVisibilityCoroutine != null) return;
        inventoryVisibilityCoroutine = StartCoroutine(MonitorInventoryContainerVisibilityCoroutine());
    }

    private void StopInventoryVisibilityMonitor()
    {
        if (inventoryVisibilityCoroutine != null)
        {
            try { StopCoroutine(inventoryVisibilityCoroutine); } catch { }
            inventoryVisibilityCoroutine = null;
        }
    }

    bool _tb_lastInventoryShouldShow = false;

    // Example 2: Wiring with Unity Editor (Inspector)
    // If you have a Button in scene and want to wire via Inspector:
    // - Create a small public method wrapper (non-IEnumerator) that calls StartCoroutine.
    // - Assign this wrapper in the Button.onClick in the Editor.
    public void OnCityButtonClick_FromInspector(string sceneName, string targetName, Vector3 coords, bool haveCoords, int price)
    {
        StartCoroutine(TryTeleportThenChargeExplicit(sceneName, targetName, coords, haveCoords, price));
    }

    void Start()
    {
        TBLog.Info("TravelButtonUI.Start called.");
        CreateTravelButton();
        EnsureInputSystems();
        // start polling for inventory container (will reparent once found)
        StartCoroutine(PollForInventoryParentImpl());

    }

    // debug helper: press F9 in-game to dump Travel button state
    void Update()
    {
        TBLog.Info("DBG: TravelButtonUI.Update running");

        // keep existing backquote behaviour if present
        if (Input.GetKeyDown(KeyCode.BackQuote))
        {
            TBLog.Info("BackQuote key pressed - opening travel dialog.");
            OpenTravelDialog();
        }

        // Press F9 to dump debug info about the Travel button & visibility target
        if (Input.GetKeyDown(KeyCode.F8))
        {
            try
            {
                TBLog.Warn("F9 called");
                DumpTravelDebugInfo();
            }
            catch (Exception ex)
            {
                TBLog.Warn("F9 failed");
            }
        }
    }

    // Cleanup: stop monitor when this component is disabled/destroyed
    private void OnDisable()
    {
        StopRefreshCoroutine();
        StopInventoryVisibilityMonitor();
    }

    private void OnDestroy()
    {
        StopRefreshCoroutine();
        StopInventoryVisibilityMonitor();
    }


    private IEnumerator MonitorInventoryContainerVisibilityCoroutine(float pollInterval = 0.12f)
    {
        if (buttonObject == null) yield break;

        TBLog.Info("MonitorInventoryContainerVisibilityCoroutine: started; monitoring " +
                   (inventoryVisibilityTarget != null ? inventoryVisibilityTarget.name : "null"));

        // Ensure the last-state field exists on the class:
        // private bool _tb_lastInventoryShouldShow = false;
        // Also respects plugin-driven hides:
        // private bool _tb_menuManagerHiddenByPlugin = false;

        while (buttonObject != null)
        {
            bool shouldShow = true;

            if (inventoryVisibilityTarget != null)
            {
                var cg = inventoryVisibilityTarget.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    // Use alpha primarily: show if alpha above threshold. Use OR with interactable to be permissive.
                    shouldShow = (cg.alpha > 0.05f) || cg.interactable;
                    TBLog.Info($"Monitor: target '{inventoryVisibilityTarget.name}' CanvasGroup alpha={cg.alpha} interactable={cg.interactable} => shouldShow={shouldShow}");
                }
                else
                {
                    shouldShow = inventoryVisibilityTarget.gameObject.activeInHierarchy;
                    TBLog.Info($"Monitor: target '{inventoryVisibilityTarget.name}' no CanvasGroup => activeInHierarchy={shouldShow}");
                }
            }
            else
            {
                // No explicit target — keep button visible (safer default)
                shouldShow = true;
                TBLog.Info("Monitor: no inventoryVisibilityTarget => default shouldShow=true");
            }

            try
            {
                // If the visibility changed, update the button active state
                if (buttonObject.activeSelf != shouldShow)
                {
                    buttonObject.SetActive(shouldShow);
                    TBLog.Info("MonitorInventoryContainerVisibilityCoroutine: set button active=" + shouldShow);
                }

                // Detect transition: inventory was visible and now closed by player (visible -> not visible)
                // Only auto-close dialog when the plugin did NOT intentionally hide the MenuManager.
                if (_tb_lastInventoryShouldShow && !shouldShow && !_tb_menuManagerHiddenByPlugin)
                {
                    // Auto-close the travel dialog (only the plugin's dialogRoot)
                    try
                    {
                        if (dialogRoot != null)
                        {
                            TBLog.Info("MonitorInventoryContainerVisibilityCoroutine: detected inventory closed by player -> auto-closing travel dialog");
                            try { UnityEngine.Object.Destroy(dialogRoot); }
                            catch (Exception ex)
                            {
                                TBLog.Warn("MonitorInventoryContainerVisibilityCoroutine: Destroy(dialogRoot) threw: " + ex);
                                try { dialogRoot.SetActive(false); } catch { }
                            }
                            dialogRoot = null;
                        }

                        // Clear EventSystem selection so input focus returns to gameplay
                        try
                        {
                            var es = UnityEngine.EventSystems.EventSystem.current;
                            if (es != null)
                            {
                                es.SetSelectedGameObject(null);
                                TBLog.Info("MonitorInventoryContainerVisibilityCoroutine: cleared EventSystem selected GameObject");
                            }
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn("MonitorInventoryContainerVisibilityCoroutine: clearing EventSystem selected GameObject threw: " + ex);
                        }

                        // Restore cursor/camera input to gameplay mode
                        try
                        {
                            Cursor.lockState = CursorLockMode.Locked;
                            Cursor.visible = false;
                            TBLog.Info("MonitorInventoryContainerVisibilityCoroutine: restored cursor to gameplay");
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn("MonitorInventoryContainerVisibilityCoroutine: restoring cursor threw: " + ex);
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("MonitorInventoryContainerVisibilityCoroutine: auto-close dialog flow threw: " + ex);
                    }
                }

                // Save last state for next iteration
                _tb_lastInventoryShouldShow = shouldShow;
            }
            catch (Exception ex)
            {
                TBLog.Warn("MonitorInventoryContainerVisibilityCoroutine: SetActive/transition handling failed: " + ex);
            }

            // Poll after the requested interval
            yield return new WaitForSeconds(pollInterval);
        }

        TBLog.Info("MonitorInventoryContainerVisibilityCoroutine: ended.");
    }

    // Place buttonObject under sectionsRt so it participates in the toolbar layout.
    private void PlaceButtonInSections(RectTransform sectionsRt)
    {
        if (ensureSectionsCoroutine != null)
        {
            try { StopCoroutine(ensureSectionsCoroutine); } catch { }
            ensureSectionsCoroutine = null;
        }

        if (buttonObject == null || sectionsRt == null) return;

        // If already placed, nothing to do
        if (IsTransformOrAncestorImpl(buttonObject.transform.parent, sectionsRt)) return;

        // Prefer a named toolbar template (btnInventory), otherwise first active Button
        var template = sectionsRt.GetComponentsInChildren<UnityEngine.UI.Button>(true)
                        .FirstOrDefault(b => b != null && b.name.IndexOf("btnInventory", StringComparison.OrdinalIgnoreCase) >= 0)
                     ?? sectionsRt.GetComponentsInChildren<UnityEngine.UI.Button>(true)
                        .FirstOrDefault(b => b != null && b.gameObject.activeInHierarchy);

        Transform parentForIcons = sectionsRt;
        int insertIndex = -1;
        if (template != null)
        {
            parentForIcons = template.transform.parent ?? sectionsRt;
            insertIndex = template.transform.GetSiblingIndex() + 1;
        }

        buttonObject.transform.SetParent(parentForIcons, false);

        // copy LayoutElement if template exists
        var templLayout = template != null ? template.GetComponent<UnityEngine.UI.LayoutElement>() : null;
        var layout = buttonObject.GetComponent<UnityEngine.UI.LayoutElement>() ?? buttonObject.AddComponent<UnityEngine.UI.LayoutElement>();
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
            float size = Mathf.Max(32f, buttonObject.GetComponent<RectTransform>().sizeDelta.x);
            layout.preferredWidth = size;
            layout.preferredHeight = size;
        }

        // copy rect transform anchor/pivot/size from template if possible
        try
        {
            var rt = buttonObject.GetComponent<RectTransform>();
            if (template != null)
            {
                var tRt = template.GetComponent<RectTransform>();
                rt.localScale = tRt.localScale;
                rt.localRotation = tRt.localRotation;
                rt.anchorMin = tRt.anchorMin;
                rt.anchorMax = tRt.anchorMax;
                rt.pivot = tRt.pivot;
                rt.sizeDelta = new Vector2(layout.preferredWidth, layout.preferredHeight);
            }
        }
        catch { }

        // sibling index
        try
        {
            if (insertIndex >= 0 && insertIndex <= parentForIcons.childCount)
                buttonObject.transform.SetSiblingIndex(insertIndex);
            else
                buttonObject.transform.SetAsLastSibling();
        }
        catch { buttonObject.transform.SetAsLastSibling(); }

        // force immediate layout update
        try
        {
            var parentRt = parentForIcons as RectTransform;
            if (parentRt != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
            Canvas.ForceUpdateCanvases();
        }
        catch { }

        buttonObject.SetActive(true);
        TBLog.Info("PlaceButtonInSections: placed under '" + parentForIcons.name + "'");

        placementFinalized = true;
        if (ensureSectionsCoroutine != null)
        {
            try { StopCoroutine(ensureSectionsCoroutine); } catch { }
            ensureSectionsCoroutine = null;
        }

        StopInventoryVisibilityMonitor();
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
                    TBLog.Info($"LoadCustomButtonSprite: could not find suitable LoadImage API for '{candidate}'");
                    continue;
                }

                try { tex.Apply(true, false); } catch { try { tex.Apply(); } catch { } }

                var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                spr.name = "TravelButton_CustomIcon";
                return spr;
            }
            catch (Exception ex)
            {
                TBLog.Warn("LoadCustomButtonSprite: failed to load candidate image: " + ex);
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
            TBLog.Warn("ApplyCustomIconToButton failed: " + ex);
        }
    }

    // Poll for the inventory UI and reparent as soon as we find the inventory.
    // Use StartCoroutine(PollForInventoryParentImpl()) to run this.
    private IEnumerator PollForInventoryParentImpl()
    {
        var wait = new WaitForSeconds(0.25f);
        const float overallTimeout = 15.0f; // total time to keep polling
        float overallDeadline = Time.realtimeSinceStartup + overallTimeout;

        TBLog.Info("PollForInventoryParentImpl: started.");

        // If someone already finalized placement, do nothing
        if (placementFinalized)
        {
            TBLog.Info("PollForInventoryParentImpl: placement already finalized; exiting.");
            yield break;
        }

        // Known toolbar / inventory button names to prefer
        var knownToolbarButtonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "btnInventory", "btnEquipment", "btnVitals", "btnEffects",
        "btnCrafting", "btnQuickSlot", "btnSkills", "btnJournal"
    };

        while (buttonObject != null)
        {
            // If placement finalized mid-loop, quit
            if (placementFinalized)
            {
                TBLog.Info("PollForInventoryParentImpl: placement finalized while polling; exiting.");
                yield break;
            }

            RectTransform foundInvRoot = null;

            try
            {
                var all = FindAllRectTransformsSafeImpl() ?? new RectTransform[0];
                RectTransform bestCandidate = null;

                foreach (var rt in all)
                {
                    if (rt == null) continue;
                    string path = GetTransformPath(rt) ?? "";

                    // Prefer explicit TopPanel/Sections/CharacterMenus candidates immediately
                    if (path.IndexOf("TopPanel", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Sections", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("CharacterMenus", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bestCandidate = rt;
                        break;
                    }

                    // Prefer explicit Inventory named nodes
                    if (path.IndexOf("/Inventory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        rt.name.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bestCandidate = rt;
                        break;
                    }

                    // Heuristic: Content rect with enough children (likely inventory grid)
                    if (rt.name.Equals("Content", StringComparison.OrdinalIgnoreCase) && rt.childCount >= 6)
                    {
                        bestCandidate = rt;
                        break;
                    }

                    // Heuristic: a rect that contains many item-like buttons/images; accept as fallback candidate
                    var buttons = rt.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                    if (buttons != null && buttons.Length >= 6)
                    {
                        // Further prefer if any button has a known toolbar name
                        if (buttons.Any(b => b != null && knownToolbarButtonNames.Contains(b.name)))
                        {
                            bestCandidate = rt;
                            break;
                        }

                        if (bestCandidate == null)
                            bestCandidate = rt;
                    }
                }

                if (bestCandidate != null)
                {
                    // If it's a "Content" node, prefer the parent container (inventory root)
                    RectTransform invRoot = bestCandidate;
                    if (bestCandidate.name.Equals("Content", StringComparison.OrdinalIgnoreCase) && bestCandidate.parent is RectTransform)
                        invRoot = bestCandidate.parent as RectTransform;

                    // Conservative acceptance test: only accept if invRoot looks like the toolbar/inventory
                    string invPath = GetTransformPath(invRoot) ?? invRoot.name;
                    bool acceptCandidate = false;

                    // Accept if path or name explicitly references toolbar-like names
                    if (invPath.IndexOf("TopPanel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        invPath.IndexOf("Sections", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        invPath.IndexOf("CharacterMenus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        invRoot.name.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        invRoot.name.Equals("Content", StringComparison.OrdinalIgnoreCase))
                    {
                        acceptCandidate = true;
                    }

                    // Accept if it contains known toolbar buttons
                    var childButtons = invRoot.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                    if (!acceptCandidate && childButtons != null && childButtons.Any(b => b != null && knownToolbarButtonNames.Contains(b.name)))
                        acceptCandidate = true;

                    // Accept if it's clearly an item grid (Content with many children)
                    if (!acceptCandidate && invRoot.name.Equals("Content", StringComparison.OrdinalIgnoreCase) && invRoot.childCount >= 6)
                        acceptCandidate = true;

                    if (acceptCandidate)
                    {
                        foundInvRoot = invRoot;
                    }
                    else
                    {
                        TBLog.Info($"PollForInventoryParentImpl: candidate '{invPath}' rejected (not toolbar/inventory-like).");
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("PollForInventoryParentImpl: exception during detection: " + ex);
            }

            // If we found a suitable invRoot, handle reparenting outside the try/catch (no yields inside try)
            if (foundInvRoot != null)
            {
                // If placement finalized while we computed, exit
                if (placementFinalized)
                {
                    TBLog.Info("PollForInventoryParentImpl: placement finalized before reparent; skipping reparent.");
                    yield break;
                }

                TBLog.Info($"PollForInventoryParentImpl: accepting inventory candidate '{GetTransformPath(foundInvRoot)}', reparenting button.");

                if (!placementFinalized)
                {
                    try
                    {
                        ReparentButtonToInventory(foundInvRoot);

                        // Mark placement finalized so other placement flows won't steal the button
                        placementFinalized = true;

                        // Debug: log parent path for diagnostics
                        try
                        {
                            string parentPath = "(none)";
                            if (buttonObject != null && buttonObject.transform.parent != null)
                                parentPath = GetTransformPath(buttonObject.transform.parent as RectTransform) ?? buttonObject.transform.parent.name;
                            TBLog.Info("Button parent after placement: " + parentPath);
                        }
                        catch { }

                        // Start visibility sync so the button hides/shows with the inventory toolbar (if a target can be found)
                        try
                        {
                            StopInventoryVisibilityMonitor();
                            if (TryFindInventoryVisibilityTarget(foundInvRoot))
                            {
                                StartInventoryVisibilityMonitor();
                                TBLog.Info("Started inventory visibility monitor for travel button.");
                            }
                            else
                            {
                                TBLog.Info("TryFindInventoryVisibilityTarget: no visibility target found after placement.");
                            }
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn("Failed to start inventory visibility monitor: " + ex);
                        }

                        TBLog.Info("PollForInventoryParentImpl: ReparentButtonToInventory called.");
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("PollForInventoryParentImpl: ReparentButtonToInventory failed: " + ex);
                    }
                }

                yield break;
            }

            // timeout check
            if (Time.realtimeSinceStartup >= overallDeadline)
            {
                TBLog.Info("PollForInventoryParentImpl: overall timeout reached; giving up.");
                yield break;
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
                TBLog.Info("ReparentButtonToInventory: found Sections under Inventory; using ParentButtonIntoSectionsImpl.");
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
                    TBLog.Warn("ReparentButtonToInventory: set sibling index failed: " + ex);
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
                    TBLog.Warn("ReparentButtonToInventory: layout rebuild failed: " + ex);
                }

                buttonObject.SetActive(true);
                TBLog.Info("ReparentButtonToInventory: inserted next to template '" + templateBtn.name + "' under parent '" + (parent.name) + "'.");
                return;
            }

            // 3) Last-resort fallback: parent under inventory root itself
            TBLog.Warn("ReparentButtonToInventory: no Sections or template button found; parenting under inventory root.");
            buttonObject.transform.SetParent(inventoryTransform, false);
            Canvas.ForceUpdateCanvases();
            buttonObject.SetActive(true);
        }
        catch (Exception ex)
        {
            TBLog.Warn("ReparentButtonToInventory: unexpected error: " + ex);
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
                TBLog.Warn("MonitorInventoryContainerVisibility exception: " + ex);
            }

            // low frequency: check twice per second
            yield return new WaitForSeconds(0.5f);
        }
    }

    // Best-effort: look for the GameObject that is actually toggled when inventory opens:
    // - prefer an object whose name contains "Window" or "Panel",
    // - or any descendant/ancestor that has a CanvasGroup (we treat its alpha/interactable as visibility)
    private bool TryFindInventoryVisibilityTarget(Transform root)
    {
        inventoryVisibilityTarget = null;
        if (root == null) return false;

        try
        {
            // Helper to check name keywords quickly
            bool NameLooksLikeToolbar(string name)
            {
                if (string.IsNullOrEmpty(name)) return false;
                name = name.ToLowerInvariant();
                return name.Contains("toppanel") || name.Contains("sections") || name.Contains("charactermenus")
                    || name.Contains("inventory") || name.Contains("toolbar") || name.Contains("menumanager") || name.Contains("generalmenus");
            }

            // 1) Prefer a CanvasGroup or Canvas in the parents whose name looks like TopPanel/Sections/etc.
            var parentCgCandidates = root.GetComponentsInParent<CanvasGroup>(true);
            foreach (var cg in parentCgCandidates)
            {
                if (cg == null) continue;
                if (NameLooksLikeToolbar(cg.gameObject.name))
                {
                    inventoryVisibilityTarget = cg.transform;
                    TBLog.Info($"TryFindInventoryVisibilityTarget: using parent CanvasGroup '{cg.gameObject.name}'");
                    return true;
                }
            }

            var parentCanvasCandidates = root.GetComponentsInParent<Canvas>(true);
            foreach (var cv in parentCanvasCandidates)
            {
                if (cv == null) continue;
                if (NameLooksLikeToolbar(cv.gameObject.name))
                {
                    inventoryVisibilityTarget = cv.transform;
                    TBLog.Info($"TryFindInventoryVisibilityTarget: using parent Canvas '{cv.gameObject.name}'");
                    return true;
                }
            }

            // 2) Then prefer a child CanvasGroup under the root (some UI hierarchies have child groups that control menu visibility)
            var childCgs = root.GetComponentsInChildren<CanvasGroup>(true);
            foreach (var cg in childCgs)
            {
                if (cg == null) continue;
                if (NameLooksLikeToolbar(cg.gameObject.name))
                {
                    inventoryVisibilityTarget = cg.transform;
                    TBLog.Info($"TryFindInventoryVisibilityTarget: using child CanvasGroup '{cg.gameObject.name}'");
                    return true;
                }
            }

            // 3) If none matched above, prefer nearest parent CanvasGroup (fallback)
            var nearestParentCg = root.GetComponentsInParent<CanvasGroup>(true).FirstOrDefault();
            if (nearestParentCg != null)
            {
                inventoryVisibilityTarget = nearestParentCg.transform;
                TBLog.Info($"TryFindInventoryVisibilityTarget: using nearest parent CanvasGroup '{nearestParentCg.gameObject.name}' (fallback)");
                return true;
            }

            // 4) Prefer a Canvas parent as a fallback if no CanvasGroup found
            var nearestCanvas = root.GetComponentsInParent<Canvas>(true).FirstOrDefault();
            if (nearestCanvas != null)
            {
                inventoryVisibilityTarget = nearestCanvas.transform;
                TBLog.Info($"TryFindInventoryVisibilityTarget: using nearest Canvas '{nearestCanvas.gameObject.name}' (fallback)");
                return true;
            }

            // 5) Last fallback: use the provided root itself
            inventoryVisibilityTarget = root;
            TBLog.Info($"TryFindInventoryVisibilityTarget: using fallback root '{root.gameObject.name}'");
            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryFindInventoryVisibilityTarget: exception while finding target: " + ex);
            inventoryVisibilityTarget = null;
            return false;
        }
    }
    // Ensure EventSystem + GraphicRaycaster exist
    private void EnsureInputSystems()
    {
        try
        {
            if (EventSystem.current == null)
            {
                TBLog.Info("No EventSystem found - creating one.");
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
                    TBLog.Info("Canvas found but missing GraphicRaycaster - adding one.");
                    anyCanvas.gameObject.AddComponent<GraphicRaycaster>();
                }
            }
            else
            {
                TBLog.Warn("No Canvas found when ensuring input systems. UI may not be interactable until a Canvas exists.");
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("EnsureInputSystems exception: " + ex);
        }
    }

    // field (add near other fields)
    private Coroutine ensureSectionsCoroutine;

    // Find first RectTransform whose GetTransformPath contains the fragment (case-insensitive)
    private RectTransform FindRectTransformByPathFragment(string pathFragment)
    {
        if (string.IsNullOrEmpty(pathFragment)) return null;
        try
        {
            var all = FindAllRectTransformsSafeImpl() ?? new RectTransform[0];
            string frag = pathFragment.ToLowerInvariant();
            foreach (var rt in all)
            {
                if (rt == null) continue;
                string p = GetTransformPath(rt) ?? "";
                if (p.ToLowerInvariant().Contains(frag) && rt.gameObject != null && rt.gameObject.activeInHierarchy)
                    return rt;
            }
        }
        catch { }
        return null;
    }

    // Coroutine: try exact path match first, then smaller fragments, for up to timeout seconds
    private IEnumerator EnsurePlacedInTopSectionsCoroutine(float timeoutSeconds = 8f, float pollInterval = 0.25f)
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (buttonObject != null && Time.realtimeSinceStartup < deadline)
        {
            // Try a unique enough path fragment from your DebugLog output
            var sections = FindRectTransformByPathFragment("MenuManager/CharacterUIs/PlayerChar")
                        ?? FindRectTransformByPathFragment("TopPanel/Sections")
                        ?? FindRectTransformByPathFragment("TopPanel");

            if (sections != null)
            {
                PlaceButtonInSections(sections);
                ensureSectionsCoroutine = null;
                yield break;
            }

            yield return new WaitForSeconds(pollInterval);
        }

        // timed out  fall back to your conservative fallback (screen top or inventory fallback)
        TBLog.Info("EnsurePlacedInTopSectionsCoroutine: timeout, using ForceTopToolbarPlacementImpl fallback.");
        ForceTopToolbarPlacementImpl(FindAllCanvasesSafeImpl().FirstOrDefault());
        if (buttonObject != null) buttonObject.SetActive(true);
        ensureSectionsCoroutine = null;
    }

    void CreateTravelButton()
    {
        TBLog.Info("CreateTravelButton: beginning UI creation.");
        try
        {
            // create basic button object
            buttonObject = new GameObject("TravelButton");
            buttonObject.AddComponent<CanvasRenderer>();

            // track whether we successfully placed the button deterministically
            bool placed = false;
            RectTransform placedSectionsRt = null;

            // Try a deterministic immediate placement using the exact path fragment(s)
            try
            {
                var sections = FindRectTransformByPathFragment("MenuManager/CharacterUIs/PlayerChar");
                if (sections == null) sections = FindRectTransformByPathFragment("TopPanel/Sections");

                if (sections != null)
                {
                    PlaceButtonInSections(sections);
                    placed = true;
                    placementFinalized = true;
                    placedSectionsRt = sections;

                    // stop any pending placement coroutine (no longer needed)
                    if (ensureSectionsCoroutine != null)
                    {
                        try { StopCoroutine(ensureSectionsCoroutine); } catch { }
                        ensureSectionsCoroutine = null;
                    }

                    // start visibility monitoring for the sections we placed under
                    StopInventoryVisibilityMonitor();
                    if (TryFindInventoryVisibilityTarget(sections))
                        StartInventoryVisibilityMonitor();
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("CreateTravelButton: deterministic placement attempt failed: " + ex);
            }

            // Add UI components (Button/Image) etc.
            travelButton = buttonObject.AddComponent<Button>();

            var img = buttonObject.AddComponent<Image>();
            img.color = new Color(0.45f, 0.26f, 0.13f, 1f);

            travelButton.targetGraphic = img;
            travelButton.interactable = true;
            img.raycastTarget = true;

            var rt = buttonObject.GetComponent<RectTransform>();
            if (rt == null) rt = buttonObject.AddComponent<RectTransform>();

            // small toolbar icon size
            const float smallSize = 40f;
            rt.sizeDelta = new Vector2(smallSize, smallSize);

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer != -1) buttonObject.layer = uiLayer;

            // keep hidden until we place it
            try { buttonObject.SetActive(false); } catch { }

            // parent to a top-level canvas so we exist in UI space
            var canvas = FindCanvas();
            if (canvas != null)
            {
                // If we didn't already place it deterministically, attach to canvas and try canvas-local heuristics
                if (!placed)
                {
                    try
                    {
                        buttonObject.transform.SetParent(canvas.transform, false);

                        // Try immediate parent into sections if available on this canvas
                        RectTransform sectionsRt = null;
                        try { sectionsRt = FindSectionsGroup(canvas); } catch { sectionsRt = null; }
                        TBLog.Info($"CreateTravelButton: FindSectionsGroup returned = {(sectionsRt != null ? sectionsRt.name : "null")}");

                        if (sectionsRt != null && sectionsRt.gameObject.activeInHierarchy)
                        {
                            try
                            {
                                ParentButtonIntoSectionsImpl(sectionsRt, smallSize);
                                TBLog.Info("CreateTravelButton: parented into Sections and activated.");
                                try { buttonObject.SetActive(true); } catch { }

                                // start visibility monitor for the sections we used
                                StopInventoryVisibilityMonitor();
                                if (TryFindInventoryVisibilityTarget(sectionsRt))
                                    StartInventoryVisibilityMonitor();

                                placed = true;
                                placedSectionsRt = sectionsRt;
                            }
                            catch (Exception ex)
                            {
                                TBLog.Warn("CreateTravelButton: ParentButtonIntoSectionsImpl failed: " + ex);
                                // fallback: try PlaceOnToolbarWhenAvailable which waits for toolbar on this canvas
                                try { StartCoroutine(PlaceOnToolbarWhenAvailable(canvas, 8f)); } catch { ForceTopToolbarPlacementImpl(canvas); }
                            }
                        }
                        else
                        {
                            // No sections found on canvas right now: start the coroutine(s) that will keep trying
                            try
                            {
                                if (ensureSectionsCoroutine == null)
                                    ensureSectionsCoroutine = StartCoroutine(EnsurePlacedInTopSectionsCoroutine());
                                // also start canvas-scoped waiter that tries to place when this canvas' Sections becomes available
                                StartCoroutine(PlaceOnToolbarWhenAvailable(canvas, 8f));
                                TBLog.Info("CreateTravelButton: started PlaceOnToolbarWhenAvailable coroutine to wait for toolbar.");
                            }
                            catch (Exception ex)
                            {
                                TBLog.Warn("CreateTravelButton: failed to start PlaceOnToolbarWhenAvailable: " + ex);
                                // as a last resort, force a top-of-screen placement
                                ForceTopToolbarPlacementImpl(canvas);
                                try { buttonObject.SetActive(true); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("CreateTravelButton: canvas parenting/sections logic failed: " + ex);
                        try { buttonObject.SetActive(true); } catch { }
                    }
                }
                else
                {
                    // Already placed deterministically: ensure it's in a canvas and has high sorting order
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
            }
            else
            {
                TBLog.Warn("CreateTravelButton: no Canvas found at creation time; button created at scene root.");
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
                TBLog.Warn("CreateTravelButton: ApplyCustomIconToButton failed: " + ex);
            }

            // start persistent monitor to snap back if moved
            try { StartCoroutine(MonitorAndMaintainButtonParentImpl()); } catch (Exception ex) { TBLog.Warn("CreateTravelButton: failed to start monitor: " + ex); }

            // If we haven't yet placed the button and it's still inactive, ensure fallback activation
            if (!placed)
            {
                try
                {
                    // if PlaceOnToolbarWhenAvailable or EnsurePlacedInTopSectionsCoroutine will handle activation later,
                    // otherwise ensure the button is visible so user can still interact with it.
                    if (buttonObject != null && !buttonObject.activeSelf)
                        buttonObject.SetActive(true);
                }
                catch { }
            }

            TBLog.Info("CreateTravelButton: Travel button created, ClickLogger attached, and listener attached.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("CreateTravelButton: exception: " + ex);
        }

        Debug_ForceShowButton();
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
            TBLog.Warn("FindSectionsGroup: " + ex);
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
            TBLog.Warn("ParentButtonIntoSectionsImpl: layout/index update failed: " + ex);
            try { buttonObject.transform.SetAsLastSibling(); } catch { }
        }

        // Ensure visible
        try { buttonObject.SetActive(true); } catch { }

        TBLog.Info("ParentButtonIntoSectionsImpl: button parented under '" + (buttonObject.transform.parent != null ? buttonObject.transform.parent.name : "null") + "'");
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

        // 3) Final fallback: Resources.FindObjectsOfTypeAll (includes inactive and assets)  filter to scene objects.
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
                    TBLog.Warn("PlaceOnToolbarWhenAvailable: FindSectionsGroup threw for canvas " + (c != null ? c.name : "null") + ": " + ex);
                }
            }

            if (foundSections != null)
            {
                try
                {
                    TBLog.Info($"PlaceOnToolbarWhenAvailable: found Sections '{foundSections.name}' under Canvas '{(foundCanvas != null ? foundCanvas.name : "null")}' - parenting.");
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
                    TBLog.Warn("PlaceOnToolbarWhenAvailable: ParentButtonIntoSectionsImpl failed: " + ex);
                    ForceTopToolbarPlacementImpl(foundCanvas ?? startCanvas);
                    if (buttonObject != null) buttonObject.SetActive(true);
                }
                yield break;
            }

            yield return wait;
        }

        TBLog.Info("PlaceOnToolbarWhenAvailable: timeout waiting for Sections; using fallback placement.");
        try
        {
            ForceTopToolbarPlacementImpl(startCanvas);
            if (buttonObject != null) buttonObject.SetActive(true);
        }
        catch (Exception ex)
        {
            TBLog.Warn("PlaceOnToolbarWhenAvailable: ForceTopToolbarPlacementImpl failed: " + ex);
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
                TBLog.Info("ForceTopToolbarPlacementImpl: placed fallback at " + localPoint);
            }
            else
            {
                TBLog.Warn("ForceTopToolbarPlacementImpl: Screen->Local conversion failed, leaving default transform.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ForceTopToolbarPlacementImpl: " + ex);
        }
        // mark placement final so other coroutines won't reparent it later
        placementFinalized = true;
        if (ensureSectionsCoroutine != null)
        {
            try { StopCoroutine(ensureSectionsCoroutine); } catch { }
            ensureSectionsCoroutine = null;
        }

        StopInventoryVisibilityMonitor();
    }

    // Persistent monitor: ensure the button remains parented to Sections while the game runs.
    private IEnumerator MonitorAndMaintainButtonParentImpl()
    {
        var waitShort = new WaitForSeconds(0.5f);
        var waitLong = new WaitForSeconds(0.75f);

        TBLog.Info("MonitorAndMaintainButtonParentImpl: started.");

        while (true)
        {
            if (buttonObject == null) yield break;

            // If placement has been finalized, continue to monitor but do not allow inventory reparenting.
            // We still call TryMaintainParent so the button snaps back to the accepted parent if something else moved it,
            // but TryMaintainParent must respect placementFinalized (see note below).
            if (placementFinalized)
            {
                // If you want the monitor to keep ensuring the button stays where you placed it, leave TryMaintainParent call here.
                // If TryMaintainParent may reparent to inventory, ensure TryMaintainParent checks placementFinalized before doing that.
                try
                {
                    TryMaintainParent(FindCanvas());
                }
                catch (Exception ex)
                {
                    TBLog.Warn("MonitorAndMaintainButtonParentImpl: TryMaintainParent threw (finalized): " + ex);
                }

                yield return waitLong;
                continue;
            }

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
                TBLog.Warn("MonitorAndMaintainButtonParentImpl: TryMaintainParent threw: " + ex);
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
            try { sections = FindSectionsGroup(canvas); } catch (Exception ex) { TBLog.Warn("TryMaintainParent: FindSectionsGroup threw: " + ex); sections = null; }

            if (sections == null) return false;

            Transform currentParent = null;
            try { currentParent = buttonObject.transform.parent; } catch (Exception ex) { TBLog.Warn("TryMaintainParent: could not get current parent: " + ex); currentParent = null; }

            bool needsReparent = (currentParent == null) || !IsTransformOrAncestorImpl(currentParent, sections);

            if (!needsReparent) return false;

            try
            {
                ParentButtonIntoSectionsImpl(sections, Mathf.Max(32f, buttonObject.GetComponent<RectTransform>().sizeDelta.x));
                TBLog.Info("TryMaintainParent: reparented button into Sections.");
                return true;
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryMaintainParent: reparent attempt failed: " + ex);
                return false;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryMaintainParent: unexpected exception: " + ex);
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
    // Corrected DebugLogToolbarCandidates  uses RectTransform list for the candidate scan
    private void DebugLogToolbarCandidates()
    {
        try
        {
            // Log canvases (existing helper)
            var canvases = FindAllCanvasesSafeImpl();
            TBLog.Info($"DebugLogToolbarCandidates: canvases found = {canvases.Length}");
            //            foreach (var c in canvases)
            //            {
            //                TBLog.Info($" Canvas '{c.name}' renderMode={c.renderMode} scale={c.scaleFactor} worldCamera={(c.worldCamera != null ? c.worldCamera.name : "null")}");
            //            }

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
                    TBLog.Info($"Candidate [{i}] '{rt.name}' active={rt.gameObject.activeInHierarchy} btnCount={(btns != null ? btns.Length : 0)} rect=({rt.rect.width:F0}x{rt.rect.height:F0}) worldTopY={worldTopY} path={GetTransformPath(rt)}");
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DebugLogToolbarCandidates: " + ex);
        }
    }

    private void StopRefreshCoroutine()
    {
        try
        {
            refreshRequested = false;
            if (refreshButtonsCoroutine != null)
            {
                try { StopCoroutine(refreshButtonsCoroutine); } catch { }
                refreshButtonsCoroutine = null;
            }
            try { UnregisterCityButtons(); } catch { }
            TBLog.Info("StopRefreshCoroutine: stopped refresh and cleared registrations.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("StopRefreshCoroutine failed: " + ex.Message);
        }
    }

    // Add to TravelButtonPlugin class

    // Print a compact summary of the current in-memory TravelButton.Cities (no variants list).
    public void DebugDumpAllCitiesSummary(string tag = "DebugDumpAllCitiesSummary")
    {
        try
        {
            if (TravelButton.Cities == null)
            {
                TBLog.Info($"{tag}: TravelButton.Cities == null");
                return;
            }

            TBLog.Info($"{tag}: Cities count = {TravelButton.Cities.Count}");

            for (int i = 0; i < TravelButton.Cities.Count; i++)
            {
                var c = TravelButton.Cities[i];
                if (c == null)
                {
                    TBLog.Info($"{tag}: city[{i}] == null");
                    continue;
                }

                string coords;
                try
                {
                    coords = (c.coords != null && c.coords.Length > 0)
                        ? $"[{string.Join(", ", c.coords.Select(f => f.ToString("G")))}]"
                        : "null";
                }
                catch { coords = "unavailable"; }

                string priceStr = c.price.HasValue ? c.price.Value.ToString() : "null";
                string lastKnown = c.lastKnownVariant ?? "";
                bool enabled = false;
                try { enabled = c.enabled; } catch { /* ignore if missing */ }
                string target = c.targetGameObjectName ?? "";

                TBLog.Info($"{tag}: city[{i}] name='{c.name}' scene='{c.sceneName}' target='{target}' enabled={enabled} visited={c.visited} price={priceStr} coords={coords} lastKnownVariant='{lastKnown}'");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"{tag}: failed: {ex.Message}");
        }
    }

    // Read the canonical TravelButton_Cities.json on disk and print every city entry (no variants array).
    // This shows what is actually persisted on disk even if the runtime list differs.
    public void DebugDumpCitiesFromJson(string tag = "DebugDumpCitiesFromJson")
    {
        try
        {
            string jsonPath = null;
            try
            {
                // Prefer existing helper if available
                jsonPath = TravelButtonPlugin.GetCitiesJsonPath();
            }
            catch { /* ignore if not available */ }

            if (string.IsNullOrEmpty(jsonPath))
            {
                // Best-effort fallback: plugin folder next to assembly
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    var pluginDir = Path.GetDirectoryName(asm.Location);
                    if (!string.IsNullOrEmpty(pluginDir))
                        jsonPath = Path.Combine(pluginDir, "TravelButton_Cities.json");
                }
                catch { }
            }

            if (string.IsNullOrEmpty(jsonPath))
            {
                TBLog.Info($"{tag}: could not determine TravelButton_Cities.json path");
                return;
            }

            TBLog.Info($"{tag}: reading canonical JSON from: {jsonPath}");

            if (!File.Exists(jsonPath))
            {
                TBLog.Info($"{tag}: file not found: {jsonPath}");
                return;
            }

            string text = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                TBLog.Info($"{tag}: file empty: {jsonPath}");
                return;
            }

            JObject root;
            try
            {
                root = JObject.Parse(text);
            }
            catch (Exception exParse)
            {
                TBLog.Warn($"{tag}: JSON parse failed: {exParse.Message}");
                return;
            }

            var citiesToken = root["cities"];
            if (citiesToken == null || citiesToken.Type != JTokenType.Array)
            {
                TBLog.Info($"{tag}: no 'cities' array found in JSON");
                return;
            }

            var arr = (JArray)citiesToken;
            TBLog.Info($"{tag}: JSON cities count = {arr.Count}");

            for (int i = 0; i < arr.Count; i++)
            {
                var jo = arr[i] as JObject;
                if (jo == null)
                {
                    TBLog.Info($"{tag}: json city[{i}] is not an object");
                    continue;
                }

                string name = (string)(jo["name"] ?? jo["sceneName"] ?? "");
                string sceneName = (string)(jo["sceneName"] ?? jo["name"] ?? "");
                string target = (string)(jo["targetGameObjectName"] ?? "");
                string desc = (string)(jo["desc"] ?? "");
                string priceStr = jo["price"] == null || jo["price"].Type == JTokenType.Null ? "null" : jo["price"].ToString();
                string visited = jo["visited"]?.ToString() ?? "";
                string lastKnown = jo["lastKnownVariant"]?.ToString() ?? "";

                string coords;
                try
                {
                    var cToken = jo["coords"] as JArray;
                    if (cToken != null)
                        coords = "[" + string.Join(", ", cToken.Children().Select(t => t.ToString())) + "]";
                    else coords = "null";
                }
                catch { coords = "unavailable"; }

                TBLog.Info($"{tag}: json city[{i}] name='{name}' scene='{sceneName}' target='{target}' visited='{visited}' price={priceStr} coords={coords} lastKnownVariant='{lastKnown}' desc='{desc}'");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn($"DebugDumpCitiesFromJson: failed: {ex.Message}");
        }
    }

    // Robust, simplified OpenTravelDialog implementation.
    // Replace your current OpenTravelDialog body with this version (keep the same signature).
    // Defensive replacement of OpenTravelDialog: per-city try/catch and progress logs to avoid crashes and collect diagnostics.
    // Robust, simplified OpenTravelDialog implementation.
    // Replace your current OpenTravelDialog body with this version (keep the same signature).
    // Defensive replacement of OpenTravelDialog: per-city try/catch and progress logs to avoid crashes and collect diagnostics.
    private void OpenTravelDialog()
    {
        TBLog.Info("OpenTravelDialog: invoked.");

        //        VerifyJsonAndRuntimeCitiesOnDialogOpen();
        DebugDumpAllCitiesSummary();

        try
        {
            try { ClearSaveRootCache(); } catch { }
            try { TravelButton.ClearVisitedCache(); } catch { }
            try { TravelButton.ClearCityVisitedCache(); } catch { }
            // Prepare persistent lookup once so per-city HasPlayerVisitedFast is cheap
            try { TravelButton.PrepareVisitedLookup(); } catch (Exception ex) { TBLog.Warn("PrepareVisitedLookup failed at dialog open: " + ex.Message); }

            // player pos
            try
            {
                var playerRoot = TeleportHelpers.FindPlayerRoot();
                if (playerRoot != null)
                {
                    var p = playerRoot.transform.position;
                    TBLog.Info($"OpenTravelDialog: hrac pos ({p.x:F3}, {p.y:F3}, {p.z:F3})");
                }
                if (playerRoot != null)
                {
                    Debug.Log("hrac exact start");
                    Debug.Log($"hrac exact world position: ({PlayerPositionExact.TryGetExactPlayerWorldPosition():F3})");
                }
                InventoryHelpers.SafeAddItemByIdToPlayer(4100550, 1);
                InventoryHelpers.SafeAddSilverToPlayer(100);
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: FindPlayerRoot failed: " + ex.Message);
            }

            // diagnostics
            try { LogLoadedScenes(); } catch (Exception ex) { TBLog.Warn("LogLoadedScenes failed: " + ex.Message); }
            try { DebugLogToolbarCandidates(); } catch (Exception ex) { TBLog.Warn("DebugLogToolbarCandidates failed: " + ex.Message); }
            try { TravelButton.DumpCityInteractability(); } catch (Exception ex) { TBLog.Warn("DumpCityInteractability failed: " + ex.Message); }

            // prepare visited lookup once
            try
            {
                if (dialogRoot == null)
                {
                    try { TravelButton.AutoAssignSceneNamesFromLoadedScenes(); } catch (Exception ex) { TBLog.Warn("AutoAssignSceneNamesFromLoadedScenes failed: " + ex.Message); }
                    try { TravelButton.PrepareVisitedLookup(); } catch (Exception ex) { TBLog.Warn("PrepareVisitedLookup failed: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: visited-cache prepare error: " + ex.Message);
            }

            // If dialog exists, re-activate and restart refresh (guarded)
            try
            {
                if (dialogRoot != null)
                {
                    dialogRoot.SetActive(true);
                    var canvas = dialogCanvas != null ? dialogCanvas.GetComponent<Canvas>() : dialogRoot.GetComponentInParent<Canvas>();
                    if (canvas != null) canvas.sortingOrder = 2000;
                    dialogRoot.transform.SetAsLastSibling();
                    TBLog.Info("OpenTravelDialog: re-activated existing dialogRoot.");

                    StartCoroutine(TemporarilyDisableDialogRaycasts());

                    if (refreshButtonsCoroutine == null)
                    {
                        refreshRequested = true;
                        refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));
                        TBLog.Info("OpenTravelDialog: started refresh coroutine (reactivate path).");
                    }
                    else
                    {
                        TBLog.Info("OpenTravelDialog: refresh coroutine already running; skipping StartCoroutine.");
                    }

                    dialogOpenedTime = Time.time;
                    return;
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: re-activation path failed: " + ex.Message);
            }

            // create canvas if needed
            try
            {
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
                    TBLog.Info("OpenTravelDialog: created TravelDialogCanvas.");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: creating dialogCanvas failed: " + ex.Message);
            }

            // build dialog root and layout
            try
            {
                dialogRoot = new GameObject("TravelDialog");
                dialogRoot.transform.SetParent(dialogCanvas.transform, false);
                dialogRoot.transform.SetAsLastSibling();
                dialogRoot.AddComponent<CanvasRenderer>();
                var rootRt = dialogRoot.AddComponent<RectTransform>();
                rootRt.anchorMin = rootRt.anchorMax = new Vector2(0.5f, 0.5f);
                rootRt.pivot = new Vector2(0.5f, 0.5f);
                rootRt.sizeDelta = new Vector2(520, 360);
                rootRt.anchoredPosition = Vector2.zero;
                var bg = dialogRoot.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.95f);
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: creating dialogRoot failed: " + ex.Message);
                // abort — cannot proceed without root
                return;
            }

            // Title and inline message
            try
            {
                var titleGO = new GameObject("Title");
                titleGO.transform.SetParent(dialogRoot.transform, false);
                var titleRt = titleGO.AddComponent<RectTransform>();
                titleRt.anchorMin = new Vector2(0f, 1f);
                titleRt.anchorMax = new Vector2(1f, 1f);
                titleRt.pivot = new Vector2(0.5f, 1f);
                titleRt.anchoredPosition = new Vector2(0, -8);
                titleRt.sizeDelta = new Vector2(0, 32);
                var titleText = titleGO.AddComponent<UnityEngine.UI.Text>();
                titleText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                titleText.text = $"Select destination (cost {TravelButton.cfgTravelCost.Value} silver)";
                titleText.alignment = TextAnchor.MiddleCenter;
                titleText.fontSize = 18;
                titleText.color = Color.white;

                var inlineMsgGO = new GameObject("InlineMessage");
                inlineMsgGO.transform.SetParent(dialogRoot.transform, false);
                var inlineRt = inlineMsgGO.AddComponent<RectTransform>();
                inlineRt.anchorMin = new Vector2(0f, 0.92f);
                inlineRt.anchorMax = new Vector2(1f, 0.99f);
                inlineRt.anchoredPosition = Vector2.zero;
                inlineRt.sizeDelta = Vector2.zero;
                var inlineText = inlineMsgGO.AddComponent<UnityEngine.UI.Text>();
                inlineText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                inlineText.text = "";
                inlineText.alignment = TextAnchor.MiddleCenter;
                inlineText.color = Color.yellow;
                inlineText.fontSize = 14;
                inlineText.raycastTarget = false;
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: creating title/inline message failed: " + ex.Message);
            }

            // Scroll + content
            RectTransform contentRt = null;
            try
            {
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
                var vImg = viewport.AddComponent<Image>(); vImg.color = Color.clear;
                viewport.AddComponent<UnityEngine.UI.RectMask2D>();

                var content = new GameObject("Content");
                content.transform.SetParent(viewport.transform, false);
                contentRt = content.AddComponent<RectTransform>();
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
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: creating scroll/content failed: " + ex.Message);
                // abort safely
                return;
            }

            // Populate buttons (defensive per-city)
            TBLog.Info($"OpenTravelDialog: cities={TravelButton.Cities?.Count ?? 0}");
            long playerMoney = GetPlayerCurrencyAmountOrMinusOne();

            if (TravelButton.Cities == null || TravelButton.Cities.Count == 0)
            {
                TBLog.Warn("OpenTravelDialog: No cities configured.");
            }
            else
            {
                int idx = 0;
                foreach (var city in TravelButton.Cities)
                {
                    //ONZA debug
                    var visited = HasPlayerVisited(city); // uses new authoritative HasPlayerVisited
                    TBLog.Info($"OpenTravelDialog: creating button for '{city.name}' visited={visited}");


                    idx++;
                    TBLog.Info($"OpenTravelDialog: creating button #{idx} for '{city?.name}'");

                    DumpVisitedKeysAndCandidates(new[] { city.name, city.sceneName, city.targetGameObjectName });

                    try
                    {
                        if (city == null || string.IsNullOrEmpty(city.name))
                        {
                            TBLog.Warn($"OpenTravelDialog: skipping null/invalid city at index {idx}");
                            continue;
                        }

                        var bgo = new GameObject("CityButton_" + city.name);
                        bgo.transform.SetParent(contentRt, false);
                        bgo.AddComponent<CanvasRenderer>();
                        var brt = bgo.AddComponent<RectTransform>(); brt.sizeDelta = new Vector2(0, 44);
                        var ble = bgo.AddComponent<LayoutElement>(); ble.preferredHeight = 44f; ble.minHeight = 30f; ble.flexibleWidth = 1f;

                        var bimg = bgo.AddComponent<Image>();
                        bimg.color = new Color(0.35f, 0.20f, 0.08f, 1f);

                        var bbtn = bgo.AddComponent<Button>();
                        var cb = bbtn.colors;
                        cb.normalColor = new Color(0.45f, 0.26f, 0.13f, 1f);
                        cb.highlightedColor = new Color(0.55f, 0.33f, 0.16f, 1f);
                        cb.pressedColor = new Color(0.36f, 0.20f, 0.08f, 1f);
                        cb.disabledColor = new Color(0.18f, 0.18f, 0.18f, 1f);
                        cb.colorMultiplier = 1f;
                        bbtn.colors = cb;

                        bbtn.interactable = false;
                        bimg.raycastTarget = false;
                        bbtn.transition = Selectable.Transition.ColorTint;
                        bbtn.targetGraphic = bimg;
                        var animator = bbtn.GetComponent<Animator>(); if (animator != null) animator.enabled = false;

                        // label
                        var lgo = new GameObject("Label"); lgo.transform.SetParent(bgo.transform, false);
                        var lrt = lgo.AddComponent<RectTransform>(); lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0.75f, 1f); lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = new Vector2(-4f, 0f);
                        var ltxt = lgo.AddComponent<UnityEngine.UI.Text>();
                        ltxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                        ltxt.text = city.name;
                        ltxt.alignment = TextAnchor.MiddleLeft;
                        ltxt.fontSize = 14;
                        ltxt.raycastTarget = false;

                        // price
                        int cost = TravelButton.cfgTravelCost.Value;
                        try { if (city.price.HasValue) cost = city.price.Value; } catch { }
                        var pgo = new GameObject("Price"); pgo.transform.SetParent(bgo.transform, false);
                        var prt = pgo.AddComponent<RectTransform>(); prt.anchorMin = new Vector2(0.75f, 0f); prt.anchorMax = new Vector2(1f, 1f); prt.offsetMin = new Vector2(0f, 0f); prt.offsetMax = new Vector2(-8f, 0f);
                        var ptxt = pgo.AddComponent<UnityEngine.UI.Text>();
                        ptxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                        ptxt.text = cost.ToString();
                        ptxt.alignment = TextAnchor.MiddleRight;
                        ptxt.fontSize = 14;
                        ptxt.raycastTarget = false;

                        // use canonical visited check
                        bool visitedInHistory = false;
                        try { visitedInHistory = TravelButton.HasPlayerVisited(city); } catch (Exception ex) { TBLog.Warn("Visited check failed for '" + city.name + "': " + ex.Message); visitedInHistory = false; }

                        // Color Logic:
                        // Active/Clickable (Light) = enabled && visited && clickable
                        // Inactive/Grey = enabled && visited && NOT clickable
                        // Hidden/Black = enabled && NOT visited

                        bool isEnabled = city.enabled;
                        bool isClickable = IsCityInteractable(city, playerMoney);

                        // Default colors
                        Color buttonColor = cb.disabledColor; // Dark/Blackish for hidden
                        Color textColor = new Color(0.55f, 0.55f, 0.55f, 1f); // Grey text
                        bool interactable = false;

                        if (isEnabled)
                        {
                            if (!visitedInHistory)
                            {
                                // Blackened / "zcernalou"
                                buttonColor = new Color(0.1f, 0.1f, 0.1f, 1f); // Very dark
                                textColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                                interactable = false;
                            }
                            else
                            {
                                if (isClickable)
                                {
                                    // Light / Active
                                    buttonColor = cb.normalColor;
                                    textColor = new Color(0.98f, 0.94f, 0.87f, 1f);
                                    interactable = true;
                                }
                                else
                                {
                                    // Grey / Inactive but visible
                                    buttonColor = cb.disabledColor;
                                    textColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                                    interactable = true; // Make it clickable so we can show the warning
                                }
                            }
                        }
                        else
                        {
                            // Should not happen if we filter enabled cities, but just in case
                            buttonColor = new Color(0.1f, 0.1f, 0.1f, 0.5f);
                            interactable = false;
                        }

                        // canvas group
                        var cg = bgo.GetComponent<CanvasGroup>() ?? bgo.AddComponent<CanvasGroup>();
                        cg.blocksRaycasts = interactable;
                        cg.interactable = interactable;

                        bbtn.interactable = interactable;
                        bimg.raycastTarget = interactable;
                        try { bimg.color = buttonColor; } catch { }
                        try { ltxt.color = textColor; } catch { }

                        // register and click handler
                        try { RegisterCityButton(city, bbtn); } catch (Exception ex) { TBLog.Warn("RegisterCityButton failed: " + ex.Message); }

                        TBLog.Info("[OpenTravelDialog] Before bbtn.onClick.AddListener((): isTeleporting = " + isTeleporting);
                        var capturedCity = city;
                        // --- replacement snippet: enhanced debug for city button listener ---
                        bbtn.onClick.AddListener(() =>
                        {
                            try
                            {
                                // Defensive local capture to avoid closure issues after UI rebuilds
                                var cityLocal = capturedCity;
                                string cityNameLocal = cityLocal?.name ?? "<null>";
                                int cityPriceLocal = cost;
                                var timeNow = DateTime.UtcNow.ToString("o");

                                TBLog.Info($"City button click (ENTER) time={timeNow} city='{cityNameLocal}' price={cityPriceLocal}");

                                if (isTeleporting)
                                {
                                    TBLog.Info($"City click: ignored because isTeleporting==true (city='{cityNameLocal}')");
                                    return;
                                }

                                bool cfgEnabled = false;
                                try { cfgEnabled = TravelButton.IsCityEnabled(cityNameLocal); } catch (Exception exCfg) { TBLog.Warn($"City click: IsCityEnabled threw for '{cityNameLocal}': {exCfg}"); }
                                bool visitedNowInHistory = false;
                                try { visitedNowInHistory = TravelButton.HasPlayerVisited(cityLocal); } catch (Exception exVisited) { TBLog.Warn($"City click: HasPlayerVisited threw for '{cityNameLocal}': {exVisited}"); visitedNowInHistory = false; }
                                long pm = GetPlayerCurrencyAmountOrMinusOne();

                                TBLog.Info($"City click: '{cityNameLocal}' cfgEnabled={cfgEnabled}, visitedNow={visitedNowInHistory}, playerMoney={pm}");

                                if (!cfgEnabled)
                                {
                                    TBLog.Info($"City click: blocked - disabled by config for '{cityNameLocal}'");
                                    // Should be visually disabled/blackened, but if clicked:
                                    return;
                                }

                                if (!visitedNowInHistory)
                                {
                                    // "zcernalou" - should be non-interactable usually, but if clicked:
                                    TBLog.Info($"City click: blocked - not discovered yet for '{cityNameLocal}'");
                                    return;
                                }

                                if (pm < 0)
                                {
                                    TBLog.Info($"City click: blocked - could not determine currency for '{cityNameLocal}'");
                                    ShowInlineDialogMessage("Nemáš dostatek prostředků"); // Treat unknown currency as failure or just warning
                                    return;
                                }

                                // Additional check: if not clickable for other reasons (e.g. current scene or funds)
                                // We check this BEFORE deduction to prevent charging for a failed travel attempt warning.
                                if (!IsCityInteractable(cityLocal, pm))
                                {
                                     // Check specific reasons to show correct message

                                     // 1. Funds check
                                     int p = TravelButton.cfgTravelCost.Value;
                                     try { if (cityLocal.price.HasValue) p = cityLocal.price.Value; } catch { }
                                     bool hasEnough = (pm >= 0) ? (pm >= p) : true; // if unknown (-1), assume enough to proceed to deduction step

                                     if (!hasEnough)
                                     {
                                         ShowInlineDialogMessage("Nemáš dostatek prostředků");
                                         return;
                                     }

                                     // 2. Current scene check
                                     var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                                     bool isCurrent = !string.IsNullOrEmpty(cityLocal.sceneName) && string.Equals(cityLocal.sceneName, active, StringComparison.OrdinalIgnoreCase);
                                     if (isCurrent)
                                     {
                                         ShowInlineDialogMessage("Jsi v této lokaci");
                                         return;
                                     }

                                     // Fallback message
                                     ShowInlineDialogMessage("Nelze cestovat");
                                     return;
                                }

                                TBLog.Info($"City click: attempting to deduct cost={cityPriceLocal} for '{cityNameLocal}' (simulation real attempt follows)");
                                bool deducted = false;
                                try
                                {
                                    deducted = CurrencyHelpers.AttemptDeductSilverDirect(cityPriceLocal, true);
                                }
                                catch (Exception exDeduct)
                                {
                                    TBLog.Warn($"City click: AttemptDeductSilverDirect threw for '{cityNameLocal}': {exDeduct}");
                                    deducted = false;
                                }

                                if (!deducted)
                                {
                                    TBLog.Info($"City click: blocked - not enough resources for '{cityNameLocal}' (cost={cityPriceLocal}, playerMoney={pm})");
                                    ShowInlineDialogMessage("Nemáš dostatek prostředků");
                                    return;
                                }

                                // disable UI buttons to prevent duplicate clicks while teleport starts
                                var contentParent = dialogRoot?.transform.Find("ScrollArea/Viewport/Content");
                                if (contentParent != null)
                                {
                                    int disabledCount = 0;
                                    for (int ci = 0; ci < contentParent.childCount; ci++)
                                    {
                                        try
                                        {
                                            var childBtn = contentParent.GetChild(ci).GetComponent<Button>();
                                            if (childBtn != null)
                                            {
                                                childBtn.interactable = false;
                                                disabledCount++;
                                            }
                                            var cgChild = contentParent.GetChild(ci).GetComponent<CanvasGroup>();
                                            if (cgChild != null) cgChild.blocksRaycasts = false;
                                        }
                                        catch (Exception exChild)
                                        {
                                            TBLog.Warn($"City click: failed disabling child index {ci} for '{cityNameLocal}': {exChild}");
                                        }
                                    }
                                    TBLog.Info($"City click: disabled {disabledCount} child buttons in dialog content before teleport for '{cityNameLocal}'");
                                }
                                else
                                {
                                    TBLog.Info($"City click: content parent not found; skipping disabling buttons for '{cityNameLocal}'");
                                }

                                // DO NOT set isTeleporting here — the coroutine should set/clear it.
                                TBLog.Info($"City click: starting TryTeleportThenCharge coroutine for '{cityNameLocal}', cost={cityPriceLocal}");

                                // Start the coroutine (must call StartCoroutine to run the IEnumerator).
                                try
                                {
                                    StartCoroutine(TryTeleportThenCharge(cityLocal, cityPriceLocal));
                                    TBLog.Info($"City click: started TryTeleportThenCharge coroutine for '{cityNameLocal}'");
                                }
                                catch (Exception exTeleport)
                                {
                                    TBLog.Warn($"City click: starting TryTeleportThenCharge coroutine threw for '{cityNameLocal}': {exTeleport}");

                                    // best-effort cleanup: re-enable buttons and clear flag if something failed
                                    isTeleporting = false;
                                    if (contentParent != null)
                                    {
                                        for (int ci = 0; ci < contentParent.childCount; ci++)
                                        {
                                            try
                                            {
                                                var childBtn = contentParent.GetChild(ci).GetComponent<Button>();
                                                if (childBtn != null) childBtn.interactable = true;
                                                var cgChild = contentParent.GetChild(ci).GetComponent<CanvasGroup>();
                                                if (cgChild != null) cgChild.blocksRaycasts = true;
                                            }
                                            catch { /* ignore */ }
                                        }
                                    }
                                    ShowInlineDialogMessage("Teleport failed to start (see log).");
                                }
                            }
                            catch (Exception ex)
                            {
                                TBLog.Warn("City button click handler exception: " + ex.ToString());
                            }
                        });
                    }
                    catch (Exception exCity)
                    {
                        TBLog.Warn($"OpenTravelDialog: failed to create button for city #{idx} ('{city?.name}'): {exCity}");
                        // continue with next city instead of letting dialog creation crash
                        continue;
                    }
                } // foreach cities
            } // else cities exist

            // force immediate layout rebuild
            try
            {
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);
                TBLog.Info($"OpenTravelDialog: content child count after populate = {contentRt.childCount}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: ForceRebuildLayoutImmediate failed: " + ex.Message);
            }

            // start finish layout coroutine and refresh (guarded)
            try { StartCoroutine(FinishDialogLayoutAndShow(dialogRoot.transform.Find("ScrollArea").GetComponent<ScrollRect>(), dialogRoot.transform.Find("ScrollArea/Viewport").GetComponent<RectTransform>(), contentRt)); } catch (Exception ex) { TBLog.Warn("FinishDialogLayoutAndShow start failed: " + ex.Message); }
            if (refreshButtonsCoroutine == null)
            {
                refreshRequested = true;
                refreshButtonsCoroutine = StartCoroutine(RefreshCityButtonsWhileOpen(dialogRoot));
                TBLog.Info("OpenTravelDialog: started refresh coroutine (initial create).");
            }
            else
            {
                TBLog.Info("OpenTravelDialog: refresh coroutine already running; skipping StartCoroutine.");
            }
            dialogOpenedTime = Time.time;

            // Close button
            try
            {
                var closeGO = new GameObject("Close");
                closeGO.transform.SetParent(dialogRoot.transform, false);
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
                var ctxt = closeTxtGO.AddComponent<UnityEngine.UI.Text>();
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
                        StopRefreshCoroutine();
                        if (dialogRoot != null) dialogRoot.SetActive(false);
                    }
                    catch (Exception ex)
                    {
                        TravelButtonPlugin.LogError("Close button click failed: " + ex);
                    }
                });
            }
            catch (Exception ex)
            {
                TBLog.Warn("OpenTravelDialog: creating close button failed: " + ex.Message);
            }

            // prevent immediate click-through
            try { StartCoroutine(TemporarilyDisableDialogRaycasts()); } catch { }

            TBLog.Info("OpenTravelDialog: created dialog.");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("OpenTravelDialog: unexpected exception: " + ex);
        }
    }
}