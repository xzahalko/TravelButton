# TravelButton — Outward: Definitive Edition Mod

This repository contains a BepInEx plugin for Outward: Definitive Edition that adds a Travel Button feature.
It allows players to teleport between cities they have visited or that are enabled in the configuration.

## Features

- **Travel Button**: Appears in the inventory UI when opened (small, unobtrusive button)
- **City Teleportation**: Travel to cities that you've visited or that are enabled in config
- **Per-City Configuration**: Each city can be individually enabled/disabled and have custom travel costs
- **Dynamic Pricing**: Configure a global default travel cost and override per-city
- **Visited Tracking**: Automatically tracks cities you visit and persists across game sessions
- **Resource Management**: Deducts silver from player inventory; shows "not enough resources to travel" message when insufficient funds
- **Auto-Discovery**: Automatically marks cities as visited when you get near them

## Prerequisites

- This project is a compile-time skeleton only. To compile, you must provide the game's Unity assemblies and BepInEx/Harmony DLLs in a `lib/` folder as described below.
- **DO NOT** ship Unity engine DLLs or game files in a public repository. Keep the `lib/` folder local to your machine (gitignore excludes it).

## Installation

1. Create a `lib/` directory in the project root and copy these runtime/compile-time DLLs there from your game/BepInEx installation:
   - BepInEx.dll (from BepInEx/core/)
   - 0Harmony.dll
   - UnityEngine.CoreModule.dll
   - UnityEngine.IMGUIModule.dll
   - UnityEngine.TextRenderingModule.dll
   - UnityEngine.UIModule.dll
   - UnityEngine.UI.dll
   - UnityEngine.JSONSerializeModule.dll
   - UnityEngine.PhysicsModule.dll
   - Assembly-CSharp.dll (from the game's Managed folder)
   - UnityEngine.dll

2. Build the project:
   ```bash
   dotnet build
   ```

3. Copy the compiled DLL (`TravelButtonMod.dll` from `bin/Debug/net472/`) into the `BepInEx/plugins/TravelButton/` folder of your Outward installation.

4. Launch Outward; check BepInEx logs for plugin load messages.

## Configuration

The mod creates a configuration file at `BepInEx/config/com.xzahalko.travelbutton.cfg` with the following options:

### General Settings

- **EnableOverlay** (default: true): Show debug overlay
- **ToggleKey** (default: F10): Key to toggle debug overlay
- **EnableTeleport** (default: true): Enable actual teleportation and payment. Set to false for UI-only mode.
- **TravelCost** (default: 200): Global default cost in silver coins for travel

### Per-City Settings

Each city has two configuration options:

- **Cities.<CityName>.Enabled** (default: false): Whether the city is available for travel before visiting
- **Cities.<CityName>.Price** (default: TravelCost): Travel cost override for this specific city

Example configuration entries:
```
[Cities]
Cierzo.Enabled = false
Cierzo.Price = 200
Levant.Enabled = true
Levant.Price = 150
```

## Testing Instructions

### Basic Functionality Test

1. **Install the mod** and launch Outward
2. **Open your inventory** (I key or game-specific inventory button)
3. **Verify the Travel button appears** in the inventory UI (small button near top-right)
4. **Click the Travel button** to open the travel dialog
5. **Verify the dialog shows**:
   - Title: "Select destination"
   - List of cities (only those enabled in config or previously visited)
   - Each city showing its name and price (e.g., "150 silver")
   - Close button at the bottom

### Configuration Test

1. **Edit the config file** at `BepInEx/config/com.xzahalko.travelbutton.cfg`
2. **Enable a city**: Set `Cierzo.Enabled = true`
3. **Set a custom price**: Set `Cierzo.Price = 100`
4. **Restart the game** and open the travel dialog
5. **Verify Cierzo appears** in the list with price "100 silver"

### Visited City Test

1. **Travel to a city** in-game (e.g., walk to Cierzo)
2. **Open the travel dialog** from inventory
3. **Verify the visited city now appears** in the list (even if disabled in config)

### Insufficient Funds Test

1. **Ensure your character has less silver** than required for a destination
2. **Open the travel dialog** and click on that destination
3. **Verify the error message appears**: "not enough resources to travel"
4. **Verify the dialog remains open** and no teleport occurs

### Successful Travel Test

1. **Ensure your character has enough silver** for a destination
2. **Open the travel dialog** and click on a destination
3. **Verify**:
   - Silver is deducted from your inventory
   - Player is teleported to the destination
   - Dialog closes automatically

### Auto-Discovery Test

1. **Walk near a city** location (within discovery radius)
2. **Press F10** to force-mark nearest city (debug feature)
3. **Open the travel dialog**
4. **Verify the city now appears** in the available destinations

### Button Visibility Test

1. **Close the inventory**
2. **Verify the Travel button is hidden**
3. **Open the inventory**
4. **Verify the Travel button appears again**

## Files Included

- `src/TravelButtonMod.cs` — Main plugin entry point, configuration, and city data management
- `src/TravelButtonUI.cs` — UI creation and interaction handling
- `src/TravelButton.cs` — Legacy file (kept for compatibility)
- `src/TravelButtonVisitedManager.cs` — Persistence of visited city data
- `src/CityDiscovery.cs` — Auto-discovery of cities when player approaches them
- `TravelButton_Cities.json` — Default city data with coordinates
- `TravelButton.csproj` — Project file

## Notes

- The plugin uses BepInEx config entries for all settings
- City data and coordinates are loaded from `TravelButton_Cities.json`
- Visited cities are persisted in `TravelButton_Visited.json`
- By default, all cities are disabled (enabled: false) and require either visiting or manual config enable
- The mod uses reflection to detect player currency and deduct silver, which should work across game updates
- Use Harmony patches carefully if extending functionality

## Troubleshooting

- **Button doesn't appear**: Check that inventory is actually open, try different inventory-related gameobjects
- **Teleport doesn't work**: Check BepInEx logs for errors about player transform or coordinates
- **Config not loading**: Verify the config file exists at `BepInEx/config/com.xzahalko.travelbutton.cfg`
- **Cities not showing**: Check if they are enabled in config or have been visited

## License

See LICENSE file for details.