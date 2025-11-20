# Transition Scene Test Results (dist/results)

Commit referenced: `3dfbbbd`  
Author: xzahalko  
Date: 2025-11-20

Summary
- Purpose: verify behavior controlled by cfgUseTransitionScene (whether teleport uses a transition scene before loading the target).
- Inputs in repository:
  - Videos: dist/videos/cfgUseTransitionScene_true.mp4, dist/videos/cfgUseTransitionScene_false.mp4, dist/videos/low_memory_cfgUseTransitionScene_false.mp4
  - Logs: dist/logs/LogOutput_cfgUseTransitionScene_true.log, dist/logs/LogOutput_cfgUseTransitionScene_false.log, dist/logs/LogOutput_low_memoty_cfgUseTransitionScene_false.log
- What I could analyze: full log files listed above and source code references for the relevant config key. I could not inspect video frames here; see "Video correlation" section for placeholders.

High-level finding (important)
- Observed behavior: In both the test runs where cfgUseTransitionScene = true and cfgUseTransitionScene = false, the plugin attempted a two-step teleport via LowMemory_TransitionScene before loading the final destination (ChersoneseNewTerrain). In other words, the transition scene was used even when cfgUseTransitionScene was false.
- This suggests either:
  - The cfgUseTransitionScene option is not being consulted (or read) at the decision point that triggers TwoStepTeleport, or
  - The runtime config value used at teleport time differs from the expectations in the tests.

Why this matters
- Expected behavior:
  - cfgUseTransitionScene = true → load LowMemory_TransitionScene as intermediate transition before final scene.
  - cfgUseTransitionScene = false → load final scene directly (no intermediate transition scene).
- Observed: intermediate transition used in both cases. This is likely the root cause of the test discrepancy and should be addressed by ensuring the config is consulted at the transition decision point or by fixing when/how cfgUseTransitionScene is populated at runtime.

Per-test notes (logs correlated)
- Test A — cfgUseTransitionScene = true
  - Video: dist/videos/cfgUseTransitionScene_true.mp4
  - Log: dist/logs/LogOutput_cfgUseTransitionScene_true.log
  - Key log excerpts:
    - Teleport attempt part of TryTeleportThenCharge: "TwoStepTeleport: attempting transition via LowMemory_TransitionScene first." (Log line ~566)
    - Teleport finished loading final scene: "TryTeleportThenCharge: scene load wait finished (elapsed=2.490s) finishedLoad=True loadSuccess=True" (Log line ~634)
  - Observed (from logs): Two-step path executed (LowMemory_TransitionScene → ChersoneseNewTerrain). Scene load succeeded.

- Test B — cfgUseTransitionScene = false
  - Video: dist/videos/cfgUseTransitionScene_false.mp4
  - Log: dist/logs/LogOutput_cfgUseTransitionScene_false.log
  - Key log excerpts:
    - Teleport attempt: "TwoStepTeleport: attempting transition via LowMemory_TransitionScene first." (Log line ~547)
    - Final scene ready/activated: "TryTeleportThenCharge: scene load wait finished (elapsed=2.443s) finishedLoad=True loadSuccess=True" (Log line ~614)
  - Observed (from logs): Two-step path executed despite cfgUseTransitionScene reported in test as false.

- Test C — cfgUseTransitionScene = false (low memory / different scene)
  - Video: dist/videos/low_memory_cfgUseTransitionScene_false.mp4
  - Log: dist/logs/LogOutput_low_memoty_cfgUseTransitionScene_false.log
  - Key log excerpts (entries in this log indicate transition scene appears in active scene traces)
    - Logs show the LowMemory_TransitionScene active around the teleport sequence (search within file for "LowMemory_TransitionScene" and "TwoStepTeleport").
  - Observed (from logs): behavior consistent with the other two tests — LowMemory_TransitionScene used as intermediate.

Detailed supporting evidence (selected lines)
- From dist/logs/LogOutput_cfgUseTransitionScene_true.log:
  - "TryTeleportThenCharge: calling tm.StartSceneLoad(scene='ChersoneseNewTerrain', correctedCoords=(136.9, 34.0, 1460.9))"  
  - "TwoStepTeleport: attempting transition via LowMemory_TransitionScene first." (line ~566)
  - "TeleportManager: scene 'LowMemory_TransitionScene' reached ready-to-activate (progress=0.90)." (line ~570)
  - "TeleportManager: starting async load for scene 'ChersoneseNewTerrain'." (line ~593)
  - "TryTeleportThenCharge: scene load wait finished (elapsed=2.490s) finishedLoad=True loadSuccess=True" (line ~633)

- From dist/logs/LogOutput_cfgUseTransitionScene_false.log:
  - "TryTeleportThenCharge: calling tm.StartSceneLoad(scene='ChersoneseNewTerrain', ...)" (line ~546)
  - "TwoStepTeleport: attempting transition via LowMemory_TransitionScene first." (line ~547)
  - "TeleportManager: scene 'LowMemory_TransitionScene' reached ready-to-activate (progress=0.90)." (line ~551)
  - "TeleportManager: starting async load for scene 'ChersoneseNewTerrain'." (line ~574)
  - "TryTeleportThenCharge: scene load wait finished (elapsed=2.443s) finishedLoad=True loadSuccess=True" (line ~614)

Interpretation
- The logs show the code path that performs a two-step transition (loading LowMemory_TransitionScene first) executes in both runs. Given the expected behavior, this indicates the configuration flag is not effectively gating the TwoStepTeleport/transition path; either:
  - The check for cfgUseTransitionScene is missing at the call site, or
  - cfgUseTransitionScene was not populated with the intended value at runtime when the teleport occurred (e.g., config binding/load timing), or
  - There is another condition that forces TwoStepTeleport regardless of cfgUseTransitionScene (for the scenes tested).

Recommended immediate next steps
1. Confirm runtime config value at teleport time:
   - Add a short debug log line immediately before TwoStepTeleport is invoked, logging the value of cfgUseTransitionScene (true/false).
   - This will confirm whether the decision logic is using the expected runtime value.
2. Inspect the call site that invokes TwoStepTeleport (TryTeleportThenCharge / TeleportManager usage) and ensure there is a condition like:
   - if (cfgUseTransitionScene.Value) { TwoStepTeleport(...); } else { direct load final scene; }
3. If the value is correct but TwoStepTeleport still runs, trace any other code that could call TwoStepTeleport unconditionally.
4. After a code fix, re-run the tests and capture video + logs to confirm expected behavior.

Video correlation & placeholders
- I could not open or step through the MP4s from here. To complete the analysis:
  - Open each video and capture:
    - timestamp of the teleport button click,
    - timestamp when transition scene appears (if present),
    - timestamp when final scene rendering/control is regained,
    - note any freezes, black screens, or visual artifacts.
  - Add 1–3 screenshot images showing the transition scene and the final scene start (store images under dist/results/screenshots/ and reference them).
  - Copy relevant log line ranges next to the video timestamps. I left placeholders in the "video analysis template" (see separate file).

Conclusions
- Logs indicate the two-step transition via LowMemory_TransitionScene is being performed even when cfgUseTransitionScene is false. This is the most likely explanation for the failing/unexpected test behavior and should be addressed by ensuring the config is consulted at the transition decision point or by fixing when/how cfgUseTransitionScene is populated at runtime.

Appendices
- Relevant files consulted:
  - src/TravelButton.cs (defines cfgUseTransitionScene and contains teleport-related helpers)
  - src/TeleportManager.cs (scene load orchestration)
  - All three log files in dist/logs/
  - Videos in dist/videos/ (not examined frame-by-frame here)
