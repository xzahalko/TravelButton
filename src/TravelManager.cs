using System;
using System.Collections.Generic;

/// <summary>
/// TravelManager defines the default 6 cities that serve as the base configuration.
/// These are seeded when TravelButton_Cities.json does not exist or is incomplete.
/// </summary>
public static class TravelManager
{
    /// <summary>
    /// Default 6 cities defined in the specification.
    /// sceneName "NewSirocco" is temporarily permanently disabled (visited = false).
    /// </summary>
    public static List<JsonCityConfig> GetDefaultCities()
    {
        return new List<JsonCityConfig>
        {
            new JsonCityConfig
            {
                name = "Cierzo",
                price = 200,
                coords = new float[] { 1410.3f, 6.7f, 1665.6f },
                targetGameObjectName = "Cierzo",
                sceneName = "CierzoNewTerrain",
                desc = "Cierzo - starting village",
                visited = false,
                lastKnownVariant = ""
            },
            new JsonCityConfig
            {
                name = "Berg",
                price = 200,
                coords = new float[] { 1039.0f, 20.0f, 1189.0f },
                targetGameObjectName = "Berg",
                sceneName = "Berg",
                desc = "Berg - mountain city",
                visited = false,
                lastKnownVariant = ""
            },
            new JsonCityConfig
            {
                name = "Monsoon",
                price = 200,
                coords = new float[] { 800.0f, 5.0f, 1300.0f },
                targetGameObjectName = "Monsoon",
                sceneName = "Monsoon",
                desc = "Monsoon - coastal town",
                visited = false,
                lastKnownVariant = ""
            },
            new JsonCityConfig
            {
                name = "Levant",
                price = 200,
                coords = new float[] { 600.0f, 10.0f, 900.0f },
                targetGameObjectName = "Levant",
                sceneName = "Levant",
                desc = "Levant - desert city",
                visited = false,
                lastKnownVariant = ""
            },
            new JsonCityConfig
            {
                name = "Harmattan",
                price = 200,
                coords = new float[] { 500.0f, 15.0f, 700.0f },
                targetGameObjectName = "Harmattan",
                sceneName = "Harmattan",
                desc = "Harmattan - oasis settlement",
                visited = false,
                lastKnownVariant = ""
            },
            new JsonCityConfig
            {
                name = "Sirocco",
                price = 200,
                coords = new float[] { 400.0f, 8.0f, 600.0f },
                targetGameObjectName = "Sirocco",
                sceneName = "NewSirocco",
                desc = "Sirocco - under construction (temporarily disabled)",
                visited = false, // permanently disabled according to spec
                lastKnownVariant = ""
            }
        };
    }

    /// <summary>
    /// Returns the default JSON structure including the cities array.
    /// </summary>
    public static JsonTravelConfig GetDefaultConfig()
    {
        return new JsonTravelConfig
        {
            cities = GetDefaultCities()
        };
    }
}
