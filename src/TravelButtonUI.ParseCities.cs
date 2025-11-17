// src/TravelButtonUI.ParseCities.cs
// Example: read the TravelButton_Cities.json file with Newtonsoft.Json and populate an in-memory list.
// Adjust paths / candidate locations to your existing logic — this shows the canonical approach.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

public partial class TravelButtonUI : MonoBehaviour
{
    // Replace your existing JSON-loading code with something like this.
    // jsonPath should be the path you wrote previously (e.g. BepInEx/config/TravelButton_Cities.json).
    // Returns the parsed list or null on failure.
    private List<CityEntry> LoadCitiesFromJson(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath))
            {
                TBLog.Warn($"LoadCitiesFromJson: file not found: {jsonPath}");
                return null;
            }

            string jsonText = File.ReadAllText(jsonPath);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                TBLog.Warn($"LoadCitiesFromJson: file empty: {jsonPath}");
                return null;
            }

            // Deserialize into typed objects; coords will be float[] if the JSON numbers are parseable as floats.
            CitiesRoot root = JsonConvert.DeserializeObject<CitiesRoot>(jsonText);
            if (root == null || root.cities == null)
            {
                TBLog.Warn("LoadCitiesFromJson: deserialized root or cities is null.");
                return null;
            }

            // Defensive: ensure coords arrays are float[] not double[] (handle mixed-number JSON)
            foreach (var c in root.cities)
            {
                if (c.coords == null)
                {
                    // optional: try to read fallback fields or log
                    TBLog.Info($"LoadCitiesFromJson: city '{c.name}' has no coords.");
                }
            }

            TBLog.Info($"LoadCitiesFromJson: loaded {root.cities.Count} cities from {jsonPath}");
            return root.cities;
        }
        catch (JsonException jex)
        {
            TBLog.Warn("LoadCitiesFromJson: JSON parse error: " + jex);
            return null;
        }
        catch (Exception ex)
        {
            TBLog.Warn("LoadCitiesFromJson: unexpected error: " + ex);
            return null;
        }
    }
}