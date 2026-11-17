# ============================================================
# What a worker host reports about its own machine: its hardware (Get-MachineInventory) and its
# load at this minute (Get-MachineLoad). The runner sends both on its check-in; the board host
# keeps the hardware as a history of changes and the load beside its minute-by-minute capacity
# samples (docs/machine-limits-plan.md). The board host reads its own in C# (Services/MachineProbe.cs)
# into the same shape - field names here are that contract, so a rename is a rename on both sides.
#
# pwsh 7 on Windows and macOS. A field this machine cannot read is $null, never a guess.
#
# Every outside command goes through Invoke-Probe, keyed by what it reads. The tests set
# $script:ProbeOverride to a scriptblock that answers those keys with captured output, so both
# operating systems are tested on either (data/machine-info.tests.ps1, data/machine-fixtures/).
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

$script:ProbeOverride = $null
$script:MachineNet = $null
$script:MachineLoads = 0
# docker stats takes a second or two, so it is read on every this-many load samples.
$MachineDockerEvery = 5
$MachineTools = @('node', 'pnpm', 'dotnet', 'pwsh', 'git', 'docker', 'claude')

# Not $IsWindows alone: Windows PowerShell 5.1 has no such variable, and an older runner
# supervisor still starts the runner on 5.1 - which read every Windows machine as 'other' and
# sent a load with nothing in it but the network and docker.
function Get-MachineOs {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'other' }
}

# One outside reading, by key. Returns text for a command, objects for a CIM class, $null when it
# cannot be read. Nothing here throws: a machine without docker is a machine without docker.
function Invoke-Probe {
    param([string]$Key, [string]$Path = '')
    if ($script:ProbeOverride) { return & $script:ProbeOverride $Key $Path }
    try {
        $kind, $arg = $Key -split ':', 2
        switch ($kind) {
            'sysctl' { return (& sysctl -n $arg 2>$null | Out-String) }
            'cmd' {
                $exe, $rest = $arg -split ' ', 2
                if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { return $null }
                $out = if ($rest) { & $exe ($rest -split ' ') 2>$null } else { & $exe 2>$null }
                if ($LASTEXITCODE -ne 0) { return $null }
                return ($out | Out-String)
            }
            'df' { return (& df -k $Path 2>$null | Out-String) }
            'diskutil' { return (& diskutil info $Path 2>$null | Out-String) }
            'docker-info' {
                if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return $null }
                $out = & docker info --format '{{.NCPU}} {{.MemTotal}}' 2>$null
                if ($LASTEXITCODE -ne 0) { return $null }
                return ($out | Out-String)
            }
            'docker-stats' {
                if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return $null }
                $out = & docker stats --no-stream --format '{{.MemUsage}}' 2>$null
                if ($LASTEXITCODE -ne 0) { return $null }
                return ($out | Out-String)
            }
            'tool' {
                if (-not (Get-Command $arg -ErrorAction SilentlyContinue)) { return $null }
                return (& $arg --version 2>$null | Select-Object -First 1 | Out-String)
            }
            'cim' { return @(Get-CimInstance -ClassName $arg -ErrorAction Stop) }
            'win-version' {
                $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
                return @{ build = [int][Environment]::OSVersion.Version.Build; display = "$($cv.DisplayVersion)" }
            }
            'disk-kind' {
                $letter = ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))).Substring(0, 1)
                $num = (Get-Partition -DriveLetter $letter -ErrorAction Stop).DiskNumber
                return "$((Get-PhysicalDisk -ErrorAction Stop | Where-Object { "$($_.DeviceId)" -eq "$num" } | Select-Object -First 1).MediaType)"
            }
            'drive' {
                $d = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path)))
                return @{ free = $d.AvailableFreeSpace; total = $d.TotalSize }
            }
            'net' {
                $rx = [long]0; $tx = [long]0
                foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
                    if ("$($ni.OperationalStatus)" -ne 'Up') { continue }
                    if ("$($ni.NetworkInterfaceType)" -in @('Loopback', 'Tunnel')) { continue }
                    $s = $ni.GetIPStatistics()
                    $rx += $s.BytesReceived; $tx += $s.BytesSent
                }
                return @{ rx = $rx; tx = $tx }
            }
        }
    }
    catch { return $null }
    return $null
}

# ---- Parsers: the same rules as MachineParse in Services/MachineInfo.cs --------------------

function ConvertTo-LongOrNull { param($Text)
    $v = [long]0
    if ("$Text".Trim() -and [long]::TryParse("$Text".Trim(), [ref]$v)) { return $v }
    return $null
}

# Memory a new process could have without paging anything out. Free alone is always near zero on
# a Mac, which keeps RAM full of cache on purpose.
function Read-VmStatAvailableMb { param([string]$Text)
    if (-not $Text) { return $null }
    $page = if ($Text -match 'page size of (\d+) bytes') { [long]$Matches[1] } else { [long]4096 }
    $pages = [long]0
    foreach ($name in 'free', 'inactive', 'speculative', 'purgeable') {
        if ($Text -match "(?m)^Pages ${name}:\s+(\d+)") { $pages += [long]$Matches[1] }
    }
    if ($pages -eq 0) { return $null }
    return [long][math]::Floor($pages * $page / 1MB)
}

function Read-SwapUsedMb { param([string]$Text)
    if ($Text -notmatch 'used\s*=\s*([\d.]+)([KMG])') { return $null }
    $v = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    $mb = switch ($Matches[2]) { 'K' { $v / 1024 } 'G' { $v * 1024 } default { $v } }
    return [long][math]::Round($mb)
}

function Read-TopCpuPct { param([string]$Text)
    $ms = [regex]::Matches("$Text", 'CPU usage:.*?([\d.]+)% idle')
    if ($ms.Count -eq 0) { return $null }
    return [math]::Round(100 - [double]::Parse($ms[$ms.Count - 1].Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture), 1)
}

function Read-DfGb { param([string]$Text, [int]$Column)
    $lines = @("$Text" -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($lines.Count -lt 2) { return $null }
    $cols = $lines[-1] -split '\s+'
    if ($cols.Count -le $Column) { return $null }
    $kb = ConvertTo-LongOrNull $cols[$Column]
    if ($null -eq $kb) { return $null }
    return [math]::Round($kb / 1024.0 / 1024.0, 1)
}

function Read-DiskutilKind { param([string]$Text)
    if ($Text -match 'Solid State:\s*(\w+)') { if ($Matches[1] -eq 'Yes') { return 'SSD' } return 'HDD' }
    return $null
}

function Read-DockerInfo { param([string]$Text)
    $parts = @("$Text".Trim() -split ' ' | Where-Object { $_ })
    if ($parts.Count -lt 2) { return @{ cpus = $null; memMb = $null } }
    $cpus = ConvertTo-LongOrNull $parts[0]
    $bytes = ConvertTo-LongOrNull $parts[1]
    return @{ cpus = $(if ($null -ne $cpus) { [int]$cpus }); memMb = $(if ($null -ne $bytes) { [long][math]::Floor($bytes / 1MB) }) }
}

function ConvertFrom-DockerSize { param([string]$Text)
    if ("$Text".Trim() -notmatch '^([\d.]+)\s*([KMGT]?)i?B$') { return $null }
    $v = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    switch ($Matches[2].ToUpperInvariant()) { 'K' { return $v / 1024 } 'M' { return $v } 'G' { return $v * 1024 } 'T' { return $v * 1024 * 1024 } default { return $v / 1MB } }
}

# One "used / limit" per container: the sum used, and the limit (Docker's VM's, the same on each).
function Read-DockerStats { param([string]$Text)
    if (-not "$Text".Trim()) { return @{ usedMb = [long]0; limitMb = $null } }
    $used = 0.0; $limit = $null
    foreach ($line in ("$Text" -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $halves = $line -split '/'
        $u = ConvertFrom-DockerSize $halves[0]
        if ($null -ne $u) { $used += $u }
        if ($halves.Count -gt 1) { $l = ConvertFrom-DockerSize $halves[1]; if ($null -ne $l) { $limit = $l } }
    }
    return @{ usedMb = [long][math]::Round($used); limitMb = $(if ($null -ne $limit) { [long][math]::Round($limit) }) }
}

function Read-Version { param([string]$Text)
    if ("$Text" -match '\d+\.\d+(\.\d+)?([-.+\w]*)?') { return $Matches[0] }
    return $null
}

# Windows has no single pressure reading: under 10% of RAM available warns, under 5% is critical.
# macOS gives one, kern.memorystatus_vm_pressure_level: 1 normal, 2 warn, 4 critical.
function Get-PressureFromAvailable { param($AvailMb, $TotalMb)
    if ($null -eq $AvailMb -or -not $TotalMb) { return $null }
    $pct = 100.0 * $AvailMb / $TotalMb
    if ($pct -lt 5) { return 2 } elseif ($pct -lt 10) { return 1 } else { return 0 }
}

function Get-PressureFromMacLevel { param($Level)
    switch ("$Level".Trim()) { '1' { return 0 } '2' { return 1 } '4' { return 2 } }
    return $null
}

# ---- The two readings -----------------------------------------------------------------------

function Get-MachineInventory {
    param([string]$DataRoot, [string]$Os = (Get-MachineOs), [bool]$Docker = $true)
    $r = [ordered]@{
        cpuModel = $null; physicalCores = $null; logicalCores = [Environment]::ProcessorCount
        perfCores = $null; effCores = $null; ramMb = $null; os = $null; osVersion = $null
        arch = "$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)".ToLowerInvariant()
        diskKind = $null; diskTotalGb = $null; dockerCpus = $null; dockerMemMb = $null; tools = @{}
    }
    if ($Os -eq 'macos') {
        $perf = ConvertTo-LongOrNull (Invoke-Probe 'sysctl:hw.perflevel0.physicalcpu')
        $eff = ConvertTo-LongOrNull (Invoke-Probe 'sysctl:hw.perflevel1.physicalcpu')
        $r.cpuModel = "$(Invoke-Probe 'sysctl:machdep.cpu.brand_string')".Trim()
        $r.physicalCores = ConvertTo-LongOrNull (Invoke-Probe 'sysctl:hw.physicalcpu')
        $logical = ConvertTo-LongOrNull (Invoke-Probe 'sysctl:hw.logicalcpu')
        if ($null -ne $logical) { $r.logicalCores = $logical }
        # An Intel Mac has no perf levels; an Apple one always has both.
        if ($null -ne $perf -and $null -ne $eff) { $r.perfCores = $perf; $r.effCores = $eff }
        $mem = ConvertTo-LongOrNull (Invoke-Probe 'sysctl:hw.memsize')
        if ($null -ne $mem) { $r.ramMb = [long][math]::Floor($mem / 1MB) }
        $r.os = 'macOS'
        $r.osVersion = "$(Invoke-Probe 'cmd:sw_vers -productVersion')".Trim()
        $m = "$(Invoke-Probe 'cmd:uname -m')".Trim()
        if ($m) { $r.arch = $m }
        $r.diskKind = Read-DiskutilKind (Invoke-Probe 'diskutil' $DataRoot)
        $r.diskTotalGb = Read-DfGb (Invoke-Probe 'df' $DataRoot) 1
    }
    elseif ($Os -eq 'windows') {
        $cpu = @(Invoke-Probe 'cim:Win32_Processor')
        if ($cpu.Count -gt 0 -and $cpu[0]) {
            $r.cpuModel = "$($cpu[0].Name)".Trim()
            $r.physicalCores = [long](($cpu | Measure-Object -Property NumberOfCores -Sum).Sum)
            $r.logicalCores = [long](($cpu | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum)
        }
        $cs = @(Invoke-Probe 'cim:Win32_ComputerSystem')
        if ($cs.Count -gt 0 -and $cs[0].TotalPhysicalMemory) { $r.ramMb = [long][math]::Floor([double]$cs[0].TotalPhysicalMemory / 1MB) }
        $r.os = 'Windows'
        $v = Invoke-Probe 'win-version'
        if ($v) {
            $name = if ($v.build -ge 22000) { '11' } else { '10' }
            $r.osVersion = ("$name $($v.display) (build $($v.build))" -replace '  ', ' ')
        }
        $kind = "$(Invoke-Probe 'disk-kind' $DataRoot)"
        $r.diskKind = if ($kind -in @('SSD', 'HDD')) { $kind } else { $null }
        $d = Invoke-Probe 'drive' $DataRoot
        if ($d) { $r.diskTotalGb = [math]::Round($d.total / 1GB, 1) }
    }
    if ($Docker) {
        $di = Read-DockerInfo (Invoke-Probe 'docker-info')
        $r.dockerCpus = $di.cpus; $r.dockerMemMb = $di.memMb
    }
    foreach ($t in $MachineTools) {
        if ($t -eq 'docker' -and -not $Docker) { continue }
        $ver = Read-Version (Invoke-Probe "tool:$t")
        if ($ver) { $r.tools[$t] = $ver }
    }
    return $r
}

function Get-MachineLoad {
    param([string]$DataRoot, [string]$Os = (Get-MachineOs), [bool]$Docker = $true, [datetime]$Now = (Get-Date))
    $r = [ordered]@{
        at = $Now.ToUniversalTime().ToString('o'); cpuPct = $null; memAvailMb = $null; memTotalMb = $null
        swapUsedMb = $null; pressure = $null; diskFreeGb = $null; netInKbps = $null; netOutKbps = $null
        dockerMemUsedMb = $null; dockerMemLimitMb = $null
    }
    if ($Os -eq 'macos') {
        $r.cpuPct = Read-TopCpuPct (Invoke-Probe 'cmd:top -l 2 -n 0 -s 1')
        $r.memAvailMb = Read-VmStatAvailableMb (Invoke-Probe 'cmd:vm_stat')
        $mem = ConvertTo-LongOrNull (Invoke-Probe 'sysctl:hw.memsize')
        if ($null -ne $mem) { $r.memTotalMb = [long][math]::Floor($mem / 1MB) }
        $r.swapUsedMb = Read-SwapUsedMb (Invoke-Probe 'sysctl:vm.swapusage')
        $r.pressure = Get-PressureFromMacLevel (Invoke-Probe 'sysctl:kern.memorystatus_vm_pressure_level')
        $r.diskFreeGb = Read-DfGb (Invoke-Probe 'df' $DataRoot) 3
    }
    elseif ($Os -eq 'windows') {
        $cpu = @(Invoke-Probe 'cim:Win32_Processor')
        $loads = @($cpu | Where-Object { $null -ne $_.LoadPercentage } | ForEach-Object { [double]$_.LoadPercentage })
        if ($loads.Count -gt 0) { $r.cpuPct = [math]::Round(($loads | Measure-Object -Average).Average, 1) }
        $pm = @(Invoke-Probe 'cim:Win32_PerfFormattedData_PerfOS_Memory')
        if ($pm.Count -gt 0 -and $null -ne $pm[0].AvailableMBytes) { $r.memAvailMb = [long]$pm[0].AvailableMBytes }
        $cs = @(Invoke-Probe 'cim:Win32_ComputerSystem')
        if ($cs.Count -gt 0 -and $cs[0].TotalPhysicalMemory) { $r.memTotalMb = [long][math]::Floor([double]$cs[0].TotalPhysicalMemory / 1MB) }
        $pf = @(Invoke-Probe 'cim:Win32_PageFileUsage')
        if ($pf.Count -gt 0 -and $pf[0]) { $r.swapUsedMb = [long](($pf | Measure-Object -Property CurrentUsage -Sum).Sum) }
        $r.pressure = Get-PressureFromAvailable $r.memAvailMb $r.memTotalMb
        $d = Invoke-Probe 'drive' $DataRoot
        if ($d) { $r.diskFreeGb = [math]::Round($d.free / 1GB, 1) }
    }
    # Bytes in and out since the last reading, as kilobits a second.
    $net = Invoke-Probe 'net'
    if ($net) {
        $prev = $script:MachineNet
        $script:MachineNet = @{ at = $Now; rx = [long]$net.rx; tx = [long]$net.tx }
        if ($prev) {
            $secs = ($Now - $prev.at).TotalSeconds
            if ($secs -gt 0 -and $net.rx -ge $prev.rx -and $net.tx -ge $prev.tx) {
                $r.netInKbps = [math]::Round(($net.rx - $prev.rx) * 8 / 1000.0 / $secs, 1)
                $r.netOutKbps = [math]::Round(($net.tx - $prev.tx) * 8 / 1000.0 / $secs, 1)
            }
        }
    }
    if ($Docker -and ($script:MachineLoads % $MachineDockerEvery) -eq 0) {
        # No answer at all is no docker; an empty one is docker with nothing running.
        $raw = Invoke-Probe 'docker-stats'
        if ($null -ne $raw) { $ds = Read-DockerStats $raw; $r.dockerMemUsedMb = $ds.usedMb; $r.dockerMemLimitMb = $ds.limitMb }
    }
    $script:MachineLoads++
    return $r
}

# ---- Devices: what a run's lease may need this machine to have (docs/ios-simulator-plan.md) ----
#
# The rule is MachineDevices / IosToolchain in Services/MachineDevices.cs, which the board host
# applies to itself; this is its copy for a worker host, sent on the check-in as `devices`. The same
# commands, in the same order, judged the same way, and held to the same fixtures
# (data/machine-fixtures/mac-simctl-*.json) by both sides' tests. A device named here and not in the
# C# table is dropped by the board, so the two must also agree on the name.

$IosSimulatorDevice = 'ios:simulator'

# How many available iOS runtimes `simctl list runtimes -j` lists, or $null when it is not that output.
function Read-SimctlIosRuntimes { param([string]$Text)
    if (-not "$Text".Trim()) { return $null }
    try { $j = $Text | ConvertFrom-Json -ErrorAction Stop } catch { return $null }
    if ($null -eq $j.runtimes) { return $null }
    return @($j.runtimes | Where-Object { "$($_.platform)" -eq 'iOS' -and $_.isAvailable -eq $true }).Count
}

# The UDIDs of the booted devices `simctl list devices booted -j` lists; empty when none or unreadable.
function Read-SimctlBooted { param([string]$Text)
    if (-not "$Text".Trim()) { return @() }
    try { $j = $Text | ConvertFrom-Json -ErrorAction Stop } catch { return @() }
    if ($null -eq $j.devices) { return @() }
    $ids = @()
    foreach ($rt in $j.devices.PSObject.Properties) {
        foreach ($d in @($rt.Value)) { if ($d -and "$($d.state)" -eq 'Booted' -and "$($d.udid)") { $ids += "$($d.udid)" } }
    }
    return $ids
}

# macOS, Xcode selected, its licence and first-launch tasks done, and the iOS runtimes - read the
# way IosToolchain.Probe reads them. A step after one that failed is not read.
function Get-IosToolchain {
    param([string]$Os = (Get-MachineOs))
    $r = [ordered]@{ mac = ($Os -eq 'macos'); xcodePath = $null; licence = $null; firstLaunch = $null; runtimes = $null }
    if (-not $r.mac) { return $r }
    $path = "$(Invoke-Probe 'cmd:xcode-select -p')".Trim()
    if (-not $path) { return $r }
    $r.xcodePath = $path
    $r.licence = $null -ne (Invoke-Probe 'cmd:xcodebuild -license check')
    $r.firstLaunch = $null -ne (Invoke-Probe 'cmd:xcodebuild -checkFirstLaunchStatus')
    $r.runtimes = Read-SimctlIosRuntimes (Invoke-Probe 'cmd:xcrun simctl list runtimes -j')
    return $r
}

# The rule: IosToolchain.Has.
function Test-IosSimulatorReady { param($Toolchain)
    return [bool]($Toolchain.mac -and $Toolchain.xcodePath -and $Toolchain.licence -eq $true -and
        $Toolchain.firstLaunch -eq $true -and $null -ne $Toolchain.runtimes -and $Toolchain.runtimes -gt 0)
}

# The devices this machine has, as the board's table names them. Always a list, empty when none.
function Get-MachineDevices {
    param([string]$Os = (Get-MachineOs))
    $devices = @()
    if (Test-IosSimulatorReady (Get-IosToolchain -Os $Os)) { $devices += $IosSimulatorDevice }
    return $devices
}

# The simulators booted right now - written down as a run that leases this machine's simulator
# starts, so the ones it booted can be told apart from any that were already up.
function Get-BootedSimulators {
    return @(Read-SimctlBooted (Invoke-Probe 'cmd:xcrun simctl list devices booted -j'))
}

# The simulators booted now that were not in -Before: SimulatorReset.ToShutDown. A UDID is hex and
# dashes; anything else is not put on a command line.
function Select-NewSimulators { param([string[]]$Before, [string[]]$Now)
    $kept = @{}
    foreach ($b in @($Before)) { if ($b) { $kept["$b".ToUpperInvariant()] = $true } }
    $out = @()
    foreach ($u in @($Now)) {
        if (-not $u -or $kept.ContainsKey("$u".ToUpperInvariant())) { continue }
        if ("$u" -notmatch '^[0-9A-Fa-f-]+$') { continue }
        if ($out -notcontains $u) { $out += $u }
    }
    return $out
}

# Shut down what a run booted, once its lease on this machine's simulator is over: the runner's half
# of SimulatorReset. Returns the UDIDs it shut down.
function Stop-NewSimulators { param([string[]]$Before)
    $stopped = @()
    foreach ($u in @(Select-NewSimulators -Before $Before -Now (Get-BootedSimulators))) {
        $null = Invoke-Probe "cmd:xcrun simctl shutdown $u"
        $stopped += $u
    }
    return $stopped
}
