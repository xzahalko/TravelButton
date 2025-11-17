// src/TravelButtonUI.UIButtonWiringFix.cs
// Updated PopulateCityButtons that expects CityEntry.coords to be float[] (no casting needed).

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class TravelButtonUI : MonoBehaviour
{
    // Call this with the List<CityEntry> returned by LoadCitiesFromJson
    /// <summary>
    /// Populate the UI list of city buttons.
    /// - contentParent: parent Transform that will contain instantiated button items (e.g. content of a scroll view).
    /// - cities: sequence of CityEntry objects (can be List, array, etc.).
    /// - buttonPrefab: prefab containing a Button component and a child label (Text or TMP).
    ///
    /// Behavior:
    /// - Clears existing children under contentParent (you can adapt to reuse/pooling).
    /// - Instantiates one button per city and sets the label.
    /// - Captures the CityEntry in a local for safe closure usage.
    /// - Wires onClick to start explicit teleport coroutine using the typed CityEntry (no reflection).
    /// - Sets button.interactable according to simple availability checks (example: sceneName present and enough data).
    /// - Logs what was wired so you can debug "destination not configured" issues easily.
    /// </summary>
    public void PopulateCityButtons(Transform contentParent, IEnumerable<CityEntry> cities, GameObject buttonPrefab)
    {
        if (contentParent == null)
        {
            TBLog.Warn("PopulateCityButtons: contentParent is null");
            return;
        }

        if (buttonPrefab == null)
        {
            TBLog.Warn("PopulateCityButtons: buttonPrefab is null");
            return;
        }

        if (cities == null)
        {
            TBLog.Info("PopulateCityButtons: no cities to populate (cities is null).");
            // Optionally clear UI
            foreach (Transform child in contentParent) GameObject.Destroy(child.gameObject);
            return;
        }

        // Remove old children (simple approach)
        foreach (Transform child in contentParent)
        {
            try { GameObject.Destroy(child.gameObject); } catch { }
        }

        // Instantiate buttons
        foreach (var city in cities)
        {
            if (city == null)
            {
                TBLog.Warn("PopulateCityButtons: encountered null CityEntry; skipping.");
                continue;
            }

            var go = GameObject.Instantiate(buttonPrefab, contentParent, false);
            if (go == null)
            {
                TBLog.Warn("PopulateCityButtons: Instantiate returned null for prefab.");
                continue;
            }

            var btn = go.GetComponent<Button>();
            if (btn == null)
            {
                TBLog.Warn("PopulateCityButtons: instantiated prefab missing Button component; destroying.");
                GameObject.Destroy(go);
                continue;
            }

            // Set label text (supports UnityEngine.UI.Text and TMP if available)
            bool labelSet = false;
            try
            {
                var text = go.GetComponentInChildren<Text>(true);
                if (text != null)
                {
                    text.text = city.name ?? "<unknown>";
                    labelSet = true;
                }
            }
            catch { }

#if TMP_PRESENT
            if (!labelSet)
            {
                try
                {
                    var tmp = go.GetComponentInChildren<TMP_Text>(true);
                    if (tmp != null)
                    {
                        tmp.text = city.name ?? "<unknown>";
                        labelSet = true;
                    }
                }
                catch { }
            }
#endif

            if (!labelSet)
            {
                TBLog.Warn($"PopulateCityButtons: could not find a UI label on the button prefab to set name for '{city.name}'.");
            }

            // Decide whether this city is available to click:
            // Example checks (customize as needed)
            bool haveSceneName = !string.IsNullOrEmpty(city.sceneName);
            bool haveCoords = (city.coords != null && city.coords.Length >= 3);
            bool hasEnoughData = haveSceneName || haveCoords; // at least one way to teleport
            bool buttonEnabled = hasEnoughData; // could also check price, visited flags, config enabled, player money, etc.

            btn.interactable = buttonEnabled;

            // Capture local copy for closure
            CityEntry captured = city;

            // Remove any existing listeners to be safe (in case prefab has default ones)
            btn.onClick.RemoveAllListeners();

            // Add listener that logs and starts the explicit teleport coroutine
            btn.onClick.AddListener(() =>
            {
                TBLog.Info($"PopulateCityButtons: click -> name='{captured.name}' scene='{captured.sceneName}' target='{captured.targetGameObjectName}' price={captured.price}");

                if (string.IsNullOrEmpty(captured.sceneName) && (captured.coords == null || captured.coords.Length < 3))
                {
                    TBLog.Warn($"PopulateCityButtons: destination not configured for city '{captured.name}' (no sceneName and no coords).");
                    try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport cancelled: destination not configured."); } catch { }
                    return;
                }

                Vector3 coordsVec = Vector3.zero;
                bool haveCoordsVec = false;
                if (captured.coords != null && captured.coords.Length >= 3)
                {
                    coordsVec = new Vector3(captured.coords[0], captured.coords[1], captured.coords[2]);
                    haveCoordsVec = true;
                }

                // Start the explicit coroutine (no reflection); this preserves payment/refund logic in that method.
                try
                {
                    StartCoroutine(TryTeleportThenChargeExplicit(
                        captured.sceneName,
                        captured.targetGameObjectName,
                        coordsVec,
                        haveCoordsVec,
                        captured.price
                    ));
                }
                catch (Exception ex)
                {
                    TBLog.Warn("PopulateCityButtons: failed to start teleport coroutine: " + ex);
                    try { TravelButtonPlugin.ShowPlayerNotification?.Invoke("Teleport failed: internal error."); } catch { }
                }
            });

            // Optionally: add hover tooltip, disable visual state, show price, visited marker, etc.
            // Example: set a child "price" Text if present
            try
            {
                var priceText = go.transform.Find("Price")?.GetComponent<Text>();
                if (priceText != null) priceText.text = $"{city.price}";
            }
            catch { }

            // If button shouldn't be interactable, optionally add a semi-opaque overlay or tooltip (not implemented here).
        } // foreach city
    } // PopulateCityButtons
}