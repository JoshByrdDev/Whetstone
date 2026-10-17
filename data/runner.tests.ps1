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
