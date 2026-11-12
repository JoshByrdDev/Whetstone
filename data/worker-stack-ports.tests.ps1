# Exercises the stack-port check in data/worker.ps1 - whether a run's stack slot can listen on its
# ports, given Windows' reserved ranges - lifted out of worker.ps1's syntax tree as the other worker
# tests do. Nothing here calls netsh: the tests hand the parser netsh's text.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-stack-ports.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Stop'
$worker = Join-Path $PSScriptRoot 'worker.ps1'
$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$null)
foreach ($name in 'ConvertFrom-ExcludedPortRanges', 'Get-StackPortProblem', 'Format-StackPortComment') {
    $fn = $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq $name }, $false) | Select-Object -First 1
    . ([scriptblock]::Create($fn.Extent.Text))
}

$script:fail = 0
$script:total = 0
function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

# netsh's table as it prints it, with a person's reservation (*) and the NAT service's block.
$netsh = @"

Protocol tcp Port Exclusion Ranges

Start Port    End Port
----------    --------
      5357        5357
      8081        8081     *
      8085        8184

* - Administered port exclusions.

"@
$ranges = ConvertFrom-ExcludedPortRanges -Text $netsh
Check 'every row is read' $ranges.Count 3
Check 'a range has its start' $ranges[2].Start 8085
Check 'a range has its end' $ranges[2].End 8184
Check 'a starred row is administered' $ranges[1].Administered $true
Check 'an unstarred row is not' $ranges[2].Administered $false
Check 'no text, no ranges' (ConvertFrom-ExcludedPortRanges -Text '').Count 0

# Slot 0 is 8080 and 8081, slot N each plus 10N.
$spec = '{ "ports": [8080, 8081], "slotStep": 10 }' | ConvertFrom-Json

Check 'slot 0 is free: 8080 reserved by nobody, 8081 by a person' ($null -eq (Get-StackPortProblem -Spec $spec -Slot 0 -Ranges $ranges)) $true

$p1 = Get-StackPortProblem -Spec $spec -Slot 1 -Ranges $ranges
Check 'slot 1 lands in the NAT block' ($null -ne $p1) $true
Check 'both of slot 1''s ports are held' (($p1.Held | ForEach-Object Port) -join ',') '8090,8091'
Check 'a held port says which range holds it' "$($p1.Held[0].Start)-$($p1.Held[0].End)" '8085-8184'
# Slots 0 and 1, less what is already a person's: 8080 alone, then 8090-8091.
Check 'what to reserve skips the administered port and runs contiguous ports together' `
    (($p1.Reserve | ForEach-Object { "$($_.Start)+$($_.Count)" }) -join ',') '8080+1,8090+2'

Check 'a slot past the block is free' ($null -eq (Get-StackPortProblem -Spec $spec -Slot 11 -Ranges $ranges)) $true
Check 'nothing reserved, nothing held' ($null -eq (Get-StackPortProblem -Spec $spec -Slot 1 -Ranges @())) $true
Check 'no stackPorts, nothing to check' ($null -eq (Get-StackPortProblem -Spec $null -Slot 1 -Ranges $ranges)) $true

# No step: every slot means slot 0's ports.
$flat = '{ "ports": [8090] }' | ConvertFrom-Json
Check 'no slotStep checks the same ports on every slot' (($(Get-StackPortProblem -Spec $flat -Slot 3 -Ranges $ranges).Held | ForEach-Object Port) -join ',') '8090'

# What the ticket is told: the machine, the ports, and commands that fix it.
$c = Format-StackPortComment -Problem $p1 -Slot 1 -Machine 'bravo-host'
Check 'the comment names the machine' ($c -like '*bravo-host has reserved*') $true
Check 'the comment names the held port and its range' ($c -like '*8090 (reserved in 8085-8184)*') $true
Check 'the comment reserves each run' ($c -like '*startport=8090 numberofports=2*') $true
Check 'the comment stops winnat before reserving' ($c.IndexOf('net stop winnat') -lt $c.IndexOf('add excludedportrange')) $true
Check 'the comment starts winnat after' ($c.IndexOf('net start winnat') -gt $c.IndexOf('add excludedportrange')) $true
$fence = [string]::new([char]96, 3)
Check 'the commands are fenced' ($c.Contains("${fence}powershell") -and $c.Contains("tcp`n$fence")) $true

Write-Host ""
if ($script:fail -gt 0) { Write-Host "$script:fail of $script:total FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all $script:total passed" -ForegroundColor Green
