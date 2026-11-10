# Exercises data/process-table.ps1: the one process table and tree kill the fleet uses on any OS.
#
# A Mac's half is read from ps, which this Windows machine cannot run, so it is driven with a Mac's
# own output; the rest runs against this machine's real processes.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/process-table.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
$script:total = 0; $script:fail = 0
function Check {
    param([string]$Name, $Got, $Want)
    $script:total++
    if ("$Got" -ceq "$Want") { Write-Host "ok    $Name" }
    else { $script:fail++; Write-Host "FAIL  $Name - expected '$Want', got '$Got'" -ForegroundColor Red }
}

. (Join-Path $PSScriptRoot 'process-table.ps1')
$Self = (Get-Process -Id $PID).Path

try {
    Write-Host "== ConvertFrom-PsListing, from a Mac's ps"
    # ps -axww -o pid=,ppid=,lstart=,args= on macOS, under LC_ALL=C.
    $mac = @(
        '    1     0 Mon Nov  2 08:14:03 2026 /sbin/launchd',
        '  812     1 Mon Nov  9 10:00:00 2026 /usr/local/bin/pwsh -NoProfile -ExecutionPolicy Bypass -File /Users/pat/Whetstone/data/worker.ps1 -Agent 3 -TicketId 42',
        '  840   812 Mon Nov  9 10:00:07 2026 node /usr/local/bin/claude -p --output-format stream-json',
        '  901     1 Tue Nov 10 23:59:59 2026 /Applications/Visual Studio Code.app/Contents/MacOS/Electron',
        'not a row at all'
    )
    $rows = ConvertFrom-PsListing -Lines $mac
    Check 'every row that is one, and nothing else' $rows.Count 4
    $w = $rows | Where-Object ProcessId -eq 812
    Check 'pid and parent' "$($w.ProcessId) $($w.ParentProcessId)" '812 1'
    Check 'the name is the executable''s file name' $w.Name 'pwsh'
    Check 'the command line is whole' $w.CommandLine '/usr/local/bin/pwsh -NoProfile -ExecutionPolicy Bypass -File /Users/pat/Whetstone/data/worker.ps1 -Agent 3 -TicketId 42'
    Check 'a day of one digit (two spaces before it) still reads' ($rows | Where-Object ProcessId -eq 1).CreationDate.ToString('yyyy-MM-dd HH:mm:ss') '2026-11-02 08:14:03'
    Check 'the start time' $w.CreationDate.ToString('yyyy-MM-dd HH:mm:ss') '2026-11-09 10:00:00'
    Check 'an agent under it is named node, which the claude pattern lists' ($rows | Where-Object ProcessId -eq 840).Name 'node'
    Check 'a path with a space is cut at it, and matches no pattern' ($rows | Where-Object ProcessId -eq 901).Name 'Visual'
    Check 'an empty listing is an empty table' @(ConvertFrom-PsListing -Lines @()).Count 0

    Write-Host "== Get-ProcessTable, on this machine"
    $me = @(Get-ProcessTable -Id $PID)
    Check 'one process by id' $me.Count 1
    Check '... named as its image' $me[0].Name ([IO.Path]::GetFileName($Self))
    $parent = if ($PSVersionTable.PSEdition -eq 'Core') { (Get-Process -Id $PID).Parent.Id } else { $me[0].ParentProcessId }
    Check '... with its parent' $me[0].ParentProcessId $parent
    Check '... and its command line' ($me[0].CommandLine -like '*process-table.tests.ps1*') $true
    Check 'the fleet''s shells include this one' (@(Get-ProcessTable -Shells | Where-Object ProcessId -eq $PID).Count) 1
    Check 'the whole table is more than the shells' (@(Get-ProcessTable).Count -gt @(Get-ProcessTable -Shells).Count) $true
    Check 'a pid that is not running is no rows' @(Get-ProcessTable -Id 999999).Count 0

    Write-Host "== Stop-ProcessTree"
    # A shell that starts a grandchild and waits on it: the kill has to take both.
    $marker = Join-Path ([IO.Path]::GetTempPath()) ("pt-" + [guid]::NewGuid().ToString('N') + '.txt')
    $inner = "Start-Sleep 60"
    $outer = "`$c = Start-Process -FilePath '$Self' -ArgumentList '-NoProfile','-Command','$inner' -PassThru -WindowStyle Hidden; Set-Content -Path '$marker' -Value `$c.Id; Start-Sleep 60"
    $p = Start-Process -FilePath $Self -ArgumentList '-NoProfile', '-Command', $outer -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $marker) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    $child = [int]("$(Get-Content $marker -ErrorAction SilentlyContinue)".Trim())
    Check 'the grandchild started' ($child -gt 0) $true
    Stop-ProcessTree -Id $p.Id
    Start-Sleep -Milliseconds 800
    Check 'the process is gone' $p.HasExited $true
    Check '... and what it started' ($null -eq (Get-Process -Id $child -ErrorAction SilentlyContinue)) $true
    Remove-Item $marker -Force -ErrorAction SilentlyContinue
    Stop-ProcessTree -Id 0
    Stop-ProcessTree -Id 999999
    Check 'nothing to kill is not an error' $true $true

    Write-Host "== The setup shell, from worker.ps1"
    $tree = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'worker.ps1'), [ref]$null, [ref]$null)
    . ([scriptblock]::Create(($tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Get-SetupShell' }, $false) | Select-Object -First 1).Extent.Text))
    $s = Get-SetupShell -Script '( npm ci ) 2>&1' -Windows $true
    Check 'Windows: cmd, AutoRun skipped' "$($s.Exe) $($s.Args)" 'cmd.exe /d /s /c "( npm ci ) 2>&1"'
    $s = Get-SetupShell -Script '( npm ci ) 2>&1 && ( dotnet restore ) 2>&1' -Windows $false
    Check 'elsewhere: sh -c, the script one argument' "$($s.Exe)|$($s.Args -join '|')" '/bin/sh|-c|( npm ci ) 2>&1 && ( dotnet restore ) 2>&1'
    Check '... named sh, which an orphaned setup is told apart by' $s.Name 'sh'
    Check 'unasked, it is this machine''s' (Get-SetupShell -Script 'x').Name $(if ($OnWindows) { 'cmd' } else { 'sh' })
}
catch { $script:fail++; $script:total++; Write-Host "FAIL  stopped by an exception: $($_.Exception.Message)" -ForegroundColor Red }

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $script:total CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$script:fail of $script:total FAILED" -ForegroundColor Red
exit 1
