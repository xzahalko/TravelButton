using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using UnityEngine;

// Plugin GUID should be unique. Replace with your identifier.
[BepInPlugin("com.deep.outward.defmod", "OutwardDefMod", "0.1.0")]
public class OutwardDefMod : BaseUnityPlugin
{
    public static ManualLogSource LogStatic;
    private Harmony harmony;

    // Config entries
    private ConfigEntry<bool> cfgEnableOverlay;
    private ConfigEntry<KeyCode> cfgToggleKey;
    private ConfigEntry<string> cfgOverlayText;

    // runtime
    private bool showOverlay = false;

    private void Awake()
    {
        LogStatic = Logger;

        Logger.LogInfo("OutwardDefMod Awake");

        cfgEnableOverlay = Config.Bind("General", "EnableOverlay", true, "Show small debug overlay in-game");
        cfgToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F10, "Key to toggle overlay visibility");
        cfgOverlayText = Config.Bind("General", "OverlayText", "OutwardDefMod active", "Text shown in overlay");

        try
        {
            harmony = new Harmony("com.deep.outward.defmod.harmony");
            // Example: add Harmony patches if/when you have a target method.
            // harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to create Harmony instance: " + ex);
        }
    }

    private void OnEnable()
    {
        Logger.LogInfo("OutwardDefMod Enabled");
    }

    private void OnDisable()
    {
        Logger.LogInfo("OutwardDefMod Disabled");
        try
        {
            harmony?.UnpatchSelf();
        }
        catch (Exception ex)
        {
            Logger.LogError("Error unpatching: " + ex);
        }
    }

    private void Update()
    {
        // Toggle overlay with configured key
        if (cfgEnableOverlay.Value && Input.GetKeyDown(cfgToggleKey.Value))
        {
            showOverlay = !showOverlay;
            Logger.LogInfo($"Overlay toggled: {showOverlay}");
        }
    }

    private void OnGUI()
    {
        if (!cfgEnableOverlay.Value || !showOverlay) return;

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
        GUILayout.Label($"Plugin: {Info.Metadata.Name} v{Info.Metadata.Version}", style);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    // Example helper: writes a file to plugin directory (safe place)
    private void WriteDebugFile(string name, string content)
    {
        try
        {
            var baseDir = Paths.PluginPath; // BepInEx helper
            var path = Path.Combine(baseDir, "OutwardDefMod");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, name), content);
        }
        catch (Exception ex)
        {
            Logger.LogError("WriteDebugFile failed: " + ex);
        }
    }

    // Example Harmony patch (commented). Replace TargetType and TargetMethod with actual types & methods.
    /*
    [HarmonyPatch(typeof(TargetType), "TargetMethodName")]
    public class ExamplePatch
    {
        public static void Prefix(TargetType __instance)
        {
            OutwardDefMod.LogStatic.LogInfo("Prefix called");
            // modify values or behavior here
        }
    }
    */
}