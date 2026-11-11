# Exercises Get-WorkerEnv in data/worker.ps1 - a project's workerEnv turned into the environment a
# worker runs with - lifted out of worker.ps1's syntax tree as the other worker tests do.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-env.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Stop'
$worker = Join-Path $PSScriptRoot 'worker.ps1'
$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$null)
$fn = $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Get-WorkerEnv' }, $false) | Select-Object -First 1
. ([scriptblock]::Create($fn.Extent.Text))

$script:fail = 0
$script:total = 0
function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

$root = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'whetstone-env', 'cache')

# Nothing set: nothing to do. A claim from an app not restarted onto workerEnv carries no field.
$none = Get-WorkerEnv -Spec $null -CacheRoot $root -ProjectKey 'alpha'
Check 'no workerEnv sets nothing' $none.Env.Count 0
Check 'no workerEnv refuses nothing' $none.Refused.Count 0

# The shape the claim arrives in: JSON, read by ConvertFrom-Json.
$spec = '{ "TURBO_CACHE_DIR": "{machineCache}/turbo", "TEST_WORKERS": "2", "_comment": "x" }' | ConvertFrom-Json
$r = Get-WorkerEnv -Spec $spec -CacheRoot $root -ProjectKey 'alpha'
Check 'machineCache is this machine, this project' $r.Env['TURBO_CACHE_DIR'] ([IO.Path]::GetFullPath([IO.Path]::Combine($root, 'alpha', 'turbo')))
Check 'a plain value passes as written' $r.Env['TEST_WORKERS'] '2'
Check 'a _comment key is not an env var' $r.Env.Contains('_comment') $false
Check 'a machineCache value is a folder to make' $r.Dirs.Contains('TURBO_CACHE_DIR') $true
Check 'a plain value is not' $r.Dirs.Contains('TEST_WORKERS') $false

# Two projects on one machine never share a cache by accident.
$b = Get-WorkerEnv -Spec $spec -CacheRoot $root -ProjectKey 'bravo'
Check 'each project has its own machineCache' ($b.Env['TURBO_CACHE_DIR'] -ne $r.Env['TURBO_CACHE_DIR']) $true

# What is the platform's is refused, and said so - never set.
$bad = [ordered]@{
    WHETSTONE_BOARD_HOST = 'evil:1'; CLAUDE_CONFIG_DIR = 'x'; ANTHROPIC_API_KEY = 'x'; GIT_DIR = 'x'
    PATH = 'x'; home = 'x'; TEMP = 'x'; CODEX_HOME = 'x'; 'NOT-A-NAME' = 'x'; NUMBER = 3; OK_NAME = 'fine'
}
$x = Get-WorkerEnv -Spec $bad -CacheRoot $root -ProjectKey 'alpha'
Check 'only the allowed name is set' (@($x.Env.Keys) -join ',') 'OK_NAME'
foreach ($n in 'WHETSTONE_BOARD_HOST', 'CLAUDE_CONFIG_DIR', 'ANTHROPIC_API_KEY', 'GIT_DIR', 'PATH', 'home', 'TEMP', 'CODEX_HOME', 'NOT-A-NAME', 'NUMBER') {
    Check "$n is refused" ($x.Refused -contains $n) $true
}

Write-Host ""
if ($script:fail -gt 0) { Write-Host "$script:fail of $script:total FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all $script:total passed" -ForegroundColor Green
