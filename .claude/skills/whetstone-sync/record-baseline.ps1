# Records a project's skill copy as in step with the current template, the way the board's
# SkillDrift.Record does after a merge on the Skills page: the template text goes to
# data/projects/<key>/template-base/<rel>, and sync.json gets its LF-normalised sha256, the last
# commit that touched the template (only when that commit's text is the template's), the time,
# and how. Keys are sorted, and the file ends with a newline.
#
#   pwsh -NoProfile -File .claude/skills/whetstone-sync/record-baseline.ps1 -Project alpha -Phase qa-test,refine
#
# Run it from the repo root, only after the project's copy really carries the template's change.
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string[]]$Phase,
    [string]$How = 'merged'
)
$ErrorActionPreference = 'Stop'
# pwsh -File hands "a,b" over as one string, not an array.
$Phase = @($Phase | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$root = (git rev-parse --show-toplevel).Trim()
$projDir = Join-Path $root "data/projects/$Project"
if (-not (Test-Path $projDir)) { throw "No project folder: $projDir" }
$baseDir = Join-Path $projDir 'template-base'
$syncFile = Join-Path $baseDir 'sync.json'

$sync = [ordered]@{}
if (Test-Path $syncFile) {
    $parsed = Get-Content $syncFile -Raw | ConvertFrom-Json -AsHashtable
    foreach ($k in $parsed.Keys) { $sync[$k] = $parsed[$k] }
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
foreach ($p in $Phase) {
    $templateRel = "data/skill-templates/$p/skill.md"
    $templatePath = Join-Path $root $templateRel
    if (-not (Test-Path $templatePath)) { throw "No template: $templateRel" }
    $text = (Get-Content $templatePath -Raw) -replace "`r`n", "`n"

    $rel = "skills/$p/skill.md"
    $baseFile = Join-Path $baseDir $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $baseFile) | Out-Null
    [System.IO.File]::WriteAllText($baseFile, $text, [System.Text.UTF8Encoding]::new($false))

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hash = -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })

    $commit = (git -C $root log -1 --format=%H -- $templateRel).Trim()
    if ($commit) {
        $atCommit = (git -C $root show "${commit}:$templateRel" | Out-String) -replace "`r`n", "`n"
        # git show through Out-String adds a trailing newline of its own; compare loosely on that.
        if ($atCommit.TrimEnd("`n") -ne $text.TrimEnd("`n")) { $commit = $null }
    } else { $commit = $null }

    $sync[$rel] = [ordered]@{
        hash     = $hash
        commit   = $commit
        syncedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        how      = $How
    }
    Write-Host "$Project ${rel}: $($hash.Substring(0, 12)) @ $(if ($commit) { $commit.Substring(0, 7) } else { 'uncommitted template' })"
}

$sorted = [ordered]@{}
foreach ($k in ($sync.Keys | Sort-Object -CaseSensitive)) { $sorted[$k] = $sync[$k] }
$json = ($sorted | ConvertTo-Json -Depth 5) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($syncFile, $json + "`n", [System.Text.UTF8Encoding]::new($false))
