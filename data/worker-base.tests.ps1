# Exercises the base-ref half of data/worker.ps1 against real throwaway repositories: which ref a
# ticket's branch is forked from, rebased onto and squashed against, for a project that delivers by
# pull request and for one that merges into its local base instead (mergeInsteadOfPr).
#
# worker.ps1 cannot be dot-sourced - it is a script that works a claim - so its functions are lifted
# out of its syntax tree and defined here, with the two things they borrow from
# heartbeat-common.ps1 stubbed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-base.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
$worker = Join-Path $PSScriptRoot 'worker.ps1'

function Write-Log { param([string]$Message) }
function Invoke-WithMutex { param([string]$Name, [scriptblock]$Body, [int]$TimeoutMs) return & $Body }

$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$null)
foreach ($fn in $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
. (Join-Path $PSScriptRoot 'worker-repos.ps1')

$env:GIT_TERMINAL_PROMPT = '0'
$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-base-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

function Add-Commit {
    param([string]$Dir, [string]$File, [string]$Subject)
    Add-Content -Path (Join-Path $Dir $File) -Value $Subject -Encoding ascii
    git -C $Dir add -A *> $null
    git -C $Dir commit -m $Subject *> $null
}

# A hub with one pushed commit on main, a bare origin beside it, and a worktree root.
function New-Hub {
    param([string]$Name, [switch]$NoOrigin)
    $dir = Join-Path $root $Name
    $hub = Join-Path $dir 'hub'
    New-Item -ItemType Directory -Path $hub -Force | Out-Null
    git -C $hub init -b main *> $null
    git -C $hub config user.email 'test@example.com'
    git -C $hub config user.name 'Test'
    git -C $hub config commit.gpgsign false
    Add-Commit -Dir $hub -File 'README.md' -Subject 'init'
    if (-not $NoOrigin) {
        $origin = Join-Path $dir 'origin.git'
        git init --bare -b main $origin *> $null
        git -C $hub remote add origin $origin *> $null
        git -C $hub push -u origin main *> $null
    }
    return @{ hub = $hub; wt = (Join-Path $dir 'wt') }
}

function Get-Sha { param([string]$Dir, [string]$Ref) return (git -C $Dir rev-parse $Ref 2>$null) }

function New-TicketWorktree {
    param($Repo, [string]$Branch, [bool]$MergeLocally, [string]$Skill = 'speculate')
    return Resolve-Worktree -RepoDir $Repo.hub -WorktreeRoot $Repo.wt -Skill $Skill -TicketId '1' `
        -RecordedBranch $Branch -IssueBranchToken '' -BaseBranch 'main' -BranchPrefix '' `
        -CopyFiles @() -Setup @() -ReviewBranch '' -MergeLocally $MergeLocally
}

# What the board's merge does: a --no-ff merge commit on local main in the hub, nothing pushed.
function Merge-Ticket {
    param($Repo, [string]$Branch)
    git -C $Repo.hub merge --no-ff --no-edit -m "Merge $Branch" $Branch *> $null
}

try {
    # ---- Resolve-BaseRef ------------------------------------------------------
    Write-Host "== Resolve-BaseRef"
    $r = New-Hub 'ref'
    Check 'PR project, local level with origin -> origin' (Resolve-BaseRef -RepoDir $r.hub -BaseBranch main -MergeLocally $false) 'origin/main'
    Check 'merge project, local level with origin -> local' (Resolve-BaseRef -RepoDir $r.hub -BaseBranch main -MergeLocally $true) 'main'

    Add-Commit -Dir $r.hub -File 'a.txt' -Subject 'unpushed'
    Check 'PR project, local ahead -> still origin' (Resolve-BaseRef -RepoDir $r.hub -BaseBranch main -MergeLocally $false) 'origin/main'
    Check 'merge project, local ahead -> local' (Resolve-BaseRef -RepoDir $r.hub -BaseBranch main -MergeLocally $true) 'main'

    # Local strictly behind: push the commit, then wind local back.
    git -C $r.hub push origin main *> $null
    git -C $r.hub reset --hard HEAD~1 *> $null
    Check 'merge project, local strictly behind -> origin' (Resolve-BaseRef -RepoDir $r.hub -BaseBranch main -MergeLocally $true) 'origin/main'

    Add-Commit -Dir $r.hub -File 'b.txt' -Subject 'diverged'
    Check 'merge project, diverged -> local' (Resolve-BaseRef -RepoDir $r.hub -BaseBranch main -MergeLocally $true) 'main'

    $n = New-Hub 'noorigin' -NoOrigin
    Check 'no origin -> local' (Resolve-BaseRef -RepoDir $n.hub -BaseBranch main -MergeLocally $false) 'main'
    Check 'no such base -> null' (Resolve-BaseRef -RepoDir $n.hub -BaseBranch nope -MergeLocally $true) ''

    # ---- Resolve-Worktree: where a new ticket forks from ----------------------
    Write-Host "== Resolve-Worktree"
    $m = New-Hub 'fork-merge'
    Add-Commit -Dir $m.hub -File 'earlier.txt' -Subject 'an earlier ticket, merged locally and never pushed'
    $wt = New-TicketWorktree -Repo $m -Branch 't-1' -MergeLocally $true
    Check 'merge project: worktree is created' $wt.ok $true
    Check 'merge project: new branch starts at local main' (Get-Sha $wt.path HEAD) (Get-Sha $m.hub main)
    Check 'merge project: the unpushed work is in the worktree' (Test-Path (Join-Path $wt.path 'earlier.txt')) $true
    Check 'merge project: branch has no upstream' (git -C $wt.path rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null) ''

    $p = New-Hub 'fork-pr'
    Add-Commit -Dir $p.hub -File 'local.txt' -Subject 'the human, mid-work on main'
    $wt = New-TicketWorktree -Repo $p -Branch 't-1' -MergeLocally $false
    Check 'PR project: new branch starts at origin/main' (Get-Sha $wt.path HEAD) (Get-Sha $p.hub origin/main)

    # ---- A merged ticket sent back --------------------------------------------
    Write-Host "== a ticket merged locally, then sent back"
    $s = New-Hub 'sentback'
    $wt = New-TicketWorktree -Repo $s -Branch 't-1' -MergeLocally $true
    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T1: the feature'
    $merged = Get-Sha $wt.path HEAD
    Merge-Ticket -Repo $s -Branch 't-1'
    Add-Commit -Dir $s.hub -File 'other.txt' -Subject 'another ticket lands after it'

    $again = New-TicketWorktree -Repo $s -Branch 't-1' -MergeLocally $true
    Check 're-speculation: same worktree' ([IO.Path]::GetFullPath($again.path)) $wt.path
    Check 're-speculation: branch fast-forwarded to local main' (Get-Sha $wt.path HEAD) (Get-Sha $s.hub main)

    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T1: follow-up'
    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T1: fix-up'
    $squash = Compress-TicketCommits -WorkDir $wt.path -TicketId '1' -Title 'The follow-up' -IssueRef '' `
        -BaseBranch 'main' -Summary 'Does the follow-up.' -MergeLocally $true
    Check 'squash: folds only the two new commits' $squash.from 2
    Check 'squash: one commit past local main' (git -C $wt.path rev-list --count main..HEAD) 1
    Check 'squash: the merged commit is untouched' $(git -C $wt.path merge-base --is-ancestor $merged HEAD; $LASTEXITCODE) 0

    # The same history without the fast-forward: follow-up commits made straight on the old
    # branch, as a ticket already mid-flight when this change landed would have them.
    $o = New-Hub 'sentback-old'
    $wt = New-TicketWorktree -Repo $o -Branch 't-1' -MergeLocally $true
    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T1: the feature'
    $merged = Get-Sha $wt.path HEAD
    Merge-Ticket -Repo $o -Branch 't-1'
    Add-Commit -Dir $o.hub -File 'other.txt' -Subject 'another ticket lands after it'
    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T1: follow-up'
    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T1: fix-up'

    # Measured against origin, the squash range is all three of this ticket's commits, one of them
    # already in main. That is the bug this file exists for.
    Check 'against origin the range holds the merged commit too' (git -C $wt.path rev-list --count origin/main..HEAD) 3
    Check 'against local main it holds only what is new' (git -C $wt.path rev-list --count main..HEAD) 2

    $notPlanning = New-TicketWorktree -Repo $o -Branch 't-1' -MergeLocally $true -Skill 'refine'
    Check 'a branch with commits of its own is not moved' (git -C $wt.path log -1 --format=%s) 'T1: fix-up'

    $squash = Compress-TicketCommits -WorkDir $wt.path -TicketId '1' -Title 'The follow-up' -IssueRef '' `
        -BaseBranch 'main' -Summary 'Does the follow-up.' -MergeLocally $true
    Check 'squash (no ff): folds only the two new commits' $squash.from 2
    Check 'squash (no ff): parent is the merged commit' (Get-Sha $wt.path 'HEAD~1') $merged

    $rebase = Update-BranchFromBase -WorkDir $wt.path -BaseBranch 'main' -RepoDir $o.hub -MergeLocally $true
    Check 'rebase: measured against local main' $rebase.base 'main'
    Check 'rebase: replayed' $rebase.rebased $true
    Check 'rebase: now directly on local main' (Get-Sha $wt.path 'HEAD~1') (Get-Sha $o.hub main)
    Check 'rebase: later local work is in the worktree' (Test-Path (Join-Path $wt.path 'other.txt')) $true

    # ---- Update-BranchFromBase on the two kinds of project --------------------
    Write-Host "== Update-BranchFromBase"
    $u = New-Hub 'rebase-merge'
    $wt = New-TicketWorktree -Repo $u -Branch 't-2' -MergeLocally $true
    Add-Commit -Dir $wt.path -File 'mine.txt' -Subject 'T2: work'
    Add-Commit -Dir $u.hub -File 'landed.txt' -Subject 'merged locally while T2 was in flight'
    $rebase = Update-BranchFromBase -WorkDir $wt.path -BaseBranch 'main' -RepoDir $u.hub -MergeLocally $true
    Check 'merge project: rebased onto local main' "$($rebase.rebased) $($rebase.base) $($rebase.behind)" 'True main 1'
    Check 'merge project: local work arrived' (Test-Path (Join-Path $wt.path 'landed.txt')) $true

    $l = New-Hub 'rebase-noorigin' -NoOrigin
    $wt = New-TicketWorktree -Repo $l -Branch 't-2' -MergeLocally $true
    Add-Commit -Dir $wt.path -File 'mine.txt' -Subject 'T2: work'
    Add-Commit -Dir $l.hub -File 'landed.txt' -Subject 'merged locally'
    $rebase = Update-BranchFromBase -WorkDir $wt.path -BaseBranch 'main' -RepoDir $l.hub -MergeLocally $true
    Check 'merge project with no origin: still rebased' $rebase.rebased $true

    $q = New-Hub 'rebase-pr'
    $wt = New-TicketWorktree -Repo $q -Branch 't-2' -MergeLocally $false
    Add-Commit -Dir $wt.path -File 'mine.txt' -Subject 'T2: work'
    Add-Commit -Dir $q.hub -File 'local.txt' -Subject 'the human, unpushed'
    $rebase = Update-BranchFromBase -WorkDir $wt.path -BaseBranch 'main' -RepoDir $q.hub -MergeLocally $false
    Check 'PR project: unpushed local main is ignored' "$($rebase.rebased) $($rebase.base)" 'False origin/main'

    $conflict = New-Hub 'rebase-conflict'
    $wt = New-TicketWorktree -Repo $conflict -Branch 't-3' -MergeLocally $true
    Add-Commit -Dir $wt.path -File 'README.md' -Subject 'T3: edits the readme'
    $before = Get-Sha $wt.path HEAD
    Add-Commit -Dir $conflict.hub -File 'README.md' -Subject 'so did a ticket merged first'
    $rebase = Update-BranchFromBase -WorkDir $wt.path -BaseBranch 'main' -RepoDir $conflict.hub -MergeLocally $true
    Check 'conflict with local main is reported' "$($rebase.ok) $($rebase.conflicts -join ',')" 'False README.md'
    Check 'conflict: branch left as it was' (Get-Sha $wt.path HEAD) $before

    # ---- Two repos, two bases (docs/multi-repo-plan.md) -------------------------
    # A project of several repos asks Resolve-BaseRef once per repo, and each answer is that repo's
    # alone. A works off main and has a human's unpushed commit on it; B works off develop. A also
    # has a develop of its own holding something else, so an answer that leaked from one repo to the
    # other would fork from the wrong commit. Both have an origin: a project of several delivers by
    # pull request, and a pull request needs one.
    Write-Host "== two repos, two bases"
    $a = New-Hub 'pair-a'
    git -C $a.hub branch develop *> $null
    git -C $a.hub checkout -q develop *> $null
    Add-Commit -Dir $a.hub -File 'a-develop.txt' -Subject "A's develop, which is not A's base"
    git -C $a.hub checkout -q main *> $null
    Add-Commit -Dir $a.hub -File 'unpushed.txt' -Subject 'the human, unpushed'
    $b = New-Hub 'pair-b'
    git -C $b.hub checkout -q -b develop *> $null
    Add-Commit -Dir $b.hub -File 'b-develop.txt' -Subject "B's develop, which is B's base"
    git -C $b.hub push -q -u origin develop *> $null
    git -C $b.hub checkout -q main *> $null

    $memberA = @{ dir = 'a'; hub = $a.hub; baseBranch = 'main'; setup = @(); copy = @(); readOnly = $false }
    $memberB = @{ dir = 'b'; hub = $b.hub; baseBranch = 'develop'; setup = @(); copy = @(); readOnly = $false }
    foreach ($order in @(@($memberA, $memberB), @($memberB, $memberA))) {
        $label = (($order | ForEach-Object { $_.dir }) -join ',')
        $ws = Resolve-Workspace -Members $order -WorktreeRoot (Join-Path $root 'pair-wt') -Skill 'speculate' -TicketId '9' `
            -RecordedBranch "t-pair-$($label -replace ',', '')" -IssueBranchToken '' -BranchPrefix '' -ReviewBranch '' -MergeLocally $false
        Check "[$label] workspace: ok" $ws.ok $true
        $wa = (@($ws.members | Where-Object { $_.dir -eq 'a' })[0]).path
        $wb = (@($ws.members | Where-Object { $_.dir -eq 'b' })[0]).path
        Check "[$label] A's base is origin/main" (Resolve-BaseRef -RepoDir $wa -BaseBranch 'main' -MergeLocally $false) 'origin/main'
        Check "[$label] B's base is origin/develop" (Resolve-BaseRef -RepoDir $wb -BaseBranch 'develop' -MergeLocally $false) 'origin/develop'
        Check "[$label] A forked from origin/main, not its unpushed main" (Get-Sha $wa HEAD) (Get-Sha $a.hub origin/main)
        Check "[$label] B forked from its own develop" (Get-Sha $wb HEAD) (Get-Sha $b.hub origin/develop)
        Check "[$label] neither has the other's develop" ((Test-Path (Join-Path $wa 'b-develop.txt')) -or (Test-Path (Join-Path $wa 'a-develop.txt')) -or (Test-Path (Join-Path $wb 'a-develop.txt'))) $false
    }

    # The hand-off rebase is per repo too: each is measured against its own base.
    Add-Commit -Dir $wa -File 'mine.txt' -Subject 'A: work'
    Add-Commit -Dir $wb -File 'mine.txt' -Subject 'B: work'
    git -C $b.hub checkout -q develop *> $null
    Add-Commit -Dir $b.hub -File 'b-landed.txt' -Subject "landed on B's develop meanwhile"
    git -C $b.hub push -q origin develop *> $null
    git -C $b.hub checkout -q main *> $null
    $ra = Update-BranchFromBase -WorkDir $wa -BaseBranch 'main' -RepoDir $a.hub -MergeLocally $false
    $rb = Update-BranchFromBase -WorkDir $wb -BaseBranch 'develop' -RepoDir $b.hub -MergeLocally $false
    Check "A's rebase: against origin/main, nothing to do" "$($ra.ok) $($ra.rebased) $($ra.base)" 'True False origin/main'
    Check "B's rebase: against origin/develop, replayed" "$($rb.ok) $($rb.rebased) $($rb.base) $($rb.behind)" 'True True origin/develop 1'
    Check "B's rebase brought only B's base" ((Test-Path (Join-Path $wb 'b-landed.txt')) -and -not (Test-Path (Join-Path $wa 'b-landed.txt'))) $true

    # ---- A setup command that fails says why ----------------------------------
    # Its output was only ever in the log of whichever machine ran it; on a worker host that is
    # another computer, and "exit 1" on the ticket is all anyone saw.
    Write-Host "== a failed setup command carries its output"
    $sf = New-Hub 'setup-fail'
    $wt = New-TicketWorktree -Repo $sf -Branch 't-4' -MergeLocally $true
    $why = Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('echo resolving packages& echo ERR_PNPM_LOCKFILE the lockfile is out of date 1>&2& exit /b 1')
    $first = ($why -split "`n")[0]
    Check 'first line: the command and its exit code' $first 'worktree setup command failed (exit 1): echo resolving packages& echo ERR_PNPM_LOCKFILE the lockfile is out of date 1>&2& exit /b 1'
    Check 'its standard output is in the reason' ($why -match 'resolving packages') $true
    Check 'and its error output' ($why -match 'ERR_PNPM_LOCKFILE the lockfile is out of date') $true
    Check 'fenced, so it reads as output' ([regex]::Matches($why, '```').Count) 2

    $quiet = Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('exit /b 3')
    Check 'a command that printed nothing reads as before' $quiet 'worktree setup command failed (exit 3): exit /b 3'
    Check 'a setup that passes is still no reason at all' "[$(Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('echo fine'))]" '[]'

    # ---- A setup command cannot hang its run ------------------------------------
    Write-Host "== a setup command has a time limit and no stdin"
    $marker = 'ping -n 47 127.0.0.1'
    $started = Get-Date
    $why = Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @("echo installing& $marker") -TimeoutMinutes 0.05
    $took = ((Get-Date) - $started).TotalSeconds
    Check 'a command past its limit is stopped and says so' (($why -split "`n")[0]) "worktree setup command timed out after 0.05 min and was stopped: echo installing& $marker"
    Check '... promptly, not when it would have finished' ($took -lt 25) $true
    Check '... with what it printed before then' ($why -match 'installing') $true
    Start-Sleep -Milliseconds 500
    $left = @(Get-CimInstance Win32_Process -Filter "Name='PING.EXE' OR Name='ping.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like '*-n 47 127.0.0.1*' })
    Check '... and the process it started is gone too' $left.Count 0

    $started = Get-Date
    $why = Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('set /p answer=Download pnpm? [Y/n] & if not defined answer exit /b 7') -TimeoutMinutes 1
    Check 'a prompt gets end-of-input and fails at once' (($why -split "`n")[0]) 'worktree setup command failed (exit 7): set /p answer=Download pnpm? [Y/n] & if not defined answer exit /b 7'
    Check '... long before the limit' (((Get-Date) - $started).TotalSeconds -lt 15) $true
    Check '... with its question in the reason' ($why -match 'Download pnpm\?') $true

    $why = Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('echo first&& echo second 1>&2&& exit /b 4')
    Check "every step of an '&&' command is captured, error output too" (($why -match 'first') -and ($why -match 'second')) $true
    Check '... and the exit code is the last step''s' (($why -split "`n")[0] -match 'exit 4') $true
    $why = Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('echo "a quoted value"& exit /b 2')
    Check 'a quoted argument survives the wrapping' ($why -match '"a quoted value"') $true
    Check 'it runs in the worktree' "[$(Initialize-Worktree -Path $wt.path -RepoDir $sf.hub -CopyFiles @() -Setup @('if not exist README.md exit /b 9'))]" '[]'

    $many = New-Object System.Collections.Generic.List[string]
    1..40 | ForEach-Object { [void]$many.Add("line $_") }
    [void]$many.Add('')
    [void]$many.Add("$([char]27)[31mred error$([char]27)[0m")
    [void]$many.Add('a ``` fence inside')
    [void]$many.Add('x' * 400)
    $tail = Format-OutputTail -Lines $many
    Check 'only the last 15 lines' ([regex]::Matches($tail, 'line \d+').Count) 12
    Check 'the earliest of them is the right one' ($tail -match 'line 29' -and $tail -notmatch 'line 28\b') $true
    Check 'colour codes stripped' ($tail -match "red error" -and $tail -notmatch [char]27) $true
    Check 'a fence in the output cannot close the block early' ([regex]::Matches($tail, '```').Count) 2
    Check 'a very long line is cut' ($tail -match ('x' * 300 + ' \.\.\.')) $true
    Check 'no output, no block' (Format-OutputTail -Lines (New-Object System.Collections.Generic.List[string])) ''
}
finally {
    # Worktree .git files are read-only on Windows; -Force is what removes them.
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
