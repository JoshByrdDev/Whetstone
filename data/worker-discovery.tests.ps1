# Exercises data/worker-discovery.ps1 - the Discovery track's part of a worker run: the one site a
# scout's browser may reach, the tools a discovery run is left with, and what its prompt is told.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-discovery.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'worker-discovery.ps1')

$script:total = 0
$script:fail = 0
function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

# --- the site ---------------------------------------------------------------------------------
Check 'origin drops the path' (Get-UrlOrigin 'http://localhost:53590/events/') 'http://localhost:53590'
Check 'origin keeps a non-default port' (Get-UrlOrigin 'https://app.localhost:8443') 'https://app.localhost:8443'
Check 'a relative url has no origin' (Get-UrlOrigin '/events') ''
Check 'slot 0 is the site as written' (Get-DiscoverySiteUrl -SiteUrl 'http://localhost:53590/' -Slot 0 -SlotStep 10) 'http://localhost:53590'
Check 'slot 2 moves the port by two steps' (Get-DiscoverySiteUrl -SiteUrl 'http://localhost:53590' -Slot 2 -SlotStep 10) 'http://localhost:53610'
Check 'no step means every slot shares the site' (Get-DiscoverySiteUrl -SiteUrl 'http://localhost:53590' -Slot 3 -SlotStep 0) 'http://localhost:53590'
Check 'a path survives the port move' (Get-DiscoverySiteUrl -SiteUrl 'http://localhost:3000/app' -Slot 1 -SlotStep 5) 'http://localhost:3005/app'
Check 'no site is no site' (Get-DiscoverySiteUrl -SiteUrl '' -Slot 1 -SlotStep 5) ''

# --- the browser ------------------------------------------------------------------------------
$pw = (Get-DiscoveryBrowserArgs -RunDir '/data/discovery/7/runs/x' -Site 'http://localhost:53590/events') -join ' '
Check 'the scout''s browser records: devtools caps' ($pw -match '--caps devtools') $true
Check 'its files land in the run folder' ($pw -match '--output-dir /data/discovery/7/runs/x') $true
Check 'it reaches the run''s origin and no other' ($pw -match '--allowed-origins http://localhost:53590$') $true
Check 'it is headless and isolated' ($pw -match '^--headless --isolated ') $true
Check 'no site, no origin flag' (((Get-DiscoveryBrowserArgs -RunDir '/r') -join ' ') -match 'allowed-origins') $false
Check 'the viewport is a desk by default' (((Get-DiscoveryBrowserArgs -RunDir '/r') -join ' ') -match '--viewport-size 1440x900') $true

# --- the tools --------------------------------------------------------------------------------
Check 'claude chart has no shell or editor' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'chart') -join ' ') '--tools Read,Grep,Glob'
Check 'claude sift has no shell or editor' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'sift') -join ' ') '--tools Read,Grep,Glob'
Check 'claude scout also loses browser_run_code_unsafe' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'scout') -join ' ') '--tools Read,Grep,Glob --disallowedTools mcp__playwright__browser_run_code_unsafe'
Check 'claude recheck drives the browser on the scout''s terms' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'recheck') -join ' ') '--tools Read,Grep,Glob --disallowedTools mcp__playwright__browser_run_code_unsafe'
Check 'scout drives the product' (Test-DiscoveryDrives 'scout') $true
Check 'recheck drives the product' (Test-DiscoveryDrives 'recheck') $true
Check 'chart does not' (Test-DiscoveryDrives 'chart') $false
Check 'sift does not' (Test-DiscoveryDrives 'sift') $false
Check 'codex has no switch, so nothing is added' (@(Get-DiscoveryToolArgs -Provider 'codex' -Skill 'scout').Count) 0

# --- the note ---------------------------------------------------------------------------------
$scout = Format-DiscoveryNote -Skill 'scout' -Commit 'abc123' -Site 'http://localhost:53590' -RunDir '/data/discovery/7/runs/x' `
    -Areas @(@{ name = 'events'; pages = @('/events/*') }, @{ name = 'account'; pages = @() })
Check 'the scout is told its commit' ($scout -match 'commit `abc123`') $true
Check 'the scout is told its site' ($scout -match 'up at http://localhost:53590') $true
Check 'the scout is told where shots go' ($scout -match '/data/discovery/7/runs/x') $true
Check 'areas are listed with their pages' ($scout -match '`events` \(/events/\*\); `account`') $true
Check 'the scout is not told a proposal cap' ($scout -match 'at most') $false
Check 'the scout is told to record' ($scout -match 'browser_start_video') $true
Check 'and to mark chapters on the board' ($scout -match 'mark_chapter') $true
Check 'and to stop before it hands off' ($scout -match 'browser_stop_video') $true
Check 'at the window size, not the 800px default' ($scout -match '"size":\{"width":1440,"height":900\}') $true
Check 'with the cursor on' ($scout -match '"cursor":true') $true
Check 'and the action overlays' ($scout -match 'browser_video_show_actions') $true
Check 'and to say what it is doing' ($scout -match '`say`') $true
Check 'a narrower window records at its own size' ((Format-DiscoveryNote -Skill 'scout' -Commit 'a' -Viewport '390x844') -match '"width":390,"height":844') $true
$sift = Format-DiscoveryNote -Skill 'sift' -Commit 'abc123' -MaxProposals 4
Check 'the sift is told the cap' ($sift -match 'at most 4 proposals') $true
Check 'the sift is not told a site' ($sift -match 'up at') $false
Check 'the sift is not told to record' ($sift -match 'browser_start_video') $false
$recheck = Format-DiscoveryNote -Skill 'recheck' -Commit 'def456' -Site 'http://localhost:53600' -RunDir '/data/discovery/7/runs/y' -MaxProposals 3
Check 'the recheck is told its site' ($recheck -match 'up at http://localhost:53600') $true
Check 'the recheck is told where shots go' ($recheck -match '/data/discovery/7/runs/y') $true
Check 'the recheck is told to check every goal' ($recheck -match 'check_goal') $true
Check 'the recheck is told the cap' ($recheck -match 'at most 3 things') $true
Check 'the recheck records too' ($recheck -match 'browser_start_video') $true
Check 'nothing to say is empty' (Format-DiscoveryNote -Skill 'chart' -Commit '') ''

Check 'the sift is told to read the taste' ($sift -match "get_discovery's taste") $true
Check 'the recheck is told to read the taste' ($recheck -match "get_discovery's taste") $true
Check 'the chart is not' ((Format-DiscoveryNote -Skill 'chart' -Commit 'abc') -match 'taste') $false

# --- the taste fold ---------------------------------------------------------------------------
# The board answers for itself here: what was posted where is kept, so each check can read it back.
$global:posted = @()
function New-Board {
    param($Begin, [switch]$RefuseFinish)
    return {
        param($Path, $Body)
        $global:posted += , @($Path, $Body)
        if ($Path -like '*/fold/begin') { return $Begin }
        if ($RefuseFinish) { throw 'The fold wrote 52 lines; Learned is kept to 40.' }
        return [pscustomobject]@{ number = 3; verdicts = 10 }
    }.GetNewClosure()
}
$global:asked = $null
$ask = { param($Prompt) $global:asked = $Prompt; return "- Wants proposals at the scale of a real month (3 approved)" }

$global:posted = @(); $global:asked = $null
$r = Invoke-DiscoveryTasteFold -ProjectKey 'alpha' -TicketId 7 -Board (New-Board ([pscustomobject]@{ due = $false; reason = '3 of 10' })) -Ask $ask
Check 'not due: nothing is asked of the model' $global:asked $null
Check 'not due: the outcome says so' $r 'not-due'
Check 'not due: only the begin is posted' $global:posted.Count 1
Check 'the begin names the project' ($global:posted[0][0]) '/api/discovery/projects/alpha/taste/fold/begin'
Check 'the begin names the run it rides on' ($global:posted[0][1].ticketId) 7

$global:posted = @(); $global:asked = $null
$due = [pscustomobject]@{ due = $true; reason = '10 verdicts to fold.'; claim = 'c1'; prompt = 'FOLD THESE' }
$r = Invoke-DiscoveryTasteFold -ProjectKey 'alpha' -TicketId 7 -Board (New-Board $due) -Ask $ask
Check 'due: the model is given the board''s prompt' $global:asked 'FOLD THESE'
Check 'due: the reply goes back with the claim' ($global:posted[1][1].claim) 'c1'
Check 'due: the reply is what the model said' ($global:posted[1][1].reply) '- Wants proposals at the scale of a real month (3 approved)'
Check 'due: the finish goes to the finish route' ($global:posted[1][0]) '/api/discovery/projects/alpha/taste/fold/finish'
Check 'due: folded' $r 'folded'

$global:posted = @()
$r = Invoke-DiscoveryTasteFold -ProjectKey 'alpha' -Board (New-Board $due) -Ask { param($Prompt) '' }
Check 'an empty answer is reported as an error, not a reply' ([bool]$global:posted[1][1].error) $true
Check 'and carries no reply' ($global:posted[1][1].ContainsKey('reply')) $false
Check 'an empty answer folds nothing' $r 'failed'

$global:posted = @()
$r = Invoke-DiscoveryTasteFold -ProjectKey 'alpha' -Board (New-Board $due) -Ask { param($Prompt) throw 'login expired' }
Check 'a failed model call lets the claim go with its reason' ($global:posted[1][1].error) 'login expired'
Check 'and is failed' $r 'failed'

$r = Invoke-DiscoveryTasteFold -ProjectKey 'alpha' -Board (New-Board $due -RefuseFinish) -Ask $ask
Check 'an answer the board refuses is refused, and does not throw' $r 'refused'

$r = Invoke-DiscoveryTasteFold -ProjectKey 'alpha' -Board { param($Path, $Body) throw 'connection refused' } -Ask $ask
Check 'a board that cannot be reached is a failure, not a crash' $r 'failed'
Check 'no project, nothing asked' (Invoke-DiscoveryTasteFold -ProjectKey '' -Board (New-Board $due) -Ask $ask) 'not-due'

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
