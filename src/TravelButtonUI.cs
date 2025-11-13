using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Linq;
using System;
using System.IO;

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

    // Insert this method inside the TravelButtonUI class (e.g. after the private fields).
    // It uses fully-qualified System.* calls so you don't need extra using directives.
    private void Trace(string message)
    {
        try
        {
            string path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), "TravelButton_component_trace.txt");
            // append with timestamp and method/class context
            System.IO.File.AppendAllText(path, $"[{System.DateTime.UtcNow:O}] TravelButtonUI: {message}\n");
        }
        catch { /* swallow to avoid causing further issues while debugging */ }
    }

    // Example: at the top of Start()
    private void Start()
    {
        Trace("Start reached");
        // existing Start code...
        Trace("Start finished - about to start init coroutine or create UI");
    }

    void Update()
    {
        Trace("Update reached");
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
        Trace($"ReparentButtonToInventory start (container={(container == null ? "null" : container.name)})");
        try
        {
            // existing method body...
        }
        catch (Exception ex)
        {
            Trace("ReparentButtonToInventory EX: " + ex.ToString());
            throw;
        }
        Trace("ReparentButtonToInventory end");
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

    private void CreateTravelButton()
    {
        Trace("CreateTravelButton start");
        try
        {
            // existing CreateTravelButton implementation ...
        }
        catch (Exception ex)
        {
            Trace("CreateTravelButton EX: " + ex.ToString());
            throw;
        }
        Trace("CreateTravelButton end");
    }

    private void OpenTravelDialog()
    {
        Trace("OpenTravelDialog start");
        try
        {
            // existing OpenTravelDialog code...
        }
        catch (Exception ex)
        {
            Trace("OpenTravelDialog EX: " + ex.ToString());
            throw;
        }
        Trace("OpenTravelDialog end");
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