# Restart the live board once its heartbeat has drained, then resume the heartbeat if it was running.
#
# Started by POST /api/heartbeat/restart-after-drain outside the board's own process tree - through
# explorer.exe (restart-board.bat) on Windows, under nohup on a Mac (BoardRestart) - because it has
# to outlive the board it stops. Never start it from a Claude session for the same reason the board
# itself is never started from one.
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

# The process to stop for the board whose listener is $ListenerPid: the listener, walked up through
# what launched it - dotnet run, and Start-Whetstone.bat (cmd) or Start-Whetstone.command (zsh, then
# caffeinate) - so the launch goes with it and its console window closes. $Procs maps a pid to
# @{ Parent; Name; Command }. Stops at the first parent that is not part of that launch.
function Select-BoardRoot {
    param([hashtable]$Procs, [int]$ListenerPid)
    $top = $ListenerPid
    while ($Procs.ContainsKey($top)) {
        $parentId = [int]$Procs[$top].Parent
        if (-not $Procs.ContainsKey($parentId)) { break }
        $parent = $Procs[$parentId]
        $launch = $parent.Name -match '^(dotnet(\.exe)?|cmd\.exe|caffeinate|zsh|sh|bash)$'
        if ($launch -and "$($parent.Command)" -match 'Whetstone') { $top = $parentId } else { break }
    }
    return $top
}

# The pid listening on the port, or $null.
function Get-ListenerPid([int]$port) {
    if ($IsWindows) {
        $l = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($l) { return [int]$l.OwningProcess }
        return $null
    }
    $found = & lsof -nP "-iTCP:$port" -sTCP:LISTEN -t 2>$null | Select-Object -First 1
    if ($found) { return [int]$found }
    return $null
}

# Every process, as Select-BoardRoot reads them.
function Get-ProcessTable {
    $procs = @{}
    if ($IsWindows) {
        Get-CimInstance Win32_Process | ForEach-Object {
            $procs[[int]$_.ProcessId] = @{ Parent = [int]$_.ParentProcessId; Name = $_.Name; Command = $_.CommandLine }
        }
        return $procs
    }
    foreach ($line in (& ps -axww -o pid=,ppid=,args=)) {
        if ($line -match '^\s*(\d+)\s+(\d+)\s+(.*)$') {
            $cmd = $Matches[3]
            $exe = ($cmd -split '\s+')[0]
            $procs[[int]$Matches[1]] = @{ Parent = [int]$Matches[2]; Name = (Split-Path $exe -Leaf).TrimStart('-'); Command = $cmd }
        }
    }
    return $procs
}

# A test dot-sources the functions above and stops here.
if ($env:WHETSTONE_RESTART_FUNCTIONS_ONLY) { return }

# What starts the board on this machine, the way a person does.
$Launcher = if ($IsWindows) { Join-Path $RepoRoot 'Start-Whetstone.bat' } else { Join-Path $RepoRoot 'Start-Whetstone.command' }
$LauncherName = Split-Path $Launcher -Leaf

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

    # 2. Stop the board: the process listening on the port, and the dotnet run and launcher it was
    #    started under, so the console window closes with it.
    Set-Phase 'restarting'
    $listener = Get-ListenerPid $Port
    if ($listener) {
        $procs = Get-ProcessTable
        $top = Select-BoardRoot -Procs $procs -ListenerPid $listener
        $name = if ($procs.ContainsKey($top)) { $procs[$top].Name } else { '?' }
        Say ('stopping {0} (pid {1}) and everything under it' -f $name, $top)
        if ($IsWindows) {
            & taskkill.exe /T /F /PID $top | Out-Null
        } else {
            try { [System.Diagnostics.Process]::GetProcessById($top).Kill($true) } catch { Say "could not stop pid ${top}: $_" }
        }
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-ListenerPid $Port) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 1
        }
    } else {
        Say "nothing is listening on $Port; starting it"
    }

    # 3. Start it again the way it is always started, so no process this script belongs to owns it:
    #    Start-Whetstone.bat through explorer, or Start-Whetstone.command through open, which hands
    #    it to Terminal.
    Set-Phase 'starting'
    Say "starting $Launcher"
    if ($IsWindows) {
        Start-Process explorer.exe -ArgumentList ('"' + $Launcher + '"')
    } else {
        & open $Launcher
    }
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
        Set-Phase 'failed' "The board did not come back within 10 minutes. See data/logs/restart-board.log, and start it with $LauncherName."
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
