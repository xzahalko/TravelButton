using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI helper MonoBehaviour renamed so it no longer collides with the plugin class name.
/// Responsible for injecting a Travel button into the inventory UI (visuals are created at runtime).
/// </summary>
public class TravelButtonUI : MonoBehaviour
{
    private Button travelButton;

    void Start()
    {
        // Assuming an inventory UI is found here
        CreateTravelButton();
    }

    void CreateTravelButton()
    {
        // Create a button and configure it
        GameObject buttonObject = new GameObject("TravelButton");
        travelButton = buttonObject.AddComponent<Button>();

        // Make it visible: add Image and Text and parent properly in your real integration
        var img = buttonObject.AddComponent<Image>();
        img.color = new Color(0.9f, 0.9f, 0.9f, 1f);

        var rt = buttonObject.GetComponent<RectTransform>();
        if (rt == null) rt = buttonObject.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(160, 30);

        // add child label
        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(buttonObject.transform, false);
        var txt = labelGO.AddComponent<Text>();
        txt.text = "Travel";
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color = Color.black;
        txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        // Set up button click listener to open dialog
        travelButton.onClick.AddListener(OpenDialog);
    }

    void OpenDialog()
    {
        // Logic to open the in-game dialog (placeholder)
        Debug.Log("Travel button clicked! Opening dialog...");
        // Display dialog logic goes here
    }
}