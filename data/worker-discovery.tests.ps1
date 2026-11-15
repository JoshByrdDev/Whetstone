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

# --- the tools --------------------------------------------------------------------------------
Check 'claude chart has no shell or editor' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'chart') -join ' ') '--tools Read,Grep,Glob'
Check 'claude sift has no shell or editor' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'sift') -join ' ') '--tools Read,Grep,Glob'
Check 'claude scout also loses browser_run_code_unsafe' ((Get-DiscoveryToolArgs -Provider 'claude' -Skill 'scout') -join ' ') '--tools Read,Grep,Glob --disallowedTools mcp__playwright__browser_run_code_unsafe'
Check 'codex has no switch, so nothing is added' (@(Get-DiscoveryToolArgs -Provider 'codex' -Skill 'scout').Count) 0

# --- the note ---------------------------------------------------------------------------------
$scout = Format-DiscoveryNote -Skill 'scout' -Commit 'abc123' -Site 'http://localhost:53590' -RunDir '/data/discovery/7/runs/x' `
    -Areas @(@{ name = 'events'; pages = @('/events/*') }, @{ name = 'account'; pages = @() })
Check 'the scout is told its commit' ($scout -match 'commit `abc123`') $true
Check 'the scout is told its site' ($scout -match 'up at http://localhost:53590') $true
Check 'the scout is told where shots go' ($scout -match '/data/discovery/7/runs/x') $true
Check 'areas are listed with their pages' ($scout -match '`events` \(/events/\*\); `account`') $true
Check 'the scout is not told a proposal cap' ($scout -match 'at most') $false
$sift = Format-DiscoveryNote -Skill 'sift' -Commit 'abc123' -MaxProposals 4
Check 'the sift is told the cap' ($sift -match 'at most 4 proposals') $true
Check 'the sift is not told a site' ($sift -match 'up at') $false
Check 'nothing to say is empty' (Format-DiscoveryNote -Skill 'chart' -Commit '') ''

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
