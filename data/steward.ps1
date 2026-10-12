# ============================================================
# One steward run, for one project, in one process.
#
# A project's steward stands in for its owner between worker runs: it files the next tickets from
# the agreed plan, answers blocked tickets from decisions already made, reviews and merges finished
# work, and asks the owner only what is the owner's. The orchestrator launches this script when the
# dashboard says a project's steward is owed a run (GET /api/steward/due), in a slot of its own
# beside the worker slots - so a steward never takes a berth a ticket could have used.
#
# It is deliberately a sibling of worker.ps1 rather than a mode of it. A worker is built around one
# ticket: a claim, a worktree, a branch, the commit and squash nets over what the session left
# behind. A steward has none of those. It runs in the project's own checkout, changes no file, and
# everything it does goes through the dashboard's steward tools - where the mode, the limits and
# the merge checks are enforced, not here.
#
# What this script owns is the launch: which model, which tools, the prompt, the timeout, and
# making sure the run's lock is given back whatever happened to the agent.
#
# Usage: powershell -ExecutionPolicy Bypass -File steward.ps1 -Project <key> -Agent <slot>
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][int]$Agent
)

$DataRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $DataRoot "heartbeat-common.ps1")

# The same two things a worker does before anything else, for the same reasons - see worker.ps1:
# a run is its own top-level session and never inherits a host session's variables, and it spends
# whichever Claude login the dashboard is switched to.
if ($env:CLAUDECODE) {
    foreach ($name in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(CLAUDE|ANTHROPIC)' } | ForEach-Object Name)) {
        if ($null -eq [Environment]::GetEnvironmentVariable($name, 'User') -and
            $null -eq [Environment]::GetEnvironmentVariable($name, 'Machine')) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
    }
}
$claudeAccountDir = Get-ClaudeAccountDir
if ($claudeAccountDir) { $env:CLAUDE_CONFIG_DIR = $claudeAccountDir }
$env:GCM_INTERACTIVE     = 'never'
$env:GIT_TERMINAL_PROMPT = '0'

$script:AgentSlot = $Agent
$StewardSkill = 'steward'

# Give the run's lock back. A run that ended itself has already released it and this does nothing;
# one that died, timed out or never started is the case it exists for. Without it the project's
# steward is locked out until the lease expires, which is most of two hours.
function Release-StewardRun {
    param([string]$Reason)
    try {
        $r = Invoke-RestMethod -Uri "$DashboardUrl/api/steward/abandon?project=$Project&reason=$([Uri]::EscapeDataString($Reason))" `
            -Method Post -TimeoutSec 10
        if ($r.released -and @($r.released).Count -gt 0) { Write-Log "  The run did not close itself: its lock was released ($Reason)." }
    }
    catch { Write-Log "  Could not release the steward lock for ${Project}: $($_.Exception.Message)" }
}

# --- What to run, from the dashboard ---
# Stamps the attempt as it answers, so a run that dies before it can open itself is still counted
# and the orchestrator does not launch another straight away.
try {
    $launch = Invoke-RestMethod -Uri "$DashboardUrl/api/steward/launching?project=$Project" -Method Post -TimeoutSec 15
}
catch {
    Write-Log "Steward [$Project]: the dashboard would not start a run: $($_.Exception.Message)"
    exit 1
}

$projectName = "$($launch.displayName)"
$workDir     = "$($launch.projectPath)".TrimEnd('\', '/')
$skillFile   = "$($launch.skillFile)"

Write-Log "Steward run for $projectName [$Project], mode $($launch.mode)."

if (-not $launch.skillExists) {
    Write-Log "  ERROR: no steward skill at $skillFile. Copy data/skill-templates/steward/skill.md there and make it this project's own."
    Send-DashboardEvent -Type "error" -Project $Project -Error "Steward: no skill file at $skillFile"
    exit 1
}
if (-not $workDir -or -not (Test-Path $workDir)) {
    Write-Log "  ERROR: the project's checkout '$workDir' does not exist."
    Send-DashboardEvent -Type "error" -Project $Project -Error "Steward: no checkout at $workDir"
    exit 1
}
if (-not (Test-Path $PromptStewardFile)) {
    Write-Log "  ERROR: $PromptStewardFile is missing."
    exit 1
}

# --- Tools ---
# One server, and it is the steward's own door on the dashboard: /mcp/steward lists the steward's
# tools and nothing a worker has, exactly as /mcp lists a worker's and none of these.
$mcpServers = @{ steward = @{ type = "http"; url = "$DashboardUrl/mcp/steward" } }
$toolSetup = New-AgentToolSetup -Provider $AgentProvider -Servers $mcpServers -RunDir $RunDir -Key "$Project-steward" -Project $Project
foreach ($k in $toolSetup.Env.Keys) { Set-Item -Path "Env:$k" -Value $toolSetup.Env[$k] }
$env:WHETSTONE_TICKET_ID = ""
$env:WHETSTONE_PROJECT   = "$Project"
$env:WHETSTONE_SKILL     = $StewardSkill

$model = Get-PhaseModel -Skill $StewardSkill
$conventionsFile = Get-ConventionsFile -Provider $AgentProvider -WorkDir $workDir
$memoryIndex = if ($launch.memoryIndex) { "$($launch.memoryIndex)" } else { "(this project keeps no notes store)" }
$planDir = if ($launch.planUnits) { "$($launch.planUnits)" } else { "(no plan folder is configured for this project)" }

$prompt = (Get-Content -Path $PromptStewardFile -Raw -Encoding UTF8).
    Replace('{{PROJECT_KEY}}', $Project).
    Replace('{{PROJECT_NAME}}', $projectName).
    Replace('{{PLAN_DIR}}', $planDir).
    Replace('{{CONVENTIONS_FILE}}', $conventionsFile).
    Replace('{{MEMORY_INDEX}}', $memoryIndex).
    Replace('{{SKILL_FILE}}', $skillFile)

$agentCmd = Get-AgentWorkerCommand -Provider $AgentProvider -Model $model -Prompt $prompt `
    -WorkDir $workDir -ToolSetup $toolSetup -ReasoningEffort $WorkerReasoningEffort
$agentArgs = @($agentCmd.Args)

# A steward reads and decides; it changes no file and runs no command. Where the CLI lets the
# built-in tools be named, that is made a fact rather than a request: with no shell and no editor
# there is nothing in the session that could write to the owner's checkout. Its MCP tools are
# unaffected - they are how it does everything it is for.
#
# Claude only. Codex and gemini run a steward with their full toolset, as they run a worker, and
# for them "you never change a file" is the prompt's rule alone - the same gap, for the same
# reason, that the local-only guard has on those two.
if ($AgentProvider.Key -eq 'claude') { $agentArgs += @('--tools', 'Read,Grep,Glob') }

Write-Log "  Model: $model. Prompt: $($prompt.Length) chars on stdin."
Send-DashboardEvent -Type "cycle_start" -Project $Project -Phase "Steward"

$startedAt      = Get-Date
$resultReceived = $false
$resultIsError  = $false
$summaryLines   = @()
$mcpProblem     = $null
$streamState    = @{ LastMessage = $null }

# The same watchdog a worker has, for the same reason: a stalled session must never hold the slot
# for ever. It kills only the agent descended from THIS process.
$timeoutSeconds = [int]($WorkerTimeoutMinutes * 60)
$watchdog = Start-Job -ScriptBlock {
    param($sec, $rootPid, $pattern)
    Start-Sleep -Seconds $sec
    try { $procs = Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name -ErrorAction Stop } catch { return }
    $childrenOf = @{}
    foreach ($p in $procs) { $k = [int]$p.ParentProcessId; if (-not $childrenOf.ContainsKey($k)) { $childrenOf[$k] = @() }; $childrenOf[$k] += $p }
    $stack = New-Object System.Collections.Stack; $stack.Push([int]$rootPid)
    while ($stack.Count -gt 0) {
        $cur = $stack.Pop()
        if ($childrenOf.ContainsKey($cur)) {
            foreach ($c in $childrenOf[$cur]) {
                $cpid = [int]$c.ProcessId
                if ($c.Name -like $pattern) { Stop-Process -Id $cpid -Force -ErrorAction SilentlyContinue }
                $stack.Push($cpid)
            }
        }
    }
} -ArgumentList $timeoutSeconds, $PID, $AgentProvider.ProcessPattern

Push-Location $workDir
try {
    $agentCmd.Stdin | & $agentCmd.Exe @agentArgs 2>&1 | ForEach-Object {
        if ($resultReceived -or $mcpProblem) { return }
        $line = $_.ToString().TrimEnd()
        if (-not $line) { return }
        try {
            $event = $line | ConvertFrom-Json -ErrorAction Stop
            $canonical = ConvertFrom-AgentEvent -Provider $AgentProvider -Event $event -State $streamState
            foreach ($e in $canonical) {
                switch ($e.Kind) {
                    "session_start" {
                        # Without its tools a steward can do nothing at all, and would spend a run
                        # finding that out. Checked on the opening event, before the first token.
                        $status = $e.ServerStatus
                        $st = if ($status.ContainsKey('steward')) { $status['steward'] } else { 'absent' }
                        if ($st -ne 'connected') { $mcpProblem = "steward ($st)" }
                        elseif ($e.ServersWithTools -and -not $e.ServersWithTools.ContainsKey('steward')) { $mcpProblem = "steward (connected, no tools)" }
                        if ($mcpProblem) {
                            Write-Log "  MCP: REQUIRED server not usable: $mcpProblem"
                            Stop-DescendantAgent -RootPid $PID -Pattern $AgentProvider.ProcessPattern
                        }
                        else { Write-Log "  MCP: steward=connected" }
                    }
                    "tool_use" {
                        $toolName = "$($e.ToolName)"
                        # The feed tells what the steward DID. Its reads - of the board and of the
                        # repo - stay in the log.
                        $reads = @('mcp__steward__steward_status', 'mcp__steward__steward_decisions',
                                   'mcp__steward__steward_review', 'mcp__steward__steward_diff')
                        if ($toolName -like 'mcp__steward__*' -and $toolName -notin $reads) {
                            Write-Log "  > Tool: $toolName $($e.Detail)"
                            Send-DashboardEvent -Type "tool_call" -ToolName $toolName -Text "$($e.Detail)" -Project $Project -Phase "Steward"
                        }
                        else { Write-Log "  . $toolName $($e.Detail)" }
                    }
                    "text" {
                        $e.Text -split "`n" | ForEach-Object {
                            $t = $_.TrimEnd()
                            if ($t) {
                                Write-Log "  | $t"
                                Send-DashboardEvent -Type "text_output" -Text $t -Project $Project -Phase "Steward"
                            }
                        }
                    }
                    "thinking" {
                        $th = ($e.Text -replace "`r", "").Trim()
                        if ($th) { Send-DashboardEvent -Type "thinking" -Text $th -Project $Project -Phase "Steward" }
                    }
                    "result" {
                        if ($null -ne $e.IsError) { $resultIsError = [bool]$e.IsError }
                        if ($e.Text) {
                            $e.Text -split "`n" | ForEach-Object { $t = $_.TrimEnd(); if ($t) { Write-Log "  | $t"; $summaryLines += $t } }
                            Send-DashboardEvent -Type "summary" -Text ($summaryLines -join "`n") -Project $Project -Phase "Steward"
                        }
                        $resultReceived = $true
                        Stop-DescendantAgent -RootPid $PID -Pattern $AgentProvider.ProcessPattern
                    }
                }
            }
        }
        catch { Write-Log "  | $line" }
    }
}
finally { Pop-Location }

Stop-Job $watchdog -ErrorAction SilentlyContinue
Remove-Job $watchdog -Force -ErrorAction SilentlyContinue

$elapsedMin = [math]::Round(((Get-Date) - $startedAt).TotalMinutes, 1)

# Refused by the account's usage limit rather than failed. A steward run is one more session on the
# same account as the fleet, so it parks the fleet the same way a worker's refusal does.
$limitReset = $null
if ($resultIsError -or $summaryLines.Count -le 3) { $limitReset = Get-UsageLimitReset -Text ($summaryLines -join "`n") }
if ($limitReset) {
    $limitText = ($summaryLines | Where-Object { $_ -match "(?i)hit your" } | Select-Object -First 1)
    Write-Log "  USAGE LIMIT: '$limitText' - nothing is started until $($limitReset.ToString('yyyy-MM-dd HH:mm'))."
    try {
        @{ resetsAt = $limitReset.ToString("o"); text = "$limitText"; ticketId = 0; at = (Get-Date).ToString("o") } |
            ConvertTo-Json -Compress | Set-Content -Path $UsageLimitFile -Encoding UTF8
    }
    catch { Write-Log "  Could not record the usage limit: $($_.Exception.Message)" }
    Send-DashboardEvent -Type "error" -Project $Project -Error "Usage limit - paused until $($limitReset.ToString('HH:mm'))"
}
elseif ($mcpProblem) {
    Send-DashboardEvent -Type "error" -Project $Project -Error "Steward: its tools were not usable ($mcpProblem)"
}
elseif (-not $resultReceived) {
    Write-Log "  The steward did not finish (elapsed ${elapsedMin}min, timeout ${WorkerTimeoutMinutes}min)."
    Send-DashboardEvent -Type "error" -Project $Project -Error "Steward did not finish (elapsed ${elapsedMin}min)"
}

$why = if ($limitReset) { "the account's usage limit refused the run" }
       elseif ($mcpProblem) { "its tools were not usable" }
       elseif (-not $resultReceived) { "the run did not finish" }
       else { "the run ended without closing itself" }
Release-StewardRun -Reason $why

if ($toolSetup.Cleanup) { Remove-Item -Path $toolSetup.Cleanup -Recurse -Force -ErrorAction SilentlyContinue }

Write-Log "Steward run for [$Project] over after ${elapsedMin}min."
Send-DashboardEvent -Type "cycle_end" -Project $Project -Phase "Steward" -DurationSeconds ([int]((Get-Date) - $startedAt).TotalSeconds)
exit 0
