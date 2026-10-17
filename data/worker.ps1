# ============================================================
# One agent, one ticket, one process.
#
# The orchestrator claims a ticket and launches this script for it. Everything that happens to that
# ticket happens here: its worktree, its Claude session, the safety nets over what the session left
# behind, the cycle report, the leases it gives back, and the reflection pass afterwards.
#
# It is a separate PROCESS rather than a function for one reason that matters more than tidiness.
# The worker timeout kills claude by walking the process tree from a root pid, and when every agent
# hung off the single orchestrator that root was shared - one agent timing out would have killed the
# other two mid-sentence. Here the root is $PID, which is this agent and nothing else.
#
# Usage: powershell -ExecutionPolicy Bypass -File worker.ps1 -ClaimFile <path> -Agent <1..N>
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    [Parameter(Mandatory = $true)][string]$ClaimFile,
    [Parameter(Mandatory = $true)][int]$Agent,
    # Identification only - the claim file is the source of truth for what this run works on. This
    # is here so that an orchestrator starting up can tell, from the process table alone, which
    # ticket a worker left over from a previous orchestrator is holding. Reading it off the command
    # line rather than out of the claim file matters: the claim file is named after the slot, so
    # after a restart it may describe a different run entirely, and sparing the wrong lease is worse
    # than sparing none. See Find-OrphanAgents in heartbeat.ps1.
    [int]$TicketId = 0,
    # Identification only, like -TicketId: on a worker host, the run id its runner was ordered to
    # start, so a runner starting up can adopt a survivor by the run its board host knows it as.
    [string]$RunId = ''
)

$DataRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $DataRoot "heartbeat-common.ps1")
# The branch round trip for a run on a worker host. Functions only; nothing in it runs unless the
# claim says this run is on one - see $onHost below.
. (Join-Path $DataRoot "worker-board.ps1")

# A worker is its own top-level session, never a child of one. When the app was started from inside
# Claude Code (`dotnet run` in a session's shell), every process under it inherits that session's
# variables: CLAUDECODE=1 makes claude refuse to launch at all ("cannot be launched inside another
# Claude Code session"), and a desktop session's ANTHROPIC_BASE_URL and CLAUDE_CODE_* would route the
# worker through a proxy and auth that belong to someone else's session. Only when CLAUDECODE says
# we really are under a session; and only what the host injected - a CLAUDE*/ANTHROPIC* variable the
# user set persistently (User or Machine scope) is theirs and stays. A normally launched app does
# nothing here. The app strips the same set at startup (Program.cs); this covers an app not yet
# restarted onto that.
if ($env:CLAUDECODE) {
    foreach ($name in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(CLAUDE|ANTHROPIC)' } | ForEach-Object Name)) {
        if ($null -eq [Environment]::GetEnvironmentVariable($name, 'User') -and
            $null -eq [Environment]::GetEnvironmentVariable($name, 'Machine')) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
    }
}

# Which Claude login this run spends, when the dashboard has been switched off the CLI's own. After
# the strip above, which would take it for something a host session injected; and here rather than
# in the orchestrator, because a worker is a fresh process that has just read config - so a switch
# reaches the next run with nothing restarted, and runs already out finish on the account they
# began with. Everything this process starts inherits it: the agent, its reflection, the notes store.
$claudeAccountDir = Get-ClaudeAccountDir
if ($claudeAccountDir) { $env:CLAUDE_CONFIG_DIR = $claudeAccountDir }

# Nobody is watching a worker, so git must never stop to ask. With several GitHub accounts stored,
# Git Credential Manager answers an unpinned fetch with an account picker on the human's desktop -
# once per fetch, per worker, forever. Fail the call instead; every fetch here already tolerates
# failure. The account for a repo is pinned with `credential.https://github.com.username` in it.
$env:GCM_INTERACTIVE     = 'never'
$env:GIT_TERMINAL_PROMPT = '0'

# From here on every log line and every event this process emits is stamped with its slot.
$script:AgentSlot = $Agent

if (-not (Test-Path $ClaimFile)) {
    Write-Log "ERROR: claim file not found at $ClaimFile - nothing to work."
    exit 1
}
try { $Claim = Get-Content -Path $ClaimFile -Raw | ConvertFrom-Json }
catch {
    Write-Log "ERROR: could not read the claim at ${ClaimFile}: $($_.Exception.Message)"
    exit 1
}

# --- Helpers this agent alone needs ---
#
# Get-Prop and Format-ToolCall used to live here. They moved to providers.ps1 (dot-sourced through
# heartbeat-common.ps1) when the second provider arrived: reading a tool call's shape is knowledge
# about a CLI, not about this agent, and Codex's tool calls need the same formatter.

# Report what one worker run spent, so the dashboard can answer "what does a ticket cost us" in
# something other than anecdotes. Fire-and-forget like the event feed: these numbers are worth
# having and never worth failing a cycle over. Note there is no dollar figure - this whole system
# runs on a Max subscription with no per-call billing, so the scarce resource is rate-limit
# headroom, which is tokens and time.
function Send-CycleReport {
    param(
        [string]$TicketId, [string]$Project, [string]$Phase, [string]$Skill, [bool]$Resumed,
        [string]$Model, [datetime]$StartedAt, [double]$DurationSeconds, [int]$Mutations,
        [string]$Outcome, $ApiDurationSeconds = $null, $NumTurns = $null,
        $InputTokens = $null, $OutputTokens = $null, $CacheReadTokens = $null, $CacheCreationTokens = $null,
        $PeakContextTokens = $null, $ToolResultChars = $null, $LargestToolResults = $null
    )
    if (-not $DashboardUrl -or -not $TicketId) { return }
    try {
        $payload = @{
            ticketId        = [int]$TicketId
            project         = $Project
            phase           = $Phase
            skill           = $Skill
            resumed         = $Resumed
            model           = $Model
            startedAt       = $StartedAt.ToUniversalTime().ToString("o")
            durationSeconds = [math]::Round($DurationSeconds, 1)
            mutations       = $Mutations
            outcome         = $Outcome
        }
        # Only send what this run actually knows. A cycle killed at the timeout never saw a result
        # event, and an absent token count is a fact about that cycle - not a zero.
        if ($null -ne $ApiDurationSeconds)  { $payload.apiDurationSeconds  = $ApiDurationSeconds }
        if ($null -ne $NumTurns)            { $payload.numTurns            = $NumTurns }
        if ($null -ne $InputTokens)         { $payload.inputTokens         = $InputTokens }
        if ($null -ne $OutputTokens)        { $payload.outputTokens        = $OutputTokens }
        if ($null -ne $CacheReadTokens)     { $payload.cacheReadTokens     = $CacheReadTokens }
        if ($null -ne $CacheCreationTokens) { $payload.cacheCreationTokens = $CacheCreationTokens }
        if ($null -ne $PeakContextTokens)   { $payload.peakContextTokens   = $PeakContextTokens }
        if ($null -ne $ToolResultChars)     { $payload.toolResultChars     = $ToolResultChars }
        if ($null -ne $LargestToolResults)  { $payload.largestToolResults  = @($LargestToolResults) }

        $body = $payload | ConvertTo-Json -Compress -Depth 4
        Invoke-RestMethod -Uri "$DashboardUrl/api/heartbeat/cycles" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 5 | Out-Null
    }
    catch { Write-Log "  (metrics) cycle report failed: $($_.Exception.Message)" }
}

# Does a branch exist locally or on origin? Asked of the hub repo, which shares its refs with
# every worktree hanging off it.
function Test-BranchRef {
    param([string]$RepoDir, [string]$Name)
    git -C $RepoDir rev-parse --verify --quiet "refs/heads/$Name" *> $null
    if ($LASTEXITCODE -eq 0) { return "local" }
    git -C $RepoDir rev-parse --verify --quiet "refs/remotes/origin/$Name" *> $null
    if ($LASTEXITCODE -eq 0) { return "remote" }
    return $null
}

# Which ref is "the base" right now: what a new branch forks from, what a finished one is rebased
# onto, and what the squash measures against. One answer for all three, because a branch forked
# from one ref and measured against another has other people's commits inside its range.
#
# A project that delivers by pull request gets origin/<base> whenever it exists. The local ref lives
# in the human's checkout and trails origin by however long it is since they last pulled.
#
# A project that merges instead (mergeInsteadOfPr) is the other way round. A finished ticket is
# merged into the LOCAL base in the hub and nothing is pushed, so that ref is where every earlier
# ticket and the owner's own unpushed commits are - on a project where a push is a
# production deploy, it can sit dozens of commits ahead of origin. There the local ref is the base, with
# one exception: when it is strictly behind origin, origin wins, since the merge fast-forwards the
# local ref to it before landing anything. Diverged stays local - the merge refuses a diverged base
# until a person reconciles it, and until then local is still where this ticket will land.
#
# The board draws its diff and runs its own rebase by the same rule - BaseRefAsync in
# Services/TicketGitService.cs - and the two have to agree, or a person reviews a different range
# from the one that was squashed. data/worker-base.tests.ps1 covers this side.
#
# Asks whatever repo it is pointed at; a worktree shares the hub's refs, so either gives the same
# answer. Does not fetch. Returns $null when the base exists nowhere.
function Resolve-BaseRef {
    param([string]$RepoDir, [string]$BaseBranch, [bool]$MergeLocally)

    git -C $RepoDir rev-parse --verify --quiet "refs/remotes/origin/$BaseBranch" *> $null
    $hasRemote = ($LASTEXITCODE -eq 0)
    git -C $RepoDir rev-parse --verify --quiet "refs/heads/$BaseBranch" *> $null
    $hasLocal = ($LASTEXITCODE -eq 0)

    if (-not $hasRemote) { if ($hasLocal) { return $BaseBranch } else { return $null } }
    if (-not $MergeLocally -or -not $hasLocal) { return "origin/$BaseBranch" }

    $localOnly  = [int](git -C $RepoDir rev-list --count "refs/remotes/origin/$BaseBranch..refs/heads/$BaseBranch" 2>$null)
    $remoteOnly = [int](git -C $RepoDir rev-list --count "refs/heads/$BaseBranch..refs/remotes/origin/$BaseBranch" 2>$null)
    if ($remoteOnly -gt 0 -and $localOnly -eq 0) { return "origin/$BaseBranch" }
    return $BaseBranch
}

# Bring a branch that has nothing of its own up to the base it will be planned against.
#
# This is the ticket that was merged and then sent back: every commit on its branch is already in
# the base, and the base has since gained whatever was merged after it. Planning there means
# planning against code that has been replaced. Nothing is rewritten - a fast-forward only moves
# the branch along commits the base already has - and a branch holding even one commit of its own
# is left exactly where it is, for the rebase before hand-off to deal with.
# Returns how many commits the branch moved; 0 when there was nothing to do.
function Update-MergedBranch {
    param([string]$Path, [string]$BaseRef)

    $own = @(git -C $Path rev-list "$BaseRef..HEAD" 2>$null | Where-Object { $_ }).Count
    if ($LASTEXITCODE -ne 0 -or $own -gt 0) { return 0 }
    $behind = @(git -C $Path rev-list "HEAD..$BaseRef" 2>$null | Where-Object { $_ }).Count
    if ($behind -le 0) { return 0 }

    git -C $Path merge --ff-only $BaseRef *> $null
    if ($LASTEXITCODE -ne 0) { return 0 }
    return $behind
}

# Turn a branch name into a directory name. Branch names carry characters a path cannot ('jb/X'),
# and nesting them as real subdirectories would bury one ticket's worktree inside another's when
# prefixes collide. Flattening keeps every worktree a single, greppable folder under the root.
function Get-WorktreeDirName {
    param([string]$Branch)
    $name = $Branch -replace '[\\/:*?"<>|]', '-'
    $name = $name -replace '\s+', '-'
    return $name.Trim('-', '.')
}

# Map branch name -> worktree path, straight from git. Git is the registry here: it already tracks
# every worktree and the branch each one holds, so we ask it rather than keeping a second list that
# could disagree (the user is free to add, move or remove worktrees by hand at any time).
function Get-WorktreeMap {
    param([string]$RepoDir)
    $map = @{}
    $current = $null
    foreach ($line in (git -C $RepoDir worktree list --porcelain 2>$null)) {
        if ($line -like 'worktree *')   { $current = $line.Substring(9).Trim() }
        elseif ($line -like 'branch *') {
            $ref = $line.Substring(7).Trim()
            if ($ref -like 'refs/heads/*' -and $current) { $map[$ref.Substring(11)] = $current }
        }
    }
    return $map
}

# Prepare a freshly created worktree so it can actually build. A worktree carries only TRACKED
# files, which is exactly right for source and exactly wrong for everything a build needs and
# gitignores: node_modules, restored packages, .env, appsettings.Development.json. Without this a
# worker fails deep into its run, on an error that looks nothing like its real cause.
# Returns $null on success, or a reason string.
function Initialize-Worktree {
    param([string]$Path, [string]$RepoDir, [string[]]$CopyFiles, [string[]]$Setup)

    foreach ($rel in $CopyFiles) {
        if (-not $rel) { continue }
        $src = Join-Path $RepoDir $rel
        if (-not (Test-Path $src)) { Write-Log "    (skip) no '$rel' in $RepoDir"; continue }
        $dest = Join-Path $Path $rel
        $destDir = Split-Path $dest -Parent
        if ($destDir -and -not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        try { Copy-Item -Path $src -Destination $dest -Recurse -Force -ErrorAction Stop; Write-Log "    copied $rel" }
        catch { return "could not copy '$rel' into the worktree: $($_.Exception.Message)" }
    }

    # Package install scripts assume a POSIX toolbox: @shopify/react-native-skia's postinstall runs
    # `rm -rf` and `cp -R` through npm's cmd.exe, and fails on 'rm is not recognized'. A human's
    # install works because their shell has Git's usr\bin on PATH; the worker's does not. APPENDED,
    # not prepended - usr\bin also carries find.exe and sort.exe, which must not shadow Windows' own.
    $gitUsrBin = $null
    $execPath = git --exec-path 2>$null
    if ($execPath) {
        # <git>\mingw64\libexec\git-core -> <git>\usr\bin
        $candidate = Join-Path (Split-Path (Split-Path (Split-Path $execPath -Parent) -Parent) -Parent) 'usr\bin'
        if (Test-Path (Join-Path $candidate 'rm.exe')) { $gitUsrBin = $candidate }
    }

    $failedMarker = Get-SetupFailedMarker -Path $Path
    $savedPath = $env:PATH
    if ($gitUsrBin -and ($env:PATH -split ';') -notcontains $gitUsrBin) { $env:PATH = "$env:PATH;$gitUsrBin" }
    try {
        foreach ($cmd in $Setup) {
            if (-not $cmd) { continue }
            Write-Log "    setup: $cmd"
            Push-Location $Path
            try { & cmd /c $cmd *>&1 | ForEach-Object { Write-Log "      $_" } }
            finally { Pop-Location }
            if ($LASTEXITCODE -ne 0) {
                if ($failedMarker) { Set-Content -Path $failedMarker -Value $cmd -Encoding ascii }
                return "worktree setup command failed (exit $LASTEXITCODE): $cmd"
            }
        }
    }
    finally { $env:PATH = $savedPath }
    if ($failedMarker -and (Test-Path $failedMarker)) { Remove-Item $failedMarker -Force }
    return $null
}

# Where a worktree records that its setup did not finish. It lives in the worktree's own git dir
# (.git\worktrees\<name>), not the tree, so it can never be committed or dirty a status check.
# A marker for FAILURE rather than success, so worktrees made before it existed are not all
# re-set-up at once. Without it a retry finds the worktree registered, treats it as ready, and
# starts a phase on a half-installed node_modules.
function Get-SetupFailedMarker {
    param([string]$Path)
    $gitDir = git -C $Path rev-parse --absolute-git-dir 2>$null
    if (-not $gitDir) { return $null }
    return Join-Path $gitDir 'whetstone-setup-failed'
}

# Give this ticket its own checkout BEFORE the worker runs.
#
# This is the whole point of the worktree model: the worker never touches $RepoDir, which is the
# human's own clone of the same repo. They can sit on any branch, with any amount of uncommitted
# work, and a cycle starting here cannot switch it, dirty it, or care. Every git command below
# either targets the hub read-only (list/fetch/rev-parse) or creates a NEW directory.
#
# Returns a hashtable:
#   ok=$true  + path/branch  -> run the worker in 'path'
#   ok=$true  + deferred     -> not a git repo; run in $RepoDir as before
#   ok=$false + reason       -> caller must block the ticket and skip the worker
# Speculate creates the branch and its worktree; the later phases attach to what already exists.
function Resolve-Worktree {
    param(
        [string]$RepoDir, [string]$WorktreeRoot, [string]$Skill, [string]$TicketId,
        [string]$RecordedBranch, [string]$IssueBranchToken, [string]$BaseBranch, [string]$BranchPrefix,
        [string[]]$CopyFiles, [string[]]$Setup, [string]$ReviewBranch, [bool]$MergeLocally
    )

    # git writes normal progress to stderr; keep that from turning into a terminating error if the
    # caller ever runs under -ErrorAction Stop. Success is judged by $LASTEXITCODE / HEAD, not stderr.
    $ErrorActionPreference = 'Continue'

    if (-not (Test-Path (Join-Path $RepoDir ".git"))) {
        return @{ ok = $true; deferred = $true; reason = "not a git repo" }
    }
    if (-not $WorktreeRoot) {
        return @{ ok = $false; reason = "no worktree root configured for this project and none could be derived from its path" }
    }

    # ---- Review track: somebody else's branch, checked out DETACHED ----------
    #
    # Detached is the whole point, and it is doing three jobs at once. It means no local branch of
    # theirs exists here to accidentally commit onto or push. It sidesteps git's refusal to check
    # out a branch that is already checked out somewhere else, which would otherwise fire whenever
    # the human has that same branch open in their own clone. And it makes the worktree's state
    # obviously not-ours to anything that later looks at it.
    #
    # The worktree is reused across passes rather than rebuilt: preparing one costs an npm ci, and a
    # branch that is reviewed three times as its author responds should pay that once.
    if ($ReviewBranch) {
        $res = Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
            git -C $RepoDir worktree prune *> $null

            $hasOrigin = @(git -C $RepoDir remote 2>$null) -contains 'origin'
            if ($hasOrigin) { git -C $RepoDir fetch origin --prune *> $null }

            # Their branch as origin has it. Never a local ref: a stale local copy of someone else's
            # branch is exactly the thing that would have us review a commit they replaced days ago.
            $startRef = "origin/$ReviewBranch"
            $sha = (git -C $RepoDir rev-parse --verify --quiet $startRef 2>$null)
            if ($LASTEXITCODE -ne 0 -or -not $sha) {
                return @{ ok = $false; reason = "branch '$ReviewBranch' was not found on origin - it may have been renamed, merged or deleted" }
            }
            $sha = $sha.Trim()

            $wtPath = Join-Path $WorktreeRoot (Get-WorktreeDirName "review-$ReviewBranch")

            if (Test-Path $wtPath) {
                $isKnown = $false
                foreach ($line in (git -C $RepoDir worktree list --porcelain 2>$null)) {
                    if ($line -like "worktree *") {
                        $p = $line.Substring(9).Trim()
                        if ($p -and ((Resolve-Path -LiteralPath $p -ErrorAction SilentlyContinue).Path -eq (Resolve-Path -LiteralPath $wtPath -ErrorAction SilentlyContinue).Path)) { $isKnown = $true }
                    }
                }
                if (-not $isKnown) {
                    return @{ ok = $false; reason = "'$wtPath' already exists but git does not know it as a worktree; move or remove it by hand" }
                }

                # A previous review run should have left nothing behind - the leftover commit is
                # skipped on this track on purpose. Anything here means a run edited their code,
                # which is the one thing this phase must never do, so it is reported rather than
                # tidied away.
                $dirty = git -C $wtPath status --porcelain 2>$null
                if ($dirty) {
                    return @{ ok = $false; reason = "the review worktree at '$wtPath' has uncommitted changes - a previous run modified code it was only supposed to read. Inspect and clean it by hand" }
                }

                git -C $wtPath checkout --detach $sha *> $null
                if ($LASTEXITCODE -ne 0) {
                    return @{ ok = $false; reason = "could not move the review worktree at '$wtPath' to $sha" }
                }
                return @{ ok = $true; path = $wtPath; branch = $ReviewBranch; sha = $sha; created = $false }
            }

            if (-not (Test-Path $WorktreeRoot)) { New-Item -ItemType Directory -Path $WorktreeRoot -Force | Out-Null }
            git -C $RepoDir worktree add --detach $wtPath $sha *> $null
            if ($LASTEXITCODE -ne 0) {
                return @{ ok = $false; reason = "could not create a detached worktree for '$ReviewBranch' at '$wtPath'" }
            }
            return @{ ok = $true; path = $wtPath; branch = $ReviewBranch; sha = $sha; created = $true }
        }

        if (-not $res.ok) { return $res }
        if (-not $res.created) {
            $marker = Get-SetupFailedMarker -Path $res.path
            if (-not ($marker -and (Test-Path $marker))) { return $res }
            Write-Log "  Review worktree at $($res.path) never finished setup - running it again"
        }
        else { Write-Log "  Prepared a detached review worktree at $($res.path)" }
        $prepError = Initialize-Worktree -Path $res.path -RepoDir $RepoDir -CopyFiles $CopyFiles -Setup $Setup
        if ($prepError) { return @{ ok = $false; reason = $prepError } }
        return $res
    }

    $recorded = if ($RecordedBranch) { $RecordedBranch.Trim() } else { "" }
    $issue    = if ($IssueBranchToken) { $IssueBranchToken.Trim() } else { "" }
    $branchName = if ($recorded)  { $recorded }
                  elseif ($issue) { "$BranchPrefix$issue" }
                  else            { "${BranchPrefix}ticket-$TicketId" }

    # Everything from here to the HEAD check touches the HUB repo, which every agent working this
    # project shares. `git worktree prune` and `git worktree add` racing each other on one repo can
    # leave a half-registered worktree behind - a state nothing downstream knows how to recover
    # from - so the hub is entered by one agent at a time.
    #
    # The lock is deliberately narrow. The expensive half of preparing a worktree is the setup
    # commands (npm ci is minutes), and those run afterwards, inside this agent's own brand-new
    # directory, where no other agent can see them. Serialising those too would make the second
    # agent's first cycle wait on the first agent's package install for no reason at all.
    $res = Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
        # Clear registrations for worktrees whose directory has been deleted by hand, so a stale
        # entry never blocks re-creating one at the same path.
        git -C $RepoDir worktree prune *> $null

        $existing = (Get-WorktreeMap -RepoDir $RepoDir)[$branchName]
        if ($existing -and (Test-Path $existing)) {
            # Already has a home - including one the user moved or made themselves. Use it where it is.
            $dirty = git -C $existing status --porcelain --untracked-files=no 2>$null
            if ($dirty) {
                return @{ ok = $false; reason = "uncommitted changes in the worktree at '$existing' - refusing to start a phase on a dirty tree" }
            }
            return @{ ok = $true; path = $existing; branch = $branchName; created = $false }
        }

        $wtPath = Join-Path $WorktreeRoot (Get-WorktreeDirName $branchName)
        if (Test-Path $wtPath) {
            # A directory is sitting where the worktree belongs but git does not know it as one.
            # Never delete it - it could be anything, including work someone cared about.
            return @{ ok = $false; reason = "'$wtPath' already exists but is not a registered worktree for '$branchName'; move or remove it by hand" }
        }
        if (-not (Test-Path $WorktreeRoot)) { New-Item -ItemType Directory -Path $WorktreeRoot -Force | Out-Null }

        $hasOrigin = @(git -C $RepoDir remote 2>$null) -contains 'origin'
        $exists = Test-BranchRef -RepoDir $RepoDir -Name $branchName

        if ($exists) {
            # The branch is real but has no worktree: first phase after the model changed, or the
            # user removed the worktree by hand and the ticket came back around. Re-attach it.
            $headHere = (git -C $RepoDir rev-parse --abbrev-ref HEAD 2>$null)
            if ($headHere -eq $branchName) {
                return @{ ok = $false; reason = "branch '$branchName' is checked out in '$RepoDir'; git allows a branch in one worktree only - switch that checkout to another branch first" }
            }
            $startRef = if ($exists -eq "local") { $branchName } else { "origin/$branchName" }
            if ($exists -eq "local") { git -C $RepoDir worktree add $wtPath $branchName *> $null }
            else                     { git -C $RepoDir worktree add -b $branchName $wtPath $startRef *> $null }
            if ($LASTEXITCODE -ne 0) {
                return @{ ok = $false; reason = "could not create a worktree for existing branch '$branchName' at '$wtPath'" }
            }
        }
        elseif ($Skill -eq 'speculate') {
            # Nothing exists yet - this phase owns creating both. Fork from the freshest base we can
            # see, and which ref that is depends on how this project delivers: see Resolve-BaseRef.
            if ($hasOrigin) { git -C $RepoDir fetch origin --prune *> $null }
            $startRef = Resolve-BaseRef -RepoDir $RepoDir -BaseBranch $BaseBranch -MergeLocally $MergeLocally
            if (-not $startRef) { return @{ ok = $false; reason = "base branch '$BaseBranch' not found" } }

            # --no-track matters: forking from origin/<base> otherwise makes git set the new branch's
            # upstream to origin/<base>, which is a lie - this branch has no counterpart on origin,
            # and anything asking "is this branch published?" gets the wrong answer.
            git -C $RepoDir worktree add -b $branchName --no-track $wtPath $startRef *> $null
            if ($LASTEXITCODE -ne 0) {
                return @{ ok = $false; reason = "could not create branch '$branchName' off '$startRef' at '$wtPath'" }
            }
        }
        else {
            # implement / qa-test / refine: never create a branch. No branch = upstream error.
            return @{ ok = $false; reason = "no feature branch for this ticket (expected one from speculation)" }
        }

        $current = (git -C $wtPath rev-parse --abbrev-ref HEAD 2>$null)
        if ($current -ne $branchName) {
            return @{ ok = $false; reason = "worktree at '$wtPath' is on '$current', not '$branchName'" }
        }

        return @{ ok = $true; path = $wtPath; branch = $branchName; created = $true }
    }

    if (-not $res.ok) { return $res }

    # Only where the base is local, and only for the phase that plans. On a pull-request project a
    # branch whose work has already merged is a question for a person (refinement blocks on it);
    # here it is the ordinary case of a merged ticket sent back, and the follow-up simply merges
    # again on top.
    $moved = 0
    if ($MergeLocally -and $Skill -eq 'speculate') {
        $baseRef = Resolve-BaseRef -RepoDir $RepoDir -BaseBranch $BaseBranch -MergeLocally $true
        if ($baseRef) { $moved = Update-MergedBranch -Path $res.path -BaseRef $baseRef }
    }

    if ($moved -gt 0) {
        # Setup runs again: $moved commits can move a lockfile as easily as anything else.
        Write-Log "  Branch '$($res.branch)' had nothing of its own - fast-forwarded $moved commit(s) to $baseRef"
    }
    elseif (-not $res.created) {
        $marker = Get-SetupFailedMarker -Path $res.path
        if (-not ($marker -and (Test-Path $marker))) { return $res }
        Write-Log "  Worktree at $($res.path) never finished setup - running it again"
    }
    else { Write-Log "  Prepared a new worktree at $($res.path)" }
    $prepError = Initialize-Worktree -Path $res.path -RepoDir $RepoDir -CopyFiles $CopyFiles -Setup $Setup
    if ($prepError) { return @{ ok = $false; reason = $prepError } }
    return $res
}

# Commit with the message delivered through a file (-F), never as a -m argument. PowerShell 5.1
# does not escape embedded double quotes when it builds a native command line, so any message
# containing one - and ticket titles and implementation summaries routinely do - splits into
# extra arguments that git reads as pathspecs and fails on ("pathspec ... did not match").
# UTF-8 without BOM: git takes the file's bytes verbatim, and a BOM would land in the subject.
# Returns git's exit code.
function Invoke-GitCommitFromFile {
    param([string]$Message)
    $msgFile = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-commit-" + [guid]::NewGuid().ToString("N") + ".txt")
    [IO.File]::WriteAllText($msgFile, $Message, (New-Object System.Text.UTF8Encoding($false)))
    try {
        git commit -F $msgFile *> $null
        return $LASTEXITCODE
    }
    finally { Remove-Item $msgFile -Force -ErrorAction SilentlyContinue }
}

# Safety net for a worker that finished without committing. The implement skill already requires a
# clean tree before hand-off, but a worker deep in the work can hand off having skipped that step -
# and the failure then surfaces a PHASE LATER, as Resolve-Worktree refusing to start QA on a dirty
# tree. That reads like a QA fault, blocks the ticket, and strands the work uncommitted. Catch it at
# the end of the cycle that caused it instead.
#
# Committing blind is safe here precisely because Resolve-Worktree refuses to START a cycle on a
# dirty tree, and because this worktree belongs to this ticket alone: anything uncommitted now was
# produced by the worker that just ran, on this ticket's own branch. Untracked files are included
# (-A) - a new migration or component is exactly the kind of file that gets left behind. Returns
# $null when the tree was already clean (the normal case).
function Save-WorkerLeftovers {
    param([string]$WorkDir, [string]$TicketId, [string]$Title, [string]$IssueRef, [string]$Skill)

    # git writes progress to stderr; success is judged by $LASTEXITCODE, not by stderr being empty.
    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path (Join-Path $WorkDir ".git"))) { return $null }

    Push-Location $WorkDir
    try {
        $dirty = git status --porcelain 2>$null
        if (-not $dirty) { return $null }

        $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
        $files  = @($dirty | Where-Object { $_ }).Count
        $short  = if ($Title.Length -gt 60) { $Title.Substring(0, 60).TrimEnd() + "..." } else { $Title }

        # The message belongs to the repo's history, so it describes the change and nothing else. How
        # the commit came to be made is a fact about this ticket, not about the code, and it is already
        # recorded where it belongs: the ticket comment posted by the caller.
        $issue  = "$IssueRef".Trim()
        $msg    = if ($issue) { "${issue}: $short" } else { $short }

        git add -A *> $null
        if ((Invoke-GitCommitFromFile -Message $msg) -ne 0) { return @{ ok = $false; files = $files; branch = $branch } }

        return @{ ok = $true; files = $files; branch = $branch; sha = (git rev-parse --short HEAD 2>$null) }
    }
    finally { Pop-Location }
}

# Wrap one paragraph at the width git log renders comfortably. $Hang indents continuation lines, so
# a wrapped bullet stays visually attached to its marker.
function Format-Wrapped {
    param([string]$Text, [int]$Width = 72, [string]$Hang = "")
    $out = @()
    $line = ""
    foreach ($word in ($Text.Trim() -split '\s+')) {
        $candidate = if ($line) { "$line $word" } else { $word }
        if ($candidate.Length -le $Width) { $line = $candidate }
        else { $out += $line; $line = "$Hang$word" }
    }
    if ($line) { $out += $line }
    return ($out -join "`n")
}

# Turn the ticket's implementation summary into a commit body someone will actually read.
#
# The summary is written as one dense paragraph - right for a ticket panel, a wall of text in
# `git log`. The sentences already do different jobs (what it delivers, then how, then what it
# touches), so the first becomes the opening paragraph and the rest become bullets. That is the
# shape the bodies already in this repo use, and it degrades safely: a summary that will not split
# is simply one wrapped paragraph, with every word still present either way.
function Format-CommitBody {
    param([string]$Text, [int]$Width = 72)

    $clean = ($Text -replace "`r`n", " ") -replace "`n", " "
    # Sentence end = .!? then space then a capital. Conservative on purpose: a missed split leaves
    # a longer bullet, while an eager one would cut a sentence in half.
    $sentences = @([regex]::Split($clean.Trim(), '(?<=[.!?])\s+(?=[A-Z])') | Where-Object { $_.Trim() })
    if ($sentences.Count -le 1) { return (Format-Wrapped -Text $clean -Width $Width) }

    $parts = @()
    $parts += (Format-Wrapped -Text $sentences[0] -Width $Width)
    $parts += ""    # blank line between the opening paragraph and the bullets
    foreach ($s in $sentences[1..($sentences.Count - 1)]) {
        $parts += (Format-Wrapped -Text "- $($s.Trim())" -Width $Width -Hang "  ")
    }
    return ($parts -join "`n").Trim()
}

# Would rewriting the commits in <MergeBase>..HEAD destroy history that exists somewhere else?
# Returns the reason it would, or $null when the range is this machine's alone. Must be called with
# the worktree as the current directory.
#
# Shared by the squash net and the rebase below, because both rewrite the same commits and a rule
# that only one of them obeyed would be no rule at all.
#
# The question is whether these COMMITS are published - not whether the branch has an upstream. A
# ticket branch forked from origin/<base> used to get an upstream of origin/<base> automatically, so
# that test called every branch published and the safety net never once fired. An upstream is only
# damning when it names this branch's own counterpart on origin.
function Test-CommitsPublished {
    param([string]$Branch, [string]$MergeBase)

    $upstream = (git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null)
    if ($LASTEXITCODE -eq 0 -and $upstream -eq "origin/$Branch") {
        return "branch tracks its own counterpart on origin"
    }

    $onRemote = git ls-remote --heads origin $Branch 2>$null
    if ($onRemote) { return "branch already exists on origin" }

    # Remote branches are ancestor-closed, so testing the OLDEST commit we would rewrite is the
    # complete test: any remote holding a later one holds this one too.
    $oldest = @(git rev-list --reverse "$MergeBase..HEAD" 2>$null | Where-Object { $_ })[0]
    if ($oldest) {
        $carriers = @(git branch -r --contains $oldest 2>$null | Where-Object { $_ })
        if ($carriers.Count -gt 0) { return "these commits are already on $($carriers[0].Trim())" }
    }

    return $null
}

# Bring a finished ticket's branch up to date with the base it will be merged into.
#
# A branch is cut from the base when the ticket is speculated and never looks at it again, so by
# the time a multi-phase ticket reaches review its base can be days old. The staleness is invisible
# until the merge, which is the worst moment to find it: the ticket has been signed off, the worker
# that wrote the code is long gone, and the conflict lands on a person. Rebasing here means a clean
# branch stays clean and a dirty one is found while there is still a phase that can fix it.
#
# The fetch runs in the WORKTREE rather than the hub. Worktrees share the hub's object store, so it
# updates the same origin/<base> either way - and doing it here means the human's own checkout is
# never touched at all, not even to move a remote-tracking ref. It is still taken under the hub lock:
# the object store and the ref namespace are shared with every other agent on this repo, and two
# fetches racing there is how you get "cannot lock ref" on a run nobody is watching.
#
# Which ref "the base" is comes from Resolve-BaseRef, and every result names it as 'base'. On a
# project that merges locally that is usually the local branch, which is what makes this rebase
# bring in the tickets merged since this one was cut - origin has none of them.
#
# Cheap because of the one-ticket-one-commit rule: by this point the branch is a single commit, so
# the rebase is one patch and a conflict names exactly the files a human would look at anyway.
function Update-BranchFromBase {
    param([string]$WorkDir, [string]$BaseBranch, [string]$RepoDir, [bool]$MergeLocally)

    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path (Join-Path $WorkDir ".git"))) { return $null }

    Push-Location $WorkDir
    try {
        $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
        if (-not $branch -or $branch -eq $BaseBranch) { return $null }
        if (git status --porcelain 2>$null) { return @{ ok = $false; reason = "working tree is not clean" } }

        # A rebase left half-finished by an earlier crash. Touching it would compound the mess.
        foreach ($state in 'rebase-merge', 'rebase-apply') {
            $dir = (git rev-parse --git-path $state 2>$null)
            if ($dir -and (Test-Path $dir)) { return @{ ok = $false; reason = "a rebase is already in progress here" } }
        }

        # No origin means nothing to be stale against - unless the base moves locally, which on a
        # project that merges instead of raising a PR it does every time a ticket lands.
        git remote get-url origin *> $null
        $hasOrigin = ($LASTEXITCODE -eq 0)
        if (-not $hasOrigin -and -not $MergeLocally) { return $null }

        if ($hasOrigin) {
            Invoke-WithMutex -Name "hub-$RepoDir" -TimeoutMs 900000 -Body {
                git fetch origin $BaseBranch --quiet 2>$null
            } | Out-Null
        }

        if ($MergeLocally) { $baseRef = Resolve-BaseRef -RepoDir $WorkDir -BaseBranch $BaseBranch -MergeLocally $true }
        else {
            $baseRef = "origin/$BaseBranch"
            git rev-parse --verify --quiet "refs/remotes/$baseRef" *> $null
            if ($LASTEXITCODE -ne 0) { $baseRef = $null }
        }
        if (-not $baseRef) { return $null }   # no base to measure against

        $behind = [int](git rev-list --count "HEAD..$baseRef" 2>$null)
        if ($behind -le 0) { return @{ ok = $true; behind = 0; rebased = $false; branch = $branch; base = $baseRef } }

        $ahead = [int](git rev-list --count "$baseRef..HEAD" 2>$null)
        if ($ahead -le 0) { return $null }   # nothing of ours to move

        $mergeBase = (git merge-base HEAD $baseRef 2>$null)
        if (-not $mergeBase) { return @{ ok = $false; behind = $behind; base = $baseRef; reason = "no merge base with '$baseRef'" } }

        # A rebase rewrites exactly what a squash rewrites, so it answers to exactly the same rule.
        $published = Test-CommitsPublished -Branch $branch -MergeBase $mergeBase
        if ($published) { return @{ ok = $false; behind = $behind; base = $baseRef; reason = $published } }

        $backup = "refs/heads/$branch-prerebase"
        git update-ref $backup HEAD *> $null

        git rebase $baseRef *> $null
        if ($LASTEXITCODE -ne 0) {
            # Conflicts. Name the files before unwinding - once the rebase is aborted the evidence
            # is gone, and "it conflicts" without saying where is not worth putting on a ticket.
            $conflicts = @(git diff --name-only --diff-filter=U 2>$null | Where-Object { $_ })
            git rebase --abort *> $null
            git reset --hard $backup *> $null
            git update-ref -d $backup *> $null
            return @{ ok = $false; behind = $behind; base = $baseRef; conflicts = $conflicts; reason = "conflicts with $baseRef" }
        }

        git update-ref -d $backup *> $null
        return @{
            ok = $true; behind = $behind; rebased = $true; branch = $branch; base = $baseRef
            sha = (git rev-parse --short HEAD 2>$null)
        }
    }
    finally { Pop-Location }
}

# Collapse a refined ticket's branch to one commit. Refinement is meant to do this itself (its skill
# says so, with this exact recipe) but a worker deep in validating the code reaches the hand-off
# having skipped it - it looked straight at three commits, two of them fix-ups, and moved on. So the
# invariant is enforced here: one ticket, one commit.
#
# This rewrites history, which is only safe under conditions we check rather than assume: a feature
# branch that is not the base, whose commits are on no remote branch and which has no counterpart on
# origin, so nothing published is being rewritten. `reset --soft` cannot change the tree by
# construction - the index and working tree are untouched - and we verify the tree hash across the
# rewrite anyway, restoring from a backup ref if it ever differs. Note that the tree check says
# nothing about WHICH commits were folded in, so the range has to be right independently.
# Returns $null when there was nothing to do.
function Compress-TicketCommits {
    param([string]$WorkDir, [string]$TicketId, [string]$Title, [string]$IssueRef, [string]$BaseBranch, [string]$Summary, [bool]$MergeLocally)

    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path (Join-Path $WorkDir ".git"))) { return $null }

    Push-Location $WorkDir
    try {
        $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
        if (-not $branch -or $branch -eq $BaseBranch) { return $null }
        if (git status --porcelain 2>$null) { return @{ ok = $false; reason = "working tree is not clean" } }

        # Resolve the base the same way Resolve-Worktree forks from it. On a pull-request project
        # that is origin/<base> when we have it: the local ref lives in the human's checkout and is
        # routinely stale - measuring against it puts every commit origin has gained since their
        # last pull inside the squash range, which is not a cosmetic difference. On the branch that
        # prompted this, that was 14 published commits about to be folded into one, and
        # `reset --soft` preserves the tree either way, so the tree-hash check would have waved it
        # straight through.
        #
        # On a project that merges locally the same mistake runs the other way. A ticket merged
        # into local <base> and then sent back has commits that <base> already contains and origin
        # has never seen; measured against origin they are all in range, and squashing them
        # rewrites history the base holds, so the next merge brings the feature in a second time.
        # Measured against the local ref the merge base is the last merged commit and the range is
        # only what is new.
        $baseRef = Resolve-BaseRef -RepoDir $WorkDir -BaseBranch $BaseBranch -MergeLocally $MergeLocally
        if (-not $baseRef) { return @{ ok = $false; reason = "base branch '$BaseBranch' not found" } }

        $mergeBase = (git merge-base HEAD $baseRef 2>$null)
        if (-not $mergeBase) { return @{ ok = $false; reason = "no merge base with '$baseRef'" } }

        $subjects = @(git log --format=%s "$mergeBase..HEAD" 2>$null | Where-Object { $_ })
        if ($subjects.Count -lt 2) { return $null }   # already one commit - nothing to tidy

        $published = Test-CommitsPublished -Branch $branch -MergeBase $mergeBase
        if ($published) { return @{ ok = $false; reason = $published } }

        $treeBefore = (git rev-parse "HEAD^{tree}" 2>$null)
        $backup = "refs/heads/$branch-presquash"
        git update-ref $backup HEAD *> $null

        # The body has to describe the branch as a whole, the way a reader of the history needs it -
        # a list of the commits it replaced describes the journey, which is exactly what squashing is
        # meant to throw away. The ticket's implementation summary already IS that description (kept
        # current by every implementation pass), so use it. Only when there is none do we fall back
        # to the old subjects, on the grounds that some record beats none.
        $issue = "$IssueRef".Trim()
        $subject = if ($issue) { "${issue}: $($Title.Trim())" } else { $Title.Trim() }
        if ($Summary -and $Summary.Trim()) {
            $body = Format-CommitBody -Text $Summary.Trim()
        }
        else {
            [array]::Reverse($subjects)   # oldest first: the original work, then the fix-ups on it
            $body = "No implementation summary was recorded for this ticket. It squashes:`n" +
                    (($subjects | ForEach-Object { "- $_" }) -join "`n")
        }
        $message = "$subject`n`n$body"

        git reset --soft $mergeBase *> $null
        if ((Invoke-GitCommitFromFile -Message $message) -ne 0) {
            git reset --hard $backup *> $null
            git update-ref -d $backup *> $null
            return @{ ok = $false; reason = "the squash commit failed; branch restored" }
        }

        $treeAfter = (git rev-parse "HEAD^{tree}" 2>$null)
        if ($treeBefore -ne $treeAfter) {
            git reset --hard $backup *> $null
            git update-ref -d $backup *> $null
            return @{ ok = $false; reason = "squash would have changed the tree; branch restored untouched" }
        }

        git update-ref -d $backup *> $null
        return @{ ok = $true; from = $subjects.Count; branch = $branch; sha = (git rev-parse --short HEAD 2>$null) }
    }
    finally { Pop-Location }
}

# ---- Reflection ------------------------------------------------------------------------------

# The output contract for reflection, owned HERE rather than in each project's reflect-prompt.md.
#
# The format is the orchestrator's business: this script is what has to parse the answer, route it,
# and refuse it when it is not an answer at all. A per-project file that could describe a different
# shape is a per-project file that can silently break the parser. What stays project-owned is what
# to LOOK FOR, which is the half that genuinely differs between repos.
#
# The examples are drawn from this system's own history: the good ones are real facts that saved a
# later run, the bad ones are real suggestions that were filed ~50 times and applied zero times.
$ReflectOutputContract = @'

---

# OUTPUT CONTRACT (this section overrides any output format described above)

Reply with a JSON array and NOTHING else - no preamble, no explanation, no markdown fences. An empty
array `[]` is the correct and common answer; most runs teach nothing new, and inventing a finding to
fill the silence is worse than silence.

Each element:

  {
    "kind":       "environment" | "codebase" | "process",
    "key":        "short-kebab-case-id",
    "text":       "One or two sentences. Specific enough to act on without this run's context.",
    "confidence": "high" | "low"
  }

- "environment" - a durable fact about THIS machine or stack: a port, a path, a credential location,
  a tool that hangs, a container that needs a flag.
- "codebase"    - a durable fact about THIS repo: where a thing lives, a convention that is enforced,
  a file format requirement, a build step that is required or pointless.
- "process"     - a change to how the heartbeat itself works (a skill, a prompt, this script).

WHICH KIND IS IT? One question decides:

  Would a human developer, or an interactive Claude session in this repo, also be wrong
  without this fact?

- YES - it is "environment" or "codebase". That is knowledge about the repo and the machine; it is
  written into the repo's own notes store, where every future run, every interactive session and
  every human reads it.
- Only the unattended pipeline would be wrong - it is "process". That is not knowledge, it is a
  change to a skill or a prompt, and it goes to a queue a human reviews.

Do not file a repo fact as "process" because it happened to bite an automated run, and do not file a
workflow change as "codebase" because it involves this repo. The test is only ever who else is wrong.
- "confidence"  - about YOUR EVIDENCE, and nothing else:
    "high" - you saw this happen in this run's log. You are reporting an observation.
    "low"  - you are inferring it, generalising from one case, or reconstructing what probably
             happened. You are reporting a guess.
  Do not lower it because the fact might get fixed later, or because you are unsure how long it will
  hold. How durable a fact is, is not something you can know from one run, and it is not what this
  field is for - a bug you watched happen is "high" even though someone may fix it tomorrow. Judging
  durability here is what used to send first-hand discoveries to a human review queue at random.
- "key"         - a stable identifier for the fact, so the same discovery made twice is recognised as
  one fact. Name the thing, not the incident: "migration-files-need-bom", not "bom-issue-today".

WORTH REPORTING (real examples from this system):
  - migration .cs files must start with a UTF-8 BOM to match the existing convention
  - the DB was at compatibility level 150, which is why GENERATE_SERIES failed
  - Postman assertions must use PascalCase against the Conversion API
  - query the DB from the host with sqlcmd; docker exec sqlcmd is the slow path

NOT WORTH REPORTING (all of these were actually filed, and all were useless):
  - "avoid duplicate searches in the same session"
  - "read a file once and keep it in context rather than re-reading"
  - "maintain awareness of files already examined"
  - "be more careful", "plan better", or any restatement of ordinary tool hygiene
  - anything already listed under KNOWN FACTS below
  - anything about the business logic of the code itself - that belongs in the ticket, not here

If you have nothing that clears that bar, reply exactly: []
'@

# Strip a project's legacy output-format section, keeping only its "what to look for" guidance.
# The contract now lives in this script (see $ReflectOutputContract), and leaving the old one in
# place would hand the model two different formats and let it pick.
function Get-ReflectGuidance {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }
    $text = Get-Content -Path $Path -Raw
    $idx = $text.IndexOf("`n## Output Format")
    if ($idx -ge 0) { $text = $text.Substring(0, $idx) }
    return $text.TrimEnd()
}

# Every fact ever accepted for this project, one key per line. Dedup is mechanical for a reason:
# the old prompt handed the model the whole lessons file and asked it not to repeat anything, and
# it repeated things anyway - "avoid duplicate searches" was filed three separate times.
function Get-ReflectKeys {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return @() }
    return @(Get-Content -Path $Path | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
}

# Pull the JSON array out of a model reply that may have wrapped it in fences or chat.
# Returns $null when there is no array to be found - which is how a rate-limit message, a crash
# dump, or an apology gets thrown away instead of appended to the suggestions file as though it
# were a suggestion. ("You've hit your limit - resets 11:30am" is sitting in that file today.)
function ConvertFrom-ReflectOutput {
    param([string]$Text)
    if (-not $Text) { return $null }
    $start = $Text.IndexOf('[')
    $end   = $Text.LastIndexOf(']')
    if ($start -lt 0 -or $end -le $start) { return $null }
    try { return @($Text.Substring($start, $end - $start + 1) | ConvertFrom-Json) }
    catch { return $null }
}

# Write-MemoryNote moved to notes-store.ps1 when reflection gained a third outcome. Writing a fact
# is no longer a one-liner appended to a file: it has to find the note that fact belongs to, decide
# between creating and merging, keep the index in step, and leave a git trail. That is a subject of
# its own, and the heartbeat's maintenance pass needs the same functions.


# Per-project reflection: mine the run for durable facts (best-effort, time-boxed).
# The reflect prompt and the review queue live in the dashboard-owned project data dir; accepted
# facts go to $MemoryDir, the notes store inside the project's own repo. The reflection claude call
# runs in $RepoDir (the worktree the run just used).
function Invoke-Reflection {
    param($ProjectDataDir, $RepoDir, $RunLog, $MemoryDir, $TicketId)

    if (-not $ProjectDataDir) { return }
    # The project's reflect-prompt.md is still the switch: delete it and this project stops
    # reflecting. Its contents are now guidance only.
    $reflectFile = Join-Path $ProjectDataDir "reflect-prompt.md"
    if (-not (Test-Path $reflectFile)) { return }
    if ($RunLog.Count -le 5) { return }

    Write-Log "--- Reflection ---"
    try {
        $suggestionsFile = Join-Path $ProjectDataDir "improvement-suggestions.md"
        $keysFile        = Join-Path $ProjectDataDir "reflection-keys.txt"
        $memoryIndex     = if ($MemoryDir) { Join-Path $MemoryDir "MEMORY.md" } else { $null }

        $guidance = Get-ReflectGuidance -Path $reflectFile

        # A real List, not a PowerShell array: `@()` returned from a function collapses to $null,
        # and a one-line file collapses to a bare string, either of which turns `+=` into string
        # concatenation and makes every duplicate check quietly answer "no". That exact bug let a
        # reply file the same fact twice in one pass.
        $knownKeys = New-Object System.Collections.Generic.List[string]
        foreach ($k in (Get-ReflectKeys -Path $keysFile)) { [void]$knownKeys.Add($k) }
        # The notes store is the other half of "already known": a fact a human wrote by hand, or one
        # filed before this project kept a key list, exists only as a filename. Without this, every
        # fact the interactive sessions already recorded is fair game to be discovered again.
        if ($MemoryDir -and (Test-Path $MemoryDir)) {
            foreach ($f in (Get-ChildItem -Path $MemoryDir -Filter *.md -File -ErrorAction SilentlyContinue)) {
                $stem = $f.BaseName.ToLowerInvariant()
                if ($stem -ne 'memory' -and -not $knownKeys.Contains($stem)) { [void]$knownKeys.Add($stem) }
            }
        }

        # The index, not the notes themselves: it is one line per fact, so it fits, and a reflection
        # that can see the titles of everything already recorded stops rediscovering them.
        $lessonsContent = if ($memoryIndex -and (Test-Path $memoryIndex)) { Get-Content -Path $memoryIndex -Raw } else { "(none)" }

        $logForReflect = $RunLog
        if ($logForReflect.Count -gt 200) { $logForReflect = $logForReflect[-200..-1] }

        $knownBlock = if ($knownKeys.Count -gt 0) { (($knownKeys | ForEach-Object { "- $_" }) -join "`n") } else { "(none yet)" }
        $reflectPrompt = @(
            $guidance,
            $ReflectOutputContract,
            "`n---`n`n# KNOWN FACTS (already recorded - do not report these again)`n`n$knownBlock",
            "`n---`n`n# THE REPO'S NOTES STORE (index of everything already recorded)`n`n$lessonsContent",
            "`n---`n`n# RUN LOG TO ANALYSE`n`n" + ($logForReflect -join "`n")
        ) -join "`n"

        # Per-ticket, not one shared name: three agents reflecting at once would otherwise overwrite
        # each other's prompt between the write and the read, and the reply would be about someone
        # else's run.
        $tempReflectFile = Join-Path $env:TEMP "claude-reflect-prompt-$TicketId.md"
        Set-Content -Path $tempReflectFile -Value $reflectPrompt -Encoding UTF8
        # NB: a Start-Job runs in a separate process and does NOT inherit script variables, so the
        # whole command has to be passed in (not rebuilt from $AgentProvider inside the block, which
        # is not defined there - providers.ps1 is not sourced in a job).
        $reflectCmd = Get-AgentReflectionCommand -Provider $AgentProvider -Model $ReflectionModel `
            -ReasoningEffort $WorkerReasoningEffort
        $reflectJob = Start-Job -ScriptBlock {
            param($file, $dir, $cmd)
            # A job is its own process and inherits none of the parent's encoding setup, so the
            # UTF-8 fix has to be repeated here or this reply comes back mangled on its own.
            try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
            $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            Set-Location $dir
            # Re-cast on the way out of serialisation: what crosses a job boundary is a deserialised
            # PSObject, and a native command's argv is the wrong place to discover that.
            $exe     = [string]$cmd.Exe
            $cmdArgs = @($cmd.Args | ForEach-Object { [string]$_ })
            # Stdout and stderr are told apart rather than folded together. The CLI warns on stderr
            # about things that are none of this call's business (a permission rule it will not
            # match, a workspace it does not trust), and 5.1 renders each such line as an error
            # record ending "[], RemoteException" - a bracket AHEAD of the reply, which the parser
            # took for the start of the array. Every reflection was discarded for days on that.
            # Stderr is still handed back when there is no reply at all, so a call that failed
            # outright says why in the log.
            $said   = New-Object System.Collections.Generic.List[string]
            $warned = New-Object System.Collections.Generic.List[string]
            Get-Content -Path $file -Raw | & $exe @cmdArgs 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) { $warned.Add([string]$_) }
                else { $said.Add([string]$_) }
            }
            $reply = ($said -join "`n").Trim()
            if ($reply) { $reply } else { $warned -join "`n" }
        } -ArgumentList $tempReflectFile, $RepoDir, $reflectCmd

        if (Wait-Job $reflectJob -Timeout 120) {
            $reflectOutput = (Receive-Job $reflectJob) -join "`n"
        }
        else {
            Write-Log "  Reflection timed out - skipping"
            Stop-Job $reflectJob
            Stop-DescendantAgent -RootPid $PID -Pattern $AgentProvider.ProcessPattern
            $reflectOutput = ""
        }
        Remove-Job $reflectJob -Force -ErrorAction SilentlyContinue
        Remove-Item $tempReflectFile -Force -ErrorAction SilentlyContinue

        $findings = ConvertFrom-ReflectOutput -Text $reflectOutput
        if ($null -eq $findings) {
            # Not an answer. Whatever it is - a rate-limit notice, a timeout, an apology - it is not
            # a finding, and the one thing never to do with it is write it into the file a human
            # reads as a list of findings.
            $preview = ("$reflectOutput" -replace "\s+", " ").Trim()
            if ($preview.Length -gt 120) { $preview = $preview.Substring(0, 120) + "..." }
            if ($preview) { Write-Log "  Reflection produced no parseable findings - discarded: $preview" }
            else { Write-Log "  Reflection produced no output" }
            return
        }

        # Filing happens in two phases, split by what has to be serialised.
        #
        # Phase one claims keys under a per-project lock, and re-reads the key list inside it. Two
        # agents on the same project finish within seconds of each other often enough, and the key
        # list read before the model was called is by then several minutes stale: without this they
        # both find the same fact "new" and file it twice, which is the exact failure the mechanical
        # dedup exists to prevent. It does no I/O beyond the key file, so the lock is held briefly.
        #
        # Phase two places each claimed fact, and deliberately runs OUTSIDE that lock, because
        # placing a fact can now cost an agent call of its own. Holding a project-wide lock across
        # two minutes of model time would serialise reflection across the whole fleet - the same
        # reason the reflection call itself has always been outside it. What phase two touches is
        # protected at a finer grain instead: the index has its own mutex, note files are written one
        # key at a time, and the git snapshot serialises on the store.
        $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
        $claim = Invoke-WithMutex -Name "reflect-$ProjectDataDir" -TimeoutMs 60000 -Body {
            $accepted  = New-Object System.Collections.Generic.List[object]
            $duplicate = 0

            $live = New-Object System.Collections.Generic.List[string]
            foreach ($k in (Get-ReflectKeys -Path $keysFile)) { [void]$live.Add($k) }
            foreach ($k in $knownKeys) { if (-not $live.Contains($k)) { [void]$live.Add($k) } }

            foreach ($f in $findings) {
                $key  = "$($f.key)".Trim().ToLowerInvariant()
                $text = ("$($f.text)" -replace "\s+", " ").Trim()
                if (-not $text) { continue }
                if (-not $key) { $key = ($text.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-') }
                if ($key.Length -gt 60) { $key = $key.Substring(0, 60) }

                if ($live.Contains($key)) { $duplicate++; continue }

                [void]$accepted.Add([pscustomobject]@{
                    Key        = $key
                    Text       = $text
                    Kind       = "$($f.kind)".Trim().ToLowerInvariant()
                    Confidence = "$($f.confidence)".Trim().ToLowerInvariant()
                })

                Add-Utf8Text -Path $keysFile -Text ($key + [Environment]::NewLine)
                [void]$live.Add($key)   # so a reply that repeats itself cannot file the same fact twice
            }

            return @{ accepted = $accepted; duplicate = $duplicate }
        }

        $promoted = 0; $merged = 0; $filed = 0
        # .Count on the list itself: @() around a generic List throws "Argument types do not match"
        # on 5.1, and it threw AFTER phase one had claimed the keys - so the facts were lost and
        # marked as known in the same breath.
        if ($claim.accepted.Count -gt 0 -and $MemoryDir) { [void](Initialize-NoteStoreGit -MemoryDir $MemoryDir) }

        foreach ($a in $claim.accepted) {
            # The routing rule, and the only place it is enforced rather than described.
            #
            # A first-hand fact about the machine or the repo is knowledge: a human or an interactive
            # session would be just as wrong without it, so it belongs in the repo's notes store
            # where all three readers find it. Writing it automatically is the whole point - the
            # review list once sat at ~50 unactioned items, so anything that only lands there lands
            # nowhere.
            #
            # Everything else - anything about how the pipeline itself should behave, and anything
            # the model is only inferring rather than reporting - is a proposal, not a fact, and it
            # waits for a human.
            $landed = $false
            if ($a.Confidence -eq 'high' -and ($a.Kind -eq 'environment' -or $a.Kind -eq 'codebase') -and $MemoryDir) {

                # Does this already have a home? The old code asked only whether the KEY existed,
                # which is why a fact already written up under a different name was filed for a
                # human to notice by hand. Find-RelatedNotes proposes; Resolve-FactPlacement, which
                # is the half that reads actual prose, decides - and answers 'new' for free when
                # there is nothing close enough to be worth an agent call.
                $candidates = @(Find-RelatedNotes -MemoryDir $MemoryDir -Text $a.Text)
                $placement  = @{ Outcome = 'new' }
                if ($candidates.Count -gt 0) {
                    $placement = Resolve-FactPlacement -MemoryDir $MemoryDir -Text $a.Text `
                        -Candidates $candidates -Stamp $stamp -ReflectCmd $reflectCmd -RepoDir $RepoDir
                }

                if ($placement.Outcome -eq 'merged' -or $placement.Outcome -eq 'appended') {
                    Write-Log "  Reflection: folded '$($a.Key)' into $($placement.Key) ($($placement.Outcome))"
                    $merged++
                    $landed = $true
                }
                elseif (Write-MemoryNote -MemoryDir $MemoryDir -Key $a.Key -Text $a.Text -Stamp $stamp) {
                    $promoted++
                    $landed = $true
                }
            }

            if (-not $landed) {
                # Also the fallback when there is no store to write to (a project with no repo), so a
                # fact is never silently dropped for want of somewhere to put it.
                Add-Utf8Text -Path $suggestionsFile -Text ("`n- [ ] [$($a.Kind)] $($a.Text)  (key: $($a.Key), $stamp)`n")
                $filed++
            }
        }

        if ($MemoryDir -and ($promoted -or $merged)) {
            Save-NoteStoreSnapshot -MemoryDir $MemoryDir -Message "Reflection after $TicketId : $promoted new, $merged merged"
        }

        if ($promoted -or $merged -or $filed -or $claim.duplicate) {
            Write-Log "  Reflection: $promoted written to notes, $merged folded into existing notes, $filed filed for review, $($claim.duplicate) already known"
        }
        else { Write-Log "  Reflection: nothing new" }

        # Consolidation rides on the back of reflection because this is where the agent command and
        # the worktree are already set up, and because reflection is already outside the measured
        # window. It is a no-op unless the index has crossed its line limit and a day has passed, so
        # the common case costs one file read.
        Invoke-NoteStoreMaintenance -MemoryDir $MemoryDir -ProjectDataDir $ProjectDataDir `
            -ReflectCmd $reflectCmd -RepoDir $RepoDir
    }
    catch { Write-Log "  Reflection error: $($_.Exception.Message)" }
}

# ---- The cycle -------------------------------------------------------------------------------

$ticketId    = "$($Claim.ticket.id)"
$phase       = "$($Claim.ticket.phase)"
$title       = "$($Claim.ticket.title)"
$projectKey  = "$($Claim.project.key)"
$projectName = "$($Claim.project.displayName)"
$projectPath = "$($Claim.project.projectPath)"
$skill       = "$($Claim.skill)"
# Delivery (our own work) or Review (somebody else's branch). Everything downstream that must not
# write to a branch we do not own keys off this: the worktree is checked out detached, the leftover
# commit is skipped, and the prompt gains a section that overrides the commit rule.
$track       = "$($Claim.ticket.track)".Trim()
if (-not $track) { $track = "Delivery" }
$isReview    = ($track -eq "Review")
# The branch under review lives on the review target, not on ticket.branch - a review ticket never
# owns a branch. Empty for a delivery ticket.
$reviewBranch = "$($Claim.ticket.review.sourceBranch)".Trim()
# The model this phase runs on: $WorkerModel unless phaseModels overrides it for this skill.
# Resolved once, here, so the command line, the cycle report and the log all name the same thing -
# a metrics row that disagreed with what actually ran would be worse than no row at all.
$phaseModel  = Get-PhaseModel -Skill $skill
$skillFile   = "$($Claim.project.skillFile)"
$memoryDir   = "$($Claim.project.memoryDir)"
$memoryIndex = "$($Claim.project.memoryIndex)"
$resumed     = if ($Claim.resumed) { "yes" } else { "no" }
# Human-authored standing instructions for this ticket. Injected into EVERY phase's prompt
# (not left to the worker to fetch) so it is always in context and always outranks the
# description. Claude has no tool that can write this back.
$masterNotes = "$($Claim.ticket.masterNotes)".Trim()
if ($masterNotes) {
    $masterNotesBlock = "The human owner left MASTER NOTES on this ticket. They are the HIGHEST AUTHORITY: where they conflict with the ticket description, the linked issue, or the skill's defaults, the master notes win. Re-read them before each step and make sure your work respects them.`n`n<master_notes>`n$masterNotes`n</master_notes>"
} else {
    $masterNotesBlock = "(none on this ticket)"
}
# What moved in the linked issue since this ticket was speculated. The dashboard computes
# and renders it at claim time; a phase that had to remember to go and check would miss one.
$issueChanges = "$($Claim.issueChanges)".Trim()
if (-not $issueChanges) { $issueChanges = "(no tracker baseline for this ticket - nothing to compare against)" }
# The answer to a question an earlier run asked by blocking the ticket, rendered by the dashboard at
# claim time. Present only on the run immediately after a human unblocked it - see
# TicketService.LastBlockEpisodeAsync - so it is the run that can actually use it.
$blockNote = "$($Claim.blockNote)".Trim()
if (-not $blockNote) { $blockNote = "(this ticket has not been blocked and unblocked - nothing to catch up on)" }
# Notes the ticket's owner left that no run has answered yet, rendered by the dashboard at claim
# time (TicketService.RenderOwnerNotes) with the local path of anything they attached. Delivered
# here rather than left in the comment timeline for the same reason the block answer is: a run that
# has to remember to go looking for one is a run that will miss it. Every note listed here holds
# this phase's hand-off until acknowledge_note clears it.
$ownerNotes = "$($Claim.ownerNotes)".Trim()
if (-not $ownerNotes) { $ownerNotes = "(the owner has left no unanswered notes on this ticket - nothing to answer)" }
# The Review track's standing override. Empty on a delivery run, so that pipeline reads exactly as
# it always has. It is a whole section rather than a line because it has to REPEAL two of the hard
# rules below it - the one that says commit whatever you leave behind, and the one that describes
# the worktree as this ticket's own branch - and a rule contradicted in passing is a rule the model
# gets to choose about.
if ($isReview) {
    $trackRules = @"
**This ticket is on the REVIEW track. The branch in your working directory belongs to ANOTHER DEVELOPER.**

You are reading ``$reviewBranch`` to give its author feedback. You are not building anything.

- **Change nothing.** Not a file, not a line, not an obvious one-character fix. Everything you would
  have fixed becomes an ``add_review_comment`` instead, addressed to the person who wrote it.
- **This overrides the "Leave the worktree clean" rule below.** That rule tells you to commit
  whatever you have changed. Here there is nothing to commit, because you change nothing. Your
  worktree is checked out DETACHED - there is no branch of theirs here to commit onto - and the
  heartbeat will not commit for you. A dirty tree at the end of this run is reported on the ticket
  as a fault, not tidied away.
- **Never ``git commit``, ``git push``, ``git checkout``, ``git switch``, ``git rebase`` or ``git reset``.**
  The dashboard refuses to push, rebase or force-push this branch at all; do not try to route around it.
- **Nothing you write is sent anywhere.** ``add_review_comment`` and ``set_review_verdict`` stage
  feedback on the local ticket. The ticket's owner reads it, edits it, and sends what they choose
  under their own name. There is no tool here that posts to the pull request, on purpose.
- **Finding defects is SUCCESS.** This phase has one forward hand-off and you take it whatever you
  found. There is no route back to implementation - the code is not ours to fix.
"@
} else {
    $trackRules = ""
}
# Dashboard-owned per-project data dir (holds reflect-prompt and the review queue).
$projectDataDir = "$($Claim.project.dataDir)"
if (-not $projectDataDir) { $projectDataDir = $null }
# Set only when this run is on a worker host - another machine than the board's, started by its
# runner (data/runner.ps1), which stamps the host's name and rewrites the claim's paths to its own
# clone before this script sees it. Everything that differs on a worker host keys off $onHost, so a
# claim without it - every board host run - takes none of those paths. See docs/worker-hosts-plan.md.
$runHost = "$($Claim.host)".Trim()
$onHost  = [bool]$runHost
$runId   = "$($Claim.runId)".Trim()

Write-Log "--- Working ticket #$ticketId [$projectKey] phase=$phase skill=$skill model=$phaseModel resumed=$resumed ---"
Send-DashboardEvent -Type "cycle_start" -TicketId $ticketId -Phase $phase -Project $projectKey

# Wall clock for the WHOLE cycle, started before the worktree is prepared. On a project whose
# setup runs npm ci that preparation is minutes of real time the model's own duration never
# sees, and it is real time this agent cannot spend on another ticket.
$cycleStartedAt = (Get-Date).ToUniversalTime()
$cycleTimer     = [System.Diagnostics.Stopwatch]::StartNew()

# The repo this project points at. This is the HUB, and the human's own checkout of it - the
# worker never runs here. It runs in a worktree hanging off it (resolved just below), so the
# human can stay on whatever branch they like, with whatever uncommitted work they like.
$repoDir = if ($projectPath -and (Test-Path $projectPath)) { $projectPath } else { $null }
$workDir = if ($repoDir) { $repoDir } else { $DataRoot }
if (-not $repoDir) {
    Write-Log "  WARNING: project path '$projectPath' not found - running from data root."
}

# Per-project settings, falling back to the global defaults when unset.
$projectBase     = "$($Claim.project.baseBranch)".Trim()
$effectiveBase   = if ($projectBase) { $projectBase } else { $BaseBranch }
$projectPrefix   = "$($Claim.project.branchPrefix)".Trim()
$effectivePrefix = if ($projectPrefix) { $projectPrefix } else { $BranchPrefix }
# Delivery by a local merge rather than a pull request, which decides what "the base" is - see
# Resolve-BaseRef. The claim says so; one written by an app not yet restarted onto this does not
# carry the field at all, and the skills already assume the answer, so that case reads the
# project's own two files the way the registry does (project.json over project.defaults.json).
$mergeLocally    = ($Claim.project.mergeInsteadOfPr -eq $true)
if ($null -eq $Claim.project.mergeInsteadOfPr -and $Claim.project.dataDir) {
    foreach ($name in 'project.defaults.json', 'project.json') {
        $file = Join-Path "$($Claim.project.dataDir)" $name
        if (-not (Test-Path $file)) { continue }
        try {
            $value = (Get-Content -Path $file -Raw | ConvertFrom-Json).mergeInsteadOfPr
            if ($null -ne $value) { $mergeLocally = ($value -eq $true) }
        }
        catch { }
    }
}

# A run that cannot start, said once: a comment, a cycle row, the claim given back. -Block when it
# needs a person; otherwise the ticket is simply left for the next claim, on whichever machine.
function Stop-CycleEarly {
    param([string]$Reason, [string]$Comment, [switch]$Block)
    Write-Log "  $Reason - $(if ($Block) { 'blocking' } else { 'giving back' }) ticket #$ticketId."
    if ($Block) { Block-Ticket -TicketId $ticketId -Reason $Reason }
    Send-TicketComment -TicketId $ticketId -Body $Comment
    Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error $Reason
    # Still a cycle: it consumed wall clock and produced nothing. A failure that leaves no row
    # behind is a failure the metrics will report as a pipeline that never stumbles.
    $cycleTimer.Stop()
    Send-CycleReport -TicketId $ticketId -Project $projectKey -Phase $phase -Skill $skill `
        -Resumed ([bool]$Claim.resumed) -Model $phaseModel -StartedAt $cycleStartedAt `
        -DurationSeconds $cycleTimer.Elapsed.TotalSeconds -Mutations 0 -Outcome "Error"
    Send-LeaseRelease -TicketId $ticketId -Reason $Reason
    Send-DashboardEvent -Type "cycle_end" -TicketId $ticketId -Project $projectKey
    exit 0
}

# On a worker host the clone is brought up to the board host's refs before the worktree is
# resolved: its base, its view of origin, and this ticket's branch as the board host last had it.
# That commit is also what the hand-back at the end says it expects to replace. A board host it
# cannot reach is not the ticket's fault, so the claim is given back rather than the ticket
# blocked; a branch that cannot be brought level without losing work is, and needs a person.
$boardStartSha = ''
if ($onHost -and $repoDir -and -not $isReview) {
    $sync = Sync-BoardRefs -RepoDir $repoDir -BaseBranch $effectiveBase
    if (-not $sync.ok) {
        Stop-CycleEarly -Reason "Could not sync with the board host ($runHost): $($sync.reason)" `
            -Comment "This run was on worker host ``$runHost`` and could not bring its clone up to the board host's repo: $($sync.reason). No work was attempted; the ticket goes back to the queue."
    }
    $recordedBranch = "$($Claim.ticket.branch)".Trim()
    if ($recordedBranch) {
        $got = Get-BoardBranch -RepoDir $repoDir -Branch $recordedBranch
        if (-not $got.ok) {
            Stop-CycleEarly -Reason "Could not fetch '$recordedBranch' from the board host ($runHost): $($got.reason)" `
                -Comment "This run was on worker host ``$runHost`` and could not fetch this ticket's branch from the board host: $($got.reason). No work was attempted; the ticket goes back to the queue."
        }
        $boardStartSha = $got.sha
        $level = Update-BranchFromBoard -RepoDir $repoDir -Branch $recordedBranch -BoardSha $got.sha
        if (-not $level.ok) {
            Stop-CycleEarly -Block -Reason "Branch '$recordedBranch' on worker host $runHost cannot be brought level with the board host: $($level.reason)" `
                -Comment "This run was on worker host ``$runHost``, whose copy of ``$recordedBranch`` cannot be brought level with the board host's without losing work: $($level.reason). Ticket blocked; no work was attempted. Compare the two machines' branches and keep the one that is right, then unblock."
        }
        Write-Log "  Worker host $runHost - '$recordedBranch' against the board host: $($level.state)"
    }
}

# Give the ticket its own checkout before anything else: the MCP config below points Augment at
# the working directory, so the working directory has to be settled first.
if ($repoDir) {
    $wt = Resolve-Worktree -RepoDir $repoDir -WorktreeRoot "$($Claim.project.worktreeRoot)".Trim() `
        -Skill $skill -TicketId $ticketId -RecordedBranch "$($Claim.ticket.branch)" `
        -IssueBranchToken "$($Claim.issueBranchToken)" -BaseBranch $effectiveBase -BranchPrefix $effectivePrefix `
        -CopyFiles @($Claim.project.worktreeCopyFiles) -Setup @($Claim.project.worktreeSetup) `
        -ReviewBranch $(if ($isReview) { $reviewBranch } else { "" }) -MergeLocally $mergeLocally

    if (-not $wt.ok) {
        $reason = "Workspace setup failed ($skill): $($wt.reason)"
        Write-Log "  $reason - blocking ticket #$ticketId."
        Block-Ticket -TicketId $ticketId -Reason $reason
        Send-TicketComment -TicketId $ticketId -Body "Heartbeat could not prepare a git worktree for this ticket: $($wt.reason). Ticket blocked; no work was attempted."
        Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error $reason
        # Still a cycle: it consumed wall clock and produced nothing. A failure that leaves no
        # row behind is a failure the metrics will report as a pipeline that never stumbles.
        $cycleTimer.Stop()
        Send-CycleReport -TicketId $ticketId -Project $projectKey -Phase $phase -Skill $skill `
            -Resumed ([bool]$Claim.resumed) -Model $phaseModel -StartedAt $cycleStartedAt `
            -DurationSeconds $cycleTimer.Elapsed.TotalSeconds -Mutations 0 -Outcome "Error"
        # No work happened, so anything the claim reserved has to go back immediately - a
        # workspace failure must not leave the QA stack held for the rest of the timeout.
        Send-LeaseRelease -TicketId $ticketId -Reason "workspace setup failed"
        Send-DashboardEvent -Type "cycle_end" -TicketId $ticketId -Project $projectKey
        exit 0
    }
    if ($wt.path) {
        $workDir = $wt.path
        if ($isReview) {
            Write-Log "  Review worktree: $workDir (detached at $($wt.sha) on '$($wt.branch)')"
            # Deliberately NOT Set-TicketBranch. ticket.branch means "the branch this ticket owns",
            # and a review ticket owns none - the branch it reads is recorded on its review target,
            # which was written when the ticket was created and is not ours to change. Writing it
            # here would make the ticket look like the author of someone else's work to every
            # feature that reads that field, the PR dialog included.
        }
        else {
            Write-Log "  Worktree: $workDir (branch '$($wt.branch)')"
            # Record both together: a branch whose worktree the dashboard cannot name is a branch
            # nobody can find later. Except on a worker host, whose worktree is on another machine:
            # the board host reads a ticket's worktree path as one of its own (analysis, the
            # steward's checks, the reaper), so it is told the branch and nothing else.
            if ($onHost) { Set-TicketBranch -TicketId $ticketId -Branch $wt.branch }
            else         { Set-TicketBranch -TicketId $ticketId -Branch $wt.branch -WorktreePath $wt.path }
        }
    }
    else {
        Write-Log "  Worktrees unavailable ($($wt.reason)) - running directly in $workDir."
    }
}

# Build this run's MCP config: the dashboard's ticket tools, plus - when auggie is installed and
# the repo path is real - Augment's codebase-retrieval scoped to THIS worktree, so the worker can
# semantically query the codebase instead of grinding grep/glob. Written per-run and passed
# with --strict-mcp-config so the worker sees exactly these servers (no ambient project configs).
$mcpServers = @{ tickets = @{ type = "http"; url = "$DashboardUrl/mcp" } }
# Which of those the run cannot honestly do its job without - checked against the session's opening
# event before the model is allowed to start. `tickets` is on every run because it is how a worker
# records anything at all; without it a run does its work and then has nowhere to put it. Servers
# NOT listed here are the ones a run degrades over rather than fails over: auggie's absence costs
# semantic search and buys grep, which is slower and still correct.
$requiredServers = @('tickets')
if ($repoDir -and (Get-Command auggie -ErrorAction SilentlyContinue)) {
    $mcpServers.auggie = @{
        type    = "stdio"
        command = "cmd"
        args    = @("/c", "auggie", "--mcp", "-w", $workDir, "--wait-for-indexing")
    }
    Write-Log "  Augment codebase-retrieval enabled ($workDir)"
}
# QA needs a real browser. Without it the qa-test skill quietly degrades to reading source code,
# and the QA shots (the captioned screenshots the ticket shows) can never be captured at all.
# Only qa-test gets it - the other phases would pay the startup cost for nothing. Nor does a
# code-review-only ticket: that run reads the diff and stops, so the browser would be a startup
# cost with nothing behind it, and its absence is what makes the skill's Part B unreachable by
# accident as well as by instruction.
#
# Captures go to a per-ticket directory in the dashboard's own data dir, NOT the repo's
# .playwright-mcp: not every project gitignores that path, and screenshots landing in the
# working tree would be swept into the ticket's branch by Save-WorkerLeftovers.
#
# --output-dir does two things: it is where the page-snapshot files go, AND it becomes one of the
# server's allowed write roots. That second part is what matters. A screenshot filename is
# resolved against the SERVER's working directory, which is the worker's cwd (the repo) - a bare
# 'shot-1.png' lands in the repo root, and the config's cwd field is ignored by the MCP client
# (verified). So the skill passes an absolute path inside this directory, which the allowed-root
# check accepts and which no longer depends on any working directory at all.
$qaShotDir = ""
$skipQaTesting = [bool]$Claim.ticket.skipQaTesting
# Both testing skills drive a browser: qa-test on our own branch, review-pr on somebody else's.
# The flag means the same thing on either - read the diff, do not stand the app up - so the browser
# is withheld the same way, which is what makes the skill's Part B unreachable by accident as well
# as by instruction.
$browserSkills = @('qa-test', 'review-pr')
if ($skill -in $browserSkills -and $skipQaTesting) {
    Write-Log "  Code-review-only ticket - no browser, no stack: this run reads the diff."
}
if ($skill -in $browserSkills -and -not $skipQaTesting) {
    $qaShotDir = Join-Path $DataRoot "qa-captures\$ticketId"
    New-Item -ItemType Directory -Path $qaShotDir -Force | Out-Null
    $mcpServers.playwright = @{
        type    = "stdio"
        command = "cmd"
        args    = @("/c", "npx", "--yes", "@playwright/mcp@latest", "--headless", "--isolated", "--output-dir", $qaShotDir)
    }
    # Required, not optional: Part B of qa-test is browser work and the skill blocks the ticket
    # when the browser is missing. That is the right call for the skill and the wrong place to
    # discover it, so this run refuses to start without one.
    $requiredServers += 'playwright'
    Write-Log "  Playwright browser configured (captures -> $qaShotDir)"
}
# Named per TICKET, not per project. Two agents on the same project used to write this file over
# each other between one's write and the other's read, and the loser started its session pointed
# at the wrong worktree - an Augment index for someone else's branch, and a QA browser writing
# into someone else's capture directory. Each provider gets there its own way; see New-AgentToolSetup.
$toolSetup = New-AgentToolSetup -Provider $AgentProvider -Servers $mcpServers -RunDir $RunDir -Key "$projectKey-$ticketId" -Project $projectKey

# Some providers are configured through the environment rather than a flag. Setting it process-wide
# is safe precisely because this script IS one agent working one ticket - the isolation the fleet
# relies on is the process boundary, so there is nobody else in here to disturb.
foreach ($k in $toolSetup.Env.Keys) { Set-Item -Path "Env:$k" -Value $toolSetup.Env[$k] }

# Which run this is, for the hooks. A hook is a child of the agent CLI, which is a child of this
# process, so the environment is the one channel that reaches it without a file per run - and the
# same process-boundary argument as above makes setting it here safe. augment-hook.ps1 stamps these
# onto every Augment call it logs, which is what lets that log be read per ticket and per phase.
$env:WHETSTONE_TICKET_ID = "$ticketId"
$env:WHETSTONE_PROJECT   = "$projectKey"
$env:WHETSTONE_SKILL     = "$skill"

# Which file this run is told holds the repo's code conventions. Resolved against the worktree, not
# assumed from the provider - see Get-ConventionsFile. The skills defer to whatever this names
# rather than spelling out a filename of their own, so there is one answer per run and the prompt
# and the skill cannot contradict each other.
$conventionsFile = Get-ConventionsFile -Provider $AgentProvider -WorkDir $workDir
Write-Log "  Conventions file: $conventionsFile"

# The ref the skills measure this branch against, named for them rather than left to each skill to
# work out: it is the same one the squash and the rebase below will use, and a worker that picked a
# different one would be counting other people's commits as its own.
$baseRefNow = if ($repoDir) { Resolve-BaseRef -RepoDir $workDir -BaseBranch $effectiveBase -MergeLocally $mergeLocally } else { $null }
if (-not $baseRefNow) { $baseRefNow = $effectiveBase }
Write-Log "  Base ref: $baseRefNow"

$prompt = (Get-Content -Path $PromptFile -Raw).
    Replace('{{CONVENTIONS_FILE}}', $conventionsFile).
    Replace('{{BASE_REF}}',    $baseRefNow).
    Replace('{{TICKET_ID}}',   $ticketId).
    Replace('{{TITLE}}',       $title).
    Replace('{{PROJECT_KEY}}', $projectKey).
    Replace('{{PROJECT_NAME}}',$projectName).
    Replace('{{PHASE}}',       $phase).
    Replace('{{RESUMED}}',     $resumed).
    Replace('{{SKILL}}',       $skill).
    Replace('{{SKILL_FILE}}',  ($skillFile   -replace '\\', '/')).
    Replace('{{MEMORY_DIR}}',   $(if ($memoryDir)   { $memoryDir   -replace '\\', '/' } else { "(this project has no notes store)" })).
    Replace('{{MEMORY_INDEX}}', $(if ($memoryIndex) { $memoryIndex -replace '\\', '/' } else { "(this project has no notes store)" })).
    Replace('{{MASTER_NOTES}}',$masterNotesBlock).
    Replace('{{QA_SHOT_DIR}}', $(if ($qaShotDir) { $qaShotDir -replace '\\', '/' } else { "(not a QA phase)" })).
    Replace('{{ISSUE_CHANGES}}', $issueChanges).
    Replace('{{BLOCK_NOTE}}', $blockNote).
    Replace('{{OWNER_NOTES}}', $ownerNotes).
    Replace('{{TRACK_RULES}}', $trackRules)

$mutations = 0
$summaryLines = @()
$runLogLines = @()
$resultReceived = $false

# Filled in from the stream's closing "result" event. They stay $null when there is no such
# event, which is exactly what a timed-out or crashed run looks like.
$apiDurationSeconds = $null
$numTurns           = $null
$inputTokens        = $null
$outputTokens       = $null
$cacheReadTokens    = $null
$cacheCreateTokens  = $null
$resultIsError      = $false

# Where the run's context went. The totals above say how much was re-read; these say why: the
# largest prompt any one turn was sent, and what each tool call handed back. Gathered as the stream
# goes by rather than off the result event, so a run killed at the timeout still reports them -
# and that is the run most worth understanding.
$peakContextTokens = $null
$toolCalls         = @{}
$toolResults       = New-Object System.Collections.Generic.List[object]

# Carries what the stream adapter has to remember between lines. Codex reports its accounting on a
# closing event that does not repeat the summary text, so the last message it sent is kept here.
$streamState = @{ LastMessage = $null }

# Worker watchdog. A stalled worker (silent hang, stuck build) must never freeze this agent forever.
# It ends the run at the wall-clock timeout, or sooner when the agent's stream has been silent for
# $WorkerStallMinutes - see Start-AgentWatchdog. It kills ONLY the agent session descended from THIS
# process, and what that session started - which is the whole reason an agent is its own process:
# when they all hung off the one orchestrator, this walk started at a shared root and one agent's
# timeout would have killed every other agent's session mid-run. The ticket is left in-progress and
# the dashboard's attempt cap blocks it if it keeps stalling.
#
# The process name is passed IN rather than hardcoded: a job cannot see $AgentProvider, and a
# watchdog matching the wrong executable is a watchdog that quietly never fires.
$activityFile = Join-Path $RunDir "activity.worker-$PID"
$workerTimer = [System.Diagnostics.Stopwatch]::StartNew()
$watchdog = Start-AgentWatchdog -RootPid $PID -Pattern $AgentProvider.ProcessPattern `
    -TimeoutSeconds ([int]($WorkerTimeoutMinutes * 60)) -StallSeconds ([int]($WorkerStallMinutes * 60)) -ActivityFile $activityFile

# The prompt travels on stdin for both providers - see Get-AgentWorkerCommand for why it must not be
# an argument. The length goes in the log because a prompt that arrives short is otherwise invisible:
# the run still starts, the model still answers, and the only trace is work that quietly skipped a
# rule nobody can prove it was told. Compare it against the template if a run looks lobotomised.
$agentCmd = Get-AgentWorkerCommand -Provider $AgentProvider -Model $phaseModel -Prompt $prompt `
    -WorkDir $workDir -ToolSetup $toolSetup -ReasoningEffort $WorkerReasoningEffort
$agentArgs = $agentCmd.Args
Write-Log "  Prompt: $($prompt.Length) chars on stdin"

# Up to two launches, and only ever for one reason: a required MCP server that did not come up.
#
# The failure this exists for is transient and silent. On 2026-08-25 a QA run was handed a config
# naming Playwright, logged that it had one, and started 22 seconds slower than its neighbours -
# the CLI waiting out the startup budget on a stdio server that never handshook. The session ran
# anyway with no browser, and four minutes later the model blocked the ticket over it. The same
# config connected all three servers on the next try.
#
# So the check runs where it is cheap. session_start arrives before the model's first token, which
# means a relaunch here throws away a process start and nothing else - no thinking, no tool calls,
# no tokens. Two attempts, because the point is to ride out a stall, not to grind on a config that
# is genuinely wrong; a server still missing on the second try is a real fault and is reported as
# one rather than handed to a model that will spend a full run rediscovering it.
$mcpAttempt  = 0
$mcpRelaunch = $false
$mcpMissing  = $null
$mcpHardFail = $null

Push-Location $workDir
try {
  while ($true) {
    $mcpAttempt++
    $mcpRelaunch = $false
    Update-AgentActivity -ActivityFile $activityFile
    $agentCmd.Stdin | & $agentCmd.Exe @agentArgs 2>&1 | ForEach-Object {
        Update-AgentActivity -ActivityFile $activityFile
        if ($resultReceived -or $mcpRelaunch) { return }
        $line = $_.ToString().TrimEnd()
        if (-not $line) { return }
        try {
            $event = $line | ConvertFrom-Json -ErrorAction Stop
            # Everything below this line is provider-agnostic: the adapter has already turned one
            # line of whichever CLI's stream into zero or more canonical events.
            $canonical = ConvertFrom-AgentEvent -Provider $AgentProvider -Event $event -State $streamState
            foreach ($e in $canonical) {
                switch ($e.Kind) {
                    "session_start" {
                        # Every server's status goes in the log whether or not anything is wrong.
                        # The line that says what a run actually HAS is worth more than the line
                        # that says what it was offered, and it is what makes "did this run have a
                        # browser?" answerable afterwards instead of inferable.
                        $status = $e.ServerStatus
                        $withTools = $e.ServersWithTools
                        $shown = (($status.Keys | Sort-Object | ForEach-Object { "$_=$($status[$_])" }) -join ', ')
                        # A server the CLI never mentions is as absent as one that failed - it is
                        # named 'absent' rather than left blank so the log says which of the two.
                        #
                        # And 'connected' is not the same claim as 'usable'. A server can complete
                        # its handshake and still hand the session no tools, which is the shape the
                        # 2026-09-08 protocol regression took: `tickets` connected, none of its 21
                        # tools registered, and the run had no way to record a single thing it did.
                        # Only required servers are held to this - an optional one contributing
                        # nothing is a degraded run, not a failed launch.
                        $bad = @()
                        foreach ($name in $requiredServers) {
                            $st = if ($status.ContainsKey($name)) { $status[$name] } else { 'absent' }
                            if ($st -ne 'connected') { $bad += "$name ($st)" }
                            elseif ($withTools -and -not $withTools.ContainsKey($name)) { $bad += "$name (connected, no tools)" }
                        }
                        if ($bad.Count -eq 0) {
                            Write-Log "  MCP servers: $shown"
                        }
                        else {
                            $mcpMissing = ($bad -join ', ')
                            Write-Log "  MCP servers: $shown - REQUIRED not usable: $mcpMissing"
                            # Set the flag BEFORE the kill: the guard at the top of this block is
                            # what stops the dying session's remaining lines from being logged as
                            # if this run had said them.
                            $mcpRelaunch = $true
                            Stop-DescendantAgent -RootPid $PID -Pattern $AgentProvider.ProcessPattern
                        }
                    }
                    "tool_use" {
                        $toolName = $e.ToolName
                        $toolDetail = $e.Detail
                        $runLogLines += "> Tool: $toolName $toolDetail"
                        if ($e.Id) { $toolCalls[$e.Id] = @{ Tool = "$toolName"; Detail = "$toolDetail" } }

                        # The feed is a human-readable story of the ticket's progress, so we only
                        # surface ticket mutations (phase moves, work/QA items, comments). Everything
                        # else - read-only exploration AND raw file edits - stays in the local run
                        # log for debugging but off the feed.
                        $isTicketMutation = $false
                        if ($toolName -like "mcp__tickets__*") {
                            $readOnly = @(
                                "mcp__tickets__get_ticket", "mcp__tickets__list_actionable_tickets",
                                "mcp__tickets__list_projects", "mcp__tickets__get_issue_context",
                                # Announcing which item it is about to work is not progress on the
                                # ticket - the matching set_work_item_done / set_qa_status is.
                                # Counting both would double every implement and QA run's change
                                # count and put a second row on the feed for every item, saying
                                # nothing the outcome row won't.
                                "mcp__tickets__start_work_item", "mcp__tickets__start_qa_item")
                            if ($toolName -notin $readOnly) { $mutations++; $isTicketMutation = $true }
                        }

                        if ($isTicketMutation) {
                            Write-Log "  > Tool: $toolName $toolDetail"
                            Send-DashboardEvent -Type "tool_call" -ToolName $toolName -Text $toolDetail -TicketId $ticketId -Project $projectKey
                        }
                        else {
                            Write-Log "  . $toolName $toolDetail"   # not shown in feed: local log only
                        }
                    }
                    "context" {
                        if ($null -eq $peakContextTokens -or $e.Tokens -gt $peakContextTokens) { $peakContextTokens = [int]$e.Tokens }
                    }
                    "tool_result" {
                        $call = $toolCalls[$e.ToolUseId]
                        $toolResults.Add([pscustomobject]@{
                            tool   = if ($call) { $call.Tool } else { '(unknown)' }
                            detail = if ($call) { $call.Detail } else { '' }
                            chars  = [int]$e.Chars
                            images = [int]$e.Images
                        })
                    }
                    "text" {
                        $e.Text -split "`n" | ForEach-Object {
                            $t = $_.TrimEnd()
                            if ($t) {
                                Write-Log "  | $t"
                                $runLogLines += $t
                                Send-DashboardEvent -Type "text_output" -Text $t -TicketId $ticketId -Project $projectKey
                            }
                        }
                    }
                    "thinking" {
                        # The model's reasoning - surfaced as its own quiet event so the feed
                        # reads like a session transcript, not just a list of actions.
                        $th = ($e.Text -replace "`r", "").Trim()
                        if ($th) {
                            Write-Log "  ~ (thinking) $($th.Substring(0, [Math]::Min(80, $th.Length)))"
                            Send-DashboardEvent -Type "thinking" -Text $th -TicketId $ticketId -Project $projectKey
                        }
                    }
                    "result" {
                        # The closing event carries the run's own accounting - duration, turns and
                        # token usage. Any of these may be null: not every provider reports every
                        # number, and a figure we were not given is left unknown rather than
                        # invented as a zero that would read as "this run was free".
                        # (Assignments inside ForEach-Object land in the enclosing scope, which is
                        # what $resultReceived and $mutations already rely on.)
                        if ($null -ne $e.DurationMs) { $apiDurationSeconds = [math]::Round(([double]$e.DurationMs) / 1000, 1) }
                        if ($null -ne $e.NumTurns)          { $numTurns          = [int]$e.NumTurns }
                        if ($null -ne $e.IsError)           { $resultIsError     = [bool]$e.IsError }
                        if ($null -ne $e.InputTokens)       { $inputTokens       = [int]$e.InputTokens }
                        if ($null -ne $e.OutputTokens)      { $outputTokens      = [int]$e.OutputTokens }
                        if ($null -ne $e.CacheReadTokens)   { $cacheReadTokens   = [int]$e.CacheReadTokens }
                        if ($null -ne $e.CacheCreateTokens) { $cacheCreateTokens = [int]$e.CacheCreateTokens }

                        if ($e.Text) {
                            $e.Text -split "`n" | ForEach-Object {
                                $t = $_.TrimEnd()
                                if ($t) { Write-Log "  | $t"; $summaryLines += $t }
                            }
                            Send-DashboardEvent -Type "summary" -Text ($summaryLines -join "`n") -TicketId $ticketId -Project $projectKey
                        }
                        $resultReceived = $true
                        Stop-DescendantAgent -RootPid $PID -Pattern $AgentProvider.ProcessPattern
                    }
                }
            }
        }
        catch { Write-Log "  | $line" }
    }
    if (-not $mcpRelaunch) { break }
    if ($mcpAttempt -ge 2) {
        $mcpHardFail = $mcpMissing
        break
    }
    Write-Log "  Relaunching the agent - attempt $($mcpAttempt + 1) of 2."
  }
}
finally { Pop-Location }

# Tear down the watchdog. If no result came back, the worker either crashed or was ended by the
# watchdog - record it on the ticket so it's visible; the attempt cap handles repeat offenders.
$workerTimer.Stop()
$watchdogFired = Receive-AgentWatchdog -Job $watchdog -ActivityFile $activityFile

# Refused by the account's usage limit rather than failed - see Get-UsageLimitReset. Only an errored
# or near-silent run qualifies: a real run's summary can quote the phrase (a ticket about rate
# limits, say) and must not park the fleet for it.
$limitReset = $null
if ($resultIsError -or $summaryLines.Count -le 3) {
    $limitReset = Get-UsageLimitReset -Text ($summaryLines -join "`n")
}
if ($limitReset) {
    $limitText = ($summaryLines | Where-Object { $_ -match "(?i)hit your" } | Select-Object -First 1)
    Write-Log "  USAGE LIMIT: '$limitText' - the fleet claims nothing until $($limitReset.ToString('yyyy-MM-dd HH:mm')); this run does not count toward the attempt cap."
    try {
        @{ resetsAt = $limitReset.ToString("o"); text = "$limitText"; ticketId = [int]$ticketId; at = (Get-Date).ToString("o") } |
            ConvertTo-Json -Compress | Set-Content -Path $UsageLimitFile -Encoding UTF8
    }
    catch { Write-Log "  Could not record the usage limit: $($_.Exception.Message)" }
    try {
        $body = @{ ticketId = [int]$ticketId; reason = "the account's usage limit refused it ($limitText). The heartbeat resumes after $($limitReset.ToString('yyyy-MM-dd HH:mm'))." } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$DashboardUrl/api/heartbeat/refund-attempt" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 5 | Out-Null
    }
    catch { Write-Log "  Could not refund the attempt: $($_.Exception.Message)" }
    Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error "Usage limit - paused until $($limitReset.ToString('HH:mm'))"
}

if ($mcpHardFail) {
    # Nothing ran, so there is nothing to report about the work - the ticket is parked on the
    # environment instead. Said here rather than left to the model on purpose: the model would
    # reach the same conclusion, but only after a full run's worth of reading the code it cannot
    # test, and it can only guess at WHY the server is missing. This knows.
    $detail = "This run was launched without the MCP tools it needs: $mcpHardFail. The heartbeat writes each run its own MCP config and checks the session's opening event against it - both that each required server connected AND that it actually contributed tools, because a server can do the first without the second. These failed that check on two consecutive launches, so no work was attempted and the branch is untouched. This is an environment fault on the machine running the fleet, not a problem with the ticket - unblock it once the server is serving its tools again, and nothing about the branch needs to change first."
    Write-Log "  BLOCKING #${ticketId}: required MCP server(s) never usable over 2 attempts: $mcpHardFail"
    Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error "Required MCP server(s) not usable: $mcpHardFail"
    Send-TicketComment -TicketId $ticketId -Body $detail
    Block-Ticket -TicketId $ticketId -Reason $detail
}
elseif (-not $resultReceived -and $watchdogFired -and $watchdogFired.Reason -eq 'stall') {
    $elapsedMin = [math]::Round($workerTimer.Elapsed.TotalMinutes, 1)
    $quietMin = $watchdogFired.Minutes
    Write-Log "  WORKER stalled: no output from the agent for ${quietMin}min (stall limit ${WorkerStallMinutes}min, elapsed ${elapsedMin}min) - terminated with everything it started."
    Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error "Worker stalled: silent for ${quietMin}min (elapsed ${elapsedMin}min)"
    Send-TicketComment -TicketId $ticketId -Body "Heartbeat worker went silent for $quietMin min - no output from the agent at all, which usually means one tool call (a command, a test run) never returned - and was terminated with everything it had started, after $elapsedMin min in all (stall limit $WorkerStallMinutes min). Ticket left in ${phase}; the next run resumes it, and the dashboard blocks it after repeated no-progress attempts. If one command legitimately runs longer than that, raise heartbeatWorkerStallMinutes."
}
elseif (-not $resultReceived) {
    $elapsedMin = [math]::Round($workerTimer.Elapsed.TotalMinutes, 1)
    Write-Log "  WORKER did not complete (elapsed ${elapsedMin}min, timeout ${WorkerTimeoutMinutes}min) - terminated."
    Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error "Worker did not complete (elapsed ${elapsedMin}min, timeout ${WorkerTimeoutMinutes}min)"
    Send-TicketComment -TicketId $ticketId -Body "Heartbeat worker did not finish this run (elapsed ${elapsedMin} min, timeout ${WorkerTimeoutMinutes} min) and was terminated. Ticket left in ${phase}; the dashboard blocks it after repeated no-progress attempts."
}

# A review run must leave the branch EXACTLY as it found it, so none of the tidying below applies to
# one - and the leftover commit in particular would be actively destructive here. On every other
# track a dirty tree means a worker forgot to commit, and committing for it keeps the ticket moving.
# On this one the branch belongs to another developer: the same helper would write a commit onto a
# detached head off their work, from an agent that was told in three places not to change anything.
# So a dirty tree here is not a miss to paper over, it is the signal that the one rule of this phase
# was broken - reported on the ticket and left exactly where it is for a person to look at.
if ($repoDir -and $isReview) {
    $dirty = @(git -C $workDir status --porcelain 2>$null | Where-Object { $_ })
    if ($dirty.Count -gt 0) {
        $names = ($dirty | Select-Object -First 10 | ForEach-Object { "- $($_.Trim())" }) -join "`n"
        Write-Log "  WARNING: review run left $($dirty.Count) modified file(s) in a worktree it was only supposed to read."
        Send-TicketComment -TicketId $ticketId -Body "This peer review run modified $($dirty.Count) file(s) in its worktree. It should not have: the branch belongs to someone else and this phase reads only. Nothing was committed and nothing was pushed - the changes are still sitting in the worktree at ``$workDir`` for you to look at and discard.`n`n$names"
        Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error "Review run left $($dirty.Count) modified file(s) in its worktree"
    }
}
# Every cycle must leave its worktree clean - the next phase refuses to start on a dirty tree.
# Commit anything the worker forgot, loudly, so the ticket keeps moving and the miss is visible
# on the ticket rather than showing up as a confusing workspace failure one phase later.
if ($repoDir -and -not $isReview) {
    $leftovers = Save-WorkerLeftovers -WorkDir $workDir -TicketId $ticketId -Title $title `
        -IssueRef "$($Claim.issueCommitRef)" -Skill $skill
    if ($leftovers -and $leftovers.ok) {
        Write-Log "  WARNING: $skill left $($leftovers.files) file(s) uncommitted - committed as $($leftovers.sha) on '$($leftovers.branch)'."
        Send-TicketComment -TicketId $ticketId -Body "The $skill phase finished without committing its work. The heartbeat committed the $($leftovers.files) leftover file(s) to '$($leftovers.branch)' as $($leftovers.sha) so the next phase can run. Worth a look: this is one catch-all commit, not the atomic per-work-item commits the skill asks for."
    }
    elseif ($leftovers) {
        Write-Log "  WARNING: $skill left $($leftovers.files) file(s) uncommitted and the safety-net commit FAILED."
        Send-TicketComment -TicketId $ticketId -Body "The $skill phase left $($leftovers.files) file(s) uncommitted on '$($leftovers.branch)' and the heartbeat could not commit them automatically. The next phase will block on branch setup until this is sorted out by hand."
    }

    # One ticket, one commit. Refinement owns tidying the history and writing a good message;
    # this only fires when it did not, and says so on the ticket because a machine-written
    # message is exactly the thing a human should look at.
    if ($skill -eq 'refine') {
        $squash = Compress-TicketCommits -WorkDir $workDir -TicketId $ticketId -Title $title `
            -IssueRef "$($Claim.issueCommitRef)" -BaseBranch $effectiveBase `
            -Summary "$($Claim.ticket.implementationSummary)" -MergeLocally $mergeLocally
        if ($squash -and $squash.ok) {
            Write-Log "  WARNING: refine left $($squash.from) commits - squashed into $($squash.sha) on '$($squash.branch)'."
            Send-TicketComment -TicketId $ticketId -Body "Refinement finished without tidying the branch history, so the heartbeat squashed its $($squash.from) commits into one ($($squash.sha)), using this ticket's implementation summary as the commit body. Worth reading before this is merged - a summary written for the ticket is not always the right description for the repo's history."
        }
        elseif ($squash) {
            # Reaching here means the branch has more than one commit AND the net could not
            # collapse them - the invariant is broken and nobody downstream knows. A log line is
            # not where that belongs; the last time this went quiet it went quiet for weeks.
            Write-Log "  WARNING: refine left more than one commit and the heartbeat could not squash: $($squash.reason)."
            Send-TicketComment -TicketId $ticketId -Body "This branch still has more than one commit after refinement, and the heartbeat could not collapse them automatically: $($squash.reason). Squash it by hand before merging - this pipeline's rule is one ticket, one commit."
        }

        # Last thing before the ticket goes to a human: make sure what they are about to merge
        # actually merges. Deliberately AFTER the squash - rebasing one commit is one patch, and
        # the conflicts it reports are the real ones rather than per-commit noise from the
        # intermediate states squashing has already thrown away. On a worker host the mirrored
        # base is refreshed first, so the rebase sees what was merged on the board host meanwhile.
        if ($onHost) {
            $resync = Sync-BoardRefs -RepoDir $repoDir -BaseBranch $effectiveBase
            if (-not $resync.ok) { Write-Log "  Could not refresh the board host's base before the rebase: $($resync.reason) - rebasing onto the base as of the start of the run." }
        }
        $rebase = Update-BranchFromBase -WorkDir $workDir -BaseBranch $effectiveBase -RepoDir $repoDir `
            -MergeLocally $mergeLocally
        if ($rebase) {
            # The ref it measured against, when it got far enough to pick one.
            $baseName = if ($rebase.base) { $rebase.base } else { $effectiveBase }
            if ($rebase.ok -and $rebase.rebased) {
                Write-Log "  Rebased '$($rebase.branch)' onto $baseName ($($rebase.behind) commit(s) behind) -> $($rebase.sha)."
                Send-TicketComment -TicketId $ticketId -Body "This branch was $($rebase.behind) commit(s) behind $baseName, so the heartbeat rebased it before hand-off. It replayed cleanly and the branch is now $($rebase.sha), sitting directly on top of the current base. What you review is what would merge."
            }
            elseif ($rebase.ok) {
                Write-Log "  Branch is already up to date with $baseName."
            }
            elseif ($rebase.conflicts) {
                # The branch cannot merge. Finding this now is the entire point: there is still a
                # phase that can fix it, whereas a conflict discovered at merge time lands on a
                # person long after the worker that wrote the code has gone.
                $files = if ($rebase.conflicts.Count -gt 0) { ($rebase.conflicts | ForEach-Object { "- $_" }) -join "`n" } else { "(git did not name the files)" }
                $undone = "The rebase was undone - the branch is exactly as refinement left it."

                # Refinement picks its own hand-off, and only the one to NeedsReview means "this
                # is finished". If it already sent the ticket back for more work, that work will
                # come round through refinement again and be rebased then; overriding its
                # decision here would just be a second opinion nobody asked for - and from
                # anywhere but NeedsReview the move is not even legal.
                $current = Get-TicketPhase -TicketId $ticketId
                if ($current -eq 'NeedsReview') {
                    Write-Log "  Branch conflicts with $baseName ($($rebase.conflicts.Count) file(s)) - sending back to implementation."
                    Send-TicketComment -TicketId $ticketId -Body "This branch is $($rebase.behind) commit(s) behind $baseName and no longer merges cleanly, so it is going back for another implementation pass rather than to review. $undone Conflicting files:`n`n$files`n`nResolve these against the current base; the ticket returns through QA and refinement automatically."
                    Set-TicketPhase -TicketId $ticketId -Phase "NeedsImplementation" `
                        -Reason "Branch conflicts with $baseName and cannot be merged as it stands." | Out-Null
                }
                else {
                    Write-Log "  Branch conflicts with $baseName ($($rebase.conflicts.Count) file(s)); ticket is already in $current - leaving it there."
                    Send-TicketComment -TicketId $ticketId -Body "This branch is $($rebase.behind) commit(s) behind $baseName and no longer merges cleanly. Refinement had already sent this ticket on to $current, so it stays there - the next pass through refinement will rebase it again. $undone Conflicting files:`n`n$files"
                }
            }
            else {
                Write-Log "  Staleness check skipped: $($rebase.reason)."
                Send-TicketComment -TicketId $ticketId -Body "The heartbeat could not check this branch against ${baseName}: $($rebase.reason). Rebase it by hand before merging, or expect to resolve whatever has landed on the base since."
            }
        }
    }
}

# On a worker host, hand the branch back to the board host - after every safety net above has
# finished with it, and before the lease goes: the board host checks that this run still holds the
# ticket before it moves anything (see the branch accept route). A hand-back that fails blocks the
# ticket, because the work now exists on one machine only and the next run, on either, would not
# start from it.
if ($onHost -and $repoDir -and -not $isReview -and $wt -and $wt.branch) {
    $tip = "$(git -C $workDir rev-parse --verify --quiet HEAD 2>$null)".Trim()
    if ($tip -and $tip -eq $boardStartSha) {
        Write-Log "  Nothing new on '$($wt.branch)' - nothing to hand back."
    }
    else {
        $why = $null
        $sent = Send-BranchToBoard -WorkDir $workDir -RunId $runId
        if (-not $sent.ok) { $why = $sent.reason }
        else {
            $body = @{ runId = [int]$runId; branch = $wt.branch; sha = $sent.sha; incomingRef = $sent.ref; expectedOld = $boardStartSha } |
                ConvertTo-Json -Compress
            try {
                $null = Invoke-RestMethod -Uri "$DashboardUrl/api/tickets/$ticketId/branch/accept" -Method Post `
                    -ContentType 'application/json' -Body $body -TimeoutSec 120
            }
            catch {
                $why = $_.Exception.Message
                try { $detail = ($_.ErrorDetails.Message | ConvertFrom-Json).error; if ($detail) { $why = $detail } } catch { }
            }
        }
        if ($why) {
            $reason = "Hand-back from worker host $runHost failed: $why"
            Write-Log "  $reason"
            Block-Ticket -TicketId $ticketId -Reason $reason
            Send-TicketComment -TicketId $ticketId -Body "This run was on worker host ``$runHost`` and could not hand its branch back to the board host: $why`n`nThe work is committed there, as ``$tip`` on ``$($wt.branch)``, and nowhere else. Ticket blocked so no run starts without it. Once the cause is fixed, unblock: the next run on that machine hands it back."
            Send-DashboardEvent -Type "error" -TicketId $ticketId -Project $projectKey -Error $reason
        }
        else {
            $null = Set-BranchHandedBack -RepoDir $repoDir -Branch $wt.branch -Sha $sent.sha
            Write-Log "  Handed '$($wt.branch)' back to the board host at $($sent.sha)."
        }
    }
}

# Record the run before the feed event, so a cycle is never counted twice if the agent is killed
# between the two. Reflection is deliberately outside the measured window: it is the dashboard
# learning about the run, not the run itself, and folding it in would make every phase look
# two minutes slower than it is.
$cycleTimer.Stop()
$outcome = if ($resultReceived) {
    if ($resultIsError) { "Error" } else { "Completed" }
}
# A stall is reported as a Timeout: the run was ended by the watchdog either way, and the log and
# the ticket comment say which limit it hit.
elseif ($watchdogFired -or $workerTimer.Elapsed.TotalMinutes -ge $WorkerTimeoutMinutes) { "Timeout" }
else { "Error" }

# The few results that account for most of what the run carried. Five, because the point is to name
# what to trim, and past the first handful a run's results are all the same small size. Left $null
# when the stream reported none (codex, gemini), so "not measured" never reads as "nothing returned".
$toolResultChars    = $null
$largestToolResults = $null
if ($toolResults.Count -gt 0) {
    $toolResultChars    = [int](($toolResults | Measure-Object -Property chars -Sum).Sum)
    $largestToolResults = @($toolResults | Sort-Object -Property chars, images -Descending | Select-Object -First 5)
}

Send-CycleReport -TicketId $ticketId -Project $projectKey -Phase $phase -Skill $skill `
    -Resumed ([bool]$Claim.resumed) -Model $phaseModel -StartedAt $cycleStartedAt `
    -DurationSeconds $cycleTimer.Elapsed.TotalSeconds -Mutations $mutations -Outcome $outcome `
    -ApiDurationSeconds $apiDurationSeconds -NumTurns $numTurns `
    -InputTokens $inputTokens -OutputTokens $outputTokens `
    -CacheReadTokens $cacheReadTokens -CacheCreationTokens $cacheCreateTokens `
    -PeakContextTokens $peakContextTokens -ToolResultChars $toolResultChars `
    -LargestToolResults $largestToolResults

Write-Log ("  Cycle: {0} in {1}s ({2} turns, {3} in / {4} out tokens)" -f $outcome,
    [math]::Round($cycleTimer.Elapsed.TotalSeconds), $numTurns, $inputTokens, $outputTokens)
if ($null -ne $peakContextTokens) {
    Write-Log ("  Context: peak {0:N0} tokens in one turn; {1} tool results, {2:N0} chars in all" -f $peakContextTokens,
        $toolResults.Count, $toolResultChars)
    foreach ($r in $largestToolResults) {
        $img = if ($r.images -gt 0) { " + $($r.images) image(s)" } else { "" }
        Write-Log ("    {0,9:N0} chars{1}  {2} {3}" -f $r.chars, $img, $r.tool, $r.detail)
    }
}

# Release AFTER the safety nets above have finished with the worktree: the commit and squash
# steps still touch this ticket's workspace, and handing the QA stack to the next agent while
# this one is mid-rewrite is the exact collision leases exist to prevent. This is also what puts
# the TICKET back in play, so it must not happen a moment before this agent is done with it.
Send-LeaseRelease -TicketId $ticketId -Reason "cycle finished ($outcome)"

Send-DashboardEvent -Type "cycle_end" -TicketId $ticketId -Project $projectKey
# Reflection runs the same CLI against the same account, so after a limit it would only be refused too.
# Not on a worker host: what it writes belongs to the board host's project folder and notes store,
# and the copies a worker host is served are read-only (see ProjectKit). Its kit carries no
# reflect-prompt.md either, which would stop it on its own; this says so where it is decided.
if (-not $limitReset -and -not $onHost) {
    Invoke-Reflection -ProjectDataDir $projectDataDir -RepoDir $workDir -RunLog $runLogLines `
        -MemoryDir $memoryDir -TicketId $ticketId
}

# Last, because reflection is the final thing that runs the agent CLI and it needs the same setup.
# Some providers are configured by handing them a directory of their own, and for those that
# directory holds a COPY OF A LOGIN - so it does not outlive the run that needed it.
if ($toolSetup.Cleanup -and (Test-Path $toolSetup.Cleanup)) {
    Remove-Item -Path $toolSetup.Cleanup -Recurse -Force -ErrorAction SilentlyContinue
}

exit 0
