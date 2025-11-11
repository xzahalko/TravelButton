using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using UnityEngine;

// Plugin GUID should be unique.
[BepInPlugin("com.xzahalko.travelbutton", "TravelButtonMod", "0.1.0")]
public class TravelButtonMod : BaseUnityPlugin
{
    // explicit BepInEx ManualLogSource type (avoid ambiguity)
    public static BepInEx.Logging.ManualLogSource LogStatic;
    private Harmony harmony;

    // Config entries
    private ConfigEntry<bool> cfgEnableOverlay;
    private ConfigEntry<KeyCode> cfgToggleKey;
    private ConfigEntry<string> cfgOverlayText;

    // runtime
    private bool showOverlay = false;

    private void Awake()
    {
        // assign instance logger explicitly
        LogStatic = this.Logger;
        this.Logger.LogInfo("TravelButtonMod Awake");

        // Use the instance Config property explicitly to avoid ambiguity
        cfgEnableOverlay = this.Config.Bind("General", "EnableOverlay", true, "Show small debug overlay in-game");
        cfgToggleKey = this.Config.Bind("General", "ToggleKey", KeyCode.F10, "Key to toggle overlay visibility");
        cfgOverlayText = this.Config.Bind("General", "OverlayText", "TravelButtonMod active", "Text shown in overlay");

        try
        {
            harmony = new Harmony("com.xzahalko.travelbutton.harmony");
            // harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
        catch (Exception ex)
        {
            this.Logger.LogError("Failed to create Harmony instance: " + ex);
        }
    }

    private void OnEnable()
    {
        this.Logger.LogInfo("TravelButtonMod Enabled");
    }

    private void OnDisable()
    {
        this.Logger.LogInfo("TravelButtonMod Disabled");
        try
        {
            // unpatch only this Harmony instance
            harmony?.UnpatchSelf();
        }
        catch (Exception ex)
        {
            this.Logger.LogError("Error unpatching: " + ex);
        }
    }

    private void Update()
    {
        // Toggle overlay with configured key - use instance Config and Logger
        if (cfgEnableOverlay.Value && Input.GetKeyDown(cfgToggleKey.Value))
        {
            showOverlay = !showOverlay;
            this.Logger.LogInfo($"Overlay toggled: {showOverlay}");
        }
    }

    private void OnGUI()
    {
        if (!cfgEnableOverlay.Value || !showOverlay) return;

        // Make sure we refer to Unity types unambiguously via namespaces (Unity types are fine here)
        GUI.backgroundColor = Color.black;
        GUI.contentColor = Color.white;
        var style = new GUIStyle(GUI.skin.box);
        style.fontSize = 14;
        style.alignment = TextAnchor.UpperLeft;
        style.padding = new RectOffset(8, 8, 6, 6);
        style.normal.textColor = Color.white;

        GUILayout.BeginArea(new Rect(12, 12, 360, 120));
        GUILayout.BeginVertical("box");
        GUILayout.Label(cfgOverlayText.Value, style);
        GUILayout.Label($"Time: {DateTime.Now:T}", style);
        GUILayout.Label($"Plugin: {this.Info.Metadata.Name} v{this.Info.Metadata.Version}", style);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    // Helper: write file to plugin folder
    private void WriteDebugFile(string name, string content)
    {
        try
        {
            var baseDir = Paths.PluginPath; // BepInEx helper
            var path = Path.Combine(baseDir, "TravelButtonMod");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, name), content);
        }
        catch (Exception ex)
        {
            this.Logger.LogError("WriteDebugFile failed: " + ex);
        }
    }
}