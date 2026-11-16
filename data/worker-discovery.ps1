# The Discovery track's part of a worker run (docs/discovery-track-plan.md). Functions only;
# nothing here runs unless the claim's ticket is on the Discovery track.
#
# ASCII ONLY - see heartbeat-common.ps1.

# The scheme, host and port of a URL, as the browser server's --allowed-origins wants it.
function Get-UrlOrigin {
    param([string]$Url)
    try {
        $u = [Uri]$Url
        if (-not $u.IsAbsoluteUri) { return '' }
        return "$($u.Scheme)://$($u.Authority)"
    } catch { return '' }
}

# Where the product answers for this run: the project's siteUrl as slot 0 sees it, moved up by
# slot * slotStep on a machine that runs more than one copy of the stack (stackPorts.slotStep).
function Get-DiscoverySiteUrl {
    param([string]$SiteUrl, [int]$Slot = 0, [int]$SlotStep = 0)
    if (-not $SiteUrl) { return '' }
    try { $u = [Uri]$SiteUrl } catch { return $SiteUrl }
    if (-not $u.IsAbsoluteUri -or $Slot -le 0 -or $SlotStep -le 0) { return $SiteUrl.TrimEnd('/') }
    $b = [UriBuilder]::new($u)
    $b.Port = $u.Port + ($Slot * $SlotStep)
    return $b.Uri.AbsoluteUri.TrimEnd('/')
}

# The discovery phases that use the product in a browser: the scout, and the recheck that drives it
# again once the approved work is built. Both get the run's own site, a capture folder, the stack
# lease and the prepare/teardown around the session; chart and sift get none of it.
function Test-DiscoveryDrives {
    param([string]$Skill)
    return ($Skill -eq 'scout' -or $Skill -eq 'recheck')
}

# The browser server's arguments for a run that drives the product: headless and isolated, its
# files in the run's own folder under the data root, the run's site and no other origin, and the
# devtools capability - which is what gives it browser_start_video, browser_stop_video and
# browser_video_chapter (docs/discovery-track-plan.md, step 3). The video lands in the same folder,
# named for the moment it started, and the board adopts it from there (DiscoveryMedia).
function Get-DiscoveryBrowserArgs {
    param([string]$RunDir, [string]$Site = '', [string]$Viewport = '1440x900')
    $a = @('--headless', '--isolated', '--caps', 'devtools', '--output-dir', $RunDir, '--viewport-size', $Viewport)
    if ($Site) {
        $origin = Get-UrlOrigin $Site
        if ($origin) { $a += @('--allowed-origins', $origin) }
    }
    return $a
}

# The agent's own tools on a discovery run. On claude, every discovery phase runs with no shell and
# no editor - Read, Grep and Glob, and the MCP servers the run is given - the steward's lock-down,
# and the scout's browser loses browser_run_code_unsafe on top. On codex and gemini there is no such
# switch, so the prompt's rule is the whole of it, as with the local-only guard.
function Get-DiscoveryToolArgs {
    param([string]$Provider, [string]$Skill)
    if ($Provider -ne 'claude') { return @() }
    $toolArgs = @('--tools', 'Read,Grep,Glob')
    if (Test-DiscoveryDrives $Skill) { $toolArgs += @('--disallowedTools', 'mcp__playwright__browser_run_code_unsafe') }
    return $toolArgs
}

# What a discovery run is told on top of the track's standing rules: the commit it is on, and for a
# scout or a recheck the one site it may use and where its screenshots go. Areas are listed when the project
# names them, so every visit and observation can carry one.
# The recording's own arguments, said once here and handed to the agent word for word. Left to its
# default, the browser shrinks what it records to fit 800x800 before it encodes - 1440x900 came out
# 800x500 and unreadable - so the size is the window's own. The cursor travels to each action point
# (and paces actions so the eye can follow it). The overlays name each action and outline the
# element it acts on, in the board's amber; a hover is an action, which is how the agent points.
function Get-DiscoveryVideoArgs {
    param([string]$Viewport = '1440x900')
    $w = 1440; $h = 900
    if ($Viewport -match '^(\d+)x(\d+)$') { $w = [int]$Matches[1]; $h = [int]$Matches[2] }
    return '`{"size":{"width":' + $w + ',"height":' + $h + '},"cursor":true}`'
}

function Get-DiscoveryActionArgs {
    return '`{"duration":1500,"position":"top","style":{"highlight":"outline:4px solid #f5a524;outline-offset:3px;border-radius:6px;background:rgba(245,165,36,0.14)","point":"width:22px;height:22px;margin:-11px 0 0 -11px;border-radius:50%;background:rgba(245,165,36,0.55)"}}`'
}

function Format-DiscoveryNote {
    param([string]$Skill, [string]$Commit, [string]$Site = '', [string]$RunDir = '', [int]$MaxProposals = 6, $Areas = @(),
        [string]$Viewport = '1440x900')
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Commit) { [void]$lines.Add("The product here is built from commit ``$Commit``: pass it as ``commit`` to every observation and visit you log.") }
    if (Test-DiscoveryDrives $Skill) {
        if ($Site) { [void]$lines.Add("The product is up at $Site, on this run's own copy of its showcase data. The browser reaches that site and nothing else.") }
        if ($RunDir) { [void]$lines.Add("Save every screenshot under ``$RunDir`` (an absolute filename there), and pass that path to ``log_observation``.") }
        [void]$lines.Add("Record the run: before the first job, ``browser_start_video`` with no filename and exactly " + (Get-DiscoveryVideoArgs -Viewport $Viewport) + ", then ``browser_video_show_actions`` with exactly " + (Get-DiscoveryActionArgs) + ". ``browser_video_chapter`` then ``mark_chapter`` at each job and each step worth jumping to, ``say`` what you are doing as you do it, and ``browser_stop_video`` before you hand off. If recording will not start, carry on without it and say so in your hand-off comment.")
    }
    if ($Skill -eq 'sift') { [void]$lines.Add("Keep at most $MaxProposals proposals; everything else goes on the dropped list with its reason.") }
    if ($Skill -eq 'recheck') { [void]$lines.Add("Check every goal get_discovery lists (check_goal), and propose at most $MaxProposals things - fix-ups and new - held to the sift's rules.") }
    if ($Skill -eq 'sift' -or $Skill -eq 'recheck') { [void]$lines.Add("Read get_discovery's taste before you cut and rank: it is what this owner's verdicts have taught about what they want proposed.") }
    $named = @($Areas | Where-Object { $_ -and $_.name })
    if ($named.Count -gt 0) {
        [void]$lines.Add("The project names its areas; tag what you log with one of: " + (($named | ForEach-Object {
            $pages = @($_.pages) -join ', '
            if ($pages) { "``$($_.name)`` ($pages)" } else { "``$($_.name)``" }
        }) -join '; ') + '.')
    }
    if ($lines.Count -eq 0) { return '' }
    return "`n" + (($lines | ForEach-Object { "- $_" }) -join "`n")
}

# ---- The taste fold (step 4) ---------------------------------------------------------------------

# Fold the owner's verdicts into the project's taste file, when a fold is due. Rides on the back of a
# worker run - any run of the project, on the board host - the way the notes store's maintenance rides
# on reflection: the reflection command and the worktree are already set up, and it is outside the
# run's measured window. The board decides whether one is due and what the model is told
# (/taste/fold/begin), and it checks, writes, commits and versions what comes back (/taste/fold/finish);
# this only runs the model between the two. A refusal or a failure folds nothing, and the verdicts wait
# for the next run. Never throws.
#
#   -Board   { param($Path, $Body) ... } posting JSON to the board and returning the parsed answer -
#            injectable, so the tests can answer for it.
#   -Ask     { param($Prompt) ... } returning the model's reply - injectable likewise.
# Returns the outcome in a word, for the log and the tests: 'not-due', 'folded', 'refused', 'failed'.
function Invoke-DiscoveryTasteFold {
    param([string]$ProjectKey, [int]$TicketId = 0, [scriptblock]$Board, [scriptblock]$Ask, [scriptblock]$Log = { param($m) })

    if (-not $ProjectKey -or -not $Board -or -not $Ask) { return 'not-due' }
    try {
        $begin = & $Board "/api/discovery/projects/$ProjectKey/taste/fold/begin" @{ ticketId = $TicketId }
    }
    catch { & $Log "  Taste fold: could not ask the board - $($_.Exception.Message)"; return 'failed' }
    if (-not $begin -or -not $begin.due) { return 'not-due' }

    & $Log "--- Taste fold ($($begin.reason)) ---"
    $reply = ''
    $err = $null
    try { $reply = "$(& $Ask ([string]$begin.prompt))".Trim() }
    catch { $err = $_.Exception.Message }
    if (-not $err -and -not $reply) { $err = 'the model answered nothing (timed out, or refused)' }

    $body = @{ claim = [string]$begin.claim; ticketId = $TicketId }
    if ($err) { $body.error = $err } else { $body.reply = $reply }
    try {
        $done = & $Board "/api/discovery/projects/$ProjectKey/taste/fold/finish" $body
        if ($err) { & $Log "  Taste fold: $err - nothing folded"; return 'failed' }
        & $Log "  Taste fold: version $($done.number) written ($($done.verdicts) verdicts)"
        return 'folded'
    }
    catch {
        $why = $_.Exception.Message
        try { if ($_.ErrorDetails.Message) { $why = ($_.ErrorDetails.Message | ConvertFrom-Json).error } } catch { }
        & $Log "  Taste fold: not written - $why"
        if ($err) { return 'failed' }
        return 'refused'
    }
}
