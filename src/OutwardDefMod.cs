using UnityEngine;
using UnityEngine.UI;

public class OutwardDefMod : MonoBehaviour
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

        // Set button properties
        travelButton.GetComponent<RectTransform>().sizeDelta = new Vector2(160, 30);

        // Set up button click listener to open dialog
        travelButton.onClick.AddListener(OpenDialog);
    }

    void OpenDialog()
    {
        // Logic to open the in-game dialog
        Debug.Log("Travel button clicked! Opening dialog...");
        // Display dialog logic goes here
    }
}