using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

//
// CityDiscovery:
// - Polls player position and auto-marks nearby cities as visited
// - When marking visited it passes the discovered world position so saved visited metadata includes coords
// - EnforceVisitedGating will continue to disable unvisited city buttons in an open dialog
//
public class CityDiscovery : MonoBehaviour
{
    private float timer = 0f;
    private const float PollInterval = 1.0f;
    private const float DiscoverRadius = 6.0f;

    void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
    }

    void Start()
    {
        TravelButtonMod.LogInfo("CityDiscovery.Start: initializing city discovery system.");
        // Diagnostic: try to print potential built-in visited fields
        try { TravelButtonVisitedManager.LogPlayerCandidateVisitedFields(); } catch { }
    }

    void Update()
    {
        try
        {
            // Hotkeys for debugging
            if (Input.GetKeyDown(KeyCode.F9))
            {
                LogPlayerPosition();
            }
            if (Input.GetKeyDown(KeyCode.F10))
            {
                ForceMarkNearestCity();
            }

            timer += Time.unscaledDeltaTime;
            if (timer >= PollInterval)
            {
                timer = 0f;
                PollForNearbyCities();
                EnforceVisitedGating();
            }
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("CityDiscovery.Update failed: " + ex);
        }
    }

    private void PollForNearbyCities()
    {
        try
        {
            var pt = FindPlayerTransform();
            if (pt == null)
            {
                TravelButtonMod.LogWarning("CityDiscovery: PollForNearbyCities - player transform not found.");
                return;
            }

            var ppos = pt.position;
            var cities = GetCitiesList();
            if (cities == null)
            {
                TravelButtonMod.LogWarning("CityDiscovery: PollForNearbyCities - could not locate TravelButtonMod.Cities.");
                return;
            }

            foreach (var city in cities)
            {
                try
                {
                    if (string.IsNullOrEmpty(city.name)) continue;
                    if (TravelButtonVisitedManager.IsCityVisited(city.name)) continue;

                    Vector3? candidate = GetCityPosition(city);
                    if (candidate == null)
                    {
                        TravelButtonMod.LogInfo($"CityDiscovery: No candidate position for city '{city.name}' (skipping).");
                        continue;
                    }

                    float dist = Vector3.Distance(ppos, candidate.Value);
                    TravelButtonMod.LogInfo($"CityDiscovery: Dist to '{city.name}' = {dist:F1} (threshold {DiscoverRadius}).");

                    if (dist <= DiscoverRadius)
                    {
                        // store the discovered position with visited metadata
                        TravelButtonVisitedManager.MarkVisited(city.name, candidate.Value);
                        TravelButtonMod.LogInfo($"CityDiscovery: Auto-discovered city '{city.name}' at distance {dist:F1}.");
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonMod.LogWarning("CityDiscovery.PollForNearbyCities failed for a city: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("CityDiscovery.PollForNearbyCities failed: " + ex);
        }
    }

    private void ForceMarkNearestCity()
    {
        var pt = FindPlayerTransform();
        if (pt == null)
        {
            TravelButtonMod.LogWarning("CityDiscovery: ForceMarkNearestCity - player transform not found.");
            return;
        }

        var ppos = pt.position;
        var cities = GetCitiesList();
        if (cities == null)
        {
            TravelButtonMod.LogWarning("CityDiscovery: ForceMarkNearestCity - could not locate TravelButtonMod.Cities.");
            return;
        }

        float bestDist = float.MaxValue;
        TravelButtonMod.City bestCity = null;
        Vector3? bestPos = null;

        foreach (var city in cities)
        {
            try
            {
                var pos = GetCityPosition(city);
                if (pos == null) continue;
                float d = Vector3.Distance(ppos, pos.Value);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestCity = city;
                    bestPos = pos;
                }
            }
            catch { }
        }

        if (bestCity != null)
        {
            TravelButtonVisitedManager.MarkVisited(bestCity.name, bestPos);
            TravelButtonMod.LogInfo($"CityDiscovery: Force-marked nearest city '{bestCity.name}' (dist {bestDist:F1}).");
        }
        else
        {
            TravelButtonMod.LogWarning("CityDiscovery: ForceMarkNearestCity - no city positions available to mark.");
        }
    }

    // Keep the UI gating function; it disables buttons for unvisited cities
    private void EnforceVisitedGating()
    {
        try
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root == null || !root.activeInHierarchy) continue;
                var content = root.transform.Find("ScrollArea/Viewport/Content");
                if (content == null) continue;

                for (int i = 0; i < content.childCount; i++)
                {
                    var child = content.GetChild(i);
                    var btn = child.GetComponent<UnityEngine.UI.Button>();
                    var img = child.GetComponent<UnityEngine.UI.Image>();
                    if (btn == null || img == null) continue;

                    string objName = child.name;
                    if (!objName.StartsWith("CityButton_")) continue;
                    string cityName = objName.Substring("CityButton_".Length);

                    bool visited = TravelButtonVisitedManager.IsCityVisited(cityName);
                    if (!visited)
                    {
                        if (btn.interactable)
                        {
                            btn.interactable = false;
                            img.color = new Color(0.18f, 0.18f, 0.18f, 1f);
                        }
                    }
                    // if visited: leave existing interactable state to be handled by other checks
                }
                break;
            }
        }
        catch (Exception ex)
        {
            TravelButtonMod.LogWarning("CityDiscovery.EnforceVisitedGating failed: " + ex);
        }
    }

    // --- helper utilities ---

    private IList<TravelButtonMod.City> GetCitiesList()
    {
        IList<TravelButtonMod.City> cities = null;
        var citiesField = typeof(TravelButtonMod).GetField("Cities", BindingFlags.Public | BindingFlags.Static);
        if (citiesField != null) cities = citiesField.GetValue(null) as IList<TravelButtonMod.City>;
        if (cities == null)
        {
            var prop = typeof(TravelButtonMod).GetProperty("Cities", BindingFlags.Public | BindingFlags.Static);
            if (prop != null) cities = prop.GetValue(null, null) as IList<TravelButtonMod.City>;
        }
        return cities;
    }

    private Transform FindPlayerTransform()
    {
        // (same robust player-finding logic as original; omitted here for brevity but kept in file in real code)
        // For clarity in this snippet, we'll keep the important checks from the original implementation.

        try
        {
            var tagged = GameObject.FindWithTag("Player");
            if (tagged != null) return tagged.transform;
        }
        catch { }

        string[] playerTypeCandidates = new string[] { "PlayerCharacter", "PlayerEntity", "Character", "PC_Player", "PlayerController", "LocalPlayer", "Player" };
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
                        if (comp != null) return comp.transform;
                    }
                }
            }
            catch { }
        }

        try
        {
            if (Camera.main != null) return Camera.main.transform;
        }
        catch { }

        try
        {
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var tr in allTransforms)
            {
                if (tr == null) continue;
                if (tr.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                    return tr;
            }
        }
        catch { }

        return null;
    }

    private Vector3? GetCityPosition(TravelButtonMod.City city)
    {
        if (city == null) return null;
        try
        {
            if (!string.IsNullOrEmpty(city.targetGameObjectName))
            {
                var go = GameObject.Find(city.targetGameObjectName);
                if (go != null) return go.transform.position;
            }
            if (city.coords != null && city.coords.Length >= 3)
            {
                return new Vector3(city.coords[0], city.coords[1], city.coords[2]);
            }
        }
        catch { }
        return null;
    }

    private void LogPlayerPosition()
    {
        var pt = FindPlayerTransform();
        if (pt == null) TravelButtonMod.LogInfo("CityDiscovery: player transform not found for LogPlayerPosition.");
        else TravelButtonMod.LogInfo($"CityDiscovery: Player pos = {pt.position}");
    }
}