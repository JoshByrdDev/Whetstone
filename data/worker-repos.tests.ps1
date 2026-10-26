# Exercises data/worker-repos.ps1 - a ticket's workspace on a project of several repos - against
# real throwaway repositories: a folder holding three repos (each with a bare origin beside it), as
# a project's projectPath does, one of them readOnly.
#
# worker.ps1 cannot be dot-sourced, so its functions are lifted out of its syntax tree as
# worker-base.tests.ps1 does, with Write-Log collected here so a test can read what was logged.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-repos.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
$worker = Join-Path $PSScriptRoot 'worker.ps1'

$script:log = New-Object System.Collections.Generic.List[string]
function Write-Log { param([string]$Message) [void]$script:log.Add($Message) }
function Invoke-WithMutex { param([string]$Name, [scriptblock]$Body, [int]$TimeoutMs) return & $Body }

# The ticket API, as heartbeat-common.ps1 would reach it, recorded instead of sent.
$script:comments = New-Object System.Collections.Generic.List[string]
$script:phase = 'NeedsReview'
function Send-TicketComment { param($TicketId, $Body) [void]$script:comments.Add($Body) }
function Send-DashboardEvent { param($Type, $TicketId, $Project, [Alias('Error')]$Err) }
function Get-TicketPhase { param($TicketId) return $script:phase }
function Set-TicketPhase { param($TicketId, $Phase, $Reason) $script:phase = $Phase; return $true }

$parseErrors = $null
$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$parseErrors)
foreach ($fn in $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
. (Join-Path $PSScriptRoot 'worker-repos.ps1')
# The one function of providers.ps1 these tests ask, which decides the prompt's conventions file.
$providers = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'providers.ps1'), [ref]$null, [ref]$null)
foreach ($fn in $providers.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Get-ConventionsFile' }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}

$env:GIT_TERMINAL_PROMPT = '0'
$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-repos-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

function Set-Identity {
    param([string]$Dir)
    git -C $Dir config user.email 'test@example.com'
    git -C $Dir config user.name 'Test'
    git -C $Dir config commit.gpgsign false
}

function Add-Commit {
    param([string]$Dir, [string]$File, [string]$Subject)
    Add-Content -Path (Join-Path $Dir $File) -Value $Subject -Encoding ascii
    git -C $Dir add -A *> $null
    git -C $Dir commit -m $Subject *> $null
}

function Get-Sha { param([string]$Dir, [string]$Ref) return "$(git -C $Dir rev-parse --verify --quiet $Ref 2>$null)".Trim() }

# A repo at <project>\<name> with one pushed commit on main (and on any extra base named), a bare
# origin outside the project folder, and a .gitignore that keeps setup's own files out of status.
function New-Member {
    param([string]$Project, [string]$Name, [string[]]$Bases = @())
    $hub = Join-Path $Project $Name
    New-Item -ItemType Directory -Path $hub -Force | Out-Null
    git -C $hub init -b main *> $null
    Set-Identity $hub
    Set-Content -Path (Join-Path $hub '.gitignore') -Value 'setup-*.txt' -Encoding ascii
    Add-Commit -Dir $hub -File 'README.md' -Subject "init $Name"
    $origin = Join-Path (Split-Path $Project -Parent) "origins\$Name.git"
    git init --bare -b main $origin *> $null
    git -C $hub remote add origin $origin *> $null
    git -C $hub push -u origin main *> $null
    foreach ($b in $Bases) {
        git -C $hub checkout -q -b $b *> $null
        Add-Commit -Dir $hub -File "$b.txt" -Subject "only on $b"
        git -C $hub push -u origin $b *> $null
        git -C $hub checkout -q main *> $null
    }
    return $hub
}

# Someone else's commit landing on origin's <base>, as a merged PR does.
function Push-Upstream {
    param([string]$Hub, [string]$Base = 'main', [string]$Subject)
    $other = Join-Path $root ("other-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
    git clone -q -b $Base (git -C $Hub remote get-url origin) $other *> $null
    Set-Identity $other
    Add-Commit -Dir $other -File 'upstream.txt' -Subject $Subject
    git -C $other push -q origin $Base *> $null
}

function Member {
    param([string]$Dir, [string]$Hub, [string]$Base = 'main', [string[]]$Setup = @(), [switch]$ReadOnly)
    return @{ dir = $Dir; hub = $Hub; baseBranch = $Base; setup = $Setup; copy = @(); readOnly = [bool]$ReadOnly }
}

function Open-Workspace {
    param([object[]]$Members, [string]$Branch, [string]$Skill = 'speculate', [string]$Review = '', [bool]$MergeLocally = $false)
    return Resolve-Workspace -Members $Members -WorktreeRoot (Join-Path $root 'wt') -Skill $Skill -TicketId '7' `
        -RecordedBranch $Branch -IssueBranchToken '' -BranchPrefix '' -ReviewBranch $Review -MergeLocally $MergeLocally `
        -SetupTimeoutMinutes 2
}

function Find { param($Ws, [string]$Dir) return @($Ws.members | Where-Object { $_.dir -eq $Dir })[0] }
function Dirs { param([object[]]$Members) return (@($Members) | ForEach-Object { $_.dir }) -join ',' }

try {
    # ---- Get-RepoMembers ------------------------------------------------------
    Write-Host "== Get-RepoMembers"
    $single = @(Get-RepoMembers -Project ([pscustomobject]@{
        projectPath = 'C:\Repos\Alpha'; baseBranch = ''; worktreeSetup = @('npm ci'); worktreeCopyFiles = @('.env')
    }) -DefaultBase 'main')
    Check 'no repos: one member' $single.Count 1
    Check '... named nothing' $single[0].dir ''
    Check '... whose hub is projectPath' $single[0].hub 'C:\Repos\Alpha'
    Check '... with the global base when the project has none' $single[0].baseBranch 'main'
    Check '... and the project-level setup and copy' "$($single[0].setup)|$($single[0].copy)" 'npm ci|.env'
    Check 'the set of one is not several' (Test-MultiRepo $single) $false

    $several = @(Get-RepoMembers -Project ([pscustomobject]@{
        projectPath = 'C:\Repos\Alpha'; baseBranch = 'develop'; worktreeSetup = @('ignored')
        repos = @(
            [pscustomobject]@{ dir = 'alpha-web'; worktreeSetup = @('pnpm install', ''); path = 'D:\elsewhere\alpha-web' },
            [pscustomobject]@{ dir = 'alpha-api'; baseBranch = 'main' },
            [pscustomobject]@{ dir = 'alpha-infra'; readOnly = $true })
    }) -DefaultBase 'trunk')
    Check 'repos: one member each, in order' (Dirs $several) 'alpha-web,alpha-api,alpha-infra'
    Check "a member's path from the claim wins" $several[0].hub 'D:\elsewhere\alpha-web'
    Check '... else it is under projectPath' $several[1].hub 'C:\Repos\Alpha\alpha-api'
    Check "a member's base falls back to the project's" $several[0].baseBranch 'develop'
    Check '... and its own wins' $several[1].baseBranch 'main'
    Check 'empty setup entries are dropped' "$($several[0].setup)" 'pnpm install'
    Check 'project-level setup is not a member''s' $several[1].setup.Count 0
    Check 'readOnly is read' "$($several[0].readOnly),$($several[2].readOnly)" 'False,True'
    Check 'several are several' (Test-MultiRepo $several) $true
    Check 'one named repo is several too' (Test-MultiRepo @($several[1])) $true

    # ---- The set of one is Resolve-Worktree ------------------------------------
    Write-Host "== the set of one"
    $soloHub = New-Member -Project (Join-Path $root 'solo') -Name 'repo'
    $solo = Open-Workspace -Members @((Member '' $soloHub -Setup @('echo ran> setup-ran.txt'))) -Branch 'jb/solo-1'
    Check 'speculate: ok' $solo.ok $true
    Check "the workspace is the branch's worktree, where it always was" $solo.path (Join-Path (Join-Path $root 'wt') 'jb-solo-1')
    Check '... on the branch' "$(git -C $solo.path rev-parse --abbrev-ref HEAD)" 'jb/solo-1'
    Check '... set up inline, as before' (Test-Path (Join-Path $solo.path 'setup-ran.txt')) $true
    Check 'its one member carries the path' "$(@($solo.members).Count):$((@($solo.members)[0]).path)" "1:$($solo.path)"
    $noBranch = Open-Workspace -Members @((Member '' $soloHub)) -Branch 'jb/solo-2' -Skill 'implement'
    Check 'outside speculate a missing branch is still an error' ($noBranch.reason -match 'no feature branch') $true

    # ---- Several: speculate -----------------------------------------------------
    Write-Host "== several repos: speculate"
    $project = Join-Path $root 'alpha'
    $web = New-Member -Project $project -Name 'alpha-web'
    $api = New-Member -Project $project -Name 'alpha-api' -Bases @('develop')
    $infra = New-Member -Project $project -Name 'alpha-infra'

    # Each setup waits for the other's to have started. One after the other, the first would wait
    # for a setup that had not begun and give up - so passing proves they ran at the same time.
    $barrier = {
        param([string]$Mine, [string]$Theirs)
        "echo.> ..\$Mine.started& echo waiting for $Theirs& (for /l %i in (1,1,60) do @(if exist ..\$Theirs.started exit /b 0) & ping -n 2 127.0.0.1 >nul)& if not exist ..\$Theirs.started exit /b 5"
    }
    $count = 'echo.>> setup-count.txt'
    $members = @(
        (Member 'alpha-web' $web -Setup @((& $barrier 'web' 'api'), $count)),
        (Member 'alpha-api' $api -Base 'develop' -Setup @((& $barrier 'api' 'web'), $count)),
        (Member 'alpha-infra' $infra -ReadOnly))
    $script:log.Clear()
    $started = Get-Date
    $ws = Open-Workspace -Members $members -Branch 'jb/PROJ-1'
    $took = ((Get-Date) - $started).TotalSeconds
    Check 'speculate: ok' $ws.ok $true
    if (-not $ws.ok) { Write-Host "      $($ws.reason)" -ForegroundColor Red }
    Check 'the workspace is named after the branch' $ws.path (Join-Path (Join-Path $root 'wt') 'jb-PROJ-1')
    Check 'every member is in it, in order' (Dirs $ws.members) 'alpha-web,alpha-api,alpha-infra'
    $wWeb = (Find $ws 'alpha-web').path; $wApi = (Find $ws 'alpha-api').path; $wInfra = (Find $ws 'alpha-infra').path
    Check "each under its own folder name" "$wWeb|$wApi|$wInfra" "$(Join-Path $ws.path 'alpha-web')|$(Join-Path $ws.path 'alpha-api')|$(Join-Path $ws.path 'alpha-infra')"
    Check 'web is on the branch' "$(git -C $wWeb rev-parse --abbrev-ref HEAD)" 'jb/PROJ-1'
    Check 'api is on the branch' "$(git -C $wApi rev-parse --abbrev-ref HEAD)" 'jb/PROJ-1'
    Check '... forked from its own base, not main' (Get-Sha $wApi HEAD) (Get-Sha $api origin/develop)
    Check 'web forked from main' (Get-Sha $wWeb HEAD) (Get-Sha $web origin/main)
    Check 'the readOnly member is detached' "$(git -C $wInfra symbolic-ref --quiet HEAD 2>$null)" ''
    Check '... at its base' (Get-Sha $wInfra HEAD) (Get-Sha $infra origin/main)
    Check '... and its hub has no such branch' "$(Test-BranchRef -RepoDir $infra -Name 'jb/PROJ-1')" ''
    Check 'setup ran in both, at the same time' ((Test-Path (Join-Path $wWeb 'setup-count.txt')) -and (Test-Path (Join-Path $wApi 'setup-count.txt'))) $true
    Check "... each job's lines carrying its repo's name" (@($script:log | Where-Object { $_ -match '\[alpha-web\].*waiting for api' }).Count -gt 0) $true
    Check '... and the other''s' (@($script:log | Where-Object { $_ -match '\[alpha-api\].*waiting for web' }).Count -gt 0) $true
    Check '... well inside the time either would have waited alone' ($took -lt 90) $true
    Check 'nothing is touched yet' (Dirs (Get-TouchedMembers -Members $ws.members)) ''

    # ---- Several: a later phase -------------------------------------------------
    Write-Host "== several repos: a later phase"
    Add-Commit -Dir $wWeb -File 'feature.txt' -Subject 'web: the feature'
    Push-Upstream -Hub $api -Base 'develop' -Subject 'api: merged by someone else'
    Push-Upstream -Hub $web -Subject 'web: merged by someone else'
    Push-Upstream -Hub $infra -Subject 'infra: merged by someone else'
    $webBefore = Get-Sha $wWeb HEAD

    $members[0].setup = @($count); $members[1].setup = @($count)
    $again = Open-Workspace -Members $members -Branch 'jb/PROJ-1' -Skill 'implement'
    Check 'implement: ok' $again.ok $true
    if (-not $again.ok) { Write-Host "      $($again.reason)" -ForegroundColor Red }
    Check 'the same workspace' $again.path $ws.path
    Check 'nothing was created this time' $again.created $false
    Check 'a touched member is not moved' (Get-Sha $wWeb HEAD) $webBefore
    Check '... and not set up again' @(Get-Content (Join-Path $wWeb 'setup-count.txt')).Count 1
    Check 'an untouched member is brought up to its base' (Get-Sha $wApi HEAD) (Get-Sha $api origin/develop)
    Check '... and set up again, since its lockfile may have moved' @(Get-Content (Join-Path $wApi 'setup-count.txt')).Count 2
    Check 'the readOnly member follows its base' (Get-Sha $wInfra HEAD) (Get-Sha $infra origin/main)
    Check 'touched: only web' (Dirs (Get-TouchedMembers -Members $again.members)) 'alpha-web'

    Set-Content -Path (Join-Path $wApi 'new-file.cs') -Value 'x' -Encoding ascii
    Check 'an untracked file touches a member' (Dirs (Get-TouchedMembers -Members $again.members)) 'alpha-web,alpha-api'
    Remove-Item (Join-Path $wApi 'new-file.cs')
    Check "setup's own ignored files do not" (Dirs (Get-TouchedMembers -Members $again.members)) 'alpha-web'

    # A repo added to "repos" mid-ticket joins with the branch, from its base.
    $docs = New-Member -Project $project -Name 'alpha-docs'
    $withDocs = @($members) + @((Member 'alpha-docs' $docs))
    $joined = Open-Workspace -Members $withDocs -Branch 'jb/PROJ-1' -Skill 'refine'
    Check 'a member added mid-ticket: ok' $joined.ok $true
    $wDocs = (Find $joined 'alpha-docs').path
    Check '... on the branch' "$(git -C $wDocs rev-parse --abbrev-ref HEAD)" 'jb/PROJ-1'
    Check '... at its base' (Get-Sha $wDocs HEAD) (Get-Sha $docs origin/main)
    Check '... untouched' (Dirs (Get-TouchedMembers -Members $joined.members)) 'alpha-web'

    # Work that has already landed upstream is nothing to deliver: its PR merged, as a merge commit.
    $merger = Join-Path $root 'merger'
    git clone -q (git -C $web remote get-url origin) $merger *> $null
    Set-Identity $merger
    git -C $merger fetch -q $web 'jb/PROJ-1' *> $null
    git -C $merger merge -q --no-ff -m 'Merged PR: the feature' FETCH_HEAD *> $null
    git -C $merger push -q origin main *> $null
    git -C $web fetch -q origin *> $null
    Check 'a branch whose commits are all in its base is untouched' (Dirs (Get-TouchedMembers -Members $joined.members)) ''

    # ---- The readOnly fault ------------------------------------------------------
    Write-Host "== a readOnly member with changes"
    Add-Content -Path (Join-Path $wInfra 'README.md') -Value 'edited' -Encoding ascii
    Check 'it is a fault' (Dirs (Get-ReadOnlyFaults -Members $joined.members)) 'alpha-infra'
    Check '... and never touched' (Dirs (Get-TouchedMembers -Members $joined.members)) ''
    $faulted = Open-Workspace -Members $withDocs -Branch 'jb/PROJ-1' -Skill 'implement'
    Check 'the next phase refuses to start' $faulted.ok $false
    Check '... naming the repo' ($faulted.reason -match '^alpha-infra: the read-only repo') $true
    Check '... as a fault' $faulted.fault $true
    git -C $wInfra checkout -q -- README.md *> $null
    Check 'cleaned by hand, it starts again' (Open-Workspace -Members $withDocs -Branch 'jb/PROJ-1' -Skill 'implement').ok $true

    # ---- What it refuses -----------------------------------------------------------
    Write-Host "== refusals"
    $none = Open-Workspace -Members $members -Branch 'jb/PROJ-2' -Skill 'implement'
    Check 'outside speculate, no member with the branch is an error' ($none.reason -match "no feature branch 'jb/PROJ-2' in any") $true
    Check 'a review is refused' ((Open-Workspace -Members $members -Branch 'x' -Review 'someone/theirs').reason -match 'Review track') $true
    Check 'a local merge is refused' ((Open-Workspace -Members $members -Branch 'x' -MergeLocally $true).reason -match 'mergeInsteadOfPr') $true

    $elsewhere = Join-Path $root 'elsewhere-web'
    git -C $web worktree move $wWeb $elsewhere *> $null
    $moved = Open-Workspace -Members $members -Branch 'jb/PROJ-1' -Skill 'implement'
    Check 'a member checked out outside the workspace is refused' ($moved.reason -match "^alpha-web: branch 'jb/PROJ-1' is checked out at .*outside this ticket's workspace") $true
    git -C $web worktree move $elsewhere $wWeb *> $null

    # ---- Setup that fails -----------------------------------------------------------
    Write-Host "== setup that fails"
    $bad = @(
        (Member 'alpha-web' $web -Setup @('echo web broke& exit /b 3')),
        (Member 'alpha-api' $api -Base 'develop' -Setup @('echo api broke& exit /b 4')),
        (Member 'alpha-infra' $infra -ReadOnly))
    $broken = Open-Workspace -Members $bad -Branch 'jb/PROJ-3'
    Check 'both failures block it' $broken.ok $false
    Check '... named together' (($broken.reason -split "`n")[0]) 'worktree setup failed in alpha-web, alpha-api'
    Check "... each with its own command and output" (($broken.reason -match 'alpha-web: worktree setup command failed \(exit 3\)') -and ($broken.reason -match 'api broke')) $true

    $fixed = @(
        (Member 'alpha-web' $web -Setup @($count)),
        (Member 'alpha-api' $api -Base 'develop' -Setup @($count)),
        (Member 'alpha-infra' $infra -ReadOnly))
    $retry = Open-Workspace -Members $fixed -Branch 'jb/PROJ-3' -Skill 'implement'
    Check 'the next run sets up what failed' $retry.ok $true
    Check '... in both' ((Test-Path (Join-Path (Find $retry 'alpha-web').path 'setup-count.txt')) -and (Test-Path (Join-Path (Find $retry 'alpha-api').path 'setup-count.txt'))) $true

    # ---- The prompt -------------------------------------------------------------------
    Write-Host "== the prompt"
    Check 'worker.ps1 still parses' (@($tree.FindAll({ $true }, $false)).Count -gt 0 -and $parseErrors.Count -eq 0) $true
    $template = Get-Content -Path (Join-Path $PSScriptRoot 'heartbeat-prompt.md') -Raw
    Check 'the base-ref line takes its backticks from the worker' ($template -match '- \*\*Base ref:\*\* \{\{BASE_REF\}\} - the ref') $true
    Check '... and ends in the repos note, so an empty one adds nothing' ($template -match 'after you finish\.\{\{REPOS_NOTE\}\}\r?\n') $true
    Check 'a project of one has no repos note' "[$(Format-ReposNote -Members @((Member '' $soloHub)) -Branch 'jb/solo-1')]" '[]'
    $note = Format-ReposNote -Members $retry.members -Branch 'jb/PROJ-3'
    Check 'several: the note opens a bullet of its own' ($note -match "^`n- \*\*Repos:\*\*") $true
    Check "... naming each repo's own base ref" (($note -match '`alpha-web` - base ref `origin/main`') -and ($note -match '`alpha-api` - base ref `origin/develop`')) $true
    Check '... and the read-only one as read-only' ($note -match '`alpha-infra` - \*\*read-only\*\*') $true
    Check '... and the branch they share' ($note -match 'on branch `jb/PROJ-3`') $true

    # ---- Workspace files ----------------------------------------------------------------
    Write-Host "== workspace files"
    $kit = Join-Path $root 'kit'
    New-Item -ItemType Directory -Path (Join-Path $kit 'docs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $kit 'alpha-web') -Force | Out-Null
    Set-Content -Path (Join-Path $kit 'CLAUDE.md') -Value '# the workspace' -Encoding ascii
    Set-Content -Path (Join-Path $kit 'docs\map.md') -Value 'map' -Encoding ascii
    Set-Content -Path (Join-Path $kit 'alpha-web\stray.txt') -Value 'x' -Encoding ascii
    $script:log.Clear()
    $n = Copy-WorkspaceFiles -From $kit -To $retry.path -Skip @('alpha-web', 'alpha-api', 'alpha-infra')
    Check 'the root files are copied, nested ones too' "$n $(Test-Path (Join-Path $retry.path 'CLAUDE.md')) $(Test-Path (Join-Path $retry.path 'docs\map.md'))" '2 True True'
    Check '... never into a repo' (Test-Path (Join-Path (Find $retry 'alpha-web').path 'stray.txt')) $false
    Check '... and the skip is logged' (@($script:log | Where-Object { $_ -match "stray.txt' is inside the repo 'alpha-web'" }).Count) 1
    Check 'a conventions file at the root is found there' (Get-ConventionsFile -Provider @{ ConventionsFile = 'CLAUDE.md' } -WorkDir $retry.path) 'CLAUDE.md'
    Check 'no kit, nothing copied' (Copy-WorkspaceFiles -From (Join-Path $root 'no-kit') -To $retry.path) 0

    # ---- The hand-off, per repo ------------------------------------------------------------
    # Invoke-HandOffNets as worker.ps1 calls it, with the script variables it reads.
    Write-Host "== the hand-off, per repo"
    $ticketId = '7'; $title = 'The feature'; $skill = 'refine'; $mergeLocally = $false; $onHost = $false
    $Claim = [pscustomobject]@{ issueCommitRef = 'AB#1'; ticket = [pscustomobject]@{ implementationSummary = 'Adds the feature.' } }
    $handoff = @(
        (Member 'alpha-web' $web),
        (Member 'alpha-api' $api -Base 'develop'),
        (Member 'alpha-infra' $infra -ReadOnly))
    $h = Open-Workspace -Members $handoff -Branch 'jb/PROJ-4'
    $hWeb = (Find $h 'alpha-web').path; $hApi = (Find $h 'alpha-api').path
    Add-Commit -Dir $hWeb -File 'one.txt' -Subject 'web: part one'
    Add-Commit -Dir $hWeb -File 'two.txt' -Subject 'web: part two'
    Set-Content -Path (Join-Path $hWeb 'left.txt') -Value 'forgotten' -Encoding ascii
    Push-Upstream -Hub $web -Subject 'web: landed meanwhile'
    git -C $web fetch -q origin *> $null
    $apiBefore = Get-Sha $hApi HEAD
    $script:comments.Clear(); $script:phase = 'NeedsReview'; $script:handOffSentBack = $false
    foreach ($m in @($h.members | Where-Object { -not $_.readOnly })) {
        Invoke-HandOffNets -WorkDir $m.path -RepoDir $m.hub -EffectiveBase $m.baseBranch -Label $m.dir -Member $m
    }
    Check 'the leftover file is committed' "$(git -C $hWeb status --porcelain)" ''
    Check '... and squashed with the rest into one commit' "$(git -C $hWeb rev-list --count origin/main..HEAD)" 1
    Check '... rebased onto the base that moved' "$(git -C $hWeb merge-base --is-ancestor origin/main HEAD; $LASTEXITCODE)" 0
    Check "every comment names the repo it is about" (@($script:comments | Where-Object { $_ -notmatch '^`alpha-web`: ' }).Count) 0
    Check '... and there are three: leftovers, squash, rebase' $script:comments.Count 3
    Check 'the untouched repo is left alone' (Get-Sha $hApi HEAD) $apiBefore

    # Both writable repos conflict with their bases: the first sends the ticket back, the second says so.
    $c = Open-Workspace -Members $handoff -Branch 'jb/PROJ-5'
    foreach ($pair in @(@($web, 'main', (Find $c 'alpha-web').path), @($api, 'develop', (Find $c 'alpha-api').path))) {
        Add-Commit -Dir $pair[2] -File 'README.md' -Subject 'the ticket edits the readme'
        $other = Join-Path $root ("clash-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
        git clone -q -b $pair[1] (git -C $pair[0] remote get-url origin) $other *> $null
        Set-Identity $other
        Add-Commit -Dir $other -File 'README.md' -Subject 'so did someone else'
        git -C $other push -q origin $pair[1] *> $null
        git -C $pair[0] fetch -q origin *> $null
    }
    $script:comments.Clear(); $script:phase = 'NeedsReview'; $script:handOffSentBack = $false
    foreach ($m in @($c.members | Where-Object { -not $_.readOnly })) {
        Invoke-HandOffNets -WorkDir $m.path -RepoDir $m.hub -EffectiveBase $m.baseBranch -Label $m.dir -Member $m
    }
    Check 'a conflict sends the ticket back' $script:phase 'NeedsImplementation'
    Check '... named for the repo that conflicted first' (@($script:comments | Where-Object { $_ -match '^`alpha-web`: .*going back for another implementation pass' }).Count) 1
    Check "the second repo's conflict does not blame refinement" (@($script:comments | Where-Object { $_ -match '^`alpha-api`: .*Another repo of this ticket already sent it back' }).Count) 1

    # A project of one: no label, and every comment reads exactly as it always has.
    $soloWs = Open-Workspace -Members @((Member '' $soloHub)) -Branch 'jb/solo-1' -Skill 'refine'
    Add-Commit -Dir $soloWs.path -File 'a.txt' -Subject 'solo: one'
    Add-Commit -Dir $soloWs.path -File 'b.txt' -Subject 'solo: two'
    $script:comments.Clear()
    Invoke-HandOffNets -WorkDir $soloWs.path -RepoDir $soloHub -EffectiveBase 'main'
    Check 'a project of one: squashed' "$(git -C $soloWs.path rev-list --count origin/main..HEAD)" 1
    Check '... with the comment it always had' (@($script:comments)[0] -match '^Refinement finished without tidying the branch history') $true
}
catch {
    # A check that throws skips every check after it, which would otherwise read as a pass.
    $script:fail++
    Write-Host "FAIL  the tests stopped early: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue
    # Worktree .git files are read-only on Windows; -Force is what removes them.
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
