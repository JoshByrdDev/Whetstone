# Restart the live board once its heartbeat has drained, then resume the heartbeat if it was running.
#
# Started by POST /api/heartbeat/restart-after-drain through explorer.exe (restart-board.bat), so it
# runs outside the board's kill-on-close job: it has to outlive the board it stops. Never start it
# from a Claude session for the same reason the board itself is never started from one.
#
# The board writes data/board.restart (JSON) before launching this. That file is the whole of the
# hand-off: this script reads the port and whether to resume from it, writes its phase back into it
# so the board's status can show where the restart is, and deletes it when done. The board deleting
# it (Cancel) is how a restart is called off while it is still waiting for the drain.
#
# Pure ASCII: PS 5.1 parses this file as cp1252.
param(
    [string]$DataRoot = $PSScriptRoot,
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$Marker = Join-Path $DataRoot 'board.restart'
$LogDir = Join-Path $DataRoot 'logs'
New-Item -ItemType Directory -Force $LogDir | Out-Null
$Log = Join-Path $LogDir 'restart-board.log'

function Say([string]$m) {
    Add-Content -Path $Log -Value ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m)
}

function Read-Marker {
    if (-not (Test-Path $Marker)) { return $null }
    try { return (Get-Content $Marker -Raw | ConvertFrom-Json) } catch { return $null }
}

function Set-Phase([string]$phase, [string]$err) {
    $m = Read-Marker
    if (-not $m) { return }
    $m | Add-Member -NotePropertyName phase -NotePropertyValue $phase -Force
    if ($err) { $m | Add-Member -NotePropertyName error -NotePropertyValue $err -Force }
    Set-Content -Path $Marker -Value ($m | ConvertTo-Json -Compress) -Encoding UTF8
}

$m = Read-Marker
if (-not $m) { Say 'no restart marker: nothing to do'; exit 0 }
$Port = if ($m.port) { [int]$m.port } else { 5338 }
$Resume = [bool]$m.resume
$Api = "http://localhost:$Port"

function Get-Status {
    try { return Invoke-RestMethod "$Api/api/heartbeat/status" -TimeoutSec 10 } catch { return $null }
}

try {
    # 1. Wait for the drain. The pending stop launches nothing new; the heartbeat exits once the
    #    agents it has out are done. Cancelling the stop, or deleting the marker, calls this off.
    Say "restart requested on port $Port (resume heartbeat: $Resume); waiting for the heartbeat to drain"
    $unreachable = 0
    while ($true) {
        if (-not (Test-Path $Marker)) { Say 'cancelled: the marker was removed'; exit 0 }
        $s = Get-Status
        if ($null -eq $s) {
            # A board that stops answering is a board that needs the restart anyway.
            $unreachable++
            if ($unreachable -ge 6) { Say 'the board stopped answering; restarting it'; break }
        } else {
            $unreachable = 0
            if (-not $s.running) { Say 'the heartbeat has stopped'; break }
            if (-not $s.stopRequested) {
                Say 'cancelled: the pending stop was cancelled'
                Remove-Item $Marker -Force -ErrorAction SilentlyContinue
                exit 0
            }
        }
        Start-Sleep -Seconds 10
    }
    if (-not (Test-Path $Marker)) { Say 'cancelled: the marker was removed'; exit 0 }

    # 2. Stop the board: the process listening on the port, and the dotnet run / Start-Whetstone.bat
    #    it was started under, so the console window closes with it.
    Set-Phase 'restarting'
    $listen = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listen) {
        $procs = @{}
        Get-CimInstance Win32_Process | ForEach-Object { $procs[[int]$_.ProcessId] = $_ }
        $top = $procs[[int]$listen.OwningProcess]
        $p = $top
        while ($p -and $procs.ContainsKey([int]$p.ParentProcessId)) {
            $parent = $procs[[int]$p.ParentProcessId]
            if (($parent.Name -eq 'dotnet.exe' -or $parent.Name -eq 'cmd.exe') -and $parent.CommandLine -match 'Whetstone') {
                $top = $parent; $p = $parent
            } else { break }
        }
        Say ('stopping {0} (pid {1}) and everything under it' -f $top.Name, $top.ProcessId)
        & taskkill.exe /T /F /PID $top.ProcessId | Out-Null
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 1
        }
    } else {
        Say "nothing is listening on $Port; starting it"
    }

    # 3. Start it again the way it is always started: Start-Whetstone.bat, through explorer, so no
    #    process this script belongs to owns it.
    Set-Phase 'starting'
    $bat = Join-Path $RepoRoot 'Start-Whetstone.bat'
    Say "starting $bat"
    Start-Process explorer.exe -ArgumentList ('"' + $bat + '"')
    # dotnet run builds before it listens, so this allows for a build.
    $deadline = (Get-Date).AddMinutes(10)
    $up = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $up = Get-Status
        if ($up) { break }
    }
    if (-not $up) {
        Say 'the board did not come back within 10 minutes'
        Set-Phase 'failed' 'The board did not come back within 10 minutes. See data/logs/restart-board.log, and start it with Start-Whetstone.bat.'
        exit 1
    }
    Say 'the board is back'

    # 4. Resume the heartbeat, if it was running when the restart was asked for.
    if ($Resume -and -not $up.running) {
        Set-Phase 'resuming'
        try {
            Invoke-RestMethod -Method Post "$Api/api/heartbeat/start" -TimeoutSec 30 | Out-Null
            Say 'heartbeat resumed'
        } catch {
            Say "could not resume the heartbeat: $_"
            Set-Phase 'failed' "The board restarted, but the heartbeat would not start: $_"
            exit 1
        }
    }
    Remove-Item $Marker -Force -ErrorAction SilentlyContinue
    Say 'done'
} catch {
    Say "failed: $_"
    Set-Phase 'failed' "The restart stopped part-way: $_. See data/logs/restart-board.log."
    exit 1
}
