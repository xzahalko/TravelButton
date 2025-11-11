```markdown
# TravelButton

TravelButton is a minimal, cross-platform demo project that shows a small web UI with a "Travel" button. When clicked the button displays a styled waiting/progress dialog (spinner + message) while simulating a long-running operation, then shows a result.

This repository is intended as a tiny starter you can extend or embed into a larger web/app project.

Quick start
1. Clone or download this repository to your machine.
2. Open `index.html` in a modern browser (no build required).
3. Click the "Travel" button to see the waiting dialog and simulated result.

Files
- `index.html` — demo UI
- `styles.css` — styling, centered layout and styled waiting dialog
- `src/script.js` — button handler and waiting dialog logic
- `README.md` — this file
- `LICENSE` — MIT license
- `.gitignore` — recommended ignores for the repo

How to create the GitHub repo and push (quick)
1. Create the repo on GitHub (web) named `TravelButton` (public).
2. Or run (if you have the GitHub CLI installed) locally in the project folder:
   - git init
   - git add .
   - git commit -m "Initial commit"
   - gh repo create xzahalko/TravelButton --public --source=. --remote=origin --push
3. Or the plain git flow after creating the repo in GitHub web:
   - git remote add origin https://github.com/xzahalko/TravelButton.git
   - git branch -M main
   - git push -u origin main

License
This project is MIT-licensed. See `LICENSE`.
```