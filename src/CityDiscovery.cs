using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Polls player position to auto-mark cities visited and enforces dialog gating.
/// Improvements:
/// - More robust player transform detection (tries tag, common types, children of 'Players' containers, camera, etc.)
/// - Diagnostic distance logging for each city
/// - F9 logs player position; F10 force-marks nearest city (testing)
/// - Slightly larger radius/logging to help debugging (tune DiscoverRadius as needed)
/// </summary>
public class CityDiscovery : MonoBehaviour
{
    private const float PollInterval = 2.0f;
    private const float DiscoverRadius = 20.0f; // you can temporarily increase this if needed

    private float timer = 0f;

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

    private void LogPlayerPosition()
    {
        var pt = FindPlayerTransform();
        if (pt != null)
        {
            var p = pt.position;
            TravelButtonMod.LogInfo($"CityDiscovery: Player position = {p.x:F3}, {p.y:F3}, {p.z:F3} (found by {playerFinderDescription})");
        }
        else
        {
            TravelButtonMod.LogWarning("CityDiscovery: Player Transform not found when logging position.");
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
        if (cities == null || cities.Count == 0)
        {
            TravelButtonMod.LogWarning("CityDiscovery: ForceMarkNearestCity - no cities available.");
            return;
        }

        float bestDist = float.MaxValue;
        TravelButtonMod.City bestCity = null;
        foreach (var city in cities)
        {
            var pos = GetCityPosition(city);
            if (pos == null) continue;
            float d = Vector3.Distance(ppos, pos.Value);
            if (d < bestDist)
            {
                bestDist = d;
                bestCity = city;
            }
        }

        if (bestCity != null)
        {
            TravelButtonVisitedManager.MarkVisited(bestCity.name);
            TravelButtonMod.LogInfo($"CityDiscovery: Force-marked nearest city '{bestCity.name}' (dist {bestDist:F1}).");
        }
        else
        {
            TravelButtonMod.LogWarning("CityDiscovery: ForceMarkNearestCity - no city positions available to mark.");
        }
    }

    private void PollForNearbyCities()
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
                    TravelButtonVisitedManager.MarkVisited(city.name);
                    TravelButtonMod.LogInfo($"CityDiscovery: Auto-discovered city '{city.name}' at distance {dist:F1}.");
                }
            }
            catch (Exception ex)
            {
                TravelButtonMod.LogWarning("CityDiscovery: error while checking city: " + ex);
            }
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

    // More robust player transform detection. Sets playerFinderDescription for logging.
    private string playerFinderDescription = "unknown";
    private Transform FindPlayerTransform()
    {
        // 1) Tag
        try
        {
            var tagged = GameObject.FindWithTag("Player");
            if (tagged != null)
            {
                playerFinderDescription = "tag:Player";
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
                var t = Type.GetType(tname + ", Assembly-CSharp");
                if (t != null)
                {
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0)
                    {
                        var comp = objs[0] as Component;
                        if (comp != null)
                        {
                            playerFinderDescription = $"type:{tname}";
                            return comp.transform;
                        }
                    }
                }
            }
            catch { }
        }

        // 3) Heuristic: if there's a root named "Players" (or similar) look for a child that looks like the player
        try
        {
            var allRoots = SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in allRoots)
            {
                if (root == null) continue;
                string rn = root.name.ToLowerInvariant();
                if (rn.Contains("player") || rn.Contains("players") || rn.Contains("playercontainer") || rn.Contains("players_root"))
                {
                    // search children for an object with 'player' in the name and active
                    var transforms = root.GetComponentsInChildren<Transform>(true);
                    foreach (var tr in transforms)
                    {
                        if (tr == null) continue;
                        string nameLow = tr.name.ToLowerInvariant();
                        if (nameLow.Contains("player") || nameLow.Contains("pc_") || nameLow.Contains("hero"))
                        {
                            playerFinderDescription = $"childOfRoot:{root.name}/{tr.name}";
                            return tr;
                        }
                    }
                }
            }
        }
        catch { }

        // 4) Fallback: camera's follow target or main camera transform
        try
        {
            if (Camera.main != null)
            {
                playerFinderDescription = "Camera.main";
                return Camera.main.transform;
            }
        }
        catch { }

        // 5) Brute-force heuristic: first transform with 'player' in name
        try
        {
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var tr in allTransforms)
            {
                if (tr == null) continue;
                if (tr.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    playerFinderDescription = $"heuristic:name:{tr.name}";
                    return tr;
                }
            }
        }
        catch { }

        playerFinderDescription = "not-found";
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
                return new Vector3((float)city.coords[0], (float)city.coords[1], (float)city.coords[2]);
            }
            // heuristic search for scene transform name
            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null) continue;
                if (t.name.IndexOf(city.name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.position;
            }
        }
        catch { }
        return null;
    }
}