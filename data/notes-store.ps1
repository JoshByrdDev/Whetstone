# Notes store: the lifecycle of a project's durable facts.
#
# The store is one file per fact plus a MEMORY.md index, living in the target repo at .claude/memory
# and read by three different readers - every heartbeat worker, every reflection, and every
# interactive Claude session in that repo. That triple duty is why the rules here are stricter than
# "append and hope": a bad line in the index is read thousands of times before anyone notices it.
#
# Reflection used to have exactly two outcomes, write a new note or file for a human, and that is
# what this file exists to fix. A fact that refines something already recorded had nowhere to go, so
# it either became a near-duplicate note under a slightly different key or it sat in a review queue.
# One subject ended up smeared across five notes and three queued items. The third outcome - fold it
# into the note that already covers it - is what Find-RelatedNotes and Resolve-FactPlacement add.
#
# Every mutating function here commits the store first. The store is gitignored by the parent repo
# (a managed repo's own .gitignore may well have `**/.claude`), so before this it had no history at all: an automated
# rewrite of a hand-written note was unrecoverable. The nested repo is invisible to the parent for
# the same reason the store is - the parent already ignores the whole .claude tree.

Set-StrictMode -Off

# ---- Reading text without destroying it -------------------------------------------------------

# Every read in this file goes through these two, and nothing here calls Get-Content.
#
# PowerShell 5.1's Get-Content decodes using the ANSI codepage (cp1252 here), not UTF-8. Notes and
# the index are UTF-8 and full of em dashes and arrows, so Get-Content turns one of those into two
# or three cp1252 characters - and because everything in this file writes back as UTF-8, those get
# re-encoded and the mojibake is now real bytes on disk. Round-tripping one project's index twice this
# way replaced every em dash with a seven-character run of accented Latin-1 and grew an 18 KB file
# to 23 KB - 5 KB of garbage, read on every worker run, every reflection and every interactive
# session. It is silent, it compounds on each pass, and only a byte count gives it away.
#
# The store is BOM-less UTF-8 by construction, but a hand-edited file may arrive with a BOM, so
# detectEncodingFromByteOrderMarks stays on: it only overrides for a file that declares itself.
function Read-NoteText {
    param([string]$Path)
    try { return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) }
    catch { return "" }
}

function Read-NoteLines {
    param([string]$Path)
    try { return @([System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)) }
    catch { return @() }
}

# ---- Git safety ------------------------------------------------------------------------------

# Words that carry no signal about WHICH fact this is. Kept deliberately short: the scoring below
# already discounts common tokens by how many notes contain them, so this list only needs to catch
# the ones that would otherwise inflate a match on pure grammar.
$script:NoteStopWords = @(
    'this','that','then','than','with','from','into','when','what','which','while','where','because',
    'have','has','had','was','were','been','being','are','the','and','for','not','but','its','it',
    'a','an','of','to','in','on','at','by','is','be','as','or','if','so','no','do','does','did',
    'will','would','should','could','must','can','may','also','only','just','same','other','after',
    'before','over','under','both','each','more','most','some','any','all','run','runs','ran','use',
    'used','uses','using','make','makes','made','get','gets','got','set','sets','one','two','way'
) | ForEach-Object { $_.ToLowerInvariant() }

# Give the store its own history. The parent repo ignores .claude entirely, so a nested repo here is
# invisible to it - no submodule, no pollution of the parent's status, and `git -C` never walks up.
function Initialize-NoteStoreGit {
    param([string]$MemoryDir)

    if (-not $MemoryDir -or -not (Test-Path $MemoryDir)) { return $false }
    $gitDir = Join-Path $MemoryDir ".git"
    if (Test-Path $gitDir) { return $true }
    try {
        & git -C $MemoryDir init --quiet 2>&1 | Out-Null
        # An identity, because a machine commit with no configured user aborts rather than defaulting.
        & git -C $MemoryDir config user.name  "whetstone-reflection" 2>&1 | Out-Null
        & git -C $MemoryDir config user.email "reflection@whetstone.local" 2>&1 | Out-Null
        & git -C $MemoryDir add -A 2>&1 | Out-Null
        & git -C $MemoryDir commit --quiet -m "Baseline: notes store before automated maintenance" 2>&1 | Out-Null
        return $true
    }
    catch { return $false }
}

# Commit whatever is currently on disk, so the NEXT mutation has something to be reverted to. Called
# before a write rather than after: what needs preserving is the state a rewrite is about to replace.
# Silent and best-effort by design - a store that cannot be committed is still a store worth writing.
function Save-NoteStoreSnapshot {
    param([string]$MemoryDir, [string]$Message)

    if (-not $MemoryDir -or -not (Test-Path (Join-Path $MemoryDir ".git"))) { return }
    try {
        & git -C $MemoryDir add -A 2>&1 | Out-Null
        $status = & git -C $MemoryDir status --porcelain 2>&1
        if ($status) {
            & git -C $MemoryDir commit --quiet -m $Message 2>&1 | Out-Null
        }
    }
    catch { }
}

# ---- Reading a note --------------------------------------------------------------------------

# The prose only. Frontmatter is metadata the matcher must not score against: every note carries
# `node_type: memory` and `type: project`, so including it makes every note look alike.
function Get-NoteBody {
    param([string]$Path)
    $raw = Read-NoteText -Path $Path
    if (-not $raw) { return "" }
    if ($raw.StartsWith("---")) {
        $end = $raw.IndexOf("`n---", 3)
        if ($end -ge 0) {
            $nl = $raw.IndexOf("`n", $end + 1)
            if ($nl -ge 0) { return $raw.Substring($nl + 1) }
        }
    }
    return $raw
}

# Distinctive tokens: identifiers, endpoint fragments, file names, error codes. Three characters is
# the floor, low enough to keep "500" and "cte" and high enough to drop most grammar; the stop list
# and the frequency weighting below handle the rest. Punctuation is a separator, so
# `InvestmentDataService.cs:5872` yields the class, the extension and the line - any one of which is
# enough to recognise the same subject written up a different way.
function Get-NoteTokens {
    param([string]$Text)

    $set = New-Object System.Collections.Generic.HashSet[string]
    if (-not $Text) { return $set }
    $lower = $Text.ToLowerInvariant()
    foreach ($m in [regex]::Matches($lower, '[a-z0-9_]{3,}')) {
        $t = $m.Value.Trim('_')
        if ($t.Length -lt 3) { continue }
        if ($script:NoteStopWords -contains $t) { continue }
        [void]$set.Add($t)
    }
    return $set
}

# ---- Finding the note a fact belongs to -----------------------------------------------------

# The gap this closes: dedup used to compare kebab-case KEYS, so a fact already recorded under a
# different name was invisible. "advisor-product-profile-header-500" and the note that already
# documented that exact 500 ("model-allocation-qa-fixtures") share no key tokens at all, so the
# fact was filed for a human who then found it by hand.
#
# Scoring is TF-IDF cosine similarity over the store. Two details in that are load-bearing, and both
# were learned by watching a simpler version get it backwards on real data:
#
# Tokens the store has never seen are dropped from the finding's vector. Inverse document frequency
# gives an unseen token the HIGHEST weight of all, so a finding carrying a ticket number and three
# novel words had a vector dominated by terms no note could possibly match, and its true match
# scored near zero. An absent token says "this is new"; it says nothing about WHICH note is closest,
# which is the only question being asked here.
#
# Cosine rather than plain coverage, because coverage has no size penalty and the longest note in
# the store therefore wins almost everything - a 9 KB architecture note shares some vocabulary with
# any finding at all. Dividing by both vector norms is what makes a short, precisely-on-topic note
# beat a long one that merely contains the same words somewhere.
#
# Length normalisation is PIVOTED (the norm raised to 0.3) rather than full. Full cosine divides by
# the whole note norm, which on a store of one-sentence notes and 9 KB architecture notes crushes
# every long note to a score no threshold can reach; no normalisation at all hands every match to
# the longest note in the store. Measured on the real store, 0.3 is what puts the right answer top.
#
# This returns CANDIDATES, not a verdict, and that is the important part. Tuning a lexical threshold
# to decide "same fact or not" was tried and the numbers do not separate: the correct note scored
# 0.466 and a wrong one 0.419 on the same finding. So the matcher is deliberately recall-first, and
# the judgement is left to the model in Resolve-FactPlacement, which sees the actual prose and can
# answer NEW. Cheap and approximate here, expensive and accurate there.
function Find-RelatedNotes {
    param([string]$MemoryDir, [string]$Text, [double]$Threshold = 0.25, [int]$Top = 3)

    if (-not $MemoryDir -or -not (Test-Path $MemoryDir)) { return $null }
    $findingTokens = Get-NoteTokens -Text $Text
    if ($findingTokens.Count -lt 3) { return $null }

    $notes = @(Get-ChildItem -Path $MemoryDir -Filter *.md -File -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName.ToLowerInvariant() -ne 'memory' })
    if ($notes.Count -lt 2) { return $null }

    # One pass to read every note and count how many contain each token. The key joins the body for
    # matching purposes: a note named for its subject should match a finding about that subject even
    # when the prose words it differently.
    $noteTokens = @{}
    $docFreq    = @{}
    foreach ($n in $notes) {
        $tokens = Get-NoteTokens -Text ((Get-NoteBody -Path $n.FullName) + " " + ($n.BaseName -replace '-', ' '))
        $noteTokens[$n.FullName] = $tokens
        foreach ($t in $tokens) {
            if ($docFreq.ContainsKey($t)) { $docFreq[$t] = $docFreq[$t] + 1 } else { $docFreq[$t] = 1 }
        }
    }

    # NEVER read .Keys or .Count off these hashtables. PowerShell surfaces a hashtable's own keys as
    # properties, and this store contains the tokens "keys" and "count", so `$docFreq.Keys` returns
    # the integer filed under the key "keys" and `$docFreq.Count` returns the one under "count". The
    # first version of this function built its weights by iterating `$docFreq.Keys`, got a single
    # integer instead of four thousand tokens, and scored every note zero. Weights are therefore
    # derived on demand from the indexer, which is not shadowable.
    $total = [double]$notes.Count
    function Get-Idf([string]$t) {
        if (-not $docFreq.ContainsKey($t)) { return 0.0 }
        return [Math]::Log($total / [double]$docFreq[$t]) + 1.0
    }

    # The finding's vector, over store vocabulary only - see the note above on unseen tokens.
    $queryNorm = 0.0
    $query = New-Object System.Collections.Generic.List[string]
    foreach ($t in $findingTokens) {
        $w = Get-Idf $t
        if ($w -gt 0) { [void]$query.Add($t); $queryNorm += ($w * $w) }
    }
    if ($query.Count -lt 2) { return @() }
    $queryNorm = [Math]::Sqrt($queryNorm)

    $scored = @()
    foreach ($n in $notes) {
        $tokens = $noteTokens[$n.FullName]
        $noteNorm = 0.0
        foreach ($t in $tokens) { $w = Get-Idf $t; $noteNorm += ($w * $w) }
        if ($noteNorm -le 0) { continue }

        $dot = 0.0
        foreach ($t in $query) { if ($tokens.Contains($t)) { $w = Get-Idf $t; $dot += ($w * $w) } }
        if ($dot -le 0) { continue }

        $score = $dot / ($queryNorm * [Math]::Pow($noteNorm, 0.3))
        if ($score -ge $Threshold) {
            $scored += [pscustomobject]@{
                Path  = $n.FullName
                Key   = $n.BaseName
                Score = [Math]::Round($score, 3)
            }
        }
    }

    return @($scored | Sort-Object Score -Descending | Select-Object -First $Top)
}

# ---- Index maintenance ----------------------------------------------------------------------

# Cut at a word boundary. The old truncation sliced mid-word and appended an ellipsis, which is how
# the index came to read "ADO.NET rejec..." - a line that costs context on every single run and
# tells the reader nothing it would not have told them three words earlier.
function Get-NoteSummary {
    param([string]$Text, [int]$Max = 140)

    $s = ($Text -replace '\s+', ' ').Trim()
    $dot = $s.IndexOf('. ')
    if ($dot -gt 20 -and $dot -lt $Max) { $s = $s.Substring(0, $dot) }
    if ($s.Length -gt $Max) {
        $cut = $s.Substring(0, $Max)
        $sp = $cut.LastIndexOf(' ')
        if ($sp -gt 40) { $cut = $cut.Substring(0, $sp) }
        $s = $cut.TrimEnd(',', ';', ':', '-') + "..."
    }
    return ($s -replace '"', "'")
}

# The index is a curated document with `##` sections a human maintains. Appending to the end of the
# file put every machine line BELOW the last section heading, so they read as though they belonged
# to "## QA" and the structure quietly stopped meaning anything. Machine lines get their own section
# at the bottom, created on demand, and a human moving a line up into a real section is then a
# one-line edit that this function will not undo.
$script:MachineSection = "## Unsorted (written by reflection)"

function Add-IndexLine {
    param([string]$MemoryDir, [string]$Key, [string]$Title, [string]$Summary)

    $index = Join-Path $MemoryDir "MEMORY.md"
    $line  = "- [$Title]($Key.md) - $Summary"

    Invoke-WithMutex -Name "notes-index" -TimeoutMs 30000 -Body {
        if (-not (Test-Path $index)) {
            Add-Utf8Text -Path $index -Text "# Memory index`n`n"
        }
        $lines = @(Read-NoteLines -Path $index)

        # Already indexed under this key: replace in place rather than adding a second line. A note
        # that gets rewritten needs its one-line summary to move with it.
        $existing = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match [regex]::Escape("($Key.md)")) { $existing = $i; break }
        }
        if ($existing -ge 0) {
            $lines[$existing] = $line
            [System.IO.File]::WriteAllLines($index, $lines, (New-Object System.Text.UTF8Encoding($false)))
            return
        }

        $sectionAt = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq $script:MachineSection) { $sectionAt = $i; break }
        }
        if ($sectionAt -lt 0) {
            $out = @($lines) + @("", $script:MachineSection, "", $line)
        }
        else {
            # Insert at the end of that section (before the next `## ` heading, or EOF).
            $insertAt = $lines.Count
            for ($i = $sectionAt + 1; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^##\s') { $insertAt = $i; break }
            }
            while ($insertAt -gt $sectionAt + 1 -and -not $lines[$insertAt - 1].Trim()) { $insertAt-- }
            $out = @($lines[0..($insertAt - 1)]) + @($line)
            if ($insertAt -lt $lines.Count) { $out += $lines[$insertAt..($lines.Count - 1)] }
        }
        [System.IO.File]::WriteAllLines($index, $out, (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Remove-IndexLine {
    param([string]$MemoryDir, [string]$Key)

    $index = Join-Path $MemoryDir "MEMORY.md"
    if (-not (Test-Path $index)) { return }
    Invoke-WithMutex -Name "notes-index" -TimeoutMs 30000 -Body {
        $lines = @(Read-NoteLines -Path $index |
            Where-Object { $_ -notmatch [regex]::Escape("($Key.md)") })
        [System.IO.File]::WriteAllLines($index, $lines, (New-Object System.Text.UTF8Encoding($false)))
    }
}

# ---- Writing a note --------------------------------------------------------------------------

function Get-NoteTitle {
    param([string]$Key)
    return (($Key -split '-') | Where-Object { $_ } |
        ForEach-Object { $_.Substring(0,1).ToUpperInvariant() + $_.Substring(1) }) -join ' '
}

# A brand new fact, in the same one-file-per-fact shape an interactive session writes.
#
# The store lives in the HUB repo, never the worker's worktree: a worktree belongs to one ticket and
# is thrown away with it, so a fact written there would be learned and lost in the same cycle.
function Write-MemoryNote {
    param([string]$MemoryDir, [string]$Key, [string]$Text, [string]$Stamp, [string]$Source = 'heartbeat-reflection')

    if (-not $MemoryDir) { return $false }
    try {
        if (-not (Test-Path $MemoryDir)) { New-Item -ItemType Directory -Path $MemoryDir -Force | Out-Null }
        $file = Join-Path $MemoryDir "$Key.md"
        if (Test-Path $file) { return $false }

        $summary = Get-NoteSummary -Text $Text
        $body = @(
            "---",
            "name: $Key",
            "description: `"$summary`"",
            "metadata:",
            "  node_type: memory",
            "  type: project",
            "  source: $Source",
            "  learned: $Stamp",
            "  last-confirmed: $Stamp",
            "---",
            "",
            $Text,
            ""
        ) -join "`n"
        [System.IO.File]::WriteAllText($file, $body, (New-Object System.Text.UTF8Encoding($false)))
        Add-IndexLine -MemoryDir $MemoryDir -Key $Key -Title (Get-NoteTitle -Key $Key) -Summary $summary
        return $true
    }
    catch { return $false }
}

# ---- Folding a fact into the note that already covers it --------------------------------------

# Run one agent call and return its stdout. Reflection, merging and maintenance all need the same
# thing: feed a prompt on stdin, read a reply, give up after a while. A job rather than a direct
# call because that is the only way to enforce the timeout, and a job is its own process that
# inherits none of the parent's encoding setup - hence the two lines re-establishing UTF-8 inside it.
function Invoke-AgentPrompt {
    param([string]$Prompt, $Command, [string]$WorkDir, [int]$TimeoutSec = 120, [string]$Tag = "agent")

    $tempFile = Join-Path $env:TEMP "whetstone-$Tag-$PID-$(Get-Random).md"
    Set-Content -Path $tempFile -Value $Prompt -Encoding UTF8
    $reply = ""
    try {
        $job = Start-Job -ScriptBlock {
            param($file, $dir, $cmd)
            try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
            $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            Set-Location $dir
            # Re-cast on the way out of serialisation: what crosses a job boundary is a deserialised
            # PSObject, and a native command's argv is the wrong place to discover that.
            $exe     = [string]$cmd.Exe
            $cmdArgs = @($cmd.Args | ForEach-Object { [string]$_ })
            # Stdout only, as the name says. Folding stderr in (2>&1 | Out-String) put the CLI's
            # own warnings - rendered by 5.1 as an error record - at the top of every reply, where
            # the first line is exactly what the callers read to decide what the agent answered.
            $said = New-Object System.Collections.Generic.List[string]
            Get-Content -Path $file -Raw | & $exe @cmdArgs 2>&1 | ForEach-Object {
                if ($_ -isnot [System.Management.Automation.ErrorRecord]) { $said.Add([string]$_) }
            }
            $said -join "`n"
        } -ArgumentList $tempFile, $WorkDir, $Command

        if (Wait-Job $job -Timeout $TimeoutSec) { $reply = (Receive-Job $job) -join "`n" }
        else { Stop-Job $job }
        Remove-Job $job -Force -ErrorAction SilentlyContinue
    }
    catch { $reply = "" }
    finally { Remove-Item $tempFile -Force -ErrorAction SilentlyContinue }

    $reply = "$reply".Trim()
    # Strip a fence the model added despite being told not to.
    if ($reply.StartsWith('```')) {
        $firstNl = $reply.IndexOf("`n")
        if ($firstNl -ge 0) { $reply = $reply.Substring($firstNl + 1) }
        if ($reply.EndsWith('```')) { $reply = $reply.Substring(0, $reply.Length - 3) }
        $reply = $reply.Trim()
    }
    return $reply
}

# The third outcome, and the one this file exists for. A finding whose subject already has a note is
# handed to the agent along with the two or three notes that might be it, and the agent either
# rewrites one of them to include the new fact or answers that the fact is genuinely new.
#
# Why the agent decides and not the score: tuning a lexical threshold to answer "same fact or not"
# was tried against the real store and the numbers do not separate. On the finding that started all
# this, the right note scored 0.466 and a wrong one 0.419. Ranking is reliable, the cutoff is not -
# so the matcher proposes and the reader disposes.
#
# Rewriting rather than appending is deliberate. A note that only accretes dated bullets becomes a
# changelog, and a changelog is read by nobody; what earns a place on the hot path is a single
# current statement of the fact. Three guards make rewriting safe enough to do unattended:
#
#   1. Save-NoteStoreSnapshot commits first, so any rewrite is one `git revert` from undone.
#   2. The reply is validated before it lands. An unlisted target, missing frontmatter, a changed
#      `name:`, or a body under half the length of what it replaces all mean refusal.
#   3. A refused rewrite degrades to appending the fact to that note. Losing tidiness beats
#      overwriting a hand-written note with a truncated reply.
#
# Returns @{ Outcome = 'merged'|'appended'|'new'; Key = <note key or $null> }.
function Resolve-FactPlacement {
    param(
        [string]$MemoryDir, [string]$Text, $Candidates, [string]$Stamp,
        $ReflectCmd, [string]$RepoDir
    )

    $new = @{ Outcome = 'new'; Key = $null }
    if (-not $Candidates -or @($Candidates).Count -eq 0) { return $new }

    # A very long note would crowd the prompt without adding discriminating power; the opening of a
    # note is where it says what it is about.
    $blocks = @()
    $byKey  = @{}
    foreach ($c in @($Candidates)) {
        $raw = Read-NoteText -Path $c.Path
        if (-not $raw) { continue }
        $byKey[$c.Key] = @{ Path = $c.Path; Length = $raw.Length }
        $shown = if ($raw.Length -gt 6000) { $raw.Substring(0, 6000) + "`n[... truncated ...]" } else { $raw }
        $blocks += "--- CANDIDATE: $($c.Key) ---`n$shown"
    }
    if ($blocks.Count -eq 0) { return $new }

    # get_Keys() rather than .Keys: a note key called "keys" would otherwise shadow the property and
    # hand back that entry's value instead of the key list. See the same trap in Find-RelatedNotes.
    $keyList = ($byKey.get_Keys() | ForEach-Object { "  - $_" }) -join "`n"
    $prompt = @"
You maintain a project's durable notes store: one file per fact, read by future automated runs and
by humans before they touch this repo.

A new observation came in. Decide whether it belongs in one of the existing notes below, or whether
it is a genuinely separate fact.

Candidates:
$keyList

REPLY FORMAT - the first line is exactly one of:
  TARGET: <one of the candidate keys above>
  TARGET: NEW

If you answered NEW, stop there and reply with nothing else.

If you named a candidate, the rest of your reply is the COMPLETE rewritten contents of that file,
starting with its ``---`` frontmatter line. No preamble, no markdown fences, no commentary.

Answer NEW unless the observation is about the SAME specific thing the note is about. Two facts
touching the same feature area are not the same fact. A note that would need an unrelated second
topic bolted onto it is the wrong home, and a bad merge costs more than a duplicate note.

When rewriting:
- Keep ``name:`` exactly as it is, and keep the other frontmatter keys.
- Update ``description:`` to one line, under 140 characters, covering the merged fact.
- Set ``last-confirmed: $Stamp`` in the metadata block, adding the key if it is missing.
- State the combined fact as one current description. Do not add a history or changelog section.
- Preserve every specific detail already there: file paths, line numbers, endpoint names, table and
  column names, credentials, exact error strings. These were expensive to learn, and losing one is
  worse than an untidy note.
- If the observation contradicts the note, the observation is what was just seen: state the
  corrected fact, and say in one clause what was previously believed.

$($blocks -join "`n`n")

--- NEW OBSERVATION ---
$Text
"@

    $reply = Invoke-AgentPrompt -Prompt $prompt -Command $ReflectCmd -WorkDir $RepoDir -Tag "merge"
    if (-not $reply) { return $new }

    $lines = $reply -split "`r?`n", 2
    if ($lines[0] -notmatch '^\s*TARGET:\s*(.+?)\s*$') { return $new }
    $target = $Matches[1].Trim()
    if ($target -eq 'NEW' -or -not $byKey.ContainsKey($target)) { return $new }

    $notePath = $byKey[$target].Path
    $original = $byKey[$target].Length
    $body     = if ($lines.Count -gt 1) { $lines[1].Trim() } else { "" }

    Save-NoteStoreSnapshot -MemoryDir $MemoryDir -Message "Before folding a new fact into $target"

    $wellFormed = $body.StartsWith('---') -and
                  $body -match "(?m)^name:\s*$([regex]::Escape($target))\s*$" -and
                  $body.Length -ge [Math]::Max(120, [int]($original * 0.5))

    if (-not $wellFormed) {
        Add-Utf8Text -Path $notePath -Text "`n**Also observed ($Stamp):** $Text`n"
        return @{ Outcome = 'appended'; Key = $target }
    }

    [System.IO.File]::WriteAllText($notePath, $body + "`n", (New-Object System.Text.UTF8Encoding($false)))

    # Keep the index line in step with the note it points at.
    $desc = ""
    if ($body -match '(?m)^description:\s*"?(.+?)"?\s*$') { $desc = $Matches[1].TrimEnd('"') }
    if (-not $desc) { $desc = Get-NoteSummary -Text $Text }
    Add-IndexLine -MemoryDir $MemoryDir -Key $target -Title (Get-NoteTitle -Key $target) -Summary $desc
    return @{ Outcome = 'merged'; Key = $target }
}

# ---- A fact a steward said it learned ----------------------------------------------------------

# Place one fact a steward run handed over with steward_learned. The same placement reflection
# uses - fold it into the note that already covers it, or write a new one - but no review queue:
# the steward said it outright, so it is first-hand by construction, and the board keeps it until
# it lands. A key that is taken gets a number rather than overwriting what is there.
#
# Returns @{ Outcome = 'new' | 'merged' | 'appended' | 'none'; Key = <note key or $null> }.
function Add-LearnedFact {
    param([string]$MemoryDir, [string]$Key, [string]$Text, [string]$Stamp, $ReflectCmd, [string]$RepoDir)

    $none = @{ Outcome = 'none'; Key = $null }
    $Text = ("$Text" -replace "\s+", " ").Trim()
    if (-not $MemoryDir -or -not $Text) { return $none }
    if (-not (Test-Path $MemoryDir)) { New-Item -ItemType Directory -Path $MemoryDir -Force | Out-Null }

    $candidates = @(Find-RelatedNotes -MemoryDir $MemoryDir -Text $Text)
    if ($candidates.Count -gt 0 -and $ReflectCmd) {
        $placement = Resolve-FactPlacement -MemoryDir $MemoryDir -Text $Text -Candidates $candidates `
            -Stamp $Stamp -ReflectCmd $ReflectCmd -RepoDir $RepoDir
        if ($placement.Outcome -eq 'merged' -or $placement.Outcome -eq 'appended') {
            return @{ Outcome = $placement.Outcome; Key = $placement.Key }
        }
    }

    $base = "$Key".Trim().ToLowerInvariant()
    if (-not $base) { $base = ($Text.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-') }
    if ($base.Length -gt 56) { $base = $base.Substring(0, 56).TrimEnd('-') }
    if (-not $base) { return $none }
    $k = $base
    $n = 2
    while (Test-Path (Join-Path $MemoryDir "$k.md")) { $k = "$base-$n"; $n++ }

    if (Write-MemoryNote -MemoryDir $MemoryDir -Key $k -Text $Text -Stamp $Stamp -Source 'steward') {
        return @{ Outcome = 'new'; Key = $k }
    }
    return $none
}

# ---- Aging ------------------------------------------------------------------------------------

# Notes never expired, so a fact about a limitation that has since been fixed is read by every run
# forever. This does not delete anything - a fact going unmentioned for months usually means it is
# simply true and nobody tripped over it. It reports, so the maintenance pass and the dashboard can
# say which notes nobody has confirmed in a long time.
function Get-StaleNotes {
    param([string]$MemoryDir, [int]$Days = 120)

    if (-not $MemoryDir -or -not (Test-Path $MemoryDir)) { return @() }
    $cutoff = (Get-Date).AddDays(-$Days)
    $stale = @()
    foreach ($f in (Get-ChildItem -Path $MemoryDir -Filter *.md -File -ErrorAction SilentlyContinue)) {
        if ($f.BaseName.ToLowerInvariant() -eq 'memory') { continue }
        $raw = Read-NoteText -Path $f.FullName
        $when = $null
        if ($raw -match '(?m)^\s*last-confirmed:\s*(.+?)\s*$') { $when = $Matches[1] }
        elseif ($raw -match '(?m)^\s*learned:\s*(.+?)\s*$')     { $when = $Matches[1] }
        $dt = $null
        if ($when) { try { $dt = [datetime]::Parse($when) } catch { $dt = $null } }
        if (-not $dt) { $dt = $f.LastWriteTime }
        if ($dt -lt $cutoff) {
            $stale += [pscustomobject]@{ Key = $f.BaseName; Age = [int]((Get-Date) - $dt).TotalDays }
        }
    }
    return $stale
}


# ---- Maintenance ------------------------------------------------------------------------------

# Why any of this exists: the index is not a filing cabinet nobody opens. Every heartbeat worker
# reads it before starting a ticket, every reflection is handed it to check against, and every
# interactive session in the repo auto-loads it. At 101 lines and 18 KB it was costing roughly four
# thousand tokens on three separate hot paths, and it only ever grew - one line per fact, forever,
# with nothing that merged, retired, or even noticed a topic recorded five different ways.
#
# Consolidation is the answer rather than deletion. A fact worth learning is still worth having; what
# is not worth having is the same subject spread over five files, each read separately, none of them
# complete. Merging shrinks the index AND makes what survives better.

# Pairs of notes similar enough that they may be one fact written twice.
#
# This uses TRUE cosine - both norms, no pivoting - because here both sides are documents of the same
# kind, and the length correction that Find-RelatedNotes needs for a one-sentence query against a
# 9 KB note is exactly wrong for comparing two notes to each other. It is also bounded at 1.0, which
# a threshold can be reasoned about.
#
# The threshold is high on purpose, and the reason is measured rather than guessed. Across all 3,616
# pairs in one project's store the highest similarity of any two notes was 0.252, and the top of that
# list mixes plausible merges with pairs that are plainly different facts (`git-conventions` and
# `stale-main-tracking-in-worktrees` scored 0.229). There is no cutoff in that range that separates
# duplicates from shared vocabulary, so anything low enough to fire on this store would be firing on
# noise, and a wrong merge destroys a fact.
#
# So the bar is what a genuine near-duplicate looks like - two notes that share most of their
# distinctive vocabulary - and on a healthy store this returns nothing. That is the correct answer:
# the duplicate this system actually produces is caught at write time by Resolve-FactPlacement, and
# what remains here is a backstop for stores that predate it. When the index is too big and nothing
# here is mergeable, Invoke-NoteStoreMaintenance asks a human once rather than merging on a hunch.
function Get-NoteClusters {
    param([string]$MemoryDir, [double]$Threshold = 0.45, [int]$Max = 3)

    $notes = @(Get-ChildItem -Path $MemoryDir -Filter *.md -File -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName.ToLowerInvariant() -ne 'memory' })
    if ($notes.Count -lt 3) { return @() }

    $tokens = @{}
    $docFreq = @{}
    foreach ($n in $notes) {
        $t = @(Get-NoteTokens -Text ((Get-NoteBody -Path $n.FullName) + " " + ($n.BaseName -replace '-', ' ')))
        if ($t.Count -lt 5) { continue }
        $tokens[$n.BaseName] = $t
        foreach ($x in $t) {
            if ($docFreq.ContainsKey($x)) { $docFreq[$x] = $docFreq[$x] + 1 } else { $docFreq[$x] = 1 }
        }
    }

    # get_Keys(), never .Keys - a token called "keys" would shadow the property. See Find-RelatedNotes.
    $names = @($tokens.get_Keys())
    if ($names.Count -lt 3) { return @() }
    $total = [double]$notes.Count

    $norm = @{}
    foreach ($k in $names) {
        $s = 0.0
        foreach ($x in $tokens[$k]) {
            $w = [Math]::Log($total / [double]$docFreq[$x]) + 1.0
            $s += ($w * $w)
        }
        $norm[$k] = [Math]::Sqrt($s)
    }

    $pairs = @()
    for ($i = 0; $i -lt $names.Count; $i++) {
        for ($j = $i + 1; $j -lt $names.Count; $j++) {
            $a = $names[$i]; $b = $names[$j]
            if ($norm[$a] -le 0 -or $norm[$b] -le 0) { continue }
            $dot = 0.0
            foreach ($x in $tokens[$a]) {
                if ($tokens[$b] -contains $x) {
                    $w = [Math]::Log($total / [double]$docFreq[$x]) + 1.0
                    $dot += ($w * $w)
                }
            }
            if ($dot -le 0) { continue }
            $cos = $dot / ($norm[$a] * $norm[$b])
            if ($cos -ge $Threshold) {
                $pairs += [pscustomobject]@{ A = $a; B = $b; Score = [Math]::Round($cos, 3) }
            }
        }
    }

    # Each note may appear in at most one merge per pass: a merge rewrites both sides, so a note
    # cannot be in two of them. A three-note topic collapses over two passes, which is fine.
    $used = New-Object System.Collections.Generic.HashSet[string]
    $out = @()
    foreach ($p in ($pairs | Sort-Object Score -Descending)) {
        if ($used.Contains($p.A) -or $used.Contains($p.B)) { continue }
        [void]$used.Add($p.A); [void]$used.Add($p.B)
        $out += $p
        if ($out.Count -ge $Max) { break }
    }
    return $out
}

# is committed beforehand, so a bad merge costs a `git revert` rather than a fact.
function Merge-NotePair {
    param([string]$MemoryDir, [string]$KeyA, [string]$KeyB, [string]$Stamp, $ReflectCmd, [string]$RepoDir)

    $pathA = Join-Path $MemoryDir "$KeyA.md"
    $pathB = Join-Path $MemoryDir "$KeyB.md"
    if (-not (Test-Path $pathA) -or -not (Test-Path $pathB)) { return $null }
    $rawA = Read-NoteText -Path $pathA
    $rawB = Read-NoteText -Path $pathB
    $floor = [int](([Math]::Max($rawA.Length, $rawB.Length)) * 0.6)

    $prompt = @"
You maintain a project's durable notes store: one file per fact, read by automated runs and by
humans before they touch this repo. The index of these notes is loaded into context on every run, so
two notes covering one subject cost real tokens every time and are each less complete than a merged
one would be.

Below are two notes that look like the same subject. Decide whether they genuinely are.

REPLY FORMAT - the first line is exactly one of:
  KEEP: $KeyA
  KEEP: $KeyB
  KEEP: NEITHER

Answer NEITHER if these are different facts that merely share vocabulary, or if merging them would
produce a note about two things. Different facts about one feature area are DIFFERENT FACTS. When in
doubt, answer NEITHER - a duplicate note is a small cost and a destroyed fact is a large one.

If you named a key, the rest of your reply is the COMPLETE contents of that file after absorbing the
other, starting with its ``---`` frontmatter line. No preamble, no fences, no commentary.

- ``name:`` must be the key you chose.
- ``description:`` one line, under 140 characters, covering the merged fact.
- Set ``last-confirmed: $Stamp`` in the metadata block.
- Carry over EVERY specific detail from both notes: file paths, line numbers, endpoint names, table
  and column names, credentials, exact error strings, commands. Dropping one to make the note read
  more neatly is the one failure that matters here.
- Write it as one coherent statement of the subject, not two sections stapled together.

--- NOTE $KeyA ---
$rawA

--- NOTE $KeyB ---
$rawB
"@

    $reply = Invoke-AgentPrompt -Prompt $prompt -Command $ReflectCmd -WorkDir $RepoDir -Tag "consolidate"
    if (-not $reply) { return $null }

    $lines = $reply -split "`r?`n", 2
    if ($lines[0] -notmatch '^\s*KEEP:\s*(.+?)\s*$') { return $null }
    $keep = $Matches[1].Trim()
    if ($keep -ne $KeyA -and $keep -ne $KeyB) { return $null }

    $drop = if ($keep -eq $KeyA) { $KeyB } else { $KeyA }
    $body = if ($lines.Count -gt 1) { $lines[1].Trim() } else { "" }

    # A merged note must be at least as substantial as the larger of its two inputs, or detail was
    # dropped on the way through and this is a summary pretending to be a merge.
    $ok = $body.StartsWith('---') -and
          $body -match "(?m)^name:\s*$([regex]::Escape($keep))\s*$" -and
          $body.Length -ge $floor
    if (-not $ok) { return $null }

    [System.IO.File]::WriteAllText((Join-Path $MemoryDir "$keep.md"), $body + "`n",
        (New-Object System.Text.UTF8Encoding($false)))
    Remove-Item (Join-Path $MemoryDir "$drop.md") -Force -ErrorAction SilentlyContinue
    Remove-IndexLine -MemoryDir $MemoryDir -Key $drop

    $desc = ""
    if ($body -match '(?m)^description:\s*"?(.+?)"?\s*$') { $desc = $Matches[1].TrimEnd('"') }
    if (-not $desc) { $desc = "See $keep.md" }
    Add-IndexLine -MemoryDir $MemoryDir -Key $keep -Title (Get-NoteTitle -Key $keep) -Summary $desc

    return [pscustomobject]@{ Kept = $keep; Dropped = $drop }
}

# The whole pass. Rate-limited by a stamp file rather than a schedule, so it runs off the back of
# whatever cycle happens to notice it is due, and never twice in a day.
#
# It fires on the index crossing a line count, not on a timer: a store that is not growing does not
# need consolidating, and one that is growing fast should be tidied sooner than a calendar would.
function Invoke-NoteStoreMaintenance {
    param(
        [string]$MemoryDir, [string]$ProjectDataDir, $ReflectCmd, [string]$RepoDir,
        [int]$IndexLineLimit = 120, [int]$MaxMerges = 3, [int]$MinHoursBetween = 20
    )

    if (-not $MemoryDir -or -not (Test-Path $MemoryDir)) { return }
    $index = Join-Path $MemoryDir "MEMORY.md"
    if (-not (Test-Path $index)) { return }

    $entries = @(Read-NoteLines -Path $index | Where-Object { $_ -match '^\s*-\s*\[' })
    if ($entries.Count -lt $IndexLineLimit) { return }

    $marker = Join-Path $ProjectDataDir "notes-maintenance.stamp"
    if (Test-Path $marker) {
        $last = (Get-Item $marker).LastWriteTime
        if ((Get-Date) -lt $last.AddHours($MinHoursBetween)) { return }
    }

    Write-Log "--- Notes maintenance ($($entries.Count) index entries) ---"
    [void](Initialize-NoteStoreGit -MemoryDir $MemoryDir)
    Save-NoteStoreSnapshot -MemoryDir $MemoryDir -Message "Before notes maintenance"
    Set-Content -Path $marker -Value (Get-Date -Format 'o') -Encoding ASCII

    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
    $done = 0
    foreach ($pair in @(Get-NoteClusters -MemoryDir $MemoryDir -Max $MaxMerges)) {
        $r = Merge-NotePair -MemoryDir $MemoryDir -KeyA $pair.A -KeyB $pair.B `
            -Stamp $stamp -ReflectCmd $ReflectCmd -RepoDir $RepoDir
        if ($r) {
            Write-Log "  Consolidated: $($r.Dropped) -> $($r.Kept)  (similarity $($pair.Score))"
            $done++
        }
        else { Write-Log "  Left alone: $($pair.A) / $($pair.B) (similarity $($pair.Score))" }
    }

    $stale = @(Get-StaleNotes -MemoryDir $MemoryDir -Days 120)
    if ($stale.Count -gt 0) {
        $worst = ($stale | Sort-Object Age -Descending | Select-Object -First 5 |
            ForEach-Object { "$($_.Key) ($($_.Age)d)" }) -join ", "
        Write-Log "  $($stale.Count) notes unconfirmed for 120+ days. Oldest: $worst"
    }

    if ($done -gt 0) {
        Save-NoteStoreSnapshot -MemoryDir $MemoryDir -Message "Notes maintenance: $done merged"
    }

    # Nothing was safely mergeable and the index is still over the limit. This is the one case in
    # the whole pipeline that genuinely needs a person: the store is costing context on every run,
    # and the machine has looked for duplicates and honestly found none, so what is left is a
    # judgement about which facts still earn their place. Asked once per pass, with the numbers.
    if ($done -eq 0 -and $ProjectDataDir) {
        $suggestions = Join-Path $ProjectDataDir "improvement-suggestions.md"
        $already = $false
        if (Test-Path $suggestions) {
            $already = @(Read-NoteLines -Path $suggestions |
                Where-Object { $_ -match 'notes index has grown' -and $_ -match '^\s*-\s*\[ \]' }).Count -gt 0
        }
        if (-not $already) {
            $staleNote = if ($stale.Count -gt 0) { " $($stale.Count) notes are 120+ days unconfirmed." } else { "" }
            Add-Utf8Text -Path $suggestions -Text (
                "`n- [ ] [process] The notes index has grown to $($entries.Count) entries and is read on " +
                "every worker run, every reflection and every interactive session. No two notes were " +
                "similar enough to merge automatically, so pruning needs a human read.$staleNote " +
                "(key: notes-index-over-limit, $stamp)`n")
            Write-Log "  Index over limit with nothing auto-mergeable - filed one item for review"
        }
    }

    Write-Log "  Maintenance done: $done merged, index now $((@(Read-NoteLines -Path $index | Where-Object { $_ -match '^\s*-\s*\[' })).Count) entries"
}

Set-StrictMode -Off
