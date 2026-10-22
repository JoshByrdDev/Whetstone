# Exercises the per-provider usage-limit pause in heartbeat-common.ps1: a refusal on one provider
# pauses only the phases that run on it, an expired pause clears itself, and the file from before
# pauses were per provider still counts - as claude's, which is what it always was.
#
# heartbeat-common.ps1 reads the machine's real config as it loads, so the functions are lifted out
# of its syntax tree and run over a config and a run folder this file hands them.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/usage-limit.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'providers.ps1')

$common = Join-Path $PSScriptRoot 'heartbeat-common.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($common, [ref]$null, [ref]$null)
foreach ($name in @('Get-UsageLimitReset', 'Get-AgentRoleText', 'Get-AgentRole', 'Get-UsageLimitFile', 'Set-UsageLimitPause',
                    'Get-UsageLimitPause', 'Get-PausedProviders', 'Get-OpenSkills')) {
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $fn) { throw "$name not found in heartbeat-common.ps1" }
    . ([scriptblock]::Create($fn.Extent.Text))
}
$DefaultReasoningEffort = 'medium'
$AgentRoleKeys = @('speculate', 'implement', 'qa-test', 'refine', 'review-pr', 'steward', 'reflection')

$RunDir = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-usage-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $RunDir | Out-Null
$LegacyUsageLimitFile = Join-Path $RunDir "usage-limit.json"

# implement on codex, everything else on claude.
$script:Config = '{ "agentRoles": { "implement": { "provider": "codex" } } }' | ConvertFrom-Json
function Get-Global([string]$Name) {
    if ($script:Config -and $null -ne $script:Config.$Name) { return $script:Config.$Name }
    return $null
}
$AgentProvidersInUse = @()
foreach ($r in $AgentRoleKeys) {
    $p = (Get-AgentRole -Role $r).Provider
    if (-not ($AgentProvidersInUse | Where-Object { $_.Key -eq $p.Key })) { $AgentProvidersInUse += $p }
}

$failed = 0
$passed = 0
function Check {
    param([string]$Name, $Got, $Want)
    if ("$Got" -eq "$Want") { $script:passed++; Write-Host "ok    $Name" }
    else { $script:failed++; Write-Host "FAIL  $Name - expected '$Want', got '$Got'" -ForegroundColor Red }
}
function Clear-Pauses { Get-ChildItem $RunDir -Filter 'usage-limit*.json' | Remove-Item -Force }

try {
    $all = 'speculate implement qa-test refine review-pr'

    Check 'nothing paused leaves every phase open' ((Get-OpenSkills -Paused (Get-PausedProviders)) -join ' ') $all

    Set-UsageLimitPause -Provider 'codex' -ResetsAt (Get-Date).AddHours(2) -Text 'hit your limit'
    $paused = Get-PausedProviders
    Check 'a codex refusal pauses codex alone' (($paused.Keys | Sort-Object) -join ' ') 'codex'
    Check 'and closes only the phase that runs on it' ((Get-OpenSkills -Paused $paused) -join ' ') 'speculate qa-test refine review-pr'

    Set-UsageLimitPause -Provider 'claude' -ResetsAt (Get-Date).AddHours(1) -Text 'hit your limit'
    Check 'both paused closes every phase' (@(Get-OpenSkills -Paused (Get-PausedProviders)).Count) 0

    Clear-Pauses
    Set-UsageLimitPause -Provider 'claude' -ResetsAt (Get-Date).AddMinutes(-5) -Text 'hit your limit'
    Check 'an expired pause does not count' (Get-PausedProviders).Count 0
    Check 'and its file is cleared' (Test-Path (Get-UsageLimitFile 'claude')) $false

    Clear-Pauses
    @{ resetsAt = (Get-Date).AddHours(1).ToString('o') } | ConvertTo-Json | Set-Content $LegacyUsageLimitFile
    $paused = Get-PausedProviders
    Check 'the pre-per-provider file pauses claude' (($paused.Keys | Sort-Object) -join ' ') 'claude'
    Check 'and leaves the codex phase open' ((Get-OpenSkills -Paused $paused) -join ' ') 'implement'

    # The reset time, in each CLI's wording. 10:00 on 22 Oct is "now".
    $now = Get-Date -Year 2026 -Month 10 -Day 22 -Hour 10 -Minute 0 -Second 0 -Millisecond 0
    $fmt = 'yyyy-MM-dd HH:mm'
    Check "claude's 'resets 8pm'" ((Get-UsageLimitReset -Text "You've hit your session limit - resets 8pm (America/Chicago)" -Now $now).ToString($fmt)) '2026-10-22 20:00'
    Check "claude's 'resets Jan 2, 8pm'" ((Get-UsageLimitReset -Text "You've hit your weekly limit - resets Jan 2, 8pm" -Now $now).ToString($fmt)) '2027-01-02 20:00'
    Check "codex's 'try again at 3:42 PM'" ((Get-UsageLimitReset -Text "You've hit your usage limit. Visit https://chatgpt.com/settings/usage to purchase more credits or try again at 3:42 PM." -Now $now).ToString($fmt)) '2026-10-22 15:42'
    Check "codex's dated form" ((Get-UsageLimitReset -Text "You've hit your usage limit. Try again at Oct 23rd, 2026 3:42 PM." -Now $now).ToString($fmt)) '2026-10-23 15:42'
    Check "codex's 'try again later' pauses an hour" ((Get-UsageLimitReset -Text "You've hit your usage limit. Try again later." -Now $now).ToString($fmt)) '2026-10-22 11:00'
    Check 'a run that is not a limit is not paused' (Get-UsageLimitReset -Text 'Tests passed. Rate limits are documented in README.' -Now $now) ''
}
finally {
    Remove-Item $RunDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed failed, $passed passed" -ForegroundColor Red; exit 1 }
Write-Host "all $passed passed"
