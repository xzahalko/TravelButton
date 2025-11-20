# Video Analysis Template (dist/results)

Use this template to add per-video timestamps, screenshots and correlate them with the log excerpts already collected.

Test A — cfgUseTransitionScene = true
- Video file: dist/videos/cfgUseTransitionScene_true.mp4
- Manual steps to fill:
  - Teleport initiated (video timestamp mm:ss): ____
  - Transition scene appears (mm:ss): ____
  - Final scene visible / control regained (mm:ss): ____
  - Observed behavior summary (1–2 lines): ____
- Correlated log excerpt(s) (file and line ranges):
  - dist/logs/LogOutput_cfgUseTransitionScene_true.log — lines ~560–635
- Screenshots:
  - dist/results/screenshots/cfgUseTransitionScene_true_transition.png
  - dist/results/screenshots/cfgUseTransitionScene_true_final.png

Test B — cfgUseTransitionScene = false
- Video file: dist/videos/cfgUseTransitionScene_false.mp4
- Manual steps to fill:
  - Teleport initiated (mm:ss): ____
  - Transition scene appears (mm:ss): ____
  - Final scene visible / control regained (mm:ss): ____
  - Observed behavior summary: ____
- Correlated log excerpt(s):
  - dist/logs/LogOutput_cfgUseTransitionScene_false.log — lines ~540–615
- Screenshots:
  - dist/results/screenshots/cfgUseTransitionScene_false_transition.png
  - dist/results/screenshots/cfgUseTransitionScene_false_final.png

Test C — low memory
- Video file: dist/videos/low_memory_cfgUseTransitionScene_false.mp4
- Manual steps to fill:
  - Teleport initiated (mm:ss): ____
  - Transition scene appears (mm:ss): ____
  - Final scene visible / control regained (mm:ss): ____
  - Observed behavior summary: ____
- Correlated log excerpt(s):
  - dist/logs/LogOutput_low_memoty_cfgUseTransitionScene_false.log — search for "LowMemory_TransitionScene" and the teleport sequence
- Screenshots:
  - dist/results/screenshots/low_memory_cfgUseTransitionScene_false_transition.png
  - dist/results/screenshots/low_memory_cfgUseTransitionScene_false_final.png

Notes on completing the template
- For each screenshot, include the video timestamp in the filename or in the README line.
- When correlating logs, quote the exact log line(s) (already summarized in TEST_RESULTS.md).
- If the videos show the same behavior as the logs (transition present when cfgUseTransitionScene=false), treat this as confirmed evidence.
```