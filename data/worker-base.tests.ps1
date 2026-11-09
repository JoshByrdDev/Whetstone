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
. (Join-Path $PSScriptRoot 'worker-board.ps1')

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

    # ---- The project's rebase resolver, handed each stop ------------------------
    # Stand-ins for a project's rebase-resolve.ps1: one settles a conflict by keeping the base's side
    # (as the real one keeps the base's migration bookkeeping), one declines.
    $resolvers = Join-Path $root 'resolvers'
    New-Item -ItemType Directory -Path $resolvers -Force | Out-Null
    $settles = Join-Path $resolvers 'settles.ps1'
    Set-Content -Path $settles -Encoding ascii -Value @'
$in = [Console]::In.ReadToEnd() | ConvertFrom-Json
foreach ($f in @($in.conflicted)) { git checkout --ours -- $f 2>$null; git add -- $f 2>$null }
New-Item -ItemType File -Path 'made-by-resolver.tmp' -Force | Out-Null
Remove-Item 'made-by-resolver.tmp'
[Console]::Out.WriteLine("kept the base's side of $(@($in.conflicted) -join ',') onto $($in.baseRef)")
exit 0
'@
    $declines = Join-Path $resolvers 'declines.ps1'
    Set-Content -Path $declines -Encoding ascii -Value @'
$null = [Console]::In.ReadToEnd()
Set-Content -Path 'left-behind.tmp' -Value 'x'
[Console]::Error.WriteLine('not only migration bookkeeping conflicts')
exit 1
'@

    Write-Host "== Update-BranchFromBase with a resolver"
    $res = New-Hub 'rebase-resolved'
    $rw = New-TicketWorktree -Repo $res -Branch 't-6' -MergeLocally $true
    Add-Commit -Dir $rw.path -File 'README.md' -Subject 'T6: takes the next number'
    Add-Commit -Dir $rw.path -File 'own.txt' -Subject 'T6: its own work'
    Add-Commit -Dir $res.hub -File 'README.md' -Subject 'a ticket merged first took it'
    $rebase = Update-BranchFromBase -WorkDir $rw.path -BaseBranch 'main' -RepoDir $res.hub -MergeLocally $true -Resolver $settles
    Check 'a stop the resolver settles is continued' "$($rebase.ok) $($rebase.rebased) $(@($rebase.resolved) -join ',')" 'True True README.md'
    Check '... and what it said comes back' ((@($rebase.resolverSaid) -join ' ') -match "kept the base's side of README.md onto main") $true
    Check '... and the rest of the branch replayed' (Test-Path (Join-Path $rw.path 'own.txt')) $true
    git -C $rw.path merge-base --is-ancestor main HEAD; Check '... onto the base' $LASTEXITCODE 0
    Check '... with nothing left mid-rebase' ((git -C $rw.path status --porcelain) -join '') ''

    $dec = New-Hub 'rebase-declined'
    $rw = New-TicketWorktree -Repo $dec -Branch 't-7' -MergeLocally $true
    Add-Commit -Dir $rw.path -File 'README.md' -Subject 'T7: edits the readme'
    $rbefore = Get-Sha $rw.path HEAD
    Add-Commit -Dir $dec.hub -File 'README.md' -Subject 'so did a ticket merged first'
    $rebase = Update-BranchFromBase -WorkDir $rw.path -BaseBranch 'main' -RepoDir $dec.hub -MergeLocally $true -Resolver $declines
    Check 'a declined stop is a conflict' "$($rebase.ok) $($rebase.conflicts -join ',')" 'False README.md'
    Check '... that says the resolver declined, and why' ($rebase.reason -match 'declined: not only migration bookkeeping') $true
    Check '... with the branch as it was' (Get-Sha $rw.path HEAD) $rbefore
    Check '... and nothing the resolver left behind' ((git -C $rw.path status --porcelain) -join '') ''

    $none = New-Hub 'rebase-no-resolver'
    $rw = New-TicketWorktree -Repo $none -Branch 't-8' -MergeLocally $true
    Add-Commit -Dir $rw.path -File 'README.md' -Subject 'T8: edits the readme'
    Add-Commit -Dir $none.hub -File 'README.md' -Subject 'so did a ticket merged first'
    $rebase = Update-BranchFromBase -WorkDir $rw.path -BaseBranch 'main' -RepoDir $none.hub -MergeLocally $true `
        -Resolver (Get-RebaseResolver -ProjectDir (Join-Path $root 'no-such-project'))
    Check 'a project with no resolver conflicts as before' "$($rebase.ok) $($rebase.reason)" 'False conflicts with main'

    # ---- The rebase as an implement or QA run starts -----------------------------
    Write-Host "== Update-BranchAtStart"
    $start = Update-BranchAtStart -WorkDir $wt.path -BaseBranch 'main' -RepoDir $conflict.hub -MergeLocally $true
    Check 'a real conflict names its files' "$($start.rebased) $($start.conflicts -join ',')" 'False README.md'
    Check '... and leaves the branch as it was' (Get-Sha $wt.path HEAD) $before

    $clean = New-Hub 'start-clean'
    $wt = New-TicketWorktree -Repo $clean -Branch 't-5' -MergeLocally $true
    Add-Commit -Dir $wt.path -File 'mine.txt' -Subject 'T5: its own file'
    Add-Commit -Dir $clean.hub -File 'theirs.txt' -Subject 'landed meanwhile'
    $start = Update-BranchAtStart -WorkDir $wt.path -BaseBranch 'main' -RepoDir $clean.hub -MergeLocally $true
    Check 'a clean branch is rebased' "$($start.rebased) $($start.behind) $($start.conflicts.Count)" 'True 1 0'
    Check '... and now has what landed' (Test-Path (Join-Path $wt.path 'theirs.txt')) $true
    $start = Update-BranchAtStart -WorkDir $wt.path -BaseBranch 'main' -RepoDir $clean.hub -MergeLocally $true
    Check 'up to date: nothing to do' "$($start.rebased) $($start.conflicts.Count)" 'False 0'

    # A first commit that conflicts and a second that takes it back: rebasing stops on the first,
    # but the branch as a whole merges cleanly, so it is no reason to send anything back.
    $mid = New-Hub 'start-intermediate'
    $wt = New-TicketWorktree -Repo $mid -Branch 't-6' -MergeLocally $true
    $cut = Get-Sha $wt.path HEAD
    Add-Commit -Dir $wt.path -File 'README.md' -Subject 'T6: a readme edit it later drops'
    git -C $wt.path checkout $cut -- README.md *> $null
    Add-Commit -Dir $wt.path -File 'kept.txt' -Subject 'T6: the change it keeps'
    $before = Get-Sha $wt.path HEAD
    Add-Commit -Dir $mid.hub -File 'README.md' -Subject 'a readme edit that landed'
    $start = Update-BranchAtStart -WorkDir $wt.path -BaseBranch 'main' -RepoDir $mid.hub -MergeLocally $true
    Check 'an intermediate-only conflict is not a conflict' "$($start.rebased) $($start.conflicts.Count)" 'False 0'
    Check '... says why it did not rebase' ($start.reason -match 'intermediate') $true
    Check '... and leaves the branch as it was' (Get-Sha $wt.path HEAD) $before
    Check 'Get-MergeConflicts agrees: the whole merges' "$((Get-MergeConflicts -WorkDir $wt.path -BaseRef 'main').conflicts.Count)" '0'

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

    # ---- A worktree left dirty by a run that never reached its nets ------------
    # Refusing it blocked the ticket on every retry: "discard and retry" was an instruction to the
    # next agent, and the next agent never started. The fleet's own worktree is set aside instead.
    Write-Host "== a worktree left dirty by a killed run"
    $d = New-Hub 'dirty'
    $wt = New-TicketWorktree -Repo $d -Branch 't-1' -MergeLocally $false
    Add-Commit -Dir $wt.path -File 'done.txt' -Subject 'the run got this far'
    $last = Get-Sha $wt.path HEAD
    Add-Content -Path (Join-Path $wt.path 'done.txt') -Value 'half-finished' -Encoding ascii
    Set-Content -Path (Join-Path $wt.path 'new.test.txt') -Value 'untracked' -Encoding ascii
    $again = New-TicketWorktree -Repo $d -Branch 't-1' -MergeLocally $false -Skill 'implement'
    Check 'dirty fleet worktree: the phase starts' "$($again.ok) $((Get-FullPathKey $again.path) -eq (Get-FullPathKey $wt.path))" 'True True'
    Check '... on the last commit' (Get-Sha $wt.path HEAD) $last
    Check '... with a clean tree' "$(git -C $wt.path status --porcelain)" ''
    Check '... saying what it set aside' ((@($again.stash.files) | Sort-Object) -join ',') 'done.txt,new.test.txt'
    Check '... as the newest stash' $again.stash.sha (Get-Sha $d.hub refs/stash)
    Check '... with the edit in it' ("$(git -C $wt.path show "$($again.stash.sha):done.txt")" -match 'half-finished') $true
    Check '... and the untracked file' "$(git -C $wt.path show "$($again.stash.sha)^3:new.test.txt")".Trim() 'untracked'
    Check '... named for the branch' ("$(git -C $wt.path stash list)" -match 'left uncommitted on t-1, set aside before a new phase') $true
    $clean = New-TicketWorktree -Repo $d -Branch 't-1' -MergeLocally $false -Skill 'implement'
    Check 'a clean worktree: nothing set aside' "$($clean.ok) $([bool]$clean.stash)" 'True False'

    # A worktree a person made elsewhere is theirs: refused, as it always was, and left alone.
    $o = New-Hub 'dirty-outside'
    $mine = Join-Path $root 'dirty-outside\mine'
    git -C $o.hub branch t-2 *> $null
    git -C $o.hub worktree add $mine t-2 *> $null
    Add-Content -Path (Join-Path $mine 'README.md') -Value 'mine, mid-edit' -Encoding ascii
    $theirs = New-TicketWorktree -Repo $o -Branch 't-2' -MergeLocally $false -Skill 'implement'
    Check "a dirty worktree outside the root: refused" "$($theirs.ok) $($theirs.reason -match 'outside this project''s worktree root')" 'False True'
    Check '... and its edits left where they are' ("$(git -C $mine status --porcelain)" -match 'README.md') $true

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

    # ---- Speculate installs beside its agent -------------------------------------
    Write-Host "== speculate's setup runs in the background"
    function Resolve-Bg {
        param($Repo, [string]$Id, [string]$Skill, [string[]]$Setup, [switch]$Background)
        return Resolve-Worktree -RepoDir $Repo.hub -WorktreeRoot $Repo.wt -Skill $Skill -TicketId $Id `
            -RecordedBranch "t-$Id" -IssueBranchToken '' -BaseBranch 'main' -BranchPrefix '' `
            -CopyFiles @() -Setup $Setup -ReviewBranch '' -MergeLocally $true -BackgroundSetup:$Background
    }
    $bg = New-Hub 'setup-bg'
    $res = Resolve-Bg $bg '9' 'speculate' @('ping -n 8 127.0.0.1 >nul', 'echo done> installed.txt') -Background
    Check 'the worktree is handed back before the install ends' ($res.setupJob -and -not $res.setupJob.process.HasExited) $true
    Check '... with the install to wait for' ([bool]$res.setupJob) $true
    $bgMarker = Get-SetupFailedMarker -Path $res.path
    Check '... and the marker up while it runs, naming its process' ((Get-Content $bgMarker -Raw) -match 'background pid=\d+') $true
    Check 'waiting for it succeeds' (Wait-BackgroundSetup -Job $res.setupJob) $true
    Check '... every command ran, in order' (Test-Path (Join-Path $res.path 'installed.txt')) $true
    Check '... and the marker is gone' (Test-Path $bgMarker) $false
    $again = Resolve-Bg $bg '9' 'implement' @('exit /b 5')
    Check 'the next phase finds it set up and runs nothing' "$($again.ok)" 'True'

    $bf = New-Hub 'setup-bg-fail'
    $res = Resolve-Bg $bf '10' 'speculate' @('exit /b 6', 'echo never> second.txt') -Background
    Check 'a failed background install reports false' (Wait-BackgroundSetup -Job $res.setupJob) $false
    Check '... stops at the failing command' (Test-Path (Join-Path $res.path 'second.txt')) $false
    Check '... and leaves the marker for the next phase' (Test-Path (Get-SetupFailedMarker -Path $res.path)) $true
    $retry = Resolve-Bg $bf '10' 'implement' @('echo ok> retried.txt')
    Check 'the next phase sets it up again, in the foreground' "$($retry.ok) $(Test-Path (Join-Path $retry.path 'retried.txt'))" 'True True'

    $bo = New-Hub 'setup-bg-orphan'
    $null = Resolve-Bg $bo '11' 'speculate' @('ping -n 5 127.0.0.1 >nul', 'echo first> orphan.txt') -Background
    # Its run exits without waiting, and the next phase is claimed at once.
    $next = Resolve-Bg $bo '11' 'implement' @('if not exist orphan.txt exit /b 8')
    Check 'an install left running is waited for before setting up again' "$($next.ok)" 'True'

    $bn = New-Hub 'setup-bg-none'
    $none = Resolve-Bg $bn '12' 'speculate' @() -Background
    Check 'nothing to install, nothing to wait for' "$($none.ok) $([bool]$none.setupJob)" 'True False'

    # ---- The skill rides in the prompt --------------------------------------------
    Write-Host "== the skill's text"
    $skillPath = Join-Path $root 'skill.md'
    [IO.File]::WriteAllText($skillPath, "# Skill`n`nUse <base-ref> - not {{TICKET_ID}} " + [char]0x2014 + " ever.`n", (New-Object System.Text.UTF8Encoding($false)))
    $text = Get-SkillText -Path $skillPath
    Check 'read as UTF-8, dashes intact' ($text.Contains([string][char]0x2014)) $true
    Check '... trimmed' ($text.EndsWith('ever.')) $true
    Check 'a missing skill says to open the file' ((Get-SkillText -Path (Join-Path $root 'nope.md')) -match 'could not be included.*nope\.md') $true
    $template = Get-Content -Path (Join-Path $PSScriptRoot 'heartbeat-prompt.md') -Raw
    Check 'the template ends with the skill' ($template.TrimEnd().EndsWith('{{SKILL_TEXT}}')) $true
    Check '... and the worktree note sits on the working-directory line' ($template -match 'git switch` here\.\{\{WORKTREE_NOTE\}\}\r?\n') $true
}
finally {
    # Worktree .git files are read-only on Windows; -Force is what removes them.
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
