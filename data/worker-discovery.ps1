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
function Format-DiscoveryNote {
    param([string]$Skill, [string]$Commit, [string]$Site = '', [string]$RunDir = '', [int]$MaxProposals = 6, $Areas = @())
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Commit) { [void]$lines.Add("The product here is built from commit ``$Commit``: pass it as ``commit`` to every observation and visit you log.") }
    if (Test-DiscoveryDrives $Skill) {
        if ($Site) { [void]$lines.Add("The product is up at $Site, on this run's own copy of its showcase data. The browser reaches that site and nothing else.") }
        if ($RunDir) { [void]$lines.Add("Save every screenshot under ``$RunDir`` (an absolute filename there), and pass that path to ``log_observation``.") }
        [void]$lines.Add("Record the run: ``browser_start_video`` with no filename before the first job, ``browser_video_chapter`` then ``mark_chapter`` at each job and each step worth jumping to, and ``browser_stop_video`` before you hand off. If recording will not start, carry on without it and say so in your hand-off comment.")
    }
    if ($Skill -eq 'sift') { [void]$lines.Add("Keep at most $MaxProposals proposals; everything else goes on the dropped list with its reason.") }
    if ($Skill -eq 'recheck') { [void]$lines.Add("Check every goal get_discovery lists (check_goal), and propose at most $MaxProposals things - fix-ups and new - held to the sift's rules.") }
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
