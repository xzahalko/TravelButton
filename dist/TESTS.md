# Transition Scene Tests — recordings & logs (dist)

Commit: `3dfbbbd`
Author: xzahalko
Date: 2025-11-20

This document records the manual test runs described by the author and links the associated recordings and logs. Place the actual files into the repository under the dist directory (suggested paths: `dist/recordings/` and `dist/logs/`) and update these links if you use different paths.

## Overview
Three recordings were produced to validate behavior controlled by the `cfgUseTransitionScene` configuration key.

- Test A — cfgUseTransitionScene = true
  - Teleport target: `ChersoneseNewTerrain`
  - File (suggested): `dist/recordings/cfgUseTransitionScene_true.mp4`
  - Logs (suggested): `dist/logs/cfgUseTransitionScene_true.log`
  - Notes: (brief summary of what happened during the recording)

- Test B — cfgUseTransitionScene = false
  - Teleport target: `ChersoneseNewTerrain`
  - File (suggested): `dist/recordings/cfgUseTransitionScene_false.mp4`
  - Logs (suggested): `dist/logs/cfgUseTransitionScene_false.log`
  - Notes: (brief summary)

- Test C — cfgUseTransitionScene = false (low memory / different scene)
  - Teleport target: `LowMemory_TransitionScene`
  - File (suggested): `dist/recordings/low_memory_cfgUseTransitionScene_false.mp4`
  - Logs (suggested): `dist/logs/low_memory_cfgUseTransitionScene_false.log`
  - Notes: (brief summary)

## Expected vs Observed
Fill in the expected and observed behavior for each test. Example format:

- Test A (cfgUseTransitionScene = true)
  - Expected: Use the configured transition scene and display transition when teleporting to `ChersoneseNewTerrain`.
  - Observed: (e.g. "transition scene displayed / not displayed; destination loaded; any errors in log")

- Test B (cfgUseTransitionScene = false)
  - Expected: Skip transition scene and load destination directly.
  - Observed: (fill in)

- Test C (low memory)
  - Expected: Behavior under low-memory simulation (describe expectation).
  - Observed: (fill in)

## Reproduction steps
1. Install plugin build for `3dfbbbd` into BepInEx (place plugin DLL into `BepInEx/plugins/`).
2. Ensure config file is in `BepInEx/config/` (or the plugin's expected config location).
3. Set `cfgUseTransitionScene` to `true` or `false` as required.
4. Launch the game and trigger the teleport to the specified target.
5. Record using a screen recorder and copy logs after the test:
   - BepInEx log: `BepInEx/LogOutput.log` (or `BepInEx.log` depending on setup)
   - Game log (if applicable): `Player.log` / `stdout` / engine-specific log

## Links (update these to the real file URLs)
- Recordings
  - Test A (cfgUseTransitionScene_true): ./dist/recordings/cfgUseTransitionScene_true.mp4
  - Test B (cfgUseTransitionScene_false): ./dist/recordings/cfgUseTransitionScene_false.mp4
  - Test C (low_memory_cfgUseTransitionScene_false): ./dist/recordings/low_memory_cfgUseTransitionScene_false.mp4

- Logs
  - Test A logs: ./dist/logs/cfgUseTransitionScene_true.log
  - Test B logs: ./dist/logs/cfgUseTransitionScene_false.log
  - Test C logs: ./dist/logs/low_memory_cfgUseTransitionScene_false.log

## Next steps
- Upload the recordings and logs to the repository (suggested paths above).
- Replace the placeholder paths with links to the committed files.
- Optionally open an issue linking this TESTS.md and the recordings if a bug is suspected.
