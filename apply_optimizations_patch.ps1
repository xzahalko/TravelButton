# apply_optimizations_no_git.ps1
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\apply_optimizations_no_git.ps1
# This script makes edits and creates backups (*.bak.optimizations). It does NOT call git.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Ensure running from repo root (script directory)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $scriptDir

Write-Host "Running no-git apply script from $scriptDir"

# 1) Insert TeleportInProgress into TeleportHelpers.cs if missing
$thFile = Join-Path 'src' 'TeleportHelpers.cs'
if (-not (Test-Path $thFile)) {
    Write-Error "Missing file: $thFile"
    exit 1
}

$thContent = Get-Content -Raw -Encoding UTF8 $thFile
if ($thContent -match 'TeleportInProgress') {
    Write-Host "TeleportInProgress already present in $thFile - skipping insertion."
} else {
    Write-Host "Adding TeleportInProgress to $thFile (creating backup)."
    Copy-Item -Path $thFile -Destination "$thFile.bak.optimizations" -Force

    $insertLines = @(
        '    // Indicates a global scene/teleport flow is in progress; used to block UI clicks and avoid re-entrancy.'
        '    // SceneLoaderInvoker.RunLoaderSafely will set this to true while a load/teleport runs and clear it in finally.'
        '    public static volatile bool TeleportInProgress = false;'
    )
    $insertBlock = "`r`n" + ($insertLines -join "`r`n") + "`r`n"

    if ($thContent -match 'TeleportGroundClearance') {
        $pattern = '(^\s*public\s+static\s+float\s+TeleportGroundClearance\s*=.*$)'
        $regex = [System.Text.RegularExpressions.Regex]::new($pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
        $newContent = $regex.Replace($thContent, { param($m) return $m.Value + $insertBlock })
    } else {
        # fallback: prepend block to top of file
        $newContent = $insertBlock + $thContent
    }
    Set-Content -Path $thFile -Value $newContent -Encoding UTF8
    Write-Host "Inserted TeleportInProgress into $thFile (backup at $thFile.bak.optimizations)."
}

# 2) Token replacements across src\*.cs, excluding DebugConfig.cs and TBLog.cs
Write-Host "Replacing TravelButtonPlugin.LogInfo -> TBLog.Info and TravelButtonPlugin.LogWarning -> TBLog.Warn in src\\*.cs (excluding DebugConfig.cs and TBLog.cs)."

$changed = @()

Get-ChildItem -Path 'src' -Filter '*.cs' -File | Where-Object { $_.Name -ne 'DebugConfig.cs' -and $_.Name -ne 'TBLog.cs' } | ForEach-Object {
    $path = $_.FullName
    $orig = Get-Content -Raw -Encoding UTF8 $path
    $new = $orig
    $new = [System.Text.RegularExpressions.Regex]::Replace($new, 'TravelButtonPlugin\.LogInfo\(', 'TBLog.Info(', [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $new = [System.Text.RegularExpressions.Regex]::Replace($new, 'TravelButtonPlugin\.LogWarning\(', 'TBLog.Warn(', [System.Text.RegularExpressions.RegexOptions]::Multiline)

    if ($new -ne $orig) {
        Copy-Item -Path $path -Destination "$path.bak.optimizations" -Force
        Set-Content -Path $path -Value $new -Encoding UTF8
        $changed += $path
        Write-Host "Updated: $path (backup: $path.bak.optimizations)"
    } else {
        Write-Host "No changes: $path"
    }
}

Write-Host ""
if ($changed.Count -gt 0) {
    Write-Host "Files changed:"
    $changed | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "Next steps (manual):"
    Write-Host "  git status --porcelain"
    Write-Host "  git --no-pager diff --staged      # after git add"
    Write-Host "  git add -A"
    Write-Host "  git commit -m `"optimizations: centralize debug logs (TBLog) and add TeleportInProgress flag`""
    Write-Host "  git push -u origin optimizations"
    Write-Host "  Then open a PR on GitHub (or run: gh pr create ... if you have gh)."
} else {
    Write-Host "No files changed by token replacement steps."
}

Write-Host "Backups left as *.bak.optimizations in src\\ for each modified file."
Write-Host "Done."