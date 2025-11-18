using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

public partial class TravelButtonUI : MonoBehaviour
{
    // Compatibility forwarder: one-arg overload
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj)
    {
        // Forward to the 4-arg overload with defaults
        return LoadSceneAndTeleportCoroutine(cityObj, 0, Vector3.zero, false);
    }

    // Compatibility forwarder: 4-arg overload (object cityObj, cost, coordsHint, haveCoordsHint)
    public IEnumerator LoadSceneAndTeleportCoroutine(object cityObj, int cost, Vector3 coordsHint, bool haveCoordsHint)
    {
        // Resolved values (filled in below, before any yield)
        string sceneName = null;
        string targetName = null;
        Vector3 coordsToUse = Vector3.zero;
        bool haveCoordsFinal = false;
        int priceToUse = cost;
        bool resolved = false;

        try
        {
            // Try to resolve typed data (no yields here)
            if (CityMappingHelpers.TryResolveCityDataFromObject(cityObj, out string resolvedScene, out string resolvedTarget, out Vector3 resolvedCoords, out bool resolvedHaveCoords, out int resolvedPrice))
            {
                sceneName = resolvedScene;
                targetName = resolvedTarget;
                // prefer explicit args where provided
                priceToUse = (cost > 0) ? cost : (resolvedPrice > 0 ? resolvedPrice : 0);

                if (haveCoordsHint)
                {
                    coordsToUse = coordsHint;
                    haveCoordsFinal = true;
                }
                else if (resolvedHaveCoords)
                {
                    coordsToUse = resolvedCoords;
                    haveCoordsFinal = true;
                }

                resolved = !string.IsNullOrEmpty(sceneName) || haveCoordsFinal;
            }
            else
            {
                // Could not resolve at all
                string typeName = cityObj == null ? "<null>" : cityObj.GetType().FullName;
                string toStr = "<ToString failed>";
                try { toStr = cityObj?.ToString() ?? "<null>"; } catch { }
                TBLog.Warn($"LoadSceneAndTeleportCoroutine(compat): couldn't resolve city data from object (type={typeName}, tostring='{toStr}'). Aborting.");
                try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
                yield break; // safe: this yield is outside any try with catch (we are still inside the method but not inside the try/catch block)
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine(compat): unexpected error: " + ex);
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        if (!resolved)
        {
            TBLog.Warn("LoadSceneAndTeleportCoroutine(compat): resolved nothing for provided cityObj; aborting.");
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        // Now it's safe to yield (we are outside the try/catch scope)
        yield return StartCoroutine(TryTeleportThenChargeExplicit(sceneName, targetName, coordsToUse, haveCoordsFinal, priceToUse));
    }

    // helper inside TravelButtonUI
    // (insert into the TravelButtonUI partial class)
    private IEnumerator ForwardCityObjectToExplicit(object cityObj, int cost = 0, Vector3 coordsHint = default, bool haveCoordsHint = false)
    {
        if (cityObj == null) { TBLog.Warn("ForwardCityObjectToExplicit: cityObj null"); yield break; }

        // Try to cast to CityEntry first
        if (cityObj is CityEntry ce)
        {
            Vector3 coordsVec = Vector3.zero;
            bool ceHasCoords = (ce.coords != null && ce.coords.Length >= 3);
            if (ceHasCoords) coordsVec = new Vector3(ce.coords[0], ce.coords[1], ce.coords[2]);
            yield return StartCoroutine(TryTeleportThenChargeExplicit(ce.sceneName, ce.targetGameObjectName, coordsVec, ceHasCoords, ce.price));
            yield break;
        }

        // Fallback: reflect minimal fields
        string sceneName = null;
        string target = null;
        int price = cost;
        Vector3 coords = coordsHint;
        bool haveCoords = haveCoordsHint;

        try
        {
            var t = cityObj.GetType();
            var sn = t.GetProperty("sceneName") ?? t.GetProperty("SceneName");
            if (sn != null) sceneName = sn.GetValue(cityObj) as string;
            var tg = t.GetProperty("targetGameObjectName") ?? t.GetProperty("TargetGameObjectName") ?? t.GetProperty("target");
            if (tg != null) target = tg.GetValue(cityObj) as string;
            var pr = t.GetProperty("price") ?? t.GetProperty("Price");
            if (pr != null)
            {
                var val = pr.GetValue(cityObj);
                if (val != null) price = Convert.ToInt32(val);
            }
            var coordsP = t.GetProperty("coords") ?? t.GetProperty("Coords");
            if (coordsP != null)
            {
                var v = coordsP.GetValue(cityObj);
                if (v is double[] da && da.Length >= 3) { coords = new Vector3((float)da[0], (float)da[1], (float)da[2]); haveCoords = true; }
                else if (v is float[] fa && fa.Length >= 3) { coords = new Vector3(fa[0], fa[1], fa[2]); haveCoords = true; }
                else if (v is System.Collections.IList list && list.Count >= 3)
                {
                    try { coords = new Vector3(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]), Convert.ToSingle(list[2])); haveCoords = true; } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            TBLog.Warn("ForwardCityObjectToExplicit: reflection failed: " + ex);
        }

        if (string.IsNullOrEmpty(sceneName) && !haveCoords)
        {
            TBLog.Warn("ForwardCityObjectToExplicit: cannot determine sceneName or coords; aborting.");
            try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
            yield break;
        }

        yield return StartCoroutine(TryTeleportThenChargeExplicit(sceneName, target, coords, haveCoords, price));
    }

    private IEnumerator ImmediateTeleportAndChargeCoroutine(TravelButton.City city, Vector3 groundedCoords, int cost, bool haveCoordsHint)
    {
        // snapshot player pos before teleport (if available) to determine whether movement occurred
        Vector3 beforePos = Vector3.zero;
        bool haveBeforePos = TryGetPlayerPosition(out beforePos);

        // Detection phase: resolve tbui or fallback player transform (no yields here)
        TravelButtonUI tbui = null;
        Transform fallbackPlayerTransform = null;
        try
        {
            tbui = UnityEngine.Object.FindObjectOfType<TravelButtonUI>();
        }
        catch (Exception ex)
        {
            TBLog.Warn("ImmediateTeleportAndChargeCoroutine: FindObjectOfType<TravelButtonUI> threw: " + ex);
            tbui = null;
        }

        if (tbui == null)
        {
            try
            {
                var gTag = GameObject.FindWithTag("Player");
                if (gTag != null) fallbackPlayerTransform = gTag.transform;
                else
                {
                    foreach (var g in GameObject.FindObjectsOfType<GameObject>())
                    {
                        if (!string.IsNullOrEmpty(g.name) && g.name.StartsWith("PlayerChar"))
                        {
                            fallbackPlayerTransform = g.transform;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TBLog.Warn("ImmediateTeleportAndChargeCoroutine: fallback transform lookup threw: " + ex);
                fallbackPlayerTransform = null;
            }
        }

        // --- YIELDABLE actions (performed outside try/catch blocks that have catches) ---

        // Preferred path: delegate to TravelButtonUI.SafeTeleportRoutine (which handles disabling physics, etc.)
        if (tbui != null)
        {
            // NOTE: do not wrap this yield in a try/catch with a catch/finally (compiler restriction).
            yield return tbui.SafeTeleportRoutine(null, groundedCoords);
        }
        else if (fallbackPlayerTransform != null)
        {
            // Minimal local fallback: clamp Y relative to player and wait two frames
            Vector3 safe = groundedCoords;
            try
            {
                safe.y = Mathf.Clamp(groundedCoords.y, fallbackPlayerTransform.position.y - 100f, fallbackPlayerTransform.position.y + 100f);
            }
            catch
            {
                // if reading fallbackPlayerTransform.position fails for any reason, leave safe as groundedCoords
            }

            try { fallbackPlayerTransform.position = safe; } catch (Exception ex) { TBLog.Warn("ImmediateTeleportAndChargeCoroutine: fallback set position failed: " + ex); }
            yield return null;
            yield return null;
        }
        else
        {
            TBLog.Warn("ImmediateTeleportAndChargeCoroutine: no TravelButtonUI and no fallback player transform; will try scene-load fallback.");
            // no yields here - immediate fallthrough to movement check and eventual fallback
        }

        // After the safe-teleport attempt, check whether the player moved.
        Vector3 afterPos = Vector3.zero;
        bool haveAfterPos = TryGetPlayerPosition(out afterPos);
        bool moved = false;
        if (haveBeforePos && haveAfterPos)
        {
            moved = (afterPos - beforePos).sqrMagnitude > 0.01f;
        }
        else if (haveAfterPos && !haveBeforePos)
        {
            // If we couldn't get before position but can after, assume success.
            moved = true;
        }

        if (moved)
        {
            TBLog.Info($"ImmediateTeleportAndChargeCoroutine: immediate teleport to '{city.name}' appears to have occurred (afterPos={afterPos}). Proceeding to charge/persist/cleanup.");

            try { TravelButton.OnSuccessfulTeleport(city.name); } catch { }

            try
            {
                bool charged = CurrencyHelpers.AttemptDeductSilverDirect(cost, false);
                if (!charged)
                {
                    TBLog.Warn($"ImmediateTeleportAndChargeCoroutine: Teleported to {city.name} but failed to deduct {cost} silver.");
                    ShowInlineDialogMessage($"Teleported to {city.name} (failed to charge {cost} {TravelButton.cfgCurrencyItem.Value})");
                }
                else
                {
                    ShowInlineDialogMessage($"Teleported to {city.name}");
                }
            }
            catch (Exception exCharge)
            {
                TBLog.Warn("ImmediateTeleportAndChargeCoroutine: charge attempt threw: " + exCharge);
                ShowInlineDialogMessage($"Teleported to {city.name} (charge error)");
            }

            try { TravelButton.PersistCitiesToPluginFolder(); } catch { }

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
            catch (Exception exCleanup)
            {
                TBLog.Warn("ImmediateTeleportAndChargeCoroutine: cleanup threw: " + exCleanup);
            }

            yield break;
        }
        else
        {
            TBLog.Warn($"ImmediateTeleportAndChargeCoroutine: safe-teleport did not move the player for '{city.name}'. Falling back to scene-load teleport.");
            // If safe-teleport didn't produce movement, try the existing scene-load coroutine path (keeps existing behavior).
            try
            {
                StartCoroutine(LoadSceneAndTeleportCoroutine(city, cost, groundedCoords, haveCoordsHint));
            }
            catch (Exception exFallback)
            {
                TBLog.Warn("ImmediateTeleportAndChargeCoroutine: failed to start LoadSceneAndTeleportCoroutine fallback: " + exFallback);
                // final fallback: notify player and reset UI
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
                    TBLog.Warn("ImmediateTeleportAndChargeCoroutine: failed to re-enable buttons after failed teleport: " + exEnable);
                }
            }
            yield break;
        }
    }

}