# TravelButton — Outward: Definitive Edition Sample Mod

<!-- Optimizations branch: Consolidated TravelButton_Cities.json handling -->

This repository contains a minimal example BepInEx plugin for Outward: Definitive Edition.
It demonstrates a safe, non-invasive plugin structure: configuration entries, a simple toggleable on-screen overlay,
and a place to add Harmony patches later.

Warning / prerequisites
- This project is a compile-time skeleton only. To compile, you must provide the game's Unity assemblies and BepInEx/Harmony DLLs in a `libs/` folder as described below.
- DO NOT ship Unity engine DLLs or game files in a public repository. Keep the `libs/` folder local to your machine (gitignore excludes it).

Quick instructions
1. Create a `libs/` directory in the project root and copy these runtime/compile-time DLLs there from your game/BepInEx installation:
   - BepInEx.dll (from BepInEx/core/)
   - 0Harmony.dll
   - UnityEngine.CoreModule.dll (or UnityEngine.dll depending on game)
2. Open the .csproj in Visual Studio (target .NET Framework 4.7.2) and build.
3. Copy the compiled DLL (`OutwardDefMod.dll`) into the BepInEx/plugins/ folder of your Outward installation.
4. Launch Outward; check BepInEx logs for plugin load messages.

Files included
- `src/OutwardDefMod.cs` — minimal plugin code (overlay + config)
- `OutwardDefMod.csproj` — project file; expects libs/ with required DLLs
- `README.md`, `LICENSE`, `.gitignore`

Notes
- The plugin uses BepInEx config entries and a simple OnGUI overlay.
- Use Harmony patches carefully — commented example provided in the code.

## Tests and Recordings

Manual test runs, recordings, and logs demonstrating behavior for the `cfgUseTransitionScene` config key are available in `dist/TESTS.md`.

Please add the recorded MP4s and log files under `dist/recordings/` and `dist/logs/` respectively.