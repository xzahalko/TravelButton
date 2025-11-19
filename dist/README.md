# TravelButton

TravelButton is a BepInEx plugin for Outward (Definitive Edition) that adds a quick-travel button to the in-game toolbar. It supports per-destination pricing (silver), runtime config updates, and safe scene loading with an overlay to avoid visual glitches.

## Join me on discord to report bugs and give me feedback:

Link: <a href="https://discord.gg/rKGzYaH4">https://discord.gg/rKGzYaH4</a>

## Key features
- Toolbar button that opens a travel dialog with configured destinations.
- Per-city enable/disable and price settings.
- Robust currency deduction using reflection across inventory-like components.
- Auto-reload of external config changes and UI refresh without restarting the game.
- Safe scene loading + teleportation with fade overlay and position probing.

## Requirements
- Outward (Definitive Edition)
- BepInEx 5.x
- (Optional) ConfigurationManager for in-game config GUI

## Installation
1. Create (if needed) the plugin folder:
   `<GameRoot>/BepInEx/plugins/TravelButton/`
2. Copy the following files into that folder:
   - `TravelButtonMod.dll`
   - `TravelButtonMod.pdb`
   - `TravelButton_Cities.json`
   - `TravelButton_icon.png`
3. Start the game. Plugin initialization messages will appear in the BepInEx logs.

## Configuration
- BepInEx config entries created for:
  - `TravelButton.EnableMod`
  - `TravelButton.GlobalTravelPrice`
  - `TravelButton.CurrencyItem`
  - Per-city entries: `{CityName}.Enabled` and `{CityName}.Price`
- `TravelButton_Cities.json` seeds city metadata (coordinates, defaults). Edit before first run or use the generated file.

## Usage
- Open the toolbar/inventory UI and click the TravelButton.
- Click a destination to teleport. The plugin attempts to deduct the configured silver amount before teleporting. If payment fails, teleport is canceled and a notification/log entry is produced.

## Quick troubleshooting
- Prices not updating after editing cfg: ensure `Config.Reload()` appears in logs and FileSystemWatcher handled the change.
- Money not deducted: check plugin logs for `TryDeductPlayerCurrency` messages which show which component/method was used.
- If teleport proceeds but money remains unchanged, ensure the real deduction path (not simulation) runs before scene activation.

## Development notes
- Build target: .NET/Unity-compatible with Unity 2020 / .NET 4.x compatibility.
- Place compiled DLL into BepInEx plugins folder for testing.
- Logs are intentionally verbose for diagnosing inventory/currency and UI refresh behavior.

## License & Contact
- Author: Deep

## Release notes:

### v1.0.1
* corrected: newly created cfg file is create with enabled states of cities

### v1.0.0
* init
```