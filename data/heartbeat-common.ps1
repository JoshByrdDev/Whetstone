# ============================================================
# Shared ground for the orchestrator and its agents.
#
# The fleet is several PROCESSES now - one orchestrator plus up to three worker.ps1 agents - and
# both sides need the same config, the same log, and the same way of talking to the dashboard. This
# file is dot-sourced by both, so there is one definition of each rather than two that drift.
#
# Dot-sourced, not imported: these functions read $DashboardUrl and friends out of the calling
# script's scope, and every caller sets $DataRoot before sourcing this file.
#
# ASCII ONLY. PowerShell 5.1 parses .ps1 files as the system ANSI codepage, and a smart quote or a
# dash pasted in from anywhere else becomes a parse error on someone else's machine.
# ============================================================

# --- Which agent CLI we drive ---
#
# Sourced first so every helper below can ask the provider rather than assume one. The provider
# OBJECT is chosen at the bottom of this file, once the config and the log are both available.
. (Join-Path $DataRoot "providers.ps1")

# --- The notes store ---
#
# Reading, matching, writing and merging a project's durable facts. Sourced here rather than in
# worker.ps1 alone because the maintenance pass runs from the heartbeat, and both halves need the
# same view of what a note is. Depends on Add-Utf8Text and Invoke-WithMutex, which are defined below
# in this file - fine, because nothing in it runs at load time.
. (Join-Path $DataRoot "notes-store.ps1")

# --- Where notifications go ---
#
# Slack or ntfy, chosen in Settings and read live at each send. Depends on Write-Log and the config
# paths below; nothing in it runs at load time.
. (Join-Path $DataRoot "notifiers.ps1")

# --- Text encoding, before anything talks to a child process ---
#
# Everything this system reads comes out of the agent CLI, and both of them emit UTF-8. PowerShell 5.1 decodes
# a native process's stdout using the CONSOLE codepage (OEM 437/850 here) and encodes what it pipes
# to stdin as ASCII, so every arrow, dash and quote in a worker's output was being destroyed on the
# way in - before any file was written. That is what produced the run of `G??` sequences through the
# logs and the suggestions file: not a bad write, a bad read. The bytes on disk are what arrived.
#
# Every process in the fleet has to do this for itself - a child inherits none of it - which is why
# it lives here rather than in whichever script happened to need it first.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)

# --- Paths (every caller sets $DataRoot to the data/ root before sourcing this) ---
$PromptFile         = Join-Path $DataRoot "heartbeat-prompt.md"
$PromptStewardFile  = Join-Path $DataRoot "steward-prompt.md"
$StewardScript      = Join-Path $DataRoot "steward.ps1"
$WorkerScript       = Join-Path $DataRoot "worker.ps1"
$LockFile           = Join-Path $DataRoot "heartbeat.lock"
$StopFile           = Join-Path $DataRoot "heartbeat.stop"
# Written by the dashboard when work arrives that a sleeping orchestrator cannot see (a ticket
# filed, unblocked or sent back, a steward run falling due). It ends an idle sleep and nothing else.
$WakeFile           = Join-Path $DataRoot "heartbeat.wake"
$LogDir             = Join-Path $DataRoot "logs"
$RunDir             = Join-Path $DataRoot "run"
$GlobalDefaultsFile = Join-Path $DataRoot "config.defaults.json"
$GlobalConfigFile   = Join-Path $DataRoot "config.json"

# Which agent this process is: 0 for the orchestrator, 1..N for a worker. Stamped onto every event
# and every log line, because with three agents interleaved a line that does not say who wrote it
# is a line nobody can act on.
$script:AgentSlot = 0

# --- Global config (config.defaults.json, overridden by config.json) ---
$DashboardUrl            = "http://localhost:5338"
$BaseHeartbeatMinutes    = 3
$HeartbeatIdleMultiplier = 2
$HeartbeatMaxMinutes     = 60
$OffHoursStart           = "17:00"
$OffHoursEnd             = "06:30"
$WorkerTimeoutMinutes    = 120
# A run whose agent says nothing on its stream for this long is ended - see Start-AgentWatchdog. 0 = off.
$WorkerStallMinutes      = 30
$BaseBranch              = "main"
$BranchPrefix            = ""
$WorkerModel             = "claude-opus-5-5"
$ReflectionModel         = "haiku"
# Per-phase overrides of $WorkerModel, keyed by SKILL name (speculate / implement / qa-test /
# refine) rather than by phase: the skill is what the worker already has in hand from the claim,
# and it is the thing whose prompt the model is being matched to. Absent or empty means "use
# $WorkerModel", so a config file that has never heard of this setting behaves exactly as it did -
# and moving one phase leaves the other three provably untouched.
$PhaseModels             = $null
$HeartbeatWorkers        = 3

# The ceiling, mirrored from HeartbeatManager.MaxWorkers on the dashboard side. Both halves of the
# system clamp to it independently so a hand-edited config file cannot start a fourth agent by
# going round whichever half happens to read the value first.
$MaxHeartbeatWorkers     = 3

# The berth a project's steward runs in: one past the last worker slot, and never counted against
# the worker limit. A steward is the thing that answers questions and signs work off, so making it
# wait for a free worker slot would have the fleet's own output queueing behind the fleet. One at a
# time across every project - it is a full session on the same account as the three beside it.
$StewardSlot             = $MaxHeartbeatWorkers + 1

$globalDefaults = $null
$global         = $null
if (Test-Path $GlobalDefaultsFile) { try { $globalDefaults = Get-Content -Path $GlobalDefaultsFile -Raw | ConvertFrom-Json } catch {} }
if (Test-Path $GlobalConfigFile)   { try { $global   = Get-Content -Path $GlobalConfigFile   -Raw | ConvertFrom-Json } catch { Write-Host "WARNING: Failed to parse config.json - using defaults." -ForegroundColor Yellow } }

function Get-Global([string]$Name) {
    if ($global         -and $null -ne $global.$Name)         { return $global.$Name }
    if ($globalDefaults -and $null -ne $globalDefaults.$Name) { return $globalDefaults.$Name }
    return $null
}

if (Get-Global 'dashboardUrl')    { $DashboardUrl    = Get-Global 'dashboardUrl' }
if ($null -ne (Get-Global 'baseHeartbeatMinutes'))    { $BaseHeartbeatMinutes    = [int](Get-Global 'baseHeartbeatMinutes') }
if ($null -ne (Get-Global 'heartbeatIdleMultiplier')) { $HeartbeatIdleMultiplier = [double](Get-Global 'heartbeatIdleMultiplier') }
if ($null -ne (Get-Global 'heartbeatMaxMinutes'))     { $HeartbeatMaxMinutes     = [int](Get-Global 'heartbeatMaxMinutes') }
if (Get-Global 'offHoursStart')   { $OffHoursStart   = Get-Global 'offHoursStart' }
if (Get-Global 'offHoursEnd')     { $OffHoursEnd     = Get-Global 'offHoursEnd' }
if ($null -ne (Get-Global 'heartbeatWorkerTimeoutMinutes')) { $WorkerTimeoutMinutes = [int](Get-Global 'heartbeatWorkerTimeoutMinutes') }
if ($null -ne (Get-Global 'heartbeatWorkerStallMinutes'))   { $WorkerStallMinutes   = [int](Get-Global 'heartbeatWorkerStallMinutes') }
if (Get-Global 'baseBranch') { $BaseBranch = Get-Global 'baseBranch' }
$configuredPrefix = Get-Global 'branchPrefix'; if ($null -ne $configuredPrefix) { $BranchPrefix = $configuredPrefix }
if (Get-Global 'workerModel')     { $WorkerModel     = Get-Global 'workerModel' }
if (Get-Global 'reflectionModel') { $ReflectionModel = Get-Global 'reflectionModel' }
$configuredPhaseModels = Get-Global 'phaseModels'
if ($configuredPhaseModels) { $PhaseModels = $configuredPhaseModels }
if ($null -ne (Get-Global 'heartbeatWorkers')) {
    $HeartbeatWorkers = [int](Get-Global 'heartbeatWorkers')
}
if ($HeartbeatWorkers -lt 1)                    { $HeartbeatWorkers = 1 }
if ($HeartbeatWorkers -gt $MaxHeartbeatWorkers) { $HeartbeatWorkers = $MaxHeartbeatWorkers }

# Which Claude login a run spends: the config directory of the account Settings has switched to, or
# $null for the CLI's own. The dashboard reads the same two keys the same way (ClaudeAccounts.cs),
# and the two have to agree or its meter shows one account while the fleet spends another - so an
# entry with no directory is skipped here exactly as it is there, never guessed at.
function Get-ClaudeAccountDir {
    $active = "$(Get-Global 'claudeAccount')".Trim()
    if (-not $active) { return $null }
    foreach ($a in @(Get-Global 'claudeAccounts')) {
        if (-not $a) { continue }
        $dir = "$($a.configDir)".Trim()
        if ("$($a.name)".Trim() -ieq $active -and $dir) { return $dir }
    }
    return $null
}

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory -Path $RunDir -Force | Out-Null }

# --- Cross-process serialisation ---

# Run a block with a machine-wide lock held. In-process locks were enough when the pipeline was one
# process; now the agents are separate processes that share a log file, a hub repo and a project's
# notes store, and nothing inside one of them can see the others at all.
#
# An abandoned mutex (its holder was killed mid-block) is treated as ACQUIRED, which is what the
# exception means: the lock is ours now. The alternative - refusing to proceed - would let one
# killed agent wedge a resource for every agent after it.
function Invoke-WithMutex {
    param([string]$Name, [scriptblock]$Body, [int]$TimeoutMs = 60000)

    $safe = ($Name -replace '[^A-Za-z0-9_-]', '-')
    $mutex = New-Object System.Threading.Mutex($false, "Local\CDH-$safe")
    $held = $false
    try {
        try { $held = $mutex.WaitOne($TimeoutMs) }
        catch [System.Threading.AbandonedMutexException] { $held = $true }
        return & $Body
    }
    finally {
        if ($held) { try { $mutex.ReleaseMutex() } catch { } }
        $mutex.Dispose()
    }
}

# --- Helpers ---

# Whether a process's command line is running exactly $Script - this checkout's copy of it, and not
# a file of the same name in another Whetstone checkout on the same machine.
#
# A machine can hold more than one: a board of its own, say, beside a worker host's checkout that
# runs workers for a board on another machine (docs/worker-hosts-plan.md). Every scan that adopts,
# kills or defers to a process by its script would otherwise reach across into the other one - a
# board starting up would take the runner's workers onto its own books, under ticket ids that mean
# something else here, or kill them for want of a slot.
#
# Only an absolute path to a different file is "not ours". A -File given relative to a working
# directory this process cannot see is counted as ours, which is what every scan did before: for a
# singleton guard the safe mistake is to defer, and nothing in the fleet launches one that way.
# A -Command line never matches - it is a person's diagnostic that mentions the name, not a run.
function Test-RunsScript {
    param([string]$CommandLine, [string]$Script)
    if (-not $CommandLine -or $CommandLine -like '*-Command*') { return $false }
    if ($CommandLine -notmatch '-File\s+(?:"([^"]+)"|(\S+))') { return $false }
    $file = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
    if ([IO.Path]::GetFileName($file) -ne [IO.Path]::GetFileName($Script)) { return $false }
    if (-not [IO.Path]::IsPathRooted($file)) { return $true }
    try { return [IO.Path]::GetFullPath($file).Equals([IO.Path]::GetFullPath($Script), [StringComparison]::OrdinalIgnoreCase) }
    catch { return $false }
}

# Append text as BOM-less UTF-8, safely for several processes at once.
#
# Bare Add-Content defaults to the system ANSI codepage on PowerShell 5.1, and everything written
# through here is model output - arrows, dashes, quotes - so every one of those characters was
# landing in the logs and the suggestions file as mojibake. Half a file of line noise is a file
# nobody reads, which is how a fortnight of reflection output quietly became unusable.
#
# UTF8Encoding($false) rather than -Encoding UTF8 because the 5.1 provider writes a BOM, and a BOM
# appearing partway down a file that is already being appended to is its own kind of corruption.
#
# The mutex is what makes the shared log survive a fleet: three agents narrating at once into one
# file would otherwise interleave mid-line, and the line most likely to be shredded is the long one
# - which is to say the error you are reading the log to find. Failures are swallowed: this is the
# logging path, and logging must never be what stops an agent.
function Add-Utf8Text {
    param([string]$Path, [string]$Text)
    Invoke-WithMutex -Name "file-$([System.IO.Path]::GetFileName($Path))" -TimeoutMs 15000 -Body {
        try { [System.IO.File]::AppendAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false))) }
        catch { }
    }
}

# One shared log for the whole fleet, with every line naming the agent that wrote it. Separate files
# per agent were the obvious alternative and the wrong one: what a person actually wants to read is
# the story of the machine over the last ten minutes, and that story is interleaved by nature.
function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $who = if ($script:AgentSlot -gt 0) { "[a$($script:AgentSlot)] " } else { "[hb] " }
    $logMessage = "[$timestamp] $who$Message"
    Write-Host $logMessage
    $logFile = Join-Path $LogDir "heartbeat-$(Get-Date -Format 'yyyy-MM-dd').log"
    Add-Utf8Text -Path $logFile -Text ($logMessage + [Environment]::NewLine)
}

# Post one event to the dashboard's feed. Stamped with this process's agent slot unless the caller
# names a different one, which the orchestrator does when it reports a slot it has just reaped.
function Send-DashboardEvent {
    param(
        [string]$Type, [string]$TicketId = $null, [string]$Phase = $null, [string]$Project = $null,
        [string]$ToolName = $null, [string]$Text = $null, [int]$DurationSeconds = 0, [string]$Error = $null,
        [int]$Agent = -1
    )
    if (-not $DashboardUrl) { return }
    try {
        $slot = if ($Agent -ge 0) { $Agent } else { $script:AgentSlot }
        $evt = @{ type = $Type; timestamp = (Get-Date).ToUniversalTime().ToString("o") }
        if ($slot -gt 0)      { $evt.agent          = $slot }
        if ($TicketId)        { $evt.ticketId       = $TicketId }
        if ($Phase)           { $evt.phase          = $Phase }
        if ($Project)         { $evt.project        = $Project }
        if ($ToolName)        { $evt.toolName       = $ToolName }
        if ($Text)            { $evt.text           = $Text }
        if ($DurationSeconds) { $evt.durationSeconds = $DurationSeconds }
        if ($Error)           { $evt.error          = $Error }
        $body = $evt | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$DashboardUrl/api/events" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 2 | Out-Null
    }
    catch { }  # fire-and-forget - the dashboard being down must never break the heartbeat
}

# Post a System comment on a local ticket (e.g. to record a worker timeout on the ticket itself).
function Send-TicketComment {
    param([string]$TicketId, [string]$Body)
    if (-not $DashboardUrl -or -not $TicketId) { return }
    try {
        $payload = @{ body = $Body; author = "System" } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$DashboardUrl/api/tickets/$TicketId/comments" -Method Post `
            -ContentType 'application/json' -Body $payload -TimeoutSec 3 | Out-Null
    }
    catch { }
}

# Give back whatever shared resources this run was holding - the QA stack, the local DB, and the
# ticket itself. Keyed by ticket, so calling it twice is harmless and a run that died halfway is
# still tidied by the next thing that mentions the ticket. The lease expiry on the dashboard side is
# the backstop for the case where this never runs at all, so failing here delays a resource - it
# does not lose it.
function Send-LeaseRelease {
    param([string]$TicketId, [string]$Reason)
    if (-not $DashboardUrl -or -not $TicketId) { return }
    try {
        $body = @{ ticketId = [int]$TicketId; reason = $Reason } | ConvertTo-Json -Compress
        $r = Invoke-RestMethod -Uri "$DashboardUrl/api/heartbeat/leases/release" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 5
        if ($r.released -gt 0) { Write-Log "  Released $($r.released) resource lease(s)." }
    }
    catch { Write-Log "  (leases) release failed: $($_.Exception.Message)" }
}

# Record the branch and/or worktree on the ticket (narrow update; never touches title/desc/issue link).
function Set-TicketBranch {
    param([string]$TicketId, [string]$Branch, [string]$WorktreePath)
    if (-not $DashboardUrl) { return }
    try {
        $payload = @{}
        if ($Branch)       { $payload.branch       = $Branch }
        if ($WorktreePath) { $payload.worktreePath = $WorktreePath }
        if ($payload.Count -eq 0) { return }
        $body = $payload | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$DashboardUrl/api/tickets/$TicketId/branch" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 5 | Out-Null
    }
    catch { Write-Log "  Could not record branch '$Branch' on ticket #${TicketId}: $($_.Exception.Message)" }
}

# Move a ticket to Blocked with a reason (used when branch enforcement fails before any work runs).
function Block-Ticket {
    param([string]$TicketId, [string]$Reason)
    if (-not $DashboardUrl) { return }
    try {
        $body = @{ toPhase = "Blocked"; reason = $Reason } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$DashboardUrl/api/tickets/$TicketId/transition" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 5 | Out-Null
    }
    catch { Write-Log "  Could not block ticket #${TicketId}: $($_.Exception.Message)" }
}

# What phase is a ticket in right now? Needed because a worker chooses its own hand-off, so nothing
# outside it can assume where the ticket ended up after the run.
function Get-TicketPhase {
    param([string]$TicketId)
    if (-not $DashboardUrl -or -not $TicketId) { return $null }
    try {
        return "$((Invoke-RestMethod -Uri "$DashboardUrl/api/tickets/$TicketId" -TimeoutSec 5).phase)"
    }
    catch { return $null }
}

# Move a ticket to a phase the state machine allows from where it is. Distinct from Block-Ticket:
# this is the pipeline routing a ticket somewhere it can still be worked, not parking it for a human.
function Set-TicketPhase {
    param([string]$TicketId, [string]$Phase, [string]$Reason)
    if (-not $DashboardUrl) { return $false }
    try {
        $body = @{ toPhase = $Phase; reason = $Reason } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$DashboardUrl/api/tickets/$TicketId/transition" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 5 | Out-Null
        return $true
    }
    catch {
        Write-Log "  Could not move ticket #${TicketId} to ${Phase}: $($_.Exception.Message)"
        return $false
    }
}

# Kill ONLY the agent process(es) descended from $RootPid. Never a global `Get-Process claude`,
# which would take down the user's interactive sessions - and, now that agents are siblings rather
# than one nested tree, would take down the other agents' in-flight work as well. Each agent passes
# its OWN $PID, so the blast radius is exactly one agent.
#
# $Pattern is one or more process-name patterns - the provider's, taken from its descriptor rather
# than hardcoded, because killing by the wrong name kills nothing and the watchdog then silently
# stops being a watchdog. More than one because an npm-installed CLI is a .cmd shim in front of
# node.exe, so the process actually holding the session is not named after the CLI at all.
function Stop-DescendantAgent {
    param([int]$RootPid, [string[]]$Pattern)
    if (-not $Pattern) { $Pattern = $script:AgentProcessPattern }
    if (-not $Pattern) { $Pattern = @('claude*') }
    try { $procs = Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name -ErrorAction Stop }
    catch { return }
    $childrenOf = @{}
    foreach ($p in $procs) {
        $k = [int]$p.ParentProcessId
        if (-not $childrenOf.ContainsKey($k)) { $childrenOf[$k] = @() }
        $childrenOf[$k] += $p
    }
    $stack = New-Object System.Collections.Stack
    $stack.Push([int]$RootPid)
    while ($stack.Count -gt 0) {
        $cur = $stack.Pop()
        if ($childrenOf.ContainsKey($cur)) {
            foreach ($c in $childrenOf[$cur]) {
                $cpid = [int]$c.ProcessId
                foreach ($pat in $Pattern) {
                    if ($c.Name -like $pat) { Stop-Process -Id $cpid -Force -ErrorAction SilentlyContinue; break }
                }
                $stack.Push($cpid)
            }
        }
    }
}

# --- The run watchdog: a wall clock, and a stall ---
#
# A run is ended for one of two reasons. The wall clock ($WorkerTimeoutMinutes) is the old one. The
# stall ($WorkerStallMinutes) exists because the wall clock alone let a worker sit for 50 minutes on
# one Bash call that never returned - past its own 580-second timeout, with no tool result and not
# one line on the stream - and it would have sat for 70 more had the heartbeat not been restarted.
#
# "Silent" means no line on the agent's stream: the pipeline that reads it touches $ActivityFile (at
# most every ten seconds, through Update-AgentActivity), and the job reads the file's write time. A
# legitimate run is never silent for long: claude's Bash tool caps a call at ten minutes before it
# backgrounds or kills it, and a subagent's turns stream like the parent's. The default is three
# times that cap; a project whose suite runs longer in one call has the setting.
#
# What it kills is every process matching the provider's pattern under $RootPid AND everything that
# process started. The hung call is a child of the agent (a shell, and the test runner under it), and
# killing only the agent leaves that tree running, holding a scratch database or a docker exec for
# nobody. Still confined to this one agent's descendants, for the reason Stop-DescendantAgent gives.
#
# The verdict - "timeout <min>" or "stall <min>" - is written to "$ActivityFile.verdict" BEFORE the
# kill, because the kill is what ends the stream: the worker reads it the moment the pipeline
# returns, while the job may still be stopping the rest of the tree. Receive-AgentWatchdog reads it.
function Start-AgentWatchdog {
    param([int]$RootPid, [string[]]$Pattern, [int]$TimeoutSeconds, [int]$StallSeconds, [string]$ActivityFile)
    if (-not $Pattern) { $Pattern = @('claude*') }
    Set-Content -Path $ActivityFile -Value '' -Encoding ASCII
    Start-Job -ScriptBlock {
        param($timeoutSec, $stallSec, $rootPid, $patterns, $activity)
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $verdict = $null
        while (-not $verdict) {
            Start-Sleep -Seconds 15
            if ($clock.Elapsed.TotalSeconds -ge $timeoutSec) { $verdict = 'timeout'; $quiet = $clock.Elapsed.TotalMinutes; break }
            if ($stallSec -gt 0) {
                try { $last = [System.IO.File]::GetLastWriteTimeUtc($activity) } catch { continue }
                $quiet = ([DateTime]::UtcNow - $last).TotalMinutes
                if ($quiet * 60 -ge $stallSec) { $verdict = 'stall' }
            }
        }
        Set-Content -Path "$activity.verdict" -Value "$verdict $([math]::Round($quiet, 1))" -Encoding ASCII
        try { $procs = Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name -ErrorAction Stop } catch { return }
        $childrenOf = @{}
        foreach ($p in $procs) { $k = [int]$p.ParentProcessId; if (-not $childrenOf.ContainsKey($k)) { $childrenOf[$k] = @() }; $childrenOf[$k] += $p }
        # Walk from the root; once a process matches the pattern, it and everything under it goes.
        $kill = New-Object System.Collections.Generic.List[int]
        $stack = New-Object System.Collections.Stack; $stack.Push(@([int]$rootPid, $false))
        while ($stack.Count -gt 0) {
            $cur, $doomed = $stack.Pop()
            if (-not $childrenOf.ContainsKey($cur)) { continue }
            foreach ($c in $childrenOf[$cur]) {
                $cpid = [int]$c.ProcessId
                $hit = $doomed
                if (-not $hit) { foreach ($pat in $patterns) { if ($c.Name -like $pat) { $hit = $true; break } } }
                if ($hit) { $kill.Add($cpid) }
                $stack.Push(@($cpid, $hit))
            }
        }
        foreach ($k in $kill) { Stop-Process -Id $k -Force -ErrorAction SilentlyContinue }
    } -ArgumentList $TimeoutSeconds, $StallSeconds, $RootPid, $Pattern, $ActivityFile
}

# Marks the agent's stream as alive. Called for every line the pipeline reads; writes at most every
# ten seconds, because a busy run emits hundreds of lines a minute and the job only needs minutes.
$script:AgentActivityClock = $null
function Update-AgentActivity {
    param([string]$ActivityFile)
    if ($script:AgentActivityClock -and $script:AgentActivityClock.Elapsed.TotalSeconds -lt 10) { return }
    try { [System.IO.File]::SetLastWriteTimeUtc($ActivityFile, [DateTime]::UtcNow) } catch { }
    $script:AgentActivityClock = [System.Diagnostics.Stopwatch]::StartNew()
}

# Stops the watchdog and returns why it ended the run: @{ Reason = 'timeout'|'stall'; Minutes = n },
# or $null when it never fired (the run closed itself, crashed, or was ended for another reason).
function Receive-AgentWatchdog {
    param($Job, [string]$ActivityFile)
    $out = $null
    if ($ActivityFile) { $out = Get-Content -LiteralPath "$ActivityFile.verdict" -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if ($Job) {
        # A job that fired is let finish its kill (seconds); one still asleep is stopped outright.
        if ($out) { Wait-Job $Job -Timeout 30 | Out-Null }
        Stop-Job $Job -ErrorAction SilentlyContinue
        Remove-Job $Job -Force -ErrorAction SilentlyContinue
    }
    if ($ActivityFile) { Remove-Item -LiteralPath $ActivityFile, "$ActivityFile.verdict" -Force -ErrorAction SilentlyContinue }
    if ("$out" -match '^(timeout|stall) ([\d.]+)$') { return @{ Reason = $Matches[1]; Minutes = [double]$Matches[2] } }
    return $null
}

# --- The account's usage limit ---
#
# "You've hit your session limit - resets 8pm (America/Chicago)" is not a run that failed, it is a
# run that was never allowed to start, and it will be refused again every time until the reset. Read
# as an ordinary failure it cost #71 all three of its attempts inside a minute and parked it Blocked
# for two hours waiting on a human. So a worker that sees it writes the reset time here, gives its
# attempt back, and the orchestrator claims nothing until the time has passed.
$UsageLimitFile = Join-Path $RunDir "usage-limit.json"

# The reset time in a limit message, as local time; $null when the text is not a limit message at
# all. The zone in brackets is ignored and the clock read as this machine's: PS 5.1 cannot resolve an
# IANA zone name, and the CLI reports the reset in the zone of the machine it runs on. A limit whose
# time cannot be read still pauses - for an hour, which is wrong in the safe direction.
function Get-UsageLimitReset {
    param([string]$Text, [datetime]$Now = (Get-Date))
    if (-not $Text -or $Text -notmatch "(?i)hit your (?:[\w-]+ )?limit") { return $null }

    $rx = "(?i)resets\s+(?:(?<mon>[a-z]{3})[a-z]*\.?\s+(?<day>\d{1,2}),?\s+(?:at\s+)?)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)"
    $m = [regex]::Match($Text, $rx)
    if (-not $m.Success) { return $Now.AddMinutes(60) }

    $hour = [int]$m.Groups['h'].Value % 12
    if ($m.Groups['ap'].Value -ieq 'pm') { $hour += 12 }
    $minute = if ($m.Groups['m'].Success) { [int]$m.Groups['m'].Value } else { 0 }

    if ($m.Groups['mon'].Success) {
        $month = [array]::IndexOf(@('jan','feb','mar','apr','may','jun','jul','aug','sep','oct','nov','dec'),
                                  $m.Groups['mon'].Value.ToLowerInvariant()) + 1
        if ($month -lt 1) { return $Now.AddMinutes(60) }
        $at = New-Object DateTime($Now.Year, $month, [int]$m.Groups['day'].Value, $hour, $minute, 0)
        if ($at -lt $Now.AddDays(-1)) { $at = $at.AddYears(1) }   # "resets Jan 2" read on Dec 30
        return $at
    }

    $at = $Now.Date.AddHours($hour).AddMinutes($minute)
    if ($at -le $Now) { $at = $at.AddDays(1) }
    return $at
}

# When the fleet may claim again, or $null when it may claim now. An expired pause is removed here,
# so nothing else has to remember to clean it up.
function Get-UsageLimitPause {
    if (-not (Test-Path $UsageLimitFile)) { return $null }
    $until = $null
    try { $until = [datetime](Get-Content -Path $UsageLimitFile -Raw | ConvertFrom-Json).resetsAt } catch { }
    if ($until -and $until -gt (Get-Date)) { return $until }
    Remove-Item $UsageLimitFile -Force -ErrorAction SilentlyContinue
    return $null
}

function Test-OffHours {
    $now = (Get-Date).TimeOfDay
    $start = [TimeSpan]::Parse($OffHoursStart)
    $end   = [TimeSpan]::Parse($OffHoursEnd)
    if ($start -gt $end) { return ($now -ge $start -or $now -lt $end) }   # crosses midnight
    else                 { return ($now -ge $start -and $now -lt $end) }
}

# --- The provider, resolved last ---
#
# Down here rather than up with the rest of the config for one reason: Resolve-AgentModel reports a
# corrected model through Write-Log, and Write-Log is defined halfway down this file. Reading config
# is order-free; anything that TALKS is not.
$AgentProviderName = 'claude'
if (Get-Global 'agentProvider') { $AgentProviderName = "$(Get-Global 'agentProvider')" }
$AgentProvider = Get-AgentProvider -Name $AgentProviderName

# Read by Stop-DescendantAgent when a caller does not name a pattern - including the orchestrator,
# which reaps agents it did not launch.
$script:AgentProcessPattern = $AgentProvider.ProcessPattern

# A model left pointing at the previous provider is corrected here, once, instead of failing every
# run for the rest of the session.
$WorkerModel     = Resolve-AgentModel -Provider $AgentProvider -Model $WorkerModel     -Fallback $AgentProvider.DefaultWorkerModel
$ReflectionModel = Resolve-AgentModel -Provider $AgentProvider -Model $ReflectionModel -Fallback $AgentProvider.DefaultReflectModel

# Which model this run's SKILL should use. $WorkerModel unless phaseModels names one for it.
#
# Resolved here rather than at config-read time because the answer depends on the claim, which does
# not exist yet when config is read - and because Resolve-AgentModel talks through Write-Log, which
# is why the other two resolves are down here too.
#
# The override goes through Resolve-AgentModel with $WorkerModel as the fallback, not the provider
# default: a phase left pointing at the other provider's model should land on whatever the fleet is
# actually running, which is the setting the user last chose, not a constant.
function Get-PhaseModel {
    param([string]$Skill)

    $override = "$(Get-Prop $PhaseModels $Skill)".Trim()
    if (-not $override) { return $WorkerModel }
    return (Resolve-AgentModel -Provider $AgentProvider -Model $override -Fallback $WorkerModel)
}

# How hard every run is asked to think - the worker and its reflection alike - where the provider
# exposes that as a dial: claude's --effort, codex's model_reasoning_effort; gemini has none and
# ignores this. Blank means high, never "whatever the CLI defaults to": claude's default moves with
# the model (Opus 5 ran at high, Opus 5.5 at medium). The dashboard's own runs read the same
# setting the same way - AgentProviderFactory.DefaultEffort.
$WorkerReasoningEffort = 'high'
$configuredEffort = "$(Get-Global 'workerReasoningEffort')".Trim()
if ($configuredEffort) { $WorkerReasoningEffort = $configuredEffort }
