# Exercises Set-ExperimentArm: what an experiment arm in a claim does to a run's provider, model,
# effort and skill file (docs/bugs-and-experiments-plan.md, 2.4). The board decides the arm; the
# worker applies it all or nothing, and says when it could not.
#
# worker.ps1 cannot be dot-sourced - it is a script that works a claim - so the function is lifted
# out of its syntax tree, as in worker-tracking.tests.ps1.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-experiment.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'providers.ps1')

$worker = Join-Path $PSScriptRoot 'worker.ps1'
$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$null)
foreach ($name in @('Set-ExperimentArm')) {
    $fn = $tree.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $false)
    if (-not $fn) { throw "$name not found in worker.ps1" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-experiment-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

try {
    $skills = Join-Path $root 'skills'
    New-Item -ItemType Directory -Path (Join-Path $skills 'implement\variants') -Force | Out-Null
    $base = Join-Path $skills 'implement\skill.md'
    Set-Content -Path $base -Value 'the skill' -Encoding ascii
    Set-Content -Path (Join-Path $skills 'implement\variants\terse.md') -Value 'a terser skill' -Encoding ascii

    $claude = Get-AgentProvider -Name 'claude'
    $role = [pscustomobject]@{ Role = 'implement'; Provider = $claude; Model = 'claude-opus-5-5'; Effort = 'high' }
    function Arm($props) {
        $h = @{ id = 3; arm = 'variant'; applied = $true; skillVariant = $null; model = $null; effort = $null; provider = $null; note = $null }
        foreach ($k in $props.Keys) { $h[$k] = $props[$k] }
        return [pscustomobject]$h
    }

    Write-Host "== no experiment"
    $r = Set-ExperimentArm -Role $role -Experiment $null -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'the role is left as it is' "$($r.Provider.Key) $($r.Model) $($r.Effort)" 'claude claude-opus-5-5 high'
    Check '... and so is the skill' $r.SkillFile $base
    Check '... and nothing names an experiment' ($null -eq $r.Id) $true

    Write-Host "== each override"
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ effort = 'xhigh' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'effort' "$($r.Effort) $($r.Applied) $($r.Arm) $($r.Id)" 'xhigh True variant 3'
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ model = 'claude-sonnet-5-5' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'model' "$($r.Model) $($r.Applied)" 'claude-sonnet-5-5 True'
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ skillVariant = 'terse' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'the variant file replaces skill.md' (Split-Path -Leaf $r.SkillFile) 'terse.md'
    Check '... from the skills folder it was handed' (Test-Path -LiteralPath $r.SkillFile) $true
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ provider = 'codex' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    $codex = Get-AgentProvider -Name 'codex'
    Check 'a provider switch takes that provider and its default model' "$($r.Provider.Key) $($r.Model)" "codex $($codex.DefaultWorkerModel)"
    Check '... and keeps the role effort' $r.Effort 'high'

    Write-Host "== what cannot be honoured runs as configured"
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ model = 'gpt-5.6-sol'; effort = 'low' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check "another provider's model is refused" "$($r.Applied) $($r.Model) $($r.Effort)" 'False claude-opus-5-5 high'
    Check '... and says why' ("$($r.Note)" -like '*not a claude model*') $true
    Check '... but still names its arm' "$($r.Id) $($r.Arm)" '3 variant'
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ skillVariant = 'gone' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'a missing variant file runs skill.md' "$($r.Applied) $($r.SkillFile -eq $base)" 'False True'
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ provider = 'nobody' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'an unknown provider is refused' "$($r.Applied) $($r.Provider.Key)" 'False claude'

    Write-Host "== the board said not to"
    $r = Set-ExperimentArm -Role $role -Experiment (Arm @{ applied = $false; effort = 'low'; note = 'experiment stopped' }) -SkillsDir $skills -Skill 'implement' -SkillFile $base
    Check 'applied false changes nothing' "$($r.Applied) $($r.Effort)" 'False high'
    Check '... and passes the board note on' $r.Note 'experiment stopped'
}
finally {
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
