# ============================================================
# An isolated QA copy of Whetstone, started from a worktree.
#
# Proving a change to the dashboard means running the dashboard, and the one already running
# on 5338 is the live board with the fleet hanging off it: restarting it kills every worker
# mid-run. So a change is proven against a copy instead - this checkout's own Release build,
# on ports of its own, over a throwaway data folder seeded with two placeholder projects
# (alpha, bravo) whose repos live inside that folder too.
#
# The copy is started with WHETSTONE_QA_ISOLATION=1, which makes it refuse everything that
# would reach beyond its folder: the heartbeat and the steward, every agent run, the tracker,
# notifications, the Claude login, docker stacks, pushes. See Services/QaIsolation.cs. It will
# not start at all over a checkout's data or on 5338.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/qa-copy.ps1          # build, seed, start
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/qa-copy.ps1 -Stop    # stop this worktree's copy
#
# API on -Port (5340), the client's Vite dev server on -VitePort (5341) proxying to it. Never
# 5338 or 5173: those are the live instance and the dev server that proxies to it.
#
# Run from a worktree, never from the main checkout: the build would land beside the files
# the live instance runs from.
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
    $DataRoot = Join-Path (Join-Path $env:TEMP 'whetstone-qa') (Split-Path $repo -Leaf)
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
for ($d = [IO.DirectoryInfo]$DataRoot; $d; $d = $d.Parent) {
    if (Test-Path (Join-Path $d.FullName 'Whetstone.csproj')) {
        Fail "the data root ($DataRoot) is inside a checkout ($($d.FullName)). It must be a throwaway folder outside every checkout."
    }
}
$stateFile = Join-Path $DataRoot 'qa-copy.json'
$marker    = Join-Path $DataRoot '.whetstone-qa-copy'

# ---- Stopping ---------------------------------------------------------------

# Only ever the processes this script recorded, and only when they are still what it started:
# the API from this checkout's own bin, the dev server by its command line. A PID on its own is
# recycled, and the wrong process here could be the live board.
function Stop-Copy {
    if (-not (Test-Path $stateFile)) { return }
    $s = Get-Content $stateFile -Raw | ConvertFrom-Json
    $bin = [IO.Path]::GetFullPath((Join-Path $repo 'bin\Release'))
    if ($s.apiPid) {
        $p = Get-Process -Id $s.apiPid -ErrorAction SilentlyContinue
        if ($p -and $p.Path -and ([IO.Path]::GetFullPath($p.Path)).StartsWith($bin, [StringComparison]::OrdinalIgnoreCase)) {
            & taskkill.exe /PID $s.apiPid /T /F | Out-Null
            Write-Host "qa-copy: stopped the API (PID $($s.apiPid))"
        }
    }
    if ($s.vitePid) {
        $v = Get-CimInstance Win32_Process -Filter "ProcessId=$($s.vitePid)" -ErrorAction SilentlyContinue
        if ($v -and "$($v.CommandLine)" -match 'npm(\.cmd)?"?\s+run\s+dev') {
            & taskkill.exe /PID $s.vitePid /T /F | Out-Null
            Write-Host "qa-copy: stopped the dev server (PID $($s.vitePid))"
        }
    }
    Remove-Item $stateFile -Force
}

if ($Stop) { Stop-Copy; exit 0 }
Stop-Copy

foreach ($p in @($Port) + $(if ($NoVite) { @() } else { @($VitePort) })) {
    if (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) {
        Fail "port $p is already in use. Stop whatever holds it, or pass another port."
    }
}

# ---- Build ------------------------------------------------------------------

if (-not $NoBuild) {
    Write-Host "qa-copy: building $repo (Release)"
    & dotnet build (Join-Path $repo 'Whetstone.csproj') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail "the build failed." }
}
$exe = Join-Path $repo 'bin\Release\net9.0\Whetstone.exe'
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
    $r = Join-Path $DataRoot "repos\$key"
    New-Item -ItemType Directory -Path $r -Force | Out-Null
    & git -C $r init -q -b main
    & git -C $r config user.name 'QA copy'
    & git -C $r config user.email 'qa-copy@localhost'
    Set-Content -Path (Join-Path $r 'README.md') -Value "# $key`n`nA placeholder repository for an isolated QA copy." -Encoding ASCII
    & git -C $r -c core.autocrlf=false add -A
    & git -C $r commit -q -m "A placeholder repository"

    $pd = Join-Path $DataRoot "projects\$key"
    New-Item -ItemType Directory -Path $pd -Force | Out-Null
    Copy-Item (Join-Path $DataRoot 'skill-templates') (Join-Path $pd 'skills') -Recurse
    Write-Json (Join-Path $pd 'project.defaults.json') ([ordered]@{
        displayName      = (Get-Culture).TextInfo.ToTitleCase($key)
        baseBranch       = 'main'
        mergeInsteadOfPr = $true
    })
    Write-Json (Join-Path $pd 'project.json') ([ordered]@{
        projectPath  = $r
        worktreeRoot = (Join-Path $DataRoot "worktrees\$key")
        tracker      = @{ kind = 'none' }
    })
}

# ---- Start ------------------------------------------------------------------

$logs = Join-Path $DataRoot 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null

# Each process is started through cmd.exe with a hidden window - a shell execute, which hands the
# child none of this process's handles. Started directly with its output redirected, the copy
# inherited whatever pipe this script's caller was reading, and the caller - an agent's shell
# call - waited on it until it timed out, though the copy was long up. cmd does the redirection.
#
# Start-Process on 5.1 has no -Environment, so the child inherits these from here.
$env:WHETSTONE_QA_ISOLATION = '1'
$env:DASHBOARD_DATA_ROOT    = $DataRoot
$env:ASPNETCORE_URLS        = "http://localhost:$Port"
function Start-Detached([string]$CommandLine, [string]$WorkDir) {
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/c `"$CommandLine`"" -WorkingDirectory $WorkDir -WindowStyle Hidden -PassThru
}
$api = Start-Detached "`"$exe`" > `"$(Join-Path $logs 'api.out.log')`" 2> `"$(Join-Path $logs 'api.err.log')`"" $repo

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
$apiProc = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue }) | Select-Object -First 1
if ($apiProc -and "$($apiProc.Path)" -like (Join-Path $repo 'bin\Release\*')) {
    $state.apiPid = $apiProc.Id
    Write-Json $stateFile $state
}
else { $isolated = $false }
if (-not $isolated) {
    if (-not $api.HasExited) { & taskkill.exe /PID $api.Id /T /F | Out-Null }
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
        $vite = Start-Detached "npm run dev > `"$(Join-Path $logs 'vite.out.log')`" 2> `"$(Join-Path $logs 'vite.err.log')`"" $client
        $state.vitePid = $vite.Id
        Write-Json $stateFile $state
        $up = $false
        for ($i = 0; $i -lt 30 -and -not $vite.HasExited; $i++) {
            Start-Sleep -Seconds 1
            if (Get-NetTCPConnection -LocalPort $VitePort -State Listen -ErrorAction SilentlyContinue) { $up = $true; break }
        }
        if (-not $up) { Write-Host "qa-copy: the dev server did not come up on $VitePort - see $logs\vite.err.log" -ForegroundColor Yellow }
    }
}

Write-Host ""
Write-Host "qa-copy: isolated QA copy running" -ForegroundColor Green
Write-Host "  API       http://localhost:$Port   (PID $($state.apiPid))"
if ($state.vitePid) { Write-Host "  Client    http://localhost:$VitePort   (dev server, proxies to $Port)" }
Write-Host "  Data      $DataRoot"
Write-Host "  Projects  alpha, bravo (placeholders)"
Write-Host "  Stop      powershell -NoProfile -ExecutionPolicy Bypass -File data/qa-copy.ps1 -Stop"
