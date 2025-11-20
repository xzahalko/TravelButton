# PR: Add test analysis results

Branch name proposed: dist/add-analysis-results

PR title:
Add analysis of transition-scene tests to dist/results

PR body (use verbatim):
This PR adds the analysis artifacts for the transition-scene manual tests referenced in dist/TESTS.md.

Files added:
- dist/results/TEST_RESULTS.md — summary, findings, logs excerpts, conclusions and recommendations.
- dist/results/VIDEO_ANALYSIS_TEMPLATE.md — template for correlating video timestamps / screenshots with logs.
- dist/results/PR_DESCRIPTION.md — this file (PR helper).

Notes:
- The analysis is based on the log files committed to dist/logs/*. The MP4 files are present in dist/videos/ but were not re-encoded or modified. I could not open the MP4s here, so video-specific timestamps/screenshots are left as placeholders in the template — you can fill them in and add images under dist/results/screenshots/.
- Key finding: the logs show the LowMemory_TransitionScene being used as an intermediate scene in both cfgUseTransitionScene=true and cfgUseTransitionScene=false runs. Recommend adding a runtime debug log of cfgUseTransitionScene right before the teleport decision to confirm the runtime boolean and to guard the TwoStepTeleport call with that flag if needed.

Suggested git steps to create the branch and open the PR:

1) create branch, add files, commit and push:
   git checkout -b dist/add-analysis-results
   mkdir -p dist/results
   (create files with the contents above)
   git add dist/results/TEST_RESULTS.md dist/results/VIDEO_ANALYSIS_TEMPLATE.md dist/results/PR_DESCRIPTION.md
   git commit -m "Add transition-scene test analysis results under dist/results"
   git push -u origin dist/add-analysis-results

2) Create PR (use GitHub UI or CLI). Suggested title/body above.
