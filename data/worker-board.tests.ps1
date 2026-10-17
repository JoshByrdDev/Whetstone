# Exercises data/worker-board.ps1 - the branch round trip for a worker on a worker host - against
# real throwaway repositories: a board host repo (non-bare, with a bare origin beside it, as a
# project checkout is) and a worker host clone of it whose remote is named 'board'.
#
# The parts of worker.ps1 the round trip has to agree with (Resolve-BaseRef, Resolve-Worktree,
# Update-BranchFromBase) are lifted out of its syntax tree as worker-base.tests.ps1 does, so the
# last section runs a ticket board -> worker host -> board -> worker host through the real code.
#
# What the board host's accept route does is simulated here with update-ref; the route itself is
# the API's and is tested there.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-board.tests.ps1
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
. (Join-Path $PSScriptRoot 'worker-board.ps1')

$env:GIT_TERMINAL_PROMPT = '0'
$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-board-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

function Set-Identity {
    param([string]$Dir)
    git -C $Dir config user.email 'test@example.com'
    git -C $Dir config user.name 'Test'
    git -C $Dir config commit.gpgsign false
}

function Add-Commit {
    param([string]$Dir, [string]$File, [string]$Subject)
    Add-Content -Path (Join-Path $Dir $File) -Value $Subject -Encoding ascii
    git -C $Dir add -A *> $null
    git -C $Dir commit -m $Subject *> $null
}

function Get-Sha { param([string]$Dir, [string]$Ref) return "$(git -C $Dir rev-parse --verify --quiet $Ref 2>$null)".Trim() }

# A board host repo with one pushed commit on main and a bare origin beside it, and a worker host
# clone of it: remote 'board', main checked out, as a fresh `git clone -o board` leaves it.
function New-Pair {
    param([string]$Name)
    $dir = Join-Path $root $Name
    $board = Join-Path $dir 'board'
    New-Item -ItemType Directory -Path $board -Force | Out-Null
    git -C $board init -b main *> $null
    Set-Identity $board
    Add-Commit -Dir $board -File 'README.md' -Subject 'init'
    $origin = Join-Path $dir 'origin.git'
    git init --bare -b main $origin *> $null
    git -C $board remote add origin $origin *> $null
    git -C $board push -u origin main *> $null

    $clone = Join-Path $dir 'clone'
    git clone --quiet -o board $board $clone *> $null
    Set-Identity $clone
    return @{ dir = $dir; board = $board; clone = $clone; boardWt = (Join-Path $dir 'board-wt'); cloneWt = (Join-Path $dir 'clone-wt') }
}

# The board host working a ticket in a worktree of its own, as its workers do.
function New-BoardTicket {
    param($Pair, [string]$Branch)
    $path = Join-Path $Pair.boardWt $Branch
    git -C $Pair.board worktree add --quiet -b $Branch $path main *> $null
    Set-Identity $path
    return $path
}

# A worker host worktree for a branch that already exists in the clone.
function New-CloneWorktree {
    param($Pair, [string]$Branch)
    $path = Join-Path $Pair.cloneWt $Branch
    git -C $Pair.clone worktree add --quiet $path $Branch *> $null
    Set-Identity $path
    return $path
}

function Sync-Branch {
    param($Pair, [string]$Branch)
    $got = Get-BoardBranch -RepoDir $Pair.clone -Branch $Branch
    if (-not $got.ok) { return $got }
    return Update-BranchFromBoard -RepoDir $Pair.clone -Branch $Branch -BoardSha $got.sha
}

try {
    # ---- Sync-BoardRefs -------------------------------------------------------
    Write-Host "== Sync-BoardRefs"
    $p = New-Pair 'sync'
    $r = Sync-BoardRefs -RepoDir $p.clone -BaseBranch main
    Check 'fresh clone: synced' $r.ok $true
    Check 'fresh clone: main checkout detached off the base' "$(git -C $p.clone symbolic-ref --quiet HEAD 2>$null)" ''
    Check 'fresh clone: base mirrors the board host' (Get-Sha $p.clone refs/heads/main) (Get-Sha $p.board main)

    # The case the mirror exists for: a ticket merged on the board host and never pushed.
    Add-Commit -Dir $p.board -File 'merged.txt' -Subject 'a ticket merged locally, never pushed'
    $r = Sync-BoardRefs -RepoDir $p.clone -BaseBranch main
    Check 'unpushed merge: synced' $r.ok $true
    Check 'unpushed merge: local base mirrors the board host' (Get-Sha $p.clone refs/heads/main) (Get-Sha $p.board main)
    Check "unpushed merge: origin/main mirrors the board host's" (Get-Sha $p.clone refs/remotes/origin/main) (Get-Sha $p.board origin/main)
    Check 'merge project: Resolve-BaseRef agrees with the board host' `
        (Resolve-BaseRef -RepoDir $p.clone -BaseBranch main -MergeLocally $true) (Resolve-BaseRef -RepoDir $p.board -BaseBranch main -MergeLocally $true)
    Check 'PR project: Resolve-BaseRef agrees with the board host' `
        (Resolve-BaseRef -RepoDir $p.clone -BaseBranch main -MergeLocally $false) (Resolve-BaseRef -RepoDir $p.board -BaseBranch main -MergeLocally $false)

    git -C $p.board push origin main *> $null
    git -C $p.board reset --quiet --hard HEAD~1 *> $null
    $r = Sync-BoardRefs -RepoDir $p.clone -BaseBranch main
    Check 'board base wound back: mirror follows it' (Get-Sha $p.clone refs/heads/main) (Get-Sha $p.board main)
    Check 'board base wound back: Resolve-BaseRef still agrees' `
        (Resolve-BaseRef -RepoDir $p.clone -BaseBranch main -MergeLocally $true) (Resolve-BaseRef -RepoDir $p.board -BaseBranch main -MergeLocally $true)

    $r = Sync-BoardRefs -RepoDir $p.clone -BaseBranch nope
    Check 'no such base on the board host: refused' $r.ok $false

    $held = New-Pair 'sync-held'
    git -C $held.clone checkout --quiet --detach *> $null
    git -C $held.clone worktree add --quiet (Join-Path $held.cloneWt 'base') main *> $null
    $r = Sync-BoardRefs -RepoDir $held.clone -BaseBranch main
    Check 'base checked out in a clone worktree: refused' $r.ok $false

    $nr = New-Pair 'sync-noremote'
    git -C $nr.clone remote rename board elsewhere *> $null
    $r = Sync-BoardRefs -RepoDir $nr.clone -BaseBranch main
    Check "no 'board' remote: refused" $r.ok $false

    # ---- Get-BoardBranch ------------------------------------------------------
    Write-Host "== Get-BoardBranch"
    $g = New-Pair 'get'
    $r = Get-BoardBranch -RepoDir $g.clone -Branch 't-1'
    Check 'absent on the board host: ok, no sha' "$($r.ok) [$($r.sha)]" 'True []'

    $bwt = New-BoardTicket -Pair $g -Branch 't-1'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T1: board host work'
    $r = Get-BoardBranch -RepoDir $g.clone -Branch 't-1'
    Check "present: the board host's commit" $r.sha (Get-Sha $g.board t-1)
    Check 'present: tracked under board/' (Get-Sha $g.clone refs/remotes/board/t-1) (Get-Sha $g.board t-1)

    git -C $g.board worktree remove --force $bwt *> $null
    git -C $g.board branch -D t-1 *> $null
    $r = Get-BoardBranch -RepoDir $g.clone -Branch 't-1'
    Check 'deleted on the board host: absent again' "$($r.ok) [$($r.sha)]" 'True []'
    Check 'deleted on the board host: stale tracking ref removed' (Get-Sha $g.clone refs/remotes/board/t-1) ''

    git -C $g.clone remote set-url board (Join-Path $g.dir 'not-there') *> $null
    $r = Get-BoardBranch -RepoDir $g.clone -Branch 't-1'
    Check 'board host unreachable: refused, not read as absent' $r.ok $false

    # ---- Update-BranchFromBoard -----------------------------------------------
    Write-Host "== Update-BranchFromBoard"
    $u = New-Pair 'update'
    $null = Sync-BoardRefs -RepoDir $u.clone -BaseBranch main
    Check 'nothing anywhere: new' (Sync-Branch -Pair $u -Branch 't-1').state 'new'

    $bwt = New-BoardTicket -Pair $u -Branch 't-1'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T1: speculated on the board host'
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'board only: created' "$($r.ok) $($r.state)" 'True created'
    Check "created: at the board host's commit" (Get-Sha $u.clone t-1) (Get-Sha $u.board t-1)
    Check 'created: agreed' (Get-Sha $u.clone (Get-AgreedRef 't-1')) (Get-Sha $u.board t-1)
    Check 'same again: current' (Sync-Branch -Pair $u -Branch 't-1').state 'current'

    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T1: more on the board host'
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'behind, no worktree: fast-forwarded' "$($r.ok) $($r.state)" 'True behind'
    Check 'behind, no worktree: at the board host' (Get-Sha $u.clone t-1) (Get-Sha $u.board t-1)

    $cwt = New-CloneWorktree -Pair $u -Branch 't-1'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T1: and more'
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'behind, in a worktree: fast-forwarded' "$($r.ok) $($r.state)" 'True behind'
    Check 'behind, in a worktree: worktree moved with it' (Get-Sha $cwt HEAD) (Get-Sha $u.board t-1)
    Check 'behind, in a worktree: files arrived' ((Get-Content (Join-Path $cwt 'f.txt')) -join '|') 'T1: speculated on the board host|T1: more on the board host|T1: and more'

    Add-Commit -Dir $bwt -File 'g.txt' -Subject 'T1: one more'
    Add-Content -Path (Join-Path $cwt 'f.txt') -Value 'uncommitted' -Encoding ascii
    $before = Get-Sha $u.clone t-1
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'dirty worktree: refused' "$($r.ok) $($r.state)" 'False behind'
    Check 'dirty worktree: branch untouched' (Get-Sha $u.clone t-1) $before
    git -C $cwt checkout --quiet -- . *> $null
    $null = Sync-Branch -Pair $u -Branch 't-1'

    Add-Commit -Dir $cwt -File 'h.txt' -Subject 'T1: here, hand-back refused'
    $mine = Get-Sha $u.clone t-1
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'ahead: left alone' "$($r.ok) $($r.state)" 'True ahead'
    Check 'ahead: commit kept' (Get-Sha $u.clone t-1) $mine

    # Diverged with a commit the board host never had: those commits exist nowhere else.
    Add-Commit -Dir $bwt -File 'i.txt' -Subject 'T1: the board host moved on meanwhile'
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'diverged with unhanded commits: refused' "$($r.ok) $($r.state)" 'False diverged'
    Check 'diverged with unhanded commits: kept' (Get-Sha $u.clone t-1) $mine

    # Diverged because the board host rebased what it already had: safe to take theirs.
    $d = New-Pair 'diverged'
    $null = Sync-BoardRefs -RepoDir $d.clone -BaseBranch main
    $bwt = New-BoardTicket -Pair $d -Branch 't-2'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T2: work'
    $null = Sync-Branch -Pair $d -Branch 't-2'
    $cwt = New-CloneWorktree -Pair $d -Branch 't-2'
    Add-Commit -Dir $d.board -File 'landed.txt' -Subject 'merged on the board host'
    git -C $bwt rebase --quiet main *> $null
    $r = Sync-Branch -Pair $d -Branch 't-2'
    Check 'board host rebased: reset to theirs' "$($r.ok) $($r.state)" 'True reset'
    Check 'board host rebased: worktree at the rebased commit' (Get-Sha $cwt HEAD) (Get-Sha $d.board t-2)
    Check 'board host rebased: their files are here' (Test-Path (Join-Path $cwt 'landed.txt')) $true

    # ---- Send-BranchToBoard ---------------------------------------------------
    Write-Host "== Send-BranchToBoard"
    $s = New-Pair 'send'
    $null = Sync-BoardRefs -RepoDir $s.clone -BaseBranch main
    $bwt = New-BoardTicket -Pair $s -Branch 't-3'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T3: board host'
    $null = Sync-Branch -Pair $s -Branch 't-3'
    $cwt = New-CloneWorktree -Pair $s -Branch 't-3'
    Add-Commit -Dir $cwt -File 'f.txt' -Subject 'T3: worker host'

    $boardTip = Get-Sha $s.board t-3
    git -C $cwt push --quiet board HEAD:refs/heads/t-3 *> $null
    Check 'the reason: a push straight to a branch checked out on the board host is refused' $LASTEXITCODE 1

    $r = Send-BranchToBoard -WorkDir $cwt -RunId 'run42'
    Check 'branch checked out on the board host: pushed anyway' $r.ok $true
    Check "incoming ref holds the worker host's commit" (Get-Sha $s.board refs/whetstone/incoming/run42) (Get-Sha $cwt HEAD)
    Check "board host's branch not touched by the push" (Get-Sha $s.board t-3) $boardTip
    Check 'bad run id: refused' (Send-BranchToBoard -WorkDir $cwt -RunId '../x').ok $false

    # ---- A ticket going round ---------------------------------------------------
    Write-Host "== round trip: worker host -> board host -> worker host"
    $t = New-Pair 'trip'
    Add-Commit -Dir $t.board -File 'earlier.txt' -Subject 'an earlier ticket, merged locally and never pushed'

    # Run 1, on the worker host: a new ticket.
    $null = Sync-BoardRefs -RepoDir $t.clone -BaseBranch main
    Check 'run 1: new ticket' (Sync-Branch -Pair $t -Branch 't-9').state 'new'
    $wt = Resolve-Worktree -RepoDir $t.clone -WorktreeRoot $t.cloneWt -Skill 'speculate' -TicketId '9' `
        -RecordedBranch 't-9' -IssueBranchToken '' -BaseBranch 'main' -BranchPrefix '' `
        -CopyFiles @() -Setup @() -ReviewBranch '' -MergeLocally $true
    Check 'run 1: worktree created' $wt.ok $true
    Check "run 1: forked from the board host's local main" (Get-Sha $wt.path HEAD) (Get-Sha $t.board main)
    Check 'run 1: the unpushed merge is in the worktree' (Test-Path (Join-Path $wt.path 'earlier.txt')) $true
    Set-Identity $wt.path
    Add-Commit -Dir $wt.path -File 'feature.txt' -Subject 'T9: the feature'

    # The hand-off rebase sees what landed on the board host during the run.
    Add-Commit -Dir $t.board -File 'during.txt' -Subject 'merged on the board host during run 1'
    $null = Sync-BoardRefs -RepoDir $t.clone -BaseBranch main
    $rebase = Update-BranchFromBase -WorkDir $wt.path -BaseBranch 'main' -RepoDir $t.clone -MergeLocally $true
    Check 'run 1: hand-off rebase onto the mirrored base' "$($rebase.rebased) $($rebase.base)" 'True main'
    Check "run 1: now directly on the board host's main" (Get-Sha $wt.path 'HEAD~1') (Get-Sha $t.board main)

    $sent = Send-BranchToBoard -WorkDir $wt.path -RunId 'run1'
    Check 'run 1: handed back' $sent.ok $true
    git -C $t.board update-ref refs/heads/t-9 $sent.sha *> $null      # the accept route
    git -C $t.board update-ref -d $sent.ref *> $null
    Check 'run 1: recorded as agreed' (Set-BranchHandedBack -RepoDir $t.clone -Branch 't-9' -Sha $sent.sha) $true

    # Run 2, on the board host: QA commits a fix.
    $bwt = Join-Path $t.boardWt 't-9'
    git -C $t.board worktree add --quiet $bwt t-9 *> $null
    Set-Identity $bwt
    Add-Commit -Dir $bwt -File 'feature.txt' -Subject 'T9: QA fix on the board host'

    # Run 3, on the worker host again.
    $null = Sync-BoardRefs -RepoDir $t.clone -BaseBranch main
    $r = Sync-Branch -Pair $t -Branch 't-9'
    Check 'run 3: picks up the board host run' "$($r.ok) $($r.state)" 'True behind'
    Check "run 3: worktree at the board host's commit" (Get-Sha $wt.path HEAD) (Get-Sha $t.board t-9)

    # Between runs the board rebases the ticket onto newer work.
    Add-Commit -Dir $t.board -File 'later.txt' -Subject 'merged on the board host after run 3'
    git -C $bwt rebase --quiet main *> $null
    $r = Sync-Branch -Pair $t -Branch 't-9'
    Check 'run 4: board host rebase taken over an agreed tip' "$($r.ok) $($r.state)" 'True reset'
    Check 'run 4: worktree has the newer work' (Test-Path (Join-Path $wt.path 'later.txt')) $true
}
finally {
    # Worktree .git files are read-only on Windows; -Force is what removes them.
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
