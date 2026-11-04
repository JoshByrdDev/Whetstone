# Exercises the parts of data/runner.ps1 that decide what a worker on a worker host sees: the host
# file, the claim rewritten to this machine's paths, the project kit unpacked over an old one, and
# what a check-in reports about the runs it holds. The loop itself talks to the board host's API and
# is proven by a live run (docs/worker-hosts-plan.md, step 9).
#
# runner.ps1 cannot be dot-sourced (it starts a loop), so its functions are lifted out of its
# syntax tree, as worker-base.tests.ps1 does for worker.ps1.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/runner.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
$runner = Join-Path $PSScriptRoot 'runner.ps1'

function Write-Log { param([string]$Message) }

$tree = [System.Management.Automation.Language.Parser]::ParseFile($runner, [ref]$null, [ref]$null)
foreach ($fn in $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-runner-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

function Write-Json { param([string]$Path, [string]$Text) Set-Content -Path $Path -Value $Text -Encoding ascii }

# A claim shaped as claim-next returns it, with the board host's paths.
function New-Claim {
    param([string]$MemoryDir = 'C:\board\alpha\.claude\memory')
    return (@"
{ "ticket": { "id": 12, "phase": "NeedsImplementation", "branch": "t-12" }, "skill": "implement", "runId": 812,
  "project": { "key": "alpha", "projectPath": "C:\\board\\alpha", "worktreeRoot": "C:\\board\\.worktrees\\alpha",
               "dataDir": "C:\\board\\Whetstone\\data\\projects\\alpha", "skillsDir": "C:\\board\\Whetstone\\data\\projects\\alpha\\skills",
               "skillFile": "C:\\board\\Whetstone\\data\\projects\\alpha\\skills\\implement\\skill.md",
               "memoryDir": "$($MemoryDir -replace '\\', '\\')", "memoryIndex": "x", "baseBranch": "main", "mergeInsteadOfPr": true,
               "worktreeCopyFiles": [".env.local"] } }
"@ | ConvertFrom-Json)
}

try {
    # ---- Read-HostConfig -------------------------------------------------------
    Write-Host "== Read-HostConfig"
    $f = Join-Path $root 'host.json'
    Write-Json $f '{ "name": "bravo-host", "projects": { "alpha": { "path": "D:\\src\\alpha" }, "beta": { "path": "D:\\src\\beta", "worktreeRoot": "E:\\wt\\beta" }, "nopath": {} } }'
    $h = Read-HostConfig -Path $f
    Check 'reads: ok' $h.ok $true
    Check 'reads: name' $h.name 'bravo-host'
    Check 'reads: a project without a path is dropped' (@($h.projects.Keys | Sort-Object) -join ',') 'alpha,beta'
    Check 'reads: worktree root kept when given' $h.projects['beta'].worktreeRoot 'E:\wt\beta'

    Check 'missing file: refused' (Read-HostConfig -Path (Join-Path $root 'none.json')).ok $false
    Write-Json $f '{ "name": "bad name/", "projects": { "alpha": { "path": "D:\\a" } } }'
    Check 'bad name: refused' (Read-HostConfig -Path $f).ok $false
    Write-Json $f '{ "name": "bravo-host", "projects": {} }'
    Check 'no projects: refused' (Read-HostConfig -Path $f).ok $false
    Write-Json $f '{ not json'
    Check 'not json: refused' (Read-HostConfig -Path $f).ok $false
    Write-Json $f '{ "name": "bravo-host", "projects": { "alpha": { "path": "D:\\a" } } }'
    Check 'auto-update: on unless said otherwise' (Read-HostConfig -Path $f).autoUpdate $true
    Write-Json $f '{ "name": "bravo-host", "autoUpdate": false, "projects": { "alpha": { "path": "D:\\a" } } }'
    Check 'auto-update: off when host.json says false' (Read-HostConfig -Path $f).autoUpdate $false
    Write-Json $f '{ "name": "bravo-host", "autoUpdate": "no", "projects": { "alpha": { "path": "D:\\a" } } }'
    Check 'auto-update: anything but false leaves it on' (Read-HostConfig -Path $f).autoUpdate $true

    # ---- Convert-ClaimForHost --------------------------------------------------
    Write-Host "== Convert-ClaimForHost"
    $kitDir = 'D:\Whetstone\data\projects\alpha'
    $c = Convert-ClaimForHost -Claim (New-Claim) -HostName 'bravo-host' -Map @{ path = 'D:\src\alpha\'; worktreeRoot = '' } -ProjectDir $kitDir
    Check 'stamped with the host' $c.host 'bravo-host'
    Check 'repo is the clone here' $c.project.projectPath 'D:\src\alpha'
    Check 'worktree root defaults beside the clone, as the board host does' $c.project.worktreeRoot 'D:\src\.worktrees\alpha'
    Check 'data dir is the kit' $c.project.dataDir $kitDir
    Check "skill file is the kit's" $c.project.skillFile 'D:\Whetstone\data\projects\alpha\skills\implement\skill.md'
    Check "notes are the kit's copy" $c.project.memoryDir 'D:\Whetstone\data\projects\alpha\_notes'
    Check "notes index is the kit's copy" $c.project.memoryIndex 'D:\Whetstone\data\projects\alpha\_notes\MEMORY.md'
    Check 'run id kept' $c.runId 812
    Check 'base and delivery kept' "$($c.project.baseBranch) $($c.project.mergeInsteadOfPr)" 'main True'
    Check 'branch kept' $c.ticket.branch 't-12'
    Check "the board host's data root, for the prompt's path rule" $c.boardDataRoot 'C:\board\Whetstone\data'

    $c = Convert-ClaimForHost -Claim (New-Claim -MemoryDir '') -HostName 'bravo-host' -Map @{ path = 'D:\src\alpha'; worktreeRoot = 'E:\wt' } -ProjectDir $kitDir
    Check 'worktree root from host.json when given' $c.project.worktreeRoot 'E:\wt'
    Check 'a project with no notes store still has none' "[$($c.project.memoryDir)]" '[]'

    # ---- Install-ProjectKit ----------------------------------------------------
    Write-Host "== Install-ProjectKit"
    $src = Join-Path $root 'kit-src'
    New-Item -ItemType Directory -Path (Join-Path $src 'skills\implement') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $src '_notes') -Force | Out-Null
    Set-Content -Path (Join-Path $src 'skills\implement\skill.md') -Value 'new skill' -Encoding ascii
    Set-Content -Path (Join-Path $src 'guard.ps1') -Value '# new guard' -Encoding ascii
    Set-Content -Path (Join-Path $src '_notes\MEMORY.md') -Value 'notes' -Encoding ascii
    $zip = Join-Path $root 'kit.zip'
    Compress-Archive -Path (Join-Path $src '*') -DestinationPath $zip

    $proj = Join-Path $root 'projects\alpha'
    New-Item -ItemType Directory -Path (Join-Path $proj 'skills\old-phase') -Force | Out-Null
    Set-Content -Path (Join-Path $proj 'skills\old-phase\skill.md') -Value 'a skill since deleted on the board host' -Encoding ascii
    Set-Content -Path (Join-Path $proj 'guard.ps1') -Value '# old guard' -Encoding ascii

    Install-ProjectKit -ZipPath $zip -ProjectDir $proj
    Check 'kit: new skill in place' ((Get-Content (Join-Path $proj 'skills\implement\skill.md')) -join '') 'new skill'
    Check 'kit: guard replaced' ((Get-Content (Join-Path $proj 'guard.ps1')) -join '') '# new guard'
    Check 'kit: notes in place' (Test-Path (Join-Path $proj '_notes\MEMORY.md')) $true
    Check 'kit: a skill the board host no longer has is gone' (Test-Path (Join-Path $proj 'skills\old-phase')) $false

    # ---- Get-RunReport ---------------------------------------------------------
    Write-Host "== Get-RunReport"
    $done = Start-Process -FilePath 'cmd' -ArgumentList '/c', 'exit 7' -PassThru -WindowStyle Hidden
    try { $null = $done.Handle } catch { }
    $done.WaitForExit()
    $live = Start-Process -FilePath 'powershell' -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 30' -PassThru -WindowStyle Hidden
    try {
        $now = Get-Date
        $runs = @{
            5 = @{ Process = $live; Pid = $live.Id; TicketId = '12'; RunId = 812; Project = 'alpha'; StartedAt = $now; ExitCode = $null; ExitedAt = $null }
            6 = @{ Process = $done; Pid = $done.Id; TicketId = '13'; RunId = 813; Project = 'alpha'; StartedAt = $now; ExitCode = $null; ExitedAt = $now }
            7 = @{ Process = $null; Pid = $null; TicketId = '14'; RunId = 814; Project = 'alpha'; StartedAt = $now; ExitCode = 3; ExitedAt = $now }
        }
        $report = Get-RunReport -Runs $runs
        Check 'report: one entry per run, in slot order' (($report | ForEach-Object { $_.slot }) -join ',') '5,6,7'
        Check 'report: a live run is not exited' $report[0].exited $false
        Check 'report: a finished run carries its exit code' "$($report[1].exited) $($report[1].exitCode)" 'True 7'
        Check 'report: a run that never started reports why by its code' "$($report[2].exited) $($report[2].exitCode)" 'True 3'
        Check 'report: ids are numbers' "$($report[0].ticketId.GetType().Name) $($report[0].runId)" 'Int32 812'
        $json = @{ runs = $report } | ConvertTo-Json -Depth 6 -Compress
        Check 'report: serialises as a list' (($json | ConvertFrom-Json).runs.Count) 3
        $one = @{ runs = (Get-RunReport -Runs @{ 5 = $runs[5] }) } | ConvertTo-Json -Depth 6 -Compress
        Check 'report: a single run still serialises as a list' ($one -match '"runs":\[') $true
        $none = @{ runs = (Get-RunReport -Runs @{}) } | ConvertTo-Json -Depth 6 -Compress
        Check 'report: no runs serialises as an empty list' ($none -match '"runs":\[\]') $true
    }
    finally { try { $live.Kill() } catch { } }

    # ---- Get-UpdateAction ------------------------------------------------------
    Write-Host "== Get-UpdateAction"
    $a = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $b = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    Check 'same commit: nothing to do' (Get-UpdateAction -Own $a -Board $a -LiveRuns 0 -Stopping $false -Enabled $true) 'none'
    Check 'same commit, other case: nothing to do' (Get-UpdateAction -Own $a -Board $a.ToUpper() -LiveRuns 0 -Stopping $false -Enabled $true) 'none'
    Check 'board named no commit: nothing to do' (Get-UpdateAction -Own $a -Board '' -LiveRuns 0 -Stopping $false -Enabled $true) 'none'
    Check 'own commit unknown: nothing to do' (Get-UpdateAction -Own '' -Board $b -LiveRuns 0 -Stopping $false -Enabled $true) 'none'
    Check 'differ with runs out: wait for them' (Get-UpdateAction -Own $a -Board $b -LiveRuns 2 -Stopping $false -Enabled $true) 'wait'
    Check 'differ, stopping: leave it' (Get-UpdateAction -Own $a -Board $b -LiveRuns 0 -Stopping $true -Enabled $true) 'none'
    Check 'differ, auto-update off: leave it' (Get-UpdateAction -Own $a -Board $b -LiveRuns 0 -Stopping $false -Enabled $false) 'none'
    Check 'differ and idle: update' (Get-UpdateAction -Own $a -Board $b -LiveRuns 0 -Stopping $false -Enabled $true) 'update'

    # ---- Update-OwnCheckout, against real repos -------------------------------
    Write-Host "== Update-OwnCheckout"
    $env:GIT_TERMINAL_PROMPT = '0'
    function New-Commit { param([string]$Dir, [string]$Text)
        Add-Content -Path (Join-Path $Dir 'f.txt') -Value $Text -Encoding ascii
        git -C $Dir add -A *> $null; git -C $Dir commit -q -m $Text *> $null
        return "$(git -C $Dir rev-parse HEAD)".Trim()
    }
    function New-Checkouts { param([string]$Name)
        $boardRepo = Join-Path $root "$Name-board"
        New-Item -ItemType Directory -Path $boardRepo -Force | Out-Null
        git -C $boardRepo init -q -b main *> $null
        git -C $boardRepo config user.email t@example.com; git -C $boardRepo config user.name T; git -C $boardRepo config commit.gpgsign false
        $null = New-Commit $boardRepo 'one'
        $here = Join-Path $root "$Name-worker"
        git clone -q $boardRepo $here *> $null
        git -C $here config user.email t@example.com; git -C $here config user.name T; git -C $here config commit.gpgsign false
        return @{ board = $boardRepo; here = $here }
    }

    $c = New-Checkouts 'ff'
    $target = New-Commit $c.board 'two'
    $null = New-Commit $c.board 'three, past the board host'
    git -C $c.board checkout -q $target *> $null   # the board host is on 'two', origin has more
    $r = Update-OwnCheckout -Repo $c.here -Sha $target
    Check 'a commit only origin has: fetched and moved to' "$($r.ok) $($r.state)" 'True moved'
    Check '... to exactly the board host commit, not the tip' "$(git -C $c.here rev-parse HEAD)".Trim() $target

    $c = New-Checkouts 'present'
    $target = New-Commit $c.board 'two'
    git -C $c.here fetch -q origin *> $null
    Check 'already fetched: moved' (Update-OwnCheckout -Repo $c.here -Sha $target).state 'moved'

    $c = New-Checkouts 'missing'
    $r = Update-OwnCheckout -Repo $c.here -Sha 'cccccccccccccccccccccccccccccccccccccccc'
    Check 'a commit origin does not have: missing, nothing moved' "$($r.ok) $($r.state)" 'False missing'

    $c = New-Checkouts 'dirty'
    $target = New-Commit $c.board 'two'
    Add-Content -Path (Join-Path $c.here 'f.txt') -Value 'a person editing' -Encoding ascii
    $before = "$(git -C $c.here rev-parse HEAD)".Trim()
    $r = Update-OwnCheckout -Repo $c.here -Sha $target
    Check 'modified tracked files: refused' "$($r.ok) $($r.state)" 'False dirty'
    Check '... and nothing moved' "$(git -C $c.here rev-parse HEAD)".Trim() $before

    $c = New-Checkouts 'untracked'
    $target = New-Commit $c.board 'two'
    Set-Content -Path (Join-Path $c.here 'host.json') -Value '{}' -Encoding ascii
    Check 'an untracked file (host.json, say) does not stop it' (Update-OwnCheckout -Repo $c.here -Sha $target).state 'moved'

    $c = New-Checkouts 'backwards'
    $old = "$(git -C $c.board rev-parse HEAD)".Trim()
    $ahead = New-Commit $c.here 'here, ahead of the board'
    $r = Update-OwnCheckout -Repo $c.here -Sha $old
    Check 'a board host behind this checkout: followed' "$($r.ok) $($r.state)" 'True rewound'
    Check '... onto exactly its commit' "$(git -C $c.here rev-parse HEAD)".Trim() $old
    Check '... with the old commit kept as a branch' "$(git -C $c.here rev-parse $r.backup)".Trim() $ahead
    Check '... named for when and what' ($r.backup -match '^runner-backup/\d{8}-\d{6}-[0-9a-f]{7}$') $true

    # What a rebase on the board host looks like from here: the commit this checkout followed is
    # replaced by a copy of itself on a new base.
    $c = New-Checkouts 'rebased'
    $followed = New-Commit $c.board 'ours'
    git -C $c.here fetch -q origin *> $null; git -C $c.here merge -q --ff-only $followed *> $null
    git -C $c.board reset -q --hard HEAD~1 *> $null
    $null = New-Commit $c.board 'theirs, from origin'
    $rebased = New-Commit $c.board 'ours, rebased'
    $r = Update-OwnCheckout -Repo $c.here -Sha $rebased
    Check 'a board host that rebased: followed' "$($r.ok) $($r.state)" 'True rewound'
    Check '... onto its new commit' "$(git -C $c.here rev-parse HEAD)".Trim() $rebased
    Check '... the commit it had followed kept' "$(git -C $c.here rev-parse $r.backup)".Trim() $followed

    $c = New-Checkouts 'sideways'
    $target = New-Commit $c.board 'board went one way'
    $mine = New-Commit $c.here 'this went another'
    $r = Update-OwnCheckout -Repo $c.here -Sha $target -FollowRewrites $false
    Check 'diverged, with followRewrites off: refused' "$($r.ok) $($r.state)" 'False not-ff'
    Check '... and nothing moved' "$(git -C $c.here rev-parse HEAD)".Trim() $mine
    Check '... and no backup made' "$(git -C $c.here branch --list 'runner-backup/*')".Trim() ''

    $c = New-Checkouts 'sideways-dirty'
    $target = New-Commit $c.board 'board went one way'
    $mine = New-Commit $c.here 'this went another'
    Add-Content -Path (Join-Path $c.here 'f.txt') -Value 'a person editing' -Encoding ascii
    $r = Update-OwnCheckout -Repo $c.here -Sha $target
    Check 'a rewrite over modified tracked files: still refused' "$($r.ok) $($r.state)" 'False dirty'
    Check '... and nothing moved' "$(git -C $c.here rev-parse HEAD)".Trim() $mine

    $c = New-Checkouts 'prune'
    foreach ($i in 1..12) { git -C $c.here branch ("runner-backup/20260101-0000{0:d2}-abcdef0" -f $i) *> $null }
    Remove-OldBackups -Repo $c.here
    $left = @(git -C $c.here for-each-ref --format='%(refname:short)' 'refs/heads/runner-backup/')
    Check 'only the newest ten backups are kept' $left.Count 10
    Check '... the oldest go first' ($left -contains 'runner-backup/20260101-000001-abcdef0') $false

    Write-Host "== update-runner.ps1: finding the runner"
    $upd = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'update-runner.ps1'), [ref]$null, [ref]$null)
    foreach ($fn in $upd.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([scriptblock]::Create($fn.Extent.Text)) }
    $common = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'heartbeat-common.ps1'), [ref]$null, [ref]$null)
    . ([scriptblock]::Create(($common.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Test-RunsScript' }, $false) | Select-Object -First 1).Extent.Text))
    $standIn = Join-Path $root 'standin\data'
    New-Item -ItemType Directory -Path $standIn -Force | Out-Null
    $RunnerScript = Join-Path $standIn 'runner.ps1'
    Set-Content -Path $RunnerScript -Value 'Start-Sleep 30' -Encoding ascii
    $elsewhere = Join-Path $root 'elsewhere\runner.ps1'
    New-Item -ItemType Directory -Path (Split-Path $elsewhere) -Force | Out-Null
    Set-Content -Path $elsewhere -Value 'Start-Sleep 30' -Encoding ascii
    Check 'no runner running: none found' @(Get-RunnerProcesses).Count 0
    $p1 = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $RunnerScript) -PassThru -WindowStyle Hidden
    $p2 = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $elsewhere) -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 800
        $found = @(Get-RunnerProcesses)
        Check 'this checkout''s runner is found' (@($found | ForEach-Object { $_.ProcessId }) -contains $p1.Id) $true
        Check '... and another checkout''s runner.ps1 is not' (@($found | ForEach-Object { $_.ProcessId }) -contains $p2.Id) $false
    }
    finally { foreach ($p in $p1, $p2) { try { $p.Kill() } catch { } } }
    Check 'no scheduled task runs a stand-in: none found' ($null -eq (Find-RunnerTask)) $true

    Write-Host "== Read-HostConfig followRewrites\"
    $hf = Join-Path $root 'host-follow.json'
    Write-Json $hf '{ "name": "bravo-host", "projects": { "alpha": { "path": "D:\\src\\alpha" } } }'
    Check 'followRewrites is on unless it says otherwise' (Read-HostConfig -Path $hf).followRewrites $true
    Write-Json $hf '{ "name": "bravo-host", "followRewrites": false, "projects": { "alpha": { "path": "D:\\src\\alpha" } } }'
    Check '... and off when it says false' (Read-HostConfig -Path $hf).followRewrites $false

    # ---- Invoke-RunnerSupervisor, with a stand-in for the runner ----------------
    Write-Host "== Invoke-RunnerSupervisor"
    $RunnerUpdatedExit = 75
    $count = Join-Path $root 'starts.txt'
    $standIn = Join-Path $root 'stand-in.ps1'
    # Exits 75 ("updated, start me again") the first time, then 3.
    Set-Content -Path $standIn -Encoding ascii -Value @"
Add-Content -Path '$count' -Value 'x'
if (@(Get-Content '$count').Count -lt 2) { exit 75 }
exit 3
"@
    $code = Invoke-RunnerSupervisor -ChildArgs @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $standIn) -PauseSeconds 0
    Check 'started again after an update, and once more only' @(Get-Content $count).Count 2
    Check 'ends with the first other exit code' $code 3

    # ---- Test-SupervisorAlive ---------------------------------------------------
    # The supervisor can die (a closed console, a Ctrl+C) and leave the runner running. After an
    # update the runner must know whether anything will start it again.
    Write-Host "== Test-SupervisorAlive"
    $common = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'heartbeat-common.ps1'), [ref]$null, [ref]$null)
    . ([scriptblock]::Create(($common.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Test-RunsScript' }, $false) | Select-Object -First 1).Extent.Text))
    $DataRoot = Join-Path $root 'sup-data'
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    $fakeRunner = Join-Path $DataRoot 'runner.ps1'
    $otherScript = Join-Path $DataRoot 'something-else.ps1'
    Set-Content -Path $fakeRunner -Value 'Start-Sleep 30' -Encoding ascii
    Set-Content -Path $otherScript -Value 'Start-Sleep 30' -Encoding ascii
    $sup = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fakeRunner) -PassThru -WindowStyle Hidden
    $other = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $otherScript) -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 800
        Check 'a live supervisor running this runner.ps1' (Test-SupervisorAlive -SupervisorPid $sup.Id) $true
        Check 'a live process that is something else (a reused pid)' (Test-SupervisorAlive -SupervisorPid $other.Id) $false
        Check 'none given' (Test-SupervisorAlive -SupervisorPid 0) $false
        $sup.Kill(); $sup.WaitForExit()
        Check 'a supervisor that has gone' (Test-SupervisorAlive -SupervisorPid $sup.Id) $false
    }
    finally { foreach ($p in $sup, $other) { try { $p.Kill() } catch { } } }

    # ---- The Claude account the board chose -------------------------------------------
    # Which logins this machine has is heartbeat-common.ps1's, which cannot be dot-sourced here
    # (it reads the config and starts a provider), so those functions are lifted out of it too.
    Write-Host "== Get-ClaudeLogins"
    $common = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'heartbeat-common.ps1'), [ref]$null, [ref]$null)
    foreach ($lifted in 'Get-ClaudeProfilePath', 'Get-ClaudeCredentialsPath', 'Read-ClaudeIdentity', 'Get-ClaudeLogins') {
        . ([scriptblock]::Create(($common.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq $lifted }, $false) | Select-Object -First 1).Extent.Text))
    }
    $script:ClaudeIdentityCache = @{}
    $home1 = Join-Path $root 'home'
    $second = Join-Path $root 'logins\second'
    $third = Join-Path $root 'logins\third'
    foreach ($d in (Join-Path $home1 '.claude'), $second, $third) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    # The CLI's own login: profile beside ~/.claude, credentials inside it.
    Write-Json (Join-Path $home1 '.claude.json') '{ "oauthAccount": { "accountUuid": "uuid-a", "emailAddress": "a@example.com" } }'
    Write-Json (Join-Path $home1 '.claude\.credentials.json') '{}'
    Write-Json (Join-Path $second '.claude.json') '{ "oauthAccount": { "accountUuid": "uuid-b" } }'
    Write-Json (Join-Path $second '.credentials.json') '{}'
    # Signed in once and signed out: a profile, no credentials.
    Write-Json (Join-Path $third '.claude.json') '{ "oauthAccount": { "accountUuid": "uuid-c" } }'
    $script:Accounts = @(
        [pscustomobject]@{ name = 'Second'; configDir = $second },
        [pscustomobject]@{ name = 'second'; configDir = $third },
        [pscustomobject]@{ name = 'Third'; configDir = $third },
        [pscustomobject]@{ name = 'NoDir'; configDir = '' })
    function Get-Global { param([string]$Key) if ($Key -eq 'claudeAccounts') { return $script:Accounts } return $null }
    $savedProfile = $env:USERPROFILE; $savedDir = $env:CLAUDE_CONFIG_DIR
    try {
        $env:USERPROFILE = $home1
        Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
        $logins = @(Get-ClaudeLogins)
        Check 'signed-in logins only, the CLI''s own first' (($logins | ForEach-Object { "$($_.name)=$($_.uuid)" }) -join ',') 'Default=uuid-a,Second=uuid-b'
        Check 'the CLI''s own login has no folder of its own' "$($logins[0].configDir)" ''
        Check 'an email when the profile has one' "$($logins[0].email)" 'a@example.com'

        Write-Host "== Find-LoginAccountId"
        Check 'a pause from a login folder names its account' (Find-LoginAccountId -Logins $logins -ConfigDir "$second\") 'uuid-b'
        Check 'a pause with no folder is the CLI''s own' (Find-LoginAccountId -Logins $logins -ConfigDir '') 'uuid-a'
        Check 'a folder that is no login here names none' "$(Find-LoginAccountId -Logins $logins -ConfigDir $third)" ''
    }
    finally {
        $env:USERPROFILE = $savedProfile
        if ($savedDir) { $env:CLAUDE_CONFIG_DIR = $savedDir } else { Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue }
    }

    Write-Host "== Set-ClaimClaudeAccount"
    $claim = New-Claim
    $order = [pscustomobject]@{ kind = 'start'; claude = $null }
    Check 'no choice from the board: nothing added' "$(Set-ClaimClaudeAccount -Claim $claim -Order $order -Logins $logins)" ''
    Check '... and the claim carries no account' ($null -eq $claim.claudeAccount) $true
    $order = [pscustomobject]@{ claude = [pscustomobject]@{ gated = $false; reason = 'Smart switch is off' } }
    $null = Set-ClaimClaudeAccount -Claim $claim -Order $order -Logins $logins
    Check 'the switch off: nothing added' ($null -eq $claim.claudeAccount) $true

    $claim = New-Claim
    $order = [pscustomobject]@{ claude = [pscustomobject]@{ gated = $true; accountId = 'UUID-B'; account = 'Work'; reason = 'x' } }
    Check 'an account this machine has: no refusal' "$(Set-ClaimClaudeAccount -Claim $claim -Order $order -Logins $logins)" ''
    Check '... its folder, by id whatever the case' $claim.claudeAccount.configDir $second
    Check '... and the board''s name for it' $claim.claudeAccount.account 'Work'

    $claim = New-Claim
    $savedDir = $env:CLAUDE_CONFIG_DIR
    Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
    $order = [pscustomobject]@{ claude = [pscustomobject]@{ gated = $true; accountId = 'uuid-a'; account = 'A' } }
    $null = Set-ClaimClaudeAccount -Claim $claim -Order $order -Logins $logins
    Check 'the CLI''s own login: an empty folder' "$($claim.claudeAccount.configDir)" ''
    Check '... gated, not refused' "$($claim.claudeAccount.gated) $($claim.claudeAccount.refused)" 'True '
    if ($savedDir) { $env:CLAUDE_CONFIG_DIR = $savedDir }

    $claim = New-Claim
    $order = [pscustomobject]@{ claude = [pscustomobject]@{ gated = $true; accountId = 'uuid-z'; account = 'Gone' } }
    Check 'an account signed out since the check-in refuses the run' (Set-ClaimClaudeAccount -Claim $claim -Order $order -Logins $logins) 'the board chose Claude account Gone, which this computer has no login for'

    $claim = New-Claim
    $order = [pscustomobject]@{ claude = [pscustomobject]@{ gated = $true; accountId = $null; reason = 'A weekly 85% >= 80%' } }
    Check 'no account under its limits: the run is not refused here' "$(Set-ClaimClaudeAccount -Claim $claim -Order $order -Logins $logins)" ''
    Check '... the worker is told why, to turn a Claude phase down' $claim.claudeAccount.refused 'A weekly 85% >= 80%'
    Check '... with no folder' "$($claim.claudeAccount.configDir)" ''
}
# An exception is a failure, not the end of the list: without this, everything after it
# goes unrun and the summary still says every check passed.
catch { $script:fail++; $script:total++; Write-Host "FAIL  stopped by an exception: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
