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
# Usage: pwsh -ExecutionPolicy Bypass -File data/runner.ps1 [-HostFile <path>]
# Stop:  create data/runner.stop. It takes nothing new and exits once its runs have finished.
#
# It keeps itself on the board host's commit. The board gives work only to a runner on its exact
# commit, and says which one in every check-in. When they differ and nothing is running here, the
# runner fast-forwards this checkout to that commit and exits with $RunnerUpdatedExit; started the
# usual way, this file is a small supervisor around the real runner (-Inner), and it starts the
# runner again on the new code. See Get-UpdateAction. host.json's "autoUpdate": false turns it off.
#
# Config: data/host.json (machine-local, gitignored) names this host and maps each project to its
# clone here; data/config.json sets dashboardUrl (the board host's API) and claudeAccount.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    [string]$HostFile = '',
    # One check-in and nothing more. For trying a new host.json out by hand.
    [switch]$Once,
    # The runner itself, as the supervisor starts it. Never passed by hand.
    [switch]$Inner,
    # The supervisor that started this runner, so the runner can tell whether anything will start it
    # again after an update. Passed by the supervisor alone.
    [int]$SupervisorPid = 0
)

$DataRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $DataRoot "heartbeat-common.ps1")
. (Join-Path $DataRoot "machine-info.ps1")

# How a process this starts is kept out of sight: hidden on Windows, where each would otherwise open
# a console window, and nothing at all elsewhere, where there are no windows and Start-Process refuses
# -WindowStyle outright. Splatted as @HiddenWindow.
$HiddenWindow = if ($OnWindows) { @{ WindowStyle = 'Hidden' } } else { @{} }

$ErrorActionPreference = 'Continue'
$env:GCM_INTERACTIVE     = 'never'
$env:GIT_TERMINAL_PROMPT = '0'

$RunnerStopFile = Join-Path $DataRoot "runner.stop"
$RunnerKeepExitedMinutes = 10

# The exit code that means "this checkout moved to the board host's commit - start me again".
$RunnerUpdatedExit = 75

# How long to wait before fetching again for a board commit this checkout cannot see yet.
$RunnerFetchBackoffMinutes = 5

# QA's browser server, installed in data/playwright-mcp so a run starts it in seconds. The folder's
# node_modules is gitignored, so a worker host's checkout never had it: every QA run there went
# through `npx @playwright/mcp@latest`, which asks the npm registry each launch, and on a busy host
# it missed the 90s connect timeout twice running and blocked the ticket on a healthy browser. The
# runner installs it itself, in the background and only while idle (an install deletes the files a
# live run's browser has open). While it runs, the marker tells workers to keep using npx, so none
# starts on a half-written install. worker.ps1 reads the same marker.
$PlaywrightDir = Join-Path $DataRoot 'playwright-mcp'
$PlaywrightInstallingMarker = '.installing'
$PlaywrightRetryMinutes = 30

# ---- Pure helpers (data/runner.tests.ps1 lifts these) ---------------------------------------

# Whether data/playwright-mcp holds the version its package.json pins, finished. Returns
# @{ ok; pinned; installed }.
function Test-PlaywrightInstall {
    param([string]$Dir)
    $pinned = ''; $installed = ''
    try { $pinned = "$((Get-Content -Raw -LiteralPath (Join-Path $Dir 'package.json') | ConvertFrom-Json).dependencies.'@playwright/mcp')".Trim() } catch { }
    $mcpPkg = Join-Path $Dir 'node_modules\@playwright\mcp\package.json'
    if (Test-Path -LiteralPath $mcpPkg) {
        try { $installed = "$((Get-Content -Raw -LiteralPath $mcpPkg | ConvertFrom-Json).version)".Trim() } catch { }
    }
    $busy = Test-Path -LiteralPath (Join-Path $Dir $PlaywrightInstallingMarker)
    return @{ ok = [bool]($pinned -and $installed -eq $pinned -and -not $busy); pinned = $pinned; installed = $installed }
}

# Start `npm ci` in data/playwright-mcp in the background, the marker written first. Returns the
# process, or $null when it could not start.
function Start-PlaywrightInstall {
    param([string]$Dir, [string]$LogDir)
    Set-Content -LiteralPath (Join-Path $Dir $PlaywrightInstallingMarker) -Value "npm ci started $(Get-Date -Format s)" -Encoding ascii
    $out = Join-Path $LogDir 'playwright-install.log'
    $err = Join-Path $LogDir 'playwright-install.err.log'
    try {
        $npmArgs = @('ci', '--no-audit', '--no-fund')
        $p = if ($OnWindows) {
            Start-Process -FilePath 'cmd.exe' -ArgumentList (@('/c', 'npm') + $npmArgs) -WorkingDirectory $Dir `
                -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
        } else {
            Start-Process -FilePath 'npm' -ArgumentList $npmArgs -WorkingDirectory $Dir `
                -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
        }
        try { $null = $p.Handle } catch { }
        return $p
    }
    catch {
        Remove-Item -LiteralPath (Join-Path $Dir $PlaywrightInstallingMarker) -Force -ErrorAction SilentlyContinue
        Write-Log "Could not start the QA browser install: $($_.Exception.Message)"
        return $null
    }
}

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
    # On unless it says false in so many words: a host that stays off the board's commit gets no work.
    $autoUpdate = -not ($raw.PSObject.Properties['autoUpdate'] -and $raw.autoUpdate -eq $false)
    # On too: a board whose history was rewritten is followed, keeping the old commit as a branch.
    $followRewrites = -not ($raw.PSObject.Properties['followRewrites'] -and $raw.followRewrites -eq $false)
    return @{ ok = $true; name = $name; projects = $projects; autoUpdate = $autoUpdate; followRewrites = $followRewrites }
}

# The claim as this machine must read it. Every path in it is the board host's, so each one is
# pointed at this host's own: its clone, its worktree root, the project kit unpacked under this
# data root. And it is stamped with the host's name, which is the one thing that puts worker.ps1 on
# its worker host paths (see $onHost there).
# The board host's data root from a project's data folder there (<root>/projects/<key>): two folders
# up, by either separator, spelled as the board host spelled it. '' when there is no such folder.
function Get-BoardDataRoot {
    param([string]$ProjectDataDir)
    $d = "$ProjectDataDir".Trim().TrimEnd('\', '/')
    foreach ($up in 1..2) {
        $cut = $d.LastIndexOfAny([char[]]@('\', '/'))
        if ($cut -lt 0) { return '' }
        $d = $d.Substring(0, $cut)
    }
    return $d
}

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
    # a path the ticket tools give under it is at the same place under this machine's. It is the
    # board host's path, so it is cut by either separator and kept as that machine spells it: this
    # machine's own path rules would read a Windows board's path as one name on a Mac.
    $boardData = Get-BoardDataRoot "$($p.dataDir)"
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
    $procs = @(Get-ProcessTable -Shells |
        Where-Object {
            # This checkout's worker.ps1 only: the machine may also run a board of its own, whose
            # workers are its business and not this runner's. See Test-RunsScript.
            $_.ProcessId -ne $PID -and (Test-RunsScript -CommandLine $_.CommandLine -Script $WorkerScript)
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
    Stop-ProcessTree -Id $target
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
    if (-not $why) { $why = Set-ClaimClaudeAccount -Claim $claim -Order $Order -Logins $script:ClaudeLogins }
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
    try { $proc = Start-Process -FilePath $PsHost -ArgumentList $psArgs -PassThru @HiddenWindow }
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

# The usage-limit pauses a worker here wrote that are still in force, one per provider, as the
# check-in reports them. A Claude pause names the account that hit it: a limit is the account's,
# not the machine's, and while the board chooses which account this host spends it marks that
# account full - for its own fleet too - and moves this host to another, rather than holding the
# whole machine. Any other pause, or one naming no account the board knows, holds the host.
function Get-HostUsageLimits {
    param($Logins)
    $limits = @()
    foreach ($p in $AgentProvidersInUse) {
        $until = Get-UsageLimitPause -Provider $p.Key
        if (-not $until) { continue }
        $accountId = $null
        if ($p.Key -eq 'claude') {
            $dir = $null
            try { $dir = "$((Get-Content -Path (Get-UsageLimitFile 'claude') -Raw | ConvertFrom-Json).configDir)" } catch { }
            $accountId = Find-LoginAccountId -Logins $Logins -ConfigDir $dir
        }
        $limits += @{ provider = $p.Key; until = $until.ToUniversalTime().ToString('o'); accountId = $accountId }
    }
    return ,@($limits)
}

# The account a login folder is signed in as, among this machine's logins - or $null. An empty
# folder is the CLI's own login, which is whatever CLAUDE_CONFIG_DIR this process has, if any.
function Find-LoginAccountId {
    param($Logins, [string]$ConfigDir)
    $want = Get-NormalDir $ConfigDir
    foreach ($l in @($Logins)) {
        if (-not $l) { continue }
        $dir = if ("$($l.configDir)") { "$($l.configDir)" } else { "$env:CLAUDE_CONFIG_DIR" }
        if ((Get-NormalDir $dir) -eq $want) { return "$($l.uuid)" }
    }
    return $null
}

function Get-NormalDir {
    param([string]$Dir)
    if (-not "$Dir".Trim()) { return '' }
    try { return [IO.Path]::GetFullPath("$Dir".Trim()).TrimEnd('\', '/').ToLowerInvariant() }
    catch { return "$Dir".Trim().TrimEnd('\', '/').ToLowerInvariant() }
}

# Put the account the board chose for this run into its claim, as one of this machine's login
# folders - worker.ps1 reads it as "claudeAccount". The board names an account by its id, because
# its names for its accounts are its own; this machine's folder for it is found here.
#
#   order.claude absent, or not gated   the board does not choose: nothing is added, and the worker
#                                       spends this machine's own claudeAccount, as it always did
#   gated, with an account              that login's folder ('' for the CLI's own)
#   gated, with none                    no folder, and the board's reason - the worker turns the
#                                       run down if its phase runs on Claude
#
# Returns why the run cannot start, or $null. The one refusal: an account this machine has no login
# for any more, signed out since its last check-in.
function Set-ClaimClaudeAccount {
    param($Claim, $Order, $Logins)
    $c = $Order.claude
    if (-not $c -or -not $c.gated) { return $null }

    $entry = [ordered]@{ gated = $true; account = "$($c.account)"; configDir = ''; refused = $null }
    if ("$($c.accountId)") {
        $login = @(@($Logins) | Where-Object { $_ -and "$($_.uuid)" -ieq "$($c.accountId)" })[0]
        if (-not $login) { return "the board chose Claude account $($c.account), which this computer has no login for" }
        $entry.configDir = if ("$($login.configDir)") { "$($login.configDir)" } else { "$env:CLAUDE_CONFIG_DIR" }
    }
    else {
        $entry.refused = if ("$($c.reason)") { "$($c.reason)" } else { 'no Claude account this computer has is under its limits' }
    }
    $Claim | Add-Member -NotePropertyName claudeAccount -NotePropertyValue ([pscustomobject]$entry) -Force
    return $null
}

# Whether to bring this checkout onto the board host's commit now.
#   none    the board named no commit, or the same one; or auto-update is off
#   wait    they differ, but runs are out here: a worker must never see its scripts change under
#           it. The board sends nothing new to a host on another commit, so they drain on their own.
#   update  they differ, nothing is running, and the runner is not stopping
function Get-UpdateAction {
    param([string]$Own, [string]$Board, [int]$LiveRuns, [bool]$Stopping, [bool]$Enabled)
    if (-not $Enabled) { return 'none' }
    if (-not $Board -or -not $Own) { return 'none' }
    if ($Own.Trim() -ieq $Board.Trim()) { return 'none' }
    if ($LiveRuns -gt 0) { return 'wait' }
    if ($Stopping) { return 'none' }
    return 'update'
}

# Fast-forward $Repo to exactly $Sha - never past it: the board may be behind its own origin, and
# the tip would only be another mismatch. Fetches first when the commit is not here yet.
# Returns @{ ok; state; reason }, state one of:
#   moved      HEAD is now $Sha
#   missing    $Sha is not reachable even after a fetch (the board's commit is not where this
#              checkout fetches from yet) - worth trying again later
#   dirty      tracked files here are modified - a person's work, never overwritten
#   rewound    $Sha is not ahead of HEAD - the board's history was rewritten (a rebase, a reset) -
#              so this checkout was moved to it anyway, its old commit kept as a branch first
#              ($result.backup names it). This checkout only ever follows the board, so a commit
#              of its own is not expected; if one was made, nothing is lost.
#   not-ff     the same, with -FollowRewrites:$false (host.json "followRewrites": false): refused
#   failed     git refused for another reason
function Update-OwnCheckout {
    param([string]$Repo, [string]$Sha, [bool]$FollowRewrites = $true)
    $ErrorActionPreference = 'Continue'
    git -C $Repo cat-file -e "$Sha^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        git -C $Repo fetch --quiet origin 2>$null
        git -C $Repo cat-file -e "$Sha^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) { return @{ ok = $false; state = 'missing'; reason = "commit $Sha is not in this checkout, even after fetching from origin" } }
    }
    $dirty = @(git -C $Repo status --porcelain --untracked-files=no 2>$null | Where-Object { $_ })
    if ($dirty.Count -gt 0) { return @{ ok = $false; state = 'dirty'; reason = "$($dirty.Count) tracked file(s) here are modified" } }
    git -C $Repo merge-base --is-ancestor HEAD $Sha 2>$null
    if ($LASTEXITCODE -ne 0) {
        if (-not $FollowRewrites) { return @{ ok = $false; state = 'not-ff'; reason = "the board's commit $Sha is not ahead of this checkout's - it is not a fast-forward" } }
        $old = "$(git -C $Repo rev-parse HEAD 2>$null)".Trim()
        $backup = "runner-backup/$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($old.Substring(0, [Math]::Min(7, $old.Length)))"
        git -C $Repo branch --quiet $backup $old *> $null
        if ($LASTEXITCODE -ne 0) { return @{ ok = $false; state = 'failed'; reason = "could not keep this checkout's commit $old as $backup before moving" } }
        git -C $Repo reset --quiet --hard $Sha *> $null
        $head = "$(git -C $Repo rev-parse HEAD 2>$null)".Trim()
        if ($LASTEXITCODE -ne 0 -or $head -ine $Sha) { return @{ ok = $false; state = 'failed'; reason = "git could not move to $Sha (the old commit is kept as $backup)" } }
        Remove-OldBackups -Repo $Repo
        return @{ ok = $true; state = 'rewound'; backup = $backup; from = $old }
    }
    git -C $Repo merge --quiet --ff-only $Sha *> $null
    $head = "$(git -C $Repo rev-parse HEAD 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ine $Sha) { return @{ ok = $false; state = 'failed'; reason = "git could not fast-forward to $Sha" } }
    return @{ ok = $true; state = 'moved' }
}

# Keep the newest $Keep runner-backup branches; a rewrite now and then should not leave a pile.
function Remove-OldBackups {
    param([string]$Repo, [int]$Keep = 10)
    $all = @(git -C $Repo for-each-ref --sort=-refname --format='%(refname:short)' 'refs/heads/runner-backup/' 2>$null | Where-Object { $_ })
    foreach ($b in @($all | Select-Object -Skip $Keep)) { git -C $Repo branch --quiet -D $b *> $null }
}

# Whether the supervisor that started this runner is still there to start it again. A pid can be
# reused by an unrelated process, so it must also be a powershell running this same script.
function Test-SupervisorAlive {
    param([int]$SupervisorPid)
    if ($SupervisorPid -le 0) { return $false }
    $p = Get-ProcessTable -Id $SupervisorPid
    if (-not $p) { return $false }
    return (Test-RunsScript -CommandLine $p.CommandLine -Script (Join-Path $DataRoot 'runner.ps1'))
}

# Windows PowerShell 5.1 is not a host the fleet runs on, but a runner set up before that rule can
# still be started on it: by a scheduled task whose action names powershell.exe, and then by the
# supervisor that task started, which starts every runner after it on the host it read at its own
# start. Whether this runner is in such a chain - itself on 5.1, or under a supervisor that is.
function Test-OnWindowsPowerShell {
    param([string]$Edition = "$($PSVersionTable.PSEdition)", [string]$SupervisorName = '')
    return ($Edition -eq 'Desktop') -or ("$SupervisorName" -match '^(?i)powershell(\.exe)?$')
}

# The action a scheduled task should have instead of one that starts Windows PowerShell: the same
# arguments, on 7. $null for an action that already starts something else.
function Get-PwshTaskAction {
    param([string]$Execute, [string]$Arguments, [string]$PsHost)
    $leaf = Split-Path -Leaf ("$Execute".Trim().Trim('"'))
    if ($leaf -notmatch '^(?i)powershell(\.exe)?$') { return $null }
    return @{ Execute = $PsHost; Arguments = "$Arguments" }
}

# The scheduled task whose action runs this checkout's runner.ps1, or $null.
function Find-RunnerTask {
    param([string]$Script = (Join-Path $DataRoot 'runner.ps1'))
    # Task Scheduler is Windows': elsewhere no task starts the runner, and the cmdlet does not exist.
    if (-not $OnWindows) { return $null }
    $want = [IO.Path]::GetFullPath($Script)
    foreach ($t in @(Get-ScheduledTask -ErrorAction SilentlyContinue)) {
        foreach ($a in @($t.Actions)) {
            if ("$($a.Execute) $($a.Arguments)" -like "*$want*") { return $t }
        }
    }
    return $null
}

# Point a scheduled task that starts the runner on Windows PowerShell at 7 instead, keeping its
# arguments, triggers and settings. 'moved', or 'same' when it already starts 7. Throws when the
# task cannot be changed.
function Move-RunnerTaskToPwsh {
    param($Task)
    $changed = $false
    $actions = @(foreach ($a in @($Task.Actions)) {
        $n = Get-PwshTaskAction -Execute $a.Execute -Arguments $a.Arguments -PsHost $PsHost
        if (-not $n) { $a; continue }
        $changed = $true
        $new = @{ Execute = $n.Execute }
        if ($n.Arguments) { $new.Argument = $n.Arguments }
        if ($a.WorkingDirectory) { $new.WorkingDirectory = $a.WorkingDirectory }
        New-ScheduledTaskAction @new
    })
    if (-not $changed) { return 'same' }
    Set-ScheduledTask -TaskName $Task.TaskName -TaskPath $Task.TaskPath -Action $actions -ErrorAction Stop | Out-Null
    return 'moved'
}

# Run the runner as a child for as long as it keeps exiting with $RunnerUpdatedExit, starting it
# again each time; return the first other exit code. A function so the tests can drive it with a
# stand-in child script rather than the runner itself.
function Invoke-RunnerSupervisor {
    param([string[]]$ChildArgs, [int]$PauseSeconds = 2)
    while ($true) {
        $child = Start-Process -FilePath $PsHost -ArgumentList $ChildArgs -PassThru @HiddenWindow
        try { $null = $child.Handle } catch { }
        $child.WaitForExit()
        $code = $child.ExitCode
        if ($code -ne $RunnerUpdatedExit) { return $code }
        Write-Log "Runner updated to the board host's commit - starting it again on the new code."
        Start-Sleep -Seconds $PauseSeconds
    }
}

# Everything below runs only when this file is executed, not when the tests lift its functions.
if ($MyInvocation.InvocationName -eq '.') { return }

# ---- The supervisor ----
#
# Started the usual way (Task Scheduler, by hand), this file runs the runner as a child and starts it
# again whenever it exits with $RunnerUpdatedExit - which it does only after moving this checkout to
# the board host's commit, so the new runner is on the new code. Any other exit ends the supervisor
# with the same code. It stays this small on purpose: it is the one part of the runner that is NOT
# reloaded by an update, so a change to it takes effect at the next logon or manual start.
if (-not $Inner -and -not $Once) {
    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path, '-Inner', '-SupervisorPid', "$PID")
    if ($HostFile) { $childArgs += @('-HostFile', $HostFile) }
    exit (Invoke-RunnerSupervisor -ChildArgs $childArgs)
}

if (-not $HostFile) { $HostFile = Join-Path $DataRoot 'host.json' }
$HostCfg = Read-HostConfig -Path $HostFile
if (-not $HostCfg.ok) { Write-Log "Runner cannot start: $($HostCfg.reason)"; exit 1 }

$single = New-Object System.Threading.Mutex($false, "Local\CDH-runner")
if (-not $single.WaitOne(0)) { Write-Log "Another runner is already running on this machine."; exit 1 }

$script:AgentSlot = 0
$commit = Get-OwnCommit
# On 5.1, or under a supervisor that is: moved to 7 as soon as nothing is running here (below).
$supervisorName = $(if ($SupervisorPid -gt 0) { "$((Get-ProcessTable -Id $SupervisorPid | Select-Object -First 1).Name)" } else { '' })
$onWindowsPs = $OnWindows -and (Test-OnWindowsPowerShell -SupervisorName $supervisorName)
$psMoveTried = $false
Write-Log "Runner '$($HostCfg.name)' on commit $commit, board host $DashboardUrl, projects: $(@($HostCfg.projects.Keys) -join ', '), auto-update $(if ($HostCfg.autoUpdate) { 'on' } else { 'off' }), supervisor $(if ($SupervisorPid -gt 0) { "pid $SupervisorPid" } else { 'none' })"
$ownRepo = Split-Path -Parent $DataRoot
# What has already been said, so a waiting or refused update is logged once per board commit and
# not on every check-in; and when a commit not fetchable yet may be tried for again.
$updateSaid = @{}
$updateRetryAt = @{}
$pwInstall = $null
$pwRetryAt = [datetime]::MinValue
$pwState = Test-PlaywrightInstall -Dir $PlaywrightDir
if (-not $pwState.ok) { Write-Log "QA browser in data/playwright-mcp is $(if ($pwState.installed) { "at $($pwState.installed)" } else { 'not installed' }) (wants $($pwState.pinned)) - installing it once this host is idle; QA uses npx until then." }

$Runs = Find-SurvivingRuns
foreach ($slot in $Runs.Keys) { Write-Log "  Adopted run $($Runs[$slot].RunId) in slot $slot (ticket #$($Runs[$slot].TicketId), pid $($Runs[$slot].Pid))" }

$failures = 0
# This machine's hardware goes with the first check-in and hourly after (the board keeps it only
# when it changed), its load at most once a minute. docs/machine-limits-plan.md.
$MachineInventoryMinutes = 60
$machineSentAt = $null
$machineTriedAt = $null
$machineLoadAt = $null
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

    # Read afresh each time: a login signed in or out here reaches the board on the next check-in.
    $script:ClaudeLogins = @(Get-ClaudeLogins)
    $inventory = $null; $load = $null
    # Tried again after a failed check-in, but not on every one of a run of them: reading the tools'
    # versions takes seconds.
    $inventoryDue = (-not $machineSentAt -or ((Get-Date) - $machineSentAt).TotalMinutes -ge $MachineInventoryMinutes) -and
        (-not $machineTriedAt -or ((Get-Date) - $machineTriedAt).TotalMinutes -ge 5)
    if ($inventoryDue) {
        $machineTriedAt = Get-Date
        try { $inventory = Get-MachineInventory -DataRoot $DataRoot } catch { Write-Log "Could not read this machine's hardware: $($_.Exception.Message)" }
    }
    if (-not $machineLoadAt -or ((Get-Date) - $machineLoadAt).TotalSeconds -ge 55) {
        try { $load = Get-MachineLoad -DataRoot $DataRoot; $machineLoadAt = Get-Date } catch { }
    }
    $body = @{
        commit = $commit
        runs = (Get-RunReport -Runs $Runs)
        claudeLogins = @($script:ClaudeLogins | ForEach-Object { @{ uuid = $_.uuid; email = $_.email; name = $_.name } })
        usageLimits = (Get-HostUsageLimits -Logins $script:ClaudeLogins)
        waitSeconds = $(if ($Once) { 0 } else { 20 })
        dataRoot = $DataRoot
        inventory = $inventory
        load = $load
        psVersion = "$($PSVersionTable.PSVersion)"
        windowsPowerShell = $onWindowsPs
    } | ConvertTo-Json -Depth 6 -Compress

    $resp = $null
    try {
        $resp = Invoke-RestMethod -Uri "$DashboardUrl/api/hosts/$($HostCfg.name)/checkin" -Method Post `
            -ContentType $JsonContentType -Body (ConvertTo-JsonBody $body) -TimeoutSec 60
        if ($failures -gt 0) { Write-Log "Board host reachable again after $failures failed check-in(s)." }
        $failures = 0
        if ($inventory) { $machineSentAt = Get-Date }
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

    # Stay on the board host's commit. After the orders, so a kill or a start this check-in carried is
    # acted on first; and only ever while idle - see Get-UpdateAction.
    $boardSha = "$($resp.boardCommit)".Trim()
    $liveNow = @($Runs.Values | Where-Object { -not $_.ExitedAt }).Count
    # A QA browser install holds the update back like a run: restarting under it would leave an
    # npm ci going with nothing to clear its marker, and the next runner would start a second.
    if ($pwInstall -and -not $pwInstall.HasExited) { $liveNow++ }
    $action = Get-UpdateAction -Own $commit -Board $boardSha -LiveRuns $liveNow -Stopping (Test-Path $RunnerStopFile) -Enabled $HostCfg.autoUpdate
    if ($action -eq 'wait' -and $updateSaid[$boardSha] -ne 'wait') {
        Write-Log "Board host is on commit $boardSha; this runner is on $commit. Updating once its $liveNow run(s) finish."
        $updateSaid[$boardSha] = 'wait'
    }
    elseif ($action -eq 'update' -and -not ($updateRetryAt[$boardSha] -and (Get-Date) -lt $updateRetryAt[$boardSha])) {
        $result = Update-OwnCheckout -Repo $ownRepo -Sha $boardSha -FollowRewrites $HostCfg.followRewrites
        if ($result.ok) {
            if ($result.state -eq 'rewound') {
                Write-Log "The board host's history was rewritten: moved this checkout from $commit to its commit $boardSha. The old commit is kept as branch $($result.backup)."
            }
            else { Write-Log "Updated this checkout from $commit to the board host's commit $boardSha." }
            $single.ReleaseMutex()
            if (Test-SupervisorAlive -SupervisorPid $SupervisorPid) { exit $RunnerUpdatedExit }
            # Nothing would start this runner again: its supervisor has gone (a console closed, a
            # Ctrl+C) or it was started without one. Exiting would take this host offline, so it
            # starts a new supervisor - on the new code - and steps aside. The lock is already free.
            Write-Log "No supervisor is waiting to restart this runner - starting a new one on the updated code."
            $restart = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
            if ($HostFile) { $restart += @('-HostFile', $HostFile) }
            Start-Process -FilePath $PsHost -ArgumentList $restart @HiddenWindow | Out-Null
            exit 0
        }
        if ($result.state -eq 'missing') {
            # Not fetchable yet: try again on a back-off, never on every check-in.
            $updateRetryAt[$boardSha] = (Get-Date).AddMinutes($RunnerFetchBackoffMinutes)
            if ($updateSaid[$boardSha] -ne 'missing') {
                Write-Log "Cannot update to the board host's commit yet: $($result.reason). Trying again every $RunnerFetchBackoffMinutes min."
                $updateSaid[$boardSha] = 'missing'
            }
        }
        else {
            # Dirty or not a fast-forward: a person's call. Not tried again for this board commit.
            $updateRetryAt[$boardSha] = [datetime]::MaxValue
            Write-Log "Not updating to the board host's commit ${boardSha}: $($result.reason). This host gets no work until its checkout is on that commit."
        }
    }

    # Off Windows PowerShell 5.1, once, while idle: the scheduled task is pointed at 7 for every
    # logon after this one, and this runner starts a new supervisor on 7 and steps aside - which
    # ends the 5.1 supervisor too, since this is not an update exit. Tried once per runner: a task
    # that cannot be changed is said in the log, and the board shows the host as still on 5.1.
    if ($onWindowsPs -and -not $psMoveTried -and $liveNow -eq 0 -and -not (Test-Path $RunnerStopFile)) {
        $psMoveTried = $true
        if (-not (Get-Command $PsHost -ErrorAction SilentlyContinue)) {
            Write-Log "This runner is on Windows PowerShell 5.1 and PowerShell 7 is not installed here: winget install --id Microsoft.PowerShell, then double-click Update-Runner.bat."
        }
        else {
            $task = Find-RunnerTask
            if ($task) {
                try {
                    if ((Move-RunnerTaskToPwsh -Task $task) -eq 'moved') { Write-Log "Scheduled task '$($task.TaskName)' now starts the runner on PowerShell 7 ($PsHost)." }
                }
                catch { Write-Log "Could not point the scheduled task '$($task.TaskName)' at PowerShell 7: $($_.Exception.Message). Change its action to $PsHost by hand (docs/worker-host-setup.md, step 12)." }
            }
            Write-Log "This runner was started on Windows PowerShell 5.1 - starting it again on PowerShell 7 ($PsHost)."
            $restart = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
            if ($HostFile) { $restart += @('-HostFile', $HostFile) }
            $single.ReleaseMutex()
            try {
                Start-Process -FilePath $PsHost -ArgumentList $restart @HiddenWindow | Out-Null
                exit 0
            }
            catch {
                Write-Log "Could not start PowerShell 7 ($PsHost): $($_.Exception.Message). Staying on 5.1."
                $null = $single.WaitOne(0)
            }
        }
    }

    # The QA browser install, after the update: an update exits this runner, and an install it had
    # just started would outlive it with its marker never cleared.
    if ($pwInstall) {
        if ($pwInstall.HasExited) {
            $code = try { $pwInstall.ExitCode } catch { $null }
            Remove-Item -LiteralPath (Join-Path $PlaywrightDir $PlaywrightInstallingMarker) -Force -ErrorAction SilentlyContinue
            $pwState = Test-PlaywrightInstall -Dir $PlaywrightDir
            if ($code -eq 0 -and $pwState.ok) { Write-Log "QA browser installed in data/playwright-mcp ($($pwState.installed)); QA starts it directly from now on." }
            else {
                # A half-written install is worse than none: npx is slow, a broken copy fails outright.
                Remove-Item -LiteralPath (Join-Path $PlaywrightDir 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue
                $pwRetryAt = (Get-Date).AddMinutes($PlaywrightRetryMinutes)
                Write-Log "QA browser install failed (exit $code; see logs\playwright-install.err.log) - QA keeps using npx; trying again in $PlaywrightRetryMinutes min."
            }
            $pwInstall = $null
        }
    }
    elseif ((Get-Date) -ge $pwRetryAt -and -not (Test-Path $RunnerStopFile) -and
            @($Runs.Values | Where-Object { -not $_.ExitedAt }).Count -eq 0 -and -not (Test-PlaywrightInstall -Dir $PlaywrightDir).ok) {
        Write-Log "Installing the QA browser in data/playwright-mcp (npm ci) while this host is idle."
        $pwInstall = Start-PlaywrightInstall -Dir $PlaywrightDir -LogDir $LogDir
        if (-not $pwInstall) { $pwRetryAt = (Get-Date).AddMinutes($PlaywrightRetryMinutes) }
    }
}

$single.ReleaseMutex()
exit 0
