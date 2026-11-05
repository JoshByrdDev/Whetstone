# Exercises the under-PR-review half of data/worker.ps1 against real throwaway repositories: a
# ticket whose branch is in a pull request someone has reviewed keeps the reviewed commits byte for
# byte, adds one commit per round on top, folds a later bounce in the same round into that commit,
# and is never rebased - only asked whether it still merges.
#
# worker.ps1 cannot be dot-sourced - it is a script that works a claim - so its functions are lifted
# out of its syntax tree and defined here, with what they borrow from heartbeat-common.ps1 stubbed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/worker-review.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
$worker = Join-Path $PSScriptRoot 'worker.ps1'

function Write-Log { param([string]$Message) }
function Invoke-WithMutex { param([string]$Name, [scriptblock]$Body, [int]$TimeoutMs) return & $Body }

$tree = [System.Management.Automation.Language.Parser]::ParseFile($worker, [ref]$null, [ref]$null)
foreach ($fn in $tree.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
. (Join-Path $PSScriptRoot 'worker-repos.ps1')

$env:GIT_TERMINAL_PROMPT = '0'
$root = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-review-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

function Add-Commit {
    param([string]$Dir, [string]$File, [string]$Subject, [string]$Body = '')
    Add-Content -Path (Join-Path $Dir $File) -Value $Subject -Encoding ascii
    git -C $Dir add -A *> $null
    if ($Body) { git -C $Dir commit -m $Subject -m $Body *> $null } else { git -C $Dir commit -m $Subject *> $null }
}

function Get-Sha { param([string]$Dir, [string]$Ref) return "$(git -C $Dir rev-parse $Ref 2>$null)".Trim() }

# A hub with a pushed main, and a ticket branch with one reviewed commit pushed to origin - what a
# pull request under review looks like on this machine. Returns the hub, its worktree, and the sha.
function New-Reviewed {
    param([string]$Name)
    $dir = Join-Path $root $Name
    $hub = Join-Path $dir 'hub'
    $origin = Join-Path $dir 'origin.git'
    New-Item -ItemType Directory -Path $hub -Force | Out-Null
    git -C $hub init -b main *> $null
    git -C $hub config user.email 'test@example.com'
    git -C $hub config user.name 'Test'
    git -C $hub config commit.gpgsign false
    Add-Commit -Dir $hub -File 'README.md' -Subject 'init'
    git init --bare -b main $origin *> $null
    git -C $hub remote add origin $origin *> $null
    git -C $hub push -u origin main *> $null

    $wt = Join-Path $dir 'wt'
    git -C $hub worktree add -b 'jb/PROJ-1' $wt 'origin/main' *> $null
    git -C $wt branch --unset-upstream *> $null
    Add-Commit -Dir $wt -File 'feature.txt' -Subject 'PROJ-1: Add export button' -Body 'The reviewed change.'
    git -C $wt push origin 'jb/PROJ-1' *> $null
    return @{ hub = $hub; wt = $wt; reviewed = (Get-Sha $wt HEAD) }
}

function Compress { param($R, [string]$Seen = $R.reviewed)
    return Compress-ReviewRound -WorkDir $R.wt -ReviewedSha $Seen -Title 'Add export button' -IssueRef 'PROJ-1' -BaseBranch 'main' -MergeLocally $false
}

try {
    # ---- Reading reviewedOn ---------------------------------------------------
    Write-Host "== Get-ReviewedSha"
    Check 'one repo: a bare sha is its own' (Get-ReviewedSha -ReviewedOn 'abc1234' -Dir '') 'abc1234'
    Check 'several: each repo its own' (Get-ReviewedSha -ReviewedOn 'alpha-web@aaa1111, alpha-api@bbb2222' -Dir 'alpha-api') 'bbb2222'
    Check 'several: matched without regard to case' (Get-ReviewedSha -ReviewedOn 'alpha-web@aaa1111' -Dir 'Alpha-Web') 'aaa1111'
    Check 'a repo the reviewer never saw has none' "[$(Get-ReviewedSha -ReviewedOn 'alpha-web@aaa1111' -Dir 'alpha-api')]" '[]'
    Check 'not under review -> none' "[$(Get-ReviewedSha -ReviewedOn '' -Dir '')]" '[]'

    Write-Host "== Format-ReviewNote"
    Check 'not under review -> no note' "[$(Format-ReviewNote -ReviewedOn '')]" '[]'
    $note = Format-ReviewNote -ReviewedOn 'abc1234'
    Check 'under review: a bullet of its own' ($note -match "^`n- \*\*Under PR review:\*\*") $true
    Check '... naming what the reviewer saw' ($note -match '`abc1234`') $true
    Check '... and one commit per round, amended within it' (($note -match 'ONE new commit') -and ($note -match 'amends it')) $true
    $template = Get-Content -Path (Join-Path $PSScriptRoot 'heartbeat-prompt.md') -Raw
    Check 'the prompt carries the note on the base-ref line' ($template -match '\{\{REPOS_NOTE\}\}\{\{REVIEW_NOTE\}\}') $true

    # ---- A round ----------------------------------------------------------------
    Write-Host "== a round on top of the reviewed commit"
    $r = New-Reviewed 'round'
    Check 'no round yet -> nothing to do' "[$(Compress $r)]" '[]'

    Add-Commit -Dir $r.wt -File 'feature.txt' -Subject 'PROJ-1: Address review - name the export file' -Body 'Per review: the file is named after the report.'
    Check 'one round commit -> nothing to do' "[$(Compress $r)]" '[]'

    Add-Commit -Dir $r.wt -File 'feature.txt' -Subject 'fix casing'
    Add-Commit -Dir $r.wt -File 'other.txt' -Subject 'wip'
    $tree = Get-Sha $r.wt 'HEAD^{tree}'
    $res = Compress $r
    Check 'three on top -> folded' $res.ok $true
    Check '... from three' $res.from 3
    Check '... into one commit on top of the reviewed one' (Get-Sha $r.wt 'HEAD~1') $r.reviewed
    Check '... the reviewed commit untouched' (Get-Sha $r.wt 'HEAD~1') (Get-Sha $r.hub 'origin/jb/PROJ-1')
    Check '... the tree exactly as the run left it' (Get-Sha $r.wt 'HEAD^{tree}') $tree
    Check "... with the round's own first message" "$(git -C $r.wt log -1 --format=%s)" 'PROJ-1: Address review - name the export file'
    Check '... body and all' ("$(git -C $r.wt log -1 --format=%b)" -match 'named after the report') $true
    Check '... not a machine message' $res.machineMessage $false
    Check 'no backup ref left behind' "$(git -C $r.wt for-each-ref --format='%(refname)' 'refs/heads/*-preround')" ''

    # A later bounce in the same round (QA, an owner's note) amends the round commit, never the
    # reviewed one: a run that commits instead is folded back into it.
    Write-Host "== a second bounce in the same round"
    $roundSha = Get-Sha $r.wt HEAD
    Add-Commit -Dir $r.wt -File 'feature.txt' -Subject 'QA: fix the empty export'
    $res = Compress $r
    Check 'the bounce is folded into the round' $res.ok $true
    Check '... still one commit on top of the reviewed one' (Get-Sha $r.wt 'HEAD~1') $r.reviewed
    Check '... the round keeps its message' "$(git -C $r.wt log -1 --format=%s)" 'PROJ-1: Address review - name the export file'
    Check '... and is a new commit, not the old round' ((Get-Sha $r.wt HEAD) -ne $roundSha) $true

    # ---- Written into the reviewed commit ----------------------------------------
    Write-Host "== a run that amended the reviewed commit"
    $a = New-Reviewed 'amended'
    Add-Content -Path (Join-Path $a.wt 'feature.txt') -Value 'review fix' -Encoding ascii
    git -C $a.wt add -A *> $null
    git -C $a.wt commit --amend --no-edit *> $null
    $tree = Get-Sha $a.wt 'HEAD^{tree}'
    Check 'the amend replaced the reviewed commit' ((Get-Sha $a.wt HEAD) -ne $a.reviewed) $true
    $res = Compress $a
    Check 'put back' $res.ok $true
    Check '... said so' $res.rewroteReviewed $true
    Check '... the reviewed commit is the parent again' (Get-Sha $a.wt 'HEAD~1') $a.reviewed
    Check '... the tree unchanged' (Get-Sha $a.wt 'HEAD^{tree}') $tree
    Check '... with a message the net wrote, and says it did' $res.machineMessage $true
    Check '... in the shape the round needs' "$(git -C $a.wt log -1 --format=%s)" 'PROJ-1: Address review - Add export button'

    # ---- Refusals ----------------------------------------------------------------
    Write-Host "== refusals"
    $p = New-Reviewed 'pushed'
    Add-Commit -Dir $p.wt -File 'feature.txt' -Subject 'PROJ-1: Address review - one'
    Add-Commit -Dir $p.wt -File 'feature.txt' -Subject 'PROJ-1: Address review - two'
    git -C $p.wt push origin 'jb/PROJ-1' *> $null
    git -C $p.wt fetch origin *> $null
    $head = Get-Sha $p.wt HEAD
    $res = Compress $p
    Check 'a round already pushed is refused' $res.ok $false
    Check '... naming the remote' ($res.reason -match 'origin/jb/PROJ-1') $true
    Check '... and the branch is untouched' (Get-Sha $p.wt HEAD) $head

    $b = New-Reviewed 'rebased'
    Add-Commit -Dir $b.hub -File 'base.txt' -Subject 'someone else, on main'
    git -C $b.hub push origin main *> $null
    git -C $b.wt fetch origin *> $null
    Add-Commit -Dir $b.wt -File 'feature.txt' -Subject 'PROJ-1: Address review - one'
    Add-Commit -Dir $b.wt -File 'feature.txt' -Subject 'two'
    git -C $b.wt rebase origin/main *> $null
    $head = Get-Sha $b.wt HEAD
    $res = Compress $b
    Check 'a branch that took in the base since the review is refused' $res.ok $false
    Check '... saying why' ($res.reason -match 'taken in commits from origin/main') $true
    Check '... and is untouched' (Get-Sha $b.wt HEAD) $head

    $res = Compress $r 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef'
    Check 'a reviewed sha this repo never had is refused' $res.ok $false

    Add-Content -Path (Join-Path $r.wt 'feature.txt') -Value 'dirty' -Encoding ascii
    $res = Compress $r
    Check 'a dirty tree is refused' $res.ok $false
    git -C $r.wt checkout -- . *> $null

    # ---- No rebase: only whether it still merges -----------------------------------
    Write-Host "== Test-ReviewBranchMerges"
    $m = New-Reviewed 'merges'
    $res = Test-ReviewBranchMerges -WorkDir $m.wt -BaseBranch 'main' -RepoDir $m.hub -MergeLocally $false
    Check 'level with the base -> nothing behind' $res.behind 0
    Add-Commit -Dir $m.hub -File 'unrelated.txt' -Subject 'someone else, elsewhere'
    git -C $m.hub push origin main *> $null
    $head = Get-Sha $m.wt HEAD
    $res = Test-ReviewBranchMerges -WorkDir $m.wt -BaseBranch 'main' -RepoDir $m.hub -MergeLocally $false
    Check 'behind and clean -> counted' $res.behind 1
    Check '... no conflicts' $res.conflicts.Count 0
    Check '... and never rebased' (Get-Sha $m.wt HEAD) $head
    Add-Commit -Dir $m.hub -File 'feature.txt' -Subject 'someone else, on the same file'
    git -C $m.hub push origin main *> $null
    $res = Test-ReviewBranchMerges -WorkDir $m.wt -BaseBranch 'main' -RepoDir $m.hub -MergeLocally $false
    Check 'a conflict is named' ($res.conflicts -join ',') 'feature.txt'
    Check '... and the branch is still where the reviewer left it' (Get-Sha $m.wt HEAD) $head

    # ---- What the reviewer is told -------------------------------------------------
    Write-Host "== Get-ReviewRoundReport"
    $g = New-Reviewed 'report'
    Add-Commit -Dir $g.wt -File 'feature.txt' -Subject 'PROJ-1: Address review - one'
    $rep = Get-ReviewRoundReport -WorkDir $g.wt -ReviewedSha $g.reviewed
    Check 'lists the round' $rep.commits.Count 1
    Check '... by subject' ($rep.commits[0] -match 'Address review - one') $true
    Check 'origin at the reviewed commit -> a fast-forward' $rep.push 'fast-forward'
    git -C $g.wt push origin 'jb/PROJ-1' *> $null
    Check 'pushed -> up to date' (Get-ReviewRoundReport -WorkDir $g.wt -ReviewedSha $g.reviewed).push 'up-to-date'
    git -C $g.wt commit --amend -m 'PROJ-1: Address review - one, reworded' *> $null
    Check 'amended after the push -> not a fast-forward' (Get-ReviewRoundReport -WorkDir $g.wt -ReviewedSha $g.reviewed).push 'diverged'
    git -C $g.wt push origin --delete 'jb/PROJ-1' *> $null
    Check 'origin has no copy -> not pushed' (Get-ReviewRoundReport -WorkDir $g.wt -ReviewedSha $g.reviewed).push 'not-pushed'
}
finally {
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fail -gt 0) { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all $($script:total) passed" -ForegroundColor Green
