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

    private void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
    }

    void Start()
    {
        TravelButtonPlugin.LogInfo("CityDiscovery.Start: initializing city discovery system.");
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
            TravelButtonPlugin.LogWarning("CityDiscovery.Update failed: " + ex);
        }
    }

    private void PollForNearbyCities()
    {
        try
        {
            var pt = FindPlayerTransform();
            if (pt == null)
            {
                TravelButtonPlugin.LogWarning("CityDiscovery: PollForNearbyCities - player transform not found.");
                return;
            }

            var ppos = pt.position;
            var cities = GetCitiesList();
            if (cities == null)
            {
                TravelButtonPlugin.LogWarning("CityDiscovery: PollForNearbyCities - could not locate TravelButtonMod.Cities.");
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name ?? "";

            foreach (var city in cities)
            {
                try
                {
                    if (string.IsNullOrEmpty(city.name)) continue;
                    if (TravelButtonVisitedManager.IsCityVisited(city.name)) continue;

                    // Try to get a known world position for the city (GameObject, explicit coords or name-match)
                    Vector3? candidate = GetCityPosition(city);

                    // If we don't have candidate coords but the active scene name contains the city name,
                    // treat the player's current position as the city's discovered position.
                    if (candidate == null)
                    {
                        if (!string.IsNullOrEmpty(sceneName) && sceneName.IndexOf(city.name, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            TravelButtonPlugin.LogInfo($"CityDiscovery: Scene '{sceneName}' matches city '{city.name}' - using player position as candidate.");
                            candidate = ppos;
                        }
                    }

                    if (candidate == null)
                    {
                        TravelButtonPlugin.LogInfo($"CityDiscovery: No candidate position for city '{city.name}' (skipping).");
                        continue;
                    }

                    float dist = Vector3.Distance(ppos, candidate.Value);
                    TravelButtonPlugin.LogInfo($"CityDiscovery: Dist to '{city.name}' = {dist:F1} (threshold {DiscoverRadius}).");

                    if (dist <= DiscoverRadius)
                    {
                        // pass the discovered world position so it can be saved
                        TravelButtonVisitedManager.MarkVisited(city.name, candidate.Value);
                        TravelButtonPlugin.LogInfo($"CityDiscovery: Auto-discovered city '{city.name}' at distance {dist:F1}.");
                    }
                }
                catch (Exception ex)
                {
                    TravelButtonPlugin.LogWarning("CityDiscovery.PollForNearbyCities failed for a city: " + ex);
                }
            }
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("CityDiscovery.PollForNearbyCities failed: " + ex);
        }
    }

    private void ForceMarkNearestCity()
    {
        var pt = FindPlayerTransform();
        if (pt == null)
        {
            TravelButtonPlugin.LogWarning("CityDiscovery: ForceMarkNearestCity - player transform not found.");
            return;
        }

        var ppos = pt.position;
        var cities = GetCitiesList();
        if (cities == null)
        {
            TravelButtonPlugin.LogWarning("CityDiscovery: ForceMarkNearestCity - could not locate TravelButtonMod.Cities.");
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
                if (pos == null)
                {
                    // If no explicit pos, try matching scene name -> consider player pos
                    var sceneName = SceneManager.GetActiveScene().name ?? "";
                    if (!string.IsNullOrEmpty(sceneName) && sceneName.IndexOf(city.name, StringComparison.OrdinalIgnoreCase) >= 0)
                        pos = ppos;
                }
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
            TravelButtonPlugin.LogInfo($"CityDiscovery: Force-marked nearest city '{bestCity.name}' (dist {bestDist:F1}).");
        }
        else
        {
            TravelButtonPlugin.LogWarning("CityDiscovery: ForceMarkNearestCity - no city positions available to mark.");
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
            TravelButtonPlugin.LogWarning("CityDiscovery.EnforceVisitedGating failed: " + ex);
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
        // 1) Tag
        try
        {
            var tagged = GameObject.FindWithTag("Player");
            if (tagged != null)
            {
                return tagged.transform;
            }
        }
        catch { }

        // 2) Common runtime types
        string[] playerTypeCandidates = new string[] { "PlayerCharacter", "PlayerEntity", "Character", "PC_Player", "PlayerController", "LocalPlayer", "Player" };
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
                            return comp.transform;
                        }
                    }
                }
            }
            catch { }
        }

        // 3) Fallback: camera's follow target or main camera transform
        try
        {
            if (Camera.main != null)
            {
                return Camera.main.transform;
            }
        }
        catch { }

        // 4) Brute-force heuristic: first transform with 'player' in name
        try
        {
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var tr in allTransforms)
            {
                if (tr == null) continue;
                if (tr.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return tr;
                }
            }
        }
        catch { }

        return null;
    }

    public Vector3? GetCityPosition(TravelButtonMod.City city)
    {
        if (city == null) return null;
        try
        {
            // 1) explicit target GameObject name
            if (!string.IsNullOrEmpty(city.targetGameObjectName))
            {
                var go = GameObject.Find(city.targetGameObjectName);
                if (go != null) return go.transform.position;
            }

            // 2) explicit coords from config / visited metadata
            if (city.coords != null && city.coords.Length >= 3)
            {
                return new Vector3(city.coords[0], city.coords[1], city.coords[2]);
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
                        return tr.position;
                    }
                }
            }
            catch { }

            // not found
            return null;
        }
        catch (Exception ex)
        {
            TravelButtonPlugin.LogWarning("GetCityPosition exception: " + ex);
            return null;
        }
    }

    private void LogPlayerPosition()
    {
        var pt = FindPlayerTransform();
        if (pt == null) TravelButtonPlugin.LogInfo("CityDiscovery: player transform not found for LogPlayerPosition.");
        else TravelButtonPlugin.LogInfo($"CityDiscovery: Player pos = {pt.position}");
    }
}