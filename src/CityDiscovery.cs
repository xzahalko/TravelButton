using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// MonoBehaviour that auto-discovers cities when the player gets within a certain radius.
/// Polls player position periodically and checks against city coordinates or target GameObjects.
/// Marks cities as visited via TravelButtonVisitedManager when within discover radius.
/// </summary>
public class CityDiscovery : MonoBehaviour
{
    // Discovery radius in Unity units (adjust based on game scale)
    private const float DiscoverRadius = 50f;
    
    // How often to check for discovery (in seconds)
    private const float CheckInterval = 5f;
    
    private Transform playerTransform;
    private bool isRunning = false;

    void Start()
    {
        TravelButtonMod.LogInfo("CityDiscovery.Start: initializing city discovery system.");
        DontDestroyOnLoad(this.gameObject);
        
        // Ensure visited manager is loaded
        TravelButtonVisitedManager.EnsureLoaded();
        
        // Start the discovery polling coroutine
        StartCoroutine(DiscoveryPollCoroutine());
    }

    /// <summary>
    /// Coroutine that periodically checks player position against city locations.
    /// </summary>
    private IEnumerator DiscoveryPollCoroutine()
    {
        isRunning = true;
        TravelButtonMod.LogInfo("CityDiscovery: starting discovery poll coroutine.");
        
        while (isRunning)
        {
            // Find player transform if we don't have it yet
            if (playerTransform == null)
            {
                playerTransform = FindPlayerTransform();
                if (playerTransform == null)
                {
                    // Wait and retry finding player
                    yield return new WaitForSeconds(CheckInterval);
                    continue;
                }
            }
            
            // Check each city for discovery
            try
            {
                if (TravelButtonMod.Cities != null)
                {
                    foreach (var city in TravelButtonMod.Cities)
                    {
                        if (city == null || string.IsNullOrEmpty(city.name))
                            continue;
                        
                        // Skip if already visited
                        if (TravelButtonVisitedManager.IsCityVisited(city.name))
                            continue;
                        
                        // Check if player is within discover radius
                        if (IsPlayerNearCity(city))
                        {
                            TravelButtonMod.LogInfo($"CityDiscovery: Player discovered city '{city.name}'!");
                            TravelButtonVisitedManager.MarkVisited(city.name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonMod.LogWarning($"CityDiscovery: exception in poll coroutine: {ex}");
            }
            
            // Wait before next check
            yield return new WaitForSeconds(CheckInterval);
        }
    }

    /// <summary>
    /// Check if the player is near a city (within DiscoverRadius).
    /// </summary>
    private bool IsPlayerNearCity(TravelButtonMod.City city)
    {
        if (playerTransform == null)
            return false;
        
        Vector3 playerPos = playerTransform.position;
        Vector3? cityPos = GetCityPosition(city);
        
        if (!cityPos.HasValue)
            return false;
        
        float distance = Vector3.Distance(playerPos, cityPos.Value);
        return distance <= DiscoverRadius;
    }

    /// <summary>
    /// Get the position of a city (from targetGameObject or coords).
    /// Returns null if no valid position is available.
    /// </summary>
    private Vector3? GetCityPosition(TravelButtonMod.City city)
    {
        // Try to find target GameObject first
        if (!string.IsNullOrEmpty(city.targetGameObjectName))
        {
            GameObject targetGO = GameObject.Find(city.targetGameObjectName);
            if (targetGO != null)
            {
                return targetGO.transform.position;
            }
        }
        
        // Try coords
        if (city.coords != null && city.coords.Length >= 3)
        {
            return new Vector3(city.coords[0], city.coords[1], city.coords[2]);
        }
        
        return null;
    }

    /// <summary>
    /// Find the player transform in the scene.
    /// Uses multiple strategies to locate the player.
    /// </summary>
    private Transform FindPlayerTransform()
    {
        // Try by tag first
        GameObject tagged = GameObject.FindWithTag("Player");
        if (tagged != null)
        {
            TravelButtonMod.LogInfo("CityDiscovery: found player by tag 'Player'.");
            return tagged.transform;
        }
        
        // Try common player type names
        string[] playerTypeCandidates = new string[] 
        { 
            "PlayerCharacter", "PlayerEntity", "Character", 
            "PC_Player", "PlayerController", "LocalPlayer" 
        };
        
        foreach (var typeName in playerTypeCandidates)
        {
            try
            {
                Type t = Type.GetType(typeName + ", Assembly-CSharp");
                if (t != null)
                {
                    var objs = UnityEngine.Object.FindObjectsOfType(t);
                    if (objs != null && objs.Length > 0)
                    {
                        var comp = objs[0] as Component;
                        if (comp != null)
                        {
                            TravelButtonMod.LogInfo($"CityDiscovery: found player via type {typeName}.");
                            return comp.transform;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TravelButtonMod.LogWarning($"CityDiscovery: exception checking type {typeName}: {ex.Message}");
            }
        }
        
        // Try by name heuristic
        var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
        foreach (var tr in allTransforms)
        {
            if (tr.name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tr.name.IndexOf("pc_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                TravelButtonMod.LogInfo($"CityDiscovery: found player by name heuristic: {tr.name}");
                return tr;
            }
        }
        
        TravelButtonMod.LogWarning("CityDiscovery: could not locate player transform.");
        return null;
    }

    void OnDestroy()
    {
        isRunning = false;
        TravelButtonMod.LogInfo("CityDiscovery: destroyed.");
    }
}
