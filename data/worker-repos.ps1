# ============================================================
# A ticket's workspace on a project whose code is several git repos side by side. See
# docs/multi-repo-plan.md, piece 2.
#
# A project without "repos" is a set of one, and every function here hands that case to the code
# that ran before any of this existed: Resolve-Workspace calls Resolve-Worktree exactly as
# worker.ps1 always has, and the workspace IS that worktree. Only a project of several takes the
# rest.
#
# For one of several, the workspace is a plain folder named after the ticket's branch, holding one
# worktree per repo under that repo's own folder name - the same layout as projectPath, so a path
# means the same thing to the agent as to the human. Every writable repo gets the branch; a
# readOnly one gets a worktree detached at its base and no branch at all.
#
# "Touched" is the word for a repo that is in the ticket: its branch has commits ahead of its base
# ref, or its worktree has uncommitted changes (git status --porcelain, untracked included, as
# Save-WorkerLeftovers counts them). A readOnly repo is never touched. The same rule is written in
# Services/TicketGitService.cs for the board's side, and the two must agree.
#
# Dot-sourced, functions only. Leans on worker.ps1 (Resolve-Worktree, Resolve-BaseRef,
# Update-MergedBranch, Initialize-Worktree and what it calls, Test-BranchRef, Get-WorktreeDirName,
# Get-TicketBranchName, Get-SetupFailedMarker) and on heartbeat-common.ps1 (Write-Log,
# Invoke-WithMutex).
#
# Tests: powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-repos.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

# The repos a claim's project is made of, in config order, as hashtables:
#   @{ dir; hub; baseBranch; setup; copy; readOnly }
# dir is '' and hub is projectPath for the set of one, which takes the project-level base, setup
# and copy keys - exactly what Resolve-Worktree was always given. A member's base falls back to the
# project's, then to $DefaultBase (the global one).
function Get-RepoMembers {
    param($Project, [string]$DefaultBase)

    $projectBase = "$($Project.baseBranch)".Trim()
    if (-not $projectBase) { $projectBase = $DefaultBase }
    $members = New-Object System.Collections.Generic.List[object]

    $repos = @($Project.repos | Where-Object { $_ })
    if ($repos.Count -eq 0) {
        [void]$members.Add(@{
            dir = ''; hub = "$($Project.projectPath)"; baseBranch = $projectBase
            setup = @($Project.worktreeSetup | Where-Object { $_ }); copy = @($Project.worktreeCopyFiles | Where-Object { $_ })
            readOnly = $false
        })
        return $members.ToArray()
    }

    foreach ($r in $repos) {
        $dir = "$($r.dir)".Trim()
        $hub = "$($r.path)".Trim()
        if (-not $hub) { $hub = Join-Path "$($Project.projectPath)" $dir }
        $base = "$($r.baseBranch)".Trim()
        if (-not $base) { $base = $projectBase }
        [void]$members.Add(@{
            dir = $dir; hub = $hub; baseBranch = $base
            setup = @($r.worktreeSetup | Where-Object { $_ }); copy = @($r.worktreeCopyFiles | Where-Object { $_ })
            readOnly = ($r.readOnly -eq $true)
        })
    }
    return $members.ToArray()
}

# True when these members are a project of several repos rather than the set of one.
function Test-MultiRepo {
    param([object[]]$Members)
    $all = @($Members)
    return ($all.Count -gt 1) -or ($all.Count -eq 1 -and "$($all[0].dir)")
}

function Get-FullPathKey {
    param([string]$Path)
    if (-not $Path) { return '' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/').ToLowerInvariant()
}

# Is $Path one of $RepoDir's registered worktrees?
function Test-RegisteredWorktree {
    param([string]$RepoDir, [string]$Path)
    $want = Get-FullPathKey $Path
    foreach ($line in @(git -C $RepoDir worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *' -and (Get-FullPathKey $line.Substring(9).Trim()) -eq $want) { return $true }
    }
    return $false
}

# A readOnly repo's worktree: detached at its base ref, no branch, brought to the current base at the
# start of every phase while it is clean. Detached is what makes it read-only in fact as well as in
# the prompt - there is no branch of the ticket's here to commit onto, so nothing here can ever be
# delivered. A worktree with changes in it is a fault: some run edited a repo it was only given to
# read, and that is reported rather than tidied away, the way a review run that edited code is.
# Returns @{ ok; path; sha; created; needsSetup } or @{ ok = $false; reason; fault }.
function Resolve-ReadOnlyWorktree {
    param([string]$RepoDir, [string]$Path, [string]$BaseBranch)

    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path (Join-Path $RepoDir '.git'))) {
        return @{ ok = $false; reason = "'$RepoDir' is not a git repo" }
    }

    $res = Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        git -C $RepoDir worktree prune *> $null

        $baseRef = Resolve-BaseRef -RepoDir $RepoDir -BaseBranch $BaseBranch -MergeLocally $false
        if (-not $baseRef) { return @{ ok = $false; reason = "base branch '$BaseBranch' not found" } }
        $sha = "$(git -C $RepoDir rev-parse --verify --quiet "$baseRef^{commit}" 2>$null)".Trim()
        if (-not $sha) { return @{ ok = $false; reason = "could not read '$baseRef'" } }

        if (Test-Path $Path) {
            if (-not (Test-RegisteredWorktree -RepoDir $RepoDir -Path $Path)) {
                return @{ ok = $false; reason = "'$Path' already exists but git does not know it as a worktree; move or remove it by hand" }
            }
            $dirty = @(git -C $Path status --porcelain 2>$null | Where-Object { $_ })
            if ($dirty.Count -gt 0) {
                return @{ ok = $false; fault = $true
                    reason = "the read-only repo at '$Path' has $($dirty.Count) changed file(s) - a run changed a repo it was only given to read. Nothing in it was committed or delivered. Inspect it and clean it by hand" }
            }
            $head = "$(git -C $Path rev-parse --verify --quiet HEAD 2>$null)".Trim()
            if ($head -eq $sha) { return @{ ok = $true; path = $Path; sha = $sha; created = $false; moved = $false } }
            git -C $Path checkout --quiet --detach $sha *> $null
            if ($LASTEXITCODE -ne 0) { return @{ ok = $false; reason = "could not move the read-only worktree at '$Path' to $baseRef" } }
            return @{ ok = $true; path = $Path; sha = $sha; created = $false; moved = $true }
        }

        $parent = Split-Path $Path -Parent
        if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        git -C $RepoDir worktree add --detach $Path $sha *> $null
        if ($LASTEXITCODE -ne 0) { return @{ ok = $false; reason = "could not create a detached worktree at '$Path'" } }
        return @{ ok = $true; path = $Path; sha = $sha; created = $true; moved = $false }
    }

    if (-not $res.ok) { return $res }
    # Moving the base can move a lockfile, and a setup that failed last time is owed another go.
    $marker = Get-SetupFailedMarker -Path $res.path
    $res.needsSetup = $res.created -or $res.moved -or ($marker -and (Test-Path $marker))
    if ($res.moved) { Write-Log "  Read-only repo at $($res.path) moved to its base ($($res.sha.Substring(0, 8)))" }
    return $res
}

# Run each item's Initialize-Worktree, all at once. Items are @{ dir; path; hub; copy; setup }.
#
# Each repo's setup is a background job of its own: a separate process, so each has its own PATH to
# append Git's usr\bin to (Initialize-Worktree changes and restores it, which two runspaces sharing
# one process would race on). Every job is waited for, even after one fails, so no install is left
# running behind a ticket that is about to be blocked. Log lines arrive as they are written, each
# with its repo's name. One item runs inline, as a single repo always has.
# Returns @{ ok; failed = @( @{ dir; reason } ) }.
function Invoke-WorkspaceSetup {
    param([object[]]$Items, [double]$TimeoutMinutes)

    $failed = New-Object System.Collections.Generic.List[object]
    $items = @($Items)
    if ($items.Count -eq 0) { return @{ ok = $true; failed = @() } }

    if ($items.Count -eq 1) {
        $i = $items[0]
        $why = Initialize-Worktree -Path $i.path -RepoDir $i.hub -CopyFiles @($i.copy) -Setup @($i.setup) -TimeoutMinutes $TimeoutMinutes
        if ($why) { [void]$failed.Add(@{ dir = $i.dir; reason = $why }) }
        return @{ ok = ($failed.Count -eq 0); failed = $failed.ToArray() }
    }

    # What a job needs from worker.ps1, sent as text: a job is another process and has none of it.
    $defs = @('Initialize-Worktree', 'Invoke-SetupCommand', 'Format-OutputTail', 'Get-SetupFailedMarker' | ForEach-Object {
        "function $_ {`n$((Get-Command $_ -CommandType Function).ScriptBlock)`n}"
    })
    $block = {
        param([string[]]$Defs, [string]$Path, [string]$RepoDir, [string[]]$CopyFiles, [string[]]$Setup, [double]$TimeoutMinutes)
        $ErrorActionPreference = 'Continue'
        # The information stream, not output: Invoke-SetupCommand's result is assigned, and anything
        # written to output inside it would land in that assignment. Left at its default preference,
        # which still records every line for the parent to read but does not also print it.
        function Write-Log { param([string]$Message) Write-Information -MessageData $Message }
        foreach ($d in $Defs) { . ([scriptblock]::Create($d)) }
        $why = Initialize-Worktree -Path $Path -RepoDir $RepoDir -CopyFiles $CopyFiles -Setup $Setup -TimeoutMinutes $TimeoutMinutes
        "$why"
    }

    $jobs = foreach ($i in $items) {
        Write-Log "    setup for $($i.dir) started, beside the others"
        @{ item = $i; seen = 0
           job = Start-Job -ScriptBlock $block -ArgumentList $defs, $i.path, $i.hub, @($i.copy), @($i.setup), $TimeoutMinutes }
    }

    function Send-JobLog {
        param($Entry)
        $info = $Entry.job.ChildJobs[0].Information
        while ($Entry.seen -lt $info.Count) {
            Write-Log "    [$($Entry.item.dir)] $("$($info[$Entry.seen].MessageData)".Trim())"
            $Entry.seen++
        }
    }

    do {
        $running = $false
        foreach ($j in $jobs) {
            Send-JobLog $j
            if ($j.job.State -in 'NotStarted', 'Running') { $running = $true }
        }
        if ($running) { Start-Sleep -Milliseconds 500 }
    } while ($running)

    foreach ($j in $jobs) {
        Send-JobLog $j
        $out = @(Receive-Job -Job $j.job -InformationAction SilentlyContinue -ErrorAction SilentlyContinue)
        $why = if ($j.job.State -eq 'Failed') {
            "worktree setup could not run: $($j.job.ChildJobs[0].JobStateInfo.Reason.Message)"
        } else { "$($out | Select-Object -Last 1)".Trim() }
        Remove-Job -Job $j.job -Force -ErrorAction SilentlyContinue
        if ($why) { [void]$failed.Add(@{ dir = $j.item.dir; reason = $why }) }
    }
    return @{ ok = ($failed.Count -eq 0); failed = $failed.ToArray() }
}

# Give this ticket its workspace BEFORE the worker runs.
#
# The set of one is Resolve-Worktree, called exactly as worker.ps1 always has, with the one member
# added to its result. For a project of several:
#
#   1. Every repo's origin is fetched, so "the base" is current for all of them.
#   2. Outside speculate, at least one writable repo must already have the branch - otherwise it is
#      still the upstream error it is for one repo. A writable repo without it gets it from its base:
#      a repo the ticket has not touched has nothing on its branch to lose, and this is how a repo
#      added to "repos" mid-ticket joins.
#   3. Each repo's worktree is made, one by one, each under its own hub's mutex.
#   4. A writable repo with nothing of its own is fast-forwarded to its base, every phase: the agent
#      should read the API as it is now, not as it was when the ticket was speculated.
#   5. Every repo that needs it is set up, all at once (Invoke-WorkspaceSetup).
#
# A peer review takes its own shape after step 1: see Resolve-ReviewWorkspace.
#
# Stops at the first repo that fails, naming it; the worktrees already made stay, and the next run
# re-enters them as Resolve-Worktree already does for one.
# Returns @{ ok; path; branch; members; created } or @{ ok = $false; reason }. Each member carries
# its worktree's path.
function Resolve-Workspace {
    param(
        [object[]]$Members, [string]$WorktreeRoot, [string]$Skill, [string]$TicketId,
        [string]$RecordedBranch, [string]$IssueBranchToken, [string]$BranchPrefix,
        [string]$ReviewBranch, [string]$ReviewRepo, [bool]$MergeLocally, [bool]$OnBase,
        [double]$SetupTimeoutMinutes = $(if ($WorktreeSetupTimeoutMinutes) { $WorktreeSetupTimeoutMinutes } else { 20 })
    )

    $ErrorActionPreference = 'Continue'
    $all = @($Members)
    if ($all.Count -eq 0) { return @{ ok = $false; reason = "this project names no repo" } }

    if (-not (Test-MultiRepo $all)) {
        $m = $all[0]
        $wt = Resolve-Worktree -RepoDir $m.hub -WorktreeRoot $WorktreeRoot -Skill $Skill -TicketId $TicketId `
            -RecordedBranch $RecordedBranch -IssueBranchToken $IssueBranchToken -BaseBranch $m.baseBranch `
            -BranchPrefix $BranchPrefix -CopyFiles @($m.copy) -Setup @($m.setup) -ReviewBranch $ReviewBranch `
            -MergeLocally $MergeLocally -OnBase $OnBase
        if ($wt.ok -and $wt.path) {
            $one = $m.Clone(); $one.path = $wt.path
            $wt.members = @($one)
        }
        return $wt
    }

    # Refused upstream as well (RepoLayout, the claim, ticket creation); here so this can never be
    # the code that finds out.
    if ($OnBase) { return @{ ok = $false; reason = "a project of several repos cannot run a slice's walk-through yet (docs/multi-repo-plan.md)" } }
    if ($MergeLocally) { return @{ ok = $false; reason = "a project of several repos delivers by pull request; mergeInsteadOfPr is refused for it" } }
    if (-not $WorktreeRoot) { return @{ ok = $false; reason = "no worktree root configured for this project and none could be derived from its path" } }

    $branch = Get-TicketBranchName -RecordedBranch $RecordedBranch -IssueBranchToken $IssueBranchToken `
        -BranchPrefix $BranchPrefix -TicketId $TicketId
    $workspace = Join-Path $WorktreeRoot (Get-WorktreeDirName $branch)

    foreach ($m in $all) {
        if (-not (Test-Path (Join-Path $m.hub '.git'))) { return @{ ok = $false; reason = "$($m.dir): '$($m.hub)' is not a git repo" } }
        $hub = $m.hub
        $null = Invoke-WithMutex -Name "hub-$hub" -TimeoutMs 900000 -Body {
            if (@(git -C $hub remote 2>$null) -contains 'origin') { git -C $hub fetch origin --prune *> $null }
        }
    }

    if ($ReviewBranch) {
        return Resolve-ReviewWorkspace -Members $all -WorktreeRoot $WorktreeRoot -ReviewBranch $ReviewBranch `
            -ReviewRepo $ReviewRepo -SetupTimeoutMinutes $SetupTimeoutMinutes
    }

    $writable = @($all | Where-Object { -not $_.readOnly })
    if ($Skill -ne 'speculate' -and -not @($writable | Where-Object { Test-BranchRef -RepoDir $_.hub -Name $branch })) {
        return @{ ok = $false; reason = "no feature branch '$branch' in any of this project's repos (expected one from speculation)" }
    }

    if (Test-Path $workspace -PathType Leaf) { return @{ ok = $false; reason = "'$workspace' is a file where this ticket's workspace belongs; move or remove it by hand" } }
    if (-not (Test-Path $workspace)) { New-Item -ItemType Directory -Path $workspace -Force | Out-Null }

    $out = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.List[object]
    $anyCreated = $false
    foreach ($m in $all) {
        $expected = Join-Path $workspace $m.dir
        if ($m.readOnly) {
            $r = Resolve-ReadOnlyWorktree -RepoDir $m.hub -Path $expected -BaseBranch $m.baseBranch
        }
        else {
            $r = Resolve-Worktree -RepoDir $m.hub -WorktreeRoot $workspace -WorktreeName $m.dir -Skill $Skill `
                -TicketId $TicketId -RecordedBranch $branch -IssueBranchToken '' -BaseBranch $m.baseBranch `
                -BranchPrefix $BranchPrefix -CopyFiles @($m.copy) -Setup @($m.setup) -ReviewBranch '' `
                -MergeLocally $false -MayCreate -NoSetup
            if ($r.ok -and $r.deferred) { $r = @{ ok = $false; reason = "'$($m.hub)' is not a git repo" } }
            if ($r.ok -and (Get-FullPathKey $r.path) -ne (Get-FullPathKey $expected)) {
                $r = @{ ok = $false; reason = "branch '$branch' is checked out at '$($r.path)', outside this ticket's workspace - remove that worktree, or move it to '$expected'" }
            }
            if ($r.ok) {
                $baseRef = Resolve-BaseRef -RepoDir $m.hub -BaseBranch $m.baseBranch -MergeLocally $false
                $moved = if ($baseRef) { Update-MergedBranch -Path $r.path -BaseRef $baseRef } else { 0 }
                if ($moved -gt 0) {
                    Write-Log "  $($m.dir): nothing of its own on '$branch' - fast-forwarded $moved commit(s) to $baseRef"
                    $r.needsSetup = $true
                }
            }
        }
        if (-not $r.ok) { return @{ ok = $false; reason = "$($m.dir): $($r.reason)"; fault = [bool]$r.fault } }

        $entry = $m.Clone()
        $entry.path = $r.path
        $entry.created = [bool]$r.created
        if ($r.created) { $anyCreated = $true }
        [void]$out.Add($entry)
        if ($r.needsSetup) { [void]$pending.Add(@{ dir = $m.dir; path = $r.path; hub = $m.hub; copy = @($m.copy); setup = @($m.setup) }) }
    }

    if ($pending.Count -gt 0) {
        $setup = Invoke-WorkspaceSetup -Items $pending.ToArray() -TimeoutMinutes $SetupTimeoutMinutes
        if (-not $setup.ok) {
            $names = ($setup.failed | ForEach-Object { $_.dir }) -join ', '
            $each = ($setup.failed | ForEach-Object { "$($_.dir): $($_.reason)" }) -join "`n`n"
            return @{ ok = $false; reason = "worktree setup failed in $names`n`n$each" }
        }
    }

    return @{ ok = $true; path = $workspace; branch = $branch; members = $out.ToArray(); created = $anyCreated }
}

# A peer review's workspace on a project of several repos. A pull request lives in one repo, so the
# review names it ($ReviewRepo): that repo is checked out DETACHED at their branch, exactly as the
# one repo of a project of one is (Resolve-Worktree's review track), and every other repo detached
# at its base, there to be read as the code their change has to work against. Nothing in the
# workspace is on a branch, so there is nothing in it to commit onto.
#
# The folder is named after the repo and the branch, not the ticket, and is reused across passes -
# setting a workspace up is what costs, and a branch reviewed three times should pay for it once.
# Every repo is checked out again each pass: theirs to what origin has now, the rest to their base.
# Returns what Resolve-Workspace does; the member under review carries underReview = $true.
function Resolve-ReviewWorkspace {
    param(
        [object[]]$Members, [string]$WorktreeRoot, [string]$ReviewBranch, [string]$ReviewRepo,
        [double]$SetupTimeoutMinutes = 20
    )

    $all = @($Members)
    $writable = (@($all | Where-Object { -not $_.readOnly } | ForEach-Object { $_.dir })) -join ', '
    if (-not $ReviewRepo) {
        return @{ ok = $false; reason = "this review does not say which repo '$ReviewBranch' is in, and on a project of several it has to ($writable)" }
    }
    $under = @($all | Where-Object { $_.dir -eq $ReviewRepo }) | Select-Object -First 1
    if (-not $under) { return @{ ok = $false; reason = "this review names the repo '$ReviewRepo', which is not one of this project's ($writable)" } }
    if ($under.readOnly) { return @{ ok = $false; reason = "this review names '$ReviewRepo', which this project keeps read-only: there is no pull request in it to review" } }

    $workspace = Join-Path $WorktreeRoot (Get-WorktreeDirName "review-$($under.dir)-$ReviewBranch")
    if (Test-Path $workspace -PathType Leaf) { return @{ ok = $false; reason = "'$workspace' is a file where this review's workspace belongs; move or remove it by hand" } }
    if (-not (Test-Path $workspace)) { New-Item -ItemType Directory -Path $workspace -Force | Out-Null }

    $out = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.List[object]
    $anyCreated = $false
    $sha = $null
    foreach ($m in $all) {
        $expected = Join-Path $workspace $m.dir
        $isUnder = $m.dir -eq $under.dir
        if ($isUnder) {
            $r = Resolve-Worktree -RepoDir $m.hub -WorktreeRoot $workspace -WorktreeName $m.dir -Skill '' -TicketId '' `
                -BaseBranch $m.baseBranch -CopyFiles @($m.copy) -Setup @($m.setup) -ReviewBranch $ReviewBranch `
                -MergeLocally $false -NoSetup
            if ($r.ok -and $r.deferred) { $r = @{ ok = $false; reason = "'$($m.hub)' is not a git repo" } }
            if ($r.ok) { $sha = $r.sha }
        }
        else {
            $r = Resolve-ReadOnlyWorktree -RepoDir $m.hub -Path $expected -BaseBranch $m.baseBranch
        }
        if ($r.ok -and (Get-FullPathKey $r.path) -ne (Get-FullPathKey $expected)) {
            $r = @{ ok = $false; reason = "its worktree is at '$($r.path)', outside this review's workspace - remove that worktree, or move it to '$expected'" }
        }
        if (-not $r.ok) { return @{ ok = $false; reason = "$($m.dir): $($r.reason)"; fault = [bool]$r.fault } }

        $entry = $m.Clone()
        $entry.path = $r.path
        $entry.created = [bool]$r.created
        $entry.underReview = $isUnder
        if ($r.created) { $anyCreated = $true }
        [void]$out.Add($entry)
        if ($r.needsSetup) { [void]$pending.Add(@{ dir = $m.dir; path = $r.path; hub = $m.hub; copy = @($m.copy); setup = @($m.setup) }) }
    }

    if ($pending.Count -gt 0) {
        $setup = Invoke-WorkspaceSetup -Items $pending.ToArray() -TimeoutMinutes $SetupTimeoutMinutes
        if (-not $setup.ok) {
            $names = ($setup.failed | ForEach-Object { $_.dir }) -join ', '
            $each = ($setup.failed | ForEach-Object { "$($_.dir): $($_.reason)" }) -join "`n`n"
            return @{ ok = $false; reason = "worktree setup failed in $names`n`n$each" }
        }
    }

    return @{ ok = $true; path = $workspace; branch = $ReviewBranch; sha = $sha; members = $out.ToArray(); created = $anyCreated }
}

# Copy what a project keeps for its workspace's root (data/projects/<key>/workspace/) into the
# workspace: a conventions file mapping the repos, typically. Anything under a folder named like one
# of the repos is skipped and logged - a copy there would land in that repo's worktree and dirty it.
# Returns how many files were copied; 0 when the project keeps none.
function Copy-WorkspaceFiles {
    param([string]$From, [string]$To, [string[]]$Skip = @())

    if (-not $From -or -not (Test-Path $From -PathType Container)) { return 0 }
    $root = (Resolve-Path -LiteralPath $From).Path.TrimEnd('\', '/')
    $copied = 0
    foreach ($f in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
        $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/')
        $first = ($rel -split '[\\/]')[0]
        if (@($Skip | Where-Object { $_ -and $_ -eq $first }).Count -gt 0) {
            Write-Log "    (skip) workspace file '$rel' is inside the repo '$first'"
            continue
        }
        $dest = Join-Path $To $rel
        $parent = Split-Path $dest -Parent
        if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        try { Copy-Item -LiteralPath $f.FullName -Destination $dest -Force -ErrorAction Stop; $copied++ }
        catch { Write-Log "    could not copy workspace file '$rel': $($_.Exception.Message)" }
    }
    return $copied
}

# What the prompt says about the repos of a project of several, appended to its base-ref line so a
# project of one - for which this is empty - reads exactly as it always did. Each repo's own base
# ref is named here, because "the base ref" is per repo and the skills say <base-ref>.
function Format-ReposNote {
    param([object[]]$Members, [string]$Branch, [bool]$MergeLocally)

    if (-not (Test-MultiRepo $Members)) { return '' }
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('')

    # A peer review's workspace (Resolve-ReviewWorkspace): one repo at their branch, the rest at
    # their bases, and nothing on a branch of ours to commit to.
    $under = @(@($Members) | Where-Object { $_.underReview }) | Select-Object -First 1
    if ($under) {
        [void]$lines.Add("- **Repos:** this working directory is a workspace of several git repos, each its own repo, and every one of them is checked out detached:")
        foreach ($m in @($Members)) {
            $ref = if ($m.path) { Resolve-BaseRef -RepoDir $m.path -BaseBranch $m.baseBranch -MergeLocally $MergeLocally } else { $null }
            if (-not $ref) { $ref = $m.baseBranch }
            if ($m.underReview) { [void]$lines.Add("  - ``$($m.dir)`` - **under review**: their branch ``$Branch``, base ref ``$ref``. This is the repo the pull request is in.") }
            else { [void]$lines.Add("  - ``$($m.dir)`` - at its base ``$ref``, here to be read as the code their change has to work with.") }
        }
        [void]$lines.Add("  The workspace root is not a repo: run git inside one (``git -C <repo> ...``), never at the root. Only ``$($under.dir)`` is under review - wherever a skill says ``<base-ref>``, it means that repo's base ref from this list. Change nothing in any of them. Every file path you give ``add_review_comment`` is relative to ``$($under.dir)``, where the pull request is.")
        return ($lines -join "`n")
    }

    [void]$lines.Add("- **Repos:** this working directory is a workspace of several git repos, each its own repo. Every repo listed with a base ref is on branch ``$Branch``:")
    foreach ($m in @($Members)) {
        if ($m.readOnly) {
            [void]$lines.Add("  - ``$($m.dir)`` - **read-only**: here to be read, never changed. It has no branch, and nothing in it is ever delivered.")
            continue
        }
        $ref = if ($m.path) { Resolve-BaseRef -RepoDir $m.path -BaseBranch $m.baseBranch -MergeLocally $MergeLocally } else { $null }
        if (-not $ref) { $ref = $m.baseBranch }
        [void]$lines.Add("  - ``$($m.dir)`` - base ref ``$ref``")
    }
    [void]$lines.Add("  The workspace root is not a repo: run git inside one (``git -C <repo> ...``), never at the root. Commit in every repo you change, on its branch. A repo you did not change is delivered as nothing - do not commit to it to keep it in step. Wherever a skill says ``<base-ref>``, use that repo's base ref from this list. Each repo keeps its own conventions file inside it, and they apply to the code in that repo.")
    return ($lines -join "`n")
}

# The writable members this ticket has touched, in config order: commits ahead of their base ref, or
# uncommitted changes. A member whose base cannot be found counts as touched, so the delivery steps
# that follow fail loudly on it rather than skip work nobody else has. Members need a path.
function Get-TouchedMembers {
    param([object[]]$Members, [bool]$MergeLocally)

    $touched = New-Object System.Collections.Generic.List[object]
    foreach ($m in @($Members)) {
        if ($m.readOnly -or -not $m.path -or -not (Test-Path $m.path)) { continue }
        if (@(git -C $m.path status --porcelain 2>$null | Where-Object { $_ }).Count -gt 0) { [void]$touched.Add($m); continue }
        $baseRef = Resolve-BaseRef -RepoDir $m.path -BaseBranch $m.baseBranch -MergeLocally $MergeLocally
        if (-not $baseRef) { [void]$touched.Add($m); continue }
        $ahead = [int]("$(git -C $m.path rev-list --count "$baseRef..HEAD" 2>$null)".Trim())
        if ($ahead -gt 0) { [void]$touched.Add($m) }
    }
    return $touched.ToArray()
}

# The readOnly members with changes in them: the fault Resolve-ReadOnlyWorktree refuses to start on.
function Get-ReadOnlyFaults {
    param([object[]]$Members)
    return @(@($Members) | Where-Object {
        $_.readOnly -and $_.path -and (Test-Path $_.path) -and @(git -C $_.path status --porcelain 2>$null | Where-Object { $_ }).Count -gt 0
    })
}
