# TravelButton — Outward: Definitive Edition Mod

A BepInEx plugin for Outward: Definitive Edition that adds a fast travel system with configurable cities, prices, and visited tracking.

## Features

- **Fast Travel System**: Teleport between major cities in Outward
- **Inventory-Based UI**: Travel button appears when inventory is open (top-right area)
- **Per-City Configuration**: Enable/disable individual cities, set custom prices
- **Visited Tracking**: Cities unlock automatically when visited, persist across saves
- **Currency System**: Configurable currency item (default: Silver) with insufficient funds checks
- **Auto-Discovery**: Automatically captures city coordinates when visiting new locations
- **Admin Override**: Enable cities in config to make them accessible without visiting

## Installation

### Prerequisites
- Outward: Definitive Edition
- BepInEx 5.x installed in your Outward directory

### Install Steps
1. Download the latest release DLL
2. Copy `TravelButtonMod.dll` to `BepInEx/plugins/TravelButton/`
3. Launch Outward - BepInEx will load the mod automatically
4. Check BepInEx console/logs to verify "TravelButtonMod Awake" message

## Configuration

Configuration file is auto-generated at: `BepInEx/config/com.xzahalko.travelbutton.cfg`

### General Settings
```ini
[General]
EnableTeleport = true        # Enable actual teleportation (false = UI-only mode)
TravelCost = 200            # Default cost for teleporting
CurrencyItem = Silver       # Currency item name to check
```

### Per-City Settings
```ini
[Cities]
Cierzo.Enabled = false      # Enable city without visiting (admin override)
Levant.Enabled = false
Monsoon.Enabled = false
Berg.Enabled = false
Harmattan.Enabled = false
Sirocco.Enabled = false
```

**Note:** Cities default to disabled. They become available when:
- Player visits the city (auto-discovered), OR
- Admin enables the city in config

## Usage

### In-Game
1. Open your inventory (press I or Tab)
2. Look for the "Travel" button at the top-right of inventory
3. Click the button to open the travel dialog
4. Select a destination (only available cities shown)
5. Confirm - costs will be deducted and you'll be teleported

### City Requirements
- City must be visited OR enabled in config
- Must have enough currency (default: 200 Silver)
- City must have valid coordinates (auto-captured when visited)

### Messages
- **"not enough resources to travel"**: Insufficient currency in inventory
- **"[No coords]"**: City is enabled but lacks coordinates (cannot teleport)

### Debug Hotkeys
- **F9**: Log current player position
- **F10**: Force-mark nearest city as visited
- **BackQuote (`)**: Open travel dialog directly

## City Data

Cities are stored in: `BepInEx/plugins/TravelButton/TravelButton_Cities.json`

### Supported Cities
- **Cierzo**: Starting town in Chersonese region
- **Levant**: Major city in Abrassar desert
- **Monsoon**: Coastal city in Hallowed Marsh
- **Berg**: Mountain city in Enmerkar Forest
- **Harmattan**: City in Antique Plateau
- **Sirocco**: Desert outpost

### Custom Coordinates
Edit `TravelButton_Cities.json` to add known coordinates:
```json
{
  "name": "Cierzo",
  "coords": [100.0, 1.5, -20.0],
  "price": null
}
```

Set `"price": 500` for per-city price overrides (null = use global default).

## Visited Tracking

Visited cities are persisted in: `BepInEx/plugins/TravelButton/TravelButton_Visited.json`

This file stores:
- List of visited city names
- Auto-captured coordinates for each city
- Persists across game sessions

## Building from Source

### Prerequisites for Compilation
You must provide game/BepInEx DLLs in a local `lib/` folder:
- BepInEx.dll
- 0Harmony.dll
- UnityEngine.CoreModule.dll
- UnityEngine.UIModule.dll
- Assembly-CSharp.dll
- Other Unity modules as listed in .csproj

**WARNING**: Do not commit game DLLs to the repository.

### Build Steps
1. Copy required DLLs to `lib/` folder
2. Run `dotnet build` or open in Visual Studio
3. Output: `bin/Debug/net472/TravelButtonMod.dll`

## Testing

See [TESTING.md](TESTING.md) for comprehensive manual testing procedures.

## Files Included
- `src/TravelButtonMod.cs` — Main plugin and configuration
- `src/TravelButtonUI.cs` — UI creation and dialog management
- `src/TravelButton.cs` — Legacy file (kept for compatibility)
- `src/CityDiscovery.cs` — Auto-discovery system
- `src/TravelButtonVisitedManager.cs` — Visited tracking persistence
- `TravelButton_Cities.json` — City data template
- `TESTING.md` — Manual testing guide

## Technical Notes

### Coordinate Discovery
- Exact world coordinates for Outward cities are not publicly documented
- Mod uses auto-discovery: captures player position when entering city areas
- Coordinates auto-save to visited tracking file for reuse
- Scene name matching: if scene contains city name, uses player position

### UI Integration
- Travel button is parented to inventory UI when found
- Button visibility syncs with inventory window state
- Dialog uses dedicated top-level canvas to avoid occlusion
- Refresh coroutine updates button states based on currency changes

### Currency Detection
- Uses reflection to find currency fields/properties in game objects
- Searches for common names: Silver, Money, Gold, Coins, Currency
- Falls back to manual checking if auto-detection fails

## License

See [LICENSE](LICENSE) file for details.