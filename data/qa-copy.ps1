# ============================================================
# An isolated QA copy of Whetstone, started from a worktree.
#
# Proving a change to the dashboard means running the dashboard, and the one already running
# on 5338 is the live board with the fleet hanging off it: restarting it kills every worker
# mid-run. So a change is proven against a copy instead - this checkout's own Release build,
# on ports of its own, over a throwaway data folder seeded with three placeholder projects
# (alpha, bravo, and charlie - a project of several repos) whose repos live inside that folder too.
#
# The copy is started with WHETSTONE_QA_ISOLATION=1, which makes it refuse everything that
# would reach beyond its folder: the heartbeat and the steward, every agent run, the tracker,
# notifications, the Claude login, docker stacks, pushes. See Services/QaIsolation.cs. It will
# not start at all over a checkout's data or on 5338.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/qa-copy.ps1          # build, seed, start
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/qa-copy.ps1 -Stop    # stop this worktree's copy
#
# API on -Port (5340), the client's Vite dev server on -VitePort (5341) proxying to it. Never
# 5338 or 5173: those are the live instance and the dev server that proxies to it.
#
# Run from a worktree, never from the main checkout: the build would land beside the files
# the live instance runs from.
#
# Runs on Windows and macOS. What differs between them - the temp folder, who listens on a port,
# a process's command line, starting a child that outlives this script, killing a process tree -
# is in the helpers below, and nothing else in the script asks which one it is on.
#
# ASCII ONLY - PS 5.1 parses this as cp1252.
# ============================================================

param(
    [int]$Port = 5340,
    [int]$VitePort = 5341,
    [string]$DataRoot,
    [switch]$NoVite,
    [switch]$NoBuild,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Fail([string]$Why) {
    Write-Host "qa-copy: $Why" -ForegroundColor Red
    exit 1
}

# ---- Platform helpers -------------------------------------------------------

# $IsWindows does not exist on 5.1, which only ever runs on Windows.
$onWindows = ($PSVersionTable.PSEdition -ne 'Core') -or $IsWindows

# The PIDs listening on a TCP port. Empty when nothing is.
function Get-ListenerPids([int]$Port) {
    if ($onWindows) {
        return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { [int]$_.OwningProcess } | Select-Object -Unique)
    }
    if (Get-Command lsof -ErrorAction SilentlyContinue) {
        return @(& lsof -nP "-iTCP:$Port" -sTCP:LISTEN -t 2>$null | ForEach-Object { [int]$_ } | Select-Object -Unique)
    }
    return @()
}

function Test-PortInUse([int]$Port) {
    if (@(Get-ListenerPids $Port).Count -gt 0) { return $true }
    if ($onWindows) { return $false }
    # Without lsof (or for a listener lsof cannot see), a bind on the loopback answers it.
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $Port)
    try { $l.Start(); return $false } catch { return $true } finally { try { $l.Stop() } catch { } }
}

# A process's full command line, or '' when it is gone.
function Get-CommandLine([int]$ProcessId) {
    if ($onWindows) {
        $w = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
        return "$($w.CommandLine)"
    }
    return "$(& ps -o command= -p $ProcessId 2>$null)".Trim()
}

# A process and everything under it, forcefully - taskkill /T /F, or its equivalent.
function Stop-Tree([int]$ProcessId) {
    if ($onWindows) { & taskkill.exe /PID $ProcessId /T /F 2>&1 | Out-Null; return }
    $all = New-Object Collections.Generic.List[int]
    $todo = New-Object Collections.Generic.Queue[int]
    $todo.Enqueue($ProcessId)
    while ($todo.Count -gt 0) {
        $id = $todo.Dequeue()
        if ($all.Contains($id)) { continue }
        $all.Add($id)
        foreach ($c in @(& pgrep -P $id 2>$null)) { if ("$c" -match '^\d+$') { $todo.Enqueue([int]$c) } }
    }
    # Children first, so none is orphaned to launchd between two kills.
    for ($i = $all.Count - 1; $i -ge 0; $i--) { & kill -9 $all[$i] 2>$null }
}

if ($Port -in 5338, 5173 -or $VitePort -in 5338, 5173) {
    Fail "5338 and 5173 are the live instance and its dev server. Pick other ports."
}
if ($Port -eq $VitePort) { Fail "-Port and -VitePort must differ." }

# ---- From a worktree only ---------------------------------------------------

$gitDir = git -C $repo rev-parse --absolute-git-dir 2>$null
$common = git -C $repo rev-parse --path-format=absolute --git-common-dir 2>$null
if (-not $gitDir -or -not $common) { Fail "$repo is not a git checkout." }
if ([IO.Path]::GetFullPath($gitDir).TrimEnd('\', '/') -ieq [IO.Path]::GetFullPath($common).TrimEnd('\', '/')) {
    Fail "this is the main checkout ($repo). Start a QA copy from a worktree: its build and its data stay away from the live instance's."
}

# ---- Where the copy keeps its data -----------------------------------------

if (-not $DataRoot) {
    $DataRoot = Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'whetstone-qa') (Split-Path $repo -Leaf)
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
for ($d = [IO.DirectoryInfo]$DataRoot; $d; $d = $d.Parent) {
    if (Test-Path (Join-Path $d.FullName 'Whetstone.csproj')) {
        Fail "the data root ($DataRoot) is inside a checkout ($($d.FullName)). It must be a throwaway folder outside every checkout."
    }
}
$stateFile = Join-Path $DataRoot 'qa-copy.json'
$binRoot   = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'bin') 'Release'))
$exe       = Join-Path (Join-Path $binRoot 'net9.0') $(if ($onWindows) { 'Whetstone.exe' } else { 'Whetstone' })
$marker    = Join-Path $DataRoot '.whetstone-qa-copy'

# ---- Stopping ---------------------------------------------------------------

# Only ever the processes this script recorded, and only when they are still what it started:
# the API from this checkout's own bin, the dev server by its command line. A PID on its own is
# recycled, and the wrong process here could be the live board.
function Stop-Copy {
    if (-not (Test-Path $stateFile)) { return }
    $s = Get-Content $stateFile -Raw | ConvertFrom-Json
    if ($s.apiPid) {
        $p = Get-Process -Id $s.apiPid -ErrorAction SilentlyContinue
        if ($p -and $p.Path -and ([IO.Path]::GetFullPath($p.Path)).StartsWith($binRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Tree $s.apiPid
            Write-Host "qa-copy: stopped the API (PID $($s.apiPid))"
        }
    }
    if ($s.vitePid) {
        if ((Get-CommandLine $s.vitePid) -match 'npm(\.cmd)?"?\s+run\s+dev') {
            Stop-Tree $s.vitePid
            Write-Host "qa-copy: stopped the dev server (PID $($s.vitePid))"
        }
    }
    Remove-Item $stateFile -Force
}

if ($Stop) { Stop-Copy; exit 0 }
Stop-Copy

foreach ($p in @($Port) + $(if ($NoVite) { @() } else { @($VitePort) })) {
    if (Test-PortInUse $p) {
        Fail "port $p is already in use. Stop whatever holds it, or pass another port."
    }
}

# ---- Build ------------------------------------------------------------------

if (-not $NoBuild) {
    Write-Host "qa-copy: building $repo (Release)"
    & dotnet build (Join-Path $repo 'Whetstone.csproj') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "the build failed." }
}
if (-not (Test-Path $exe)) { Fail "no build at $exe. Run without -NoBuild." }

# ---- A fresh, throwaway data root ------------------------------------------

if (Test-Path $DataRoot) {
    if (-not (Test-Path $marker)) {
        Fail "$DataRoot exists and is not a QA copy's (no .whetstone-qa-copy in it). Refusing to empty it."
    }
    Remove-Item $DataRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $DataRoot | Out-Null
Set-Content -Path $marker -Value 'A throwaway data root for an isolated Whetstone QA copy. data/qa-copy.ps1 empties it on every start.' -Encoding ASCII

# Every tracked file under data/ - scripts, prompts, templates, defaults - except the projects,
# which are machine-local and never the copy's. The scripts ride along so the copy looks like the
# real thing; the isolation is what stops them running.
$tracked = git -C $repo ls-files -- data | Where-Object { $_ -notlike 'data/projects/*' }
foreach ($rel in $tracked) {
    $src = Join-Path $repo $rel
    if (-not (Test-Path $src -PathType Leaf)) { continue }
    $dst = Join-Path $DataRoot ($rel.Substring('data/'.Length))
    $dir = Split-Path $dst -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item $src $dst
}

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
}

Write-Json (Join-Path $DataRoot 'config.json') ([ordered]@{
    dashboardUrl        = "http://localhost:$Port"
    heartbeatProjects   = @()
    notificationChannel = 'none'
    notificationEvents  = @()
    slackWebhookUrl     = ''
    ntfyTopic           = ''
})

# Two placeholder projects, each with a repo of its own inside the data root - so every git
# command the copy runs on a project's behalf lands in a folder that is deleted next start.
foreach ($key in 'alpha', 'bravo') {
    $r = Join-Path (Join-Path $DataRoot 'repos') $key
    New-Item -ItemType Directory -Path $r -Force | Out-Null
    & git -C $r init -q -b main
    & git -C $r config user.name 'QA copy'
    & git -C $r config user.email 'qa-copy@localhost'
    Set-Content -Path (Join-Path $r 'README.md') -Value "# $key`n`nA placeholder repository for an isolated QA copy." -Encoding ASCII
    & git -C $r -c core.autocrlf=false add -A
    & git -C $r commit -q -m "A placeholder repository"

    $pd = Join-Path (Join-Path $DataRoot 'projects') $key
    New-Item -ItemType Directory -Path $pd -Force | Out-Null
    Copy-Item (Join-Path $DataRoot 'skill-templates') (Join-Path $pd 'skills') -Recurse
    Write-Json (Join-Path $pd 'project.defaults.json') ([ordered]@{
        displayName      = (Get-Culture).TextInfo.ToTitleCase($key)
        baseBranch       = 'main'
        mergeInsteadOfPr = $true
    })
    Write-Json (Join-Path $pd 'project.json') ([ordered]@{
        projectPath  = $r
        worktreeRoot = (Join-Path (Join-Path $DataRoot 'worktrees') $key)
        tracker      = @{ kind = 'none' }
    })
}

# A third placeholder, a project of several repos (docs/multi-repo-plan.md): a folder holding three
# repos, one read-only, each with a bare origin inside the data root so "the base" is origin/main as
# on a real pull-request project. It delivers by pull request, which alpha and bravo do not - they
# merge locally, and a project of several refuses that. The copy refuses every push anyway. Branch
# qa/two-repos has a commit in two of the repos, so a ticket pointed at it shows the PR dialog a
# change across several; qa/their-change is on charlie-api's origin alone, for a peer review.
$charlie = Join-Path (Join-Path $DataRoot 'repos') 'charlie'
foreach ($name in 'charlie-web', 'charlie-api', 'charlie-infra') {
    $r = Join-Path $charlie $name
    $origin = Join-Path (Join-Path $DataRoot 'origins') "$name.git"
    New-Item -ItemType Directory -Path $r -Force | Out-Null
    & git init -q --bare -b main $origin
    & git -C $r init -q -b main
    & git -C $r config user.name 'QA copy'
    & git -C $r config user.email 'qa-copy@localhost'
    Set-Content -Path (Join-Path $r 'README.md') -Value "# $name`n`nOne of charlie's placeholder repositories for an isolated QA copy." -Encoding ASCII
    & git -C $r -c core.autocrlf=false add -A
    & git -C $r commit -q -m "A placeholder repository"
    & git -C $r remote add origin $origin
    & git -C $r push -q -u origin main
    if ($name -ne 'charlie-infra') {
        & git -C $r checkout -q -b qa/two-repos
        Set-Content -Path (Join-Path $r 'CHANGE.md') -Value "A change in $name, one half of qa/two-repos." -Encoding ASCII
        & git -C $r -c core.autocrlf=false add -A
        & git -C $r commit -q -m "Half of a change across two repos"
        & git -C $r checkout -q main
    }
    # Somebody else's branch, on charlie-api's origin only - what a peer review of one repo reads.
    if ($name -eq 'charlie-api') {
        & git -C $r checkout -q -b qa/their-change
        Set-Content -Path (Join-Path $r 'THEIRS.md') -Value "A colleague's change to charlie-api, waiting for review." -Encoding ASCII
        & git -C $r -c core.autocrlf=false add -A
        & git -C $r commit -q -m "A colleague's change"
        & git -C $r push -q origin qa/their-change
        & git -C $r checkout -q main
        & git -C $r branch -q -D qa/their-change
    }
}
$pd = Join-Path (Join-Path $DataRoot 'projects') 'charlie'
New-Item -ItemType Directory -Path $pd -Force | Out-Null
Copy-Item (Join-Path $DataRoot 'skill-templates') (Join-Path $pd 'skills') -Recurse
Write-Json (Join-Path $pd 'project.defaults.json') ([ordered]@{
    displayName = 'Charlie'
    baseBranch  = 'main'
    repos       = @('charlie-web', 'charlie-api', [ordered]@{ dir = 'charlie-infra'; readOnly = $true })
    # Both tracks, so a peer review of one repo's branch can be filed and read here too.
    tracks      = @('Delivery', 'Review')
})
Write-Json (Join-Path $pd 'project.json') ([ordered]@{
    projectPath  = $charlie
    worktreeRoot = (Join-Path (Join-Path $DataRoot 'worktrees') 'charlie')
    tracker      = @{ kind = 'none' }
})

# ---- Start ------------------------------------------------------------------

$logs = Join-Path $DataRoot 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null

# Each process is started so that it holds none of this script's stdio. Started directly with its
# output redirected, the copy inherited whatever pipe this script's caller was reading, and the
# caller - an agent's shell call - waited on it until it timed out, though the copy was long up.
# On Windows that is cmd.exe with a hidden window - a shell execute, which hands the child none of
# this process's handles - and cmd does the redirection. Elsewhere it is /bin/sh, which points all
# three of stdin, stdout and stderr away from the caller and then execs nohup, so the PID returned
# is the program itself and it survives the terminal that started it.
#
# Start-Process on 5.1 has no -Environment, so the child inherits these from here.
$env:WHETSTONE_QA_ISOLATION = '1'
$env:DASHBOARD_DATA_ROOT    = $DataRoot
$env:ASPNETCORE_URLS        = "http://localhost:$Port"
# Off Windows the apphost finds its runtime through DOTNET_ROOT, and a pwsh installed with its own
# runtime sets that to its own - a newer major than the build targets, so the copy will not start.
# Point the child at the net9 runtime of the dotnet on PATH, the one that just built it.
if (-not $onWindows) {
    $rt = @(& dotnet --list-runtimes 2>$null | ForEach-Object {
        if ($_ -match '^Microsoft\.NETCore\.App 9\.\S+ \[(.+)\]$') { $Matches[1] }
    }) | Select-Object -Last 1
    if ($rt) { $env:DOTNET_ROOT = Split-Path (Split-Path $rt -Parent) -Parent }
}
function Start-Detached([string]$Program, [string[]]$Arguments, [string]$OutLog, [string]$ErrLog, [string]$WorkDir) {
    if ($onWindows) {
        # A path is quoted for cmd, a bare command name is left as it is.
        $first = if ($Program -match '[\\/\s]') { "`"$Program`"" } else { $Program }
        $line = (@($first) + $Arguments) -join ' '
        $line = "$line > `"$OutLog`" 2> `"$ErrLog`""
        return Start-Process -FilePath 'cmd.exe' -ArgumentList "/c `"$line`"" -WorkingDirectory $WorkDir -WindowStyle Hidden -PassThru
    }
    $q = { param($s) "'" + ($s -replace "'", "'\''") + "'" }
    $words = (@($Program) + $Arguments | ForEach-Object { & $q $_ }) -join ' '
    $si = New-Object Diagnostics.ProcessStartInfo('/bin/sh')
    $si.ArgumentList.Add('-c')
    $si.ArgumentList.Add("exec nohup $words > $(& $q $OutLog) 2> $(& $q $ErrLog) < /dev/null")
    $si.WorkingDirectory = $WorkDir
    $si.UseShellExecute = $false
    return [Diagnostics.Process]::Start($si)
}
$api = Start-Detached $exe @() (Join-Path $logs 'api.out.log') (Join-Path $logs 'api.err.log') $repo

$state = [ordered]@{
    apiPid    = $null
    vitePid   = $null
    port      = $Port
    vitePort  = $(if ($NoVite) { $null } else { $VitePort })
    dataRoot  = $DataRoot
    repo      = $repo
    startedAt = (Get-Date).ToString('o')
}
Write-Json $stateFile $state

$isolated = $false
for ($i = 0; $i -lt 60 -and -not $api.HasExited; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-RestMethod -Uri "http://localhost:$Port/api/qa-isolation" -TimeoutSec 2
        $isolated = [bool]$r.isolated
        break
    }
    catch { }
}
# The API's own PID is whatever now listens on the port, and it has to be this checkout's build.
$apiProc = @(Get-ListenerPids $Port |
    ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }) | Select-Object -First 1
if ($apiProc -and $apiProc.Path -and ([IO.Path]::GetFullPath($apiProc.Path)).StartsWith($binRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $state.apiPid = $apiProc.Id
    Write-Json $stateFile $state
}
else { $isolated = $false }
if (-not $isolated) {
    if (-not $api.HasExited) { Stop-Tree $api.Id }
    Stop-Copy
    $why = if (Test-Path (Join-Path $logs 'api.err.log')) { (Get-Content (Join-Path $logs 'api.err.log') -Raw) } else { '' }
    Fail "the copy did not come up isolated on $Port. $why"
}

if (-not $NoVite) {
    $client = Join-Path $repo 'ClientApp'
    if (-not (Test-Path (Join-Path $client 'node_modules'))) {
        Write-Host "qa-copy: ClientApp has no node_modules - run 'npm ci --prefix ClientApp' for the dev server. The API is up without it." -ForegroundColor Yellow
    }
    else {
        $env:WHETSTONE_API_PORT  = "$Port"
        $env:WHETSTONE_VITE_PORT = "$VitePort"
        $vite = Start-Detached 'npm' @('run', 'dev') (Join-Path $logs 'vite.out.log') (Join-Path $logs 'vite.err.log') $client
        $state.vitePid = $vite.Id
        Write-Json $stateFile $state
        $up = $false
        for ($i = 0; $i -lt 30 -and -not $vite.HasExited; $i++) {
            Start-Sleep -Seconds 1
            if (@(Get-ListenerPids $VitePort).Count -gt 0) { $up = $true; break }
        }
        if (-not $up) { Write-Host "qa-copy: the dev server did not come up on $VitePort - see $(Join-Path $logs 'vite.err.log')" -ForegroundColor Yellow }
    }
}

Write-Host ""
Write-Host "qa-copy: isolated QA copy running" -ForegroundColor Green
Write-Host "  API       http://localhost:$Port   (PID $($state.apiPid))"
if ($state.vitePid) { Write-Host "  Client    http://localhost:$VitePort   (dev server, proxies to $Port)" }
Write-Host "  Data      $DataRoot"
Write-Host "  Projects  alpha, bravo, charlie (placeholders; charlie is several repos)"
Write-Host "  Stop      pwsh -NoProfile -ExecutionPolicy Bypass -File data/qa-copy.ps1 -Stop"
