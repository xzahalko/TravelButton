```markdown
# TravelButton

Mod for Outward (definitive edition) — adds an in‑game travel button with per‑city configuration (price, enabled, visited tracking).

This README is short and focused on installation requirements for the three plugin artifacts you provided and on how the mod behaves at runtime.

---

## Required files
Place these files together in the same plugin folder (see Installation):

- `TravelButton.dll` — required plugin assembly (mod code).
- `TravelButton.pdb` — optional debugging symbols (place next to the DLL to get richer stack traces in logs).
- `TravelButton_Cities.json` — canonical city data file (seeded city metadata, visited flags). The plugin reads and persists city data here by default when the JSON is next to the DLL.

> Important: the plugin detects its folder from the DLL location. Putting the JSON next to the DLL ensures predictable load/save behavior.

---

## Requirements
- BepInEx installed and working for your game (commonly BepInEx 5+ for modern Unity games).
- The game must be launched with BepInEx so plugins are loaded.
- (Optional) ConfigurationManager or similar in‑game config UI to edit settings in-game.

---

## Installation (manual)
1. Locate your game root (where the game executable is).
2. Ensure BepInEx is installed (you should have a `BepInEx` folder).
3. Create the plugin folder:
   - `<game root>\BepInEx\plugins\TravelButton\`
4. Copy the files:
   - `TravelButton.dll` → `<game root>\BepInEx\plugins\TravelButton\TravelButton.dll`
   - `TravelButton.pdb` (optional) → same folder
   - `TravelButton_Cities.json` → same folder
5. Start (or restart) the game.
6. Check BepInEx logs (e.g., `BepInEx/LogOutput.log`) for TravelButton startup messages.

---

## Behavior (detailed)

This section explains how the mod manages cities, configuration and visited state.

### City data
- `TravelButton_Cities.json` contains seeded city entries. Typical fields per city:
  - `name` (string) — unique city identifier shown in UI
  - `sceneName` (optional string)
  - `targetGameObjectName` (optional string)
  - `coords` (optional array [x,y,z])
  - `price` (optional int)
  - `visited` (optional bool seed)
- If JSON contains `price`, that value seeds the BepInEx default for that city's price. BepInEx values (ConfigEntries) are authoritative at runtime.

### Per‑city configuration
- For each city the plugin creates configuration bindings:
  - `Enabled` (bool) — whether the city can be traveled to (controls availability).
  - `Price` (int) — teleport price.
  - `Visited` (bool) — whether the city is considered visited.
- The plugin binds BepInEx ConfigEntries (Config.Bind) for these keys so ConfigurationManager (if present) can show them in‑game.
- SettingChanged handlers sync changes from ConfigEntries back into the runtime city model and persist them.

!!!WARNING!!!: do not modify `Visited` flag trough the config. Mod tracks it and marks it down when you visit a city.
When you override `Visited` value to true, game progress could be inconsistent after you teleport there.

### Visited state — how it works & where it is stored
- Purpose: "Visited" indicates the player has already been to that city. The UI uses it to enable/disable travel buttons or to show discovery state.
- Persistence:
  - Primary: `TravelButton_Cities.json` — the preferred canonical storage. The plugin merges runtime visited flags into this JSON when persisting.
  - Legacy: an auxiliary BepInEx-style cfg (e.g., `cz.valheimskal.travelbutton.cfg`) may be written with `[TravelButton.Cities]` keys such as `CityName.Visited = true` for compatibility.

### How visited is set at runtime
- The plugin sets `visited` in these situations:
  - When a scene loads that matches a city's `sceneName` or `targetGameObjectName` (auto-detect), the plugin marks that city visited.
  - After a successful teleport to a city, the plugin marks the city visited.
- After marking visited the plugin:
  - Persists the flag into `TravelButton_Cities.json`.
  - Optionally writes the legacy `.cfg` visited key if configured that way.
  - Refreshes the in‑game UI so buttons reflect the new state.

---

## Editing data
- To add or modify cities, edit `TravelButton_Cities.json` (backup before editing). /* not tested */
- After editing the JSON, restart the game to ensure changes are loaded

---

## Troubleshooting
- Plugin not loaded → ensure `TravelButton.dll` is in the correct plugin folder and BepInEx is installed. Check `BepInEx/LogOutput.log`.
- Changes not reflected → verify `TravelButton_Cities.json` is valid JSON and located next to the DLL.

---

## Uninstall
- Remove `TravelButton.dll` (and `TravelButton.pdb`) from:
  - `<game root>\BepInEx\plugins\TravelButton\`
- Optionally remove `TravelButton_Cities.json` if you want to remove persisted city data.

---
## Support
- For issues, include `BepInEx/LogOutput.log` and your `TravelButton_Cities.json` when filing a bug report.

---
## Contact
- Author: Deep

## Release notes:

### v1.1.1
* corrected teleportation to Cierzo from Cherson
* revision of target coordination computing

### v1.1.0
* completely reworked teleportation logic
* disabled teleportation to Sirocco, becouse teleport there could brake building progress in the city
* code revision

### v1.0.1
* corrected: newly created cfg file is created with enabled states of cities

### v1.0.0
* init
```