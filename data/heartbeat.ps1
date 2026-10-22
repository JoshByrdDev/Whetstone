# ============================================================
# Claude Heartbeat Orchestrator (global, project-agnostic)
#
# ONE loop for ALL projects, running a small fleet of agents. Each pass asks the dashboard for the
# highest-priority workable ticket across every project (most-progressed first) and hands it to a
# free agent - worker.ps1, in its own process, in that ticket's own repo. It keeps asking until
# either every slot is full or the dashboard says there is nothing else workable, so the number of
# agents actually running is decided by how much work exists, not by anything a human dispatches.
#
# Backs off (and hibernates in off-hours) only when the whole fleet is idle AND nothing is claimable.
#
# The claim is the single point of coordination. Every agent gets its work from here, one at a time,
# and the dashboard leases each ticket exclusively - so there is no distributed race to lose, and
# adding agents cannot produce two workers on one branch.
#
# Usage: powershell -ExecutionPolicy Bypass -File heartbeat.ps1
# Stop:  Ctrl+C, or the dashboard "Stop After Cycle" button (lock auto-cleans)
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

$DataRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $DataRoot "heartbeat-common.ps1")
# Berths on other machines - worker hosts. Functions only; with no workerHosts configured every one
# of them finds nothing to do. See docs/worker-hosts-plan.md.
. (Join-Path $DataRoot "heartbeat-hosts.ps1")

# --- Constants ---
$LogRetentionDays = 7     # Delete logs older than this
$AgentPollSeconds = 10    # How often to check on the fleet while any agent is out

# How long a lease is left alone before the reconciler is willing to call it orphaned. It covers the
# one case the ownership test in Resolve-OrphanedClaims cannot see: a claim-next this orchestrator
# has already given up on may still be running on the dashboard, and the lease it has just taken is
# about to be handed back to us as a perfectly good claim.
$OrphanClaimGraceSeconds = 60

# --- Adaptive interval state (only used while the whole fleet is idle) ---
$CurrentHeartbeatMinutes = $BaseHeartbeatMinutes

# --- Sleep-notification state (see Send-SleepNotice) ---
#
# One notification per stretch of work, not one per empty pass. The loop reaches its sleep branch
# every time it wakes to a board with nothing on it, and a notification for each of those is a
# notification nobody reads - so the message is gated on an agent having actually run since the last
# one went out.
$script:WorkedSinceSleepNotice   = $false
$script:TicketsWorkedSinceNotice = @()

# --- Prerequisites ---
if (-not (Get-Command $AgentProvider.Exe -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: '$($AgentProvider.Exe)' command not found in PATH. Install the $($AgentProvider.DisplayName) CLI first, or change the agent provider in Settings." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $PromptFile)) {
    Write-Host "ERROR: Prompt file not found at $PromptFile" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $WorkerScript)) {
    Write-Host "ERROR: Worker script not found at $WorkerScript" -ForegroundColor Red
    exit 1
}

# An agent launched with -NoNewWindow writes into this console, which is what you want when a person
# is watching the fleet work; every line is tagged with its slot, so three at once still reads. When
# the dashboard starts the orchestrator there is no console at all, and -NoNewWindow would give each
# agent its own window instead - so in that case they are launched hidden and the log file is the
# record, which it is anyway.
$HasConsole = $true
try { $null = [Console]::WindowWidth } catch { $HasConsole = $false }

# --- Lock ---
# The lock records WHO holds it, not just when. Without the PID the dashboard can only tell that
# some heartbeat exists - it cannot stop one it did not itself spawn, so a Stop after a dashboard
# restart deleted this file, killed nothing, and left the process to wake from its sleep and carry
# on. Line 2 is our start time: a PID alone can be recycled onto an unrelated process, and killing
# that would be far worse than failing to stop.
#   line 1: process id
#   line 2: process start time (UTC, round-trip format)
#   line 3: last refresh (UTC, round-trip format)
function New-Lock {
    $start = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString("o")
    (@("$PID", $start, (Get-Date).ToUniversalTime().ToString("o")) -join "`n") |
        Out-File -FilePath $LockFile -NoNewline -Encoding ASCII
}
function Remove-Lock { if (Test-Path $LockFile) { Remove-Item $LockFile -Force } }

function Remove-OldLogs {
    Get-ChildItem -Path $LogDir -Filter "heartbeat-*.log" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$LogRetentionDays) } |
        Remove-Item -Force
}

# Sleep in slices, watching for the stop signal. A single Start-Sleep of 48 minutes means a stop
# pressed during the nap does nothing until the nap ends - which reads as "I stopped it and it
# started itself back up". Returns $false when a stop was requested.
function Start-InterruptibleSleep {
    # -WhileExists ends the sleep early, as though it had run its course, once that file is gone:
    # for a wait whose reason somebody else can remove.
    #
    # -Wakeable ends it early when the dashboard leaves a wake signal, and says so in
    # $script:WokenEarly so the caller can drop its backoff: there is known work, and the next
    # idle pass should not start from an hour. Only the idle sleeps pass it - a usage-limit pause
    # is not something new work can lift.
    param([int]$Seconds, [string]$WhileExists, [switch]$Wakeable)
    $script:WokenEarly = $false
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        if (Test-Path $StopFile) { return $false }
        if ($Wakeable -and (Test-Path $WakeFile)) {
            Remove-Item $WakeFile -Force -ErrorAction SilentlyContinue
            $script:WokenEarly = $true
            return $true
        }
        if ($WhileExists -and -not (Test-Path $WhileExists)) { return $true }
        $remaining = ($deadline - (Get-Date)).TotalSeconds
        if ($remaining -le 0) { return $true }
        Start-Sleep -Seconds ([Math]::Max(1, [Math]::Min(5, [Math]::Ceiling($remaining))))
    }
}

function Get-SleepUntilOffHoursEnd {
    $now = Get-Date
    $endToday = (Get-Date).Date + [TimeSpan]::Parse($OffHoursEnd)
    if ($endToday -le $now) { $endToday = $endToday.AddDays(1) }
    return [math]::Ceiling(($endToday - $now).TotalSeconds)
}

# Raised by Invoke-ClaimNext when the CALL failed, as against the dashboard answering "nothing to
# do". Both come back $null and the difference is invisible at the call site, but they mean opposite
# things afterwards: an empty queue has earned a longer sleep, while a failed call may have left a
# ticket claimed on the dashboard with nobody working it. Reset at the top of each fill loop.
$ClaimCallFailed = $false

# Ask the dashboard for the next ticket to work. Returns the "claimed" object or $null.
#
# Called once per free slot per pass. The dashboard hands back a DIFFERENT ticket each time because
# the claim takes an exclusive lease on whatever it returns - which is the one thing that makes
# calling it in a tight loop safe, and the reason the fleet needs no coordination of its own.
function Invoke-ClaimNext {
    try {
        $resp = Invoke-RestMethod -Uri "$DashboardUrl/api/heartbeat/claim-next" -Method Post `
            -ContentType $JsonContentType -Body (ConvertTo-JsonBody '{}') -TimeoutSec 15
        return $resp.claimed
    }
    catch {
        # Timing out here does NOT mean the claim did not happen: the dashboard commits the phase
        # move and the lease partway through the request, and this end gives up on its own clock.
        # Say so, and let the reconciler on the next pass find whatever was left behind.
        $script:ClaimCallFailed = $true
        Write-Log "  Could not reach dashboard claim-next: $($_.Exception.Message)"
        return $null
    }
}

# Hand back everything a previous run was still holding, minus the tickets named in KeepTicketIds.
# Safe to call ONLY here, at startup: the singleton guard has just confirmed no other orchestrator
# exists and this one has launched no agents yet, so a live lease belongs to a run that was killed
# rather than one that finished. Without it, a hard stop parks every in-flight ticket behind its own
# lease until the TTL runs out.
#
# The exception is an agent that outlived the orchestrator that launched it, whose lease is held by a
# process that is still writing. Those are found and adopted before this runs, and their ticket ids
# come in here to be spared - see Add-OrphanAgents.
function Reset-Leases {
    param([int[]]$KeepTicketIds = @())
    try {
        # @() around the array so a single kept ticket still serialises as a list rather than a bare
        # number, which the endpoint would refuse to bind.
        $body = @{ keepTicketIds = @($KeepTicketIds) } | ConvertTo-Json -Compress
        $r = Invoke-RestMethod -Uri "$DashboardUrl/api/heartbeat/leases/reset" -Method Post `
            -ContentType $JsonContentType -Body (ConvertTo-JsonBody $body) -TimeoutSec 10
        if ($r.released -gt 0) { Write-Log "Reclaimed $($r.released) lease(s) left behind by a previous run." }
    }
    catch { Write-Log "Could not reclaim old leases: $($_.Exception.Message)" }
}

# Everything the dashboard is currently handing out, or $null when it could not be asked. The
# difference matters more than it looks: an empty list means "nothing is held", and a call that
# failed must never be read that way by anyone about to release leases on the strength of it.
function Get-ActiveLeases {
    try {
        return @(Invoke-RestMethod -Uri "$DashboardUrl/api/leases" -Method Get -TimeoutSec 10)
    }
    catch {
        Write-Log "  Could not read leases: $($_.Exception.Message)"
        return $null
    }
}

# --- The fleet ---
# slot number (1..N) -> what that agent is doing. An absent key is a free berth.
$Agents = @{}

function Get-BusyCount { return $Agents.Count }

# Tickets being worked HERE, which is what the worker limit limits. The steward's berth is beside the
# worker slots, not one of them (see $StewardSlot), so it counts as busy and never as a worker; and
# a worker host's berths are past the steward's and limited by that host's own slot count.
function Get-WorkerCount {
    return @($Agents.Keys | Where-Object { $_ -lt $StewardSlot }).Count
}

function Get-FreeSlot {
    for ($i = 1; $i -le $HeartbeatWorkers; $i++) {
        if (-not $Agents.ContainsKey($i)) { return $i }
    }
    return 0
}

# Clear out agents whose process has exited, and say so. This is also where a slot is reported free
# to the dashboard: an agent killed mid-run never gets to send its own cycle_end, and without this
# its bot would sit on the board looking busy forever.
function Update-Fleet {
    foreach ($slot in @($Agents.Keys)) {
        # A worker host's run has no process here; Update-RemoteFleet below reads it from its host.
        if (Test-RemoteSlot $slot) { continue }
        $a = $Agents[$slot]
        if (-not $a.Process.HasExited) { continue }

        $mins = [math]::Round(((Get-Date) - $a.StartedAt).TotalMinutes, 1)

        # An unreadable exit code is not a failed one. Start-Agent caches the handle so this should
        # always have an answer now, but a code we could not read must never be reported as a crash:
        # that is noise in the one log somebody reads to find out whether the fleet is healthy.
        $code = $null
        try { $code = $a.Process.ExitCode } catch { }

        if ($slot -eq $StewardSlot) {
            Write-Log "The steward's run for [$($a.Project)] is over after ${mins}min$(if ($null -ne $code -and $code -ne 0) { " (exit code $code)" })."
            # The run gives its own lock back, and steward.ps1 does it again on the way out. This
            # is for the one case neither covers: the script itself was killed.
            Send-StewardAbandon -Project $a.Project -Reason "the steward's process ended"
            Send-DashboardEvent -Type "state_change" -Text "idle" -Agent $slot
            $Agents.Remove($slot)
            continue
        }

        if ($code -eq 0)         { Write-Log "Agent $slot finished ticket #$($a.TicketId) [$($a.Project)] after ${mins}min." }
        elseif ($null -eq $code) { Write-Log "Agent $slot finished ticket #$($a.TicketId) [$($a.Project)] after ${mins}min (exit code unavailable)." }
        else                     { Write-Log "Agent $slot exited with code $code on ticket #$($a.TicketId) [$($a.Project)] after ${mins}min." }

        Send-DashboardEvent -Type "state_change" -Text "idle" -Agent $slot
        # An adopted agent may have no claim file to clean up - it was launched by an orchestrator
        # that is no longer here to have written one this run.
        if ($a.ClaimFile) { Remove-Item $a.ClaimFile -Force -ErrorAction SilentlyContinue }
        $Agents.Remove($slot)
    }

    Update-RemoteFleet
}

# Give back any ticket the dashboard believes is being worked but nobody here is working.
#
# A claim is committed on the dashboard and acted on in this process, and those are two machines. If
# the claim-next call dies after the server has taken the lease - a timeout on this end while the
# server was still resolving the linked issue is how it actually happens - the ticket is left sitting in its
# "-ing" phase behind a live lease with no worker. The lease is then the very thing that stops any
# later pass from picking it up, so the ticket does not come back until the lease expires hours
# later, and the log meanwhile reports "No actionable tickets" quite truthfully.
#
# The comparison this makes - held by the dashboard, not held by any agent here - is one this
# process is entitled to make. The singleton guard means no second orchestrator can be claiming, and
# survivors of a previous one were adopted onto the books before the loop started, so a lease
# against a ticket no slot is running belongs to nobody. It also covers the neighbouring case:
# a worker killed before it could release what it held.
#
# Called only from the top of a pass, never from inside the fill loop, because a claim is taken and
# recorded in $Agents in one synchronous step there and nothing legitimate is ever briefly
# untracked. $OrphanClaimGraceSeconds covers the other side of the same timing: a claim-next we have
# given up on may still be running server-side, and its lease is left alone until it cannot be.
function Resolve-OrphanedClaims {
    $leases = Get-ActiveLeases
    if ($null -eq $leases) { return }   # could not ask - assume nothing rather than release blind
    if ($leases.Count -eq 0) { return }

    $mine = @($Agents.Values | ForEach-Object { "$($_.TicketId)" })
    # AcquiredAt comes back as UTC without an offset on it, so compare it against a UTC now.
    $cutoff = (Get-Date).ToUniversalTime().AddSeconds(-$OrphanClaimGraceSeconds)

    # A ticket can hold several leases (itself, plus whatever its skill needs exclusively) and the
    # release endpoint gives back all of a ticket's at once - so judge by ticket, taking the newest
    # of its leases as its age.
    $newest = @{}
    foreach ($l in $leases) {
        $id = "$($l.ticketId)"
        if ($id -eq "" -or $id -eq "0") { continue }
        # An unreadable timestamp counts as old rather than fresh. The grace period is a timing
        # guard and the ownership test is the real one; failing the other way would quietly switch
        # the whole reconciler off the first time the date format moved.
        $when = [datetime]::MinValue
        try { $when = [datetime]$l.acquiredAt } catch { }
        if (-not $newest.ContainsKey($id) -or $when -gt $newest[$id]) { $newest[$id] = $when }
    }

    foreach ($id in @($newest.Keys)) {
        if ($mine -contains $id) { continue }
        if ($newest[$id] -gt $cutoff) { continue }

        Write-Log "Ticket #$id is leased but no agent here is working it - releasing the orphaned claim."
        Send-LeaseRelease -TicketId $id -Reason "orphaned claim - the heartbeat holding it never started a worker"
    }
}

# Release a steward run's lock on the dashboard: one project's, or with no project every project's.
# Harmless when nothing is held, which is the ordinary case.
function Send-StewardAbandon {
    param([string]$Project = '', [string]$Reason = 'the run ended without closing itself')
    try {
        $q = "reason=$([Uri]::EscapeDataString($Reason))"
        if ($Project) { $q = "project=$([Uri]::EscapeDataString($Project))&$q" }
        Invoke-RestMethod -Uri "$DashboardUrl/api/steward/abandon?$q" -Method Post -TimeoutSec 10 | Out-Null
    }
    catch { Write-Log "  Could not release the steward lock: $($_.Exception.Message)" }
}

# Start a steward's run if some project is owed one and the steward's berth is free.
#
# Whether a run is owed is the dashboard's to say (GET /api/steward/due): a ticket came for review,
# one blocked, the owner answered a question, the base branch moved. Asking costs a few reads and
# no agent, so it is asked every pass - and a board where nothing has changed starts nothing, which
# is the whole difference between this and running one on a clock.
function Start-StewardIfDue {
    if ($Agents.ContainsKey($StewardSlot)) { return $false }
    if (-not (Test-Path $StewardScript)) { return $false }

    $due = $null
    # Enumerated and filtered, not just wrapped: Windows PowerShell hands a JSON array back as ONE
    # object, and an empty one as an object that is not nothing - so "@(...)" alone counts one due
    # project where there are none, and goes on to launch a steward for a project with no name.
    try {
        $due = @((Invoke-RestMethod -Uri "$DashboardUrl/api/steward/due" -Method Get -TimeoutSec 15) |
            ForEach-Object { $_ } | Where-Object { $_ -and $_.project })
    }
    catch { return $false }   # a dashboard that will not answer is asked again next pass
    if (-not $due -or $due.Count -eq 0) { return $false }

    $project = "$($due[0].project)"
    Write-Log "Steward [$project] is due: $($due[0].reason)"

    $psArgs = @("-ExecutionPolicy", "Bypass", "-File", $StewardScript, "-Project", $project, "-Agent", "$StewardSlot")
    try {
        $proc = if ($HasConsole) {
            Start-Process -FilePath "powershell" -ArgumentList $psArgs -PassThru -NoNewWindow
        } else {
            Start-Process -FilePath "powershell" -ArgumentList $psArgs -PassThru -WindowStyle Hidden
        }
    }
    catch {
        Write-Log "Could not launch the steward for [$project]: $($_.Exception.Message)"
        return $false
    }
    try { $null = $proc.Handle } catch { }   # see Start-Agent: keeps the exit code readable

    $Agents[$StewardSlot] = @{
        Process   = $proc
        TicketId  = ''
        Project   = $project
        Skill     = 'steward'
        StartedAt = Get-Date
        ClaimFile = $null
    }
    return $true
}

# A steward left running by an orchestrator that died is taken onto the books like a worker is, and
# for a simpler reason: it holds its project's steward lock, and it will give it back when it ends.
# With none running, any lock still held belongs to a process that is gone, and is released.
function Add-OrphanSteward {
    $found = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { Test-RunsScript -CommandLine $_.CommandLine -Script $StewardScript })
    foreach ($o in $found) {
        $project = if ($o.CommandLine -match '-Project\s+"?([A-Za-z0-9._-]+)') { $Matches[1] } else { '' }
        $proc = $null
        try { $proc = Get-Process -Id $o.ProcessId -ErrorAction Stop; $null = $proc.Handle } catch { }
        if ($proc -and $project -and -not $Agents.ContainsKey($StewardSlot)) {
            Write-Log "A steward run for [$project] is still going from a previous orchestrator (pid $($o.ProcessId)) - tracking it."
            $Agents[$StewardSlot] = @{ Process = $proc; TicketId = ''; Project = $project; Skill = 'steward'; StartedAt = Get-Date; ClaimFile = $null }
            Send-DashboardEvent -Type "state_change" -Text "working" -Agent $StewardSlot -Project $project -Phase "Steward"
        }
    }
    if (-not $Agents.ContainsKey($StewardSlot)) { Send-StewardAbandon -Reason "orchestrator restarted" }
}

# Put one claimed ticket in front of one agent. The claim is written to disk rather than passed on
# the command line: it carries the ticket's description, its master notes and a rendered issue diff,
# which is far past what a command line can hold and full of characters a shell would eat.
function Start-Agent {
    param($Claim, [int]$Slot)

    $ticketId = "$($Claim.ticket.id)"
    $project  = "$($Claim.project.key)"
    $claimFile = Join-Path $RunDir "claim-$Slot.json"

    try {
        ($Claim | ConvertTo-Json -Depth 15) | Set-Content -Path $claimFile -Encoding UTF8
    }
    catch {
        Write-Log "Could not stage the claim for ticket #${ticketId}: $($_.Exception.Message)"
        # The ticket is claimed and leased with nobody to work it. Give it straight back so the next
        # pass can pick it up rather than leaving it locked behind an agent that never started.
        Send-LeaseRelease -TicketId $ticketId -Reason "agent could not be started"
        return $false
    }

    # Not $args: that is an automatic variable, and quietly shadowing it inside a function is the
    # kind of thing that works until someone adds a parameter-less call below it.
    # -TicketId is not read as input by the worker; it is stamped on the command line so that an
    # orchestrator starting up later can identify a survivor of this run. See Find-OrphanAgents.
    $psArgs = @("-ExecutionPolicy", "Bypass", "-File", $WorkerScript, "-ClaimFile", $claimFile,
                "-Agent", "$Slot", "-TicketId", "$ticketId")
    try {
        $proc = if ($HasConsole) {
            Start-Process -FilePath "powershell" -ArgumentList $psArgs -PassThru -NoNewWindow
        } else {
            Start-Process -FilePath "powershell" -ArgumentList $psArgs -PassThru -WindowStyle Hidden
        }
    }
    catch {
        Write-Log "Could not launch agent ${Slot}: $($_.Exception.Message)"
        Send-LeaseRelease -TicketId $ticketId -Reason "agent could not be started"
        return $false
    }

    # Touch the handle while the process is still alive. Start-Process -PassThru hands back a
    # Process object with no open handle, and once the agent exits there is nothing left for .NET to
    # read the exit code out of - ExitCode comes back empty, and Update-Fleet used to read that
    # emptiness as "did not exit 0" and log every clean finish as a crash.
    try { $null = $proc.Handle } catch { }

    $Agents[$Slot] = @{
        Process   = $proc
        TicketId  = $ticketId
        Project   = $project
        Phase     = "$($Claim.ticket.phase)"
        ClaimFile = $claimFile
        StartedAt = Get-Date
    }
    Write-Log "Agent $Slot -> ticket #$ticketId [$project] $($Claim.ticket.phase) / $($Claim.skill) (pid $($proc.Id))"
    return $true
}

# Drain the fleet: wait for every agent to finish the phase it is on. Used by the graceful stop -
# the agents already out are left to finish rather than killed, because a ticket abandoned mid-phase
# in a half-written worktree is the state this pipeline is worst at recovering from. Each agent is
# one process working one phase, so "finished" needs nothing asked of it: it exits, and the slot it
# frees is simply never refilled while the stop stands. The dashboard's hard Stop is the button for
# "now", and it kills the whole tree.
#
# Returns $true when the fleet is empty and the shutdown should go ahead, $false when the stop was
# CANCELLED while draining - the dashboard offers that button for the whole of this wait, and until
# now it deleted the file to no effect: the drain ran to the end and the heartbeat stopped anyway,
# several agent-hours after the person watching had been told the stop was called off.
function Wait-ForFleet {
    while ($true) {
        # The flag, not the count, is the authority. Checking it first means a cancel that lands in
        # the same poll as the last agent's exit still resumes, rather than stopping on a
        # technicality nobody could have seen.
        if (-not (Test-Path $StopFile)) {
            Write-Log "  Stop request cancelled - resuming with $(Get-BusyCount) agent(s) still working."
            return $false
        }
        Update-Fleet
        if ((Get-BusyCount) -eq 0) { return $true }
        # A drain is bounded by the worker timeout, not by anything this loop controls, so it can be
        # the longest stretch of the whole run. Keep the lock's heartbeat honest across it.
        New-Lock
        Write-Log "  Waiting for $(Get-BusyCount) agent(s) to finish..."
        Start-Sleep -Seconds $AgentPollSeconds
    }
}

# --- Orphaned agents ---
# Agents are separate PROCESSES, deliberately (see the header of worker.ps1), and nothing makes them
# children of this one. The dashboard's Stop kills the whole tree and Stop After Cycle waits them
# out, so both supported ways of stopping take the fleet with them - but an orchestrator that dies on
# its own (a crash, a closed console window, one pid killed by hand) leaves its agents running.
#
# That is the one case where the startup lease reset is wrong. Wiping a live worker's ticket lease
# puts its ticket back in play while it is still writing in the worktree, and the next agent to pick
# it up finds a dirty tree - which Resolve-Worktree refuses to start on, so the ticket does not just
# get worked twice, it lands in Blocked and waits for a human.
#
# Killing the survivors instead is the simpler answer and it fails the same way: a worker cut off
# mid-write never reaches the commit net at the end of its cycle, so it leaves behind exactly the
# dirty tree that blocks the ticket. So let them finish, and take them onto the books - the fleet
# then counts them against its worker limit, reaps them, and reports them like anything it launched.
function Find-OrphanAgents {
    # Match only processes actually RUNNING the worker, the same care the singleton guard takes and
    # for a sharper reason: a process that merely MENTIONS worker.ps1 - a `Get-CimInstance ... -Filter
    # ... worker.ps1` diagnostic, say, where "-Filter" itself contains "-File" - would be read as an
    # unnameable orphan and killed. So require -File to actually point at the script, and exclude
    # anything running a -Command string.
    $found = @()
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            # This checkout's worker.ps1 only: a worker host's runner on the same machine runs the
            # same file name from its own checkout, for another board's tickets. See Test-RunsScript.
            $_.ProcessId -ne $PID -and (Test-RunsScript -CommandLine $_.CommandLine -Script $WorkerScript)
        })

    foreach ($p in $procs) {
        # Preferred source for both: bare integers on the command line, so neither depends on parsing
        # a quoted path, and the ticket id does not depend on a claim file that a later run may
        # already have overwritten. The fallback below covers agents launched before -TicketId.
        $slot     = if ($p.CommandLine -match '-Agent\s+(\d+)')    { [int]$Matches[1] } else { 0 }
        $ticketId = if ($p.CommandLine -match '-TicketId\s+(\d+)') { [int]$Matches[1] } else { 0 }

        $proc = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
        if (-not $proc) { continue }   # finished between the scan and here; nothing to adopt

        $project = "?"
        $phase = "?"
        $claimFile = if ($slot -gt 0) { Join-Path $RunDir "claim-$slot.json" } else { $null }
        if ($claimFile -and (Test-Path $claimFile)) {
            try {
                $c = Get-Content -Path $claimFile -Raw | ConvertFrom-Json
                $claimTicket = [int]$c.ticket.id

                # An agent launched before -TicketId existed does not carry one, and killing it for
                # that would make the first restart after this change the one that breaks. The claim
                # file is a good enough second source: it is named after the slot and only rewritten
                # when a new agent takes that slot, and the orchestrator that would have done that is
                # the one that just died.
                if ($ticketId -le 0) { $ticketId = $claimTicket }

                # Only describe the run if the two agree. If they do not, the file has been reused by
                # a later agent and everything in it except the id we already trust is about someone
                # else - so leave the log honest about not knowing rather than name the wrong ticket.
                if ($claimTicket -eq $ticketId) {
                    $project = "$($c.project.key)"
                    $phase   = "$($c.ticket.phase)"
                }
            }
            catch { }
        }

        $found += @{
            Process = $proc; Pid = $p.ProcessId; Slot = $slot
            TicketId = $ticketId; Project = $project; Phase = $phase; ClaimFile = $claimFile
        }
    }
    return $found
}

# Take the survivors onto the books. Returns the ticket ids whose leases must NOT be reset - which is
# every one we managed to adopt, and deliberately none of the ones we could not.
function Add-OrphanAgents {
    $orphans = @(Find-OrphanAgents)
    if ($orphans.Count -eq 0) { return @() }

    Write-Log "$($orphans.Count) agent(s) are still running from a previous orchestrator."
    $keep = @()

    foreach ($o in $orphans) {
        # Prefer the berth it was launched into; it is free anyway, unless the worker count was
        # lowered since. Nothing has launched yet, so this only ever falls through on that edge.
        $slot = if ($o.Slot -ge 1 -and $o.Slot -le $HeartbeatWorkers -and -not $Agents.ContainsKey($o.Slot)) {
            $o.Slot
        } else {
            Get-FreeSlot
        }

        # A survivor we cannot name, or cannot find a berth for, is the dangerous kind: its lease is
        # about to be reset and there would be nothing tracking it when the ticket is handed out
        # again. Losing its work to a kill is the lesser outcome, so it goes - loudly.
        $why = if ($o.TicketId -le 0) { "it did not say which ticket it is working" }
               elseif ($slot -eq 0)   { "there is no free agent slot to track it in" }
               else                   { $null }

        if ($why) {
            Write-Log "  Stopping orphan agent pid $($o.Pid): $why."
            try { Stop-Process -Id $o.Pid -Force -ErrorAction Stop }
            catch { Write-Log "    Could not stop pid $($o.Pid): $($_.Exception.Message)" }
            continue
        }

        # Same reason Start-Agent does it: without an open handle there is nothing to read an exit
        # code out of once the process ends. Update-Fleet treats a code it cannot read as unknown
        # rather than as a crash, so failing here costs a clear log line and nothing else.
        try { $null = $o.Process.Handle } catch { }

        $started = try { $o.Process.StartTime } catch { Get-Date }
        $Agents[$slot] = @{
            Process   = $o.Process
            TicketId  = "$($o.TicketId)"
            Project   = $o.Project
            Phase     = $o.Phase
            ClaimFile = $o.ClaimFile
            StartedAt = $started
        }
        $keep += $o.TicketId
        # An adopted agent is work in flight just as much as one we launched: when it finishes and
        # the board empties, that is the end of a real stretch of work and worth saying so.
        Add-WorkedTicket -TicketId "$($o.TicketId)" -Project $o.Project

        $mins = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        Write-Log "  Adopted agent pid $($o.Pid) into slot $slot - ticket #$($o.TicketId) [$($o.Project)] $($o.Phase), running ${mins}min. Its lease stands."
        # Name the ticket on the adoption event: the dashboard's Start just cleared every slot, and
        # this is the only thing that puts the survivor back on the board with the right ticket
        # before its next tool call happens to mention it. Project and phase only when actually
        # known - "?" is this script admitting ignorance, not a value worth displaying.
        $adoptEvent = @{ Type = "state_change"; Text = "working"; Agent = $slot; TicketId = "$($o.TicketId)" }
        if ($o.Project -ne "?") { $adoptEvent.Project = $o.Project }
        if ($o.Phase -ne "?")   { $adoptEvent.Phase   = $o.Phase }
        Send-DashboardEvent @adoptEvent
    }

    return $keep
}

# Remember that an agent actually ran. Without this the fleet going quiet is indistinguishable from
# the fleet having been quiet all along, and only the first of those is worth a message.
function Add-WorkedTicket {
    param([string]$TicketId, [string]$Project)
    $script:WorkedSinceSleepNotice = $true
    $label = if ($Project -and $Project -ne "?") { "$Project #$TicketId" } else { "#$TicketId" }
    # A ticket resumed over several passes is one ticket, not three.
    if ($script:TicketsWorkedSinceNotice -notcontains $label) { $script:TicketsWorkedSinceNotice += $label }
}

# Say the fleet has gone quiet - once per stretch of work.
#
# $Next is what happens now, in the caller's own words: the two sleeps are different facts, and
# "back in 6 min" and "back at 06:30 tomorrow" are read very differently at 11pm.
function Send-SleepNotice {
    param([string]$Next, [string]$Headline = "Heartbeat is going to sleep - no actionable tickets left.")
    if (-not $script:WorkedSinceSleepNotice) { return }

    $worked = @($script:TicketsWorkedSinceNotice)
    $lines  = @($Headline)
    if ($worked.Count -gt 0) {
        $shown = if ($worked.Count -gt 10) { (@($worked[0..9]) -join ", ") + ", +$($worked.Count - 10) more" }
                 else                      { $worked -join ", " }
        $plural = if ($worked.Count -eq 1) { "ticket" } else { "tickets" }
        $lines += "Worked $($worked.Count) $plural this stretch: $shown"
    }
    if ($Next)         { $lines += $Next }
    if ($DashboardUrl) { $lines += $DashboardUrl }
    Send-Notification -EventKey "heartbeat_sleep" -Title "Whetstone heartbeat asleep" -Message ($lines -join "`n")

    # Cleared whether or not anything was actually posted. The gate is "has work happened since the
    # fleet last went quiet", and it has now gone quiet either way; leaving the flag up would only
    # move the message to the next empty pass, which is the thing this exists to prevent.
    $script:WorkedSinceSleepNotice   = $false
    $script:TicketsWorkedSinceNotice = @()
}

# --- Singleton guard ---
# Never run two orchestrators at once: concurrent loops would both claim and both reap, and the
# fleet accounting is per-process. If another heartbeat.ps1 is already running, exit now rather than
# clobbering the lock and piling on. (This is why duplicates accumulated across dashboard restarts:
# the old startup blindly cleared the lock and proceeded.)
# Match only processes actually RUNNING the script (launched with -File ...heartbeat.ps1), not
# arbitrary commands that merely mention the filename (e.g. a `Get-Process | where CommandLine`
# diagnostic), which would otherwise be a false positive. The agents are launched with worker.ps1
# on their command line, so they never match this. And only THIS checkout's heartbeat.ps1: a second
# Whetstone on the same machine is a different board with an orchestrator of its own.
$others = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessId -ne $PID -and (Test-RunsScript -CommandLine $_.CommandLine -Script (Join-Path $DataRoot 'heartbeat.ps1')) })
if ($others.Count -gt 0) {
    Write-Host "Another heartbeat is already running (PID $($others[0].ProcessId)) - exiting to avoid duplicates." -ForegroundColor Yellow
    exit 0
}

# --- Graceful shutdown ---
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { Remove-Lock }
trap { Write-Log "Interrupted. Cleaning up..."; Remove-Lock; exit 0 }

# No other orchestrator is running, so any lock on disk is stale - clear it and hold our own.
if (Test-Path $LockFile) { Write-Host "Clearing leftover lock file from previous run."; Remove-Item $LockFile -Force }
# Same reasoning for a stop signal: it was raised against the run that has just been shown not to
# exist, so it is not an instruction to this one. The dashboard clears it on Start, but a heartbeat
# launched from a console does not go through the dashboard - and without this, the first thing it
# would do is drain an empty fleet and exit within the second, for reasons only the log explains.
if (Test-Path $StopFile) { Write-Host "Clearing leftover stop signal from previous run."; Remove-Item $StopFile -Force -ErrorAction SilentlyContinue }
New-Lock
Remove-OldLogs

$notifyStatus = Get-NotificationStatus
Write-Log "========================================="
Write-Log " Claude Heartbeat Orchestrator (all projects)"
Write-Log " Agents        : up to $HeartbeatWorkers at once (max $MaxHeartbeatWorkers)"
Write-Log " Base interval : ${BaseHeartbeatMinutes}min  (idle x${HeartbeatIdleMultiplier}, max ${HeartbeatMaxMinutes}min)"
Write-Log " Off-hours     : ${OffHoursStart} - ${OffHoursEnd}"
Write-Log " Dashboard MCP : $DashboardUrl/mcp"
Write-Log " Notifications : $notifyStatus"
Write-Log "========================================="

# Both of these have to happen before the reset below, and in this order. Adoption decides which
# leases are still genuinely held, and it is also what says which claim files are still in use by a
# running agent - so the stale-claim sweep can only run once the survivors are on the books.
$keepTickets = Add-OrphanAgents
# The same for runs on worker hosts, read from what their runners last reported. $null means the
# hosts could not be asked, and then nothing is reset at all: releasing blind would hand a live
# remote run's ticket to a second run.
$remoteKeep = Add-RemoteAgents

$inUse = @($Agents.Values | ForEach-Object { $_.ClaimFile } | Where-Object { $_ })
Get-ChildItem -Path $RunDir -Filter "claim-*.json" -ErrorAction SilentlyContinue |
    Where-Object { $inUse -notcontains $_.FullName } | Remove-Item -Force

if ($null -eq $remoteKeep) {
    Write-Log "Could not read the worker hosts - leaving every lease as it is rather than resetting blind."
}
else {
    Reset-Leases -KeepTicketIds (@($keepTickets) + @($remoteKeep))
}
Add-OrphanSteward

# --- Main loop: fill every free slot, then watch; back off only when the whole fleet is idle ---
while ($true) {
    # Graceful stop (dashboard "Stop After Cycle" button)
    if (Test-Path $StopFile) {
        Write-Log "Stop signal detected. No new work will be claimed."
        if (Wait-ForFleet) {
            Remove-Item $StopFile -Force -ErrorAction SilentlyContinue
            Send-DashboardEvent -Type "state_change" -Text "idle"
            break
        }
        continue   # cancelled while draining - carry on where we left off
    }

    New-Lock  # refresh timestamp so a long-running ticket never looks stale
    # A wake signal is for a sleeping orchestrator. This pass is about to look at the queue anyway,
    # so one left while the fleet was busy is spent here rather than cutting short a later sleep
    # that has nothing to do with it.
    if (Test-Path $WakeFile) { Remove-Item $WakeFile -Force -ErrorAction SilentlyContinue }
    Update-Fleet

    # Take back anything claimed but abandoned before asking for more - most often the ticket a
    # claim-next on the previous pass left behind when the call timed out after the server had
    # already committed it. Released here, it is re-claimed by the fill loop directly below.
    Resolve-OrphanedClaims

    # Fill the fleet. Keep asking while there is a berth free: the dashboard hands back the next
    # workable ticket each time and null when there is nothing left, so the fleet sizes itself to
    # the work rather than to a number somebody picked. One busy project can legitimately occupy
    # every agent, and one lonely ticket legitimately runs alone.
    $launched = 0
    $ClaimCallFailed = $false

    # A worker was refused by the account's usage limit. Every claim before the reset would be
    # refused the same way, so none is made: agents already out finish, and once the fleet is empty
    # the orchestrator sleeps straight through to the reset rather than backing off in steps.
    $limitUntil = Get-UsageLimitPause
    if ($limitUntil) {
        # The limit is the board host's account's. A worker host spends its own, and reports its
        # own limit, which the API reads into whether it is dispatchable - so its berths still fill.
        $null = Start-Fill -LocalOpen $false
        if ((Get-BusyCount) -gt 0) {
            $null = Start-InterruptibleSleep -Seconds $AgentPollSeconds   # a stop is handled at the top
            continue
        }
        # Two minutes past the stated time: the reset is quoted to the hour, not the second.
        $sleepSeconds = [math]::Ceiling(($limitUntil - (Get-Date)).TotalSeconds) + 120
        $wakeTime = (Get-Date).AddSeconds($sleepSeconds).ToString("yyyy-MM-dd HH:mm")
        Write-Log "Usage limit reached - claiming nothing until the reset (wake at $wakeTime)."
        Send-DashboardEvent -Type "sleep" -DurationSeconds $sleepSeconds -Text "Usage limit until $wakeTime"
        Send-DashboardEvent -Type "state_change" -Text "sleeping"
        Send-SleepNotice -Headline "Heartbeat paused - the account hit its usage limit." -Next "Waking at $wakeTime."
        # Switching the dashboard to another Claude account deletes the pause, since the limit was
        # the old account's - so the sleep watches for that rather than running out the clock.
        if (-not (Start-InterruptibleSleep -Seconds $sleepSeconds -WhileExists $UsageLimitFile)) { continue }   # the stop is handled at the top
        Remove-Item $UsageLimitFile -Force -ErrorAction SilentlyContinue
        continue
    }

    # This machine and every worker host, in the order and the way the Heartbeat page says - see
    # Start-Fill in heartbeat-hosts.ps1.
    $launched += Start-Fill

    # A stop that landed mid-fill goes round to the top rather than being handled here: that is the
    # one place the drain lives, and it keeps this pass from announcing a sleep it will not take.
    if (Test-Path $StopFile) { continue }

    # After the workers, in its own berth. A failure here is never allowed to cost the pass.
    try { $null = Start-StewardIfDue }
    catch { Write-Log "ERROR starting a steward run: $($_.Exception.Message)" }

    if ((Get-BusyCount) -gt 0) {
        # Work is in flight. Poll on a short interval rather than the heartbeat interval: a slot
        # that frees up should be refilled in seconds, and sleeping for minutes here would leave
        # agents idle while the queue behind them is full.
        $CurrentHeartbeatMinutes = $BaseHeartbeatMinutes
        if (-not (Start-InterruptibleSleep -Seconds $AgentPollSeconds)) {
            Write-Log "Stop signal detected while agents are working."
            if (Wait-ForFleet) {
                Remove-Item $StopFile -Force -ErrorAction SilentlyContinue
                Send-DashboardEvent -Type "state_change" -Text "idle"
                break
            }
        }
        continue
    }

    # Nothing workable and nobody working - back off, and hibernate through off-hours once maxed out.
    Write-Log "No actionable tickets across any project."
    Send-DashboardEvent -Type "state_change" -Text "idle"

    # Back off only when the queue is genuinely empty. A claim that FAILED is indistinguishable from
    # an empty queue at the call site - both are $null - but it may have left a ticket claimed with
    # nobody working it, and the reconciler at the top of the NEXT pass is what gives that back.
    # Doubling the interval would put that pass up to an hour away, which is most of the stall this
    # is here to fix, so a pass that hit an error holds the interval at its base instead.
    if ($ClaimCallFailed) {
        if ($CurrentHeartbeatMinutes -ne $BaseHeartbeatMinutes) {
            Write-Log "  A claim call failed this pass - holding at ${BaseHeartbeatMinutes}min rather than backing off, to reconcile what it may have left claimed."
        }
        $CurrentHeartbeatMinutes = $BaseHeartbeatMinutes
    }
    else {
        $previous = $CurrentHeartbeatMinutes
        $CurrentHeartbeatMinutes = [math]::Min([math]::Ceiling($CurrentHeartbeatMinutes * $HeartbeatIdleMultiplier), $HeartbeatMaxMinutes)
        if ($previous -ne $CurrentHeartbeatMinutes) { Write-Log "  Idle backoff ${previous}min -> ${CurrentHeartbeatMinutes}min" }
    }

    if ($CurrentHeartbeatMinutes -ge $HeartbeatMaxMinutes -and (Test-OffHours)) {
        $sleepSeconds = Get-SleepUntilOffHoursEnd
        $wakeTime = (Get-Date).AddSeconds($sleepSeconds).ToString("yyyy-MM-dd HH:mm")
        Write-Log "Off-hours hibernation - sleeping until ${OffHoursEnd} (wake at $wakeTime)"
        Send-DashboardEvent -Type "sleep" -DurationSeconds $sleepSeconds -Text "Off-hours until $wakeTime"
        Send-DashboardEvent -Type "state_change" -Text "off-hours"
        Send-SleepNotice -Next "Off-hours until ${OffHoursEnd} - waking at $wakeTime."
        $slept = Start-InterruptibleSleep -Seconds $sleepSeconds -Wakeable
    }
    else {
        Write-Log "Sleeping $CurrentHeartbeatMinutes min..."
        Send-DashboardEvent -Type "sleep" -DurationSeconds ($CurrentHeartbeatMinutes * 60) -Text "Sleeping ${CurrentHeartbeatMinutes}m"
        Send-DashboardEvent -Type "state_change" -Text "sleeping"
        Send-SleepNotice -Next "Next check in ${CurrentHeartbeatMinutes} min."
        $slept = Start-InterruptibleSleep -Seconds ($CurrentHeartbeatMinutes * 60) -Wakeable
    }

    if ($script:WokenEarly) {
        Write-Log "Woken by the dashboard: there is new work to look at."
        $CurrentHeartbeatMinutes = $BaseHeartbeatMinutes
        Send-DashboardEvent -Type "state_change" -Text "idle"
    }

    # Woken early by a stop request - act on it now rather than starting another cycle first.
    if (-not $slept) {
        Write-Log "Stop signal detected during sleep. Shutting down."
        Remove-Item $StopFile -Force -ErrorAction SilentlyContinue
        Send-DashboardEvent -Type "state_change" -Text "idle"
        break
    }
}

Remove-Lock
