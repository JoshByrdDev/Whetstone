# ============================================================
# The branch round trip for a worker that runs on a worker host - a machine other than the one the
# board runs on. See docs/worker-hosts-plan.md.
#
# The board host's copy of a project repo is the only one that counts: the board diffs, rebases and
# merges there, and the next run of a ticket may land on either machine. So a run on a worker host
# starts by bringing its clone up to what the board host has, and ends by handing its branch back.
# Nothing here pushes to origin, and nothing here is run by the agent - worker.ps1 calls it, outside
# the session, so the guard never sees it.
#
# The worker host's clone names the board host's repo as the remote `board`. Not `hub`: in
# worker.ps1 "the hub" is a project's main checkout, the one its worktrees hang off, and on a
# worker host that is this clone.
#
# Dot-sourced, functions only. Every git call that writes a ref in the clone is taken under the
# same "hub-<repo>" mutex worker.ps1 uses, because the ref namespace is shared by every agent on
# the repo. Invoke-WithMutex comes from heartbeat-common.ps1.
#
# Tests: powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-board.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

$BoardRemote = 'board'

# Where the clone records the last commit of a branch that both machines are known to have had:
# set when the branch is brought up to the board host's, and when the board host accepts a hand-
# back. It is what makes a diverged branch safe to reset - see Update-BranchFromBoard.
function Get-AgreedRef { param([string]$Branch) return "refs/whetstone/agreed/$Branch" }

function Get-RefSha {
    param([string]$Dir, [string]$Ref)
    $sha = "$(git -C $Dir rev-parse --verify --quiet "$Ref^{commit}" 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0) { return '' }
    return $sha
}

# The linked or main worktree that has refs/heads/<Branch> checked out, or $null. A worktree in the
# middle of rebasing the branch counts: git lists it as detached, but the branch is its, and git
# refuses to check the branch out anywhere else until the rebase ends.
function Get-BranchWorktree {
    param([string]$RepoDir, [string]$Branch)
    $path = $null
    foreach ($line in @(git -C $RepoDir worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *') { $path = $line.Substring(9) }
        elseif ($line -eq "branch refs/heads/$Branch") { return $path }
        elseif ($line -eq 'detached' -and $path -and (Get-RebasingBranch -Dir $path) -eq $Branch) { return $path }
    }
    return $null
}

# ---- A git operation a run left stopped ------------------------------------------------------
#
# A run can end with a rebase, merge, cherry-pick or revert stopped on its conflicts: the agent was
# killed, hit a usage limit, or simply handed off mid-way. A stopped rebase is the dangerous one,
# because its HEAD is detached: whatever is committed there - by the leftover net at the end of the
# run, say - moves no branch, so the branch stays where it was while the commit is handed on. On a
# worker host that made the board host's branch, and the clone's record of what was agreed, a
# commit its own branch never reached, and every later run there blocked as a divergence that no
# answer on the ticket could clear. So a stopped operation is never committed over and never
# worked on top of: what it had got to is kept under a ref, and it is aborted, which puts the
# branch and the worktree back where they were before it began.

# The full path of a file under a worktree's git dir (rev-parse --git-path answers relative to it).
function Get-GitDirPath {
    param([string]$Dir, [string]$Name)
    $p = "$(git -C $Dir rev-parse --git-path $Name 2>$null)".Trim()
    if (-not $p) { return '' }
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $Dir $p }
    return $p
}

# The branch a stopped rebase in this worktree is rewriting, or ''.
function Get-RebasingBranch {
    param([string]$Dir)
    foreach ($state in 'rebase-merge', 'rebase-apply') {
        $file = Join-Path (Get-GitDirPath -Dir $Dir -Name $state) 'head-name'
        if (Test-Path -LiteralPath $file) {
            return ("$(Get-Content -LiteralPath $file -TotalCount 1)".Trim() -replace '^refs/heads/', '')
        }
    }
    return ''
}

# @{ kind; branch } for an operation stopped in this worktree, or $null. kind is the git command
# that aborts it (rebase, am, merge, cherry-pick, revert); branch is the one it is on, or rewriting.
function Get-StoppedGitOperation {
    param([string]$Dir)
    foreach ($state in 'rebase-merge', 'rebase-apply') {
        $p = Get-GitDirPath -Dir $Dir -Name $state
        if ($p -and (Test-Path -LiteralPath $p)) {
            $kind = if (Test-Path -LiteralPath (Join-Path $p 'applying')) { 'am' } else { 'rebase' }
            return @{ kind = $kind; branch = (Get-RebasingBranch -Dir $Dir) }
        }
    }
    foreach ($s in @(@{ f = 'MERGE_HEAD'; k = 'merge' }, @{ f = 'CHERRY_PICK_HEAD'; k = 'cherry-pick' }, @{ f = 'REVERT_HEAD'; k = 'revert' })) {
        $p = Get-GitDirPath -Dir $Dir -Name $s.f
        if ($p -and (Test-Path -LiteralPath $p)) {
            return @{ kind = $s.k; branch = "$(git -C $Dir symbolic-ref --quiet --short HEAD 2>$null)".Trim() }
        }
    }
    return $null
}

# Keep what a stopped operation had got to, then abort it. Everything in the worktree - resolved
# files, unresolved ones with their markers, untracked ones - is written as one commit on top of
# HEAD through a scratch index, so the operation's own index is not touched before it is aborted,
# and kept under refs/whetstone/stopped/<branch>/<short sha>. A stopped rebase's HEAD is part of
# what is kept, which is what makes a commit made on its detached HEAD recoverable.
# Refuses to abort when it could not keep that commit: the worktree is then the only copy.
# Returns @{ ok; kind; branch; was; sha; ref; head; reason } - kind '' when nothing was stopped,
# was the HEAD it stood on, head the commit the branch is at after the abort.
function Undo-StoppedGitOperation {
    param([string]$Dir, [string]$Why = 'before a new run started on it')
    $ErrorActionPreference = 'Continue'

    $op = Get-StoppedGitOperation -Dir $Dir
    if (-not $op) { return @{ ok = $true; kind = '' } }
    $label = if ($op.branch) { $op.branch } else { 'detached' }
    $was = Get-RefSha -Dir $Dir -Ref HEAD

    $sha = ''
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-index-" + [guid]::NewGuid().ToString('N'))
    $index = Get-GitDirPath -Dir $Dir -Name 'index'
    $prior = $env:GIT_INDEX_FILE
    try {
        if ($index -and (Test-Path -LiteralPath $index)) { Copy-Item -LiteralPath $index -Destination $scratch }
        $env:GIT_INDEX_FILE = $scratch
        git -C $Dir add -A *> $null
        if ($LASTEXITCODE -eq 0) {
            $tree = "$(git -C $Dir write-tree 2>$null)".Trim()
            if ($LASTEXITCODE -eq 0 -and $tree) {
                $msg = "whetstone: $label as a stopped $($op.kind) left it, kept $Why"
                $parent = if ($was) { @('-p', $was) } else { @() }
                $sha = "$(git -C $Dir commit-tree $tree @parent -m $msg 2>$null)".Trim()
                if ($LASTEXITCODE -ne 0) { $sha = '' }
            }
        }
    }
    finally {
        if ($null -eq $prior) { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue } else { $env:GIT_INDEX_FILE = $prior }
        Remove-Item -LiteralPath $scratch -Force -ErrorAction SilentlyContinue
    }
    if (-not $sha) {
        return @{ ok = $false; kind = $op.kind; branch = $op.branch
                  reason = "a $($op.kind) is stopped in the worktree at $Dir, and keeping what it had got to before aborting it failed" }
    }
    $ref = "refs/whetstone/stopped/$label/$($sha.Substring(0, 8))"
    git -C $Dir update-ref $ref $sha *> $null
    if ($LASTEXITCODE -ne 0) {
        return @{ ok = $false; kind = $op.kind; branch = $op.branch
                  reason = "a $($op.kind) is stopped in the worktree at $Dir, and keeping what it had got to as $ref failed" }
    }

    git -C $Dir $op.kind --abort *> $null
    if (Get-StoppedGitOperation -Dir $Dir) {
        return @{ ok = $false; kind = $op.kind; branch = $op.branch; sha = $sha; ref = $ref
                  reason = "a $($op.kind) is stopped in the worktree at $Dir and would not abort (what it had got to is kept as $ref)" }
    }
    # An abort puts tracked files back but leaves the files the operation's conflicts created. They
    # are in the commit above, so the worktree is left as clean as the branch.
    if (@(git -C $Dir status --porcelain 2>$null | Where-Object { $_ }).Count -gt 0) {
        git -C $Dir reset --quiet --hard *> $null
        git -C $Dir clean -fdq *> $null
    }
    return @{ ok = $true; kind = $op.kind; branch = $op.branch; was = $was; sha = $sha; ref = $ref
              head = (Get-RefSha -Dir $Dir -Ref HEAD) }
}

# One line for a ticket comment on what Undo-StoppedGitOperation did. $Where names the machine.
function Format-StoppedGitOperation {
    param($Stopped, [string]$Where = '')
    $s = $Stopped
    $was = if ($s.was) { " on ``$($s.was.Substring(0, 8))``" } else { '' }
    $branch = if ($s.branch) { "``$($s.branch)``" } else { 'the branch' }
    $at = if ($s.head) { " at ``$($s.head.Substring(0, 8))``" } else { '' }
    return "A ``git $($s.kind)`` was left stopped$was in this ticket's worktree$Where, by a run that ended before finishing it. " +
        "It was aborted, so $branch is back where it was before that $($s.kind) began$at, and nothing was committed on top of it. " +
        "What it had got to - resolved files, unresolved ones and anything committed on its detached HEAD - is kept as ``$($s.ref)`` (``$($s.sha.Substring(0, 8))``)$Where. " +
        "To look: ``git show --stat $($s.ref)``. The next run redoes the $($s.kind) from the branch."
}

# Whether the board host has had every commit of <Sha> as this branch: <Sha> is, or is behind, a
# commit this clone fetched as the board host's branch. Read from the tracking ref's reflog, which
# Get-BoardBranch keeps, so a branch the board host has since moved past - a rebase there, or a
# hand-back of a commit this clone's own branch never reached - is still known to be theirs.
function Test-BoardHadCommit {
    param([string]$RepoDir, [string]$Branch, [string]$Sha)
    if (-not $Sha) { return $false }
    $seen = @(git -C $RepoDir rev-list --walk-reflogs --max-count=200 "refs/remotes/$BoardRemote/$Branch" 2>$null |
        Where-Object { $_ } | Select-Object -Unique)
    foreach ($s in $seen) {
        if ($s -eq $Sha) { return $true }
        git -C $RepoDir merge-base --is-ancestor $Sha $s *> $null
        if ($LASTEXITCODE -eq 0) { return $true }
    }
    return $false
}

# Mirror the board host's view of the base into the clone: its LOCAL <base>, and its origin/*.
#
# With both mirrored, Resolve-BaseRef on the worker host answers exactly what it would on the board
# host, so which ref is "the base" stays decided in the two places CLAUDE.md names and no third.
# For a project that merges instead of raising a PR this is the part that matters: the board
# host's local <base> holds every ticket merged there and never pushed, and origin has none of it.
#
# The clone's <base> is a mirror. Nothing on the worker host commits to it, so it is force-updated,
# and the clone's main checkout is detached off it first if needed (a detach rewrites no file) -
# git will not update a branch that is checked out. Called at the start of a run, and again before
# the hand-off rebase so the rebase sees what landed on the board host meanwhile.
# Returns @{ ok; reason }.
function Sync-BoardRefs {
    param([string]$RepoDir, [string]$BaseBranch)
    $ErrorActionPreference = 'Continue'

    return Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        if (@(git -C $RepoDir remote 2>$null) -notcontains $BoardRemote) {
            return @{ ok = $false; reason = "the clone at $RepoDir has no '$BoardRemote' remote" }
        }

        $head = "$(git -C $RepoDir symbolic-ref --quiet HEAD 2>$null)".Trim()
        if ($head -eq "refs/heads/$BaseBranch") {
            git -C $RepoDir checkout --quiet --detach *> $null
            if ($LASTEXITCODE -ne 0) { return @{ ok = $false; reason = "could not detach the clone's main checkout off '$BaseBranch'" } }
        }
        $holder = Get-BranchWorktree -RepoDir $RepoDir -Branch $BaseBranch
        if ($holder) {
            return @{ ok = $false; reason = "'$BaseBranch' is checked out in $holder - the clone's base is a mirror and must not be checked out" }
        }

        git -C $RepoDir fetch --quiet --no-tags --prune $BoardRemote `
            "+refs/heads/${BaseBranch}:refs/heads/$BaseBranch" `
            "+refs/remotes/origin/*:refs/remotes/origin/*" *> $null
        if ($LASTEXITCODE -ne 0) {
            return @{ ok = $false; reason = "could not fetch '$BaseBranch' from the board host (unreachable, or it has no such branch)" }
        }
        return @{ ok = $true }
    }
}

# Fetch the ticket branch as the board host has it, into refs/remotes/board/<Branch>.
# Returns @{ ok; sha; reason } - sha is '' when the board host has no such branch, which is a new
# ticket and not a failure. Asked with ls-remote first so "absent" and "unreachable" are told apart:
# a fetch of a missing ref fails exactly as a fetch of an unreachable remote does.
function Get-BoardBranch {
    param([string]$RepoDir, [string]$Branch)
    $ErrorActionPreference = 'Continue'

    return Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        $tracking = "refs/remotes/$BoardRemote/$Branch"
        $null = git -C $RepoDir ls-remote --exit-code --heads $BoardRemote "refs/heads/$Branch" 2>$null
        $code = $LASTEXITCODE
        if ($code -eq 2) {
            git -C $RepoDir update-ref -d $tracking *> $null
            return @{ ok = $true; sha = '' }
        }
        if ($code -ne 0) { return @{ ok = $false; sha = ''; reason = "could not reach the board host's repo" } }

        # The reflog is what Test-BoardHadCommit reads, so it is kept whatever the clone's config says.
        git -C $RepoDir -c core.logAllRefUpdates=true fetch --quiet --no-tags $BoardRemote "+refs/heads/${Branch}:$tracking" *> $null
        if ($LASTEXITCODE -ne 0) { return @{ ok = $false; sha = ''; reason = "could not fetch '$Branch' from the board host" } }
        return @{ ok = $true; sha = (Get-RefSha -Dir $RepoDir -Ref $tracking) }
    }
}

# Bring the clone's refs/heads/<Branch> to the board host's commit, before Resolve-Worktree.
#
#   board has none            -> 'new' (nothing here either) or 'local-only' (a run here that never
#                                handed back); left alone
#   no local branch           -> 'created' at the board host's commit
#   equal                     -> 'current'
#   local behind              -> 'behind', fast-forwarded (the board host ran it since)
#   local ahead               -> 'ahead', left alone: commits a refused hand-back left here, which
#                                the next hand-back carries
#   diverged                  -> 'reset' to the board host's commit, but ONLY when everything here
#                                was already on the board host: the local tip is the agreed commit,
#                                or one this clone has seen as the board host's branch
#                                (Test-BoardHadCommit) - a rebase over there, or a hand-back of a
#                                commit the local branch never reached. Otherwise refused: those
#                                commits exist nowhere else.
#
# Before any of that, a rebase or merge an earlier run left stopped in the branch's worktree is
# kept under a ref and aborted (Undo-StoppedGitOperation), and said in stopped.
#
# A worktree holding the branch is moved with it. Anything uncommitted in it is set aside in a
# stash first (Save-WorktreeLeftovers) rather than refusing. By the time a worktree is looked at,
# every commit on the branch here is also on the board host ('current', 'behind', and a 'reset'
# from the agreed commit), so what is uncommitted is all there is to keep - and in practice it is
# what a run that was killed before its hand-off nets left behind. Refusing instead blocked the
# ticket on every retry, with nobody able to clean a tree on another machine but a person there.
# 'ahead' and a refused 'diverged' never reach the worktree, and are as they were.
# Returns @{ ok; state; reason; stash; stopped } - stash is @{ sha; files; message } when something
# was set aside, stopped is Undo-StoppedGitOperation's answer when an operation was undone.
function Update-BranchFromBoard {
    param([string]$RepoDir, [string]$Branch, [string]$BoardSha)
    $ErrorActionPreference = 'Continue'

    return Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        # A rebase or merge an earlier run left stopped is undone first (Undo-StoppedGitOperation):
        # until it is, the branch is not where its worktree's HEAD is, and nothing below can be
        # judged. stopped is reported back, like stash, because what it kept is the only copy.
        $wt = Get-BranchWorktree -RepoDir $RepoDir -Branch $Branch
        $stopped = $null
        if ($wt) {
            $undo = Undo-StoppedGitOperation -Dir $wt -Why 'before bringing it level with the board host'
            if (-not $undo.ok) { return @{ ok = $false; state = 'stopped'; reason = $undo.reason } }
            if ($undo.kind) { $stopped = $undo }
        }

        $local = Get-RefSha -Dir $RepoDir -Ref "refs/heads/$Branch"
        $agreed = Get-AgreedRef $Branch

        if (-not $BoardSha) { return @{ ok = $true; state = $(if ($local) { 'local-only' } else { 'new' }); stopped = $stopped } }

        if (-not $local) {
            git -C $RepoDir branch --no-track $Branch $BoardSha *> $null
            if ($LASTEXITCODE -ne 0) { return @{ ok = $false; state = 'created'; reason = "could not create '$Branch' at the board host's commit"; stopped = $stopped } }
            git -C $RepoDir update-ref $agreed $BoardSha *> $null
            return @{ ok = $true; state = 'created'; stopped = $stopped }
        }

        $behind = $false
        if ($local -eq $BoardSha) { $state = 'current' }
        else {
            git -C $RepoDir merge-base --is-ancestor $local $BoardSha *> $null
            $behind = ($LASTEXITCODE -eq 0)
            if (-not $behind) {
                git -C $RepoDir merge-base --is-ancestor $BoardSha $local *> $null
                if ($LASTEXITCODE -eq 0) { return @{ ok = $true; state = 'ahead'; stopped = $stopped } }
                # Safe to reset only when nothing here would be lost: the local tip is the agreed
                # commit, or one the board host has had as this branch before it moved on.
                if ((Get-RefSha -Dir $RepoDir -Ref $agreed) -ne $local -and
                    -not (Test-BoardHadCommit -RepoDir $RepoDir -Branch $Branch -Sha $local)) {
                    return @{ ok = $false; state = 'diverged'; stopped = $stopped
                              reason = "'$Branch' here has commits the board host never received, and the board host's branch has moved on without them" }
                }
            }
            $state = if ($behind) { 'behind' } else { 'reset' }
        }

        $stash = $null
        if ($wt) {
            $saved = Save-WorktreeLeftovers -Dir $wt -Branch $Branch
            if (-not $saved.ok) { return @{ ok = $false; state = $state; reason = $saved.reason; stopped = $stopped } }
            if ($saved.sha) { $stash = $saved }
        }

        if ($state -ne 'current') {
            if ($wt) {
                if ($behind) { git -C $wt merge --quiet --ff-only $BoardSha *> $null }
                else         { git -C $wt reset --quiet --hard $BoardSha *> $null }
            }
            else {
                git -C $RepoDir update-ref "refs/heads/$Branch" $BoardSha $local *> $null
            }
            if ($LASTEXITCODE -ne 0) { return @{ ok = $false; state = $state; reason = "could not move '$Branch' to the board host's commit"; stash = $stash; stopped = $stopped } }
        }

        git -C $RepoDir update-ref $agreed $BoardSha *> $null
        return @{ ok = $true; state = $state; stash = $stash; stopped = $stopped }
    }
}

# Set aside everything uncommitted in a worktree - untracked files included, ignored ones not - as
# one stash entry named after the branch, so the worktree is clean and nothing in it is lost. The
# stash lives in the repo's shared refs, so `git stash list` in any worktree of the clone shows it.
# Used on the board host as well (Resolve-Worktree), where $Why says why it was set aside instead.
# Returns @{ ok; sha; files; message; reason } - sha is '' when there was nothing to set aside.
function Save-WorktreeLeftovers {
    param([string]$Dir, [string]$Branch, [string]$Why = 'before bringing it level with the board host')
    $ErrorActionPreference = 'Continue'

    $files = @(git -C $Dir status --porcelain 2>$null | Where-Object { $_ } | ForEach-Object { "$_".Substring(3) })
    if ($files.Count -eq 0) { return @{ ok = $true; sha = ''; files = @() } }

    $message = "whetstone: left uncommitted on $Branch, set aside $Why"
    git -C $Dir stash push --quiet --include-untracked -m $message *> $null
    if ($LASTEXITCODE -ne 0) {
        return @{ ok = $false; sha = ''; files = $files
                  reason = "the worktree at $Dir has uncommitted changes, and setting them aside in a stash failed" }
    }
    if (@(git -C $Dir status --porcelain 2>$null | Where-Object { $_ }).Count -gt 0) {
        return @{ ok = $false; sha = ''; files = $files
                  reason = "the worktree at $Dir still has uncommitted changes after setting them aside in a stash" }
    }
    return @{ ok = $true; sha = (Get-RefSha -Dir $Dir -Ref 'refs/stash'); files = $files; message = $message }
}

# Push the worktree's HEAD to refs/whetstone/incoming/<RunId> on the board host.
#
# Never to refs/heads/<branch>: the board host normally has that branch checked out in a worktree
# of its own (any ticket it has run before), and git refuses to update a checked-out branch. An
# incoming ref is checked out nowhere. The board host's API then moves the branch itself, where it
# knows which worktree holds it and can check the run's lease - see the branch accept route.
#
# --no-verify: this is transport between two copies of one repo, not a publish. A project's
# pre-push hook is written for pushes to origin and would run its checks here for nothing.
# $Ref is what is sent: the run's branch, not its HEAD, because the commit handed back is recorded
# as agreed for that branch, and a HEAD left detached off it would make the two disagree.
# Returns @{ ok; sha; ref; reason }.
function Send-BranchToBoard {
    param([string]$WorkDir, [string]$RunId, [string]$Ref = 'HEAD')
    $ErrorActionPreference = 'Continue'

    if ($RunId -notmatch '^[A-Za-z0-9_-]+$') { return @{ ok = $false; sha = ''; reason = "run id '$RunId' is not a valid ref component" } }
    $sha = Get-RefSha -Dir $WorkDir -Ref $Ref
    if (-not $sha) { return @{ ok = $false; sha = ''; reason = "no commit at $Ref in $WorkDir" } }

    $ref = "refs/whetstone/incoming/$RunId"
    git -C $WorkDir push --quiet --no-verify $BoardRemote "+${sha}:$ref" *> $null
    if ($LASTEXITCODE -ne 0) { return @{ ok = $false; sha = $sha; ref = $ref; reason = "could not push to the board host's repo" } }
    return @{ ok = $true; sha = $sha; ref = $ref }
}

# Record that the board host accepted <Sha> as <Branch>. Called only after the accept route said
# yes; until then the commit is not agreed, and a later divergence must not be reset over it.
function Set-BranchHandedBack {
    param([string]$RepoDir, [string]$Branch, [string]$Sha)
    $ErrorActionPreference = 'Continue'
    return Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        git -C $RepoDir update-ref (Get-AgreedRef $Branch) $Sha *> $null
        return ($LASTEXITCODE -eq 0)
    }
}

# ---- A ticket's files, at the same paths on both machines (phase 2) -------------------------
#
# Each machine keeps a ticket's files at the same path under its own data root (HostDataMirror in
# the API). The ticket's attachments and owner-note files come here when a run starts; its QA
# captures go to the board host as they land.

# Fetch the ticket's own files from the board host and unpack them under this data root.
# Returns @{ ok; files; reason }. A failure is the caller's to log, not to stop on: a run without
# its attachments can still do most of its work, and says so if it needed one.
function Get-BoardTicketData {
    param([string]$DashboardUrl, [string]$TicketId, [string]$DataRoot)
    $zip = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-ticket-$TicketId-" + [guid]::NewGuid().ToString('N') + ".zip")
    try {
        Invoke-WebRequest -Uri "$DashboardUrl/api/hosts/ticket-data/$TicketId" -OutFile $zip -UseBasicParsing -TimeoutSec 120
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        $count = 0
        try {
            $root = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\') + '\'
            foreach ($entry in $archive.Entries) {
                if (-not $entry.Name) { continue }
                $dest = [IO.Path]::GetFullPath((Join-Path $DataRoot ($entry.FullName -replace '/', '\')))
                # Only ever under this data root, whatever the archive says.
                if (-not $dest.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
                New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                $count++
            }
        }
        finally { $archive.Dispose() }
        return @{ ok = $true; files = $count }
    }
    catch { return @{ ok = $false; files = 0; reason = $_.Exception.Message } }
    finally { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
}

# Upload every capture under $Dir the board host has not had yet - new, or changed since it was
# sent. $Seen remembers what was sent (relative path -> size and time), and is the caller's to keep
# between passes. A file still being written is left for the next pass. Returns how many went.
function Send-QaCaptures {
    param([string]$DashboardUrl, [string]$Dir, [string]$TicketId, [string]$HostName, [string]$RunId, [hashtable]$Seen)
    if (-not (Test-Path $Dir)) { return 0 }
    $sent = 0
    $base = [IO.Path]::GetFullPath($Dir).TrimEnd('\') + '\'
    foreach ($f in @(Get-ChildItem -Path $Dir -File -Recurse -ErrorAction SilentlyContinue)) {
        if (@('.png', '.jpg', '.jpeg', '.webp', '.gif') -notcontains $f.Extension.ToLowerInvariant()) { continue }
        $rel = $f.FullName.Substring($base.Length) -replace '\\', '/'
        $stamp = "$($f.Length)@$($f.LastWriteTimeUtc.Ticks)"
        if ($Seen[$rel] -eq $stamp) { continue }
        # Still open for writing: next time.
        try { $probe = [IO.File]::Open($f.FullName, 'Open', 'Read', 'Read'); $probe.Dispose() } catch { continue }
        $uri = "$DashboardUrl/api/hosts/$HostName/qa-captures/$TicketId/" + (($rel -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/') + "?runId=$RunId"
        try {
            $null = Invoke-WebRequest -Uri $uri -Method Put -InFile $f.FullName -ContentType 'application/octet-stream' -UseBasicParsing -TimeoutSec 60
            $Seen[$rel] = $stamp
            $sent++
        }
        catch { }
    }
    return $sent
}

# Keep uploading captures for the whole of a QA run: a background job that passes over $Dir every
# couple of seconds, so set_qa_shots - which the agent calls mid-run, naming its captures by this
# machine's paths - finds each one already on the board host. Stop-QaCaptureMirror ends it.
function Start-QaCaptureMirror {
    param([string]$DashboardUrl, [string]$Dir, [string]$TicketId, [string]$HostName, [string]$RunId)
    $lib = Join-Path $PSScriptRoot 'worker-board.ps1'
    return Start-Job -ArgumentList $lib, $DashboardUrl, $Dir, $TicketId, $HostName, $RunId -ScriptBlock {
        param($lib, $url, $dir, $ticket, $hostName, $run)
        . $lib
        $seen = @{}
        while ($true) {
            $null = Send-QaCaptures -DashboardUrl $url -Dir $dir -TicketId $ticket -HostName $hostName -RunId $run -Seen $seen
            Start-Sleep -Seconds 2
        }
    }
}

# End the mirror, with one last pass of its own so a capture taken in the run's final seconds is
# not left behind.
function Stop-QaCaptureMirror {
    param($Job, [string]$DashboardUrl, [string]$Dir, [string]$TicketId, [string]$HostName, [string]$RunId)
    if ($Job) { Stop-Job -Job $Job -ErrorAction SilentlyContinue; Remove-Job -Job $Job -Force -ErrorAction SilentlyContinue }
    return Send-QaCaptures -DashboardUrl $DashboardUrl -Dir $Dir -TicketId $TicketId -HostName $HostName -RunId $RunId -Seen @{}
}
