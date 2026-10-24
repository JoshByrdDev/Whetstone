# Exercises the reflection pass in data/worker.ps1 end to end against a stand-in agent CLI: a reply
# is read, parsed, deduplicated and filed into a throwaway notes store.
#
# It exists because reflection fails quietly by design - a reply that is not an answer is discarded
# and the cycle carries on - so a break in the plumbing looks exactly like a model with nothing to
# say. Two such breaks sat in it together for days: the CLI's stderr folded into the reply ahead of
# the JSON, and a throw between claiming a fact's key and writing the fact.
#
# worker.ps1 cannot be dot-sourced - it is a script that works a claim - so its functions are lifted
# out of its syntax tree and defined here, as worker-base.tests.ps1 does.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-reflect.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'

$script:log = New-Object System.Collections.Generic.List[string]
function Write-Log { param([string]$Message) $script:log.Add($Message) }

$common = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'heartbeat-common.ps1'), [ref]$null, [ref]$null)
foreach ($fn in $common.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if (@('Invoke-WithMutex', 'Add-Utf8Text', 'Stop-DescendantAgent') -contains $fn.Name) { . ([scriptblock]::Create($fn.Extent.Text)) }
}
. (Join-Path $PSScriptRoot 'providers.ps1')
. (Join-Path $PSScriptRoot 'notes-store.ps1')

$tree = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'worker.ps1'), [ref]$null, [ref]$null)
foreach ($fn in $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
$contract = $tree.FindAll({
    $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $args[0].Left.Extent.Text -eq '$ReflectOutputContract' }, $false) | Select-Object -First 1
. ([scriptblock]::Create($contract.Extent.Text))

$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-reflect-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

# The stand-in CLI: swallows the prompt on stdin, optionally warns on stderr the way the real one
# does about a project's settings file, then prints whatever reply the case asked for.
$fakeCli = Join-Path $root 'agent.cmd'
Set-Content -Path $fakeCli -Encoding ascii -Value @'
@echo off
more > nul
if "%FAKE_WARN%"=="1" echo Permission allow rule (.claude\settings.json): Write(.cache/**) is not matched by file permission checks 1>&2
if exist "%FAKE_REPLY%" type "%FAKE_REPLY%"
'@

$ReflectProvider = [pscustomobject]@{
    Key = 'claude'; Exe = $fakeCli; DisplayName = 'stand-in'; ProcessPattern = @('no-such-process*'); SupportsReasoning = $true
}
$ReflectionModel = 'haiku'
$ReflectEffort = 'medium'

# One reflection over a fresh project folder and notes store. $Reply of $null means the CLI prints
# nothing on stdout at all.
function Invoke-Case {
    param([string]$Name, $Reply, [switch]$Warn)

    $dir  = Join-Path $root $Name
    $proj = Join-Path $dir 'proj'
    $repo = Join-Path $dir 'repo'
    $mem  = Join-Path $repo '.claude\memory'
    New-Item -ItemType Directory -Path $proj, $mem -Force | Out-Null
    Set-Content -Path (Join-Path $proj 'reflect-prompt.md') -Value 'Look for durable facts.' -Encoding ascii
    Set-Content -Path (Join-Path $mem 'MEMORY.md') -Value '# Notes' -Encoding ascii

    $replyFile = Join-Path $dir 'reply.txt'
    if ($null -ne $Reply) { Set-Content -Path $replyFile -Value $Reply -Encoding ascii }
    $env:FAKE_REPLY = $replyFile
    $env:FAKE_WARN  = if ($Warn) { '1' } else { '0' }

    $script:log.Clear()
    Invoke-Reflection -ProjectDataDir $proj -RepoDir $repo -RunLog (1..10 | ForEach-Object { "line $_" }) `
        -MemoryDir $mem -TicketId "PROJ-$Name" 2>$null

    return @{ proj = $proj; mem = $mem; last = $script:log[$script:log.Count - 1].Trim() }
}

$fact    = '[{"kind":"codebase","key":"migrations-need-bom","text":"Migration files must start with a UTF-8 BOM.","confidence":"high"}]'
$twoKind = '[{"kind":"environment","key":"db-on-1433","text":"The database listens on localhost,1433.","confidence":"high"},' +
           '{"kind":"process","key":"skill-wording","text":"The implement skill should say so.","confidence":"low"}]'

# --- An empty answer is an answer, with or without the CLI warning ahead of it ---
$r = Invoke-Case -Name 'empty-quiet' -Reply '[]'
Check 'empty reply: nothing new' $r.last 'Reflection: nothing new'

$r = Invoke-Case -Name 'empty-warned' -Reply '[]' -Warn
Check 'empty reply behind a stderr warning: still nothing new' $r.last 'Reflection: nothing new'

# --- A fact lands in the notes store, and its key is claimed only alongside it ---
$r = Invoke-Case -Name 'fact-quiet' -Reply $fact
Check 'one fact: reported as written' $r.last 'Reflection: 1 written to notes, 0 folded into existing notes, 0 filed for review, 0 already known'
Check 'one fact: the note exists' (Test-Path (Join-Path $r.mem 'migrations-need-bom.md')) $true
Check 'one fact: its key is recorded' ((Get-Content (Join-Path $r.proj 'reflection-keys.txt')) -join ',') 'migrations-need-bom'

$r = Invoke-Case -Name 'fact-warned' -Reply $fact -Warn
Check 'one fact behind a stderr warning: the note exists' (Test-Path (Join-Path $r.mem 'migrations-need-bom.md')) $true

# --- Routing: first-hand repo and machine facts are written, pipeline proposals wait for a human ---
$r = Invoke-Case -Name 'routing' -Reply $twoKind -Warn
Check 'routing: one written, one filed' $r.last 'Reflection: 1 written to notes, 0 folded into existing notes, 1 filed for review, 0 already known'
Check 'routing: the environment fact is a note' (Test-Path (Join-Path $r.mem 'db-on-1433.md')) $true
Check 'routing: the process proposal is not a note' (Test-Path (Join-Path $r.mem 'skill-wording.md')) $false
Check 'routing: the process proposal is in the review file' `
    ([bool](Select-String -Path (Join-Path $r.proj 'improvement-suggestions.md') -Pattern 'key: skill-wording' -Quiet)) $true

# --- Not an answer: discarded, and nothing is claimed or written ---
$r = Invoke-Case -Name 'chatter' -Reply 'You have hit your limit - resets 11:30am' -Warn
Check 'a reply with no array: discarded, quoting the reply rather than the warning' `
    $r.last 'Reflection produced no parseable findings - discarded: You have hit your limit - resets 11:30am'
Check 'a reply with no array: no key claimed' (Test-Path (Join-Path $r.proj 'reflection-keys.txt')) $false

$r = Invoke-Case -Name 'silent-warned' -Reply $null -Warn
Check 'no reply at all: the log says what stderr said' ($r.last -like 'Reflection produced no parseable findings - discarded: Permission allow rule*') $true

$r = Invoke-Case -Name 'silent' -Reply $null
Check 'no reply and no warning: no output' $r.last 'Reflection produced no output'

# --- A fact a steward said it learned (Add-LearnedFact, called by steward.ps1 after a run) ---
$learnCmd = [pscustomobject]@{ Exe = $fakeCli; Args = @() }
$lmem = Join-Path $root 'learned\memory'
New-Item -ItemType Directory -Path $lmem -Force | Out-Null
Set-Content -Path (Join-Path $lmem 'MEMORY.md') -Value '# Notes' -Encoding ascii
$env:FAKE_WARN = '0'
$env:FAKE_REPLY = Join-Path $root 'learned\none.txt'

$r = Add-LearnedFact -MemoryDir $lmem -Key 'day-read' -Text 'The day read is loadDay in core/day.ts.' -Stamp '2026-10-23 10:00' -ReflectCmd $learnCmd -RepoDir $root
Check 'learned: a new fact is a new note' "$($r.Outcome) $($r.Key)" 'new day-read'
Check 'learned: the note says it came from the steward' `
    ([bool](Select-String -Path (Join-Path $lmem 'day-read.md') -Pattern 'source: steward' -SimpleMatch -Quiet)) $true
Check 'learned: the index lists it' ([bool](Select-String -Path (Join-Path $lmem 'MEMORY.md') -Pattern 'day-read' -SimpleMatch -Quiet)) $true

$r = Add-LearnedFact -MemoryDir $lmem -Key 'day-read' -Text 'Port 52547 is free on this machine for the emulator.' -Stamp '2026-10-23 10:01' -ReflectCmd $learnCmd -RepoDir $root
Check 'learned: a taken key gets a number, never overwrites' "$($r.Outcome) $($r.Key)" 'new day-read-2'

# A fact about the same thing as a note already there is folded into that note.
$seed = "---`nname: check-timeouts`ndescription: `"How long the merge checks take`"`nmetadata:`n  type: project`n---`n`nThe database merge check takes about fifteen to twenty minutes and its timeout is forty five minutes per merge check run.`n"
[IO.File]::WriteAllText((Join-Path $lmem 'check-timeouts.md'), $seed)
$merged = "TARGET: check-timeouts`n---`nname: check-timeouts`ndescription: `"How long the merge checks take, and their timeout`"`nmetadata:`n  type: project`n  last-confirmed: 2026-10-23 10:02`n---`n`nThe database merge check takes about fifteen to twenty minutes, sometimes twenty five, and its timeout is forty five minutes per merge check run.`n"
$env:FAKE_REPLY = Join-Path $root 'learned\merge.txt'
[IO.File]::WriteAllText($env:FAKE_REPLY, $merged)
$r = Add-LearnedFact -MemoryDir $lmem -Key '' -Text 'The database merge check sometimes takes twenty five minutes; its timeout is forty five minutes per merge check run.' -Stamp '2026-10-23 10:02' -ReflectCmd $learnCmd -RepoDir $root
Check 'learned: a fact about a noted thing is folded into that note' "$($r.Outcome) $($r.Key)" 'merged check-timeouts'
Check 'learned: the folded note says the new part' `
    ([bool](Select-String -Path (Join-Path $lmem 'check-timeouts.md') -Pattern 'sometimes twenty five' -SimpleMatch -Quiet)) $true

# The placement call answering NEW leaves it a note of its own.
$env:FAKE_REPLY = Join-Path $root 'learned\new.txt'
[IO.File]::WriteAllText($env:FAKE_REPLY, "TARGET: NEW")
$r = Add-LearnedFact -MemoryDir $lmem -Key 'check-retry' -Text 'A database merge check that times out at forty five minutes is retried once before asking the owner.' -Stamp '2026-10-23 10:03' -ReflectCmd $learnCmd -RepoDir $root
Check 'learned: NEW from the placement call is a note of its own' "$($r.Outcome) $($r.Key)" 'new check-retry'

$r = Add-LearnedFact -MemoryDir $lmem -Key 'x' -Text '   ' -Stamp '2026-10-23 10:04' -ReflectCmd $learnCmd -RepoDir $root
Check 'learned: nothing said, nothing placed' $r.Outcome 'none'

Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item Env:FAKE_REPLY, Env:FAKE_WARN -ErrorAction SilentlyContinue

Write-Host ""
if ($script:fail) { Write-Host "$($script:fail) of $($script:total) failed" -ForegroundColor Red; exit 1 }
Write-Host "all $($script:total) passed" -ForegroundColor Green
