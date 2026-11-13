# Tests for restart-board.ps1's choice of what to stop: the board's listener, walked up through the
# launch that started it on Windows or a Mac, and never past it.
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/restart-board.tests.ps1
# Pure ASCII: PS 5.1 parses this file as cp1252.

$ErrorActionPreference = 'Stop'
$failed = 0
function Check([string]$name, $got, $want) {
    if ("$got" -ceq "$want") { Write-Host "ok    $name" }
    else { Write-Host "FAIL  $name - expected '$want', got '$got'"; $script:failed++ }
}

$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'restart-board.ps1'), [ref]$null, [ref]$errors)
Check 'the script parses' $errors.Count 0

$env:WHETSTONE_RESTART_FUNCTIONS_ONLY = '1'
try { . (Join-Path $PSScriptRoot 'restart-board.ps1') -DataRoot ([IO.Path]::GetTempPath()) }
finally { Remove-Item Env:WHETSTONE_RESTART_FUNCTIONS_ONLY }

function P($parent, $name, $cmd) { @{ Parent = $parent; Name = $name; Command = $cmd } }

Write-Host '== Windows: Start-Whetstone.bat'
$win = @{
    1   = P 0 'explorer.exe' 'C:\Windows\explorer.exe'
    10  = P 1 'cmd.exe' 'cmd /c "C:\src\Whetstone\Start-Whetstone.bat"'
    11  = P 10 'dotnet.exe' 'dotnet run --project "C:\src\Whetstone\Whetstone.csproj"'
    12  = P 11 'Whetstone.exe' 'C:\src\Whetstone\bin\Debug\net9.0\Whetstone.exe'
}
Check 'walks up to the .bat, not explorer' (Select-BoardRoot -Procs $win -ListenerPid 12) 10

Write-Host '== Mac: Start-Whetstone.command'
# As ps shows it: the window's login shell runs dotnet, and caffeinate ends up beside the app, under
# dotnet - so the walk stops at dotnet, and stopping it takes caffeinate with it.
$mac = @{
    1   = P 0 'launchd' '/sbin/launchd'
    50  = P 1 'Terminal' '/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal'
    51  = P 50 'zsh' '-zsh'
    61  = P 51 'dotnet' '/opt/homebrew/Cellar/dotnet@9/9.0.124/libexec/dotnet run --project Whetstone.csproj'
    62  = P 61 'caffeinate' 'caffeinate -is dotnet run --project Whetstone.csproj'
    63  = P 61 'Whetstone' '/Users/josh/src/Whetstone/bin/Debug/net9.0/Whetstone'
}
Check 'walks up to dotnet run, not the window''s shell' (Select-BoardRoot -Procs $mac -ListenerPid 63) 61
$mac[61].Parent = 60
$mac[60] = P 51 'caffeinate' 'caffeinate -is dotnet run --project Whetstone.csproj'
Check 'and through caffeinate where it is the parent' (Select-BoardRoot -Procs $mac -ListenerPid 63) 60

Write-Host '== Started some other way'
$other = @{
    70 = P 1 'zsh' '/bin/zsh -l'
    71 = P 70 'dotnet' 'dotnet run --project Whetstone.csproj'
    72 = P 71 'Whetstone' '/Users/josh/src/Whetstone/bin/Debug/net9.0/Whetstone'
}
Check 'a shell that is not the launcher is left alone' (Select-BoardRoot -Procs $other -ListenerPid 72) 71
Check 'a listener with no known parent is itself' (Select-BoardRoot -Procs @{ 80 = P 999 'Whetstone' 'x' } -ListenerPid 80) 80

if ($failed) { Write-Host "$failed FAILED"; exit 1 }
Write-Host 'ALL PASSED'
