# ============================================================
# The orchestrator's worker hosts: berths on other machines (docs/worker-hosts-plan.md, piece 5).
#
# A worker host's berths are slots numbered past the steward's ($StewardSlot), in blocks the board
# host's API hands out from `workerHosts` in its config. The orchestrator fills them the way it fills
# its own - one claim per free berth - but a start is an ORDER placed with the API, which that host's
# runner (data/runner.ps1) picks up on its next check-in, and "has it finished" is read from what the
# runner last reported rather than from a process handle.
#
# An entry in $Agents for a remote slot has no Process. It carries Host and RunId instead, and
# Seen once the runner has reported the run as started.
#
# Dot-sourced by heartbeat.ps1 after heartbeat-common.ps1. Functions only.
# Tests: powershell -NoProfile -ExecutionPolicy Bypass -File data/heartbeat-hosts.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

# How long a start order may go unseen before the orchestrator decides it never ran.
$RemoteStartGraceSeconds = 120

# How long a host may be unreachable before its runs are given up on and their tickets released.
# Longer than a reboot, shorter than an afternoon of the queue waiting on a machine that is off.
$HostOfflineReleaseMinutes = 10

# What the API last said about every worker host, or $null when it could not be asked.
$script:LastHosts = $null

function Test-RemoteSlot { param([int]$Slot) return $Slot -gt $StewardSlot }

# Every worker host as GET /api/hosts describes it, or $null when it could not be asked. $null is
# "do not know", which every caller reads as "change nothing" - never as "there are none".
function Get-WorkerHosts {
    try {
        $script:LastHosts = @(Invoke-RestMethod -Uri "$DashboardUrl/api/hosts" -Method Get -TimeoutSec 10)
    }
    catch {
        # A 404 is an app not yet restarted onto worker hosts, which therefore has none. Read as
        # "unknown" it would skip the startup lease reset on every start until somebody restarted it.
        $status = $null
        try { $status = [int]$_.Exception.Response.StatusCode } catch { }
        if ($status -eq 404) { $script:LastHosts = @() }
        else {
            Write-Log "  Could not read the worker hosts: $($_.Exception.Message)"
            $script:LastHosts = $null
        }
    }
    # The comma keeps an empty list a list: returned bare, PowerShell unrolls @() to $null, and a
    # board with no worker hosts would read as one whose hosts could not be asked.
    if ($null -eq $script:LastHosts) { return $null }
    return ,$script:LastHosts
}

# Agents on remote berths.
function Get-RemoteCount { return @($Agents.Keys | Where-Object { Test-RemoteSlot $_ }).Count }

# The first of a host's berths with nothing in it, or 0.
function Get-RemoteFreeSlot {
    param($HostView)
    for ($i = [int]$HostView.firstSlot; $i -lt [int]$HostView.firstSlot + [int]$HostView.slots; $i++) {
        if (-not $Agents.ContainsKey($i)) { return $i }
    }
    return 0
}

function Invoke-HostClaim {
    param($HostView)
    try {
        $body = @{ host = "$($HostView.name)" } | ConvertTo-Json -Compress
        $resp = Invoke-RestMethod -Uri "$DashboardUrl/api/heartbeat/claim-next" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 15
        return $resp.claimed
    }
    catch {
        # As for a board host claim: a timeout here does not mean nothing was claimed. The
        # reconciler at the top of the next pass gives back whatever this left behind.
        $script:ClaimCallFailed = $true
        Write-Log "  Could not reach claim-next for worker host $($HostView.name): $($_.Exception.Message)"
        return $null
    }
}

function Start-RemoteAgent {
    param($Claim, [int]$Slot, [string]$HostName)

    $ticketId = "$($Claim.ticket.id)"
    $project  = "$($Claim.project.key)"
    $runId    = [int]$Claim.runId
    if ($runId -le 0) {
        # A claim from an app that predates run ids cannot be handed back safely. Give it back.
        Write-Log "  Claim for ticket #$ticketId carries no run id - not sending it to $HostName."
        Send-LeaseRelease -TicketId $ticketId -Reason "claim carried no run id for a worker host"
        return $false
    }

    $body = @{ kind = 'start'; slot = $Slot; runId = $runId; claim = $Claim } | ConvertTo-Json -Depth 20 -Compress
    try {
        $null = Invoke-RestMethod -Uri "$DashboardUrl/api/hosts/$HostName/orders" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 15
    }
    catch {
        Write-Log "  Could not order ticket #$ticketId onto $HostName slot ${Slot}: $($_.Exception.Message)"
        Send-LeaseRelease -TicketId $ticketId -Reason "could not order it onto worker host $HostName"
        return $false
    }

    $Agents[$Slot] = @{
        Process   = $null
        Host      = $HostName
        RunId     = $runId
        Seen      = $false
        TicketId  = $ticketId
        Project   = $project
        Phase     = "$($Claim.ticket.phase)"
        ClaimFile = $null
        StartedAt = Get-Date
    }
    Write-Log "Agent $Slot ($HostName) -> ticket #$ticketId [$project] $($Claim.ticket.phase) / $($Claim.skill) (run $runId)"
    return $true
}

# Fill every worker host's free berths. Worker hosts go before the board host, so a single ticket
# runs on the bigger machine and the board host keeps its headroom for the person using it.
# Returns how many were started.
function Start-RemoteFill {
    $launched = 0
    if ($null -eq $script:LastHosts) { return 0 }
    foreach ($h in $script:LastHosts) {
        if (-not $h.dispatchable) { continue }
        while ($true) {
            if (Test-Path $StopFile) { return $launched }
            $slot = Get-RemoteFreeSlot -HostView $h
            if ($slot -eq 0) { break }
            $claim = Invoke-HostClaim -HostView $h
            if (-not $claim) { break }
            if (Start-RemoteAgent -Claim $claim -Slot $slot -HostName "$($h.name)") {
                $launched++
                Add-WorkedTicket -TicketId "$($claim.ticket.id)" -Project "$($claim.project.key)"
            }
            else { break }
        }
    }
    return $launched
}

# Clear out remote agents that are over, from what the hosts last reported. Called from
# Update-Fleet, which does the same for the board host's own.
function Update-RemoteFleet {
    $hosts = Get-WorkerHosts
    if ($null -eq $hosts) { return }   # could not ask: change nothing
    $now = Get-Date

    foreach ($slot in @($Agents.Keys | Where-Object { Test-RemoteSlot $_ })) {
        $a = $Agents[$slot]
        $h = @($hosts | Where-Object { "$($_.name)" -eq $a.Host })[0]
        $mins = [math]::Round(($now - $a.StartedAt).TotalMinutes, 1)

        $why = $null
        $release = $false
        if (-not $h) {
            $why = "its host '$($a.Host)' is no longer in the board's config"
            $release = $true
        }
        elseif (-not $h.online) {
            $last = $null
            try { if ($h.lastCheckin) { $last = ([datetime]$h.lastCheckin).ToLocalTime() } } catch { }
            $since = if ($last) { $last } else { $a.StartedAt }
            if (($now - $since).TotalMinutes -lt $HostOfflineReleaseMinutes) { continue }
            $why = "$($a.Host) has not checked in for over $HostOfflineReleaseMinutes min"
            $release = $true
        }
        else {
            $run = @($h.runs | Where-Object { [int]$_.runId -eq [int]$a.RunId })[0]
            if ($run -and -not $run.exited) {
                if (-not $a.Seen) { $a.Seen = $true }
                continue
            }
            if ($run) {
                $code = $run.exitCode
                if ($code -eq 0)         { Write-Log "Agent $slot ($($a.Host)) finished ticket #$($a.TicketId) [$($a.Project)] after ${mins}min." }
                elseif ($null -eq $code) { Write-Log "Agent $slot ($($a.Host)) finished ticket #$($a.TicketId) [$($a.Project)] after ${mins}min (exit code unavailable)." }
                else                     { Write-Log "Agent $slot ($($a.Host)) exited with code $code on ticket #$($a.TicketId) [$($a.Project)] after ${mins}min." }
            }
            elseif ($a.Seen) {
                Write-Log "Agent $slot ($($a.Host)) on ticket #$($a.TicketId) is no longer reported - taking it as finished."
            }
            elseif (($now - $a.StartedAt).TotalSeconds -lt $RemoteStartGraceSeconds) {
                continue   # ordered, not yet picked up
            }
            else {
                $why = "$($a.Host) never started it within ${RemoteStartGraceSeconds}s"
                $release = $true
            }
        }

        if ($release) {
            Write-Log "Agent $slot ($($a.Host)) on ticket #$($a.TicketId): giving up - $why. Releasing its claim."
            Send-LeaseRelease -TicketId $a.TicketId -Reason "worker host run given up: $why"
        }
        Send-DashboardEvent -Type "state_change" -Text "idle" -Agent $slot
        $Agents.Remove($slot)
    }
}

# At startup: take every run a worker host still reports onto the books, BEFORE the lease reset.
# Without this, restarting the orchestrator would release every ticket a worker host is in the
# middle of, and the next claim would hand it to a second run. Returns their ticket ids, or $null
# when the hosts could not be read - which the caller must treat as "do not reset at all".
function Add-RemoteAgents {
    $hosts = Get-WorkerHosts
    if ($null -eq $hosts) { return $null }
    $keep = @()
    foreach ($h in $hosts) {
        foreach ($run in @($h.runs)) {
            if (-not $run -or $run.exited) { continue }
            $slot = [int]$run.slot
            if ($Agents.ContainsKey($slot)) { continue }
            $Agents[$slot] = @{
                Process = $null; Host = "$($h.name)"; RunId = [int]$run.runId; Seen = $true
                TicketId = "$($run.ticketId)"; Project = "$($run.project)"; Phase = '?'
                ClaimFile = $null; StartedAt = $(try { ([datetime]$run.startedAt).ToLocalTime() } catch { Get-Date })
            }
            $keep += [int]$run.ticketId
            Add-WorkedTicket -TicketId "$($run.ticketId)" -Project "$($run.project)"
            Write-Log "  Adopted run $($run.runId) on $($h.name) into slot $slot - ticket #$($run.ticketId) [$($run.project)]. Its lease stands."
        }
    }
    return ,$keep
}
