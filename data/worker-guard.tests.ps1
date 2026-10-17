# Exercises data/worker-guard.ps1 against the commands a fleet really runs (must pass) and
# the ones it must never run (must be denied); then the way a project adds rules of its own
# (data/projects/<key>/guard.ps1); then data/tfvc-guard.ps1 the way a TFVC repo's interactive
# sessions run it (deny or say nothing - never allow).
#
# Nothing here reads the real data/projects: the project cases run against throwaway folders
# under TEMP, so this passes the same on a machine with no projects as on one with ten. A
# project's own rules are tested from its own folder, by a guard.tests.ps1 beside its guard.ps1.
$guard     = Join-Path $PSScriptRoot 'worker-guard.ps1'
$tfvcGuard = Join-Path $PSScriptRoot 'tfvc-guard.ps1'
$tfPath    = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\TF.exe'

# ---- Throwaway project folders ----------------------------------------------

$fixture = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-guard-tests-" + [Guid]::NewGuid().ToString('N'))
$mixed   = Join-Path $fixture 'mixed'    # one project with rules, one without, one whose rules will not load
$clean   = Join-Path $fixture 'clean'    # the same without the broken one
foreach ($root in $mixed, $clean) {
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'ruled'), (Join-Path $root 'plain') | Out-Null
    # Single-quoted here-string: this is the project file's text, not this script's code.
    Set-Content -Encoding ASCII -Path (Join-Path $root 'ruled\guard.ps1') -Value @'
if ($c -match '\bnode(?:\.exe)?\b[^\r\n;&|]*\bwipe-live\.mjs\b') {
    Deny "scripts/wipe-live.mjs - this project's script that only ever talks to production"
}
# A project file cannot widen the shared rules: by the time it runs they have all passed.
if ($c -match 'kubectl') { Write-Decision -Decision 'allow' }
# A project file also sees where the browser is going and which file an edit tool writes.
if ($url -match ':4999(?:/|$)') { Deny "this project's live instance, on 4999" }
if ($path -match '[\\/]locked[\\/]') { Deny "a file this project keeps out of a worker's reach" }
'@
}
New-Item -ItemType Directory -Force -Path (Join-Path $mixed 'broken') | Out-Null
Set-Content -Encoding ASCII -Path (Join-Path $mixed 'broken\guard.ps1') -Value 'throw "this file is wrong"'

$plain = @('-ProjectsDir', $mixed, '-Project', 'plain')

$cases = @(
    # --- must ALLOW: what the phases actually do every run ---
    @{ Expect='allow'; Tool='Bash'; Cmd='cd docker && docker compose up -d --build' },
    @{ Expect='allow'; Tool='Bash'; Cmd='cd docker && docker compose down app-web app-core ingress app-web-migrations' },
    @{ Expect='allow'; Tool='Bash'; Cmd='sqlcmd -S localhost,1433 -U sa -P PassW0rd -C -Q "select top 5 * from Plans"' },
    @{ Expect='allow'; Tool='Bash'; Cmd='sqlcmd -S app-local-db -U sa -C -Q "select 1"' },
    @{ Expect='allow'; Tool='Bash'; Cmd='dotnet test backend/tests/App.Tests --filter Category=Unit' },
    @{ Expect='allow'; Tool='Bash'; Cmd='npm ci && npm run build' },
    @{ Expect='allow'; Tool='Bash'; Cmd='git add -A && git commit -m "PROJ-123: add cleanup script"' },
    @{ Expect='allow'; Tool='Bash'; Cmd='curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/' },
    @{ Expect='allow'; Tool='Bash'; Cmd='terraform fmt -check terraform/environment' },
    @{ Expect='allow'; Tool='Bash'; Cmd='dotnet ef database update --connection "Server=localhost,1433;Database=AppCore;User Id=sa"' },
    @{ Expect='allow'; Tool='Bash'; Cmd='docker exec app-local-db /opt/mssql-tools/bin/sqlcmd -S localhost -U sa' },
    @{ Expect='allow'; Tool='Bash'; Cmd='grep -rn "azure" backend/ | head' },
    @{ Expect='allow'; Tool='Bash'; Cmd='ls docker/azurite && cat docker/.env' },
    @{ Expect='allow'; Tool='mcp__playwright__browser_navigate'; Url='http://localhost:8080/customers' },
    @{ Expect='allow'; Tool='Bash'; Cmd='msbuild src\App.sln /t:Build /p:Configuration=Debug' },
    @{ Expect='allow'; Tool='Bash'; Cmd='vstest.console.exe src\App.Tests\bin\Debug\App.Tests.dll' },
    @{ Expect='allow'; Tool='Bash'; Cmd='sqlcmd -S localhost -E -d Admin -Q "select Id from Organizations where IsActive = 1"' },
    # Reading integration code that mentions a transfer tool is ordinary work - only RUNNING
    # a transfer is not.
    @{ Expect='allow'; Tool='Bash'; Cmd='grep -rn "WinSCP" src/Core/Integrations/Clients' },
    # TFVC: reading its state is fine, and so is a commit message that talks about it.
    @{ Expect='allow'; Tool='Bash'; Cmd='tf status /recursive /format:brief' },
    @{ Expect='allow'; Tool='PowerShell'; Cmd="`$tf = '$tfPath'; & `$tf diff Web\Startup.cs /format:unified" },
    @{ Expect='allow'; Tool='PowerShell'; Cmd="Get-ChildItem 'C:\Program Files' -Recurse -Filter tf.exe | Select-Object -First 1" },
    @{ Expect='allow'; Tool='Bash'; Cmd='git commit -m "Never run tf checkin from a worker"' },
    @{ Expect='allow'; Tool='Bash'; Cmd="git commit -F - <<'EOF'`nKeep the build script, and never tf checkin`nEOF" },
    # A pnpm monorepo on Postgres in docker, reached by a connection URL.
    @{ Expect='allow'; Tool='Bash'; Cmd='DATABASE_URL=postgres://app:app@localhost:5433/app_qa pnpm db:reset' },
    @{ Expect='allow'; Tool='Bash'; Cmd='DATABASE_URL=postgres://app:app@localhost:5433/app_qa pnpm --filter @app/web exec next dev -p 53547' },
    @{ Expect='allow'; Tool='Bash'; Cmd='docker compose -p myapp exec -T db psql -U app -d app_qa -v ON_ERROR_STOP=1 -1 -f - < scripts/sql/fix.sql' },
    @{ Expect='allow'; Tool='Bash'; Cmd='docker compose -p myapp exec -T db createdb -U app app_t412' },
    @{ Expect='allow'; Tool='Bash'; Cmd='pnpm install --frozen-lockfile && pnpm check' },
    @{ Expect='allow'; Tool='Bash'; Cmd='pnpm --filter @app/db exec drizzle-kit generate --custom --name add_flag' },
    @{ Expect='allow'; Tool='Bash'; Cmd="powershell -NoProfile -Command 'Get-NetTCPConnection -LocalPort 53547 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { Stop-Process -Id `$_ -Force }'" },
    # Changing the infrastructure code is ordinary work.
    @{ Expect='allow'; Tool='Bash'; Cmd='git add sst.config.ts infra/alarms.ts && git commit -m "Alarms: a throttle alarm on the web function"' },

    # --- must DENY: reaching off this machine ---
    @{ Expect='deny'; Tool='Bash'; Cmd='terraform apply -auto-approve' },
    @{ Expect='deny'; Tool='Bash'; Cmd='cd terraform/environment && terraform plan' },
    @{ Expect='deny'; Tool='Bash'; Cmd='az pipelines run --name app-deploy' },
    @{ Expect='deny'; Tool='Bash'; Cmd='az login && az account show' },
    @{ Expect='deny'; Tool='Bash'; Cmd='kubectl delete pod app-core-7d9 -n app' },
    @{ Expect='deny'; Tool='Bash'; Cmd='sqlcmd -S app-prod.database.windows.net -U admin -Q "delete from Orders where Id = 42"' },
    @{ Expect='deny'; Tool='Bash'; Cmd='sqlcmd -S app-uat -U sa -Q "select 1"' },
    @{ Expect='deny'; Tool='Bash'; Cmd='psql -h db.production.internal -U postgres -c "delete from users"' },
    @{ Expect='deny'; Tool='Bash'; Cmd='git push origin jb/PROJ-123' },
    @{ Expect='deny'; Tool='Bash'; Cmd='ssh deploy@app-01 "systemctl restart app"' },
    @{ Expect='deny'; Tool='Bash'; Cmd='curl -X DELETE https://app.example.com/api/v1/customers/42' },
    @{ Expect='deny'; Tool='Bash'; Cmd='dotnet ef database update --connection "Server=app-prod.database.windows.net;Database=AppCore"' },
    @{ Expect='deny'; Tool='Bash'; Cmd='gh workflow run deploy.yml' },
    @{ Expect='deny'; Tool='Bash'; Cmd='aws s3 rm s3://app-uploads/file.csv' },
    @{ Expect='deny'; Tool='mcp__playwright__browser_navigate'; Url='https://app.example.com/admin' },
    # Production one command away, by the roads a serverless Node stack has.
    @{ Expect='deny'; Tool='Bash'; Cmd='pnpm sst deploy --stage prod' },
    @{ Expect='deny'; Tool='Bash'; Cmd='cd infra && npx sst diff --stage prod' },
    @{ Expect='deny'; Tool='Bash'; Cmd='sst dev' },
    @{ Expect='deny'; Tool='Bash'; Cmd='pnpm deploy:prod' },
    @{ Expect='deny'; Tool='Bash'; Cmd='DATABASE_URL=postgres://app:hunter2@10.0.12.7:5432/app pnpm db:migrate' },
    @{ Expect='deny'; Tool='Bash'; Cmd='DATABASE_URL=postgresql://app@app-prod.internal/app pnpm db:migrate' },
    # Pulling a database backup off a server is a human's job. WinSCP is often vendored
    # inside a repo, so it is invoked by path rather than at a command position.
    @{ Expect='deny'; Tool='Bash'; Cmd='src\tools\WinSCP.exe /command "open sftp://user@backups.example.com" "get nightly.bak"' },
    @{ Expect='deny'; Tool='Bash'; Cmd='.\tools\winscp.com /script=pull.txt' },
    @{ Expect='deny'; Tool='Bash'; Cmd='ftp backups.example.com' },
    @{ Expect='deny'; Tool='Bash'; Cmd='curl -O ftp://backups.example.com/nightly/app.bak' },
    # TFVC is the human's: check-ins, and the local verbs that rewrite pending changes.
    @{ Expect='deny'; Tool='Bash'; Cmd='tf checkin /comment:"3981" /noprompt' },
    @{ Expect='deny'; Tool='PowerShell'; Cmd="& `"$tfPath`" checkin /recursive" },
    @{ Expect='deny'; Tool='PowerShell'; Cmd="`$tf = '$tfPath'; & `$tf add src\Core\New.cs" },
    @{ Expect='deny'; Tool='Bash'; Cmd='tf vc undo /recursive src' },
    @{ Expect='deny'; Tool='Bash'; Cmd='tf get /recursive /force' },
    @{ Expect='deny'; Tool='PowerShell'; Cmd="Start-Process -FilePath '$tfPath' -ArgumentList 'checkin /noprompt'" },
    @{ Expect='deny'; Tool='Bash'; Cmd='tfpt online /recursive' },
    @{ Expect='deny'; Tool='Bash'; Cmd='git tfs rcheckin' },
    @{ Expect='deny'; Tool='PowerShell'; Cmd='Invoke-RestMethod -Method Post -Uri http://localhost:8080/_apis/tfvc/changesets' },
    # A heredoc fed to a shell is a command, not a message.
    @{ Expect='deny'; Tool='Bash'; Cmd="bash <<'EOF'`ntf checkin /noprompt`nEOF" },

    # --- a worker host: the board's API, at its exact host and port, and nothing else there ---
    @{ Expect='allow'; Board='192.168.1.20:5338'; Tool='Bash'; Cmd='curl -s http://192.168.1.20:5338/api/tickets/7' },
    @{ Expect='allow'; Board='192.168.1.20:5338'; Tool='Bash'; Cmd='curl -s http://localhost:8080/health' },
    @{ Expect='deny';  Board='192.168.1.20:5338'; Tool='Bash'; Cmd='curl -s http://192.168.1.20:8080/' },
    @{ Expect='deny';  Board='192.168.1.20:5338'; Tool='Bash'; Cmd='curl -s http://192.168.1.20/' },
    @{ Expect='deny';  Board='192.168.1.20:5338'; Tool='Bash'; Cmd='curl -s http://192.168.1.21:5338/api/tickets' },
    @{ Expect='deny';  Board='192.168.1.20:5338'; Tool='Bash'; Cmd='psql -h 192.168.1.20 -U app app' },
    @{ Expect='deny';  Board='192.168.1.20:5338'; Tool='Bash'; Cmd='sqlcmd -S 192.168.1.20,1433 -Q "select 1"' },
    @{ Expect='allow'; Board='board-pc:5338';     Tool='Bash'; Cmd='curl -s http://board-pc:5338/api/tickets/7' },
    @{ Expect='deny';  Board='board-pc:5338';     Tool='Bash'; Cmd='curl -s http://board-pc:8080/' },
    @{ Expect='deny';  Board='board-pc:5338';     Tool='Bash'; Cmd='sqlcmd -S board-pc,1433 -Q "select 1"' },
    # The same LAN address with no board named - every board host run - is off-machine, as before.
    @{ Expect='deny';  Tool='Bash'; Cmd='curl -s http://192.168.1.20:5338/api/tickets/7' }
)

# A project's own rules. 'ruled' denies one script of its own; 'plain' has no guard.ps1;
# 'broken' has one that throws.
$projectCases = @(
    # Held to its own rule, and still to every shared one.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Bash'; Cmd='node scripts/wipe-live.mjs --all' },
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Bash'; Cmd='cat scripts/wipe-live.mjs' },
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Bash'; Cmd='npm ci && npm run build' },
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Bash'; Cmd='az login' },
    # The project file tries to allow kubectl. It never gets the chance.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Bash'; Cmd='kubectl get pods' },
    # One project's rule is not another's.
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'plain');  Tool='Bash'; Cmd='node scripts/wipe-live.mjs --all' },
    # A project that does not exist has no rules; that is not an error.
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'nobody'); Tool='Bash'; Cmd='npm ci' },
    # Rules that will not load clear nothing, however harmless the command.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'broken'); Tool='Bash'; Cmd='ls' },
    # A project name that could walk out of the projects folder is refused outright.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', '..\ruled'); Tool='Bash'; Cmd='ls' },
    # Told nothing about which project it is for, the guard applies every project's rules.
    @{ Expect='deny';  Args=@('-ProjectsDir', $clean); Tool='Bash'; Cmd='node scripts/wipe-live.mjs --all' },
    @{ Expect='allow'; Args=@('-ProjectsDir', $clean); Tool='Bash'; Cmd='npm ci' },
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed); Tool='Bash'; Cmd='npm ci' },
    # Exactly one decision comes back even when the deny is issued from the project file.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Bash'; Cmd='node scripts/wipe-live.mjs'; One=$true },
    # Browser navigation reaches a project's rules too - after the shared rule, never instead of it.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='mcp__playwright__browser_navigate'; Url='http://localhost:4999/board'; One=$true },
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='mcp__playwright__browser_navigate'; Url='http://localhost:5000/board' },
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'plain');  Tool='mcp__playwright__browser_navigate'; Url='http://localhost:4999/board' },
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'plain');  Tool='mcp__playwright__browser_navigate'; Url='https://app.example.com/' },
    # And so does the file an edit tool writes, for a project whose edits are sent here at all.
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Edit'; Path='C:\work\locked\settings.json'; One=$true },
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'ruled');  Tool='Write'; Path='C:\work\open\settings.json' },
    @{ Expect='allow'; Args=@('-ProjectsDir', $mixed, '-Project', 'plain');  Tool='Edit'; Path='C:\work\locked\settings.json' },
    @{ Expect='deny';  Args=@('-ProjectsDir', $mixed, '-Project', 'broken'); Tool='Edit'; Path='C:\work\open\settings.json' }
)

# tfvc-guard.ps1 as an interactive session's hook: it denies TFVC writes and is SILENT about
# everything else - including things the worker guard would refuse, which a human session
# may legitimately do.
$tfvcCases = @(
    @{ Expect='none'; Tool='Bash'; Cmd='tf status /recursive' },
    @{ Expect='none'; Tool='PowerShell'; Cmd="`$tf = '$tfPath'; & `$tf history Web\Startup.cs /stopafter:5" },
    @{ Expect='none'; Tool='Bash'; Cmd='az login && az account show' },
    @{ Expect='none'; Tool='Bash'; Cmd='git merge --no-ff --no-commit jb/3981' },
    @{ Expect='none'; Tool='Bash'; Cmd="git commit -F - <<'EOF'`nKeep tf checkin a human step`nEOF" },
    @{ Expect='none'; Tool='Read' },
    @{ Expect='deny'; Tool='Bash'; Cmd='tf checkin /noprompt' },
    @{ Expect='deny'; Tool='PowerShell'; Cmd="`$tf = '$tfPath'; & `$tf undo src\Web\Startup.cs" },
    @{ Expect='deny'; Tool='Bash'; Cmd='tfpt uu /recursive' }
)

function Invoke-Hook {
    param([string]$Script, $Case, [string[]]$DefaultArgs)
    $call = @{ tool_name = $Case.Tool; tool_input = @{} }
    if ($Case.Cmd) { $call.tool_input['command'] = $Case.Cmd }
    if ($Case.Url) { $call.tool_input['url']     = $Case.Url }
    if ($Case.Path) { $call.tool_input['file_path'] = $Case.Path }
    $json = ConvertTo-Json $call -Depth 6 -Compress
    $hookArgs = if ($Case.Args) { $Case.Args } else { $DefaultArgs }
    # The board's address, as worker.ps1 sets it for a run on a worker host - and only then.
    if ($Case.Board) { $env:WHETSTONE_BOARD_HOST = $Case.Board } else { Remove-Item Env:WHETSTONE_BOARD_HOST -ErrorAction SilentlyContinue }
    $out = "$($json | & powershell -NoProfile -ExecutionPolicy Bypass -File $Script @hookArgs)"
    Remove-Item Env:WHETSTONE_BOARD_HOST -ErrorAction SilentlyContinue
    if (-not $out.Trim()) { return 'none' }
    # Two decisions in one answer is not valid JSON, and the harness would read it as none.
    if ($Case.One -and ([regex]::Matches($out, 'permissionDecision"')).Count -ne 1) { return 'many' }
    return ($out | ConvertFrom-Json).hookSpecificOutput.permissionDecision
}

$fail = 0
$total = 0
try {
    foreach ($set in @(@{ Name = 'worker-guard.ps1'; Script = $guard; Cases = $cases; Args = $plain },
                       @{ Name = "worker-guard.ps1 (a project's own rules)"; Script = $guard; Cases = $projectCases; Args = $plain },
                       @{ Name = 'tfvc-guard.ps1 (interactive hook)'; Script = $tfvcGuard; Cases = $tfvcCases; Args = @() })) {
        Write-Host "== $($set.Name)"
        foreach ($t in $set.Cases) {
            $total++
            $got  = Invoke-Hook -Script $set.Script -Case $t -DefaultArgs $set.Args
            $what = if ($t.Cmd) { $t.Cmd -replace "`n", ' \n ' } elseif ($t.Url) { $t.Url } elseif ($t.Path) { "$($t.Tool) $($t.Path)" } else { "($($t.Tool) call)" }
            if ($t.Args -and $t.Args.Count -gt 2) { $what = "[$($t.Args[3])] $what" } elseif ($t.Args) { $what = "[no -Project] $what" }
            if ($got -ne $t.Expect) {
                $fail++
                Write-Host ("FAIL  expected {0,-5} got {1,-5}  {2}" -f $t.Expect, $got, $what) -ForegroundColor Red
            }
            else {
                Write-Host ("ok    {0,-5}  {1}" -f $got, $what) -ForegroundColor DarkGray
            }
        }
    }
}
finally {
    Remove-Item -Recurse -Force $fixture -ErrorAction SilentlyContinue
}
Write-Host ""
if ($fail -eq 0) { Write-Host "ALL $total CASES PASSED" -ForegroundColor Green }
else { Write-Host "$fail of $total FAILED" -ForegroundColor Red; exit 1 }
