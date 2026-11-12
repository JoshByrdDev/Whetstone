# Exercises what a worker run records about itself beside its tokens (New-RunDetail and the cycle
# report's `detail`): the hashes that tell one skill or prompt version from the next, the size of
# the branch it leaves, the session id and CLI version from the stream, and that a report sends only
# the parts a run learned.
#
# worker.ps1 cannot be dot-sourced - it is a script that works a claim - so its functions are lifted
# out of its syntax tree, as in worker-base.tests.ps1.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-tracking.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'providers.ps1')

function Write-Log { param([string]$Message) }
function ConvertTo-JsonBody { param([string]$Json) return $Json }
$JsonContentType = 'application/json'

$worker = Join-Path $PSScriptRoot 'worker.ps1'
$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$null)
foreach ($name in @('New-RunDetail', 'Get-TextHash', 'Get-BranchChangeStats', 'Get-RunCommitRange', 'Send-CycleReport')) {
    $fn = $tree.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $false)
    if (-not $fn) { throw "$name not found in worker.ps1" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

# The report's POST, caught instead of sent.
$script:posted = @()
function Invoke-RestMethod { param($Uri, $Method, $ContentType, $Body, $TimeoutSec) $script:posted += ,@{ uri = $Uri; body = $Body } }

$env:GIT_TERMINAL_PROMPT = '0'
$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-tracking-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

function New-Repo {
    param([string]$Name)
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    git -C $dir init -b main *> $null
    git -C $dir config user.email 'test@example.com'
    git -C $dir config user.name 'Test'
    git -C $dir config commit.gpgsign false
    Set-Content -Path (Join-Path $dir 'README.md') -Value "one`ntwo`nthree" -Encoding ascii
    git -C $dir add -A *> $null
    git -C $dir commit -m init *> $null
    return $dir
}

try {
    Write-Host "== hashes"
    Check 'twelve hex digits' ((Get-TextHash 'abc') -match '^[0-9a-f]{12}$') $true
    Check 'the same text, the same hash' (Get-TextHash 'skill v1') (Get-TextHash 'skill v1')
    Check 'one character changes it' ((Get-TextHash 'skill v1') -ne (Get-TextHash 'skill v2')) $true
    Check 'SHA-256 of UTF-8' (Get-TextHash 'abc') 'ba7816bf8f01'
    Check 'no text, no hash' ($null -eq (Get-TextHash '')) $true

    Write-Host "== the branch's size"
    $a = New-Repo 'alpha'
    git -C $a switch -c PROJ-123 *> $null
    Set-Content -Path (Join-Path $a 'README.md') -Value "one`nTWO`nthree`nfour" -Encoding ascii
    New-Item -ItemType Directory -Path (Join-Path $a 'src') | Out-Null
    Set-Content -Path (Join-Path $a 'src\new file.cs') -Value "a`nb" -Encoding ascii
    [IO.File]::WriteAllBytes((Join-Path $a 'logo.bin'), [byte[]](0, 1, 2, 0, 255))
    git -C $a add -A *> $null
    git -C $a commit -m work *> $null
    # A commit on the base after the fork is not the branch's.
    git -C $a switch main *> $null
    Set-Content -Path (Join-Path $a 'other.txt') -Value 'base moved' -Encoding ascii
    git -C $a add -A *> $null
    git -C $a commit -m 'base moved' *> $null
    git -C $a switch PROJ-123 *> $null

    $one = Get-BranchChangeStats -Places @(@{ path = $a; baseRef = 'main'; dir = $null })
    Check 'files the branch changed, not the base' $one.files 3
    Check 'lines added (a binary file adds none)' $one.added 4
    Check 'lines removed' $one.removed 1
    $sorted = [string[]]@($one.paths); [Array]::Sort($sorted, [StringComparer]::Ordinal)
    Check 'paths as git names them, spaces and all' ($sorted -join '|') 'README.md|logo.bin|src/new file.cs'

    $b = New-Repo 'bravo'
    git -C $b switch -c PROJ-123 *> $null
    Add-Content -Path (Join-Path $b 'README.md') -Value 'more' -Encoding ascii
    git -C $b commit -am more *> $null
    $two = Get-BranchChangeStats -Places @(@{ path = $a; baseRef = 'main'; dir = 'alpha' }, @{ path = $b; baseRef = 'main'; dir = 'bravo' })
    Check 'several repos are summed' "$($two.files) $($two.added) $($two.removed)" '4 5 1'
    Check '... and each path starts with its repo' ((@($two.paths) -contains 'bravo/README.md') -and (@($two.paths) -contains 'alpha/src/new file.cs')) $true

    $c = New-Repo 'charlie'
    $none = Get-BranchChangeStats -Places @(@{ path = $c; baseRef = 'main'; dir = $null })
    Check 'a branch with nothing on it measures as nothing' "$($none.files) $($none.added) $($none.removed) $(@($none.paths).Count)" '0 0 0 0'
    Check 'a base that does not exist measures as unknown' ($null -eq (Get-BranchChangeStats -Places @(@{ path = $c; baseRef = 'no-such-ref'; dir = $null }))) $true
    Check 'no places, unknown' ($null -eq (Get-BranchChangeStats -Places @())) $true

    Write-Host "== the cycle report's detail"
    $DashboardUrl = 'http://localhost:1'
    $detail = New-RunDetail -Provider 'claude' -Effort ''
    Check 'a new detail knows its provider' $detail.provider 'claude'
    Check '... and an unset effort is unknown, not empty' ($null -eq $detail.effort) $true
    Check 'a run with no worker host ran on the board host' "$($detail.host) $($null -eq $detail.slot)" '@board True'
    $remote = New-RunDetail -Provider 'claude' -Effort 'high' -RunHost 'bravo-host' -Slot 12
    Check '... and one on a worker host names it, with its berth' "$($remote.host) $($remote.slot)" 'bravo-host 12'
    Check 'a run names its ticket lease, which the QA ruling log credits' (New-RunDetail -Provider 'claude' -RunId 41).runId 41
    Check '... and a run with none sends none' ($null -eq $detail.runId) $true
    $detail.skillHash = 'abc123abc123'; $detail.prepSeconds = 12.5; $detail.filesChanged = 0; $detail.changedFiles = @('a.cs', 'b.cs')
    $script:posted = @()
    Send-CycleReport -TicketId '7' -Project 'alpha' -Phase 'Speculating' -Skill 'speculate' -Resumed $false -Model 'm' `
        -StartedAt (Get-Date) -DurationSeconds 30 -Mutations 1 -Outcome 'Completed' -Detail $detail
    Check 'one report sent' $script:posted.Count 1
    $sent = $script:posted[0].body | ConvertFrom-Json
    Check 'the parts it learned are sent' "$($sent.detail.provider) $($sent.detail.skillHash) $($sent.detail.prepSeconds)" 'claude abc123abc123 12.5'
    Check '... a zero is a fact, and is sent' $sent.detail.filesChanged 0
    Check '... the file list as a list' (@($sent.detail.changedFiles) -join ',') 'a.cs,b.cs'
    Check '... the parts it never learned are not' (@($sent.detail.PSObject.Properties.Name | Where-Object { $_ -in 'effort', 'sessionId', 'startupSeconds' }).Count) 0

    $script:posted = @()
    $empty = New-RunDetail; $empty.host = $null
    Send-CycleReport -TicketId '7' -Project 'alpha' -Phase 'Speculating' -Skill 'speculate' -Resumed $false -Model 'm' `
        -StartedAt (Get-Date) -DurationSeconds 1 -Mutations 0 -Outcome 'Error' -Detail $empty
    Check 'a detail with nothing in it is left out' ($null -eq ($script:posted[0].body | ConvertFrom-Json).detail) $true

    Write-Host "== the run's own commits"
    $own = New-Repo 'own'
    $start = "$(git -C $own rev-parse HEAD)".Trim()
    Check 'a run that committed nothing has no range' ($null -eq (Get-RunCommitRange -Places @(@{ path = $own; dir = $null; startHead = $start }))) $true
    Set-Content -Path (Join-Path $own 'a.cs') -Value 'x' -Encoding ascii
    git -C $own add -A *> $null; git -C $own commit -m one *> $null
    Set-Content -Path (Join-Path $own 'b.cs') -Value 'y' -Encoding ascii
    git -C $own add -A *> $null; git -C $own commit -m two *> $null
    $end = "$(git -C $own rev-parse HEAD)".Trim()
    $r = Get-RunCommitRange -Places @(@{ path = $own; dir = $null; startHead = $start })
    Check 'the range runs from the start head to the end head' "$($r.ranges[0].from) $($r.ranges[0].to)" "$start $end"
    Check '... with no repo on a project of one' $r.ranges[0].repo ''
    Check '... and the files its commits changed' (@($r.files) -join ',') 'a.cs,b.cs'
    $r2 = Get-RunCommitRange -Places @(@{ path = $own; dir = 'alpha-api'; startHead = $start })
    Check 'a repo of several names itself, and prefixes its files' "$($r2.ranges[0].repo) $(@($r2.files) -join ',')" 'alpha-api alpha-api/a.cs,alpha-api/b.cs'
    $d = New-RunDetail -Provider 'claude'
    $d.commitRanges = @($r.ranges); $d.runFiles = @($r.files)
    $script:posted = @()
    Send-CycleReport -TicketId '7' -Project 'alpha' -Phase 'Implementing' -Skill 'implement' -Resumed $false -Model 'm' `
        -StartedAt (Get-Date) -DurationSeconds 1 -Mutations 0 -Outcome 'Completed' -Detail $d
    $sentRange = ($script:posted[0].body | ConvertFrom-Json).detail
    Check 'the report carries the range' "$($sentRange.commitRanges[0].from)" $start
    Check '... and the run files as a list' (@($sentRange.runFiles) -join ',') 'a.cs,b.cs'

    Write-Host "== the session, from the stream"
    $claude = Get-AgentProvider -Key 'claude'
    $init = '{"type":"system","subtype":"init","session_id":"5f1c2a","claude_code_version":"2.9.1","mcp_servers":[{"name":"tickets","status":"connected"}],"tools":["mcp__tickets__get_ticket"]}' | ConvertFrom-Json
    $ev = @(ConvertFrom-AgentEvent -Provider $claude -Event $init -State @{}) | Where-Object { $_.Kind -eq 'session_start' }
    Check 'the session id rides on session_start' $ev.SessionId '5f1c2a'
    Check '... and the CLI version' $ev.CliVersion '2.9.1'
    $bare = '{"type":"system","subtype":"init","mcp_servers":[],"tools":[]}' | ConvertFrom-Json
    $ev2 = @(ConvertFrom-AgentEvent -Provider $claude -Event $bare -State @{}) | Where-Object { $_.Kind -eq 'session_start' }
    Check 'a CLI that leaves them out leaves them unknown' "$($null -eq $ev2.SessionId) $($null -eq $ev2.CliVersion)" 'True True'

    Write-Host "== the context window and compactions, from the stream"
    $compact = '{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":178067}}' | ConvertFrom-Json
    $c = @(ConvertFrom-AgentEvent -Provider $claude -Event $compact -State @{}) | Where-Object { $_.Kind -eq 'compaction' }
    Check 'a compact boundary is a compaction' @($c).Count 1
    Check '... with the size it compacted at' $c.PreTokens 178067
    $subCompact = '{"type":"system","subtype":"compact_boundary","parent_tool_use_id":"toolu_1","compact_metadata":{"pre_tokens":9}}' | ConvertFrom-Json
    Check "a sub-agent's compaction is not the run's" @(@(ConvertFrom-AgentEvent -Provider $claude -Event $subCompact -State @{}) | Where-Object { $_.Kind -eq 'compaction' }).Count 0
    # The main model read the most; the small one on the side has a smaller window and must not win.
    $result = '{"type":"result","num_turns":3,"usage":{},"modelUsage":{"small-model":{"inputTokens":10,"cacheReadInputTokens":0,"cacheCreationInputTokens":900,"contextWindow":200000},"main-model":{"inputTokens":5,"cacheReadInputTokens":800000,"cacheCreationInputTokens":40000,"contextWindow":1000000}}}' | ConvertFrom-Json
    $r = @(ConvertFrom-AgentEvent -Provider $claude -Event $result -State @{}) | Where-Object { $_.Kind -eq 'result' }
    Check "the window is the model that read the most" $r.ContextWindow 1000000
    $noUsage = '{"type":"result","num_turns":1,"usage":{}}' | ConvertFrom-Json
    $r2 = @(ConvertFrom-AgentEvent -Provider $claude -Event $noUsage -State @{}) | Where-Object { $_.Kind -eq 'result' }
    Check 'a result without modelUsage leaves the window unknown' ($null -eq $r2.ContextWindow) $true

    $script:posted = @()
    Send-CycleReport -TicketId '7' -Project 'alpha' -Phase 'QaTesting' -Skill 'qa-test' -Resumed $false -Model 'm' `
        -StartedAt (Get-Date) -DurationSeconds 1 -Mutations 0 -Outcome 'Completed' `
        -PeakContextTokens 486000 -ContextWindowTokens 1000000 -Compactions 0
    $sent = $script:posted[0].body | ConvertFrom-Json
    Check 'the report carries the window' $sent.contextWindowTokens 1000000
    Check '... and a zero compaction count, which is a fact' $sent.compactions 0
    $script:posted = @()
    Send-CycleReport -TicketId '7' -Project 'alpha' -Phase 'QaTesting' -Skill 'qa-test' -Resumed $false -Model 'm' `
        -StartedAt (Get-Date) -DurationSeconds 1 -Mutations 0 -Outcome 'Completed'
    $sent2 = $script:posted[0].body | ConvertFrom-Json
    Check 'a run that knows neither sends neither' "$($null -eq $sent2.PSObject.Properties['contextWindowTokens']) $($null -eq $sent2.PSObject.Properties['compactions'])" 'True True'
}
finally {
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
