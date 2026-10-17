# ============================================================
# The runner: what a worker host runs so the board host's orchestrator can use its berths.
#
# A worker host is another machine on the LAN that runs workers for a board and hands their
# branches back (docs/worker-hosts-plan.md). There is still one orchestrator, on the board host.
# This loop is the worker host's half of it: it checks in with the board host's API, receives
# orders in the response ("start this claim in slot 5", "kill run 812"), and runs worker.ps1 for
# each start exactly as the orchestrator's Start-Agent does on the board host.
#
# It pulls; nothing listens here. Every check-in reports everything this runner is holding, so the
# board host's view of it is rebuilt from scratch on each one and survives an API restart.
#
# Never start it from a Claude session. Whatever owns this process's tree can kill every worker
# under it, which is the same lesson the live instance taught (CLAUDE.md, "Build and run"). Start
# it at logon from Task Scheduler - see docs/worker-host-setup.md.
#
# Usage: powershell -ExecutionPolicy Bypass -File data/runner.ps1 [-HostFile <path>]
# Stop:  create data/runner.stop. It takes nothing new and exits once its runs have finished.
#
# Config: data/host.json (machine-local, gitignored) names this host and maps each project to its
# clone here; data/config.json sets dashboardUrl (the board host's API) and claudeAccount.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    [string]$HostFile = '',
    # One check-in and nothing more. For trying a new host.json out by hand.
    [switch]$Once
)

$DataRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $DataRoot "heartbeat-common.ps1")

$ErrorActionPreference = 'Continue'
$env:GCM_INTERACTIVE     = 'never'
$env:GIT_TERMINAL_PROMPT = '0'

$RunnerStopFile = Join-Path $DataRoot "runner.stop"
$RunnerKeepExitedMinutes = 10

# ---- Pure helpers (data/runner.tests.ps1 lifts these) ---------------------------------------

# host.json, checked. Returns @{ ok; name; projects = @{ key = @{ path; worktreeRoot } }; reason }.
function Read-HostConfig {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return @{ ok = $false; reason = "no host file at $Path" } }
    try { $raw = Get-Content -Path $Path -Raw | ConvertFrom-Json }
    catch { return @{ ok = $false; reason = "could not read ${Path}: $($_.Exception.Message)" } }

    $name = "$($raw.name)".Trim()
    if ($name -notmatch '^[A-Za-z0-9_.-]{1,64}$') { return @{ ok = $false; reason = "host.json needs a 'name' of letters, digits, dash, underscore or dot" } }

    $projects = @{}
    if ($raw.projects) {
        foreach ($prop in $raw.projects.PSObject.Properties) {
            $path = "$($prop.Value.path)".Trim()
            if (-not $path) { continue }
            $projects[$prop.Name] = @{ path = $path; worktreeRoot = "$($prop.Value.worktreeRoot)".Trim() }
        }
    }
    if ($projects.Count -eq 0) { return @{ ok = $false; reason = "host.json maps no projects to a clone on this machine" } }
    return @{ ok = $true; name = $name; projects = $projects }
}

# The claim as this machine must read it. Every path in it is the board host's, so each one is
# pointed at this host's own: its clone, its worktree root, the project kit unpacked under this
# data root. And it is stamped with the host's name, which is the one thing that puts worker.ps1 on
# its worker host paths (see $onHost there).
function Convert-ClaimForHost {
    param($Claim, [string]$HostName, $Map, [string]$ProjectDir)

    # [IO.Path] rather than Join-Path, which on PowerShell 5.1 refuses a drive this process cannot
    # see - and these are paths for a worker, not for whatever drives the runner happens to have.
    $p = $Claim.project
    $key = "$($p.key)"
    $repo = $Map.path.TrimEnd('\', '/')
    $root = if ($Map.worktreeRoot) { $Map.worktreeRoot } else { [IO.Path]::Combine([IO.Path]::GetDirectoryName($repo), '.worktrees', $key) }
    $skillsDir = [IO.Path]::Combine($ProjectDir, 'skills')
    $notes = [IO.Path]::Combine($ProjectDir, '_notes')

    # The board host's data root, before the paths are rewritten: the prompt tells the worker that
    # a path the ticket tools give under it is at the same place under this machine's.
    $boardData = "$($p.dataDir)".Trim()
    if ($boardData) { $boardData = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($boardData.TrimEnd('\', '/'))) }
    $Claim | Add-Member -NotePropertyName boardDataRoot -NotePropertyValue $boardData -Force

    $p.projectPath  = $repo
    $p.worktreeRoot = $root
    $p.dataDir      = $ProjectDir
    $p.skillsDir    = $skillsDir
    $p.skillFile    = [IO.Path]::Combine($skillsDir, "$($Claim.skill)", 'skill.md')
    if ("$($p.memoryDir)".Trim()) {
        $p.memoryDir   = $notes
        $p.memoryIndex = [IO.Path]::Combine($notes, 'MEMORY.md')
    }
    $Claim | Add-Member -NotePropertyName host -NotePropertyValue $HostName -Force
    return $Claim
}

# Replace a project's kit with a fresh one. Only what the kit carries is replaced - its skills, its
# guard and its notes - so nothing else in the folder is touched (there should be nothing else).
function Install-ProjectKit {
    param([string]$ZipPath, [string]$ProjectDir)
    New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null
    foreach ($old in @('skills', '_notes', 'guard.ps1')) {
        $target = Join-Path $ProjectDir $old
        if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Expand-Archive -Path $ZipPath -DestinationPath $ProjectDir -Force
}

# What a check-in says about the runs this runner holds.
function Get-RunReport {
    param([hashtable]$Runs)
    $report = @()
    foreach ($slot in @($Runs.Keys | Sort-Object)) {
        $r = $Runs[$slot]
        $exited = $false
        $code = $null
        if ($r.Process) {
            if ($r.Process.HasExited) { $exited = $true; try { $code = $r.Process.ExitCode } catch { } }
        }
        elseif ($r.Pid) {
            # Adopted from an earlier runner: no handle, so no exit code - only whether it is alive.
            $exited = -not (Get-Process -Id $r.Pid -ErrorAction SilentlyContinue)
        }
        if ($null -ne $r.ExitCode) { $exited = $true; $code = $r.ExitCode }
        $report += @{
            slot = [int]$slot; ticketId = [int]$r.TicketId; runId = [int]$r.RunId; project = "$($r.Project)"
            pid = $r.Pid; exited = $exited; exitCode = $code; startedAt = $r.StartedAt.ToUniversalTime().ToString('o')
        }
    }
    return ,$report
}

# ---- The runner -----------------------------------------------------------------------------

function Get-OwnCommit {
    $repo = Split-Path -Parent $DataRoot
    $sha = "$(git -C $repo rev-parse HEAD 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0) { return $null }
    return $sha
}

# worker.ps1 processes left by an earlier runner, by their command lines: the same match the
# orchestrator's Find-OrphanAgents uses, plus the -RunId the runner stamped on them.
function Find-SurvivingRuns {
    $found = @{}
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessId -ne $PID -and
            $_.CommandLine -notlike '*-Command*' -and
            $_.CommandLine -match '-File\s+"?[^"]*worker\.ps1'
        })
    foreach ($p in $procs) {
        $slot  = if ($p.CommandLine -match '-Agent\s+(\d+)')    { [int]$Matches[1] } else { 0 }
        $tid   = if ($p.CommandLine -match '-TicketId\s+(\d+)') { [int]$Matches[1] } else { 0 }
        $runId = if ($p.CommandLine -match '-RunId\s+(\d+)')    { [int]$Matches[1] } else { 0 }
        if ($slot -le 0 -or $runId -le 0) { continue }
        $project = '?'
        $claimFile = Join-Path $RunDir "claim-$slot.json"
        if (Test-Path $claimFile) { try { $project = "$((Get-Content $claimFile -Raw | ConvertFrom-Json).project.key)" } catch { } }
        $found[$slot] = @{ Process = $null; Pid = $p.ProcessId; TicketId = $tid; RunId = $runId; Project = $project
                           StartedAt = $(try { [datetime]$p.CreationDate } catch { Get-Date }); ExitCode = $null; ExitedAt = $null }
    }
    return $found
}

function Stop-Run {
    param($Run, [string]$Why)
    $target = if ($Run.Process) { $Run.Process.Id } else { $Run.Pid }
    if (-not $target) { return }
    Write-Log "  Killing run $($Run.RunId) (ticket #$($Run.TicketId), pid $target): $Why"
    & taskkill /PID $target /T /F *> $null
}

function Start-Run {
    param($Order, $HostCfg)

    $slot   = [int]$Order.slot
    $claim  = $Order.claim
    $key    = "$($claim.project.key)"
    $ticket = "$($claim.ticket.id)"

    if ($Runs.ContainsKey($slot) -and -not $Runs[$slot].ExitedAt) {
        Write-Log "  Slot $slot is still running run $($Runs[$slot].RunId) - refusing to start ticket #$ticket over it."
        return
    }
    $map = $HostCfg.projects[$key]
    $why = $null
    if (Test-Path $RunnerStopFile) { $why = "this runner is stopping" }
    elseif (-not $map) { $why = "host.json maps no clone for project '$key'" }
    elseif (-not (Test-Path (Join-Path $map.path '.git'))) { $why = "no git clone at $($map.path)" }

    $projectDir = Join-Path (Join-Path $DataRoot 'projects') $key
    if (-not $why) {
        $zip = Join-Path $RunDir "kit-$key-$slot.zip"
        try {
            Invoke-WebRequest -Uri "$DashboardUrl/api/hosts/project-kit/$key" -OutFile $zip -UseBasicParsing -TimeoutSec 60
            Install-ProjectKit -ZipPath $zip -ProjectDir $projectDir
        }
        catch { $why = "could not fetch the project kit: $($_.Exception.Message)" }
        finally { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
    }
    if ($why) {
        Write-Log "  Cannot start ticket #$ticket in slot ${slot}: $why - giving the claim back."
        Send-LeaseRelease -TicketId $ticket -Reason "worker host $($HostCfg.name) could not start it: $why"
        $Runs[$slot] = @{ Process = $null; Pid = $null; TicketId = $ticket; RunId = $Order.runId; Project = $key
                          StartedAt = Get-Date; ExitCode = 3; ExitedAt = Get-Date }
        return
    }

    $claim = Convert-ClaimForHost -Claim $claim -HostName $HostCfg.name -Map $map -ProjectDir $projectDir
    $claimFile = Join-Path $RunDir "claim-$slot.json"
    ($claim | ConvertTo-Json -Depth 15) | Set-Content -Path $claimFile -Encoding UTF8

    $psArgs = @("-ExecutionPolicy", "Bypass", "-File", $WorkerScript, "-ClaimFile", $claimFile,
                "-Agent", "$slot", "-TicketId", "$ticket", "-RunId", "$($Order.runId)")
    try { $proc = Start-Process -FilePath "powershell" -ArgumentList $psArgs -PassThru -WindowStyle Hidden }
    catch {
        Write-Log "  Could not launch worker for ticket #${ticket}: $($_.Exception.Message)"
        Send-LeaseRelease -TicketId $ticket -Reason "worker host $($HostCfg.name) could not launch a worker"
        $Runs[$slot] = @{ Process = $null; Pid = $null; TicketId = $ticket; RunId = $Order.runId; Project = $key
                          StartedAt = Get-Date; ExitCode = 3; ExitedAt = Get-Date }
        return
    }
    # Touch the handle while the process is alive, or ExitCode is unreadable later (see Start-Agent).
    try { $null = $proc.Handle } catch { }
    $Runs[$slot] = @{ Process = $proc; Pid = $proc.Id; TicketId = $ticket; RunId = $Order.runId; Project = $key
                      StartedAt = Get-Date; ExitCode = $null; ExitedAt = $null }
    Write-Log "Slot $slot -> ticket #$ticket [$key] $($claim.ticket.phase) / $($claim.skill) (run $($Order.runId), pid $($proc.Id))"
}

# Read the usage-limit pause a worker here wrote, if it is still in force. A worker writes it into
# this machine's run/ - the account it hit is this machine's - and the board host is told on the
# next check-in, which pauses this host's berths and no others.
function Get-HostUsageLimit {
    if (-not (Test-Path $UsageLimitFile)) { return $null }
    $until = $null
    try { $until = [datetime](Get-Content -Path $UsageLimitFile -Raw | ConvertFrom-Json).resetsAt } catch { }
    if ($until -and $until -gt (Get-Date)) { return $until.ToUniversalTime().ToString('o') }
    Remove-Item $UsageLimitFile -Force -ErrorAction SilentlyContinue
    return $null
}

# Everything below runs only when this file is executed, not when the tests lift its functions.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $HostFile) { $HostFile = Join-Path $DataRoot 'host.json' }
$HostCfg = Read-HostConfig -Path $HostFile
if (-not $HostCfg.ok) { Write-Log "Runner cannot start: $($HostCfg.reason)"; exit 1 }

$single = New-Object System.Threading.Mutex($false, "Local\CDH-runner")
if (-not $single.WaitOne(0)) { Write-Log "Another runner is already running on this machine."; exit 1 }

$script:AgentSlot = 0
$commit = Get-OwnCommit
Write-Log "Runner '$($HostCfg.name)' on commit $commit, board host $DashboardUrl, projects: $(@($HostCfg.projects.Keys) -join ', ')"

$Runs = Find-SurvivingRuns
foreach ($slot in $Runs.Keys) { Write-Log "  Adopted run $($Runs[$slot].RunId) in slot $slot (ticket #$($Runs[$slot].TicketId), pid $($Runs[$slot].Pid))" }

$failures = 0
while ($true) {
    # Finished runs are reported for a while and then forgotten; the orchestrator reads an absent
    # run as finished too, so dropping one never strands a slot.
    foreach ($slot in @($Runs.Keys)) {
        $r = $Runs[$slot]
        if (-not $r.ExitedAt) {
            $gone = if ($r.Process) { $r.Process.HasExited } else { -not (Get-Process -Id $r.Pid -ErrorAction SilentlyContinue) }
            if ($gone) {
                $r.ExitedAt = Get-Date
                $code = if ($r.Process) { try { $r.Process.ExitCode } catch { $null } } else { $null }
                Write-Log "Slot $slot finished ticket #$($r.TicketId) (run $($r.RunId), exit $code)."
            }
        }
        elseif (((Get-Date) - $r.ExitedAt).TotalMinutes -ge $RunnerKeepExitedMinutes) { $Runs.Remove($slot) }
    }

    $live = @($Runs.Values | Where-Object { -not $_.ExitedAt }).Count
    if ((Test-Path $RunnerStopFile) -and $live -eq 0) {
        Write-Log "Stop file present and nothing running - runner exiting."
        Remove-Item $RunnerStopFile -Force -ErrorAction SilentlyContinue
        break
    }

    $body = @{
        commit = $commit
        runs = (Get-RunReport -Runs $Runs)
        usageLimitUntil = (Get-HostUsageLimit)
        waitSeconds = $(if ($Once) { 0 } else { 20 })
        dataRoot = $DataRoot
    } | ConvertTo-Json -Depth 6 -Compress

    $resp = $null
    try {
        $resp = Invoke-RestMethod -Uri "$DashboardUrl/api/hosts/$($HostCfg.name)/checkin" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 60
        if ($failures -gt 0) { Write-Log "Board host reachable again after $failures failed check-in(s)." }
        $failures = 0
    }
    catch {
        $failures++
        if ($failures -eq 1 -or $failures % 30 -eq 0) { Write-Log "Check-in failed ($failures in a row): $($_.Exception.Message)" }
        if ($Once) { exit 1 }
        Start-Sleep -Seconds 5
        continue
    }

    # Runs whose lease is gone: the orchestrator gave up on them while this host was unreachable,
    # and their tickets may be running elsewhere by now. Killed before they can hand anything back.
    foreach ($rid in @($resp.revoked)) {
        foreach ($slot in @($Runs.Keys)) {
            $r = $Runs[$slot]
            if ([int]$r.RunId -eq [int]$rid -and -not $r.ExitedAt) { Stop-Run -Run $r -Why "its lease was released on the board host" }
        }
    }

    foreach ($order in @($resp.orders)) {
        if (-not $order) { continue }
        switch ("$($order.kind)") {
            'start' { Start-Run -Order $order -HostCfg $HostCfg }
            'kill' {
                foreach ($slot in @($Runs.Keys)) {
                    $r = $Runs[$slot]
                    if ([int]$r.RunId -eq [int]$order.runId -and -not $r.ExitedAt) { Stop-Run -Run $r -Why "ordered by the board host" }
                }
            }
            default { Write-Log "  Ignoring an order of unknown kind '$($order.kind)'." }
        }
    }

    if ($Once) { break }
}

$single.ReleaseMutex()
exit 0
