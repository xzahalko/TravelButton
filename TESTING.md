# TravelButton Mod - Testing Guide

This document provides manual testing steps for the TravelButton mod features.

## Prerequisites

1. Build the mod: `dotnet build`
2. Copy `bin/Debug/net472/TravelButtonMod.dll` to your BepInEx/plugins/TravelButton folder
3. Launch Outward: Definitive Edition
4. Verify BepInEx loads the plugin (check BepInEx console or logs)

## Test Cases

### 1. Configuration Testing

**Test per-city enabled/disabled settings:**
1. Navigate to `BepInEx/config/com.xzahalko.travelbutton.cfg`
2. Verify each city has an `[Cities]` section entry like `Cierzo.Enabled = false`
3. Change one city to `true` (e.g., `Cierzo.Enabled = true`)
4. Restart the game
5. Open inventory and click Travel button
6. **Expected:** Only the enabled city (and any visited cities) should appear in the dialog

**Test global travel cost:**
1. In config file, set `[General] TravelCost = 500`
2. Restart the game
3. Open Travel dialog
4. **Expected:** Dialog title shows "default cost: 500 Silver"

**Test currency item:**
1. In config file, set `[General] CurrencyItem = Gold`
2. Restart the game
3. Open Travel dialog
4. **Expected:** Dialog shows prices in "Gold" instead of "Silver"

### 2. UI Behavior Testing

**Test button visibility on main screen:**
1. Load into the game
2. Look at main HUD/screen
3. **Expected:** Travel button should NOT be visible on main screen

**Test button visibility with inventory:**
1. Open player inventory (press I or Tab)
2. **Expected:** Travel button appears at top-right area of inventory
3. Close inventory
4. **Expected:** Travel button disappears

**Test dialog opening:**
1. Open inventory
2. Click Travel button
3. **Expected:** Dialog opens centered on screen with city list
4. **Expected:** Close button appears at bottom of dialog
5. Click Close button
6. **Expected:** Dialog closes

### 3. Visited Tracking Testing

**Test visited city unlocking:**
1. Start new game or with unvisited cities
2. Navigate to a city location (e.g., Cierzo)
3. Walk around the city area
4. Press F10 to force-mark nearest city as visited (debug hotkey)
5. Open Travel dialog
6. **Expected:** The visited city now appears in the list and is clickable

**Test visited persistence:**
1. Visit a city and mark it as visited
2. Save game and exit
3. Restart game and load save
4. Open Travel dialog
5. **Expected:** Previously visited cities remain unlocked

**Test config override (enabled cities):**
1. In config, enable a city you haven't visited: `Berg.Enabled = true`
2. Restart game
3. Open Travel dialog
4. **Expected:** Berg appears and is clickable even though not visited

### 4. Teleportation Testing

**Test successful teleport:**
1. Visit Cierzo (or enable it in config)
2. Ensure city has coordinates set
3. Ensure you have enough silver (check with F9 debug hotkey for position)
4. Open Travel dialog and click Cierzo
5. **Expected:** Player teleports to Cierzo location
6. **Expected:** Silver is deducted from inventory

**Test insufficient funds:**
1. Set TravelCost to a very high value (e.g., 99999)
2. Open Travel dialog
3. Click on a city
4. **Expected:** Message displays: "not enough resources to travel"
5. **Expected:** Player is NOT teleported
6. **Expected:** No silver is deducted

**Test missing coordinates:**
1. Open `TravelButton_Cities.json`
2. Ensure a city has `"coords": null`
3. Enable that city in config
4. Open Travel dialog
5. **Expected:** City shows "[No coords]" suffix
6. **Expected:** City button is disabled/grayed out
7. **Expected:** Log shows warning about missing coordinates

### 5. Discovery System Testing

**Test auto-discovery:**
1. Navigate to a city scene/area
2. Walk around for a few seconds
3. Check BepInEx logs for "Auto-discovered city" message
4. Open Travel dialog
5. **Expected:** City is now marked as visited and appears in dialog

**Test coordinate capture:**
1. Visit a new city
2. Check `BepInEx/plugins/TravelButton/TravelButton_Visited.json`
3. **Expected:** File contains visited city with captured coordinates

### 6. Debug Hotkeys Testing

**F9 - Log player position:**
1. Press F9 while in-game
2. Check BepInEx console/logs
3. **Expected:** Current player position is logged

**F10 - Force mark nearest city:**
1. Navigate near a city
2. Press F10
3. Check logs
4. **Expected:** Nearest city is marked as visited

**BackQuote (`) - Open travel dialog:**
1. Press ` key while in-game
2. **Expected:** Travel dialog opens (alternative to clicking button)

## Configuration File Reference

Example `com.xzahalko.travelbutton.cfg`:

```ini
[General]
EnableOverlay = true
ToggleKey = F10
OverlayText = TravelButtonMod active
EnableTeleport = true
TravelCost = 200
CurrencyItem = Silver

[Cities]
Cierzo.Enabled = false
Levant.Enabled = false
Monsoon.Enabled = false
Berg.Enabled = false
Harmattan.Enabled = false
Sirocco.Enabled = false
```

## City Data File Reference

Example `TravelButton_Cities.json` structure:

```json
{
  "name": "Cierzo",
  "desc": "A starting town in Chersonese region.",
  "coords": [100.0, 1.5, -20.0],
  "targetGameObjectName": "",
  "isCityEnabled": false,
  "visited": false,
  "price": null
}
```

- `coords`: [x, y, z] world coordinates, or `null` if unknown
- `isCityEnabled`: Initial enabled state (overridden by config)
- `price`: Per-city price override, or `null` to use global price

## Common Issues

**Travel button not appearing:**
- Ensure inventory UI is actually open
- Check logs for "TravelButtonUI not found" or "PollForInventoryParent" messages
- Verify mod DLL is loaded by BepInEx

**Cities not appearing in dialog:**
- Check that cities are either visited OR enabled in config
- Verify `IsCityEnabled` returns true for the city (check logs)

**"not enough resources to travel" always shows:**
- Check that player actually has silver in inventory
- Verify currency detection is working (check logs for "GetPlayerCurrencyAmountOrMinusOne")
- Try manually adding silver via game console/cheats

**Teleport does nothing:**
- Ensure `EnableTeleport = true` in config
- Verify city has valid coordinates (not null)
- Check logs for "AttemptTeleportToPosition" messages

## Notes

- All cities default to `enabled = false` and require visiting OR config-enabling to appear
- Exact in-game coordinates for Outward cities are not available from public sources
- Coordinates will be auto-populated when players visit cities via the discovery system
- Users can manually edit `TravelButton_Cities.json` to add known coordinates
