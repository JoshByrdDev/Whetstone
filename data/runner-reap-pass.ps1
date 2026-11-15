# ============================================================
# One pass of reaping this worker host's worktrees, in a process of its own. The runner starts it
# (Start-ReapPass in runner-reap.ps1) and does not wait for it: removing a worktree full of installed
# packages can take minutes, and the runner's loop must keep reading start orders meanwhile. What a
# pass does, and why it is safe beside running workers, is at the top of runner-reap.ps1.
#
# Exits 0 when it is done, $ReapMoreExit (10) when it stopped with more to do - the runner then
# starts the next one in a few minutes rather than an hour. One at a time on a machine.
#
# Never started by hand or from a Claude session; the runner starts it.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    [string]$HostFile = '',
    [int]$BudgetSeconds = 600
)

$DataRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $DataRoot "heartbeat-common.ps1")
. (Join-Path $DataRoot "worker-board.ps1")
. (Join-Path $DataRoot "worker-repos.ps1")
. (Join-Path $DataRoot "runner-reap.ps1")

$ErrorActionPreference = 'Continue'
$env:GCM_INTERACTIVE     = 'never'
$env:GIT_TERMINAL_PROMPT = '0'
$script:AgentSlot = 0

$single = New-Object System.Threading.Mutex($false, "Local\CDH-runner-reap")
if (-not $single.WaitOne(0)) { exit 0 }

try {
    if (-not $HostFile) { $HostFile = Join-Path $DataRoot 'host.json' }
    $raw = Get-Content -LiteralPath $HostFile -Raw | ConvertFrom-Json
    $hostName = "$($raw.name)".Trim()
    $projects = @{}
    foreach ($prop in @($raw.projects.PSObject.Properties)) {
        $path = "$($prop.Value.path)".Trim()
        if ($path) { $projects[$prop.Name] = @{ path = $path; worktreeRoot = "$($prop.Value.worktreeRoot)".Trim() } }
    }

    $heldFile = Join-Path $RunDir 'reap-held.json'
    $stopFile = Join-Path $DataRoot 'runner.stop'
    $ask = {
        param($Key, $Items)
        $b = @{ project = $Key; worktrees = @($Items) } | ConvertTo-Json -Depth 5 -Compress
        $r = Invoke-RestMethod -Uri "$DashboardUrl/api/hosts/$hostName/worktrees" -Method Post `
            -ContentType $JsonContentType -Body (ConvertTo-JsonBody $b) -TimeoutSec 60
        return @($r.verdicts)
    }
    $r = Remove-HostWorktrees -Projects $projects -Ask $ask -BudgetSeconds $BudgetSeconds `
        -Held { Read-ReapHeld -Path $heldFile } -Stopping { Test-Path -LiteralPath $stopFile }

    foreach ($line in @($r.removed)) { Write-Log "Reaped worktree $line (the branch is kept)." }
    foreach ($line in @($r.kept)) { Write-Log "Kept worktree $line, though the board says it may go." }
    foreach ($line in @($r.failed)) { Write-Log "Could not reap worktree $line" }
    if ($r.more) { exit $ReapMoreExit }
    exit 0
}
catch {
    Write-Log "Reaping this host's worktrees failed: $($_.Exception.Message)"
    exit 1
}
finally {
    try { $single.ReleaseMutex() } catch { }
}
