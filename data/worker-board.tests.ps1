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
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-board.tests.ps1
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

    # A detached worktree git still lists after its folder went (a killed run's): the base lookup
    # asks it for a stopped rebase, and that used to throw the whole sync away.
    $gone = New-Pair 'sync-prunable'
    $goneWt = Join-Path $gone.cloneWt 'gone'
    git -C $gone.clone worktree add --quiet --detach $goneWt *> $null
    Remove-Item -Recurse -Force -LiteralPath $goneWt
    $r = Sync-BoardRefs -RepoDir $gone.clone -BaseBranch main
    Check 'a prunable detached worktree in the clone: synced' ($null -ne $r -and $r.ok) $true
    Check '... and the base mirrored' (Get-Sha $gone.clone 'refs/heads/main') (Get-Sha $gone.board 'refs/heads/main')

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

    # Leftovers of a run killed before its hand-off nets: set aside, not a block on every retry.
    Add-Commit -Dir $bwt -File 'g.txt' -Subject 'T1: one more'
    Add-Content -Path (Join-Path $cwt 'f.txt') -Value 'uncommitted' -Encoding ascii
    Set-Content -Path (Join-Path $cwt 'stray.txt') -Value 'untracked' -Encoding ascii
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'dirty worktree, behind: brought level' "$($r.ok) $($r.state)" 'True behind'
    Check 'dirty worktree, behind: worktree at the board host' (Get-Sha $cwt HEAD) (Get-Sha $u.board t-1)
    Check 'dirty worktree, behind: worktree clean' "$(git -C $cwt status --porcelain)" ''
    Check 'dirty worktree, behind: says what it set aside' ((@($r.stash.files) | Sort-Object) -join ',') 'f.txt,stray.txt'
    Check 'dirty worktree, behind: the stash it names is the newest' $r.stash.sha (Get-Sha $u.clone refs/stash)
    Check 'dirty worktree, behind: the edit is in the stash' `
        ("$(git -C $cwt show "$($r.stash.sha):f.txt")" -match 'uncommitted') $true
    Check 'dirty worktree, behind: so is the untracked file' "$(git -C $cwt show "$($r.stash.sha)^3:stray.txt")".Trim() 'untracked'
    Check 'dirty worktree, behind: stash named for the branch' ("$(git -C $cwt stash list)" -match 'left uncommitted on t-1') $true

    Add-Content -Path (Join-Path $cwt 'f.txt') -Value 'again' -Encoding ascii
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'dirty worktree, current: still set aside' "$($r.ok) $($r.state) $(@($r.stash.files) -join ',')" 'True current f.txt'
    Check 'dirty worktree, current: clean, so Resolve-Worktree will start' "$(git -C $cwt status --porcelain)" ''
    Check 'clean worktree: nothing set aside' ((Sync-Branch -Pair $u -Branch 't-1').stash) $null

    Add-Commit -Dir $cwt -File 'h.txt' -Subject 'T1: here, hand-back refused'
    $mine = Get-Sha $u.clone t-1
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'ahead: left alone' "$($r.ok) $($r.state)" 'True ahead'
    Check 'ahead: commit kept' (Get-Sha $u.clone t-1) $mine

    # Diverged with a commit the board host never had. Copying it to the board host fails: those
    # commits exist nowhere else, so nothing moves.
    Add-Commit -Dir $bwt -File 'i.txt' -Subject 'T1: the board host moved on meanwhile'
    $boardUrl = "$(git -C $u.clone remote get-url board)".Trim()
    git -C $u.clone remote set-url --push board (Join-Path $u.dir 'not-there') *> $null
    Add-Content -Path (Join-Path $cwt 'h.txt') -Value 'uncommitted' -Encoding ascii
    $stashes = @(git -C $cwt stash list).Count
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'diverged, board copy fails: refused' "$($r.ok) $($r.state)" 'False diverged'
    Check 'diverged, board copy fails: branch kept' (Get-Sha $u.clone t-1) $mine
    Check 'diverged, board copy fails: nothing stashed' @(git -C $cwt stash list).Count $stashes
    Check 'diverged, board copy fails: edits left where they are' ("$(git -C $cwt status --porcelain)" -match 'h.txt') $true
    git -C $cwt checkout --quiet -- . *> $null
    git -C $u.clone remote set-url --push board $boardUrl *> $null

    # The same with the board host reachable: kept on both machines, then reset - no longer a block.
    $r = Sync-Branch -Pair $u -Branch 't-1'
    Check 'diverged with unhanded commits: reset' "$($r.ok) $($r.state)" 'True reset'
    Check "diverged with unhanded commits: the branch is the board host's" (Get-Sha $u.clone t-1) (Get-Sha $u.board t-1)
    Check 'diverged with unhanded commits: worktree moved with it' (Get-Sha $cwt HEAD) (Get-Sha $u.board t-1)
    Check 'diverged with unhanded commits: not equivalent' "$($r.diverged.equivalent) $($r.diverged.onBoard)" 'False True'
    Check 'diverged with unhanded commits: kept here' (Get-Sha $u.clone $r.diverged.ref) $mine
    Check 'diverged with unhanded commits: kept on the board host' (Get-Sha $u.board $r.diverged.ref) $mine
    Check 'diverged with unhanded commits: names the commit' ("$($r.diverged.commits)" -match 'hand-back refused') $true
    Check 'diverged with unhanded commits: the comment names the ref' `
        ((Format-DivergedCommits -Diverged $r.diverged -Branch 't-1' -BoardSha (Get-Sha $u.board t-1)) -match [regex]::Escape($r.diverged.ref)) $true
    Check 'diverged with unhanded commits: agreed now' (Get-Sha $u.clone (Get-AgreedRef 't-1')) (Get-Sha $u.board t-1)
    Check 'diverged with unhanded commits: next sync is current' (Sync-Branch -Pair $u -Branch 't-1').state 'current'

    # The same work under other commit ids: a hand-back the board host refused (its lease had gone),
    # whose commits a later run there cherry-picked onto the branch. Nothing is only here.
    Write-Host "== a refused hand-back redone on the board host"
    $e = New-Pair 'equivalent'
    $null = Sync-BoardRefs -RepoDir $e.clone -BaseBranch main
    $bwt = New-BoardTicket -Pair $e -Branch 't-3'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T3: the base of the ticket'
    $null = Sync-Branch -Pair $e -Branch 't-3'
    $cwt = New-CloneWorktree -Pair $e -Branch 't-3'
    Add-Commit -Dir $cwt -File 'g.txt' -Subject 'T3: built on the worker host'
    Add-Commit -Dir $cwt -File 'h.txt' -Subject 'T3: and its tests'
    $sent = Send-BranchToBoard -WorkDir $cwt -RunId 'run33'      # refused: never accepted, never agreed
    $workerTip = Get-Sha $e.clone t-3
    git -C $bwt cherry-pick --quiet "$(Get-Sha $bwt HEAD)..$($sent.sha)" *> $null
    Check 'setup: the board host has the same work under new ids' `
        "$((Get-Sha $e.board t-3) -ne $workerTip) $((Get-Sha $e.board 't-3^{tree}') -eq (Get-Sha $e.clone 't-3^{tree}'))" 'True True'
    $r = Sync-Branch -Pair $e -Branch 't-3'
    Check 'redone on the board host: reset, not refused' "$($r.ok) $($r.state)" 'True reset'
    Check "redone on the board host: the branch is the board host's" (Get-Sha $e.clone t-3) (Get-Sha $e.board t-3)
    Check 'redone on the board host: read as equivalent' "$($r.diverged.equivalent) $($r.diverged.onBoard) $(@($r.diverged.commits).Count)" 'True False 0'
    Check 'redone on the board host: old tip kept here' (Get-Sha $e.clone $r.diverged.ref) $workerTip
    Check 'redone on the board host: comment says nothing was lost' `
        ((Format-DivergedCommits -Diverged $r.diverged -Branch 't-3' -BoardSha (Get-Sha $e.board t-3)) -match 'nothing was lost') $true

    # Squashed instead of cherry-picked: no patch matches, but the files are the same.
    $q = New-Pair 'squashed'
    $null = Sync-BoardRefs -RepoDir $q.clone -BaseBranch main
    $bwt = New-BoardTicket -Pair $q -Branch 't-4'
    Add-Commit -Dir $bwt -File 'f.txt' -Subject 'T4: the base of the ticket'
    $null = Sync-Branch -Pair $q -Branch 't-4'
    $cwt = New-CloneWorktree -Pair $q -Branch 't-4'
    Add-Commit -Dir $cwt -File 'g.txt' -Subject 'T4: one'
    Add-Commit -Dir $cwt -File 'g.txt' -Subject 'T4: two'
    $sent = Send-BranchToBoard -WorkDir $cwt -RunId 'run44'
    git -C $bwt merge --quiet --squash $sent.sha *> $null
    git -C $bwt commit --quiet -m 'T4: one and two, squashed' *> $null
    $r = Sync-Branch -Pair $q -Branch 't-4'
    Check 'squashed on the board host: reset as equivalent' "$($r.ok) $($r.state) $($r.diverged.equivalent)" 'True reset True'

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

    # ---- A git operation a run left stopped -------------------------------------
    Write-Host "== a rebase left stopped"
    # The run ends with its rebase stopped on a conflict: the leftover net keeps it and aborts it,
    # rather than committing onto the detached HEAD (which moves no branch).
    $x = New-Pair 'stopped'
    $null = Sync-BoardRefs -RepoDir $x.clone -BaseBranch main
    $bwt = New-BoardTicket -Pair $x -Branch 't-5'
    Add-Commit -Dir $bwt -File 'README.md' -Subject 'T5: the ticket'
    $null = Sync-Branch -Pair $x -Branch 't-5'
    $cwt = New-CloneWorktree -Pair $x -Branch 't-5'
    $before = Get-Sha $x.clone t-5
    Add-Commit -Dir $x.board -File 'README.md' -Subject 'landed on the base meanwhile'
    $null = Sync-BoardRefs -RepoDir $x.clone -BaseBranch main
    git -C $cwt rebase main *> $null
    Check 'setup: the rebase stopped' (Get-StoppedGitOperation -Dir $cwt).kind 'rebase'
    Check 'setup: git lists the worktree as detached' ((@(git -C $x.clone worktree list --porcelain) -contains 'branch refs/heads/t-5')) $false
    Check 'mid-rebase: still the worktree of its branch' ([bool](Get-BranchWorktree -RepoDir $x.clone -Branch 't-5')) $true
    Check 'mid-rebase: and in the worktree map' ([bool]((Get-WorktreeMap -RepoDir $x.clone)['t-5'])) $true
    Set-Content -Path (Join-Path $cwt 'resolving.txt') -Value 'made while resolving' -Encoding ascii

    $r = Save-WorkerLeftovers -WorkDir $cwt -TicketId '5' -Title 'T5' -IssueRef '' -Skill 'implement'
    Check 'run ends mid-rebase: said as stopped' "$($r.ok) $($r.stopped.kind) $($r.stopped.branch)" 'True rebase t-5'
    Check 'run ends mid-rebase: nothing committed, the branch is where it was' (Get-Sha $x.clone t-5) $before
    Check 'run ends mid-rebase: worktree back on the branch' "$(git -C $cwt symbolic-ref --quiet --short HEAD)" 't-5'
    Check 'run ends mid-rebase: no rebase left' (Get-StoppedGitOperation -Dir $cwt) $null
    Check 'run ends mid-rebase: worktree clean' "$(git -C $cwt status --porcelain)" ''
    Check 'run ends mid-rebase: kept on the HEAD it stood on' (Get-Sha $cwt "$($r.stopped.ref)~1") (Get-Sha $x.clone main)
    Check 'run ends mid-rebase: kept the conflicted file, markers and all' `
        (((git -C $cwt show "$($r.stopped.ref):README.md") -join "`n") -match '<<<<<<<') $true
    Check 'run ends mid-rebase: kept the untracked file' "$(git -C $cwt show "$($r.stopped.ref):resolving.txt")".Trim() 'made while resolving'
    Check 'run ends mid-rebase: the comment names the ref' ((Format-StoppedGitOperation -Stopped $r.stopped) -match [regex]::Escape($r.stopped.ref)) $true
    Check 'clean tree, nothing stopped: still nothing to do' (Save-WorkerLeftovers -WorkDir $cwt -TicketId '5' -Title 'T5' -IssueRef '' -Skill 'implement') $null

    # The branch moved on while a rebase sat stopped - a worker host's hand-back accepted here. An
    # abort would wind it back to where the rebase began, past everything since: it must stay put.
    Write-Host "== a stopped rebase on a branch that moved since"
    $mv = New-Pair 'stopped-moved'
    $null = Sync-BoardRefs -RepoDir $mv.clone -BaseBranch main
    $mbt = New-BoardTicket -Pair $mv -Branch 't-7'
    Add-Commit -Dir $mbt -File 'README.md' -Subject 'T7: the ticket'
    $null = Sync-Branch -Pair $mv -Branch 't-7'
    $mwt = New-CloneWorktree -Pair $mv -Branch 't-7'
    Add-Commit -Dir $mv.board -File 'README.md' -Subject 'landed on the base meanwhile'
    $null = Sync-BoardRefs -RepoDir $mv.clone -BaseBranch main
    git -C $mwt rebase main *> $null
    Check 'setup: stopped' (Get-StoppedGitOperation -Dir $mwt).kind 'rebase'
    $began = Get-Sha $mv.clone t-7
    $moved = "$(git -C $mv.clone commit-tree -p $began -m 'handed back meanwhile' "$(Get-Sha $mv.clone 't-7^{tree}')")".Trim()
    git -C $mv.clone update-ref refs/heads/t-7 $moved *> $null
    $mr = Undo-StoppedGitOperation -Dir $mwt
    Check 'moved branch: cleared' "$($mr.ok) $($mr.kind) $($mr.moved)" 'True rebase True'
    Check 'moved branch: not rewound' (Get-Sha $mv.clone t-7) $moved
    Check 'moved branch: worktree on the branch as it stands' "$(git -C $mwt symbolic-ref --quiet --short HEAD) $(Get-Sha $mwt HEAD)" "t-7 $moved"
    Check 'moved branch: no rebase left' (Get-StoppedGitOperation -Dir $mwt) $null
    Check 'moved branch: worktree clean' "$(git -C $mwt status --porcelain)" ''
    Check 'moved branch: what it had got to is kept' (((git -C $mwt show "$($mr.ref):README.md") -join "`n") -match '<<<<<<<') $true
    Check 'moved branch: the comment says it was not rewound' ((Format-StoppedGitOperation -Stopped $mr) -match 'without rewinding') $true

    # A HEAD left off the branch is not what is handed back: the branch is.
    git -C $cwt checkout --quiet --detach main *> $null
    $r = Send-BranchToBoard -WorkDir $cwt -RunId 'run55' -Ref 'refs/heads/t-5'
    Check 'detached HEAD: the branch is what is sent' (Get-Sha $x.board refs/whetstone/incoming/run55) $before
    git -C $cwt checkout --quiet t-5 *> $null

    # The incident, as the old leftover net left a worker host: the rebase stopped, the leftovers
    # committed on its detached HEAD, that HEAD handed back and recorded as agreed, the branch here
    # still on the commit the run started from. Every run after it blocked as diverged.
    Write-Host "== a worker host left mid-rebase by the old leftover net"
    $y = New-Pair 'incident'
    $null = Sync-BoardRefs -RepoDir $y.clone -BaseBranch main
    $bwt = New-BoardTicket -Pair $y -Branch 't-6'
    Add-Commit -Dir $bwt -File 'README.md' -Subject 'T6: QA passed this'
    $null = Sync-Branch -Pair $y -Branch 't-6'
    $cwt = New-CloneWorktree -Pair $y -Branch 't-6'
    $qa = Get-Sha $y.clone t-6
    Add-Commit -Dir $y.board -File 'README.md' -Subject 'landed on the base meanwhile'
    $null = Sync-BoardRefs -RepoDir $y.clone -BaseBranch main
    git -C $cwt rebase main *> $null
    Set-Content -Path (Join-Path $cwt 'README.md') -Value 'resolved by hand' -Encoding ascii
    git -C $cwt add -A *> $null
    git -C $cwt commit --quiet -m 'leftovers' *> $null
    $sent = Send-BranchToBoard -WorkDir $cwt -RunId 'run66'
    git -C $bwt reset --quiet --hard $sent.sha *> $null                 # the accept route
    $null = Set-BranchHandedBack -RepoDir $y.clone -Branch 't-6' -Sha $sent.sha
    Check 'setup: the branch here is not what it handed back' ((Get-Sha $y.clone t-6) -eq $sent.sha) $false
    Check 'setup: and the board host has moved past it' ((Get-Sha $y.board t-6) -eq $qa) $false

    $r = Sync-Branch -Pair $y -Branch 't-6'
    Check 'stuck clone: brought level' "$($r.ok) $($r.state)" 'True reset'
    Check 'stuck clone: the stopped rebase was undone, and said' "$($r.stopped.kind) $($r.stopped.branch)" 'rebase t-6'
    Check "stuck clone: the branch is the board host's" (Get-Sha $y.clone t-6) (Get-Sha $y.board t-6)
    Check 'stuck clone: worktree on the branch' "$(git -C $cwt symbolic-ref --quiet --short HEAD)" 't-6'
    Check "stuck clone: worktree at the board host's commit" (Get-Sha $cwt HEAD) (Get-Sha $y.board t-6)
    Check 'stuck clone: no rebase left' (Get-StoppedGitOperation -Dir $cwt) $null
    Check 'stuck clone: agreed again' (Get-Sha $y.clone (Get-AgreedRef 't-6')) (Get-Sha $y.board t-6)
    Check 'stuck clone: the next sync is current' (Sync-Branch -Pair $y -Branch 't-6').state 'current'

    # On the board host the same leftovers meet Resolve-Worktree at the start of the next phase.
    Write-Host "== Resolve-Worktree on a stopped rebase"
    $z = New-Pair 'resolve-stopped'
    $wt = Resolve-Worktree -RepoDir $z.board -WorktreeRoot $z.boardWt -Skill 'speculate' -TicketId '8' `
        -RecordedBranch 't-8' -IssueBranchToken '' -BaseBranch 'main' -BranchPrefix '' `
        -CopyFiles @() -Setup @() -ReviewBranch '' -MergeLocally $true
    Set-Identity $wt.path
    Add-Commit -Dir $wt.path -File 'README.md' -Subject 'T8: the ticket'
    $t8 = Get-Sha $z.board t-8
    Add-Commit -Dir $z.board -File 'README.md' -Subject 'landed on the base meanwhile'
    git -C $wt.path rebase main *> $null
    Check 'setup: stopped' (Get-StoppedGitOperation -Dir $wt.path).kind 'rebase'
    $again = Resolve-Worktree -RepoDir $z.board -WorktreeRoot $z.boardWt -Skill 'implement' -TicketId '8' `
        -RecordedBranch 't-8' -IssueBranchToken '' -BaseBranch 'main' -BranchPrefix '' `
        -CopyFiles @() -Setup @() -ReviewBranch '' -MergeLocally $true
    Check 'next phase: starts, in the same worktree' "$($again.ok) $($again.created)" 'True False'
    Check 'next phase: says what it undid' $again.stopped.kind 'rebase'
    Check 'next phase: on the branch, where it was' "$(git -C $again.path symbolic-ref --quiet --short HEAD) $(Get-Sha $again.path HEAD)" "t-8 $t8"

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

    # ---- A ticket's files on both machines (phase 2) ---------------------------
    Write-Host "== Get-BoardTicketData / Send-QaCaptures"
    $script:Puts = @()
    $script:PutFails = $false
    $script:Zip = $null
    function Invoke-WebRequest {
        param([string]$Uri, [string]$Method, [string]$InFile, [string]$OutFile, [string]$ContentType, [switch]$UseBasicParsing, [int]$TimeoutSec)
        if ($OutFile) { Copy-Item $script:Zip $OutFile; return }
        if ($script:PutFails) { throw "board unreachable" }
        $script:Puts += [pscustomobject]@{ Uri = $Uri; Method = $Method; Bytes = [IO.File]::ReadAllBytes($InFile) }
    }

    $src = Join-Path $root 'zip-src'
    New-Item -ItemType Directory -Path (Join-Path $src 'ticket-files\12') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $src 'issue-attachments\12\from-PROJ-1') -Force | Out-Null
    Set-Content -Path (Join-Path $src 'ticket-files\12\log.txt') -Value 'owner' -Encoding ascii
    Set-Content -Path (Join-Path $src 'issue-attachments\12\from-PROJ-1\mock.png') -Value 'img' -Encoding ascii
    $script:Zip = Join-Path $root 'ticket.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($src, $script:Zip)
    # An entry that tries to leave the data root, as a hostile or broken archive might.
    $z = [IO.Compression.ZipFile]::Open($script:Zip, 'Update')
    $e = $z.CreateEntry('../escaped.txt'); $w = New-Object IO.StreamWriter($e.Open()); $w.Write('x'); $w.Dispose(); $z.Dispose()

    $hostData = Join-Path $root 'host-data'
    $r = Get-BoardTicketData -DashboardUrl 'http://board.test:5338' -TicketId '12' -DataRoot $hostData
    Check 'ticket files: fetched' "$($r.ok) $($r.files)" 'True 2'
    Check "ticket files: the owner's file at the same path here" (Test-Path (Join-Path $hostData 'ticket-files\12\log.txt')) $true
    Check "ticket files: the issue's attachment at the same path here" (Test-Path (Join-Path $hostData 'issue-attachments\12\from-PROJ-1\mock.png')) $true
    Check 'ticket files: nothing lands outside the data root' (Test-Path (Join-Path $root 'escaped.txt')) $false

    $caps = Join-Path $hostData 'qa-captures\12'
    New-Item -ItemType Directory -Path (Join-Path $caps 'sub') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $caps 'one.png'), [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path $caps 'sub\two two.jpg'), [byte[]](4, 5))
    Set-Content -Path (Join-Path $caps 'page.md') -Value 'not a capture' -Encoding ascii
    $seen = @{}
    $n = Send-QaCaptures -DashboardUrl 'http://board.test:5338' -Dir $caps -TicketId '12' -HostName 'bravo-host' -RunId '812' -Seen $seen
    Check 'captures: every image sent, nothing else' $n 2
    Check 'captures: by host, ticket and run' ($script:Puts[0].Uri) 'http://board.test:5338/api/hosts/bravo-host/qa-captures/12/one.png?runId=812'
    Check 'captures: a nested one keeps its path, escaped' ($script:Puts[1].Uri) 'http://board.test:5338/api/hosts/bravo-host/qa-captures/12/sub/two%20two.jpg?runId=812'
    Check 'captures: as PUT, with the bytes' "$($script:Puts[0].Method) $($script:Puts[0].Bytes -join ',')" 'PUT 1,2,3'
    Check 'captures: nothing new, nothing sent' (Send-QaCaptures -DashboardUrl 'http://board.test:5338' -Dir $caps -TicketId '12' -HostName 'bravo-host' -RunId '812' -Seen $seen) 0
    Start-Sleep -Milliseconds 20
    [IO.File]::WriteAllBytes((Join-Path $caps 'one.png'), [byte[]](9, 9, 9, 9))
    Check 'captures: a changed one goes again' (Send-QaCaptures -DashboardUrl 'http://board.test:5338' -Dir $caps -TicketId '12' -HostName 'bravo-host' -RunId '812' -Seen $seen) 1

    $script:PutFails = $true
    $fresh = @{}
    Check 'captures: a failed upload is not counted' (Send-QaCaptures -DashboardUrl 'http://board.test:5338' -Dir $caps -TicketId '12' -HostName 'bravo-host' -RunId '812' -Seen $fresh) 0
    Check '... and is tried again next pass' $fresh.Count 0
    $script:PutFails = $false
    $script:Puts = @()
    Check 'mirror stop: one last full pass' (Stop-QaCaptureMirror -Job $null -DashboardUrl 'http://board.test:5338' -Dir $caps -TicketId '12' -HostName 'bravo-host' -RunId '812') 2
    Check 'captures: no directory, nothing to do' (Send-QaCaptures -DashboardUrl 'http://board.test:5338' -Dir (Join-Path $root 'none') -TicketId '12' -HostName 'bravo-host' -RunId '812' -Seen @{}) 0
}
# An exception is a failure, not the end of the list: without this, everything after it
# goes unrun and the summary still says every check passed.
catch { $script:fail++; $script:total++; Write-Host "FAIL  stopped by an exception: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    # Worktree .git files are read-only on Windows; -Force is what removes them.
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
