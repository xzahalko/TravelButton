using System.Collections.Generic;

// Default city definitions for TravelButton mod
public static class TravelManager
{
    // Define the 6 default cities as specified
    public static List<TravelButton.City> DefaultCities = new List<TravelButton.City>
    {
        new TravelButton.City("Cierzo")
        {
            price = 200,
            coords = new float[] { 1410.3f, 6.7f, 1665.6f },
            targetGameObjectName = "Cierzo",
            sceneName = "CierzoNewTerrain",
            desc = "Cierzo - example description",
            visited = false,
            lastKnownVariant = "",
            enabled = true
        },
        new TravelButton.City("Levant")
        {
            price = 200,
            coords = new float[] { -55.2f, 1.0f, 79.3f },
            targetGameObjectName = "Levant_Location",
            sceneName = "Levant",
            desc = "Levant - example description",
            visited = false,
            lastKnownVariant = "",
            enabled = true
        },
        new TravelButton.City("Monsoon")
        {
            price = 200,
            coords = new float[] { 56.893f, -4.853f, 114.147f },
            targetGameObjectName = "Monsoon_Location",
            sceneName = "Monsoon",
            desc = "Monsoon - example description",
            visited = false,
            lastKnownVariant = "",
            enabled = true
        },
        new TravelButton.City("Berg")
        {
            price = 200,
            coords = new float[] { 1203.700f, -10.097f, 1376.038f },
            targetGameObjectName = "Berg",
            sceneName = "Berg",
            desc = "Berg - example description",
            visited = false,
            lastKnownVariant = "",
            enabled = true
        },
        new TravelButton.City("Harmattan")
        {
            price = 200,
            coords = new float[] { 93.7f, 65.4f, 767.8f },
            targetGameObjectName = "Harmattan_Location",
            sceneName = "Harmattan",
            desc = "Harmattan - example description",
            visited = false,
            lastKnownVariant = "",
            enabled = true
        },
        new TravelButton.City("Sirocco")
        {
            price = 200,
            coords = new float[] { 62.5f, 56.8f, -54.0f },
            targetGameObjectName = "Sirocco_Location",
            sceneName = "NewSirocco",
            desc = "Sirocco - example description",
            visited = false,
            lastKnownVariant = "",
            enabled = true
        }
    };
}
