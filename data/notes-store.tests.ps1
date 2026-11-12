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

Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($script:fail) { Write-Host "$($script:fail) of $($script:total) failed" -ForegroundColor Red; exit 1 }
Write-Host "all $($script:total) passed" -ForegroundColor Green
