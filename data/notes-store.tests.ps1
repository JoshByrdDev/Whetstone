# Exercises the notes store's own lifecycle in data/notes-store.ps1, with no agent at all: what a
# run is recorded as having used, the index held to its byte budget, which notes are promoted out
# of the store, the index's topic sections surviving every write, and the keys notes are named by.
#
# The reflection that feeds the store is worker-reflect.tests.ps1; this is everything after it.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/notes-store.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'

$script:log = New-Object System.Collections.Generic.List[string]
function Write-Log { param([string]$Message) $script:log.Add($Message) }

$common = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'heartbeat-common.ps1'), [ref]$null, [ref]$null)
foreach ($fn in $common.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if (@('Invoke-WithMutex', 'Add-Utf8Text') -contains $fn.Name) { . ([scriptblock]::Create($fn.Extent.Text)) }
}
. (Join-Path $PSScriptRoot 'notes-store.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-notes-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

# A fresh store and project folder. Notes are written the way reflection writes them.
function New-Store {
    param([string]$Name, [string]$Index = "# Memory index`n")
    $dir = Join-Path $root $Name
    $mem = Join-Path $dir 'memory'
    $proj = Join-Path $dir 'proj'
    New-Item -ItemType Directory -Path $mem, $proj -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $mem 'MEMORY.md'), $Index)
    return @{ mem = $mem; proj = $proj }
}
function Add-Note([string]$Mem, [string]$Key, [string]$Text, [string]$Stamp = '2026-01-01 10:00') {
    [void](Write-MemoryNote -MemoryDir $Mem -Key $Key -Text $Text -Stamp $Stamp)
}
function Index-Keys([string]$Mem) { (@(Get-IndexEntries -MemoryDir $Mem) | ForEach-Object Key) -join ',' }

$now = [datetime]'2026-11-11 12:00'

# ---- Which note a file tool was pointed at ----
$s = New-Store -Name 'paths'
Add-Note $s.mem 'db-port' 'The database listens on 1433.'
Check 'path: a note in the store' (Get-NoteKeyFromPath -Path (Join-Path $s.mem 'db-port.md') -MemoryDir $s.mem) 'db-port'
Check 'path: forward slashes and other case' (Get-NoteKeyFromPath -Path ((Join-Path $s.mem 'DB-PORT.md') -replace '\\', '/') -MemoryDir $s.mem) 'db-port'
$hub = Join-Path $root 'paths/repo/.claude/memory'
New-Item -ItemType Directory -Path $hub -Force | Out-Null
Add-Note $hub 'db-port' 'The database listens on 1433.'
Check 'path: the same store under a worktree' (Get-NoteKeyFromPath -Path 'C:/wt/alpha-42/.claude/memory/db-port.md' -MemoryDir $hub) 'db-port'
Check 'path: the index is not a note' "[$(Get-NoteKeyFromPath -Path (Join-Path $s.mem 'MEMORY.md') -MemoryDir $s.mem)]" '[]'
Check 'path: a repo file of the same name is not' "[$(Get-NoteKeyFromPath -Path 'C:/repo/docs/db-port.md' -MemoryDir $s.mem)]" '[]'
Check 'path: a note that does not exist is not' "[$(Get-NoteKeyFromPath -Path (Join-Path $s.mem 'nope.md') -MemoryDir $s.mem)]" '[]'

# ---- What runs used ----
$s = New-Store -Name 'usage'
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '1' -Phase 'implement' -Served @('a', 'b') -Opened @('a') -When $now.AddDays(-2)
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '2' -Phase 'qa-test' -Served @() -Opened @('a') -When $now.AddDays(-1)
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '3' -Phase 'implement' -Served @() -Opened @() -When $now.AddDays(-40)
$u = Get-NoteUsage -ProjectDataDir $s.proj -Days 30 -Now $now
Check 'usage: runs in the window' $u.Runs 2
Check 'usage: tracking began with the oldest line' $u.Since.ToString('yyyy-MM-dd') $now.AddDays(-40).ToString('yyyy-MM-dd')
Check 'usage: a note used by two runs, once both ways' $u.Notes['a'].Runs.Count 2
Check 'usage: opened by both' $u.Notes['a'].Opened.Count 2
Check 'usage: served to one' $u.Notes['a'].Served.Count 1
Check 'usage: by phase' "$($u.Notes['a'].Phases['implement']) $($u.Notes['a'].Phases['qa-test'])" '1 1'
Check 'usage: last used' $u.Notes['a'].Last.ToString('yyyy-MM-dd') $now.AddDays(-1).ToString('yyyy-MM-dd')
Check 'usage: a note called "keys" does not break it' (& {
    Add-NoteUsage -ProjectDataDir $s.proj -TicketId '4' -Phase 'implement' -Opened @('keys', 'count') -When $now
    (Get-NoteUsage -ProjectDataDir $s.proj -Days 30 -Now $now).Notes['keys'].Runs.Count }) 1

# ---- The index held to its budget ----
# Twelve notes of about 120 bytes of index each, learned long ago. Some runs used some of them.
$s = New-Store -Name 'budget'
for ($i = 1; $i -le 12; $i++) {
    Add-Note $s.mem ("note-{0:D2}" -f $i) ("Fact number $i about the build, long enough to give its index line some weight in bytes, padded out to be sure.")
}
$size = (Get-Item (Join-Path $s.mem 'MEMORY.md')).Length
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '9' -Phase 'implement' -Opened @('note-01') -When $now.AddDays(-60)
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '9' -Phase 'implement' -Opened @('note-02', 'note-03') -When $now.AddDays(-5)

$gone = @(Invoke-NoteIndexBudget -MemoryDir $s.mem -ProjectDataDir $s.proj -BudgetBytes ($size + 10) -Now $now)
Check 'budget: under it, nothing goes' $gone.Count 0

$gone = @(Invoke-NoteIndexBudget -MemoryDir $s.mem -ProjectDataDir $s.proj -BudgetBytes ([int]($size / 2)) -Now $now)
$left = (Get-Item (Join-Path $s.mem 'MEMORY.md')).Length
Check 'budget: over it, the index is brought under' ($left -le [int]($size / 2)) $true
Check 'budget: recently used notes stay' ((@('note-02', 'note-03') | Where-Object { $gone -contains $_ }).Count) 0
Check 'budget: an evicted note is gone from disk' (Test-Path (Join-Path $s.mem "$($gone[0]).md")) $false
Check 'budget: and from the index' ((Index-Keys $s.mem) -split ',' -contains $gone[0]) $false
Check 'budget: the least recently used goes first' ($gone -contains 'note-01' -or (Test-Path (Join-Path $s.mem 'note-01.md'))) $true

# A store with no usage log yet: tracking starts now, so nothing is old enough to go.
$s = New-Store -Name 'budget-fresh'
for ($i = 1; $i -le 6; $i++) { Add-Note $s.mem "fresh-$i" "Fact $i, written long before anyone counted reads, and padded out a little." }
$gone = @(Invoke-NoteIndexBudget -MemoryDir $s.mem -ProjectDataDir $s.proj -BudgetBytes 50 -Now $now)
Check 'budget: no reads on record yet, nothing is evicted' $gone.Count 0
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '1' -Phase 'implement' -When $now.AddDays(-30)
$gone = @(Invoke-NoteIndexBudget -MemoryDir $s.mem -ProjectDataDir $s.proj -BudgetBytes 50 -Now $now)
Check 'budget: thirty days of tracking with no reads, they go' ($gone.Count -gt 0) $true

# A line a person placed under a topic goes only after every eligible machine line has gone.
$idx = "# Memory index`n`n## Build`n`n- [Placed](placed.md) - a person put this here`n"
$s = New-Store -Name 'budget-human' -Index $idx
[IO.File]::WriteAllText((Join-Path $s.mem 'placed.md'), "---`nname: placed`nmetadata:`n  learned: 2026-01-01 10:00`n---`n`nPlaced by hand.`n")
Add-Note $s.mem 'machine-a' 'A machine-written fact that nobody has read in a long time, padded a little.'
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '1' -Phase 'implement' -When $now.AddDays(-90)
$oneLine = [Text.Encoding]::UTF8.GetByteCount((@(Read-NoteLines (Join-Path $s.mem 'MEMORY.md')) | Where-Object { $_ -match 'machine-a' })) + 1
$budget = (Get-Item (Join-Path $s.mem 'MEMORY.md')).Length - $oneLine
$gone = @(Invoke-NoteIndexBudget -MemoryDir $s.mem -ProjectDataDir $s.proj -BudgetBytes $budget -Now $now)
Check 'budget: the machine line goes before the placed one' ($gone -join ',') 'machine-a'
Check 'budget: the placed note is still there' (Test-Path (Join-Path $s.mem 'placed.md')) $true

# The maintenance pass evicts on its own, with no agent to merge with.
$s = New-Store -Name 'maint'
for ($i = 1; $i -le 8; $i++) { Add-Note $s.mem "old-$i" "An old fact numbered $i that no run has needed in months, padded out a bit." }
Add-NoteUsage -ProjectDataDir $s.proj -TicketId '1' -Phase 'implement' -When (Get-Date).AddDays(-60)
$script:log.Clear()
Invoke-NoteStoreMaintenance -MemoryDir $s.mem -ProjectDataDir $s.proj -BudgetBytes 300
Check 'maintenance: the index is brought under budget' ((Get-Item (Join-Path $s.mem 'MEMORY.md')).Length -le 300) $true
Check 'maintenance: it files nothing for a human' (Test-Path (Join-Path $s.proj 'improvement-suggestions.md')) $false
Check 'maintenance: it says what it did' ([bool]($script:log | Where-Object { $_ -like '*evicted*' })) $true

# ---- Note keys ----
# The key that started this: two keys run together with a space, cut mid-word at 56 characters.
$joined = 'alpha-credit-one-row alpha-dog-created-at-activation dedupe'
Check 'key: a space is a hyphen, and it is cut at one' (ConvertTo-NoteKey -Text $joined -Max 56) 'alpha-credit-one-row-alpha-dog-created-at-activation'
Check 'key: punctuation and case' (ConvertTo-NoteKey -Text '  The DB: port 1433 / "local" ') 'the-db-port-1433-local'
Check 'key: nothing usable is nothing' "[$(ConvertTo-NoteKey -Text ' -- ')]" '[]'
Check 'key: never longer than asked' ((ConvertTo-NoteKey -Text ('word-' * 40)).Length -le 60) $true
$s = New-Store -Name 'keys'
$r = Add-LearnedFact -MemoryDir $s.mem -Key $joined -Text 'A fact a steward handed over under two keys at once.' -Stamp '2026-11-11 12:00'
Check 'key: a steward''s spaced key is a clean file name' "$($r.Outcome) $($r.Key)" 'new alpha-credit-one-row-alpha-dog-created-at-activation'
Check 'key: no file name with a space was written' (@(Get-ChildItem $s.mem -Filter '* *').Count) 0
[void](Write-MemoryNote -MemoryDir $s.mem -Key 'Bad Key Here' -Text 'Written straight, bypassing the callers.' -Stamp '2026-11-11 12:00')
Check 'key: Write-MemoryNote normalises a key it is handed raw' (Test-Path (Join-Path $s.mem 'bad-key-here.md')) $true
Check 'key: and indexes it by that name' ((Index-Keys $s.mem) -split ',' -contains 'bad-key-here') $true

# ---- The index's topic sections survive every write ----
# A person has grouped the index under topics; the machine's section is at the bottom.
$topics = @"
# Memory index

## Build

- [Build Cache](build-cache.md) - hand-written summary
  - [Nested Port](./nested-port.md) - a person's own link style

## Unsorted (written by reflection)

- [Machine A](machine-a.md) - machine line

## Release

- [Release Day](release-day.md) - tuesdays
"@
$s = New-Store -Name 'sections' -Index $topics
foreach ($k in 'build-cache', 'nested-port', 'machine-a', 'release-day') {
    [IO.File]::WriteAllText((Join-Path $s.mem "$k.md"), "---`nname: $k`ndescription: `"$k`"`nmetadata:`n  learned: 2026-01-01 10:00`n---`n`nThe $k fact, with enough words in it to stand as a note of its own for a merge.`n")
}
function Section-Of([string]$Mem, [string]$Key) { (@(Get-IndexEntries -MemoryDir $Mem) | Where-Object Key -eq $Key | Select-Object -First 1).Section }
function Lines-For([string]$Mem, [string]$Key) { @(Read-NoteLines (Join-Path $Mem 'MEMORY.md') | Where-Object { $_ -match (Get-IndexLinkPattern -Key $Key) }).Count }

Add-IndexLine -MemoryDir $s.mem -Key 'build-cache' -Title 'Build Cache' -Summary 'rewritten by a fold'
Check 'sections: a rewritten line stays under its topic' (Section-Of $s.mem 'build-cache') '## Build'
Check 'sections: and its summary moves with the note' ([bool](Read-NoteLines (Join-Path $s.mem 'MEMORY.md') | Where-Object { $_ -match 'rewritten by a fold' })) $true
Add-IndexLine -MemoryDir $s.mem -Key 'nested-port' -Title 'Nested Port' -Summary 'rewritten'
Check 'sections: a ./ link is the same note - no second line' (Lines-For $s.mem 'nested-port') 1
Check 'sections: and keeps its indentation' ([bool](Read-NoteLines (Join-Path $s.mem 'MEMORY.md') | Where-Object { $_ -like '  - `[Nested Port`](nested-port.md)*' })) $true
Add-Note $s.mem 'brand-new' 'A brand new fact written by reflection after a run.'
Check 'sections: a new line goes to Unsorted' (Section-Of $s.mem 'brand-new') '## Unsorted (written by reflection)'
Check 'sections: the topic after Unsorted is untouched' (Section-Of $s.mem 'release-day') '## Release'
Remove-IndexLine -MemoryDir $s.mem -Key 'brand-new'
Check 'sections: removing a line leaves the others where they are' ((@('build-cache', 'nested-port', 'machine-a', 'release-day') | ForEach-Object { Section-Of $s.mem $_ }) -join '|') '## Build|## Build|## Unsorted (written by reflection)|## Release'

# A merge whose survivor was in Unsorted while the note it absorbed had been placed by a person:
# the merged note takes the person's place.
# A batch file on Windows; elsewhere the same stand-in as a shell script.
if ($IsWindows -or $PSVersionTable.PSEdition -ne 'Core') {
    $fake = Join-Path $root 'agent.cmd'
    Set-Content -Path $fake -Encoding ascii -Value "@echo off`r`nmore > nul`r`ntype `"%FAKE_REPLY%`"`r`n"
}
else {
    $fake = Join-Path $root 'agent'
    [IO.File]::WriteAllText($fake, "#!/bin/sh`ncat > /dev/null`ncat `"`$FAKE_REPLY`"`n")
    & chmod +x $fake
}
$cmd = [pscustomobject]@{ Exe = $fake; Args = @() }
$env:FAKE_REPLY = Join-Path $root 'merge-reply.txt'
[IO.File]::WriteAllText($env:FAKE_REPLY, "KEEP: machine-a`n---`nname: machine-a`ndescription: `"merged machine-a and release-day`"`nmetadata:`n  last-confirmed: 2026-11-11 12:00`n---`n`nThe machine-a fact and the release-day fact, merged into one statement with every detail of both kept.`n")
$m = Merge-NotePair -MemoryDir $s.mem -KeyA 'machine-a' -KeyB 'release-day' -Stamp '2026-11-11 12:00' -ReflectCmd $cmd -RepoDir $root
Check 'merge: it merged' "$($m.Kept)<-$($m.Dropped)" 'machine-a<-release-day'
Check 'merge: the survivor takes the placed line''s topic' (Section-Of $s.mem 'machine-a') '## Release'
Check 'merge: one line for the survivor' (Lines-For $s.mem 'machine-a') 1
Check 'merge: none for the absorbed note' (Lines-For $s.mem 'release-day') 0
# And the other way: a survivor already under a topic stays there.
[IO.File]::WriteAllText($env:FAKE_REPLY, "KEEP: build-cache`n---`nname: build-cache`ndescription: `"merged`"`nmetadata:`n  last-confirmed: 2026-11-11 12:00`n---`n`nThe build-cache fact and the machine-a fact, merged into one statement with every detail of both kept here.`n")
$m = Merge-NotePair -MemoryDir $s.mem -KeyA 'build-cache' -KeyB 'machine-a' -Stamp '2026-11-11 12:00' -ReflectCmd $cmd -RepoDir $root
Check 'merge: a placed survivor stays under its own topic' (Section-Of $s.mem 'build-cache') '## Build'
Check 'merge: and the absorbed line is gone' (Lines-For $s.mem 'machine-a') 0
Remove-Item Env:FAKE_REPLY

# ---- Promoting what most runs look up ----
$s = New-Store -Name 'promote'
Add-Note $s.mem 'flag-env-var' 'The feature is off unless FEATURE_X_ON=1 is set in the API environment.'
Add-Note $s.mem 'rare-fact' 'The legacy export runs only on the first of the month.'
Add-Note $s.mem 'served-a-lot' 'A note whose words are common enough to be served to many tickets.'
[void](Write-MemoryNote -MemoryDir $s.mem -Key 'ticket-only' -Text 'Only ticket 5 needs this.' -Stamp '2026-11-01 10:00' -Ticket '5')
for ($i = 1; $i -le 60; $i++) {
    $opened = @()
    if ($i -le 10) { $opened += 'flag-env-var' }
    if ($i -le 10) { $opened += 'ticket-only' }
    if ($i -eq 1)  { $opened += 'rare-fact' }
    $phase = if ($i -le 7) { 'implement' } elseif ($i -le 9) { 'qa-test' } else { 'refine' }
    Add-NoteUsage -ProjectDataDir $s.proj -TicketId "$i" -Phase $phase -Served @('served-a-lot') -Opened $opened -When $now.AddDays(-($i % 20))
}
$p = @(Get-NotePromotions -ProjectDataDir $s.proj -MemoryDir $s.mem -Now $now)
Check 'promote: the note most runs open, and only it' (($p | ForEach-Object Key) -join ',') 'flag-env-var'
Check 'promote: counted out of every run' "$($p[0].Opened)/$($p[0].Total)" '10/60'
Check 'promote: too few runs, nothing yet' (@(Get-NotePromotions -ProjectDataDir $s.proj -MemoryDir $s.mem -MinRuns 100 -Now $now).Count) 0
$f = @(Add-NotePromotionSuggestions -ProjectDataDir $s.proj -MemoryDir $s.mem -Now $now)
$q = [IO.File]::ReadAllText((Join-Path $s.proj 'improvement-suggestions.md'))
Check 'promote: filed once as a process suggestion' ($q -match '- \[ \] \[process\] Note `flag-env-var` was opened by 10 of 60 runs') $true
Check 'promote: it names the skill that reads it most' ($q -match 'skills/implement/skill.md` \(7 runs\)') $true
Check 'promote: and not one that barely does' ($q -match 'skills/refine') $false
Check 'promote: it offers project.json for a setting' ($q -match 'project.json') $true
$f = @(Add-NotePromotionSuggestions -ProjectDataDir $s.proj -MemoryDir $s.mem -Now $now)
Check 'promote: never filed twice, even once deleted from the queue' $f.Count 0

# ---- The notes a run is handed ----
$s = New-Store -Name 'relevant'
Add-Note $s.mem 'invoice-rounding' 'Invoice totals are rounded per line in billing/totals.ts, never on the sum; the tax line is computed from the rounded lines.'
Add-Note $s.mem 'invoice-pdf-fonts' 'The invoice PDF renderer needs the bundled fonts copied into public/fonts before it can draw totals.'
Add-Note $s.mem 'emulator-cold-boot' 'The phone emulator needs a cold boot after the host sleeps or it never reaches the dev server.'
Add-Note $s.mem 'compose-volumes' 'docker compose down keeps the database volume; add -v to start from an empty database.'
Add-Note $s.mem 'seed-takes-minutes' 'The seed script takes about four minutes on a fresh database and prints nothing while it works.'
[void](Write-MemoryNote -MemoryDir $s.mem -Key 'ours-migration' -Text 'Migration 0137 is this branch''s.' -Stamp '2026-11-01 10:00' -Ticket '42')
[void](Write-MemoryNote -MemoryDir $s.mem -Key 'theirs-invoice' -Text 'Invoice totals for the other branch are rounded on the sum in billing/totals.ts while it is open.' -Stamp '2026-11-01 10:00' -Ticket '43')
$ticket = "Invoice totals are off by a cent`nThe invoice total in billing/totals.ts disagrees with the sum of its rounded lines when tax applies."
$r = Get-RelevantNotes -MemoryDir $s.mem -Text $ticket -TicketId '42'
Check 'relevant: this ticket''s own note comes first' $r.Keys[0] 'ours-migration'
Check 'relevant: the note about its subject is included' ($r.Keys -contains 'invoice-rounding') $true
Check 'relevant: another ticket''s note never is' ($r.Keys -contains 'theirs-invoice') $false
Check 'relevant: a note about something else is not' ($r.Keys -contains 'emulator-cold-boot') $false
Check 'relevant: the text is the notes themselves' ($r.Text -match 'rounded per line in billing/totals.ts') $true
Check 'relevant: and carries no frontmatter' ($r.Text -match 'node_type') $false
$r = Get-RelevantNotes -MemoryDir $s.mem -Text $ticket -TicketId '42' -Top 1
Check 'relevant: no more than asked for' "$($r.Keys.Count) $($r.Keys[0])" '1 ours-migration'
$r = Get-RelevantNotes -MemoryDir $s.mem -Text $ticket -TicketId '7' -PerNoteChars 30
Check 'relevant: a long note is cut and says where the rest is' ($r.Text -match 'the rest is in invoice-rounding.md') $true
$r = Get-RelevantNotes -MemoryDir (Join-Path $root 'no-such-store') -Text $ticket -TicketId '42'
Check 'relevant: no store, no notes' "$($r.Keys.Count)[$($r.Text)]" '0[]'

Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($script:fail) { Write-Host "$($script:fail) of $($script:total) failed" -ForegroundColor Red; exit 1 }
Write-Host "all $($script:total) passed" -ForegroundColor Green
