# ============================================================
# Augment usage, recorded where it can be counted.
#
# A PostToolUse hook on mcp__auggie__codebase-retrieval, wired into every claude worker run by
# New-AgentToolSetup (providers.ps1). It does two things per call:
#
#   1. Appends one JSON line to logs/augment-calls.jsonl - which ticket, which phase, what was
#      asked, how much came back and which files. Written here rather than asked of the model, so
#      the count is complete even on a run that forgets to rate.
#   2. Asks the model to rate the call against grep/glob in its hand-off add_comment, so the rating
#      lands in the Comments table beside the ticket it is about.
#
# Before this, the only rating hook was one project's own .claude/settings.local.json (copied into its
# worktrees). Every other project went unrated, and the ratings that did exist lived only in
# session transcripts, where nothing ever read them back.
#
# FAILS OPEN. This is measurement, not a wall: a payload it cannot read produces no output and the
# run carries on exactly as if the hook were not there.
#
# The worker sets WHETSTONE_TICKET_ID / _PROJECT / _SKILL before launching the agent; the hook is a
# grandchild of that process and inherits them. WHETSTONE_AUGMENT_LOG overrides the log path (tests).
#
# ASCII ONLY - see heartbeat-common.ps1. The rating line's emoji and punctuation are built from code
# points below, and every non-ASCII character is \u-escaped on the way out, so neither this file's
# codepage nor the console's can mangle them.
# ============================================================

$ErrorActionPreference = 'Stop'

# The retrieval result arrives as a string, a content array of { type, text }, or an object holding
# one; all three reduce to the text a person would have read.
function Get-ResultText {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [System.Array]) { return (@($Value | ForEach-Object { Get-ResultText $_ }) -join "`n") }
    if ($Value.PSObject.Properties['text'])    { return "$($Value.text)" }
    if ($Value.PSObject.Properties['content']) { return (Get-ResultText $Value.content) }
    if ($Value.PSObject.Properties['result'])  { return (Get-ResultText $Value.result) }
    return ''
}

# ---- Read the call ---------------------------------------------------------

try {
    # Raw bytes, decoded as UTF-8: [Console]::In would decode with the OEM codepage and mangle every
    # quote and dash in the question the model asked.
    $stdin = [Console]::OpenStandardInput()
    $buffer = New-Object System.IO.MemoryStream
    $stdin.CopyTo($buffer)
    $payload = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray()) | ConvertFrom-Json
}
catch { exit 0 }

# ---- 1. The call log -------------------------------------------------------

try {
    $query = ''
    $ti = $payload.tool_input
    if ($ti -and $ti.PSObject.Properties['information_request']) { $query = "$($ti.information_request)" }

    $text  = Get-ResultText $payload.tool_response
    $files = @([regex]::Matches($text, '(?m)^Path:\s*(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)

    $ticketId = 0
    [void][int]::TryParse("$env:WHETSTONE_TICKET_ID", [ref]$ticketId)

    $record = [ordered]@{
        at            = (Get-Date).ToString('o')
        ticketId      = $(if ($ticketId -gt 0) { $ticketId } else { $null })
        project       = "$env:WHETSTONE_PROJECT"
        skill         = "$env:WHETSTONE_SKILL"
        session       = "$($payload.session_id)"
        cwd           = "$($payload.cwd)"
        query         = $query
        responseChars = $text.Length
        files         = $files
    }
    $line = (ConvertTo-Json $record -Depth 4 -Compress) + "`n"

    $log = "$env:WHETSTONE_AUGMENT_LOG"
    if (-not $log) { $log = Join-Path $PSScriptRoot 'logs\augment-calls.jsonl' }
    $dir = Split-Path -Parent $log
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # Up to three agents share this file. Same mutex naming as Add-Utf8Text in heartbeat-common.ps1.
    $mutex = New-Object System.Threading.Mutex($false, 'Local\CDH-file-augment-calls-jsonl')
    $held = $false
    try {
        try { $held = $mutex.WaitOne(5000) } catch [System.Threading.AbandonedMutexException] { $held = $true }
        [System.IO.File]::AppendAllText($log, $line, (New-Object System.Text.UTF8Encoding($false)))
    }
    finally {
        if ($held) { try { $mutex.ReleaseMutex() } catch { } }
        $mutex.Dispose()
    }
}
catch { }

# ---- 2. Ask for the rating -------------------------------------------------

$compass = [char]::ConvertFromUtf32(0x1F9ED)
$dash    = [char]0x2014
$dot     = [char]0x00B7
$format  = "$compass **Augment** $dash asked: [~8 words] $dot returned: [~10 words] $dot vs grep/glob: **N/10**"

$context = "You just called Augment's codebase-retrieval. Rate it: in the add_comment you post when you " +
    "hand this phase off, include ONE line per Augment call you made this run, in exactly this format: " +
    "$format - where N is how much more useful it was than Grep/Glob/Read would have been for THIS " +
    "query (10 = far better, 5 = about the same, 1 = grep would have been faster or better). A low " +
    "score is as useful as a high one, so rate what actually happened. One line per call, written " +
    "once, even if another reminder asks for the same line."

$out  = @{ hookSpecificOutput = @{ hookEventName = 'PostToolUse'; additionalContext = $context } }
$json = ConvertTo-Json $out -Depth 4 -Compress
$json = [regex]::Replace($json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
[Console]::Out.Write($json)
exit 0
