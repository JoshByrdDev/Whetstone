# Exercises Get-AgentRole in heartbeat-common.ps1: which provider, model and effort each kind of
# run takes, from agentRoles, from the settings that came before it, or from the defaults. The C#
# half (AgentRoles.Resolve) answers the same questions in AgentProviderTests; the two must agree.
#
# heartbeat-common.ps1 reads the machine's real config as it loads, so its two role functions are
# lifted out of its syntax tree and run over a config this file hands them.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/agent-roles.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'providers.ps1')

$common = Join-Path $PSScriptRoot 'heartbeat-common.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($common, [ref]$null, [ref]$null)
foreach ($name in @('Get-AgentRoleText', 'Get-AgentRole')) {
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $fn) { throw "$name not found in heartbeat-common.ps1" }
    . ([scriptblock]::Create($fn.Extent.Text))
}
$DefaultReasoningEffort = 'medium'

# The config the lifted functions read, set per case.
$script:Config = $null
function Get-Global([string]$Name) {
    if ($script:Config -and $null -ne $script:Config.$Name) { return $script:Config.$Name }
    return $null
}

$failed = 0
$passed = 0
function Check {
    param([string]$Name, [string]$Json, [string]$Role, [string]$Provider, [string]$Model, [string]$Effort)
    $script:Config = if ($Json) { $Json | ConvertFrom-Json } else { $null }
    $r = Get-AgentRole -Role $Role
    $got = "$($r.Provider.Key) $($r.Model) $($r.Effort)"
    $want = "$Provider $Model $Effort"
    if ($got -eq $want) { $script:passed++; Write-Host "ok    $Name" }
    else { $script:failed++; Write-Host "FAIL  $Name - expected '$want', got '$got'" -ForegroundColor Red }
}

Check 'nothing configured runs claude on its defaults' '' 'implement' 'claude' 'claude-opus-5-5' 'medium'
Check 'reflection defaults to the reflection model' '' 'reflection' 'claude' 'haiku' 'medium'

$mixed = '{ "agentRoles": {
    "implement": { "provider": "codex",  "model": "gpt-5.6-sol", "effort": "high" },
    "qa-test":   { "provider": "claude", "model": "claude-opus-5-5", "effort": "low" } } }'
Check 'implement runs on its own provider' $mixed 'implement' 'codex' 'gpt-5.6-sol' 'high'
Check 'qa-test runs on another at the same time' $mixed 'qa-test' 'claude' 'claude-opus-5-5' 'low'

$legacy = '{ "agentProvider": "codex", "workerModel": "gpt-5.6-terra", "reflectionModel": "gpt-5.6-luna",
    "phaseModels": { "speculate": "gpt-5.6-sol" }, "workerReasoningEffort": "xhigh" }'
Check 'a pre-roles config still runs its worker model' $legacy 'implement' 'codex' 'gpt-5.6-terra' 'xhigh'
Check 'and its per-phase override' $legacy 'speculate' 'codex' 'gpt-5.6-sol' 'xhigh'
Check 'and its reflection model' $legacy 'reflection' 'codex' 'gpt-5.6-luna' 'xhigh'

$outranks = '{ "agentProvider": "claude", "workerModel": "claude-opus-5", "workerReasoningEffort": "xhigh",
    "agentRoles": { "steward": { "provider": "gemini", "model": "flash" } } }'
Check 'a role entry outranks the legacy provider' $outranks 'steward' 'gemini' 'flash' 'xhigh'
Check 'a role with no entry still reads the legacy settings' $outranks 'refine' 'claude' 'claude-opus-5' 'xhigh'

$mismatch = '{ "agentRoles": {
    "implement":  { "provider": "codex", "model": "claude-opus-5-5" },
    "reflection": { "provider": "codex", "model": "" } } }'
Check 'another provider''s model becomes this provider''s default' $mismatch 'implement' 'codex' 'gpt-5.6-sol' 'medium'
Check 'a blank reflection model is the reflection default' $mismatch 'reflection' 'codex' 'gpt-5.6-luna' 'medium'

Check 'an unknown provider reads as claude' '{ "agentRoles": { "implement": { "provider": "nope" } } }' 'implement' 'claude' 'claude-opus-5-5' 'medium'

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed failed, $passed passed" -ForegroundColor Red; exit 1 }
Write-Host "all $passed passed"
