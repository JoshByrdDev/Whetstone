# ============================================================
# The machine's processes, on any OS: one table and one tree kill, for everything in the fleet
# that looks for its own processes or ends them.
#
# Its own file, not part of heartbeat-common.ps1, so that what cannot load that file - the
# watchdog's job, a test lifting a function out of worker.ps1 - can load this one.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================
# Windows answers through CIM, as the fleet always has. macOS has no CIM, so there the same
# table is read from ps. Either way a row is ProcessId, ParentProcessId, Name (the image's file
# name: pwsh.exe here, pwsh on a Mac, so a pattern like 'claude*' matches on both), CommandLine
# and CreationDate. ps prints a command line unquoted, so on a Mac Test-RunsScript can only read a
# -File path without spaces in it - which is why a Mac checkout lives at a path without any.
$OnWindows = ($PSVersionTable.PSEdition -ne 'Core') -or $IsWindows

# Rows from the text of `ps -axww -o pid=,ppid=,lstart=,args=` (lstart is a fixed five words,
# 'Mon Nov  9 10:00:00 2026'). A function of its own so the tests can hand it a Mac's output.
function ConvertFrom-PsListing {
    param([string[]]$Lines)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($line in $Lines) {
        $m = [regex]::Match("$line", '^\s*(\d+)\s+(\d+)\s+(\w{3}\s+\w{3}\s+\d{1,2}\s+[\d:]{8}\s+\d{4})\s+(.*)$')
        if (-not $m.Success) { continue }
        $cmd = $m.Groups[4].Value.TrimEnd()
        # The executable is the first word of the command line; a path to it with a space in it
        # (an .app bundle's) is read up to the space, which leaves a name no fleet pattern matches.
        $exe = ($cmd -split '\s+', 2)[0]
        $started = $null
        try { $started = [datetime]::ParseExact(($m.Groups[3].Value -replace '\s+', ' '), 'ddd MMM d HH:mm:ss yyyy', [Globalization.CultureInfo]::InvariantCulture) } catch { }
        $rows.Add([pscustomobject]@{
            ProcessId = [int]$m.Groups[1].Value; ParentProcessId = [int]$m.Groups[2].Value
            Name = [IO.Path]::GetFileName($exe); CommandLine = $cmd; CreationDate = $started
        })
    }
    return $rows.ToArray()
}

# Every process, or the one -Id names, or (-Shells) only the fleet's own PowerShells - either
# edition, because a heartbeat started on one may find workers a previous one started on the other.
function Get-ProcessTable {
    param([int]$Id = 0, [switch]$Shells)
    if ($OnWindows) {
        $filter = if ($Id -gt 0) { "ProcessId=$Id" } elseif ($Shells) { "Name='powershell.exe' OR Name='pwsh.exe'" } else { $null }
        $cim = if ($filter) { Get-CimInstance Win32_Process -Filter $filter -ErrorAction SilentlyContinue }
               else { Get-CimInstance Win32_Process -ErrorAction SilentlyContinue }
        return @($cim | ForEach-Object {
            [pscustomobject]@{ ProcessId = [int]$_.ProcessId; ParentProcessId = [int]$_.ParentProcessId; Name = "$($_.Name)"
                               CommandLine = "$($_.CommandLine)"; CreationDate = $_.CreationDate } })
    }
    $psArgs = @('-axww', '-o', 'pid=,ppid=,lstart=,args=')
    if ($Id -gt 0) { $psArgs = @('-ww', '-p', "$Id", '-o', 'pid=,ppid=,lstart=,args=') }
    $locale = $env:LC_ALL
    $env:LC_ALL = 'C'   # lstart in English, whatever the machine's language
    try { $rows = @(ConvertFrom-PsListing -Lines @(& ps @psArgs 2>$null)) }
    finally { $env:LC_ALL = $locale }
    if ($Shells) { $rows = @($rows | Where-Object { $_.Name -in 'pwsh', 'pwsh-preview' }) }
    return @($rows)
}

# Kill a process and everything it started. Windows: taskkill /T, as before. Elsewhere: .NET's own
# tree kill, which 7 has and 5.1 (Windows-only) does not need.
function Stop-ProcessTree {
    param([int]$Id)
    if ($Id -le 0) { return }
    if ($OnWindows) { & taskkill.exe /PID $Id /T /F *> $null; return }
    try { [System.Diagnostics.Process]::GetProcessById($Id).Kill($true) } catch { }
}
