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
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-reflect.tests.ps1
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
# The board, for the one call reflection makes to it: a defect filed as a Backlog ticket. Each call
# is recorded; $script:boardDown makes it answer as an unreachable board would.
$script:filed = New-Object System.Collections.Generic.List[object]
$script:boardDown = $false
function New-DefectTicket {
    param([string]$ProjectKey, [string]$Key, [string]$Text, [string]$FoundOn)
    if ($script:boardDown) { return $null }
    $script:filed.Add([pscustomobject]@{ Project = $ProjectKey; Key = $Key; Text = $Text; FoundOn = $FoundOn })
    return "$(900 + $script:filed.Count)"
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
if defined FAKE_PROMPT_OUT (more > "%FAKE_PROMPT_OUT%") else (more > nul)
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
    param([string]$Name, $Reply, [switch]$Warn, [string]$TicketId, [string]$IssueKey)

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
        -MemoryDir $mem -TicketId $(if ($TicketId) { $TicketId } else { "PROJ-$Name" }) -ProjectKey 'alpha' `
        -IssueKey $IssueKey -Branch '' 2>$null

    return @{ proj = $proj; mem = $mem; last = $script:log[$script:log.Count - 1].Trim() }
}

$fact    = '[{"kind":"codebase","key":"migrations-need-bom","text":"Migration files must start with a UTF-8 BOM.","confidence":"high"}]'
$twoKind = '[{"kind":"environment","key":"db-on-1433","text":"The database listens on localhost,1433.","confidence":"high"},' +
           '{"kind":"process","key":"skill-wording","text":"The implement skill should say so.","confidence":"high"}]'

# --- An empty answer is an answer, with or without the CLI warning ahead of it ---
$r = Invoke-Case -Name 'empty-quiet' -Reply '[]'
Check 'empty reply: nothing new' $r.last 'Reflection: nothing new'

$r = Invoke-Case -Name 'empty-warned' -Reply '[]' -Warn
Check 'empty reply behind a stderr warning: still nothing new' $r.last 'Reflection: nothing new'

# --- A fact lands in the notes store, and its key is claimed only alongside it ---
$r = Invoke-Case -Name 'fact-quiet' -Reply $fact
Check 'one fact: reported as written' $r.last 'Reflection: 1 written to notes, 0 folded into existing notes, 0 filed for review, 0 filed as tickets, 0 already known, 0 dropped as guesses'
Check 'one fact: the note exists' (Test-Path (Join-Path $r.mem 'migrations-need-bom.md')) $true
Check 'one fact: its key is recorded' ((Get-Content (Join-Path $r.proj 'reflection-keys.txt')) -join ',') 'migrations-need-bom'

$r = Invoke-Case -Name 'fact-warned' -Reply $fact -Warn
Check 'one fact behind a stderr warning: the note exists' (Test-Path (Join-Path $r.mem 'migrations-need-bom.md')) $true

# --- Routing: first-hand repo and machine facts are written, pipeline proposals wait for a human ---
$r = Invoke-Case -Name 'routing' -Reply $twoKind -Warn
Check 'routing: one written, one filed' $r.last 'Reflection: 1 written to notes, 0 folded into existing notes, 1 filed for review, 0 filed as tickets, 0 already known, 0 dropped as guesses'
Check 'routing: the environment fact is a note' (Test-Path (Join-Path $r.mem 'db-on-1433.md')) $true
Check 'routing: the process proposal is not a note' (Test-Path (Join-Path $r.mem 'skill-wording.md')) $false
Check 'routing: the process proposal is in the review file' `
    ([bool](Select-String -Path (Join-Path $r.proj 'improvement-suggestions.md') -Pattern 'key: skill-wording' -Quiet)) $true

# --- A guess is dropped, whatever its kind, and its key is left for a run that sees it ---
$guesses = '[{"kind":"codebase","key":"maybe-cache","text":"The cache is probably rebuilt on every request.","confidence":"low"},' +
           '{"kind":"process","key":"maybe-skill","text":"The QA skill seems to forbid reseeding, which looks wrong.","confidence":"low"},' +
           '{"kind":"environment","key":"no-confidence","text":"The emulator wants a cold boot."}]'
$r = Invoke-Case -Name 'guesses' -Reply $guesses
Check 'guesses: all three dropped' $r.last 'Reflection: 0 written to notes, 0 folded into existing notes, 0 filed for review, 0 filed as tickets, 0 already known, 3 dropped as guesses'
Check 'guesses: no note' (@(Get-ChildItem $r.mem -Filter '*.md' | Where-Object { $_.Name -ne 'MEMORY.md' }).Count) 0
Check 'guesses: nothing queued' (Test-Path (Join-Path $r.proj 'improvement-suggestions.md')) $false
Check 'guesses: no key claimed' (Test-Path (Join-Path $r.proj 'reflection-keys.txt')) $false

# --- A fact with no store to go to is not queued either ---
$dir = Join-Path $root 'no-store'; $proj = Join-Path $dir 'proj'
New-Item -ItemType Directory -Path $proj -Force | Out-Null
Set-Content -Path (Join-Path $proj 'reflect-prompt.md') -Value 'Look.' -Encoding ascii
Set-Content -Path (Join-Path $dir 'reply.txt') -Value $fact -Encoding ascii
$env:FAKE_REPLY = Join-Path $dir 'reply.txt'
$script:log.Clear()
Invoke-Reflection -ProjectDataDir $proj -RepoDir $dir -RunLog (1..10 | ForEach-Object { "line $_" }) -MemoryDir '' -TicketId 'PROJ-1' -ProjectKey 'alpha' 2>$null
Check 'no store: the fact is not queued' (Test-Path (Join-Path $proj 'improvement-suggestions.md')) $false
Check 'no store: its key is handed back' ((@(Get-Content (Join-Path $proj 'reflection-keys.txt') | Where-Object { $_ })).Count) 0

# --- A bug seen in passing is a Backlog ticket, never a note and never a queued suggestion ---
$defect = '[{"kind":"defect","key":"invoice-total-drops-tax","text":"InvoiceTotals.Sum in billing/totals.ts adds line items but drops the tax line, so every invoice total is short by its tax.","confidence":"high"}]'
$script:filed.Clear()
$r = Invoke-Case -Name 'defect' -Reply $defect
Check 'defect: reported as a ticket' $r.last 'Reflection: 0 written to notes, 0 folded into existing notes, 0 filed for review, 1 filed as tickets, 0 already known, 0 dropped as guesses'
Check 'defect: the board was asked once, for this project' "$($script:filed.Count) $($script:filed[0].Project)" '1 alpha'
Check 'defect: the ticket says which run found it' $script:filed[0].FoundOn 'PROJ-defect'
Check 'defect: no note was written' (Test-Path (Join-Path $r.mem 'invoice-total-drops-tax.md')) $false
Check 'defect: nothing was queued' (Test-Path (Join-Path $r.proj 'improvement-suggestions.md')) $false
Check 'defect: its key is claimed' ((Get-Content (Join-Path $r.proj 'reflection-keys.txt')) -join ',') 'invoice-total-drops-tax'

# The board cannot be reached: still not a note, and the key is handed back for a later run.
$script:boardDown = $true
$r = Invoke-Case -Name 'defect-down' -Reply $defect
$script:boardDown = $false
Check 'defect, board down: no note' (Test-Path (Join-Path $r.mem 'invoice-total-drops-tax.md')) $false
Check 'defect, board down: not queued' (Test-Path (Join-Path $r.proj 'improvement-suggestions.md')) $false
Check 'defect, board down: key left unclaimed' ((@(Get-Content (Join-Path $r.proj 'reflection-keys.txt') | Where-Object { $_ })).Count) 0

# --- A fact about one ticket is tagged with it, and retired when that ticket is done ---
# The board, as the maintenance pass asks it: ticket 42 is done, 43 is in QA, 44 is gone, and 45
# cannot be asked. Every case below runs through Invoke-Reflection, which runs the pass.
$script:phases = @{ '42' = 'Done'; '43' = 'NeedsQa'; '44' = 'Gone' }
$script:TicketPhaseOf = { param($id) if ($script:phases.ContainsKey("$id")) { $script:phases["$id"] } else { $null } }

$scoped = '[{"kind":"codebase","key":"migration-clash-0137","text":"Migration 0137 is taken by this branch; the other open ticket must renumber.","confidence":"high","ticket":true}]'
$r = Invoke-Case -Name 'scoped-flag' -Reply $scoped -TicketId '43'
Check 'ticket fact, flagged: tagged with its ticket' (Get-NoteTicket -Path (Join-Path $r.mem 'migration-clash-0137.md')) '43'

$named = '[{"kind":"codebase","key":"seeded-row","text":"Ticket #43 seeded an extra advisor row into the local database for its QA pass.","confidence":"high"}]'
$r = Invoke-Case -Name 'scoped-named' -Reply $named -TicketId '43'
Check 'ticket fact, named in the text: tagged anyway' (Get-NoteTicket -Path (Join-Path $r.mem 'seeded-row.md')) '43'

$byKey = '[{"kind":"codebase","key":"issue-fixture","text":"PROJ-77 needs the fixture user to be an admin.","confidence":"high"}]'
$r = Invoke-Case -Name 'scoped-issue' -Reply $byKey -TicketId '43' -IssueKey 'PROJ-77'
Check 'ticket fact, naming its tracker key: tagged' (Get-NoteTicket -Path (Join-Path $r.mem 'issue-fixture.md')) '43'

$r = Invoke-Case -Name 'durable' -Reply $fact -TicketId '43'
Check 'a durable fact is not tagged' "[$(Get-NoteTicket -Path (Join-Path $r.mem 'migrations-need-bom.md'))]" '[]'
Check 'a mention of #430 is not ticket 43' (Test-FindingNamesTicket -Text 'See #430 for the port.' -TicketId '43') $false

# Retiring: done and gone go, open and unknown stay, and the index loses their lines.
$emem = Join-Path $root 'expire\memory'
New-Item -ItemType Directory -Path $emem -Force | Out-Null
Set-Content -Path (Join-Path $emem 'MEMORY.md') -Value '# Notes' -Encoding ascii
foreach ($t in '42', '43', '44', '45') {
    [void](Write-MemoryNote -MemoryDir $emem -Key "about-$t" -Text "Something only ticket $t needs to know." -Stamp '2026-11-01 10:00' -Ticket $t)
}
[void](Write-MemoryNote -MemoryDir $emem -Key 'durable-port' -Text 'The API listens on port 5100 locally.' -Stamp '2026-11-01 10:00')
$gone = @(Remove-FinishedTicketNotes -MemoryDir $emem -PhaseOf $script:TicketPhaseOf | Sort-Object)
Check 'retire: the done and the gone ticket notes' ($gone -join ',') 'about-42,about-44'
Check 'retire: open, unknown and durable notes stay' ((@(Get-ChildItem $emem -Filter 'about-*.md' | ForEach-Object BaseName) + @(Get-ChildItem $emem -Filter 'durable-*.md' | ForEach-Object BaseName) | Sort-Object) -join ',') 'about-43,about-45,durable-port'
$idx = [IO.File]::ReadAllText((Join-Path $emem 'MEMORY.md'))
Check 'retire: the index lost their lines' (($idx -match 'about-42') -or ($idx -match 'about-44')) $false
Check 'retire: the index kept the rest' (($idx -match 'about-43') -and ($idx -match 'durable-port')) $true

# The pass runs it on every reflection, however small the index.
$r = Invoke-Case -Name 'scoped-done' -Reply $scoped -TicketId '42'
Check 'a fact about a ticket already done is retired by the same pass' (Test-Path (Join-Path $r.mem 'migration-clash-0137.md')) $false

# A durable finding is never folded into a ticket's note: those are not candidates.
$fmem = Join-Path $root 'fold\memory'
New-Item -ItemType Directory -Path $fmem -Force | Out-Null
[void](Write-MemoryNote -MemoryDir $fmem -Key 'clash-note' -Text 'The reporting migration numbering clashed between two open branches over number 0137 in the reporting schema.' -Stamp '2026-11-01 10:00' -Ticket '43')
[void](Write-MemoryNote -MemoryDir $fmem -Key 'other' -Text 'The emulator needs a cold boot after a host sleep.' -Stamp '2026-11-01 10:00')
[void](Write-MemoryNote -MemoryDir $fmem -Key 'third' -Text 'Docker compose down leaves the volume in place.' -Stamp '2026-11-01 10:00')
$q = 'Reporting migration numbering in the reporting schema is sequential.'
Check 'related: a ticket note is a candidate when asked for all' ((@(Find-RelatedNotes -MemoryDir $fmem -Text $q -Threshold 0.01) | ForEach-Object Key) -contains 'clash-note') $true
Check 'related: but not when asked for durable notes' ((@(Find-RelatedNotes -MemoryDir $fmem -Text $q -Threshold 0.01 -DurableOnly) | ForEach-Object Key) -contains 'clash-note') $false
$script:TicketPhaseOf = $null

# --- Reflection is shown the store's keys, not its index ---
$dir = Join-Path $root 'keys-only'; $proj = Join-Path $dir 'proj'; $repo = Join-Path $dir 'repo'; $mem = Join-Path $repo '.claude\memory'
New-Item -ItemType Directory -Path $proj, $mem -Force | Out-Null
Set-Content -Path (Join-Path $proj 'reflect-prompt.md') -Value 'Look.' -Encoding ascii
Set-Content -Path (Join-Path $mem 'MEMORY.md') -Value "# Notes`n`n- [Db](db-on-1433.md) - SUMMARY-ONLY-IN-THE-INDEX" -Encoding ascii
Set-Content -Path (Join-Path $mem 'db-on-1433.md') -Value "---`nname: db-on-1433`n---`n`nThe database listens on localhost,1433." -Encoding ascii
Set-Content -Path (Join-Path $dir 'reply.txt') -Value '[]' -Encoding ascii
$env:FAKE_REPLY = Join-Path $dir 'reply.txt'
$env:FAKE_PROMPT_OUT = Join-Path $dir 'prompt.txt'
Invoke-Reflection -ProjectDataDir $proj -RepoDir $repo -RunLog (1..10 | ForEach-Object { "line $_" }) -MemoryDir $mem -TicketId 'PROJ-1' -ProjectKey 'alpha' 2>$null
Remove-Item Env:FAKE_PROMPT_OUT
$sent = [IO.File]::ReadAllText((Join-Path $dir 'prompt.txt'))
Check 'reflection prompt: lists the known keys' ($sent -match '(?m)^- db-on-1433\s*$') $true
Check 'reflection prompt: does not carry the index' ($sent -match 'SUMMARY-ONLY-IN-THE-INDEX') $false

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

# ---- finding near-duplicate notes ----
# Two notes saying one thing are a pair; a note about something else pairs with neither. And it stays
# quick with many notes: the pairwise scoring once took hours on a store of ~580, holding a worker
# - and a fleet drain - long after its ticket was done.
$cmem = Join-Path $root 'clusters'
New-Item -ItemType Directory -Path $cmem -Force | Out-Null
function Note([string]$key, [string]$body) {
    [IO.File]::WriteAllText((Join-Path $cmem "$key.md"), "---`nname: $key`ndescription: `"$key`"`n---`n`n$body`n")
}
Note 'deploy-window' 'Deploys happen on tuesday mornings after the nightly backup finishes and the release captain signs off.'
Note 'deploy-window-again' 'Deploys happen on tuesday mornings after the nightly backup finishes and the release captain signs off quickly.'
Note 'stripe-mock-port' 'The local payment stand in listens on port twelve thousand one hundred eleven inside its container.'
$pairs = @(Get-NoteClusters -MemoryDir $cmem)
Check 'clusters: one pair found' $pairs.Count 1
Check 'clusters: it is the two deploy notes' ((@($pairs[0].A, $pairs[0].B) | Sort-Object) -join ',') 'deploy-window,deploy-window-again'

$words = 'alpha','bravo','charlie','delta','echo','foxtrot','golf','hotel','india','juliet','kilo','lima','mike','november','oscar','papa','quebec','romeo','sierra','tango','uniform','victor','whiskey','xray','yankee','zulu'
for ($i = 0; $i -lt 400; $i++) {
    $body = (0..30 | ForEach-Object { $words[($i * 7 + $_ * 13) % $words.Count] + "$($_ % 9)x$($i % 50)" }) -join ' '
    Note "many-$i" $body
}
$sw = [Diagnostics.Stopwatch]::StartNew()
[void](Get-NoteClusters -MemoryDir $cmem)
$sw.Stop()
Check 'clusters: 400 notes are scored in under a minute' ($sw.Elapsed.TotalSeconds -lt 60) $true

Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item Env:FAKE_REPLY, Env:FAKE_WARN -ErrorAction SilentlyContinue

Write-Host ""
if ($script:fail) { Write-Host "$($script:fail) of $($script:total) failed" -ForegroundColor Red; exit 1 }
Write-Host "all $($script:total) passed" -ForegroundColor Green
