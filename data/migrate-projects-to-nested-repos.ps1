<#
.SYNOPSIS
    Move every project folder Whetstone still tracks out into its own nested git repo.

.DESCRIPTION
    A project folder under data/projects/ is one machine's business: its skills name that
    repo's stack, its project.json points at a path that exists on one computer, and its
    project.defaults.json describes infrastructure nobody else runs. Committing those into
    Whetstone meant every clone received every machine's projects, enabled by default (the
    .disabled marker is local), with no repo behind them.

    The replacement is the arrangement the notes store already uses (Initialize-NoteStoreGit
    in notes-store.ps1): Whetstone ignores data/projects/*/ entirely, and each project folder
    carries its own git repo, pushed to its own private remote. Versioned and backed up,
    never shared.

    RUN THIS ON THE MACHINE THAT OWNS THE PROJECTS, and only there. The operation that
    un-tracks a folder is `git rm --cached`, which leaves the files on this disk - but every
    OTHER clone that pulls the resulting commit has those files deleted from its working
    tree. That is correct for a machine that does not work on the project, and a disaster for
    the one that does. This script is how the owning machine makes the move without being
    that disaster: it copies everything out first, builds each nested repo WITH its history
    (git subtree split), confirms the nested repo holds every file Whetstone was tracking,
    and only then un-tracks.

    Nothing is pushed unless -Push is given.

.PARAMETER Remote
    Optional private remote per project, as key=url. Repeat for each, comma-separated:
        -Remote alpha=https://github.com/you/whetstone-alpha.git,bravo=https://github.com/you/whetstone-bravo.git
    Strings rather than a hashtable because `powershell -File` passes every argument as a
    string, so a hashtable literal can never arrive intact that way.
    A project with no remote still gets its nested repo and history; add the remote later
    with `git -C data/projects/<key> remote add origin <url>`.

.PARAMETER Push
    Push each nested repo that has a remote, then push Whetstone's un-tracking commit.
    Without it, everything is committed locally and you push when you have looked.

.PARAMETER DryRun
    Report what would happen and change nothing.

.EXAMPLE
    # Look first.
    powershell -ExecutionPolicy Bypass -File data\migrate-projects-to-nested-repos.ps1 -DryRun

.EXAMPLE
    # Do it, keep it local, inspect, push by hand.
    powershell -ExecutionPolicy Bypass -File data\migrate-projects-to-nested-repos.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Remote = @(),
    [switch]$Push,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# key=url pairs -> lookup. Split on the first '=' only; URLs can carry their own.
$Remotes = @{}
foreach ($pair in $Remote) {
    foreach ($item in ($pair -split ',')) {
        $item = $item.Trim()
        if (-not $item) { continue }
        $eq = $item.IndexOf('=')
        if ($eq -lt 1) { throw "-Remote entries look like key=url; got '$item'." }
        $Remotes[$item.Substring(0, $eq).Trim().ToLowerInvariant()] = $item.Substring($eq + 1).Trim()
    }
}

function Invoke-Git {
    # Native git with an exit-code check: Invoke-Git <dir> <git args...>
    #
    # Deliberately a plain function over $args with no param block. With named parameters,
    # PowerShell prefix-matches git's own flags against them - `branch -D x` bound "x" to a
    # -Dir parameter and ran git in a directory called x. $args passes every token through
    # untouched, which is the only safe way to forward someone else's command line.
    #
    # Also deliberately no 2>&1: in Windows PowerShell 5.1 that wraps every stderr line
    # (including subtree's progress output) in an ErrorRecord, which this script's Stop
    # preference would then treat as a failure of a command that succeeded.
    $dir = $args[0]
    $gitArgs = @($args | Select-Object -Skip 1)
    $out = & git -C $dir @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git -C '$dir' $($gitArgs -join ' ') failed (exit $LASTEXITCODE)" }
    return $out
}

function Step([string]$Text) { Write-Host ""; Write-Host "==> $Text" -ForegroundColor Cyan }
function Ok([string]$Text)   { Write-Host "    ok  $Text" -ForegroundColor Green }
function Note([string]$Text) { Write-Host "    $Text" }

# The runtime and secret exclusions Whetstone's own .gitignore applies to a project folder as
# data/**/... rules. Those rules are evaluated by the OUTER repo and never reach inside a
# nested one, so each nested repo has to restate them - or its first `git add -A` sweeps up
# the tracker token in project.json and a heartbeat's worth of logs.
$NestedGitignore = @'
# This folder is its own git repo, nested inside Whetstone's data/projects/ (which Whetstone
# gitignores). Whetstone's .gitignore rules are written as data/**/... and are evaluated by the
# outer repo, which never sees these files - so every runtime and secret exclusion it applies
# to a project folder is restated here.

# Machine-local config: repo path, tracker token, QA credentials. The path is worth backing up
# and the token is not, and they share a file - so the file stays out.
project.json
*.bak

# Runtime state, written by the heartbeat and the reflection pass.
events.jsonl
logs/
*.lock
*.stop
improvement-suggestions.md
reflection-keys.txt
notes-maintenance.stamp

# Local unregister marker - whether this project is enabled is a property of the machine.
.disabled

# Generated per-run MCP config.
mcp-config.json
mcp-config.*.json
'@

# ── Locate Whetstone ─────────────────────────────────────────────────────────
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repo 'Whetstone.csproj'))) {
    throw "Expected Whetstone.csproj in '$repo'. Run this script from its place in data/."
}
$projectsDir = Join-Path $repo 'data\projects'

Step "Preflight"

$branch = (Invoke-Git $repo rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "Whetstone is on '$branch'. Switch to main first." }
Ok "on main"

$dirty = Invoke-Git $repo status --porcelain
if ($dirty) {
    Note ($dirty -join "`n    ")
    throw "Whetstone's working tree is not clean. Commit or stash first - the un-tracking commit must contain nothing else."
}
Ok "working tree clean"

# A heartbeat mid-cycle holds claims against these folders' skill files. Moving the repo out
# from under it is safe for the files (they stay put) but not worth finding out mid-ticket.
if (Test-Path (Join-Path $repo 'data\heartbeat.lock')) {
    throw "data\heartbeat.lock exists - the heartbeat is running. Stop it (Settings, or create data\heartbeat.stop) and wait for workers to finish."
}
Ok "heartbeat not running"

# Look for the script itself rather than reading an exit code: `git subtree -h` exits 129 when
# subtree IS installed (usage output), and 1 when it is not, which is a trap to invert.
$execPath = (& git --exec-path).Trim()
if (-not (Test-Path (Join-Path $execPath 'git-subtree'))) {
    throw "git subtree is not installed ($execPath has no git-subtree). It ships with Git for Windows; update Git."
}
Ok "git subtree available"

$tracked = @(Invoke-Git $repo ls-files -- data/projects) | Where-Object { $_ }
$keys = @($tracked | ForEach-Object { ($_ -split '/')[2] } | Sort-Object -Unique)
if ($keys.Count -eq 0) {
    Ok "Whetstone tracks no project folders. Nothing to move."
    Step "Making sure every future project is ignored too"
}
else {
    Note "Whetstone currently tracks: $($keys -join ', ')"
}

foreach ($k in $Remotes.Keys) {
    if ($keys -notcontains $k) { Note "WARNING: a remote was given for '$k', which Whetstone does not track. Ignoring it." }
}

if ($DryRun) {
    Step "Dry run - nothing below will be done"
    foreach ($k in $keys) {
        $n = @($tracked | Where-Object { $_ -like "data/projects/$k/*" }).Count
        $remoteShown = if ($Remotes.ContainsKey($k)) { $Remotes[$k] } else { '(none - add later)' }
        Note "$k : $n tracked file(s) -> nested repo with history; remote $remoteShown"
    }
    Note "Whetstone .gitignore: ignore data/projects/*/ instead of individual projects"
    Note "Whetstone: git rm -r --cached for each folder above, one commit"
    Note "Push: $(if ($Push) { 'yes' } else { 'no - you push after inspecting' })"
    return
}

# ── Back up first ────────────────────────────────────────────────────────────
# A plain copy outside the repo, including the untracked files (project.json, .disabled) that
# git cannot restore. Everything below is designed not to need it; this is for when that
# design is wrong.
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupRoot = Join-Path $env:USERPROFILE "whetstone-project-backups\$stamp"
if ($keys.Count -gt 0) {
    Step "Backing up to $backupRoot"
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    foreach ($k in $keys) {
        Copy-Item -Recurse -Force (Join-Path $projectsDir $k) (Join-Path $backupRoot $k)
        Ok "$k copied"
    }
}

# ── One nested repo per project, carrying its history ────────────────────────
foreach ($k in $keys) {
    $dir = Join-Path $projectsDir $k
    Step "Project '$k'"

    if (Test-Path (Join-Path $dir '.git')) {
        Note "already has a nested repo - leaving its history alone, only un-tracking it from Whetstone"
    }
    else {
        # subtree split rewrites Whetstone's history down to the commits that touched this
        # folder, re-rooted at it. That is the project's skill history - months of "why does QA
        # do it this way" in commit messages - and a fresh `git init` would throw it away.
        $splitBranch = "split/$k-$stamp"
        Note "extracting history (git subtree split) - this reads every commit and can take a minute"
        Invoke-Git $repo subtree split -q "--prefix=data/projects/$k" -b $splitBranch | Out-Null

        Invoke-Git $dir init -q | Out-Null
        Invoke-Git $dir symbolic-ref HEAD refs/heads/main | Out-Null
        Invoke-Git $dir fetch -q $repo $splitBranch | Out-Null
        Invoke-Git $dir update-ref refs/heads/main FETCH_HEAD | Out-Null
        # Mixed reset: load the index from the history just fetched WITHOUT touching a single
        # file on disk. A --hard here would overwrite any local edit; this cannot.
        Invoke-Git $dir reset -q | Out-Null
        Invoke-Git $repo branch -D $splitBranch | Out-Null
        $commits = (Invoke-Git $dir rev-list --count HEAD).Trim()
        Ok "nested repo created with $commits commit(s) of history"

        $gi = Join-Path $dir '.gitignore'
        if (-not (Test-Path $gi)) {
            [IO.File]::WriteAllText($gi, $NestedGitignore.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
        }
        Invoke-Git $dir add -A | Out-Null
        $staged = Invoke-Git $dir diff --cached --name-only
        if ($staged) {
            Invoke-Git $dir -c user.useConfigOnly=false commit -q -m "Move out of Whetstone into this project's own repo`n`nHistory above this commit is Whetstone's, filtered to data/projects/$k by git subtree split. From here the folder is versioned on its own and backed up to its own private remote, so no other machine's clone of the dashboard ever receives it.`n`nAdded .gitignore restating the parent's runtime and secret exclusions, which do not reach into a nested repo." | Out-Null
            Ok "committed .gitignore (and anything Whetstone had not been tracking yet)"
        }
    }

    # The check that gates the un-tracking. Every file Whetstone tracks under this folder must
    # be tracked by the nested repo too, or un-tracking it from Whetstone is losing it.
    $inParent = @($tracked | Where-Object { $_ -like "data/projects/$k/*" } | ForEach-Object { $_.Substring("data/projects/$k/".Length) })
    $inNested = @(Invoke-Git $dir ls-files)
    $missing = @($inParent | Where-Object { $inNested -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "Nested repo for '$k' is missing $($missing.Count) file(s) Whetstone tracks: $($missing -join ', '). Stopping BEFORE un-tracking anything. Backup is at $backupRoot."
    }
    if (Invoke-Git $dir status --porcelain) {
        throw "Nested repo for '$k' has uncommitted changes after setup. Stopping before un-tracking. Backup is at $backupRoot."
    }
    Ok "nested repo holds all $($inParent.Count) file(s) Whetstone tracked"

    if ($Remotes.ContainsKey($k)) {
        $existing = & git -C $dir remote 2>$null
        if ($existing -notcontains 'origin') { Invoke-Git $dir remote add origin $Remotes[$k] | Out-Null }
        Ok "remote origin -> $($Remotes[$k])"
        if ($Push) {
            Invoke-Git $dir push -u origin main | Out-Null
            Ok "pushed"
        }
    }
    else {
        Note "no remote given - add one: git -C data/projects/$k remote add origin <private-repo-url>"
    }
}

# ── Whetstone: ignore every project folder, then stop tracking them ──────────
Step "Whetstone .gitignore"
$giPath = Join-Path $repo '.gitignore'
$gi = [IO.File]::ReadAllText($giPath)
$wildcard = 'data/projects/*/'
$finalBlock = @"
# ---- Per-project dashboard config (skills, prompts, defaults) ---- [begin project-folders]
# Every project folder is ignored. A project folder is one machine's business: its skills name
# that repo's stack and its project.json points at a path that exists on one computer. Each one
# carries its own nested git repo and private remote instead - the arrangement the notes store
# already uses (Initialize-NoteStoreGit in notes-store.ps1) - so it is versioned and backed up
# without any other machine's clone of Whetstone ever receiving it.
#
# data/skill-templates/ IS shared: it is what a new project is scaffolded from, and knows
# nothing about any one repo. Moved here by data/migrate-projects-to-nested-repos.ps1.
$wildcard
# ---- [end project-folders]
"@
$markerPattern = '(?s)# ---- Per-project dashboard config[^\r\n]*\[begin project-folders\].*?# ---- \[end project-folders\]\r?\n?'
if ([regex]::IsMatch($gi, $markerPattern)) {
    # Replace the whole marked block, prose included. The block written before the move says
    # which folders are still tracked and why; after the move every sentence of that is false,
    # and a stale explanation sitting directly above the rule it contradicts is worse than none.
    $before = $gi
    $gi = [regex]::Replace($gi, $markerPattern, ($finalBlock.Replace("`r`n", "`n") + "`n"))
    if ($gi -ne $before) {
        [IO.File]::WriteAllText($giPath, $gi.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
        Ok "replaced the project-folders block with a single $wildcard rule"
    }
    else {
        Ok "project-folders block is already the single $wildcard rule"
    }
}
elseif ($gi -notmatch [regex]::Escape($wildcard)) {
    # No markers - someone edited them away. Fall back to removing per-project lines and
    # appending the rule; the prose around them may need tidying by hand.
    $gi = [regex]::Replace($gi, '(?m)^data/projects/[^/*\r\n]+/\r?\n?', '')
    $gi = $gi.TrimEnd() + "`n`n" + $finalBlock.Replace("`r`n", "`n") + "`n"
    [IO.File]::WriteAllText($giPath, $gi.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    Ok "now ignores $wildcard (no block markers found - check the surrounding comments by hand)"
}
else {
    Ok "already ignores $wildcard"
}

Invoke-Git $repo add .gitignore | Out-Null
foreach ($k in $keys) {
    Invoke-Git $repo rm -r -q --cached "data/projects/$k" | Out-Null
    Ok "un-tracked data/projects/$k (files remain on this disk)"
}

$staged = Invoke-Git $repo diff --cached --name-only
if ($staged) {
    $moved = if ($keys.Count -gt 0) { $keys -join ', ' } else { 'no project folders' }
    Invoke-Git $repo commit -q -m "Move $moved out of Whetstone into their own repos`n`nEach project folder now carries its own nested git repo and private remote, and Whetstone ignores data/projects/*/ outright. Done with data/migrate-projects-to-nested-repos.ps1 on the machine that owns these projects, which extracted each folder's history with git subtree split and checked every tracked file had arrived before un-tracking anything.`n`nOn any OTHER machine, pulling this commit deletes these folders from the working tree. That is intended: a machine that does not work on a project should not have it. Any .disabled marker left behind is harmless - a folder without project.defaults.json or project.json is not discovered as a project." | Out-Null
    Ok "committed in Whetstone"
}

# ── Verify ───────────────────────────────────────────────────────────────────
Step "Verify"
$stillTracked = @(Invoke-Git $repo ls-files -- data/projects) | Where-Object { $_ }
if ($stillTracked.Count -gt 0) { throw "Whetstone still tracks: $($stillTracked -join ', ')" }
Ok "Whetstone tracks nothing under data/projects/"
foreach ($k in $keys) {
    $defaults = Join-Path $projectsDir "$k\project.defaults.json"
    if (-not (Test-Path $defaults)) { throw "$defaults is gone from disk. Restore from $backupRoot." }
    Ok "$k still on disk, nested repo at $((Invoke-Git (Join-Path $projectsDir $k) rev-parse --short HEAD).Trim())"
}

if ($Push) {
    Step "Pushing Whetstone"
    Invoke-Git $repo push origin main | Out-Null
    Ok "pushed"
}
else {
    Step "Not pushed"
    Note "Inspect, then:  git push origin main"
    Note "and for each nested repo with a remote:  git -C data\projects\<key> push -u origin main"
}
if ($keys.Count -gt 0) {
    Note ""
    Note "Backup of the pre-move folders: $backupRoot"
    Note "Delete it once you are satisfied - it includes project.json, which holds tracker tokens."
}
