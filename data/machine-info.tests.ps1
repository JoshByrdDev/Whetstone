# Exercises data/machine-info.ps1: what a worker host reports about its hardware and its load, on
# macOS and on Windows, from captured command output (data/machine-fixtures/ - the C# tests of
# Services/MachineInfo.cs parse the same files, so both collectors read them the same way). Then
# one live reading of this machine, which only checks that the shape is there.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File data/machine-info.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'machine-info.ps1')
$fx = Join-Path $PSScriptRoot 'machine-fixtures'
function Fixture { param([string]$Name) Get-Content -Raw -LiteralPath (Join-Path $fx $Name) }

$script:fail = 0
$script:total = 0
function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

# ---- The parsers ----

Check 'vm_stat counts free, inactive, speculative and purgeable pages' (Read-VmStatAvailableMb (Fixture 'mac-vm_stat.txt')) 8640
Check 'vm_stat with nothing in it is no reading' "[$(Read-VmStatAvailableMb '')]" '[]'
Check 'swap in use, in MB' (Read-SwapUsedMb (Fixture 'mac-swapusage.txt')) 1536
Check 'swap given in G' (Read-SwapUsedMb 'total = 4.00G  used = 1.50G  free = 2.50G') 1536
Check 'top: the second sample, as busy %' (Read-TopCpuPct (Fixture 'mac-top.txt')) 42.5
Check 'df: free GB' (Read-DfGb (Fixture 'mac-df.txt') 3) 239.6
Check 'df: size GB' (Read-DfGb (Fixture 'mac-df.txt') 1) 460.4
Check 'diskutil: solid state' (Read-DiskutilKind (Fixture 'mac-diskutil.txt')) 'SSD'
Check 'diskutil: spinning' (Read-DiskutilKind '   Solid State:               No') 'HDD'
$di = Read-DockerInfo (Fixture 'docker-info.txt')
Check 'docker info: CPUs' $di.cpus 8
Check 'docker info: memory MB' $di.memMb 7836
$ds = Read-DockerStats (Fixture 'docker-stats.txt')
Check 'docker stats: summed use across containers' $ds.usedMb 1660
Check 'docker stats: the limit' $ds.limitMb 7838
Check 'docker stats: nothing running is zero used' (Read-DockerStats '').usedMb 0
Check 'a version out of a --version line' (Read-Version 'v22.11.0') '22.11.0'
Check 'a version out of a longer line' (Read-Version 'git version 2.47.1.windows.1') '2.47.1.windows.1'
Check 'pressure from the mac level: 4 is critical' (Get-PressureFromMacLevel 4) 2
Check 'pressure from the mac level: 1 is normal' (Get-PressureFromMacLevel '1') 0
Check 'pressure from what is available: 8% warns' (Get-PressureFromAvailable 800 10000) 1
Check 'pressure from what is available: 4% is critical' (Get-PressureFromAvailable 400 10000) 2
Check 'pressure from what is available: plenty is normal' (Get-PressureFromAvailable 4000 10000) 0

# ---- A Mac (Apple Silicon) ----

$mac = @{
    'sysctl:hw.perflevel0.physicalcpu' = "4`n"; 'sysctl:hw.perflevel1.physicalcpu' = "6`n"
    'sysctl:machdep.cpu.brand_string' = "Apple M5`n"; 'sysctl:hw.physicalcpu' = "10`n"; 'sysctl:hw.logicalcpu' = "10`n"
    'sysctl:hw.memsize' = "51539607552`n"; 'cmd:sw_vers -productVersion' = "26.1`n"; 'cmd:uname -m' = "arm64`n"
    'diskutil' = (Fixture 'mac-diskutil.txt'); 'df' = (Fixture 'mac-df.txt')
    'docker-info' = (Fixture 'docker-info.txt'); 'docker-stats' = (Fixture 'docker-stats.txt')
    'cmd:top -l 2 -n 0 -s 1' = (Fixture 'mac-top.txt'); 'cmd:vm_stat' = (Fixture 'mac-vm_stat.txt')
    'sysctl:vm.swapusage' = (Fixture 'mac-swapusage.txt'); 'sysctl:kern.memorystatus_vm_pressure_level' = "2`n"
    'tool:node' = "v22.11.0`n"; 'tool:pnpm' = "9.12.3`n"; 'tool:pwsh' = "PowerShell 7.5.0`n"
    'net' = @{ rx = 1000000; tx = 500000 }
}
$script:ProbeOverride = { param($Key, $Path) $mac[$Key] }
$inv = Get-MachineInventory -DataRoot '/Users/me/whetstone/data' -Os 'macos'
Check 'mac: CPU model' $inv.cpuModel 'Apple M5'
Check 'mac: performance cores' $inv.perfCores 4
Check 'mac: efficiency cores' $inv.effCores 6
Check 'mac: physical cores' $inv.physicalCores 10
Check 'mac: RAM' $inv.ramMb 49152
Check 'mac: OS' "$($inv.os) $($inv.osVersion) $($inv.arch)" 'macOS 26.1 arm64'
Check 'mac: disk' "$($inv.diskKind) $($inv.diskTotalGb)" 'SSD 460.4'
Check 'mac: Docker is its VM, not the Mac' "$($inv.dockerCpus) $($inv.dockerMemMb)" '8 7836'
Check 'mac: tools found are listed' "$($inv.tools['node']) $($inv.tools['pnpm']) $($inv.tools['pwsh'])" '22.11.0 9.12.3 7.5.0'
Check 'mac: a tool not found is left out' $inv.tools.ContainsKey('dotnet') $false

$script:MachineNet = $null; $script:MachineLoads = 0
$t0 = [datetime]'2026-11-11T12:00:00Z'
$load = Get-MachineLoad -DataRoot '/Users/me/whetstone/data' -Os 'macos' -Now $t0
Check 'mac load: CPU' $load.cpuPct 42.5
Check 'mac load: available, not free' $load.memAvailMb 8640
Check 'mac load: total' $load.memTotalMb 49152
Check 'mac load: swap' $load.swapUsedMb 1536
Check 'mac load: pressure level 2 is a warning' $load.pressure 1
Check 'mac load: disk free' $load.diskFreeGb 239.6
Check 'mac load: the first reading has no network rate yet' "[$($load.netInKbps)]" '[]'
Check 'mac load: docker read on the first sample' "$($load.dockerMemUsedMb)/$($load.dockerMemLimitMb)" '1660/7838'
$mac['net'] = @{ rx = 1000000 + 7500000; tx = 500000 + 750000 }
$load2 = Get-MachineLoad -DataRoot '/x' -Os 'macos' -Now $t0.AddMinutes(1)
Check 'mac load: network in, kbit/s over the minute' $load2.netInKbps 1000
Check 'mac load: network out' $load2.netOutKbps 100
Check 'mac load: docker not read again until its turn' "[$($load2.dockerMemUsedMb)]" '[]'

# ---- An Intel Mac, no docker ----

$intel = $mac.Clone()
$intel.Remove('sysctl:hw.perflevel0.physicalcpu'); $intel.Remove('sysctl:hw.perflevel1.physicalcpu')
$intel.Remove('docker-info')
$intel['sysctl:machdep.cpu.brand_string'] = 'Intel(R) Core(TM) i7-8700B CPU @ 3.20GHz'
$script:ProbeOverride = { param($Key, $Path) $intel[$Key] }
$inv = Get-MachineInventory -DataRoot '/x' -Os 'macos'
Check 'intel mac: no perf levels means neither count' "[$($inv.perfCores)][$($inv.effCores)]" '[][]'
Check 'intel mac: no docker is no docker, not zero' "[$($inv.dockerCpus)][$($inv.dockerMemMb)]" '[][]'

# ---- Windows ----

$win = @{
    'cim:Win32_Processor' = @([pscustomobject]@{ Name = 'AMD Ryzen 5 5600X 6-Core Processor  '; NumberOfCores = 6; NumberOfLogicalProcessors = 12; LoadPercentage = 37 })
    'cim:Win32_ComputerSystem' = @([pscustomobject]@{ TotalPhysicalMemory = 17179869184 })
    'cim:Win32_PerfFormattedData_PerfOS_Memory' = @([pscustomobject]@{ AvailableMBytes = 1200 })
    'cim:Win32_PageFileUsage' = @([pscustomobject]@{ CurrentUsage = 900 }, [pscustomobject]@{ CurrentUsage = 100 })
    'win-version' = @{ build = 26200; display = '25H2' }
    'disk-kind' = 'SSD'
    'drive' = @{ free = 107374182400; total = 1000204886016 }
    'tool:node' = 'v22.11.0'; 'tool:dotnet' = '9.0.306'; 'tool:docker' = 'Docker version 28.5.1, build e180ab8'
    'docker-info' = "12 16496934912`n"; 'docker-stats' = ''
}
$script:ProbeOverride = { param($Key, $Path) $win[$Key] }
$inv = Get-MachineInventory -DataRoot 'C:\whetstone\data' -Os 'windows'
Check 'windows: CPU model trimmed' $inv.cpuModel 'AMD Ryzen 5 5600X 6-Core Processor'
Check 'windows: cores' "$($inv.physicalCores)/$($inv.logicalCores)" '6/12'
Check 'windows: not hybrid, so no perf/eff split' "[$($inv.perfCores)]" '[]'
Check 'windows: RAM' $inv.ramMb 16384
Check 'windows: 11 told from 10 by its build' $inv.osVersion '11 25H2 (build 26200)'
Check 'windows: disk' "$($inv.diskKind) $($inv.diskTotalGb)" 'SSD 931.5'
Check 'windows: docker' "$($inv.dockerCpus) $($inv.dockerMemMb)" '12 15732'
Check 'windows: docker version listed' $inv.tools['docker'] '28.5.1'

$script:MachineNet = $null; $script:MachineLoads = 0
$load = Get-MachineLoad -DataRoot 'C:\whetstone\data' -Os 'windows' -Now $t0
Check 'windows load: CPU' $load.cpuPct 37
Check 'windows load: available' "$($load.memAvailMb)/$($load.memTotalMb)" '1200/16384'
Check 'windows load: page files summed' $load.swapUsedMb 1000
Check 'windows load: 7% available warns' $load.pressure 1
Check 'windows load: disk free' $load.diskFreeGb 100
Check 'windows load: docker with nothing running is zero' "$($load.dockerMemUsedMb)/[$($load.dockerMemLimitMb)]" '0/[]'

$script:MachineLoads = 0
$load = Get-MachineLoad -DataRoot 'C:\x' -Os 'windows' -Docker $false -Now $t0
Check 'docker left alone when told to be' "[$($load.dockerMemUsedMb)]" '[]'

# ---- What the board receives ----

$json = Get-MachineInventory -DataRoot 'C:\x' -Os 'windows' | ConvertTo-Json -Depth 4 -Compress
Check 'the report is camelCase JSON the board reads' ($json -match '"ramMb":16384' -and $json -match '"tools":\{') $true

# ---- This machine, for real ----

$script:ProbeOverride = $null
$live = Get-MachineInventory -DataRoot $PSScriptRoot -Docker $false
Check 'live: logical cores read' ($live.logicalCores -gt 0) $true
if ((Get-MachineOs) -ne 'other') {
    Check 'live: RAM read' ($live.ramMb -gt 0) $true
    Check 'live: pwsh version read' ([bool]$live.tools['pwsh']) $true
    $l = Get-MachineLoad -DataRoot $PSScriptRoot -Docker $false
    Check 'live: available memory read' ($l.memAvailMb -gt 0) $true
    Check 'live: CPU read' ($null -ne $l.cpuPct) $true
}

Write-Host ""
if ($script:fail -eq 0) { Write-Host "All $script:total machine-info checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$script:fail of $script:total machine-info checks FAILED." -ForegroundColor Red
exit 1
