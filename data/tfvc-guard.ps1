# ============================================================
# TFVC belongs to the human.
#
# Some projects' real source control is TFVC (repoType tfvc). Everything an agent does there
# goes into the git mirror that shares the working tree, and the human checks work in from
# Visual Studio once they have reviewed it. This file is the rule that keeps it that way,
# and it can run in two places:
#
#   - worker-guard.ps1 dot-sources it, so every heartbeat worker is held to it.
#   - A TFVC project's own .claude/settings.json can run it as a PreToolUse hook, so
#     interactive sessions started in that repo are held to it as well. Such a hook names
#     this file by ABSOLUTE PATH: move or rename this file and it silently stops firing.
#
# A write here is more than a check-in. Even with the server out of reach - a tracker token
# with no Code scope, a tf command line that is not signed in - there are still the human's
# PENDING CHANGES, which tf add, undo, get and checkout all rewrite without touching the
# server. Only tf's read verbs pass.
#
# A backstop for the obvious spellings, not a sandbox: a tf path assembled at runtime or an
# encoded command passes. The instructions are the rule; this catches the honest mistake.
#
# Run as a hook it prints a deny or NOTHING. Never 'allow': in an interactive session that
# would skip the human's own permission prompt for the command.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

# Returns $null, or @{ Kind; What } naming the TFVC write the command contains.
function Get-TfvcWrite {
    param([string]$Command)
    if (-not $Command) { return $null }

    $sq = [string][char]39
    $q  = '["' + $sq + ']?'
    $c  = $Command

    # A commit message is read, never run, and one that mentions 'tf checkin' describes
    # the rule rather than breaking it. Only messages: a heredoc piped to a shell
    # is a command, so heredoc bodies are dropped only when the command is a git commit.
    $c = [regex]::Replace($c, '(?i)(?:\s-m|--message)(?:[ \t]+|=)("[^"]*"|' + $sq + '[^' + $sq + ']*' + $sq + ')', ' ')
    if ($c -match '(?i)\bgit\b[^\r\n]*\b(?:commit|tag)\b') {
        $c = [regex]::Replace($c, '(?s)<<-?[ \t]*([' + $sq + '"]?)(\w+)\1[^\n]*\n.*?\n[ \t]*\2[ \t]*(?=\r?\n|$)', ' ')
        $c = [regex]::Replace($c, '(?s)@' + $sq + '\r?\n.*?\r?\n' + $sq + '@', ' ')
        $c = [regex]::Replace($c, '(?s)@"\r?\n.*?\r?\n"@', ' ')
    }

    # tf itself. Every verb it is given must be a read; anything else - checkin, add, undo,
    # get, checkout, shelve, workfold with a mapping - is a write to somebody's TFVC state.
    $readVerbs = '^(?:status|stat|st|difference|diff|history|hist|dir|info|properties|prop|view|help)$'
    $verbs = New-Object System.Collections.Generic.List[string]

    # Named directly: bare, as tf.exe, or at the end of a path, quoted or not.
    foreach ($m in [regex]::Matches($c, '(?i)(?:^|[\s;&|(\\/"' + $sq + '])tf(?:\.exe)?' + $q + '[ \t]+(?:vc[ \t]+)?([a-z]+)')) {
        $verbs.Add($m.Groups[1].Value)
    }
    # Through a variable the path was put in: $tf = '...\TF.exe'; & $tf checkin
    foreach ($v in [regex]::Matches($c, '(?i)\$(\w+)[ \t]*=[ \t]*' + $q + '[^"' + $sq + '\r\n;]*\btf\.exe' + $q)) {
        $name = [regex]::Escape($v.Groups[1].Value)
        foreach ($m in [regex]::Matches($c, '(?i)\$' + $name + '\b' + $q + '[ \t]+(?:vc[ \t]+)?([a-z]+)')) {
            $verbs.Add($m.Groups[1].Value)
        }
    }
    # Through Start-Process: -FilePath ...\TF.exe -ArgumentList 'checkin ...'
    if ($c -match '(?i)\btf\.exe') {
        foreach ($m in [regex]::Matches($c, '(?i)-ArgumentList[ \t]+[@(" ' + $sq + ']*(?:vc[ \t]+)?([a-z]+)')) {
            $verbs.Add($m.Groups[1].Value)
        }
    }
    foreach ($verb in $verbs) {
        if ($verb -notmatch $readVerbs) { return @{ Kind = 'tf'; What = "'tf $($verb.ToLower())'" } }
    }

    # The TFS Power Tools have no read verbs worth the risk: online, uu, scorch, treeclean.
    if ($c -match ('(?i)(?:^|[\s;&|(\\/"' + $sq + '])tfpt(?:\.exe)?\b')) {
        return @{ Kind = 'tf'; What = 'the TFS Power Tools (tfpt)' }
    }
    if ($c -match '(?i)\bgit[ \t-]+tfs?[ \t]+(?:r?checkin|checkintool|ct|shelve|unshelve)\b') {
        return @{ Kind = 'tf'; What = "a git-tfs check-in ('$($Matches[0])')" }
    }
    # A changeset can be created over REST as well as through tf.
    if ($c -match '(?i)_apis/tfvc' -and $c -match ('(?i)(?:-X[ \t]*|--request[ \t]+|-Method[ \t]+' + $q + ')(?:POST|PUT|PATCH|DELETE)\b')) {
        return @{ Kind = 'tf'; What = 'a write to the TFVC REST API' }
    }
    if ($c -match '(?i)\b(?:Add|New|Remove|Set|Update|Restore|Undo|Invoke)-Tfs\w*') {
        return @{ Kind = 'tf'; What = "a TFS PowerShell cmdlet ($($Matches[0]))" }
    }
    return $null
}

# Dot-sourced by worker-guard.ps1: define the function and stop here.
if ($MyInvocation.InvocationName -eq '.') { return }

# ---- Run as a hook ---------------------------------------------------------

$ErrorActionPreference = 'Stop'

function Write-Deny {
    param([string]$Reason)
    $inner = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $Reason }
    [Console]::Out.Write((ConvertTo-Json @{ hookSpecificOutput = $inner } -Depth 6 -Compress))
    exit 0
}

$command = $null
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $ti = $payload.tool_input
    if ($ti -and $ti.PSObject.Properties['command']) { $command = "$($ti.command)" }
}
catch {
    Write-Deny "BLOCKED BY THE TFVC GUARD: it could not read this tool call, so it cannot clear it ($($_.Exception.Message))."
}

$hit = Get-TfvcWrite -Command $command
if (-not $hit) { exit 0 }

Write-Deny @"
BLOCKED BY THE TFVC GUARD: $($hit.What).

On this machine TFVC has one writer: the human at the keyboard, checking in from Visual
Studio after reviewing the work. Claude's work goes into the git mirror - commit it there.
That includes the verbs that never reach the server (add, undo, get, checkout): they
rewrite the human's pending changes, which are just as much theirs as a check-in is.

Read-only tf commands pass: status, diff, history, dir, info, properties, view.

If this task needs something done in TFVC, tell the human exactly what to run and why.
"@
