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
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static MapMagic.SpatialHash;
using static TravelButton;

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

        // timed out � fall back to your conservative fallback (screen top or inventory fallback)
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

        // 3) Final fallback: Resources.FindObjectsOfTypeAll (includes inactive and assets) � filter to scene objects.
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
    // Corrected DebugLogToolbarCandidates � uses RectTransform list for the candidate scan
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

    // Robust, simplified OpenTravelDialog implementation.
    // Replace your current OpenTravelDialog body with this version (keep the same signature).
    // Defensive replacement of OpenTravelDialog: per-city try/catch and progress logs to avoid crashes and collect diagnostics.
    // Robust, simplified OpenTravelDialog implementation.
    // Replace your current OpenTravelDialog body with this version (keep the same signature).
    // Defensive replacement of OpenTravelDialog: per-city try/catch and progress logs to avoid crashes and collect diagnostics.
    private void OpenTravelDialog()
    {
        TBLog.Info("OpenTravelDialog: invoked.");

        PrepareVisitedLookup();

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
//                InventoryHelpers.SafeAddItemByIdToPlayer(4100550, 2);
//                InventoryHelpers.SafeAddSilverToPlayer(100);
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

                        // compute initial interactability
                        bool initialInteractable = IsCityInteractable(city, playerMoney);

                        // canvas group
                        var cg = bgo.GetComponent<CanvasGroup>() ?? bgo.AddComponent<CanvasGroup>();
                        cg.blocksRaycasts = initialInteractable;
                        cg.interactable = initialInteractable;

                        bbtn.interactable = initialInteractable;
                        bimg.raycastTarget = initialInteractable;
                        try { bimg.color = initialInteractable ? cb.normalColor : cb.disabledColor; } catch { }
                        try { ltxt.color = initialInteractable ? new Color(0.98f, 0.94f, 0.87f, 1f) : new Color(0.55f, 0.55f, 0.55f, 1f); } catch { }

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
                                    ShowInlineDialogMessage("Destination disabled by config");
                                    return;
                                }

                                if (!visitedNowInHistory)
                                {
                                    TBLog.Info($"City click: blocked - not discovered yet for '{cityNameLocal}'");
                                    ShowInlineDialogMessage("Destination not discovered yet");
                                    return;
                                }

                                if (pm < 0)
                                {
                                    TBLog.Info($"City click: blocked - could not determine currency for '{cityNameLocal}'");
                                    ShowInlineDialogMessage("Could not determine your currency amount; travel blocked");
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
                                    ShowInlineDialogMessage("not enough resources to travel");
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

    // Add to TravelButtonUI class (near other public helpers)
    public void OnTeleportFinished_ClearUiState()
    {
        try
        {
            // Clear the flag so subsequent clicks are honored
            isTeleporting = false;

            // Re-enable dialog buttons if dialog is currently open
            try
            {
                var contentParent = dialogRoot?.transform.Find("ScrollArea/Viewport/Content");
                if (contentParent != null)
                {
                    int enabledCount = 0;
                    for (int ci = 0; ci < contentParent.childCount; ci++)
                    {
                        try
                        {
                            var childBtn = contentParent.GetChild(ci).GetComponent<UnityEngine.UI.Button>();
                            if (childBtn != null)
                            {
                                childBtn.interactable = true;
                                enabledCount++;
                            }
                            var cgChild = contentParent.GetChild(ci).GetComponent<UnityEngine.CanvasGroup>();
                            if (cgChild != null) cgChild.blocksRaycasts = true;
                        }
                        catch { /* best-effort */ }
                    }
                    TBLog.Info($"OnTeleportFinished_ClearUiState: cleared isTeleporting and re-enabled {enabledCount} child buttons.");
                }
                else
                {
                    TBLog.Info("OnTeleportFinished_ClearUiState: dialogRoot/content not found; just cleared isTeleporting.");
                }
            }
            catch (Exception exInner)
            {
                TBLog.Warn("OnTeleportFinished_ClearUiState: failed re-enabling buttons: " + exInner);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("OnTeleportFinished_ClearUiState: unexpected error: " + ex);
        }
    }

    // Add/replace these methods inside the TravelButtonMod class.

    /// <summary>
    /// Core evaluator that decides interactability from precomputed boolean inputs.
    /// Keeps the decision logic in one place so callers can use whichever inputs they already computed.
    /// </summary>
    public static bool IsCityInteractable(
        TravelButton.City city,
        bool enabledByConfig,
        bool visited,
        bool coordsAvailable,
        bool allowWithoutCoords,
        bool hasEnoughMoney,
        bool isCurrentScene)
    {
        if (city == null) return false;

        // final rule:
        // interactable = enabledByConfig && visited && (coordsAvailable || allowWithoutCoords) && hasEnoughMoney && !isCurrentScene
        return enabledByConfig
            && visited
            && (coordsAvailable || allowWithoutCoords)
            && hasEnoughMoney
            && !isCurrentScene;
    }

    /// <summary>
    /// Convenience evaluator that computes the necessary inputs from the current runtime state
    /// and returns whether the city should be interactable for the given playerMoney.
    /// playerMoney: pass -1 if unknown (treats unknown permissively).
    /// </summary>
    public static bool IsCityInteractable(TravelButton.City city, long playerMoney)
    {
        if (city == null) return false;

        // 1) Config flag
        bool enabledByConfig = false;
        try { enabledByConfig = IsCityEnabled(city.name); } catch { enabledByConfig = false; }

        // 2) Use the canonical city-aware visited check (fast+fallback, cached)
        bool visited = false;
        try { visited = HasPlayerVisited(city); } catch { visited = false; }

        // 3) Coordinates availability (treat configured coords/target as sufficient)
        bool coordsAvailable = false;
        try
        {
            coordsAvailable = !string.IsNullOrEmpty(city.targetGameObjectName) || (city.coords != null && city.coords.Length >= 3);
        }
        catch { coordsAvailable = false; }

        // 4) Price / player money
        int price = cfgTravelCost.Value;
        try { if (city.price.HasValue) price = city.price.Value; } catch { /* ignore */ }

        bool playerMoneyKnown = playerMoney >= 0;
        bool hasEnoughMoney = !playerMoneyKnown || (playerMoney >= price);

        // 5) Scene-aware allowance
        bool targetSceneSpecified = !string.IsNullOrEmpty(city.sceneName);
        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bool sceneMatches = !targetSceneSpecified || string.Equals(city.sceneName, activeScene.name, StringComparison.OrdinalIgnoreCase);
        bool allowWithoutCoords = targetSceneSpecified && !sceneMatches;
        bool isCurrentScene = targetSceneSpecified && sceneMatches;

        // Use the core boolean evaluator (keeps rule centralized)
        return IsCityInteractable(city, enabledByConfig, visited, coordsAvailable, allowWithoutCoords, hasEnoughMoney, isCurrentScene);
    }

    // Add this static helper into the TravelButtonMod class (paste anywhere among the other static helpers).
    // It safely attempts to destroy any existing dialog GameObject and to reset TravelButtonUI's dialog field(s),
    // then triggers a rebuild/open if the UI exposes such a method. This handles multiple fallback strategies so it's robust.
    public static void RebuildTravelDialog()
    {
        try
        {
            TBLog.Info("RebuildTravelDialog: attempting to find TravelButtonUI and rebuild dialog.");

            // Prefer typed TravelButtonUI instance if present
            var ui = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (ui != null)
            {
                var uiType = ui.GetType();

                // 1) Try to find and destroy a dialogRoot field/property on the TravelButtonUI instance
                try
                {
                    var fd = uiType.GetField("dialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (fd != null)
                    {
                        var go = fd.GetValue(ui) as UnityEngine.GameObject;
                        if (go != null)
                        {
                            TBLog.Info("RebuildTravelDialog: destroying dialogRoot GameObject on TravelButtonUI.");
                            UnityEngine.Object.Destroy(go);
                            fd.SetValue(ui, null);
                        }
                    }
                    else
                    {
                        var prop = uiType.GetProperty("dialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (prop != null)
                        {
                            var go = prop.GetValue(ui) as UnityEngine.GameObject;
                            if (go != null)
                            {
                                TBLog.Info("RebuildTravelDialog: destroying dialogRoot (property) on TravelButtonUI.");
                                UnityEngine.Object.Destroy(go);
                                try { prop.SetValue(ui, null); } catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("RebuildTravelDialog: clearing dialogRoot field/property failed: " + ex.Message);
                }

                // 2) Try to invoke a known rebuild/open method on the UI (best-effort)
                string[] openMethodNames = new[] { "BuildDialog", "BuildDialogContents", "OpenDialog", "ShowDialog", "Open", "Show" };
                foreach (var name in openMethodNames)
                {
                    try
                    {
                        var m = uiType.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (m != null && m.GetParameters().Length == 0)
                        {
                            TBLog.Info($"RebuildTravelDialog: invoking '{name}' on TravelButtonUI.");
                            m.Invoke(ui, null);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"RebuildTravelDialog: invoking {name} failed: " + ex.Message);
                    }
                }

                // 3) If no open method found, try to destroy any GameObject named "TravelDialog" so next Open recreates it
                try
                {
                    var go = UnityEngine.GameObject.Find("TravelDialog");
                    if (go != null)
                    {
                        TBLog.Info("RebuildTravelDialog: destroying GameObject named 'TravelDialog'.");
                        UnityEngine.Object.Destroy(go);
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("RebuildTravelDialog: destroy fallback failed: " + ex.Message);
                }

                return;
            }

            // If TravelButtonUI instance is not present, fall back to global heuristics

            // 1) Try common dialog object names
            string[] dialogNames = new[] { "TravelDialog", "TravelButtonDialog", "TravelButton_Dialog", "TravelDialogCanvas" };
            foreach (var n in dialogNames)
            {
                try
                {
                    var go = UnityEngine.GameObject.Find(n);
                    if (go != null)
                    {
                        TBLog.Info($"RebuildTravelDialog: destroying dialog GameObject '{n}'.");
                        UnityEngine.Object.Destroy(go);
                    }
                }
                catch { /* ignore */ }
            }

            // 2) As a last resort, try to find a GameObject that looks like the dialog by searching for a Button named "Teleport"
            try
            {
                var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
                foreach (var b in allButtons)
                {
                    if (b == null || string.IsNullOrEmpty(b.name)) continue;
                    if (b.name.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var root = b.transform.root?.gameObject;
                        if (root != null)
                        {
                            TBLog.Info($"RebuildTravelDialog: destroying root GameObject for button '{b.name}' -> '{root.name}'.");
                            UnityEngine.Object.Destroy(root);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("RebuildTravelDialog: last-resort search failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("RebuildTravelDialog: unexpected error: " + ex.Message);
        }
    }

    /// <summary>Register a Button instance for the given City so refresh/update code can find it later.</summary>
    public static void RegisterCityButton(TravelButton.City city, UnityEngine.UI.Button btn)
    {
        if (city == null || string.IsNullOrEmpty(city.name) || btn == null) return;
        try
        {
            lock (s_cityButtonLock)
            {
                s_cityButtonMap[city.name] = btn;
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("RegisterCityButton failed: " + ex.Message);
        }
    }

    /// <summary>Remove a single city's button registration (safe no-op if not present).</summary>
    public static void UnregisterCityButton(string cityName)
    {
        if (string.IsNullOrEmpty(cityName)) return;
        try
        {
            lock (s_cityButtonLock)
            {
                s_cityButtonMap.Remove(cityName);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("UnregisterCityButton failed: " + ex.Message);
        }
    }

    /// <summary>Clear all registered city -> button mappings (call when dialog destroyed).</summary>
    public static void UnregisterCityButtons()
    {
        try
        {
            lock (s_cityButtonLock)
            {
                s_cityButtonMap.Clear();
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("UnregisterCityButtons failed: " + ex.Message);
        }
    }

    /// <summary>Try to return the registered Button for a city name. Returns true if found.</summary>
    public static bool TryGetRegisteredButton(string cityName, out UnityEngine.UI.Button btn)
    {
        btn = null;
        if (string.IsNullOrEmpty(cityName)) return false;
        try
        {
            lock (s_cityButtonLock)
            {
                if (s_cityButtonMap.TryGetValue(cityName, out btn))
                {
                    // ensure the button hasn't been destroyed
                    if (btn == null || btn.gameObject == null)
                    {
                        s_cityButtonMap.Remove(cityName);
                        btn = null;
                        return false;
                    }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryGetRegisteredButton failed: " + ex.Message);
        }
        return false;
    }

    // Best-effort player position probe used for debug logging
    private bool TryGetPlayerPosition(out Vector3 outPos)
    {
        outPos = Vector3.zero;
        try
        {
            try
            {
                var go = GameObject.FindWithTag("Player");
                if (go != null && go.transform != null) { outPos = go.transform.position; return true; }
            }
            catch { }

            try
            {
                if (Camera.main != null) { outPos = Camera.main.transform.position; return true; }
            }
            catch { }

            try
            {
                var go2 = GameObject.Find("Player");
                if (go2 != null && go2.transform != null) { outPos = go2.transform.position; return true; }
            }
            catch { }
        }
        catch { }
        return false;
    }

    // Modified TryTeleportThenCharge: call CheckChargePossibleAndRefund at start,
    // and stop using TeleportManager.EnsureInstance(); instead use TeleportManager.Instance (no creation).
    // Teleportation logic otherwise left unchanged.
    // Replace TryTeleportThenCharge with this implementation.
    // Call StartCoroutine(TryTeleportThenCharge(city, cost)) where needed.
    private IEnumerator TryTeleportThenCharge(TravelButton.City city, int cost)
    {
        float entryTime = Time.realtimeSinceStartup;
        LogCityConfig(city?.name);
        TBLog.Info($"TryTeleportThenCharge: Enter (t={entryTime:F3}) city='{city?.name ?? "<null>"}' cost={cost}");

        if (city == null)
        {
            TBLog.Warn("TryTeleportThenCharge: city is null.");
            isTeleporting = false;
            yield break;
        }

        if (isTeleporting)
        {
            TBLog.Info("TryTeleportThenCharge: teleport already in progress, ignoring request.");
            yield break;
        }

        // Pre-check (no yields inside this try/catch)
        try
        {
            bool canPay = CurrencyHelpers.CheckChargePossibleAndRefund(cost);
            TBLog.Info($"TryTeleportThenCharge: CheckChargePossibleAndRefund returned {canPay} for cost={cost}");
            if (!canPay)
            {
                TBLog.Info($"TryTeleportThenCharge: player cannot pay {cost} silver for '{city.name}'. Aborting.");
                try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Not enough silver for teleport."); } catch { }
                isTeleporting = false;
                yield break;
            }
        }
        catch (Exception exCheck)
        {
            TBLog.Warn("TryTeleportThenCharge: CheckChargePossibleAndRefund threw: " + exCheck);
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: could not verify currency."); } catch { }
            isTeleporting = false;
            yield break;
        }

        isTeleporting = true;
        TBLog.Info("TryTeleportThenCharge: isTeleporting set = true");

        // Debug logging (no yields)
        try
        {
            bool enabledByConfig = TravelButton.IsCityEnabled(city.name);
            bool visitedInHistory = false;
            try { visitedInHistory = TravelButton.HasPlayerVisited(city); } catch (Exception ex) { visitedInHistory = false; TBLog.Warn("HasPlayerVisited failed: " + ex); }

            long currentMoney = GetPlayerCurrencyAmountOrMinusOne();
            bool haveMoneyInfo = currentMoney >= 0;
            bool hasEnoughMoney = haveMoneyInfo ? currentMoney >= cost : true;
            bool coordsAvailable = !string.IsNullOrEmpty(city.targetGameObjectName) || (city.coords != null && city.coords.Length >= 3);
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool targetSceneSpecified = !string.IsNullOrEmpty(city.sceneName);
            bool isCurrentScene = targetSceneSpecified && string.Equals(city.sceneName, activeScene.name, StringComparison.OrdinalIgnoreCase);
            Vector3 playerPos;
            bool havePlayerPos = TryGetPlayerPosition(out playerPos);

            TBLog.Info($"Debug Teleport '{city.name}': enabledByConfig={enabledByConfig}, visitedInHistory={visitedInHistory}, hasEnoughMoney={hasEnoughMoney}, coordsAvailable={coordsAvailable}, isCurrentScene={isCurrentScene}, playerPos={(havePlayerPos ? $"({playerPos.x:F1},{playerPos.y:F1},{playerPos.z:F1})" : "unknown")}, currentMoney={currentMoney}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenCharge debug logging failed: " + ex);
        }

        // Determine coords/anchor availability (no yields)
        Vector3 coordsHint = Vector3.zero;
        bool haveCoordsHint = false;
        bool haveTargetGameObject = false;
        bool targetGameObjectFound = false;
        bool isFaded = false;
        /*        try
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
                            TBLog.Info($"TryTeleportThenCharge: Found target GameObject '{city.targetGameObjectName}' at {coordsHint} - will prefer anchor.");
                        }
                        else
                        {
                            TBLog.Info($"TryTeleportThenCharge: targetGameObjectName '{city.targetGameObjectName}' provided, but GameObject not found in scene.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TryTeleportThenCharge: error checking targetGameObjectName: " + ex);
                }
        */
        if (!haveCoordsHint && city.coords != null && city.coords.Length >= 3)
        {
            coordsHint = new Vector3(city.coords[0], city.coords[1], city.coords[2]);
            haveCoordsHint = true;
            TBLog.Info($"TryTeleportThenCharge: using explicit coords from config for {city.name}: {coordsHint}");
            if (!IsCoordsReasonable(coordsHint))
            {
                TBLog.Warn($"TryTeleportThenCharge: explicit coords {coordsHint} look suspicious for city '{city.name}'. Verify travel_config.json contains correct world coords.");
            }
        }

        // Guess sceneName if missing (no yields)
        if (string.IsNullOrEmpty(city.sceneName))
        {
            try
            {
                var guessed = GuessSceneNameFromBuildSettings(city.name);
                if (!string.IsNullOrEmpty(guessed))
                {
                    TBLog.Info($"TryTeleportThenCharge: guessed sceneName='{guessed}' from build settings for city '{city.name}'");
                    city.sceneName = guessed;
                }
                else
                {
                    TBLog.Info("TryTeleportThenCharge: GuessSceneNameFromBuildSettings returned empty");
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryTeleportThenCharge: GuessSceneNameFromBuildSettings failed: " + ex);
            }
        }

        TBLog.Info($"TryTeleportThenCharge: city='{city.name}', haveTargetGameObject={haveTargetGameObject}, targetGameObjectFound={targetGameObjectFound}, haveCoordsHint={haveCoordsHint}, coordsHint={coordsHint}, sceneName='{city.sceneName}'");

        var activeScene2 = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bool targetSceneSpecified2 = !string.IsNullOrEmpty(city.sceneName);
        bool sceneMatches = !targetSceneSpecified2 || string.Equals(city.sceneName, activeScene2.name, StringComparison.OrdinalIgnoreCase);
        const float blackHoldSeconds = 1.5f;

        TBLog.Info($"TryTeleportThenCharge: activeScene='{activeScene2.name}', targetSceneSpecified={targetSceneSpecified2}, sceneMatches={sceneMatches}");

        // ---------- Fast path: same-scene StartTeleport ----------
        if (haveCoordsHint && sceneMatches)
        {
            TBLog.Info("TryTeleportThenCharge: taking FAST same-scene teleport path");
            try { DisableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons disabled (fast path)"); } catch (Exception ex) { TBLog.Warn("DisableDialogButtons threw: " + ex); }

            // Fade out (yield - outside any try/catch)
            TBLog.Info("TryTeleportThenCharge: starting screen fade out (fast path)");
            yield return TravelDialog.ScreenFade(0f, 1f, 0.35f);
            isFaded = true;
            TBLog.Info("TryTeleportThenCharge: screen faded out (fast path)");

            // Prefer EnsureInstance and then use tm consistently
            TeleportManager tm = null;
            try { tm = TeleportManager.EnsureInstance(); TBLog.Info("TryTeleportThenCharge: TeleportManager.EnsureInstance() called"); }
            catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: EnsureInstance threw: " + ex); tm = TeleportManager.Instance; }

            TBLog.Info($"TryTeleportThenCharge: tm resolved: {(tm != null ? "non-null" : "null")}");
            if (tm != null)
            {
                try { TBLog.Info($"TryTeleportThenCharge: tm info - instance? {(TeleportManager.Instance != null ? "Instance exists" : "Instance null")}"); } catch { }
            }

            if (tm == null)
            {
                TBLog.Warn("TryTeleportThenCharge: TeleportManager not available; aborting and restoring UI.");
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (fast abort)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                // close dialog & inventory on abort
                CloseDialogAndInventory_Safe();
                yield break;
            }

            // Subscribe to OnTeleportFinished using tm (no yields)
            bool subscribeOk = false;
            bool finished2 = false;
            bool success2 = false;
            Action<bool> onFinished = (s) =>
            {
                try
                {
                    TBLog.Info($"TryTeleportThenCharge: OnTeleportFinished callback invoked (fast path) success={s}");
                }
                catch { }
                success2 = s;
                finished2 = true;
            };

            try
            {
                tm.OnTeleportFinished += onFinished;
                subscribeOk = true;
                TBLog.Info("TryTeleportThenCharge: subscribed to tm.OnTeleportFinished (fast path)");
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryTeleportThenCharge: subscribe OnTeleportFinished failed: " + ex);
                subscribeOk = false;
            }

            if (!subscribeOk)
            {
                TBLog.Warn("TryTeleportThenCharge: subscription failed; restoring UI (fast path)");
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (subscribe fail)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                CloseDialogAndInventory_Safe();
                yield break;
            }

            // compute a corrected placement coordinate synchronously
            Vector3 correctedCoords;
            Vector3 correctedCoordsFinal;
            Vector3 groundedCoords = TeleportHelpers.GetGroundedPosition(coordsHint);
            TBLog.Info($"TryTeleportThenCharge: computing safe placement coords based on coordsHint={coordsHint}");
            bool coordsOk = TeleportManagerSafePlace.ComputeSafePlacementCoords(this, coordsHint, true, out correctedCoords);
            if (!coordsOk)
            {
                // fallback to grounded coords if compute failed
                correctedCoordsFinal = groundedCoords;
                TBLog.Info($"ComputeSafePlacementCoords: failed – using groundedCoords={correctedCoordsFinal}");
            }
            else
            {
                correctedCoordsFinal = correctedCoords;
                TBLog.Info($"ComputeSafePlacementCoords: ok -> correctedCoordsFinal={correctedCoordsFinal}, groundedCoords={groundedCoords}");
            }

            // Start teleport request (no yields in this try)
            bool accepted = false;
            try
            {
                TBLog.Info($"TryTeleportThenCharge: calling tm.StartTeleport(activeScene='{activeScene2.name}', target='{city.targetGameObjectName}', correctedCoordsFinal={correctedCoordsFinal}, haveCoordsHint={haveCoordsHint}, cost={cost})");
                accepted = tm.StartTeleport(activeScene2.name, city.targetGameObjectName, correctedCoordsFinal, haveCoordsHint, cost);
                TBLog.Info($"TryTeleportThenCharge: tm.StartTeleport returned accepted={accepted}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryTeleportThenCharge: StartTeleport threw: " + ex);
                accepted = false;
            }

            if (!accepted)
            {
                TBLog.Warn("TryTeleportThenCharge: StartTeleport rejected the request.");
                try { if (tm != null) tm.OnTeleportFinished -= onFinished; TBLog.Info("TryTeleportThenCharge: unsubscribed OnTeleportFinished after StartTeleport rejection"); } catch { }
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (start rejected)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                CloseDialogAndInventory_Safe();
                yield break;
            }

            // WAIT FOR COMPLETION (yield outside try/catch)
            TBLog.Info("TryTeleportThenCharge: waiting for OnTeleportFinished callback (fast path)");
            float waitStartFast = Time.realtimeSinceStartup;
            while (!finished2) yield return null;
            float waitEndFast = Time.realtimeSinceStartup;
            TBLog.Info($"TryTeleportThenCharge: OnTeleportFinished detected (fast path) after {(waitEndFast - waitStartFast):F3}s, success={success2}");

            // Unsubscribe using tm
            try { if (tm != null) tm.OnTeleportFinished -= onFinished; TBLog.Info("TryTeleportThenCharge: unsubscribed tm.OnTeleportFinished (fast path)"); } catch (Exception ex) { TBLog.Warn("Unsubscribe OnTeleportFinished threw: " + ex); }

            if (success2)
            {
                try
                {
                    bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
                    TBLog.Info($"TryTeleportThenCharge: AttemptDeductSilverDirect returned {charged} (fast path)");
                    if (!charged)
                    {
                        TBLog.Warn($"TryTeleportThenCharge: Teleported to {city.name} but failed to deduct {cost} silver.");
                        ShowInlineDialogMessage($"Teleported to {city.name} (failed to charge {cost} {TravelButton.cfgCurrencyItem.Value})");
                    }
                    else
                    {
                        ShowInlineDialogMessage($"Teleported to {city.name}");
                    }
                }
                catch (Exception exCharge)
                {
                    TBLog.Warn("TryTeleportThenCharge: charge attempt threw: " + exCharge);
                    ShowInlineDialogMessage($"Teleported to {city.name} (charge error)");
                }

                try { TravelButton.OnSuccessfulTeleport(city.name); TBLog.Info("TryTeleportThenCharge: OnSuccessfulTeleport invoked (fast path)"); } catch { }
                try { TravelButton.PersistCitiesToPluginFolder(); TBLog.Info("TryTeleportThenCharge: PersistCitiesToPluginFolder invoked (fast path)"); } catch (Exception ex) { TBLog.Warn("Persist after teleport failed: " + ex); }
            }
            else
            {
                TBLog.Warn($"TryTeleportThenCharge: teleport to '{city.name}' failed (TeleportManager fast path).");
                ShowInlineDialogMessage("Teleport failed");
            }

            // Restore UI; close dialog & inventory now that teleport finished (success or fail)
            try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (fast path end)"); } catch { }
            CloseDialogAndInventory_Safe();

            isTeleporting = false;
            isFaded = false;
            yield return new WaitForSecondsRealtime(blackHoldSeconds);
            yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
            // immediately restore UI/menu state + input focus
            try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
            yield break;
        }

        // ---------- Scene-load path ----------
        if (targetSceneSpecified2 && !sceneMatches)
        {
            TBLog.Info("TryTeleportThenCharge: taking SCENE-LOAD path");
            try { DisableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons disabled (scene-load path)"); } catch (Exception ex) { TBLog.Warn("DisableDialogButtons threw: " + ex); }

            TBLog.Info("TryTeleportThenCharge: starting screen fade out (scene-load)");
            yield return TravelDialog.ScreenFade(0f, 1f, 0.35f);
            isFaded = true;
            TBLog.Info("TryTeleportThenCharge: screen faded out (scene-load)");

            TeleportManager tm = null;
            try { tm = TeleportManager.EnsureInstance(); TBLog.Info("TryTeleportThenCharge: TeleportManager.EnsureInstance() called (scene-load)"); }
            catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: EnsureInstance threw: " + ex); tm = TeleportManager.Instance; }

            TBLog.Info($"TryTeleportThenCharge: tm resolved (scene-load): {(tm != null ? "non-null" : "null")}");
            if (tm == null)
            {
                TBLog.Warn("TryTeleportThenCharge: TeleportManager not available for scene-load; aborting.");
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (tm null)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                CloseDialogAndInventory_Safe();
                yield break;
            }

            bool finishedLoad = false;
            bool loadSuccess = false;

            // compute a corrected placement coordinate synchronously
            // compute a corrected placement coordinate synchronously
            Vector3 correctedCoords;
            Vector3 correctedCoordsFinal;
            Vector3 groundedCoords = TeleportHelpers.GetGroundedPosition(coordsHint);
            TBLog.Info($"TryTeleportThenCharge: computing safe placement coords based on coordsHint={coordsHint}");
            bool coordsOk = TeleportManagerSafePlace.ComputeSafePlacementCoords(this, coordsHint, true, out correctedCoords);
            if (!coordsOk)
            {
                // fallback to grounded coords if compute failed
                correctedCoordsFinal = groundedCoords;
                TBLog.Info($"ComputeSafePlacementCoords: failed – using groundedCoords={correctedCoordsFinal}");
            }
            else
            {
                correctedCoordsFinal = correctedCoords;
                TBLog.Info($"ComputeSafePlacementCoords: ok -> correctedCoordsFinal={correctedCoordsFinal}, groundedCoords={groundedCoords}");
            }

            // use correctedCoords from here on when starting the teleport/placement
            TBLog.Info($"TryTeleportThenCharge: calling tm.StartSceneLoad(scene='{city.sceneName}', correctedCoords={correctedCoordsFinal})");
            bool acceptedLoad = false;
            try
            {
                acceptedLoad = tm.StartSceneLoad(city.sceneName, correctedCoordsFinal, (loadedScene, asyncOp, ok) =>
                {
                    try { TBLog.Info($"TryTeleportThenCharge: StartSceneLoad callback invoked for scene='{city.sceneName}' ok={ok}, loadedScene.name={(loadedScene != null ? loadedScene.name : "<null>")}"); } catch { }
                    loadSuccess = ok;
                    finishedLoad = true;
                    if (!ok) TBLog.Warn($"TryTeleportThenCharge: scene '{city.sceneName}' failed to load.");
                });
                TBLog.Info($"TryTeleportThenCharge: tm.StartSceneLoad returned acceptedLoad={acceptedLoad}");
            }
            catch (Exception ex)
            {
                TBLog.Warn("TryTeleportThenCharge: StartSceneLoad threw: " + ex);
                acceptedLoad = false;
            }

            if (!acceptedLoad)
            {
                TBLog.Warn("TryTeleportThenCharge: StartSceneLoad rejected the request.");
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (start load rejected)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                CloseDialogAndInventory_Safe();
                yield break;
            }

            // Wait for load completion (yield here, outside the try/catch that started the load)
            float loadWaitStart = Time.realtimeSinceStartup;
            float deadline = loadWaitStart + 30f;
            TBLog.Info($"TryTeleportThenCharge: waiting up to 30s for scene load callback (tstart={loadWaitStart:F3})");
            while (!finishedLoad && Time.realtimeSinceStartup < deadline)
                yield return null;
            float loadWaitEnd = Time.realtimeSinceStartup;
            TBLog.Info($"TryTeleportThenCharge: scene load wait finished (elapsed={(loadWaitEnd - loadWaitStart):F3}s) finishedLoad={finishedLoad} loadSuccess={loadSuccess}");

            if (!finishedLoad)
            {
                TBLog.Warn("TryTeleportThenCharge: scene load timed out.");
                ShowInlineDialogMessage("Teleport failed (timeout)");
                // keep screen black for a short hold so user sees the failure state
                yield return new WaitForSecondsRealtime(blackHoldSeconds);
                CloseDialogAndInventory_Safe();
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (load timeout)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                yield break;
            }

            if (!loadSuccess)
            {
                TBLog.Warn("TryTeleportThenCharge: scene load reported failure.");
                ShowInlineDialogMessage("Teleport failed");
                yield return new WaitForSecondsRealtime(blackHoldSeconds);
                CloseDialogAndInventory_Safe();
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (load failed)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                yield break;
            }

            TBLog.Info("TryTeleportThenCharge: scene loaded successfully - performing placement attempt using AttemptTeleportToPositionSafe");

            bool placed = false;
            try
            {
                placed = TravelButtonUI.AttemptTeleportToPositionSafe(correctedCoordsFinal);
                TBLog.Info($"TryTeleportThenCharge: AttemptTeleportToPositionSafe returned placed={placed} for correctedCoords={correctedCoordsFinal}");
            }
            catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: AttemptTeleportToPositionSafe threw: " + ex); placed = false; }

            if (!placed)
            {
                TBLog.Warn("TryTeleportThenCharge: placement after load failed.");
                ShowInlineDialogMessage("Teleport failed");
                yield return new WaitForSecondsRealtime(blackHoldSeconds);
                CloseDialogAndInventory_Safe();
                yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
                // immediately restore UI/menu state + input focus
                try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
                try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (placement failed)"); } catch { }
                isTeleporting = false;
                isFaded = false;
                yield break;
            }

            try
            {
                bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
                TBLog.Info($"TryTeleportThenCharge: AttemptDeductSilverDirect returned {charged} (scene-load path)");
                if (!charged)
                {
                    TBLog.Warn($"TryTeleportThenCharge: Teleported to {city.name} but failed to deduct {cost} silver.");
                    ShowInlineDialogMessage($"Teleported to {city.name} (failed to charge {cost} {TravelButton.cfgCurrencyItem.Value})");
                }
                else
                {
                    ShowInlineDialogMessage($"Teleported to {city.name}");
                }
            }
            catch (Exception exCharge)
            {
                TBLog.Warn("TryTeleportThenCharge: charge attempt threw after scene-load placement: " + exCharge);
                ShowInlineDialogMessage($"Teleported to {city.name} (charge error)");
            }

            try { TravelButton.OnSuccessfulTeleport(city.name); TBLog.Info("TryTeleportThenCharge: OnSuccessfulTeleport invoked (scene-load path)"); } catch { }
            try { TravelButton.PersistCitiesToPluginFolder(); TBLog.Info("TryTeleportThenCharge: PersistCitiesToPluginFolder invoked (scene-load path)"); } catch (Exception ex) { TBLog.Warn("Persist after scene-load teleport failed: " + ex); }

            // close dialog & inventory on success
            CloseDialogAndInventory_Safe();

            try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (scene-load path end)"); } catch { }
            isTeleporting = false;
            isFaded = false;
            yield return new WaitForSecondsRealtime(blackHoldSeconds);
            yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
            // immediately restore UI/menu state + input focus
            try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
            yield break;
        }

        // ---------- Fallback helper ----------
        TBLog.Info("TryTeleportThenCharge: taking FALLBACK helper path");
        bool finished = false;
        bool success = false;
        bool startHelperFailed = false;

        try
        {
            TeleportHelpersBehaviour helper = UnityEngine.Object.FindObjectOfType<TeleportHelpersBehaviour>();
            if (helper == null)
            {
                var go = new GameObject("TeleportHelpersHost");
                UnityEngine.Object.DontDestroyOnLoad(go);
                helper = go.AddComponent<TeleportHelpersBehaviour>();
                TBLog.Info("TryTeleportThenCharge: TeleportHelpersBehaviour created for fallback helper");
            }
            else
            {
                TBLog.Info("TryTeleportThenCharge: TeleportHelpersBehaviour found for fallback helper: " + helper.name);
            }

            helper.StartCoroutine(helper.EnsureSceneAndTeleport(city, coordsHint, haveCoordsHint, ok =>
            {
                try { TBLog.Info($"TryTeleportThenCharge: helper.EnsureSceneAndTeleport callback invoked ok={ok}"); } catch { }
                success = ok;
                finished = true;
            }));
            TBLog.Info("TryTeleportThenCharge: helper.EnsureSceneAndTeleport coroutine started");
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogError("TryTeleportThenCharge exception starting helper: " + ex);
            try { EnableDialogButtons(); } catch { }
            isTeleporting = false;
            startHelperFailed = true;
        }

        if (startHelperFailed)
        {
            try { EnableDialogButtons(); } catch { }
            isTeleporting = false;
            isFaded = false;
            TBLog.Info($"TryTeleportThenCharge: holding black screen for {blackHoldSeconds:F3}s before fade-in (helper failed start)");
            yield return new WaitForSecondsRealtime(blackHoldSeconds);
            CloseDialogAndInventory_Safe();
            yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
            // immediately restore UI/menu state + input focus
            try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
            yield break;
        }

        float helperStartTime = Time.realtimeSinceStartup;
        float helperDeadline = helperStartTime + 30f;
        TBLog.Info($"TryTeleportThenCharge: waiting up to 30s for helper to finish (tstart={helperStartTime:F3})");
        while (!finished && Time.realtimeSinceStartup < helperDeadline)
            yield return null;
        TBLog.Info($"TryTeleportThenCharge: helper wait ended after {(Time.realtimeSinceStartup - helperStartTime):F3}s finished={finished} success={success}");

        if (!finished)
        {
            TBLog.Warn("TryTeleportThenCharge: helper timed out.");
            ShowInlineDialogMessage("Teleport failed (timeout)");
            yield return new WaitForSecondsRealtime(blackHoldSeconds);
            CloseDialogAndInventory_Safe();
            yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
            // immediately restore UI/menu state + input focus
            try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
            try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (helper timeout)"); } catch { }
            isTeleporting = false;
            isFaded = false;
            yield break;
        }

        if (success)
        {
            TBLog.Info("TryTeleportThenCharge: helper reported success - performing post-success actions");
            try { TravelButton.OnSuccessfulTeleport(city.name); TBLog.Info("TryTeleportThenCharge: OnSuccessfulTeleport invoked (helper)"); } catch { }
            try
            {
                bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
                TBLog.Info($"TryTeleportThenCharge: AttemptDeductSilverDirect returned {charged} (helper path)");
                if (!charged)
                {
                    TBLog.Warn($"TryTeleportThenCharge: Teleported to {city.name} but failed to deduct {cost} silver.");
                    ShowInlineDialogMessage($"Teleported to {city.name} (failed to charge {cost} {TravelButton.cfgCurrencyItem.Value})");
                }
                else
                {
                    ShowInlineDialogMessage($"Teleported to {city.name}");
                }
            }
            catch (Exception exCharge)
            {
                TBLog.Warn("TryTeleportThenCharge: charge attempt threw: " + exCharge);
                ShowInlineDialogMessage($"Teleported to {city.name} (charge error)");
            }

            try { TravelButton.PersistCitiesToPluginFolder(); TBLog.Info("TryTeleportThenCharge: PersistCitiesToPluginFolder invoked (helper)"); } catch { }

            // close dialog & inventory on success
            CloseDialogAndInventory_Safe();
        }
        else
        {
            TBLog.Warn($"TryTeleportThenCharge: teleport to '{city.name}' failed (helper).");
            ShowInlineDialogMessage("Teleport failed");
            // close dialog & inventory on failure
            CloseDialogAndInventory_Safe();
        }

        try { EnableDialogButtons(); TBLog.Info("TryTeleportThenCharge: dialog buttons re-enabled (fallback end)"); } catch { }
        isTeleporting = false;
        isFaded = false;
        TBLog.Info("TryTeleportThenCharge: finishing and fading in UI");
        yield return new WaitForSecondsRealtime(blackHoldSeconds);
        yield return TravelDialog.ScreenFade(1f, 0f, 0.35f);
        // immediately restore UI/menu state + input focus
        try { RestoreDialogAndInventory_Safe(); } catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe invocation threw: " + ex); }
        yield break;
    }

    /// <summary>
    /// Robust replacement for the old reflection-based compatibility path.
    /// - Extracts sceneName/target/coords/price from the provided cityObj (best-effort).
    /// - Charges player immediately (using existing currency helper).
    /// - Calls TeleportManager.StartTeleport with explicit parameters (no fragile inner reflection).
    /// - Disables dialog UI while TeleportManager runs and re-enables it when OnTeleportFinished fires.
    /// 
    /// Replace your existing TryTeleportThenCharge / wrapper StartCoroutine(...) call with:
    ///   StartCoroutine(TryTeleportThenCharge(cityObj));
    /// or adapt to your call-site signature.
    /// 
    /// NOTE: This file intentionally uses the same helper names seen in your logs:
    ///   - CurrencyHelpers.AttemptDeductSilverDirect (simulate/real deduction)
    ///   - TryRefundPlayerCurrency (refund helper)
    ///   - TravelButtonPlugin.ShowPlayerNotification (notify player)
    ///   - TBLog (logging)
    /// If your project uses different helper names, rename the calls accordingly.
    /// </summary>
    public IEnumerator TryTeleportThenCharge(object cityObj)
    {
        // --- 1) Extract metadata safely via reflection ---
        string sceneName = null;
        string targetGameObjectName = null;
        Vector3 coordsHint = Vector3.zero;
        bool haveCoordsHint = false;
        int price = 1; // default price if not specified

        try
        {
            if (cityObj != null)
            {
                var t = cityObj.GetType();

                // sceneName
                try
                {
                    var p = t.GetProperty("sceneName", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("SceneName", BindingFlags.Public | BindingFlags.Instance);
                    if (p != null) sceneName = p.GetValue(cityObj) as string;
                }
                catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: reading sceneName threw: " + ex); }

                // targetGameObjectName
                try
                {
                    var p = t.GetProperty("targetGameObjectName", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("TargetGameObjectName", BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("target", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
                    if (p != null) targetGameObjectName = p.GetValue(cityObj) as string;
                }
                catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: reading targetGameObjectName threw: " + ex); }

                // price / cost
                try
                {
                    var p = t.GetProperty("price", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("Price", BindingFlags.Public | BindingFlags.Instance);
                    if (p != null)
                    {
                        var v = p.GetValue(cityObj);
                        if (v != null) price = Convert.ToInt32(v);
                    }
                }
                catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: reading price threw: " + ex); }

                // coords
                try
                {
                    var p = t.GetProperty("coords", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("Coords", BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("position", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                          ?? t.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);

                    if (p != null)
                    {
                        var val = p.GetValue(cityObj);
                        if (val is float[] farr && farr.Length >= 3)
                        {
                            coordsHint = new Vector3(farr[0], farr[1], farr[2]);
                            haveCoordsHint = true;
                        }
                        else if (val is double[] darr && darr.Length >= 3)
                        {
                            coordsHint = new Vector3((float)darr[0], (float)darr[1], (float)darr[2]);
                            haveCoordsHint = true;
                        }
                        else if (val is IList<object> listObj && listObj.Count >= 3)
                        {
                            try
                            {
                                coordsHint = new Vector3(Convert.ToSingle(listObj[0]), Convert.ToSingle(listObj[1]), Convert.ToSingle(listObj[2]));
                                haveCoordsHint = true;
                            }
                            catch { }
                        }
                        else if (val is IList<float> lf && lf.Count >= 3)
                        {
                            coordsHint = new Vector3(lf[0], lf[1], lf[2]);
                            haveCoordsHint = true;
                        }
                    }
                }
                catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: reading coords threw: " + ex); }
            }
        }
        catch (Exception exAll)
        {
            TBLog.Warn("TryTeleportThenCharge: unexpected reflection error: " + exAll);
        }

        // If there is a well-known "name" on the object and sceneName is empty, try to lookup by name in TravelButton data.
        if (string.IsNullOrEmpty(sceneName))
        {
            try
            {
                string candidateName = null;
                if (cityObj is string s) candidateName = s;
                else
                {
                    var np = cityObj?.GetType().GetProperty("name", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                              ?? cityObj?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (np != null) candidateName = np.GetValue(cityObj) as string;
                }

                if (!string.IsNullOrEmpty(candidateName))
                {
                    // attempt to find matching entry in TravelButton.Cities if present
                    try
                    {
                        var travelType = typeof(TravelButton);
                        var citiesProp = travelType.GetProperty("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        object citiesObj = null;
                        if (citiesProp != null) citiesObj = citiesProp.GetValue(null);
                        else
                        {
                            var citiesField = travelType.GetField("Cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                               ?? travelType.GetField("cities", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            if (citiesField != null) citiesObj = citiesField.GetValue(null);
                        }

                        if (citiesObj is System.Collections.IEnumerable en)
                        {
                            foreach (var entry in en)
                            {
                                if (entry == null) continue;
                                string entryName = null;
                                try
                                {
                                    var np = entry.GetType().GetProperty("name", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                                              ?? entry.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                                    if (np != null) entryName = np.GetValue(entry) as string;
                                }
                                catch { }

                                if (string.IsNullOrEmpty(entryName)) continue;
                                if (!string.Equals(entryName, candidateName, StringComparison.OrdinalIgnoreCase)) continue;

                                // extract sceneName and additional fields from the city entry
                                try { sceneName = (entry.GetType().GetProperty("sceneName", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ?? entry.GetType().GetProperty("SceneName"))?.GetValue(entry) as string; } catch { }
                                try { targetGameObjectName = (entry.GetType().GetProperty("targetGameObjectName", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ?? entry.GetType().GetProperty("TargetGameObjectName"))?.GetValue(entry) as string; } catch { }
                                try
                                {
                                    var coordsP = entry.GetType().GetProperty("coords", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ?? entry.GetType().GetProperty("Coords");
                                    if (coordsP != null)
                                    {
                                        var val = coordsP.GetValue(entry);
                                        if (val is float[] farr && farr.Length >= 3)
                                        {
                                            coordsHint = new Vector3(farr[0], farr[1], farr[2]);
                                            haveCoordsHint = true;
                                        }
                                    }
                                }
                                catch { }

                                try
                                {
                                    var priceP = entry.GetType().GetProperty("price", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance) ?? entry.GetType().GetProperty("Price");
                                    if (priceP != null)
                                    {
                                        var val = priceP.GetValue(entry);
                                        if (val != null) price = Convert.ToInt32(val);
                                    }
                                }
                                catch { }

                                if (!string.IsNullOrEmpty(sceneName)) break;
                            }
                        }
                    }
                    catch (Exception exLookup)
                    {
                        TBLog.Warn("TryTeleportThenCharge: lookup via TravelButton.Cities failed: " + exLookup);
                    }
                }
            }
            catch { /* ignore */ }
        }

        // --- 2) Validate destination before disabling UI or charging permanently ---
        if (string.IsNullOrEmpty(sceneName))
        {
            TBLog.Warn("TryTeleportThenCharge: destination scene not configured for the provided city/object; aborting teleport.");
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        TBLog.Info($"TryTeleportThenCharge: preparing teleport to scene='{sceneName}', target='{targetGameObjectName}', coords={coordsHint} haveCoords={haveCoordsHint} price={price}");

        // --- 3) Charge the player (real deduction) ---
        bool charged = false;
        try
        {
            // AttemptDeductSilverDirect is used in previous code to attempt deduction.
            // First param: amount, second param: "simulate" flag; false means real deduction in our earlier examples.
            charged = CurrencyHelpers.AttemptDeductSilverDirect(price, false);
            if (!charged)
            {
                TBLog.Info($"TryTeleportThenCharge: payment of {price} silver failed (insufficient funds).");
                try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Not enough silver for teleport."); } catch { }
                yield break;
            }
            TBLog.Info($"TryTeleportThenCharge: charged {price} silver.");
        }
        catch (Exception exCharge)
        {
            TBLog.Warn("TryTeleportThenCharge: exception while charging: " + exCharge);
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: unable to charge."); } catch { }
            yield break;
        }

        // If payment succeeded, ensure TeleportManager exists and request the teleport.
        try
        {
            if (TeleportManager.Instance == null)
            {
                var go = new GameObject("TeleportManagerHost");
                go.AddComponent<TeleportManager>();
                TBLog.Info("TryTeleportThenCharge: created TeleportManager host GameObject.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenCharge: could not create TeleportManager: " + ex);
            // refund
            try { TryRefundPlayerCurrency(price); } catch { }
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: internal error."); } catch { }
            yield break;
        }

        bool started = false;
        try
        {
            started = TeleportManager.Instance.StartTeleport(sceneName, targetGameObjectName, coordsHint, haveCoordsHint, price);
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenCharge: StartTeleport threw: " + ex);
            // refund
            try { TryRefundPlayerCurrency(price); } catch { }
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: internal error."); } catch { }
            yield break;
        }

        if (!started)
        {
            TBLog.Info("TryTeleportThenCharge: TeleportManager rejected the request (another transition probably running). Refunding.");
            try { TryRefundPlayerCurrency(price); } catch { }
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport is already in progress. Please wait."); } catch { }
            yield break;
        }

        // --- 4) Transition started: disable dialog UI and subscribe to OnTeleportFinished to re-enable + handle refund on failure if necessary ---
        try { DisableDialogButtons(); } catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: DisableDialogButtons threw: " + ex); }

        System.Action<bool> onFinished = null;
        onFinished = (success) =>
        {
            try
            {
                if (!success)
                {
                    // teleport failed: refund user (best-effort)
                    try { TryRefundPlayerCurrency(price); } catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: refund threw: " + ex); }
                    try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed; your payment was refunded."); } catch { }
                }

                // re-enable the UI
                try { EnableDialogButtons(); } catch (Exception ex) { TBLog.Warn("TryTeleportThenCharge: EnableDialogButtons threw: " + ex); }
            }
            finally
            {
                try { TeleportManager.Instance.OnTeleportFinished -= onFinished; } catch { }
            }
        };

        try
        {
            TeleportManager.Instance.OnTeleportFinished += onFinished;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenCharge: subscribing to OnTeleportFinished failed: " + ex);
            // best-effort re-enable and refund
            try { EnableDialogButtons(); } catch { }
            try { TryRefundPlayerCurrency(price); } catch { }
        }

        // Non-blocking: the TeleportManager is handling the rest.
        yield break;
    }

    // Helper to call StartCoroutine and return the IEnumerator that can be yielded.
    // Wrapping StartCoroutine in a method avoids putting a yield inside a try/catch in the caller.
    private IEnumerator StartCoroutineWrapper(IEnumerator enumerator)
    {
        // Start the coroutine and also yield its execution back to caller
        // Note: StartCoroutine returns a Coroutine, but yielding the enumerator sequence is correct
        // so we simply yield return the enumerator.
        yield return StartCoroutine(enumerator);
    }

    // Best-effort public wrappers so other code can call EnableDialogButtons/DisableDialogButtons.
    // Adapt to call your real methods if they exist; if your class already has methods with
    // these names remove these definitions.
    public void DisableDialogButtons()
    {
        try
        {
            if (!TrySetDialogRootActive(false))
            {
                var go = GameObject.Find("TravelDialogCanvas");
                if (go != null) { go.SetActive(false); return; }
                TBLog.Warn("DisableDialogButtons: could not find dialogRoot or TravelDialogCanvas to disable.");
            }
        }
        catch (Exception ex) { TBLog.Warn("DisableDialogButtons: " + ex); }
    }

    public void EnableDialogButtons()
    {
        try
        {
            if (!TrySetDialogRootActive(true))
            {
                var go = GameObject.Find("TravelDialogCanvas");
                if (go != null) { go.SetActive(true); return; }
                TBLog.Warn("EnableDialogButtons: could not find dialogRoot or TravelDialogCanvas to enable.");
            }
        }
        catch (Exception ex) { TBLog.Warn("EnableDialogButtons: " + ex); }
    }

    // Tries to find a GameObject field/property named like dialogRoot and set it active/inactive.
    // Returns true if we found and toggled something.
    private bool TrySetDialogRootActive(bool active)
    {
        try
        {
            Type t = this.GetType();
            string[] names = new[] { "dialogRoot", "DialogRoot", "dialogRootField", "dialogRootObj", "dialog", "dialogRootGameObject" };
            foreach (var n in names)
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null)
                {
                    var val = f.GetValue(this) as GameObject;
                    if (val != null) { val.SetActive(active); return true; }
                }

                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (p != null)
                {
                    var val = p.GetValue(this) as GameObject;
                    if (val != null) { val.SetActive(active); return true; }
                }
            }

            // Try to find TravelDialogCanvas under this GameObject
            var child = transform.Find("TravelDialogCanvas");
            if (child != null && child.gameObject != null) { child.gameObject.SetActive(active); return true; }
        }
        catch { /* swallow - helper should be best-effort */ }

        return false;
    }

    // Add this static helper into the TravelButtonMod class (paste with other static helpers).
    // It attempts multiple safe strategies to find and close/hide the open travel dialog.
    public static void CloseOpenTravelDialog()
    {
        try
        {
            TBLog.Info("CloseOpenTravelDialog: attempting to close travel dialog.");

            // 1) Prefer a TravelButtonUI instance if present and try known API / fields
            try
            {
                var ui = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
                if (ui != null)
                {
                    var uiType = ui.GetType();

                    // Try method names that might close or hide the dialog
                    string[] closeMethodNames = new[] { "CloseDialog", "Close", "HideDialog", "Hide", "Dismiss" };
                    foreach (var name in closeMethodNames)
                    {
                        try
                        {
                            var m = uiType.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (m != null && m.GetParameters().Length == 0)
                            {
                                TBLog.Info($"CloseOpenTravelDialog: invoking TravelButtonUI.{name}()");
                                m.Invoke(ui, null);
                                return;
                            }
                        }
                        catch { /* ignore and try next */ }
                    }

                    // Try to find a dialogRoot field/property and deactivate it
                    try
                    {
                        var fd = uiType.GetField("dialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (fd != null)
                        {
                            var root = fd.GetValue(ui) as GameObject;
                            if (root != null)
                            {
                                TBLog.Info("CloseOpenTravelDialog: disabling dialogRoot field on TravelButtonUI.");
                                root.SetActive(false);
                                try { fd.SetValue(ui, null); } catch { }
                                return;
                            }
                        }
                    }
                    catch { /* ignore */ }

                    try
                    {
                        var prop = uiType.GetProperty("dialogRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (prop != null)
                        {
                            var root = prop.GetValue(ui) as GameObject;
                            if (root != null)
                            {
                                TBLog.Info("CloseOpenTravelDialog: disabling dialogRoot property on TravelButtonUI.");
                                root.SetActive(false);
                                try { prop.SetValue(ui, null); } catch { }
                                return;
                            }
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("CloseOpenTravelDialog: TravelButtonUI-based close attempt failed: " + ex.Message);
            }

            // 2) Common dialog GameObject names (fallback)
            string[] dialogNames = new[] { "TravelDialog", "TravelButtonDialog", "TravelButton_Dialog", "TravelDialogCanvas", "TravelButton_Global" };
            foreach (var n in dialogNames)
            {
                try
                {
                    var go = GameObject.Find(n);
                    if (go != null)
                    {
                        TBLog.Info($"CloseOpenTravelDialog: found GameObject '{n}', disabling it.");
                        go.SetActive(false);
                        return;
                    }
                }
                catch { /* ignore */ }
            }

            // 3) Heuristic: find any GameObject with a Button named like "Teleport" or "Close" near it and disable its root
            try
            {
                var allButtons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
                foreach (var b in allButtons)
                {
                    if (b == null || string.IsNullOrEmpty(b.name)) continue;
                    var nm = b.name.ToLowerInvariant();
                    if (nm.Contains("teleport") || nm.Contains("travel") || nm.Contains("close"))
                    {
                        var root = b.transform.root?.gameObject;
                        if (root != null)
                        {
                            TBLog.Info($"CloseOpenTravelDialog: heuristic disabling root '{root.name}' for button '{b.name}'.");
                            root.SetActive(false);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("CloseOpenTravelDialog: heuristic search failed: " + ex.Message);
            }

            // 4) As last resort, attempt to find any GameObject that contains a child Text with the title string
            try
            {
                var allTexts = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Text>();
                foreach (var t in allTexts)
                {
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (t.text.IndexOf("Select destination", StringComparison.OrdinalIgnoreCase) >= 0
                        || t.text.IndexOf("Select destination", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var root = t.transform.root?.gameObject;
                        if (root != null)
                        {
                            TBLog.Info($"CloseOpenTravelDialog: disabling root '{root.name}' found via title text match.");
                            root.SetActive(false);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("CloseOpenTravelDialog: last-resort text search failed: " + ex.Message);
            }

            TBLog.Info("CloseOpenTravelDialog: no dialog found to close.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("CloseOpenTravelDialog: unexpected error: " + ex.Message);
        }
    }

    // ---- Reflection helpers ----
    private static string TryGetStringFieldOrProp(object obj, string[] candidateNames)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        foreach (var n in candidateNames)
        {
            try
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    if (v != null) return v.ToString();
                }
                var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead)
                {
                    var v = p.GetValue(obj, null);
                    if (v != null) return v.ToString();
                }
            }
            catch { }
        }
        return null;
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
                TBLog.Info("DumpDetectedPositionsForActiveScene: active scene invalid, skipping.");
                return;
            }

            if (TravelButton.Cities == null || TravelButton.Cities.Count == 0)
            {
                TBLog.Info("DumpDetectedPositionsForActiveScene: no cities configured, skipping.");
                return;
            }

            TBLog.Info($"DumpDetectedPositionsForActiveScene: scanning scene '{scene.name}' for candidate anchors...");

            // Prepare map of cityName -> list of positions
            var detected = new Dictionary<string, List<Vector3>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in TravelButton.Cities)
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
                foreach (var city in TravelButton.Cities)
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
                            TBLog.Info($"DumpDetectedPositionsForActiveScene: matched '{objName}' -> city '{city.name}' pos=({tr.position.x:F3},{tr.position.y:F3},{tr.position.z:F3})");
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
                //                File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
                TBLog.Info($"DumpDetectedPositionsForActiveScene: wrote detected positions for scene '{scene.name}' to '{outPath}'");
            }
            catch (Exception exWrite)
            {
                TBLog.Warn("DumpDetectedPositionsForActiveScene: failed to write file: " + exWrite.Message);
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpDetectedPositionsForActiveScene: unexpected error: " + ex);
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
                        TBLog.Info($"GuessSceneNameFromBuildSettings: matched build-scene '{file}' (path='{path}') for city '{cityName}'.");
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
                            TBLog.Info($"GuessSceneNameFromBuildSettings: matched variant '{v}' -> build-scene '{file}' for city '{cityName}'.");
                            return file;
                        }
                    }
                }
                catch { /* ignore individual index errors */ }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("GuessSceneNameFromBuildSettings exception: " + ex);
        }
        return null;
    }

    // Helper: more robust visited detection with fallbacks.
    // Returns true if any reasonable indicator suggests the player has visited the city.
    public static bool IsCityVisitedFallback(TravelButton.City city)
    {
        try
        {
            if (city == null) return false;

            // Primary: the official visited tracker by city name
            try
            {
                if (VisitedTracker.HasVisited(city.name))
                {
                    TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: VisitedTracker.HasVisited(city.name) => true for '{city.name}' (detection only; not mutating state)");
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
                        TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: VisitedTracker.HasVisited(sceneName) => true for '{city.sceneName}' (city='{city.name}') (detection only; not mutating state)");
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
                        TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: target GameObject '{city.targetGameObjectName}' found -> treat '{city.name}' as visited (detection only; not mutating state).");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: GameObject.Find('{city.targetGameObjectName}') threw: {ex}");
                }
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
                        TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: found scene transform '{t.name}' containing city name '{city.name}' -> treat as visited (detection only; not mutating state).");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonPlugin.LogDebug($"IsCityVisitedFallback: scanning transforms threw: {ex}");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("IsCityVisitedFallback exception: " + ex);
        }

        return false;
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
    public static void LogCityConfig(string cityName)
    {
        try
        {
            var c = TravelButton.Cities?.Find(x => string.Equals(x.name, cityName, StringComparison.OrdinalIgnoreCase));
            if (c == null)
            {
                TBLog.Info($"LogCityConfig: city '{cityName}' not found in in-memory Cities.");
                return;
            }
            TBLog.Info($"LogCityConfig: '{c.name}' scene='{c.sceneName ?? "(null)"}' target='{c.targetGameObjectName ?? "(null)"}' coords={(c.coords != null ? $"[{string.Join(", ", c.coords)}]" : "(null)")} price={(c.price.HasValue ? c.price.Value.ToString() : "(global)")}");
        }
        catch (Exception ex)
        {
            TBLog.Warn("LogCityConfig exception: " + ex);
        }
    }

    // In TeleportHelpers static class - update AttemptTeleportToPositionSafe or the method you use to teleport
    // AttemptTeleportToPositionSafe + helper TryFindNearestNavMeshOrGround
    [Obsolete("Use TeleportManager.SafePlacePlayerCoroutine or TravelButtonUI.SafeTeleportRoutine instead. This synchronous method will be removed in a future version.")]
    public static bool AttemptTeleportToPositionSafe(Vector3 target)
    {
        try
        {
            // Ensure we reset the flag at start; we'll set it to true only on success.
            _lastAttemptedTeleportSucceeded = false;

            var initialRoot = FindPlayerRoot();
            if (initialRoot == null)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: player root not found.");
                return false;
            }

            // Resolve the actual GameObject that should be moved
            var moveGO = TeleportHelpers.ResolveActualPlayerGameObject(initialRoot) ?? initialRoot;

            // Prefer authoritative root if we accidentally selected a camera child
            try
            {
                GameObject rootCandidate = null;
                try { rootCandidate = moveGO.transform.root != null ? moveGO.transform.root.gameObject : null; } catch { rootCandidate = null; }

                bool switched = false;
                if (rootCandidate != null && rootCandidate != moveGO)
                {
                    if (rootCandidate.name != null && rootCandidate.name.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                    {
                        moveGO = rootCandidate;
                        switched = true;
                    }
                    else
                    {
                        var charType = ReflectionUtils.SafeGetType("Character, Assembly-CSharp") ?? ReflectionUtils.SafeGetType("Character");
                        if (charType != null)
                        {
                            try
                            {
                                var comp = moveGO.GetComponent(charType);
                                var rootComp = rootCandidate.GetComponent(charType);
                                if (rootComp != null && comp == null)
                                {
                                    moveGO = rootCandidate;
                                    switched = true;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (switched)
                    TBLog.Info($"AttemptTeleportToPositionSafe: preferred root '{moveGO.name}' for movement (was camera/child).");
            }
            catch { /* ignore */ }

            // Diagnostics: hierarchy and player candidates
            string parentName = "(null)";
            try { parentName = moveGO.transform.parent != null ? moveGO.transform.parent.name : "(null)"; } catch { parentName = "(unknown)"; }
            string rootName = "(unknown)";
            try { rootName = moveGO.transform.root != null ? moveGO.transform.root.name : "(unknown)"; } catch { }
            TBLog.Info($"AttemptTeleportToPositionSafe: moving object '{moveGO.name}' (instance id={moveGO.GetInstanceID()}) parent='{parentName}' root='{rootName}'");
            TBLog.Info($"  localPos={moveGO.transform.localPosition}, worldPos={moveGO.transform.position}");

            try
            {
                var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
                foreach (var t in allTransforms)
                {
                    if (t == null) continue;
                    var n = t.name ?? "";
                    if (n.StartsWith("PlayerChar", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = "(null)";
                        try { p = t.parent != null ? t.parent.name : "(null)"; } catch { p = "(unknown)"; }
                        TBLog.Info($"  Detected PlayerChar candidate: '{t.name}' pos={t.position} parent='{p}'");
                    }
                }
            }
            catch { /* ignore diagnostics errors */ }

            var before = moveGO.transform.position;
            TBLog.Info($"AttemptTeleportToPositionSafe: BEFORE pos = ({before.x:F3}, {before.y:F3}, {before.z:F3})");

            // --- Helper: Extended search for safe ground positions (tries NavMesh, vertical scans, grid search and spawn anchors) ---
            bool TryExtendedGroundSearch(Vector3 rawTarget, out Vector3 found)
            {
                found = rawTarget;

                try
                {
                    // 1) Try NavMesh sampling (if navmesh module exists)
                    try
                    {
                        var navType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule") ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh");
                        var navHitType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit, UnityEngine.AIModule") ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit");
                        if (navType != null && navHitType != null)
                        {
                            // Use reflection to call NavMesh.SamplePosition if present
                            var sampleMethod = navType.GetMethod("SamplePosition", new Type[] { typeof(Vector3), navHitType, typeof(float), typeof(int) });
                            if (sampleMethod != null)
                            {
                                object navHit = Activator.CreateInstance(navHitType);
                                float[] searchRadii = new float[] { 5f, 15f, 50f };
                                foreach (var r in searchRadii)
                                {
                                    try
                                    {
                                        var args = new object[] { rawTarget, navHit, r, -1 };
                                        var ok = (bool)sampleMethod.Invoke(null, args);
                                        if (ok)
                                        {
                                            // navHit.position property
                                            var posProp = navHitType.GetProperty("position");
                                            if (posProp != null)
                                            {
                                                var pos = (Vector3)posProp.GetValue(navHit);
                                                found = pos;
                                                TBLog.Info($"TryExtendedGroundSearch: NavMesh.SamplePosition succeeded at {pos} (radius {r}).");
                                                return true;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }

                    // 2) Vertical ray scans (up/down) with increasing offset
                    int maxSteps = 60; // up to ~60 meters above/below
                    float step = 1.0f;
                    LayerMask mask = ~0;
                    for (int d = 0; d <= maxSteps; d++)
                    {
                        float offset = d * step;
                        // probe from above
                        try
                        {
                            RaycastHit hit;
                            Vector3 originUp = new Vector3(rawTarget.x, rawTarget.y + 200f + offset, rawTarget.z);
                            if (Physics.Raycast(originUp, Vector3.down, out hit, 400f + offset, mask, QueryTriggerInteraction.Ignore))
                            {
                                found = new Vector3(rawTarget.x, hit.point.y + TeleportHelpers.TeleportGroundClearance, rawTarget.z);
                                TBLog.Info($"TryExtendedGroundSearch: Vertical scan (up origin +{offset}) hit y={hit.point.y:F3}, returning {found}.");
                                return true;
                            }
                        }
                        catch { }

                        // probe from below
                        try
                        {
                            RaycastHit hit;
                            Vector3 originDown = new Vector3(rawTarget.x, rawTarget.y - 200f - offset, rawTarget.z);
                            if (Physics.Raycast(originDown, Vector3.up, out hit, 400f + offset, mask, QueryTriggerInteraction.Ignore))
                            {
                                found = new Vector3(rawTarget.x, hit.point.y + TeleportHelpers.TeleportGroundClearance, rawTarget.z);
                                TBLog.Info($"TryExtendedGroundSearch: Vertical scan (down origin -{offset}) hit y={hit.point.y:F3}, returning {found}.");
                                return true;
                            }
                        }
                        catch { }
                    }

                    // 3) small horizontal grid NavMesh/raycast search
                    float[] gridOffsets = new float[] { 0f, 1f, 2f, 4f, 6f, 8f };
                    foreach (var dx in gridOffsets)
                    {
                        foreach (var dz in gridOffsets)
                        {
                            foreach (var sx in new float[] { -1f, 1f })
                                foreach (var sz in new float[] { -1f, 1f })
                                {
                                    Vector3 cand = new Vector3(rawTarget.x + sx * dx, rawTarget.y, rawTarget.z + sz * dz);
                                    try
                                    {
                                        RaycastHit hit;
                                        Vector3 origin = new Vector3(cand.x, cand.y + 200f, cand.z);
                                        if (Physics.Raycast(origin, Vector3.down, out hit, 400f, mask, QueryTriggerInteraction.Ignore))
                                        {
                                            found = new Vector3(cand.x, hit.point.y + TeleportHelpers.TeleportGroundClearance, cand.z);
                                            TBLog.Info($"TryExtendedGroundSearch: Grid scan hit at XZ=({cand.x:F1},{cand.z:F1}) y={hit.point.y:F3} -> {found}");
                                            return true;
                                        }
                                    }
                                    catch { }
                                }
                        }
                    }

                    // 4) Try to find common spawn anchors in the scene and use their position
                    string[] spawnNames = new[] { "PlayerSpawn", "PlayerSpawnPoint", "PlayerStart", "StartPosition", "SpawnPoint", "Spawn", "PlayerStartPoint", "Anchor" };
                    foreach (var name in spawnNames)
                    {
                        try
                        {
                            var go = GameObject.Find(name);
                            if (go != null)
                            {
                                found = go.transform.position;
                                TBLog.Info($"TryExtendedGroundSearch: found scene anchor '{name}' at {found}. Using as fallback.");
                                return true;
                            }
                        }
                        catch { }
                    }

                    // 5) As last measure, search transforms for likely spawn-like root names
                    try
                    {
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                        {
                            if (t == null) continue;
                            var n = t.name ?? "";
                            if (n.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                found = t.position;
                                TBLog.Info($"TryExtendedGroundSearch: heuristic anchor '{n}' at {found} selected as fallback.");
                                return true;
                            }
                        }
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("TryExtendedGroundSearch: unexpected error: " + ex.Message);
                }

                return false;
            }

            // --- Permutation / NavMesh/Ground probe to pick a safe target ---
            try
            {
                if (TeleportHelpers.TryPickBestCoordsPermutation(target, out Vector3 permSafe))
                {
                    TBLog.Info($"AttemptTeleportToPositionSafe: TryPickBestCoordsPermutation selected {permSafe} (original {target}).");
                    target = permSafe;
                }
                else
                {
                    // fallback: at least try normal probe on original
                    if (TryFindNearestNavMeshOrGround(target, out Vector3 safeFinal, navSearchRadius: 15f, maxGroundRay: 400f))
                    {
                        TBLog.Info($"AttemptTeleportToPositionSafe: Using safe position {safeFinal} (was {target})");
                        target = safeFinal;
                    }
                    else
                    {
                        TBLog.Info($"AttemptTeleportToPositionSafe: initial NavMesh/ground probe failed for {target}. Will try extended search later if needed.");
                    }
                }
            }
            catch (Exception exSafe)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: TryPickBestCoordsPermutation/TryFindNearestNavMeshOrGround threw: " + exSafe.Message);
            }

            // --- Safety: prevent huge vertical jumps (reject or conservative-ground) ---
            try
            {
                const float maxVerticalDelta = 100f;   // adjust to taste (meters)
                float extraGroundClearance = TeleportHelpers.TeleportGroundClearance;

                float verticalDelta = Mathf.Abs(target.y - before.y);
                if (verticalDelta > maxVerticalDelta)
                {
                    TBLog.Warn($"AttemptTeleportToPositionSafe: rejected target.y {target.y:F3} because it differs from current player.y {before.y:F3} by {verticalDelta:F3}m (max {maxVerticalDelta}). Trying conservative grounding...");

                    try
                    {
                        // First quick conservative raycast from above relative to player's Y
                        RaycastHit hit;
                        Vector3 origin = new Vector3(target.x, before.y + 200f, target.z); // raycast from relative height
                        if (Physics.Raycast(origin, Vector3.down, out hit, 400f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            float candidateY = hit.point.y + extraGroundClearance;
                            float candDelta = Mathf.Abs(candidateY - before.y);
                            if (candDelta <= maxVerticalDelta)
                            {
                                TBLog.Info($"AttemptTeleportToPositionSafe: conservative grounding succeeded: hit.y={hit.point.y:F3}, using y={candidateY:F3} (delta {candDelta:F3}).");
                                target = new Vector3(target.x, candidateY, target.z);
                            }
                            else
                            {
                                TBLog.Warn($"AttemptTeleportToPositionSafe: grounding hit at y={hit.point.y:F3} but delta {candDelta:F3} still > maxVerticalDelta. Will attempt extended ground search.");
                                if (TryExtendedGroundSearch(target, out Vector3 ext))
                                {
                                    float extDelta = Mathf.Abs(ext.y - before.y);
                                    if (extDelta <= maxVerticalDelta)
                                    {
                                        TBLog.Info($"AttemptTeleportToPositionSafe: extended ground search provided {ext} (delta {extDelta:F3}). Using it.");
                                        target = ext;
                                    }
                                    else
                                    {
                                        TBLog.Warn($"AttemptTeleportToPositionSafe: extended-ground candidate delta {extDelta:F3} still > maxVerticalDelta. Aborting teleport.");
                                        return false;
                                    }
                                }
                                else
                                {
                                    TBLog.Warn("AttemptTeleportToPositionSafe: extended ground search failed. Aborting teleport to avoid sending player into sky.");
                                    return false;
                                }
                            }
                        }
                        else
                        {
                            TBLog.Warn("AttemptTeleportToPositionSafe: conservative grounding found NO hit. Attempting extended ground search before aborting.");
                            if (TryExtendedGroundSearch(target, out Vector3 ext2))
                            {
                                float extDelta2 = Mathf.Abs(ext2.y - before.y);
                                if (extDelta2 <= maxVerticalDelta)
                                {
                                    TBLog.Info($"AttemptTeleportToPositionSafe: extended ground search provided {ext2} (delta {extDelta2:F3}). Using it.");
                                    target = ext2;
                                }
                                else
                                {
                                    TBLog.Warn($"AttemptTeleportToPositionSafe: extended-ground candidate delta {extDelta2:F3} still > maxVerticalDelta. Aborting teleport.");
                                    return false;
                                }
                            }
                            else
                            {
                                TBLog.Warn("AttemptTeleportToPositionSafe: extended ground search failed. Aborting teleport to avoid sending player into sky.");
                                return false;
                            }
                        }
                    }
                    catch (Exception exGround)
                    {
                        TBLog.Warn("AttemptTeleportToPositionSafe: conservative grounding failed: " + exGround);
                        return false;
                    }
                }
            }
            catch (Exception exSafety)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: safety-check exception: " + exSafety);
                return false;
            }

            // Zero any rigidbody velocities (child rigidbody)
            try
            {
                var rb0 = moveGO.GetComponentInChildren<Rigidbody>(true);
                if (rb0 != null)
                {
                    rb0.velocity = Vector3.zero;
                    rb0.angularVelocity = Vector3.zero;
                }
            }
            catch { }

            // --- Detect NavMeshAgent (reflection) and temporarily disable updates so agent won't fight the warp ---
            object navAgentObj = null;
            Type navAgentType = null;
            try
            {
                navAgentType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshAgent, UnityEngine.AIModule") ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshAgent");
                if (navAgentType != null)
                {
                    var getAgent = typeof(GameObject).GetMethod("GetComponentInChildren", new Type[] { typeof(Type), typeof(bool) });
                    if (getAgent != null)
                    {
                        try { navAgentObj = getAgent.Invoke(moveGO, new object[] { navAgentType, true }); } catch { navAgentObj = null; }
                    }
                    else
                    {
                        try
                        {
                            var objs = UnityEngine.Object.FindObjectsOfType(navAgentType);
                            foreach (var o in objs)
                            {
                                var comp = o as Component;
                                if (comp != null && comp.gameObject != null && comp.gameObject.transform.IsChildOf(moveGO.transform))
                                {
                                    navAgentObj = comp;
                                    break;
                                }
                            }
                        }
                        catch { navAgentObj = null; }
                    }

                    if (navAgentObj != null)
                    {
                        try
                        {
                            var updatePosProp = navAgentType.GetProperty("updatePosition");
                            var updateRotProp = navAgentType.GetProperty("updateRotation");
                            if (updatePosProp != null && updatePosProp.CanWrite) updatePosProp.SetValue(navAgentObj, false);
                            if (updateRotProp != null && updateRotProp.CanWrite) updateRotProp.SetValue(navAgentObj, false);
                            TBLog.Info("AttemptTeleportToPositionSafe: Temporarily disabled NavMeshAgent.updatePosition/updateRotation.");
                        }
                        catch { }
                    }
                }
            }
            catch { navAgentObj = null; navAgentType = null; }

            // --- Suspend likely movement scripts and make child rigidbodies kinematic ---
            var suspendPatterns = new string[]
            {
        "LocalCharacterControl","AdvancedMover","CharacterFastTraveling","RigidbodySuspender",
        "CharacterResting","CharacterMovement","PlayerMovement","PlayerController","Movement","Motor","AI"
            };

            var disabledBehaviours = new List<Behaviour>();
            var changedRigidbodies = new List<(Rigidbody rb, bool originalIsKinematic)>();

            try
            {
                var comps = moveGO.GetComponentsInChildren<Component>(true);
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    try
                    {
                        if (c is Rigidbody rb)
                        {
                            try
                            {
                                changedRigidbodies.Add((rb, rb.isKinematic));
                                rb.isKinematic = true;
                            }
                            catch { }
                        }

                        if (c is Behaviour b)
                        {
                            var tname = b.GetType().Name ?? "";
                            foreach (var p in suspendPatterns)
                            {
                                if (tname.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try
                                    {
                                        if (b.enabled)
                                        {
                                            b.enabled = false;
                                            disabledBehaviours.Add(b);
                                        }
                                    }
                                    catch { }
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception exSuspend)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: error while suspending components: " + exSuspend.Message);
            }

            // --- Perform the move (prefer NavMeshAgent.Warp if available) ---
            bool moveSucceeded = false;
            try
            {
                if (navAgentObj != null && navAgentType != null)
                {
                    try
                    {
                        var warpMethod = navAgentType.GetMethod("Warp", new Type[] { typeof(Vector3) });
                        if (warpMethod != null)
                        {
                            TBLog.Info($"AttemptTeleportToPositionSafe: warping NavMeshAgent to {target}.");
                            var warped = (bool)warpMethod.Invoke(navAgentObj, new object[] { target });
                            moveSucceeded = warped;
                            try { moveGO.transform.position = target; } catch { }
                        }
                        else
                        {
                            moveGO.transform.position = target;
                            moveSucceeded = true;
                        }
                    }
                    catch (Exception exWarp)
                    {
                        TBLog.Warn("AttemptTeleportToPositionSafe: NavMeshAgent.Warp failed: " + exWarp.Message);
                        try { moveGO.transform.position = target; moveSucceeded = true; } catch { moveSucceeded = false; }
                    }
                    finally
                    {
                        try
                        {
                            var updatePosProp = navAgentType.GetProperty("updatePosition");
                            var updateRotProp = navAgentType.GetProperty("updateRotation");
                            if (updatePosProp != null && updatePosProp.CanWrite) updatePosProp.SetValue(navAgentObj, true);
                            if (updateRotProp != null && updateRotProp.CanWrite) updateRotProp.SetValue(navAgentObj, true);
                        }
                        catch { }
                    }
                }
                else
                {
                    var localCC = moveGO.GetComponent<CharacterController>();
                    if (localCC != null)
                    {
                        try
                        {
                            localCC.enabled = false;
                            moveGO.transform.position = target;
                            localCC.enabled = true;
                            moveSucceeded = true;
                            TBLog.Info("AttemptTeleportToPositionSafe: Teleported using CharacterController on moved GameObject.");
                        }
                        catch (Exception exCC)
                        {
                            TBLog.Warn("AttemptTeleportToPositionSafe: CharacterController move failed: " + exCC.Message);
                            try { moveGO.transform.position = target; moveSucceeded = true; } catch { moveSucceeded = false; }
                        }
                    }
                    else
                    {
                        try
                        {
                            moveGO.transform.position = target;
                            moveSucceeded = true;
                        }
                        catch (Exception exMove)
                        {
                            TBLog.Warn("AttemptTeleportToPositionSafe: direct transform set failed: " + exMove.Message);
                            moveSucceeded = false;
                        }
                    }
                }
            }
            catch (Exception exAll)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: unexpected move exception: " + exAll.Message);
                moveSucceeded = false;
            }

            // --- Ground-first + overlap-safety (raycast then small raises) ---
            try
            {
                Vector3 FindGroundY(Vector3 samplePos, float startAbove = 200f, float maxDistance = 400f, float clearance = 0.5f)
                {
                    try
                    {
                        RaycastHit hit;
                        Vector3 origin = new Vector3(samplePos.x, samplePos.y + startAbove, samplePos.z);
                        if (Physics.Raycast(origin, Vector3.down, out hit, maxDistance, ~0, QueryTriggerInteraction.Ignore))
                        {
                            Vector3 g = new Vector3(samplePos.x, hit.point.y + clearance, samplePos.z);
                            TBLog.Info($"AttemptTeleportToPositionSafe: Ground raycast hit at y={hit.point.y:F3} for XZ=({samplePos.x:F3},{samplePos.z:F3}), returning grounded pos {g}");
                            return g;
                        }
                        else
                        {
                            TBLog.Info($"AttemptTeleportToPositionSafe: Ground raycast found NO hit for XZ=({samplePos.x:F3},{samplePos.z:F3}) (origin Y={samplePos.y + startAbove:F3}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn("AttemptTeleportToPositionSafe: Ground raycast failed: " + ex.Message);
                    }
                    return samplePos; // fallback
                }

                try { Physics.SyncTransforms(); } catch { }

                const float overlapCheckRadius = 0.4f;
                const float raiseStep = 0.25f;
                const float maxRaiseFallback = 2.0f;
                const float maxAllowedRaise = 12.0f;

                bool CheckOverlapsAt(Vector3 pos)
                {
                    try
                    {
                        Collider[] cols = Physics.OverlapSphere(pos, overlapCheckRadius, ~0, QueryTriggerInteraction.Ignore);
                        if (cols != null && cols.Length > 0)
                        {
                            foreach (var c in cols)
                            {
                                if (c == null) continue;
                                if (c.isTrigger) continue;
                                if (c.transform.IsChildOf(moveGO.transform)) continue;
                                return true;
                            }
                        }
                    }
                    catch { }
                    return false;
                }

                var after = moveGO.transform.position;
                var grounded = FindGroundY(after, startAbove: 200f, maxDistance: 400f, clearance: 0.5f);

                bool usedGrounding = false;
                if (!Mathf.Approximately(grounded.y, after.y))
                {
                    float deltaY = grounded.y - after.y;
                    if (Mathf.Abs(deltaY) <= maxAllowedRaise)
                    {
                        try
                        {
                            moveGO.transform.position = grounded;
                            try { Physics.SyncTransforms(); } catch { }
                            TBLog.Info($"AttemptTeleportToPositionSafe: Applied grounding: moved from y={after.y:F3} to grounded y={grounded.y:F3}");
                            usedGrounding = true;
                            after = moveGO.transform.position;
                        }
                        catch (Exception exG) { TBLog.Warn("AttemptTeleportToPositionSafe: failed to apply grounding: " + exG.Message); }
                    }
                    else
                    {
                        TBLog.Warn($"AttemptTeleportToPositionSafe: grounding would move by {deltaY:F3}m which exceeds maxAllowedRaise={maxAllowedRaise}. Skipping auto-grounding.");
                    }
                }
                else
                {
                    TBLog.Info("AttemptTeleportToPositionSafe: grounding did not change Y (no reliable hit).");
                }

                // overlap checking and conservative raising
                after = moveGO.transform.position;
                bool isOverlapping = CheckOverlapsAt(after);
                if (isOverlapping)
                {
                    TBLog.Warn($"AttemptTeleportToPositionSafe: detected overlap at pos {after}. usedGrounding={usedGrounding}. Attempting incremental raise.");
                    bool foundFree = false;
                    float maxRaise = usedGrounding ? 3.0f : maxRaiseFallback;
                    for (float raise = raiseStep; raise <= maxRaise; raise += raiseStep)
                    {
                        Vector3 check = new Vector3(after.x, after.y + raise, after.z);
                        if (!CheckOverlapsAt(check))
                        {
                            try
                            {
                                moveGO.transform.position = check;
                                try { Physics.SyncTransforms(); } catch { }
                            }
                            catch { }
                            TBLog.Info($"AttemptTeleportToPositionSafe: raised player by {raise:F2}m to avoid overlap -> {check}");
                            foundFree = true;
                            after = check;
                            break;
                        }
                    }
                    if (!foundFree)
                    {
                        TBLog.Warn($"AttemptTeleportToPositionSafe: could not find non-overlapping spot within {maxRaise}m above {after}. Player may still be embedded or in open air.");
                    }
                }

                // Clamp extreme heights relative to requested target
                after = moveGO.transform.position;
                float allowedDeltaFromTarget = 20.0f;
                if (Mathf.Abs(after.y - target.y) > allowedDeltaFromTarget)
                {
                    Vector3 clampPos = new Vector3(after.x, target.y + 1.0f, after.z);
                    try
                    {
                        moveGO.transform.position = clampPos;
                        try { Physics.SyncTransforms(); } catch { }
                        TBLog.Warn($"AttemptTeleportToPositionSafe: final pos was {after.y:F3} which is >{allowedDeltaFromTarget}m from target.y={target.y:F3}; clamped to {clampPos}.");
                        after = moveGO.transform.position;
                    }
                    catch { }
                }

                TBLog.Info($"AttemptTeleportToPositionSafe: final verified pos after grounding/overlap checks = ({after.x:F3}, {after.y:F3}, {after.z:F3})");
            }
            catch (Exception exOverlap)
            {
                TBLog.Warn("AttemptTeleportToPositionSafe: grounding/overlap-safety check failed: " + exOverlap.Message);
            }

            // Start monitoring coroutine (logs if anything moves the object after teleport)
            try
            {
                var host = TeleportHelpersBehaviour.GetOrCreateHost();
                host.StartCoroutine(host.WatchPositionAfterTeleport(moveGO, moveGO.transform.position, 2.0f));
                // re-enable suspended components and restore rigidbody kinematic flags after short delay
                host.StartCoroutine(host.ReenableComponentsAfterDelay(moveGO, disabledBehaviours, changedRigidbodies, 0.4f));
            }
            catch { }

            // Mark success/failure flag so callers can react after coroutine returns
            _lastAttemptedTeleportSucceeded = true;

            TBLog.Info($"AttemptTeleportToPositionSafe: completed teleport (moveSucceeded={moveSucceeded}).");

            return true;
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptTeleportToPositionSafe: exception: " + ex.Message);
            _lastAttemptedTeleportSucceeded = false;
            return false;
        }
    }


    // Helper: find safe nearby position using NavMesh.SamplePosition (reflection) or grounding raycast, fallback to small raises.
    // returns true + outPos when found safe candidate.
    public static bool TryFindNearestNavMeshOrGround(Vector3 desired, out Vector3 outPos, float navSearchRadius = 10f, float maxGroundRay = 400f)
    {
        outPos = desired;
        try
        {
            // 1) NavMesh.SamplePosition (reflection-safe)
            try
            {
                var navMeshType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule")
                                  ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMesh");
                var navMeshHitType = ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit, UnityEngine.AIModule")
                                  ?? ReflectionUtils.SafeGetType("UnityEngine.AI.NavMeshHit");
                if (navMeshType != null && navMeshHitType != null)
                {
                    var sampleMethod = navMeshType.GetMethod("SamplePosition", new Type[] { typeof(Vector3), navMeshHitType, typeof(float), typeof(int) });
                    if (sampleMethod != null)
                    {
                        object hitBox = Activator.CreateInstance(navMeshHitType);
                        object[] args = new object[] { desired, hitBox, navSearchRadius, -1 };
                        bool found = false;
                        try { found = (bool)sampleMethod.Invoke(null, args); } catch { found = false; }
                        if (found)
                        {
                            var posField = navMeshHitType.GetField("position");
                            if (posField != null)
                            {
                                outPos = (Vector3)posField.GetValue(args[1]);
                                TBLog.Info($"TryFindNearestNavMeshOrGround: NavMesh.SamplePosition found {outPos} (radius={navSearchRadius}).");
                                return true;
                            }
                            var posProp = navMeshHitType.GetProperty("position");
                            if (posProp != null)
                            {
                                outPos = (Vector3)posProp.GetValue(args[1], null);
                                TBLog.Info($"TryFindNearestNavMeshOrGround: NavMesh.SamplePosition found {outPos} (radius={navSearchRadius}).");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception exNav)
            {
                TBLog.Info("TryFindNearestNavMeshOrGround: NavMesh probe failed or not present: " + exNav.Message);
            }

            // 2) Raycast z v��ky dol� (grounding)
            try
            {
                Vector3 origin = new Vector3(desired.x, desired.y + 200f, desired.z);
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxGroundRay, ~0, QueryTriggerInteraction.Ignore))
                {
                    outPos = new Vector3(desired.x, hit.point.y + 0.5f, desired.z); // clearance
                    TBLog.Info($"TryFindNearestNavMeshOrGround: Raycast grounded to {outPos} (hit y={hit.point.y:F3}).");
                    return true;
                }
                else
                {
                    TBLog.Info($"TryFindNearestNavMeshOrGround: Ground raycast found NO hit for XZ=({desired.x:F3},{desired.z:F3}).");
                }
            }
            catch (Exception exRay)
            {
                TBLog.Warn("TryFindNearestNavMeshOrGround: ground raycast failed: " + exRay.Message);
            }

            // 3) Konzervativn� fallback: jen mal� zvednut� (max 1m). Pokud nic nenalezeno, vr�t�me false.
            try
            {
                const float step = 0.25f;
                const float maxUp = 1.0f; // konzervativn�
                for (float yoff = step; yoff <= maxUp; yoff += step)
                {
                    var cand = new Vector3(desired.x, desired.y + yoff, desired.z);
                    Collider[] cols = Physics.OverlapSphere(cand, 0.4f, ~0, QueryTriggerInteraction.Ignore);
                    bool blocked = false;
                    if (cols != null && cols.Length > 0)
                    {
                        foreach (var c in cols)
                        {
                            if (c == null) continue;
                            if (c.isTrigger) continue;
                            blocked = true;
                            break;
                        }
                    }
                    if (!blocked)
                    {
                        outPos = cand;
                        TBLog.Info($"TryFindNearestNavMeshOrGround: small-fallback free spot at {outPos}.");
                        return true;
                    }
                }
            }
            catch { /* ignore */ }

            TBLog.Warn("TryFindNearestNavMeshOrGround: no safe pos found near desired position (navmesh/raycast/small-fallback all failed).");
            return false;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryFindNearestNavMeshOrGround: unexpected: " + ex);
            outPos = desired;
            return false;
        }
    }

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
            TBLog.Warn("CloseDialogAndStopRefresh failed: " + ex);
        }
    }

    // Coroutine that refreshes button states while dialog is open.
    // NOTE: yields are outside try/catch to satisfy C# iterator restrictions.
    public IEnumerator RefreshCityButtonsWhileOpen(GameObject dialogRoot)
    {
        refreshRequested = true;
        TBLog.Info("RefreshCityButtonsWhileOpen: started");

        while (refreshRequested && dialogRoot != null && dialogRoot.activeInHierarchy)
        {
            // quick guard: find content; if missing wait and continue (yield outside try)
            var contentTransform = dialogRoot.transform.Find("ScrollArea/Viewport/Content");
            if (contentTransform == null)
            {
                yield return new WaitForSeconds(0.5f);
                continue;
            }

            try
            {
                long playerMoney = GetPlayerCurrencyAmountOrMinusOne();
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

                for (int i = 0; i < contentTransform.childCount; i++)
                {
                    var child = contentTransform.GetChild(i);
                    if (child == null) continue;

                    var btn = child.GetComponent<Button>();
                    var img = child.GetComponent<Image>();
                    if (btn == null || img == null) continue;

                    // derive city name from GameObject name "CityButton_<name>"
                    var goName = child.name ?? "";
                    if (!goName.StartsWith("CityButton_")) continue;
                    string cityName = goName.Substring("CityButton_".Length);

                    var city = TravelButton.FindCity(cityName);

                    // compute required flags (reuse central helper where possible)
                    bool initialInteractable = false;
                    try { initialInteractable = IsCityInteractable(city, playerMoney); }
                    catch { initialInteractable = false; }

                    // apply state only when changed
                    if (btn.interactable != initialInteractable)
                    {
                        btn.interactable = initialInteractable;

                        // Image raycast target follows interactable
                        img.raycastTarget = initialInteractable;

                        // ensure CanvasGroup present and update blocksRaycasts + interactable
                        var cg = btn.GetComponent<CanvasGroup>() ?? btn.gameObject.AddComponent<CanvasGroup>();
                        cg.blocksRaycasts = initialInteractable;
                        cg.interactable = initialInteractable;

                        // update label color
                        var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
                        if (txt != null) txt.color = initialInteractable ? new Color(0.98f, 0.94f, 0.87f, 1f) : new Color(0.55f, 0.55f, 0.55f, 1f);

                        // update image color to match states (use button colors where possible)
                        try
                        {
                            var cb = btn.colors;
                            img.color = initialInteractable ? cb.normalColor : cb.disabledColor;
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("RefreshCityButtonsWhileOpen exception: " + ex.Message);
            }

            // wait between refreshes (yield outside try/catch)
            yield return new WaitForSeconds(0.7f);
        }

        // cleanup on exit
        refreshRequested = false;
        refreshButtonsCoroutine = null;
        try { UnregisterCityButtons(); } catch { }
        TBLog.Info("RefreshCityButtonsWhileOpen: stopped");
        yield break;
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
                TBLog.Info($"FinishDialogLayoutAndShow: set content width to {viewportWidth}");
            }
            else
            {
                TBLog.Warn("FinishDialogLayoutAndShow: viewport width is zero after two frames - layout may be incorrect.");
            }

            // Rebuild layouts top-down
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);
            LayoutRebuilder.ForceRebuildLayoutImmediate(viewportRt);
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.GetComponent<RectTransform>());

            // Make sure ScrollRect shows top
            scrollRect.verticalNormalizedPosition = 1f;

            TBLog.Info("FinishDialogLayoutAndShow: finished rebuild and set scroll position.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("FinishDialogLayoutAndShow exception: " + ex);
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
            TBLog.Info($"Loaded Scene[{i}] name='{s.name}' path='{s.path}' isLoaded={s.isLoaded}");
        }
    }

    /// <summary>
    /// Try to detect player's currency amount. Returns -1 if could not determine.
    /// This is a best-effort reflection-based reader scanning MonoBehaviours, fields and properties.
    /// </summary>
    // replace the existing GetPlayerCurrencyAmountOrMinusOne method with this
    // Replace the existing GetPlayerCurrencyAmountOrMinusOne method with this improved, aggregate version.
    // This function first tries the local player's inventory for known currency fields/properties and sums them.
    // If that fails, it falls back to scanning scene components but excludes obvious UI/display components
    // to avoid reading color/flag fields from CurrencyDisplay etc. Every candidate read is logged.
    public static long GetPlayerCurrencyAmountOrMinusOne()
    {
        try
        {
            var candidates = new List<(long value, string source)>();

            // Helper to attempt reading currency-like values from an object via reflection
            long TryReadCurrencyFromObject(object obj, out string note)
            {
                note = "(unknown)";
                if (obj == null) return -1;
                var t = obj.GetType();
                note = t.FullName;
                string[] names = new[] { "ContainedSilver", "containedSilver", "Silver", "Money", "Gold", "Coins", "Currency", "CurrentMoney", "SilverAmount", "MoneyAmount" };
                foreach (var n in names)
                {
                    try
                    {
                        var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null)
                        {
                            var val = f.GetValue(obj);
                            if (val is int i) { note += $".{f.Name}"; return i; }
                            if (val is long l) { note += $".{f.Name}"; return l; }
                            if (val is float fval) { note += $".{f.Name}"; return (long)fval; }
                            if (val is double dval) { note += $".{f.Name}"; return (long)dval; }
                        }
                    }
                    catch { }
                    try
                    {
                        var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        if (p != null && p.CanRead)
                        {
                            var val = p.GetValue(obj, null);
                            if (val is int i2) { note += $".{p.Name}()"; return i2; }
                            if (val is long l2) { note += $".{p.Name}()"; return l2; }
                            if (val is float fval2) { note += $".{p.Name}()"; return (long)fval2; }
                            if (val is double dval2) { note += $".{p.Name}()"; return (long)dval2; }
                        }
                    }
                    catch { }
                }

                string[] methodNames = new[] { "GetSilver", "GetMoney", "GetCoins", "GetCurrency", "GetContainedSilver" };
                foreach (var mname in methodNames)
                {
                    try
                    {
                        var mi = t.GetMethod(mname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        if (mi != null && mi.GetParameters().Length == 0)
                        {
                            var ret = mi.Invoke(obj, null);
                            if (ret is int ri) { note += $".{mi.Name}()"; return ri; }
                            if (ret is long rl) { note += $".{mi.Name}()"; return rl; }
                            if (ret is float rf) { note += $".{mi.Name}()"; return (long)rf; }
                            if (ret is double rd) { note += $".{mi.Name}()"; return (long)rd; }
                        }
                    }
                    catch { }
                }

                return -1;
            }

            // 1) Preferred: search for known types that likely represent player inventory
            string[] knownTypeNames = new[] { "CharacterInventory", "CharacterInv", "Inventory", "PlayerInventory", "PlayerWallet" };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type ciType = null;
                try
                {
                    ciType = asm.GetTypes().FirstOrDefault(t => knownTypeNames.Contains(t.Name));
                }
                catch { continue; }
                if (ciType == null) continue;

                // static Instance property/field
                try
                {
                    var instProp = ciType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                                 ?? ciType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)
                                 ?? ciType.GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static);
                    object inst = null;
                    if (instProp != null) inst = instProp.GetValue(null);
                    else
                    {
                        var instField = ciType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)
                                    ?? ciType.GetField("instance", BindingFlags.Public | BindingFlags.Static)
                                    ?? ciType.GetField("m_instance", BindingFlags.NonPublic | BindingFlags.Static);
                        if (instField != null) inst = instField.GetValue(null);
                    }
                    if (inst != null)
                    {
                        var val = TryReadCurrencyFromObject(inst, out var note);
                        if (val >= 0) candidates.Add((val, $"StaticInstance:{note}"));
                    }
                }
                catch { }

                // active instances in scene
                try
                {
                    var objs = UnityEngine.Object.FindObjectsOfType(ciType);
                    if (objs != null)
                    {
                        foreach (var o in objs)
                        {
                            try
                            {
                                var val = TryReadCurrencyFromObject(o, out var note);
                                if (val >= 0) candidates.Add((val, $"FindObjectsOfType:{note}"));
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // 2) General scan of MonoBehaviours (fallback) - record candidates but don't return first-match
            try
            {
                var allMono = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in allMono)
                {
                    if (mb == null) continue;
                    try
                    {
                        var val = TryReadCurrencyFromObject(mb, out var note);
                        if (val >= 0) candidates.Add((val, $"MB:{note}"));
                    }
                    catch { }
                }
            }
            catch { }

            // 3) Evaluate candidates
            if (candidates.Count == 0)
            {
                TBLog.Warn("GetPlayerCurrencyAmountOrMinusOne: no currency candidates found.");
                return -1;
            }

            // pick the best candidate: prefer highest positive value (helps avoid transient zeros)
            var best = candidates.OrderByDescending(c => c.value).First();
            TBLog.Info($"GetPlayerCurrencyAmountOrMinusOne: picked candidate value={best.value} source={best.source} (found {candidates.Count} candidates)");

            // If best is zero, treat as unknown to avoid disabling UI on transient reads shortly after scene-load
            if (best.value == 0)
            {
                TBLog.Info("GetPlayerCurrencyAmountOrMinusOne: best candidate is 0 -> treat as unknown (-1) to avoid false-negative during load.");
                return -1;
            }

            return best.value;
        }
        catch (Exception ex)
        {
            TBLog.Warn("GetPlayerCurrencyAmountOrMinusOne exception: " + ex.Message);
            return -1;
        }
    }

    // helper used above; include in this file if not already present
    private static string SafeToString(object o)
    {
        try
        {
            if (o == null) return "null";
            return o.ToString();
        }
        catch { return "<err>"; }
    }

    // add inside TravelButtonUI (or a debug MonoBehaviour)
    private void DumpTravelButtonState()
    {
        try
        {
            var tb = GameObject.Find("TravelButton");
            if (tb == null)
            {
                TBLog.Warn("DumpTravelButtonState: TravelButton GameObject not found.");
                return;
            }

            var rt = tb.GetComponent<RectTransform>();
            var btn = tb.GetComponent<UnityEngine.UI.Button>();
            var img = tb.GetComponent<UnityEngine.UI.Image>();
            var cg = tb.GetComponent<CanvasGroup>();
            var root = tb.transform.root;
            TBLog.Info($"DumpTravelButtonState: name='{tb.name}', activeSelf={tb.activeSelf}, activeInHierarchy={tb.activeInHierarchy}");
            TBLog.Info($"DumpTravelButtonState: parent='{tb.transform.parent?.name}', root='{root?.name}'");
            if (rt != null) TBLog.Info($"DumpTravelButtonState: anchoredPosition={rt.anchoredPosition}, sizeDelta={rt.sizeDelta}, anchorMin={rt.anchorMin}, anchorMax={rt.anchorMax}");
            if (btn != null) TBLog.Info($"DumpTravelButtonState: Button.interactable={btn.interactable}");
            if (img != null) TBLog.Info($"DumpTravelButtonState: Image.color={img.color}, raycastTarget={img.raycastTarget}");
            if (cg != null) TBLog.Info($"DumpTravelButtonState: CanvasGroup alpha={cg.alpha}, interactable={cg.interactable}, blocksRaycasts={cg.blocksRaycasts}");
            var canvas = tb.GetComponentInParent<Canvas>();
            if (canvas != null) TBLog.Info($"DumpTravelButtonState: Canvas name={canvas.gameObject.name}, sortingOrder={canvas.sortingOrder}, renderMode={canvas.renderMode}");
            else TBLog.Warn("DumpTravelButtonState: No parent Canvas found.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("DumpTravelButtonState exception: " + ex);
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
                            TBLog.Info($"AttemptDeductSilver: invoking refresh method {t.FullName}.{mi.Name}() on '{sourceMb.gameObject?.name}'");
                            mi.Invoke(sourceMb, null);
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn($"AttemptDeductSilver: invoking {t.FullName}.{mi.Name}() threw: {ex}");
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
                                TBLog.Info($"AttemptDeductSilver: invoked potential refresh {mt.FullName}.{mi.Name}() on '{mb.gameObject?.name}'");
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
            TBLog.Info($"AttemptDeductSilver: attempted to invoke {invoked} potential refresh methods after deduction.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("AttemptDeductSilver: TryInvokeRefreshMethods exception: " + ex);
        }
    }


    // Show a short, inline message in the open dialog (if present). Clears after a few seconds.
    private Coroutine inlineMessageClearCoroutine;
    /// <summary>
    /// Instance implementation that updates the inline message and manages the clear coroutine.
    /// Must be an instance method so StartCoroutine/StopCoroutine operate on this MonoBehaviour.
    /// </summary>
    /// <summary>
    /// Instance implementation that updates the inline message and manages the clear coroutine.
    /// Must be an instance method so StartCoroutine/StopCoroutine operate on this MonoBehaviour.
    /// </summary>
    public void ShowInlineDialogMessageInstance(string msg)
    {
        try
        {
            TBLog.Info("[TravelButton] Inline message: " + msg);

            if (dialogRoot == null)
            {
                TBLog.Warn("ShowInlineDialogMessageInstance: dialogRoot is null.");
                return;
            }

            var inline = dialogRoot.transform.Find("InlineMessage");
            if (inline == null)
            {
                TBLog.Warn("ShowInlineDialogMessageInstance: InlineMessage element not found in dialogRoot.");
                return;
            }

            var txt = inline.GetComponent<UnityEngine.UI.Text>();
            if (txt == null)
            {
                TBLog.Warn("ShowInlineDialogMessageInstance: InlineMessage Text component missing.");
                return;
            }

            txt.text = msg;

            // Restart clear coroutine if already running
            if (inlineMessageClearCoroutine != null)
            {
                try { StopCoroutine(inlineMessageClearCoroutine); } catch { }
                inlineMessageClearCoroutine = null;
            }

            inlineMessageClearCoroutine = StartCoroutine(ClearInlineMessageAfterDelay(3f));
        }
        catch (Exception ex)
        {
            TBLog.Warn("ShowInlineDialogMessageInstance exception: " + ex);
        }
    }

    /// <summary>
    /// Static wrapper: finds the active TravelButtonUI instance and calls the instance method.
    /// Use this from other static contexts safely.
    /// </summary>
    public static void ShowInlineDialogMessage(string msg)
    {
        try
        {
            var ui = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
            if (ui == null)
            {
                TBLog.Warn("ShowInlineDialogMessage: TravelButtonUI instance not found.");
                return;
            }
            ui.ShowInlineDialogMessageInstance(msg);
        }
        catch (Exception ex)
        {
            TBLog.Warn("ShowInlineDialogMessage wrapper exception: " + ex);
        }
    }

    /// <summary>
    /// Instance coroutine to clear the inline message after a delay.
    /// The yield is placed before the try/catch to avoid the C# restriction.
    /// </summary>
    private IEnumerator ClearInlineMessageAfterDelay(float delay)
    {
        // yield first (no try/catch around yields that have catch/finally)
        yield return new WaitForSeconds(delay);

        try
        {
            if (dialogRoot == null)
            {
                yield break;
            }

            var inline = dialogRoot.transform.Find("InlineMessage");
            var txt = inline?.GetComponent<UnityEngine.UI.Text>();
            if (txt != null) txt.text = "";
        }
        catch (Exception ex)
        {
            TBLog.Warn("ClearInlineMessageAfterDelay exception: " + ex);
        }

        // Ensure coroutine ref is cleared (no try/finally with yield needed)
        try { inlineMessageClearCoroutine = null; } catch { }
        yield break;
    }

    // ClickLogger for debugging
    private class ClickLogger : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            TBLog.Info("ClickLogger: OnPointerClick received on " + gameObject.name + " button.");
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            TBLog.Info("ClickLogger: OnPointerEnter on " + gameObject.name);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            TBLog.Info("ClickLogger: OnPointerExit on " + gameObject.name);
        }
    }

    // If you don't already have TryTeleportThenChargeExplicit implemented, include it (or use yours).
    // This is the explicit, no-reflection entrypoint used above.
    /// <summary>
    /// Explicit, non-reflection teleport coroutine.
    /// Call with StartCoroutine(TryTeleportThenChargeExplicit(sceneName, targetGameObjectName, coordsHint, haveCoordsHint, price));
    ///
    /// Behavior:
    /// - Validates scene/coords presence and player funds
    /// - Attempts to charge the player (uses CurrencyHelpers.AttemptDeductSilverDirect)
    /// - Ensures TeleportManager exists and requests StartTeleport
    /// - Disables dialog UI while teleport runs and subscribes to OnTeleportFinished to re-enable UI (and refund on failure)
    /// - Handles errors and refunds as best-effort
    /// </summary>
    public IEnumerator TryTeleportThenChargeExplicit(string sceneName, string targetGameObjectName, Vector3 coordsHint, bool haveCoordsHint, int price)
    {
        TBLog.Info("TryTeleportThenChargeExplicit: ENTER (sceneName='" + sceneName + "')");

        TBLog.Info($"TryTeleportThenChargeExplicit: requested scene='{sceneName}' target='{targetGameObjectName}' coordsHint={coordsHint} haveCoords={haveCoordsHint} price={price}");

        // Validate destination (require sceneName or coords as fallback)
        if (string.IsNullOrEmpty(sceneName) && !haveCoordsHint)
        {
            TBLog.Warn("TryTeleportThenChargeExplicit: destination not configured (no sceneName and no coords). Aborting.");
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        // Attempt to charge the player (real deduction)
        bool charged = false;
        try
        {
            // AttemptDeductSilverDirect(amount, simulate=false) - adjust if your helper has different signature
            charged = CurrencyHelpers.AttemptDeductSilverDirect(price, false);
            if (!charged)
            {
                TBLog.Info($"TryTeleportThenChargeExplicit: player has insufficient funds for {price} silver.");
                try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Not enough silver for teleport."); } catch { }
                yield break;
            }
            TBLog.Info($"TryTeleportThenChargeExplicit: charged {price} silver.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenChargeExplicit: charging threw exception: " + ex);
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: payment error."); } catch { }
            yield break;
        }

        // Ensure TeleportManager exists
        try
        {
            if (TeleportManager.Instance == null)
            {
                var host = new GameObject("TeleportManagerHost");
                host.AddComponent<TeleportManager>();
                TBLog.Info("TryTeleportThenChargeExplicit: created TeleportManager host GameObject.");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenChargeExplicit: could not ensure TeleportManager exists: " + ex);
            // Refund on internal failure
            try { TryRefundPlayerCurrency(price); } catch (Exception rex) { TBLog.Warn("Refund after TeleportManager creation failure threw: " + rex); }
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: internal error."); } catch { }
            yield break;
        }

        // Start teleport
        bool started = false;
        try
        {
            started = TeleportManager.Instance.StartTeleport(sceneName, targetGameObjectName, coordsHint, haveCoordsHint, price);
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenChargeExplicit: StartTeleport threw: " + ex);
            try { TryRefundPlayerCurrency(price); } catch (Exception rex) { TBLog.Warn("Refund after StartTeleport exception threw: " + rex); }
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: internal error."); } catch { }
            yield break;
        }

        if (!started)
        {
            TBLog.Info("TryTeleportThenChargeExplicit: TeleportManager rejected the request (likely another transition in progress). Refunding.");
            try { TryRefundPlayerCurrency(price); } catch (Exception rex) { TBLog.Warn("Refund after StartTeleport rejected threw: " + rex); }
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport is already in progress. Please wait."); } catch { }
            yield break;
        }

        // Transition accepted: disable dialog UI while teleport runs.
        try { DisableDialogButtons(); } catch (Exception ex) { TBLog.Warn("TryTeleportThenChargeExplicit: DisableDialogButtons threw: " + ex); }

        // Subscribe for completion to re-enable UI and handle refund on failure
        System.Action<bool> onFinished = null;
        onFinished = (success) =>
        {
            try
            {
                if (!success)
                {
                    // If teleport failed, refund the payment (best-effort)
                    try { TryRefundPlayerCurrency(price); } catch (Exception rex) { TBLog.Warn("TryTeleportThenChargeExplicit: refund on failure threw: " + rex); }
                    try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed; your payment was refunded."); } catch { }
                }

                // Re-enable UI
                try { EnableDialogButtons(); } catch (Exception rex) { TBLog.Warn("TryTeleportThenChargeExplicit: EnableDialogButtons threw: " + rex); }
            }
            finally
            {
                // Unsubscribe (best-effort)
                try { TeleportManager.Instance.OnTeleportFinished -= onFinished; } catch { }
            }
        };

        try
        {
            TeleportManager.Instance.OnTeleportFinished += onFinished;
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryTeleportThenChargeExplicit: failed to subscribe to OnTeleportFinished: " + ex);
            // If subscription fails, ensure UI is at least re-enabled and refund so player isn't charged indefinitely.
            try { EnableDialogButtons(); } catch { }
            try { TryRefundPlayerCurrency(price); } catch { }
            yield break;
        }

        // The TeleportManager is now responsible for the asynchronous scene load/teleport.
        yield break;
    }

    // The project already contains a TryRefundPlayerCurrency implementation; leave this method call here as a reference point.
    private void TryRefundPlayerCurrency(int amount)
    {
        try
        {
            var method = this.GetType().GetMethod("TryRefundPlayerCurrency", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (method != null) { method.Invoke(this, new object[] { amount }); return; }

            var chType = Type.GetType("CurrencyHelpers, " + typeof(CurrencyHelpers).Assembly.GetName().Name);
            if (chType != null)
            {
                var m = chType.GetMethod("AttemptRefundSilver", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (m != null) { m.Invoke(null, new object[] { amount }); return; }
            }

            TBLog.Warn("TryRefundPlayerCurrency: no refund helper found.");
        }
        catch (Exception ex)
        {
            TBLog.Warn("TryRefundPlayerCurrency: " + ex);
        }
    }

    // Replace existing CloseDialogAndInventory_Safe usage with this safer implementation:
    private void CloseDialogAndInventory_Safe()
    {
        try
        {
            // Destroy our dialog UI if present (best-effort)
            if (dialogRoot != null)
            {
                try
                {
                    TBLog.Info("CloseDialogAndInventory_Safe_Safe: destroying dialogRoot");
                    UnityEngine.Object.Destroy(dialogRoot);
                }
                catch (Exception ex)
                {
                    TBLog.Warn("CloseDialogAndInventory_Safe_Safe: destroying dialogRoot threw: " + ex);
                    try { dialogRoot.SetActive(false); } catch { }
                }
                dialogRoot = null;
            }
            else
            {
                TBLog.Info("CloseDialogAndInventory_Safe_Safe: dialogRoot was null");
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("CloseDialogAndInventory_Safe_Safe: dialogRoot close attempt threw: " + ex);
        }

        // Try to find "MenuManager" and close/hide it safely
        try
        {
            var menuGO = GameObject.Find("MenuManager");
            if (menuGO == null)
            {
                TBLog.Info("CloseDialogAndInventory_Safe_Safe: MenuManager GameObject not found");
                return;
            }

            // Save reference for restore
            _tb_savedMenuManagerGO = menuGO;
            _tb_savedMenuManagerActive = menuGO.activeSelf;
            _tb_menuManagerHiddenByPlugin = false;
            _tb_menuManagerClosedByMethod = false;

            // 1) Try to call a close method on a MonoBehaviour component if available
            var comps = menuGO.GetComponents<MonoBehaviour>();
            foreach (var comp in comps)
            {
                if (comp == null) continue;
                var t = comp.GetType();
                // common close method names to try
                var mClose = t.GetMethod("CloseAllMenus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                          ?? t.GetMethod("CloseAll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                          ?? t.GetMethod("Close", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                          ?? t.GetMethod("HideMenus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                          ?? t.GetMethod("ToggleMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mClose != null)
                {
                    try
                    {
                        TBLog.Info($"CloseDialogAndInventory_Safe_Safe: invoking {t.FullName}.{mClose.Name}() on MenuManager component to close menus");
                        mClose.Invoke(comp, null);
                        _tb_menuManagerClosedByMethod = true;
                        // We invoked a real close API; assume it handled focus and visuals properly
                        return;
                    }
                    catch (Exception ex)
                    {
                        TBLog.Warn($"CloseDialogAndInventory_Safe_Safe: invoking {t.FullName}.{mClose.Name} threw: " + ex);
                    }
                }
            }

            // 2) No close method found: hide using CanvasGroup, but save the original state to restore later
            var cg = menuGO.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                try
                {
                    _tb_savedMenuManagerCanvasGroup = cg;
                    _tb_savedCanvasAlpha = cg.alpha;
                    _tb_savedCanvasInteractable = cg.interactable;
                    _tb_savedCanvasBlocksRaycasts = cg.blocksRaycasts;

                    TBLog.Info("CloseDialogAndInventory_Safe_Safe: hiding MenuManager CanvasGroup (fallback)");
                    cg.alpha = 0f;
                    cg.interactable = false;
                    cg.blocksRaycasts = false;

                    _tb_menuManagerHiddenByPlugin = true;

                    // Clear selected UI so EventSystem doesn't keep UI-focused selection
                    try
                    {
                        var es = UnityEngine.EventSystems.EventSystem.current;
                        if (es != null)
                        {
                            es.SetSelectedGameObject(null);
                            TBLog.Info("CloseDialogAndInventory_Safe_Safe: cleared EventSystem selected GameObject");
                        }
                    }
                    catch (Exception ex) { TBLog.Warn("CloseDialogAndInventory_Safe_Safe: clearing EventSystem selection threw: " + ex); }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("CloseDialogAndInventory_Safe_Safe: CanvasGroup hide fallback threw: " + ex);
                }
            }
            else
            {
                // 3) Last-resort: deactivate the GameObject, but remember to restore
                try
                {
                    TBLog.Info("CloseDialogAndInventory_Safe_Safe: MenuManager CanvasGroup not found; deactivating MenuManager GameObject as fallback");
                    _tb_savedMenuManagerActive = menuGO.activeSelf;
                    menuGO.SetActive(false);
                    _tb_menuManagerHiddenByPlugin = true;
                }
                catch (Exception ex)
                {
                    TBLog.Warn("CloseDialogAndInventory_Safe_Safe: deactivating MenuManager threw: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("CloseDialogAndInventory_Safe_Safe: inventory/menu close attempt threw: " + ex);
        }
    }

    // Call this after teleport is complete (after fade-in) to restore any state we changed.
    private void RestoreDialogAndInventory_Safe()
    {
        try
        {
            // If a real close method was used, hope it handled restored state; we still attempt to clear selection and restore cursor.
            if (_tb_menuManagerClosedByMethod)
            {
                // Clear the flag — no other restoration we can do safely
                _tb_menuManagerClosedByMethod = false;
            }

            // If we hid MenuManager via CanvasGroup or deactivated it, restore saved values
            if (_tb_savedMenuManagerGO != null && _tb_menuManagerHiddenByPlugin)
            {
                try
                {
                    // Restore CanvasGroup if we used it
                    if (_tb_savedMenuManagerCanvasGroup != null)
                    {
                        try
                        {
                            _tb_savedMenuManagerCanvasGroup.alpha = _tb_savedCanvasAlpha;
                            _tb_savedMenuManagerCanvasGroup.interactable = _tb_savedCanvasInteractable;
                            _tb_savedMenuManagerCanvasGroup.blocksRaycasts = _tb_savedCanvasBlocksRaycasts;
                            TBLog.Info("RestoreDialogAndInventory_Safe: restored MenuManager CanvasGroup properties");
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn("RestoreDialogAndInventory_Safe: restoring CanvasGroup properties threw: " + ex);
                        }
                    }
                    else
                    {
                        // restore active state if we deactivated MenuManager
                        try
                        {
                            _tb_savedMenuManagerGO.SetActive(_tb_savedMenuManagerActive);
                            TBLog.Info($"RestoreDialogAndInventory_Safe: restored MenuManager active={_tb_savedMenuManagerActive}");
                        }
                        catch (Exception ex)
                        {
                            TBLog.Warn("RestoreDialogAndInventory_Safe: restoring MenuManager active state threw: " + ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    TBLog.Warn("RestoreDialogAndInventory_Safe: error while restoring MenuManager: " + ex);
                }
            }

            // Clear saved references and flags
            _tb_menuManagerHiddenByPlugin = false;
            _tb_savedMenuManagerCanvasGroup = null;
            _tb_savedMenuManagerGO = null;

            // Make sure EventSystem selection is cleared so camera input isn't blocked by stale selection,
            // then optionally restore game cursor lock/visibility to gameplay mode.
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null)
                {
                    es.SetSelectedGameObject(null);
                    TBLog.Info("RestoreDialogAndInventory_Safe: cleared EventSystem selected GameObject");
                }
            }
            catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe: clearing EventSystem selection threw: " + ex); }

            try
            {
                // restore cursor to gameplay (adjust if your game uses different lock/visibility)
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                TBLog.Info("RestoreDialogAndInventory_Safe: set Cursor.lockState=Locked, visible=false");
            }
            catch (Exception ex) { TBLog.Warn("RestoreDialogAndInventory_Safe: cursor restore threw: " + ex); }
        }
        catch (Exception ex)
        {
            TBLog.Warn("RestoreDialogAndInventory_Safe: restore attempt threw: " + ex);
        }
    }

    // Temporary: force the travel button visible and stop visibility monitor
    private void Debug_ForceShowButton()
    {
        try
        {
            StopInventoryVisibilityMonitor(); // make sure monitor won't immediately toggle it
        }
        catch { }

        if (buttonObject != null && !buttonObject.activeSelf)
        {
            try { buttonObject.SetActive(true); } catch { }
        }
        TBLog.Info("DEBUG: Forced Travel button visible and stopped visibility monitor.");
    }

    /// <summary>
    /// Dump debugging information relevant to travel/teleport availability:
    /// - city/destination components and visited/enabled flags
    /// - travel/visited manager fields
    /// - player money-like fields
    /// - config/settings flags that mention cities
    /// Safe to call on button click or after teleport success.
    /// </summary>
    public void DumpTravelDebugInfo()
    {
        try
        {
            TBLog.Info("DBG: ---- Travel debug dump start ----");
            DumpVisitedManagers();
            DumpCityComponents();
            DumpPlayerMoneyCandidates();
            DumpConfigFlags();
            TBLog.Info("DBG: ---- Travel debug dump end ----");
        }
        catch (Exception ex)
        {
            TBLog.Warn("DBG: DumpTravelDebugInfo failed: " + ex);
        }
    }

    private void DumpVisitedManagers()
    {
        try
        {
            // Try to find known manager types first, then fallback to heuristics
            string[] managerTypeNames = new[] { "TravelButtonVisitedManager", "VisitedManager", "CityDiscovery", "TravelManager", "VisitedList" };
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var name in managerTypeNames)
            {
                var t = assemblies.Select(a => a.GetType(name, false)).FirstOrDefault(tt => tt != null);
                if (t != null)
                {
                    // Try find an instance in scene
                    var instance = FindObjectOfType(t) as object;
                    if (instance == null)
                    {
                        // Try static Instance property
                        instance = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                    }

                    TBLog.Info($"DBG: Found manager type {name}: type={t.FullName}, instance={(instance != null ? "yes" : "no")}");
                    if (instance != null)
                    {
                        DumpObjectFieldsAndProperties(instance, "manager");
                    }
                }
            }

            // Fallback: attempt to locate any type in assemblies with "Visited" or "CityDiscovery" in name
            var fallbackTypes = assemblies.SelectMany(a =>
            {
                try { return a.GetTypes(); } catch { return new Type[0]; }
            })
            .Where(tt => tt.Name.IndexOf("Visited", StringComparison.OrdinalIgnoreCase) >= 0
                      || tt.Name.IndexOf("CityDiscovery", StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct();

            foreach (var ft in fallbackTypes)
            {
                var instance = FindObjectOfType(ft) as object;
                if (instance != null)
                {
                    TBLog.Info($"DBG: Fallback manager instance found: {ft.FullName} on GameObject {(instance as MonoBehaviour)?.gameObject.name}");
                    DumpObjectFieldsAndProperties(instance, "manager-fallback");
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DBG: DumpVisitedManagers exception: " + ex);
        }
    }

    private void DumpCityComponents()
    {
        try
        {
            // Get all MonoBehaviours (including inactive) and filter by type name
            var comps = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(m => m != null && m.gameObject != null && m.gameObject.scene.IsValid()).ToArray();
            var interesting = comps.Where(c =>
            {
                var n = c.GetType().Name.ToLowerInvariant();
                return n.Contains("city") || n.Contains("destination") || n.Contains("travel") || n.Contains("town");
            }).ToArray();

            TBLog.Info($"DBG: Found {interesting.Length} city-like components in scene.");
            foreach (var comp in interesting)
            {
                var t = comp.GetType();
                string goName = comp.gameObject != null ? comp.gameObject.name : "(no-go)";
                TBLog.Info($"DBG: Component: {t.FullName} on GO '{goName}'");

                // Basic name / display field attempts
                var nameField = t.GetField("Name", BindingFlags.Public | BindingFlags.Instance)
                             ?? t.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                if (nameField != null)
                {
                    try { TBLog.Info($"DBG:  - Name field: {nameField.GetValue(comp)}"); } catch { }
                }

                // Look for visited/enabled boolean fields and properties
                var boolMembers = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   .Where(m =>
                                   {
                                       string mn = m.Name.ToLowerInvariant();
                                       return mn.Contains("visited") || mn.Contains("isvisited") || mn.Contains("visitedflag")
                                           || mn.Contains("enabled") || mn.Contains("isenabled") || mn.Contains("available") || mn.Contains("locked");
                                   });

                foreach (var mem in boolMembers)
                {
                    try
                    {
                        if (mem is FieldInfo fi && fi.FieldType == typeof(bool))
                        {
                            var val = fi.GetValue(comp);
                            TBLog.Info($"DBG:  - Field {fi.Name} (bool) = {val}");
                        }
                        else if (mem is PropertyInfo pi && pi.PropertyType == typeof(bool) && pi.GetIndexParameters().Length == 0)
                        {
                            var val = pi.GetValue(comp, null);
                            TBLog.Info($"DBG:  - Prop {pi.Name} (bool) = {val}");
                        }
                    }
                    catch { /* ignore per-field errors */ }
                }

                // Look for cost/price numeric fields
                var numMembers = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                  .Where(m =>
                                  {
                                      string mn = m.Name.ToLowerInvariant();
                                      return mn.Contains("cost") || mn.Contains("price") || mn.Contains("fee") || mn.Contains("gold") || mn.Contains("coins");
                                  });

                foreach (var mem in numMembers)
                {
                    try
                    {
                        if (mem is FieldInfo fi && (fi.FieldType == typeof(int) || fi.FieldType == typeof(float) || fi.FieldType == typeof(double)))
                        {
                            var val = fi.GetValue(comp);
                            TBLog.Info($"DBG:  - Field {fi.Name} (num) = {val}");
                        }
                        else if (mem is PropertyInfo pi && (pi.PropertyType == typeof(int) || pi.PropertyType == typeof(float) || pi.PropertyType == typeof(double))
                                 && pi.GetIndexParameters().Length == 0)
                        {
                            var val = pi.GetValue(comp, null);
                            TBLog.Info($"DBG:  - Prop {pi.Name} (num) = {val}");
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DBG: DumpCityComponents exception: " + ex);
        }
    }

    private void DumpPlayerMoneyCandidates()
    {
        try
        {
            // Find MonoBehaviours with "Player" or "Character" in the type name
            var comps = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(m => m != null && m.gameObject != null && m.gameObject.scene.IsValid()).ToArray();
            var players = comps.Where(c =>
            {
                var n = c.GetType().Name.ToLowerInvariant();
                return n.Contains("player") || n.Contains("character") || n.Contains("wallet") || n.Contains("account");
            }).ToArray();

            TBLog.Info($"DBG: Found {players.Length} player-like components.");

            foreach (var p in players)
            {
                TBLog.Info($"DBG: Player-like component: {p.GetType().FullName} on GO '{p.gameObject.name}'");
                var t = p.GetType();

                // Numeric candidate fields/properties that might represent money
                var numMembers = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                  .Where(m =>
                                  {
                                      string mn = m.Name.ToLowerInvariant();
                                      return mn.Contains("money") || mn.Contains("gold") || mn.Contains("coins") || mn.Contains("silver") || mn.Contains("balance") || mn.Contains("wallet");
                                  });

                foreach (var mem in numMembers)
                {
                    try
                    {
                        if (mem is FieldInfo fi && (fi.FieldType == typeof(int) || fi.FieldType == typeof(float) || fi.FieldType == typeof(double) || fi.FieldType == typeof(long)))
                        {
                            var val = fi.GetValue(p);
                            TBLog.Info($"DBG:  - Field {fi.Name} = {val}");
                        }
                        else if (mem is PropertyInfo pi && pi.GetIndexParameters().Length == 0 &&
                                 (pi.PropertyType == typeof(int) || pi.PropertyType == typeof(float) || pi.PropertyType == typeof(double) || pi.PropertyType == typeof(long)))
                        {
                            var val = pi.GetValue(p, null);
                            TBLog.Info($"DBG:  - Prop {pi.Name} = {val}");
                        }
                    }
                    catch { }
                }
            }

            // Also try to find a global GameManager-like type that might hold currency
            var allTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } });
            var gmTypes = allTypes.Where(tt => tt.Name.IndexOf("GameManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               tt.Name.IndexOf("Economy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               tt.Name.IndexOf("Currency", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var gt in gmTypes)
            {
                TBLog.Info($"DBG: Found manager type candidate: {gt.FullName}");
                // try static properties/fields
                var props = gt.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var pi in props.Where(p => (p.PropertyType == typeof(int) || p.PropertyType == typeof(float) || p.PropertyType == typeof(double) || p.PropertyType == typeof(long)) && p.GetIndexParameters().Length == 0))
                {
                    try
                    {
                        var val = pi.GetValue(null, null);
                        TBLog.Info($"DBG:  - Static Prop {gt.Name}.{pi.Name} = {val}");
                    }
                    catch { }
                }

                var fields = gt.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var fi in fields.Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(float) || f.FieldType == typeof(double) || f.FieldType == typeof(long)))
                {
                    try
                    {
                        var val = fi.GetValue(null);
                        TBLog.Info($"DBG:  - Static Field {gt.Name}.{fi.Name} = {val}");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DBG: DumpPlayerMoneyCandidates exception: " + ex);
        }
    }

    private void DumpConfigFlags()
    {
        try
        {
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } });

            // look for types that look like config/settings
            var configTypes = types.Where(t => t.Name.IndexOf("Config", StringComparison.OrdinalIgnoreCase) >= 0
                                           || t.Name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0
                                           || t.Name.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var ct in configTypes)
            {
                try
                {
                    // look for static instance or static fields/properties with booleans mentioning cities
                    object instance = null;
                    var instProp = ct.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (instProp != null)
                    {
                        try { instance = instProp.GetValue(null); } catch { }
                    }

                    // log static boolean fields and properties that reference city or enable
                    var staticBools = ct.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                        .Where(m =>
                                        {
                                            string mn = m.Name.ToLowerInvariant();
                                            return mn.Contains("city") || mn.Contains("enable") || mn.Contains("enabled") || mn.Contains("allow");
                                        });

                    TBLog.Info($"DBG: Config/Settings candidate: {ct.FullName}, instance={(instance != null ? "yes" : "no")}");
                    foreach (var mem in staticBools)
                    {
                        try
                        {
                            if (mem is FieldInfo sfi && sfi.FieldType == typeof(bool))
                            {
                                TBLog.Info($"DBG:  - Static Field {ct.Name}.{sfi.Name} = {sfi.GetValue(null)}");
                            }
                            else if (mem is PropertyInfo spi && spi.PropertyType == typeof(bool) && spi.GetIndexParameters().Length == 0)
                            {
                                TBLog.Info($"DBG:  - Static Prop {ct.Name}.{spi.Name} = {spi.GetValue(null)}");
                            }
                        }
                        catch { }
                    }

                    // If instance exists, log instance bool fields/properties that mention city/enable
                    if (instance != null)
                    {
                        var instMembers = ct.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                           .Where(m =>
                                           {
                                               string mn = m.Name.ToLowerInvariant();
                                               return mn.Contains("city") || mn.Contains("enable") || mn.Contains("enabled") || mn.Contains("allow");
                                           });

                        foreach (var mem in instMembers)
                        {
                            try
                            {
                                if (mem is FieldInfo fi && fi.FieldType == typeof(bool))
                                {
                                    TBLog.Info($"DBG:  - Instance Field {ct.Name}.{fi.Name} = {fi.GetValue(instance)}");
                                }
                                else if (mem is PropertyInfo pi && pi.PropertyType == typeof(bool) && pi.GetIndexParameters().Length == 0)
                                {
                                    TBLog.Info($"DBG:  - Instance Prop {ct.Name}.{pi.Name} = {pi.GetValue(instance)}");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DBG: DumpConfigFlags exception: " + ex);
        }
    }

    private void DumpObjectFieldsAndProperties(object obj, string prefix = "")
    {
        if (obj == null) return;
        try
        {
            var t = obj.GetType();
            TBLog.Info($"DBG: Dumping fields/properties for {t.FullName} ({prefix})");

            // boolean members
            var boolFields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                              .Where(f => f.FieldType == typeof(bool));
            foreach (var f in boolFields)
            {
                try { TBLog.Info($"DBG:  - Field {f.Name} = {f.GetValue(obj)}"); } catch { }
            }

            var boolProps = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                             .Where(p => p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0);
            foreach (var p in boolProps)
            {
                try { TBLog.Info($"DBG:  - Prop {p.Name} = {p.GetValue(obj, null)}"); } catch { }
            }

            // list-like visited containers (IEnumerable of strings or bools or objects)
            var listFields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                              .Where(f => typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(string));
            foreach (var f in listFields)
            {
                try
                {
                    var val = f.GetValue(obj) as System.Collections.IEnumerable;
                    if (val == null) continue;
                    TBLog.Info($"DBG:  - Enumerable Field {f.Name}:");
                    int i = 0;
                    foreach (var item in val)
                    {
                        TBLog.Info($"DBG:     [{i}] {item}");
                        i++;
                        if (i > 50) { TBLog.Info("DBG:     ... truncated after 50 items"); break; }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("DBG: DumpObjectFieldsAndProperties failed: " + ex);
        }
    }
    /// <summary>
    /// Detects the currently active scene and logs a short info message.
    /// Uses the project's TBLog helper so output appears in the same log as the mod.
    /// Call: TravelButtonUtils.LogActiveSceneInfo();
    /// </summary>
    public static void LogActiveSceneInfo()
    {
        try
        {
            var scene = SceneManager.GetActiveScene();
            string sceneName = scene.IsValid() ? scene.name : "(invalid)";
            TBLog.Info($"Active scene: '{sceneName}' (isLoaded={scene.isLoaded}, rootCount={scene.rootCount})");
        }
        catch (Exception ex)
        {
            TBLog.Warn("LogActiveSceneInfo failed: " + ex.Message);
        }
    }
}