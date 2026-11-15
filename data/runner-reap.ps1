# ============================================================
# Reaping a worker host's worktrees. Functions only: dot-sourced by runner.ps1 (which starts a pass
# and keeps the list of what its runs hold) and by runner-reap-pass.ps1 (one pass, in a process of
# its own). Needs heartbeat-common.ps1, worker-board.ps1 (Get-StoppedGitOperation, Get-GitDirPath)
# and worker-repos.ps1 (Get-FullPathKey) loaded first.
#
# A worker host's worktree paths never reach the board (CLAUDE.md, "Worker hosts"), so the board's
# own reaper (Services/WorktreeReaper.cs) can never remove one here - and for as long as nothing
# else did, every ticket this host ever ran kept its worktree, package install and all, until the
# disk filled and every run after it failed at `git worktree add`. So a pass lists what is here,
# asks the board which may go (by branch and ticket, never by path - the board answers from its
# tickets, HostWorktrees.cs), and removes only those, after checks of its own that only this machine
# can make. The branch always stays, as on the board host.
#
# It runs in its own process, never in the runner's loop. The first version ran inline: removing
# one worktree with thousands of installed packages takes minutes on Windows, the runner read no
# start order meanwhile, and the board gave up on every run it sent there. Now the runner only
# starts a pass and looks at whether it has ended.
#
# Removing a worktree is two steps, and only the first holds the hub lock the workers take:
#   1. under the lock, briefly: the checks, then `git worktree move` it aside to .reaping-<name>
#      under the same root (a rename) and detach it, so its branch is free and its folder name is
#      one no run looks for - a run of the same ticket that starts now makes a new worktree
#   2. without the lock, for as long as it takes: `git worktree remove` of the moved copy
# A pass that is killed between the two leaves a .reaping-* folder, which the next pass removes.
#
# A project of several repos never runs on a worker host (worker.ps1 stops such a run before it
# makes anything), so what is here is always one clone's worktrees, straight under its root.
#
# Tests: pwsh -NoProfile -ExecutionPolicy Bypass -File data/runner.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

# What a moved-aside worktree's folder starts with. Nothing else makes one, so anything by this name
# under a worktree root is a pass's own, half done.
$ReapAsidePrefix = '.reaping-'

# The exit code of a pass that stopped with more to do, so the runner comes back soon, not in an hour.
$ReapMoreExit = 10

# A project's worktree root on this machine: host.json's, or the one Convert-ClaimForHost derives.
function Get-HostWorktreeRoot {
    param([string]$Key, $Map)
    $repo = "$($Map.path)".TrimEnd('\', '/')
    if ("$($Map.worktreeRoot)") { return "$($Map.worktreeRoot)" }
    return [IO.Path]::Combine([IO.Path]::GetDirectoryName($repo), '.worktrees', $Key)
}

# The worktrees of $Repo straight under $Root, as git lists them: @(@{ name; path; kind; branch;
# ticketId }). kind is what the board is asked about them by (HostWorktreeItem): 'branch' for a
# ticket's own branch, 'review' for a detached review-<branch>, 'base' for a detached
# walkthrough-ticket-<id> (a slice's walk-through or a discovery). Left out, so never asked about
# and never removed: the clone itself, anything outside the root or deeper in it, a locked
# worktree, one whose folder is gone (git worktree prune is the workers' business), and a detached
# one of any other name - which includes one stopped mid-rebase, whose branch is still its own,
# and one a pass moved aside.
function Get-HostWorktrees {
    param([string]$Repo, [string]$Root)
    $rootKey = Get-FullPathKey $Root
    if (-not $rootKey) { return }
    $sep = [IO.Path]::DirectorySeparatorChar
    $list = @()
    $cur = $null
    $flush = {
        if (-not $cur -or $cur.locked) { return }
        $key = Get-FullPathKey $cur.path
        if (-not $key.StartsWith("$rootKey$sep")) { return }
        $name = Split-Path -Leaf $cur.path
        if ($key -ne "$rootKey$sep$($name.ToLowerInvariant())") { return }
        if ($name.StartsWith($ReapAsidePrefix)) { return }
        if (-not (Test-Path -LiteralPath $cur.path)) { return }
        if ($cur.branch) { $list += @{ name = $name; path = $cur.path; kind = 'branch'; branch = $cur.branch; ticketId = $null }; return }
        if ($name -match '^walkthrough-ticket-(\d+)$') { $list += @{ name = $name; path = $cur.path; kind = 'base'; branch = $null; ticketId = [int]$Matches[1] }; return }
        if ($name -like 'review-*') { $list += @{ name = $name; path = $cur.path; kind = 'review'; branch = $null; ticketId = $null } }
    }
    foreach ($line in @(git -C $Repo worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *') { . $flush; $cur = @{ path = $line.Substring(9).Trim(); branch = $null; locked = $false } }
        elseif ($cur -and $line -like 'branch refs/heads/*') { $cur.branch = $line.Substring(18).Trim() }
        elseif ($cur -and ($line -eq 'locked' -or $line -like 'locked *')) { $cur.locked = $true }
    }
    . $flush
    return $list
}

# A branch's folder name, as worker.ps1's Get-WorktreeDirName makes it (and BranchNaming.WorktreeDirName
# on the board): written three times now, and the three must agree.
function Get-WorktreeDirName {
    param([string]$Branch)
    $name = $Branch -replace '[\\/:*?"<>|]', '-'
    $name = $name -replace '\s+', '-'
    return $name.Trim('-', '.')
}

# The folder names and branches the runs live here hold, read from their claims: a worktree one of
# them names is never asked about, and is checked for again under the lock just before it is moved.
# Whatever the claim could resolve to is held - the branch it records, the one the board named for
# it, the walk-through's checkout and the review's - so a run that has made its worktree but not yet
# told the board its branch is covered too.
function Get-HeldWorktreeNames {
    param([object[]]$Claims)
    $held = @{}
    foreach ($c in @($Claims)) {
        if (-not $c) { continue }
        foreach ($b in @("$($c.ticket.branch)", "$($c.branchName)")) {
            if ($b.Trim()) { $held["b:$($b.Trim())"] = $true; $held["n:$(Get-WorktreeDirName $b.Trim())"] = $true }
        }
        if ("$($c.ticket.id)") { $held["n:walkthrough-ticket-$($c.ticket.id)"] = $true }
        $rv = "$($c.ticket.review.sourceBranch)".Trim()
        if ($rv) { $held["n:$(Get-WorktreeDirName "review-$rv")"] = $true }
    }
    return $held
}

# The held list, as the runner hands it to a pass: a file it rewrites on every loop and before it
# launches each worker, so the pass reads it fresh before every move. Written whole and renamed into
# place, so a pass never reads half of one.
function Write-ReapHeld {
    param([string]$Path, [hashtable]$Held, [datetime]$Now = (Get-Date))
    $body = @{ at = $Now.ToUniversalTime().ToString('o'); held = @($Held.Keys | Sort-Object) } | ConvertTo-Json -Compress
    $tmp = "$Path.$PID.tmp"
    Set-Content -LiteralPath $tmp -Value $body -Encoding ascii
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

# The held list a pass reads back, or $null - and a pass that cannot tell what is held removes
# nothing. A list older than $StaleMinutes is $null too: the runner rewrites it every loop, so an
# old one means the runner has stopped, and what it holds now is unknown.
function Read-ReapHeld {
    param([string]$Path, [int]$StaleMinutes = 5, [datetime]$Now = (Get-Date))
    try {
        $j = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json
        if (($Now.ToUniversalTime() - ([datetime]$j.at).ToUniversalTime()).TotalMinutes -gt $StaleMinutes) { return $null }
        $h = @{}
        foreach ($k in @($j.held)) { if ("$k") { $h["$k"] = $true } }
        return $h
    }
    catch { return $null }
}

# Why one worktree the board said may go must stay after all, or $null. Only this machine can tell:
#   anything uncommitted or untracked - work no commit holds (git worktree remove would refuse too)
#   a stopped rebase, merge, cherry-pick or revert - the next run of its ticket keeps what it had got to
#   anything git wrote there in the last $QuietMinutes (HEAD, its reflog, the folder itself) - a run
#     this runner does not know about yet, such as one adopted from a runner that is still exiting
function Get-WorktreeKeepReason {
    param([string]$Path, [int]$QuietMinutes = 60, [datetime]$Now = (Get-Date))
    $op = Get-StoppedGitOperation -Dir $Path
    if ($op) { return "a git $($op.kind) is stopped in it" }
    $status = @(git -C $Path status --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0) { return "git could not read its status" }
    if (@($status | Where-Object { "$_".Trim() }).Count -gt 0) { return "it has uncommitted or untracked files" }
    $stamps = @((Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue))
    foreach ($n in 'HEAD', 'logs/HEAD') {
        $p = Get-GitDirPath -Dir $Path -Name $n
        if ($p -and (Test-Path -LiteralPath $p)) { $stamps += Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
    }
    $newest = @($stamps | Where-Object { $_ } | Sort-Object LastWriteTime -Descending)[0]
    if ($newest -and ($Now - $newest.LastWriteTime).TotalMinutes -lt $QuietMinutes) { return "git wrote to it in the last $QuietMinutes min" }
    return $null
}

# The hub lock worker.ps1 takes as Invoke-WithMutex "hub-<repo>" - the same name, so the two exclude
# each other - but waited for only $WaitMs, and never entered when not got: Invoke-WithMutex runs
# its block anyway after its wait, which suits a worker that has to go on, and not a pass that can
# simply come back later. Returns the held mutex, or $null.
function Enter-HubLock {
    param([string]$Repo, [int]$WaitMs = 5000)
    $safe = ("hub-$Repo" -replace '[^A-Za-z0-9_-]', '-')
    $m = New-Object System.Threading.Mutex($false, "Local\CDH-$safe")
    try { if ($m.WaitOne($WaitMs)) { return $m } }
    catch [System.Threading.AbandonedMutexException] { return $m }
    $m.Dispose()
    return $null
}

function Exit-HubLock {
    param($Lock)
    if (-not $Lock) { return }
    try { $Lock.ReleaseMutex() } catch { }
    $Lock.Dispose()
}

# Step 2, for a worktree already moved aside (or a folder a killed pass left under that name):
# remove it, without the lock. git first; if git cannot (a worker's prune dropped its record
# meanwhile, a path too long for it), the folder itself, which is this pass's by its name and
# holds nothing anyone wrote - it was clean when it was moved. Then git's record, under the lock if
# it can be had quickly. Returns '' when it is gone, else why not.
function Remove-AsideWorktree {
    param([string]$Repo, [string]$Path, [int]$LockWaitMs = 5000)
    if (-not (Split-Path -Leaf $Path).StartsWith($ReapAsidePrefix)) { return "'$Path' is not a folder a pass moved aside" }
    $lines = @(git -C $Repo worktree remove $Path 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0 -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        $lock = Enter-HubLock -Repo $Repo -WaitMs $LockWaitMs
        if ($lock) { try { git -C $Repo worktree prune *> $null } finally { Exit-HubLock $lock } }
    }
    if (Test-Path -LiteralPath $Path) {
        return "could not remove '$Path': $((@($lines | Where-Object { $_.Trim() } | Select-Object -Last 3)) -join ' / ')"
    }
    return ''
}

# One pass over every project this host serves: finish what an earlier pass left moved aside, list
# the worktrees, ask the board (-Ask, a scriptblock taking the project key and the items, returning
# its verdicts), and remove what it says may go and nothing here says must stay.
#   -Held      a scriptblock giving what the runs here hold, asked afresh before every move;
#              $null from it stops the pass - it cannot tell what is safe
#   -Stopping  a scriptblock; true stops the pass between removals (runner.stop)
# The pass stops starting removals once $BudgetSeconds have gone - lock waits and slow removals
# included, since it is wall-clock - and when the hub lock cannot be had in $LockWaitMs, rather than
# queue behind a worker. Either way it says there is more, and the runner comes back soon.
# Returns @{ removed = @('key/name: why'); kept = @('key/name: why'); failed = @(); more = $bool }.
function Remove-HostWorktrees {
    param([hashtable]$Projects, [scriptblock]$Ask, [scriptblock]$Held = { @{} }, [int]$QuietMinutes = 60,
          [int]$BudgetSeconds = 600, [int]$LockWaitMs = 5000, [scriptblock]$Stopping = { $false },
          [datetime]$Now = (Get-Date))
    $ErrorActionPreference = 'Continue'
    $result = @{ removed = @(); kept = @(); failed = @(); more = $false }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $outOfTime = { ($clock.Elapsed.TotalSeconds -ge $BudgetSeconds) -or [bool](& $Stopping) }
    foreach ($key in @($Projects.Keys | Sort-Object)) {
        if (& $outOfTime) { $result.more = $true; break }
        $map = $Projects[$key]
        $repo = "$($map.path)".TrimEnd('\', '/')
        if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) { continue }
        $root = Get-HostWorktreeRoot -Key $key -Map $map
        if (-not (Test-Path -LiteralPath $root)) { continue }

        foreach ($left in @(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name.StartsWith($ReapAsidePrefix) })) {
            if (& $outOfTime) { $result.more = $true; break }
            $why = Remove-AsideWorktree -Repo $repo -Path $left.FullName -LockWaitMs $LockWaitMs
            $name = $left.Name.Substring($ReapAsidePrefix.Length)
            if ($why) { $result.failed += "$key/${name}: $why" } else { $result.removed += "$key/${name}: an earlier pass had moved it aside" }
        }
        if ($result.more) { break }

        $holding = & $Held
        if ($null -eq $holding) { $result.failed += "${key}: could not read what the runs here hold - removed nothing"; break }
        $wts = @(Get-HostWorktrees -Repo $repo -Root $root | Where-Object {
            -not $holding["n:$($_.name)"] -and -not ($_.branch -and $holding["b:$($_.branch)"])
        })
        if ($wts.Count -eq 0) { continue }
        $items = @($wts | ForEach-Object { @{ name = $_.name; kind = $_.kind; branch = $_.branch; ticketId = $_.ticketId } })
        $verdicts = $null
        try { $verdicts = @(& $Ask $key $items) }
        catch { $result.failed += "${key}: could not ask the board: $($_.Exception.Message)"; continue }

        foreach ($v in @($verdicts)) {
            if (-not $v -or -not $v.reap) { continue }
            $wt = @($wts | Where-Object { $_.name -eq "$($v.name)" })[0]
            if (-not $wt) { continue }
            if (& $outOfTime) { $result.more = $true; break }

            # Step 1, under the lock and quick.
            $lock = Enter-HubLock -Repo $repo -WaitMs $LockWaitMs
            if (-not $lock) { $result.kept += "$key/$($wt.name): the hub was busy; trying again soon"; $result.more = $true; break }
            $aside = Join-Path $root "$ReapAsidePrefix$($wt.name)"
            $step = $null
            try {
                $holdingNow = & $Held
                if ($null -eq $holdingNow) { $step = @{ stop = "could not read what the runs here hold" } }
                elseif ($holdingNow["n:$($wt.name)"] -or ($wt.branch -and $holdingNow["b:$($wt.branch)"])) { $step = @{ kept = "a run here holds it now" } }
                elseif ($why = Get-WorktreeKeepReason -Path $wt.path -QuietMinutes $QuietMinutes -Now $Now) { $step = @{ kept = $why } }
                elseif (Test-Path -LiteralPath $aside) { $step = @{ failed = "'$aside' is in the way; the next pass clears it" } }
                else {
                    $lines = @(git -C $repo worktree move $wt.path $aside 2>&1 | ForEach-Object { "$_" })
                    if ($LASTEXITCODE -ne 0) { $step = @{ failed = "could not move it aside: $((@($lines | Where-Object { $_.Trim() } | Select-Object -Last 3)) -join ' / ')" } }
                    else {
                        git -C $aside checkout --quiet --detach *> $null
                        if ($LASTEXITCODE -ne 0) {
                            # Its branch would still be checked out there, where a run of its ticket
                            # could find it and start inside a folder being deleted. Put it back.
                            git -C $repo worktree move $aside $wt.path *> $null
                            $step = @{ failed = "could not free its branch after moving it aside; moved it back" }
                        }
                        else { $step = @{ aside = $true } }
                    }
                }
            }
            finally { Exit-HubLock $lock }

            if ($step.stop) { $result.failed += "${key}: $($step.stop) - removed nothing more"; $result.more = $true; break }
            if ($step.kept) { $result.kept += "$key/$($wt.name): $($step.kept)"; continue }
            if ($step.failed) { $result.failed += "$key/$($wt.name): $($step.failed)"; continue }

            # Step 2, without the lock, however long it takes.
            $why = Remove-AsideWorktree -Repo $repo -Path $aside -LockWaitMs $LockWaitMs
            if ($why) { $result.failed += "$key/$($wt.name): moved aside, but $why; the next pass tries again" }
            else { $result.removed += "$key/$($wt.name): $($v.why)" }
        }
        if ($result.more) { break }
    }
    return $result
}

# When the runner starts the next pass, from how the last one ended: soon when it stopped with more
# to do (out of time, the hub busy, the board unreachable mid-way), else in an hour.
function Get-NextReapAt {
    param([int]$ExitCode, [datetime]$Now = (Get-Date), [int]$SoonMinutes = 5, [int]$HourlyMinutes = 60)
    if ($ExitCode -eq $ReapMoreExit) { return $Now.AddMinutes($SoonMinutes) }
    return $Now.AddMinutes($HourlyMinutes)
}

# Start one pass in its own process and return at once with it: the runner's loop goes straight on
# to its check-in and its orders. $null when it could not be started.
function Start-ReapPass {
    param([string]$Script, [string[]]$PassArgs = @())
    try {
        $p = Start-Process -FilePath $PsHost -PassThru @HiddenWindow `
            -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + @($PassArgs))
        try { $null = $p.Handle } catch { }
        return $p
    }
    catch { return $null }
}
