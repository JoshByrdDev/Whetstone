# ============================================================
# Bring a worker host up to date by hand, in one go: stop the runner, move this checkout to the
# board host's commit, start the runner again. What Update-Runner.bat beside the repo root runs.
#
# The runner does this by itself after every commit on the board host (see Get-UpdateAction and
# Update-OwnCheckout in runner.ps1). This is for when it cannot: the runner has died, it was stopped
# and not started again, or its update was refused (tracked files modified here). It uses the
# runner's own update, so it moves to the same commit by the same rules - exactly the board host's,
# following a rewritten history with the old commit kept as a runner-backup/* branch, and never over
# modified tracked files.
#
# It restarts the scheduled task that runs runner.ps1, which an update by the runner itself cannot
# do - so this is also how a change to the runner's supervisor lines takes effect without a logon.
#
# Usage: double-click Update-Runner.bat, or
#        pwsh -ExecutionPolicy Bypass -File data/update-runner.ps1 [-WaitMinutes 60]
#
# Never from a Claude session: the runner it starts would belong to that session's tree.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    # How long to wait for runs in progress here to finish before giving up and leaving it running.
    [int]$WaitMinutes = 60,
    # The runner's host file, when it is not data\host.json. Read from the scheduled task when not given.
    [string]$HostFile = ''
)

# Copied before runner.ps1 is dot-sourced: its own param block resets $HostFile.
$UpdaterWait = $WaitMinutes
$UpdaterHostFile = $HostFile
$RunnerScript = Join-Path $PSScriptRoot 'runner.ps1'
# The runner's functions and settings, without its loop: it returns when dot-sourced.
. $RunnerScript
$ErrorActionPreference = 'Continue'

# The runner processes running this checkout's runner.ps1: the supervisor and the runner itself.
function Get-RunnerProcesses {
    @(Get-ProcessTable -Shells |
        Where-Object { $_.ProcessId -ne $PID -and (Test-RunsScript -CommandLine $_.CommandLine -Script $RunnerScript) })
}

# The -HostFile a command line passes runner.ps1, or ''.
function Get-HostFileArg {
    param([string]$CommandLine)
    if ("$CommandLine" -match '-HostFile\s+(?:"([^"]+)"|(\S+))') { return $(if ($Matches[1]) { $Matches[1] } else { $Matches[2] }) }
    return ''
}

# Every runner.ps1 on this computer that is not this checkout's - a scheduled task's or a running
# one's - as the folder of the checkout it is in. A machine that also runs a board of its own keeps
# the runner in a checkout of its own (docs/worker-host-setup.md, step 8), and double-clicking this
# in the other one is the easy mistake.
function Find-OtherRunners {
    $mine = [IO.Path]::GetFullPath($RunnerScript)
    $lines = @()
    foreach ($t in @(Get-ScheduledTask -ErrorAction SilentlyContinue)) {
        foreach ($a in @($t.Actions)) { $lines += "$($a.Execute) $($a.Arguments)" }
    }
    foreach ($p in @(Get-ProcessTable -Shells)) { $lines += "$($p.CommandLine)" }
    $found = @()
    foreach ($l in $lines) {
        if ($l -notmatch '-File\s+(?:"([^"]+[\\/]data[\\/]runner\.ps1)"|(\S+[\\/]data[\\/]runner\.ps1))') { continue }
        $file = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
        try { $file = [IO.Path]::GetFullPath($file) } catch { continue }
        if ($file -ieq $mine) { continue }
        $found += (Split-Path -Parent (Split-Path -Parent $file))
    }
    return @($found | Sort-Object -Unique)
}

# Which host file the runner reads: the one given, else the one its scheduled task passes, else
# data\host.json - the same default as the runner's own.
$task = Find-RunnerTask -Script $RunnerScript
$hostFile = $UpdaterHostFile
if (-not $hostFile -and $task) {
    foreach ($a in @($task.Actions)) { $arg = Get-HostFileArg "$($a.Arguments)"; if ($arg) { $hostFile = $arg } }
}
if (-not $hostFile) { $hostFile = Join-Path $DataRoot 'host.json' }
$hostCfg = Read-HostConfig -Path $hostFile
if (-not $hostCfg.ok) {
    Write-Host "This checkout ($(Split-Path -Parent $DataRoot)) is not a worker host's: $($hostCfg.reason). Nothing was changed." -ForegroundColor Yellow
    $others = @(Find-OtherRunners)
    if ($others.Count -gt 0) {
        Write-Host ""
        Write-Host "The runner on this computer runs from another checkout:" -ForegroundColor Yellow
        foreach ($o in $others) { Write-Host "  $o  - double-click $o\Update-Runner.bat there" }
    }
    else {
        Write-Host "No scheduled task or running process on this computer runs a runner.ps1 either. If its host file is elsewhere, pass it: Update-Runner.bat -HostFile <path>"
    }
    exit 1
}
Write-Log "Host file: $hostFile"

$repo = Split-Path -Parent $DataRoot
Write-Log "Updating worker host '$($hostCfg.name)' by hand (update-runner.ps1)."

# ---- 1. Stop the runner, letting runs in progress here finish ----
$running = @(Get-RunnerProcesses)
if ($running.Count -gt 0) {
    Write-Log "Stopping the runner: it takes nothing new and exits once its runs here have finished."
    Set-Content -Path $RunnerStopFile -Value 'update-runner.ps1' -Encoding ascii
    $deadline = (Get-Date).AddMinutes($UpdaterWait)
    $said = Get-Date
    while (@(Get-RunnerProcesses).Count -gt 0) {
        if ((Get-Date) -gt $deadline) {
            Remove-Item $RunnerStopFile -Force -ErrorAction SilentlyContinue
            Write-Log "The runner is still finishing runs after $UpdaterWait min. Left it running, unchanged; try again later."
            exit 1
        }
        if (((Get-Date) - $said).TotalSeconds -ge 30) {
            Write-Host "  still waiting for its runs to finish..." -ForegroundColor DarkGray
            $said = Get-Date
        }
        Start-Sleep -Seconds 3
    }
    Write-Log "The runner has stopped."
}
else { Write-Log "The runner is not running." }
Remove-Item $RunnerStopFile -Force -ErrorAction SilentlyContinue

# ---- 2. Move this checkout to the board host's commit ----
$target = $null
try { $target = "$((Invoke-RestMethod -Uri "$DashboardUrl/api/hosts/settings" -Method Get -TimeoutSec 15).boardCommit)".Trim() }
catch { Write-Log "Could not ask the board host ($DashboardUrl) for its commit: $($_.Exception.Message)" }
if (-not $target) {
    git -C $repo fetch --quiet origin 2>$null
    $target = "$(git -C $repo rev-parse --verify --quiet origin/HEAD 2>$null)".Trim()
    if (-not $target) { $target = "$(git -C $repo rev-parse --verify --quiet origin/main 2>$null)".Trim() }
    if ($target) { Write-Log "Using origin's main instead: $target." }
}
$own = "$(git -C $repo rev-parse HEAD 2>$null)".Trim()
$moved = $false
if (-not $target) {
    Write-Log "Could not tell which commit to move to. This checkout stays on $own."
}
elseif ($own -ieq $target) {
    Write-Log "Already on the board host's commit $target."
}
else {
    $result = Update-OwnCheckout -Repo $repo -Sha $target -FollowRewrites $hostCfg.followRewrites
    switch ($result.state) {
        'moved'   { Write-Log "Moved this checkout from $own to $target."; $moved = $true }
        'rewound' { Write-Log "The board host's history was rewritten: moved from $own to $target. The old commit is kept as branch $($result.backup)."; $moved = $true }
        'dirty'   {
            Write-Log "Not moved: $($result.reason). Those changes are left as they are:"
            git -C $repo status --short --untracked-files=no
            Write-Log "Commit or undo them (git -C $repo checkout -- <file>), then run this again. Until then the board gives this host no work."
        }
        default   { Write-Log "Not moved: $($result.reason)." }
    }
}

# ---- 3. Start the runner again ----
if ($task) {
    # A task set up before the fleet left Windows PowerShell 5.1 still starts powershell.exe.
    try {
        if ((Move-RunnerTaskToPwsh -Task $task) -eq 'moved') { Write-Log "The scheduled task '$($task.TaskName)' started the runner on Windows PowerShell 5.1; it now starts it on PowerShell 7 ($PsHost)." }
    }
    catch { Write-Log "Could not point the scheduled task '$($task.TaskName)' at PowerShell 7: $($_.Exception.Message). Change its action to $PsHost by hand (docs/worker-host-setup.md, step 12)." }
    if ($task.State -eq 'Running') { Stop-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue }
    Start-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath
    Write-Log "Started the scheduled task '$($task.TaskName)'."
}
else {
    $runArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $RunnerScript)
    if ($hostFile -ine (Join-Path $DataRoot 'host.json')) { $runArgs += @('-HostFile', $hostFile) }
    Start-Process -FilePath $PsHost -ArgumentList $runArgs -WindowStyle Hidden | Out-Null
    Write-Log "No scheduled task runs $RunnerScript, so the runner was started directly. It will not come back after a logoff - see docs/worker-host-setup.md, step 12."
}

$deadline = (Get-Date).AddSeconds(30)
while (@(Get-RunnerProcesses).Count -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
if (@(Get-RunnerProcesses).Count -gt 0) {
    $now = "$(git -C $repo rev-parse --short HEAD 2>$null)".Trim()
    Write-Log "The runner is running on commit $now$(if (-not $moved -and $target -and $own -ine $target) { ' - still not the board host''s, so it gets no work yet' })."
    exit 0
}
Write-Log "The runner did not start. Its log is in $LogDir."
exit 1
