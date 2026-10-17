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

# The linked or main worktree that has refs/heads/<Branch> checked out, or $null.
function Get-BranchWorktree {
    param([string]$RepoDir, [string]$Branch)
    $path = $null
    foreach ($line in @(git -C $RepoDir worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *') { $path = $line.Substring(9) }
        elseif ($line -eq "branch refs/heads/$Branch") { return $path }
    }
    return $null
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

        git -C $RepoDir fetch --quiet --no-tags $BoardRemote "+refs/heads/${Branch}:$tracking" *> $null
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
#   diverged                  -> 'reset' to the board host's commit, but ONLY when the local tip is
#                                the agreed commit, i.e. everything here was already on the board
#                                host and the divergence is a rebase over there. Otherwise refused:
#                                those commits exist nowhere else.
#
# A worktree holding the branch is moved with it, and refuses when it has anything uncommitted -
# a run hands back only what it committed, so uncommitted files there are somebody's edits.
# Returns @{ ok; state; reason }.
function Update-BranchFromBoard {
    param([string]$RepoDir, [string]$Branch, [string]$BoardSha)
    $ErrorActionPreference = 'Continue'

    return Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        $local = Get-RefSha -Dir $RepoDir -Ref "refs/heads/$Branch"
        $agreed = Get-AgreedRef $Branch

        if (-not $BoardSha) { return @{ ok = $true; state = $(if ($local) { 'local-only' } else { 'new' }) } }

        if (-not $local) {
            git -C $RepoDir branch --no-track $Branch $BoardSha *> $null
            if ($LASTEXITCODE -ne 0) { return @{ ok = $false; state = 'created'; reason = "could not create '$Branch' at the board host's commit" } }
            git -C $RepoDir update-ref $agreed $BoardSha *> $null
            return @{ ok = $true; state = 'created' }
        }
        if ($local -eq $BoardSha) {
            git -C $RepoDir update-ref $agreed $BoardSha *> $null
            return @{ ok = $true; state = 'current' }
        }

        git -C $RepoDir merge-base --is-ancestor $local $BoardSha *> $null
        $behind = ($LASTEXITCODE -eq 0)
        if (-not $behind) {
            git -C $RepoDir merge-base --is-ancestor $BoardSha $local *> $null
            if ($LASTEXITCODE -eq 0) { return @{ ok = $true; state = 'ahead' } }
            if ((Get-RefSha -Dir $RepoDir -Ref $agreed) -ne $local) {
                return @{ ok = $false; state = 'diverged'
                          reason = "'$Branch' here has commits the board host never received, and the board host's branch has moved on without them" }
            }
        }
        $state = if ($behind) { 'behind' } else { 'reset' }

        $wt = Get-BranchWorktree -RepoDir $RepoDir -Branch $Branch
        if ($wt) {
            if (@(git -C $wt status --porcelain 2>$null | Where-Object { $_ }).Count -gt 0) {
                return @{ ok = $false; state = $state; reason = "the worktree at $wt has uncommitted changes" }
            }
            if ($behind) { git -C $wt merge --quiet --ff-only $BoardSha *> $null }
            else         { git -C $wt reset --quiet --hard $BoardSha *> $null }
        }
        else {
            git -C $RepoDir update-ref "refs/heads/$Branch" $BoardSha $local *> $null
        }
        if ($LASTEXITCODE -ne 0) { return @{ ok = $false; state = $state; reason = "could not move '$Branch' to the board host's commit" } }

        git -C $RepoDir update-ref $agreed $BoardSha *> $null
        return @{ ok = $true; state = $state }
    }
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
# Returns @{ ok; sha; ref; reason }.
function Send-BranchToBoard {
    param([string]$WorkDir, [string]$RunId)
    $ErrorActionPreference = 'Continue'

    if ($RunId -notmatch '^[A-Za-z0-9_-]+$') { return @{ ok = $false; sha = ''; reason = "run id '$RunId' is not a valid ref component" } }
    $sha = Get-RefSha -Dir $WorkDir -Ref HEAD
    if (-not $sha) { return @{ ok = $false; sha = ''; reason = "no commit at HEAD in $WorkDir" } }

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
