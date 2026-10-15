# ============================================================
# The wall around an unattended run.
#
# Whetstone's whole pipeline is local: a git worktree, a docker stack, a database on
# localhost. Nothing in it has any business touching a deployed environment, and the
# heartbeat prompt says so in its hard rules. This file is what makes that a wall rather
# than a request, because every provider runs its agent with approvals switched off -
# claude with --permission-mode bypassPermissions, codex with
# --dangerously-bypass-approvals-and-sandbox, gemini with --approval-mode yolo - and the
# only thing standing between a worker and production was the paragraph asking it not to.
#
# A PreToolUse hook still fires under bypassPermissions (verified 2026-09-04: the deny
# came back to the model as the blocked reason, with the tool never running), and that
# reason text is shown to the model, so a denial also teaches mid-run.
#
# WHAT THIS DOES NOT COVER. It is a backstop, not the rule:
#   - Claude only. Codex has no hook system and Gemini's is not equivalent, so on those
#     providers the heartbeat prompt's hard rule is the whole of the enforcement.
#   - Shell commands and browser navigation only. A model that reaches a deployed
#     environment some other way is stopped by the prompt or by nothing.
# The rule lives in data/heartbeat-prompt.md. This file only catches the obvious half.
#
# FAILS CLOSED. An unreadable payload is denied, not waved through: a wedged fleet is
# loud and recoverable, and the failure this exists to prevent is neither.
#
# NOTHING HERE KNOWS ANY ONE PROJECT. Every rule in this file is about a kind of command -
# a cloud CLI, a database client aimed off-machine - and holds for whatever repo the fleet
# is pointed at. A rule that only makes sense for one repo (a script of its own that
# defaults to production, a test assembly wired to a live vendor) goes in that project's
# folder as data/projects/<key>/guard.ps1, which stays out of this repository with the rest
# of the folder. It is dot-sourced last, after every rule here has cleared the call, so it
# can only refuse what would otherwise pass - never allow something this file denies. It
# sees browser navigation, and the edit tools of a project that opts into guardEdits, as
# well as commands. See Invoke-ProjectGuards for what it is handed.
#
# providers.ps1 passes -Project so a worker is held to its own project's rules and nobody
# else's. Run WITHOUT -Project, every project's guard.ps1 applies: a guard that was not
# told who it is working for takes the strictest reading rather than the loosest.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

param(
    [string]$Project,
    [string]$ProjectsDir
)

$ErrorActionPreference = 'Stop'

function Write-Decision {
    param([string]$Decision, [string]$Reason)
    $inner = @{ hookEventName = 'PreToolUse'; permissionDecision = $Decision }
    if ($Reason) { $inner['permissionDecisionReason'] = $Reason }
    $out = @{ hookSpecificOutput = $inner }
    [Console]::Out.Write((ConvertTo-Json $out -Depth 6 -Compress))
    [Console]::Out.Flush()
    # Not 'exit': called from a project's dot-sourced guard.ps1, 'exit' ends only that file
    # and this one carries on to print a second decision - an allow, after the deny.
    [Environment]::Exit(0)
}

function Deny {
    param([string]$What)
    $reason = @"
BLOCKED BY THE LOCAL-ONLY GUARD: $What

This machine is the whole world for an unattended run. Every environment beyond it -
production, UAT, staging, shared dev, CI - is out of reach in every direction: no writes,
no reads, no connection attempts, no credentials, no pipeline triggers. There is no
wording of this command that is allowed, and no master note or ticket description that
authorises it.

If this ticket asks for work on a deployed environment, that work is a HUMAN'S to run.
Yours is to do the investigation locally and hand them something they can execute: the
exact statements, proven against the local database, with a rollback and a verification
step, committed to this branch and written up on the ticket. Carry on with that. Do not
block the ticket to ask permission to run this - the answer is no, permanently.
"@
    Write-Decision -Decision 'deny' -Reason $reason
}

# ---- Read the call ---------------------------------------------------------

$tool    = ''
$command = $null
$url     = $null
$path    = $null
try {
    $raw = [Console]::In.ReadToEnd()
    $payload = $raw | ConvertFrom-Json
    $tool = "$($payload.tool_name)"
    $ti = $payload.tool_input
    if ($ti) {
        if ($ti.PSObject.Properties['command']) { $command = "$($ti.command)" }
        if ($ti.PSObject.Properties['url'])     { $url     = "$($ti.url)" }
        # The file an edit tool is about to write. Only a project that asks for its edits to be
        # guarded (guardEdits in its config - see providers.ps1) sends those calls here at all.
        if ($ti.PSObject.Properties['file_path'])         { $path = "$($ti.file_path)" }
        elseif ($ti.PSObject.Properties['notebook_path']) { $path = "$($ti.notebook_path)" }
    }
}
catch {
    Deny "the guard could not read this tool call, so it cannot clear it ($($_.Exception.Message))"
}

# ---- What counts as local --------------------------------------------------

$localHostPattern = '^(?:localhost|127(?:\.\d{1,3}){3}|0\.0\.0\.0|\[?::1\]?|host\.docker\.internal|[A-Za-z0-9_-]+\.localhost|\.|\(local\)|\(localdb\)[^\s]*)$'

# A name with no dot in it cannot be resolved on the public internet - it is a docker
# compose service, a LAN name, or a SQL Server instance alias. A docker stack reaches its
# own database as something like 'local-db', so treating single-label names as remote would
# deny the database work this pipeline does constantly. The exception is the handful of words that
# name an environment outright: 'prod' as a compose service is not a thing, and a run that
# has typed it has stopped talking about this machine.
$environmentWord = '(?i)(?:^|[^a-z])(?:prod|production|uat|stag(?:e|ing)?|live|preprod|release|qa\d)(?:$|[^a-z])'

function Test-LocalHost {
    param([string]$Value)
    if (-not $Value) { return $true }
    $h = "$Value".Trim().Trim('"').Trim([char]39)
    $h = $h -replace '^[a-zA-Z][a-zA-Z0-9+.-]*://', ''    # strip scheme
    $h = $h -replace '^[^@/]*@', ''                        # strip user:pass@
    $h = ($h -split '[/\\?#]')[0]                          # strip path
    $h = ($h -split ',')[0]                                # sqlcmd host,port
    $h = $h -replace ':\d+$', ''                           # strip :port
    if ($h -match $localHostPattern) { return $true }
    if ($h -match $environmentWord)  { return $false }
    return ($h -notmatch '\.')
}

# A token counts only where a command can start: beginning of the line, or after a shell
# separator. Matching the bare word anywhere would trip over a path or a string that
# happens to contain 'az'.
$sep = '(?:^|[\r\n;]|&&|\|\||\||&|\(|\bthen\b|\bdo\b|\bexec\b)\s*'

$c = "$command"

# ---- This project's own rules ----------------------------------------------

# Run last on every path out of this file that would allow, so a project file can only narrow
# what the rules here already cleared. It is dot-sourced into this scope and handed:
#
#   $c              the command, as the shell will see it ('' when the call carries none)
#   $url            where a browser navigation is going ('' otherwise)
#   $path           the file an edit tool is about to write ('' otherwise)
#   $sep            the pattern for "a command can start here" - prefix a CLI name with it
#   Deny <what>     refuse, with the standard local-only explanation; does not return
#   Write-Decision  for a refusal whose reason is not about leaving the machine
#   Test-LocalHost  whether a host, URL or connection target is this machine
#
# It should do nothing but test those and refuse. A project file that will not load denies the
# call - the same bargain as everything below.
function Invoke-ProjectGuards {
    if (-not $ProjectsDir) { $ProjectsDir = Join-Path $PSScriptRoot 'projects' }
    $projectGuards = @()
    if ($Project) {
        if ($Project -notmatch '^[A-Za-z0-9._-]+$') {
            Deny "the guard was started for a project whose name it cannot trust ('$Project'), so it cannot clear this call"
        }
        $projectGuards = @(Join-Path (Join-Path $ProjectsDir $Project) 'guard.ps1')
    }
    elseif (Test-Path $ProjectsDir) {
        $projectGuards = @(Get-ChildItem -Path $ProjectsDir -Directory | ForEach-Object { Join-Path $_.FullName 'guard.ps1' })
    }
    foreach ($projectGuard in $projectGuards) {
        if (-not (Test-Path $projectGuard)) { continue }
        try { . $projectGuard }
        catch {
            Deny "a project's own guard rules ($projectGuard) could not be loaded, so this call cannot be cleared ($($_.Exception.Message))"
        }
    }
}

# ---- Browser navigation ----------------------------------------------------

if ($tool -match 'navigate') {
    if ($url -and -not (Test-LocalHost $url)) {
        Deny "pointing the browser at '$url', which is not this machine. QA drives the local stack and nothing else."
    }
    Invoke-ProjectGuards
    Write-Decision -Decision 'allow'
}

if (-not $command) {
    Invoke-ProjectGuards
    Write-Decision -Decision 'allow'
}

# ---- Hosts that are somebody's cloud by their very name --------------------

$cloudSuffix = '\.database\.windows\.net|\.rds\.amazonaws\.com|\.blob\.core\.windows\.net|\.azurewebsites\.net|\.cloudapp\.azure\.com|\.servicebus\.windows\.net|\.vault\.azure\.net|\.azurecr\.io|\.amazonaws\.com'
if ($c -match $cloudSuffix) {
    Deny "this command names a hosted cloud endpoint ($($Matches[0]))"
}

# ---- Command-position CLIs that only ever act on something remote ----------

$cliRules = @(
    @{ Pattern = 'az';      What = "the Azure CLI ('az')" },
    @{ Pattern = 'aws';     What = "the AWS CLI ('aws')" },
    @{ Pattern = 'gcloud';  What = "the Google Cloud CLI ('gcloud')" },
    @{ Pattern = 'kubectl'; What = "kubectl" },
    @{ Pattern = 'helm';    What = "helm" },
    @{ Pattern = 'pulumi';  What = "pulumi" },
    @{ Pattern = 'doctl';   What = "doctl" },
    @{ Pattern = 'ssh';     What = "ssh - a shell on another machine" },
    @{ Pattern = 'scp';     What = "scp - a copy to or from another machine" },
    @{ Pattern = 'sftp';    What = "sftp" },
    @{ Pattern = 'plink';   What = "plink" },
    @{ Pattern = 'psexec';  What = "psexec" }
)
foreach ($r in $cliRules) {
    if ($c -match ($sep + $r.Pattern + '\b')) { Deny $r.What }
}

# terraform reads and writes remote state even to plan, so only the offline verbs pass.
if ($c -match ($sep + '(?:terraform|tofu)\b')) {
    if ($c -notmatch ($sep + '(?:terraform|tofu)\s+(?:fmt|validate|version|-h|--help|-version|--version)\b')) {
        Deny "terraform against real infrastructure (only 'fmt' and 'validate' run offline)"
    }
}

# git push is already forbidden by the heartbeat prompt; this is the enforcement of it.
if ($c -match ($sep + 'git\b[^\r\n;&|]*\bpush\b')) {
    Deny "'git push' - this pipeline is entirely local and never publishes a branch"
}

# ---- SST, and package scripts that deploy ----------------------------------

# SST has no offline verb that matters: deploy, diff, dev, secret, remove, shell and refresh
# all authenticate to the cloud account and read or write its state, and 'sst dev' deploys a
# personal stage on the way. It is nearly always reached through a package runner rather
# than typed bare, so the runner is part of the pattern.
$runner = '(?:(?:pnpm|npm|yarn|bun)\s+(?:(?:run|exec|dlx)\s+)?|(?:npx|pnpx|bunx)\s+)?'
if ($c -match ($sep + $runner + 'sst(?=\s|$)')) {
    if ($c -notmatch ($sep + $runner + 'sst\s+(?:version|--version|--help|-h)(?=\s|$)')) {
        Deny "sst - every verb that does anything talks to the live cloud account, 'dev' and 'diff' included"
    }
}

# A package script named deploy:<stage> is a deploy whatever it wraps. Bare 'pnpm deploy' is
# left alone: that is pnpm's own command and copies a package into a local folder.
if ($c -match ($sep + '(?:pnpm|npm|yarn|bun)\s+(?:run\s+)?deploy:[\w-]+')) {
    Deny "a package script named deploy:<stage> - a deploy is a human's to run"
}

# ---- File transfer to somebody else's server -------------------------------

# sftp/scp/ssh are covered by the CLI rules above. These three are the gaps they leave:
#
#   - WinSCP is often vendored INSIDE a repo, so it rides into every worktree and needs no
#     install. The CLI rules only match at a command position, and this would be invoked by
#     path, so it is matched anywhere - but only as an executable, so reading integration
#     code that references the WinSCP assembly is still allowed.
#   - Windows ships an `ftp` client.
#   - An ftp:// URL handed to curl or wget slips past the HTTP rule below, which only
#     looks for http and https.
#
# Pulling files off somebody else's server - a database backup most of all - is a HUMAN's
# job. There is no ticket that makes it an unattended agent's work.
if ($c -match '(?i)winscp\.(?:exe|com)\b') {
    Deny "WinSCP - a file transfer to or from another machine"
}
if ($c -match ($sep + 'ftp\b')) {
    Deny "the ftp client"
}
if ($c -match '(?i)ftp://') {
    Deny "an ftp:// URL, which is not this machine"
}

# ---- TFVC, which belongs to the human --------------------------------------

# For a project whose real source control is TFVC (repoType tfvc), this pipeline never
# touches it: the git mirror exists precisely so an agent's work stays out of the human's
# pending changes until they choose to check it in. The rule lives in tfvc-guard.ps1 because
# such a repo's interactive sessions can run it as a hook too. Fails closed like the rest of
# this file: a rule that cannot load clears nothing.
try { . (Join-Path $PSScriptRoot 'tfvc-guard.ps1') }
catch {
    Write-Decision -Decision 'deny' -Reason "BLOCKED: the TFVC rule (tfvc-guard.ps1) could not be loaded, so this call cannot be cleared ($($_.Exception.Message))."
}
$tfvc = Get-TfvcWrite -Command $c
if ($tfvc) {
    Write-Decision -Decision 'deny' -Reason @"
BLOCKED: $($tfvc.What).

TFVC is the human's. Your work goes into git - commit it to your branch - and the human
reviews it and checks it in from Visual Studio. The verbs that never reach the server
(add, undo, get, checkout) are no different: they rewrite the human's pending changes
before the human has chosen anything.

Read-only tf commands pass: status, diff, history, dir, info, properties, view. A worktree
is not a TFVC workspace, so even those only answer from the hub.

If this ticket needs something done in TFVC, say so on the ticket and carry on with the git
side of the work.
"@
}

# gh's read verbs are harmless; its write verbs act on a hosted repo.
if ($c -match ($sep + 'gh\s+(?:workflow|release|pr\s+merge|api|secret|cache|repo\s+(?:create|delete))\b')) {
    Deny "a 'gh' subcommand that writes to a hosted repository"
}

# ---- Database clients pointed somewhere other than this machine ------------

$dbRules = @(
    @{ Client = 'sqlcmd';        Flag = 'S' },
    @{ Client = 'Invoke-Sqlcmd'; Flag = 'ServerInstance' },
    @{ Client = 'psql';          Flag = 'h' },
    @{ Client = 'mysql';         Flag = 'h' },
    @{ Client = 'mariadb';       Flag = 'h' },
    @{ Client = 'redis-cli';     Flag = 'h' },
    @{ Client = 'mongosh';       Flag = 'host' }
)
$quoted = '("[^"]+"|' + [char]39 + '[^' + [char]39 + ']+' + [char]39 + '|[^\s]+)'
foreach ($r in $dbRules) {
    if ($c -match ($sep + [regex]::Escape($r.Client) + '\b')) {
        $m = [regex]::Match($c, '-{1,2}' + $r.Flag + '[= ]?\s*' + $quoted)
        if ($m.Success -and -not (Test-LocalHost $m.Groups[1].Value)) {
            Deny "$($r.Client) pointed at '$($m.Groups[1].Value)', which is not this machine"
        }
    }
}

# A connection string carries its server inline rather than in a flag.
foreach ($m in [regex]::Matches($c, '(?i)(?:server|data\s+source|host)\s*=\s*([^;' + [char]39 + '"\s]+)')) {
    if (-not (Test-LocalHost $m.Groups[1].Value)) {
        Deny "a connection string pointing at '$($m.Groups[1].Value)', which is not this machine"
    }
}

# The same again for a connection URL (DATABASE_URL=postgres://user:pw@host:5432/db), which
# carries its host where neither rule above looks - and is the only form a Node stack uses.
# The reason quotes the URL with its credentials cut out: the text goes back to the model
# and into the run's log.
foreach ($m in [regex]::Matches($c, '(?i)\b(?:postgres(?:ql)?|mysql|mariadb|mongodb(?:\+srv)?|rediss?|sqlserver|mssql)://[^\s"`' + [char]39 + ']+')) {
    if (-not (Test-LocalHost $m.Value)) {
        Deny "a database URL pointing at '$($m.Value -replace '://[^@/]*@', '://')', which is not this machine"
    }
}

# ---- HTTP clients aimed off-machine ----------------------------------------

# Deliberately narrow: only the tools whose whole purpose is to fetch a URL. Package
# managers reach the network legitimately every run and are none of this guard's business.
if ($c -match ($sep + '(?:curl|wget|Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b')) {
    foreach ($m in [regex]::Matches($c, '(?i)https?://[^\s' + [char]39 + '"\\)]+')) {
        if (-not (Test-LocalHost $m.Value)) {
            Deny "an HTTP request to '$($m.Value)', which is not this machine"
        }
    }
}

# ---- This project's own rules ----------------------------------------------

# See Invoke-ProjectGuards near the top of this file for what a project file is handed.
Invoke-ProjectGuards

Write-Decision -Decision 'allow'
