# Exercises data/heartbeat-hosts.ps1 - the orchestrator's berths on worker hosts - against a stubbed
# board host API: filling them, reading their runs back, giving up on them, and adopting them at
# startup before the lease reset.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File data/heartbeat-hosts.tests.ps1
#
# ASCII ONLY - see heartbeat-common.ps1.

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'heartbeat-hosts.ps1')

$DashboardUrl = 'http://board.test:5338'
$StewardSlot = 4
$StopFile = Join-Path ([IO.Path]::GetTempPath()) ("whetstone-hosts-nostop-" + [guid]::NewGuid().ToString("N"))
$script:fail = 0
$script:total = 0

function Check {
    param([string]$What, $Got, $Want)
    $script:total++
    if ("$Got" -eq "$Want") { Write-Host "ok    $What" -ForegroundColor DarkGray }
    else { $script:fail++; Write-Host "FAIL  $What - expected '$Want', got '$Got'" -ForegroundColor Red }
}

# ---- The stubbed board host ---------------------------------------------------------------

$script:Hosts = @()
$script:HostsDown = $false
$script:OrdersDown = $false
$script:Claims = New-Object System.Collections.Queue
$script:ClaimHosts = @()
$script:Orders = @()
$script:Released = @()
$script:Idle = @()

$script:Placed = @()
$script:Refuse = @()
$script:DispatchView = $null
$script:DispatchDown = $false
$HeartbeatWorkers = 2

function Write-Log { param([string]$Message) }
function Send-DashboardEvent { param($Type, $Text, $Agent, [Alias('Error')]$Err) if ($Text -eq 'idle') { $script:Idle += $Agent } }
function Send-LeaseRelease { param([string]$TicketId, [string]$Reason) $script:Released += $TicketId }
function Add-WorkedTicket { param($TicketId, $Project) }

# This machine's half, as heartbeat.ps1 defines it, over the same queue the hosts claim from.
function Get-WorkerCount { return @($script:Agents.Keys | Where-Object { $_ -lt $StewardSlot }).Count }
function Get-FreeSlot {
    for ($i = 1; $i -le $HeartbeatWorkers; $i++) { if (-not $script:Agents.ContainsKey($i)) { return $i } }
    return 0
}
function Invoke-ClaimNext {
    $script:ClaimHosts += '@board'
    if ($script:Refuse -contains '@board') { return $null }
    if ($script:Claims.Count -gt 0) { return $script:Claims.Dequeue() }
    return $null
}
function Start-Agent {
    param($Claim, [int]$Slot)
    $script:Agents[$Slot] = @{ Process = 'local'; TicketId = "$($Claim.ticket.id)" }
    $script:Placed += "@board:$($Claim.ticket.id)"
    return $true
}

function Invoke-RestMethod {
    param([string]$Uri, [string]$Method, [string]$ContentType, [string]$Body, [int]$TimeoutSec)
    $path = $Uri.Substring($DashboardUrl.Length)
    if ($path -eq '/api/hosts/dispatch') {
        if ($script:DispatchDown) { throw "connection refused" }
        return $script:DispatchView
    }
    if ($path -eq '/api/hosts') {
        if ($script:HostsDown) { throw "connection refused" }
        if ($script:HostsNotFound) {
            $resp = New-Object PSObject -Property @{ StatusCode = 404 }
            $ex = New-Object System.Exception "404 Not Found"
            $ex | Add-Member -NotePropertyName Response -NotePropertyValue $resp -Force
            throw $ex
        }
        return $script:Hosts
    }
    if ($path -eq '/api/heartbeat/claim-next') {
        $claimHost = ($Body | ConvertFrom-Json).host
        $script:ClaimHosts += $claimHost
        $next = if ($script:Refuse -contains $claimHost) { $null }
                elseif ($script:Claims.Count -gt 0) { $script:Claims.Dequeue() } else { $null }
        return [pscustomobject]@{ claimed = $next }
    }
    if ($path -like '/api/hosts/*/orders') {
        if ($script:OrdersDown) { throw "500" }
        $order = [pscustomobject]@{ path = $path; body = ($Body | ConvertFrom-Json) }
        $script:Orders += $order
        $script:Placed += "$($path.Split('/')[3]):$($order.body.claim.ticket.id)"
        return $null
    }
    throw "unexpected call to $Uri"
}

function New-HostView {
    param([string]$Name = 'bravo-host', [int]$FirstSlot = 5, [int]$Slots = 2, [bool]$Dispatchable = $true,
          [bool]$Online = $true, $LastCheckin = (Get-Date).ToUniversalTime().ToString('o'), $Runs = @())
    return [pscustomobject]@{ name = $Name; firstSlot = $FirstSlot; slots = $Slots; dispatchable = $Dispatchable
                              online = $Online; lastCheckin = $LastCheckin; runs = @($Runs) }
}

function New-Claim {
    param([int]$Ticket, [int]$RunId = (800 + $Ticket))
    return [pscustomobject]@{ ticket = [pscustomobject]@{ id = $Ticket; phase = 'NeedsImplementation' }
                              project = [pscustomobject]@{ key = 'alpha' }; skill = 'implement'; runId = $RunId }
}

function New-Run { param([int]$Slot, [int]$Ticket, [int]$RunId, [bool]$Exited = $false, $Code = $null)
    return [pscustomobject]@{ slot = $Slot; ticketId = $Ticket; runId = $RunId; project = 'alpha'; exited = $Exited
                              exitCode = $Code; startedAt = (Get-Date).ToUniversalTime().ToString('o') }
}

function Reset-Board {
    $script:Agents = @{}
    $script:Hosts = @()
    $script:HostsDown = $false
    $script:OrdersDown = $false
    $script:Claims.Clear()
    $script:ClaimHosts = @()
    $script:Orders = @()
    $script:Released = @()
    $script:Idle = @()
    $script:LastHosts = $null
    $script:Placed = @()
    $script:Refuse = @()
    $script:DispatchView = [pscustomobject]@{ mode = 'fill'; order = @() }
    $script:DispatchDown = $false
    $script:Dispatch = $null
}

function Set-Dispatch { param([string]$Mode, [string[]]$Order = @())
    $script:DispatchView = [pscustomobject]@{ mode = $Mode; order = @($Order) }
}

# A remote agent as Start-RemoteAgent books one, ordered $Ago seconds ago.
function Set-RemoteAgent { param([int]$Slot, [int]$Ticket, [int]$RunId, [bool]$Seen = $false, [int]$Ago = 0, [string]$HostName = 'bravo-host')
    $script:Agents[$Slot] = @{ Process = $null; Host = $HostName; RunId = $RunId; Seen = $Seen; TicketId = "$Ticket"
                               Project = 'alpha'; Phase = 'x'; ClaimFile = $null; StartedAt = (Get-Date).AddSeconds(-$Ago) }
}

# ---- Filling --------------------------------------------------------------------------------
# The worker hosts alone first (-LocalOpen $false, as when this machine is at its usage limit), then
# both together in each order and mode.
Write-Host "== Start-Fill, worker hosts"
Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
foreach ($t in 11, 12, 13) { $script:Claims.Enqueue((New-Claim $t)) }
$null = Get-WorkerHosts
$n = Start-Fill -LocalOpen $false
Check 'fills every free berth and no more' $n 2
Check 'berths numbered from the host block' ((@($script:Agents.Keys) | Sort-Object) -join ',') '5,6'
Check 'claims name the host' (($script:ClaimHosts | Select-Object -Unique) -join ',') 'bravo-host'
Check 'the third ticket was never claimed' $script:Claims.Count 1
Check 'orders carry slot, run id and the claim' "$($script:Orders[0].body.kind) $($script:Orders[0].body.slot) $($script:Orders[0].body.runId) $($script:Orders[0].body.claim.ticket.id)" 'start 5 811 11'
Check 'orders go to the host' $script:Orders[0].path '/api/hosts/bravo-host/orders'
Check 'a remote agent has no process here' ($null -eq $script:Agents[5].Process) $true
Check 'a remote agent is not yet seen' $script:Agents[5].Seen $false

Reset-Board
$script:Hosts = @(New-HostView -Dispatchable $false)
$script:Claims.Enqueue((New-Claim 11))
$null = Get-WorkerHosts
Check 'a host that is not dispatchable is not claimed for' (Start-Fill -LocalOpen $false) 0
Check '... and not asked' $script:ClaimHosts.Count 0

Reset-Board
$script:Claims.Enqueue((New-Claim 11))
Check 'hosts unknown: nothing is claimed' (Start-Fill -LocalOpen $false) 0

Reset-Board
$script:Hosts = @(New-HostView -Slots 1)
$script:Claims.Enqueue((New-Claim -Ticket 11 -RunId 0))
$null = Get-WorkerHosts
Check 'a claim with no run id is not sent' (Start-Fill -LocalOpen $false) 0
Check '... and is given back' ($script:Released -join ',') '11'

Reset-Board
$script:Hosts = @(New-HostView -Slots 1)
$script:Claims.Enqueue((New-Claim 11))
$script:OrdersDown = $true
$null = Get-WorkerHosts
Check 'an order that cannot be placed starts nothing' (Start-Fill -LocalOpen $false) 0
Check '... gives the claim back' ($script:Released -join ',') '11'
Check '... and books no agent' $script:Agents.Count 0

Write-Host "== Start-Fill, order and mode"
function New-Tickets { param([int]$Count) for ($i = 0; $i -lt $Count; $i++) { $script:Claims.Enqueue((New-Claim (11 + $i))) } }

Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
New-Tickets 5
$null = Get-WorkerHosts
Check 'nothing written: fill, worker hosts first' (Start-Fill) 4
Check '... host full before this machine is given anything' ($script:Placed -join ' ') 'bravo-host:11 bravo-host:12 @board:13 @board:14'
Check '... and nothing past every berth' $script:Claims.Count 1

Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
Set-Dispatch -Mode 'fill' -Order @('@board', 'bravo-host')
New-Tickets 4
$null = Get-WorkerHosts
$null = Start-Fill
Check 'fill, this machine first' ($script:Placed -join ' ') '@board:11 @board:12 bravo-host:13 bravo-host:14'

Reset-Board
$script:Hosts = @((New-HostView -Name 'r1' -FirstSlot 5 -Slots 2), (New-HostView -Name 'r2' -FirstSlot 7 -Slots 2))
Set-Dispatch -Mode 'spread' -Order @('r1', '@board', 'r2')
New-Tickets 3
$null = Get-WorkerHosts
$null = Start-Fill
Check 'spread: one each, in the order' ($script:Placed -join ' ') 'r1:11 @board:12 r2:13'

Reset-Board
$script:Hosts = @((New-HostView -Name 'r1' -FirstSlot 5 -Slots 2), (New-HostView -Name 'r2' -FirstSlot 7 -Slots 2))
Set-Dispatch -Mode 'spread' -Order @('r1', '@board', 'r2')
New-Tickets 7
$null = Get-WorkerHosts
Check 'spread: round again once each has one, until every berth is full' (Start-Fill) 6
Check '... in the order each time' ($script:Placed -join ' ') 'r1:11 @board:12 r2:13 r1:14 @board:15 r2:16'

Reset-Board
$script:Hosts = @(New-HostView -Name 'r1' -Slots 2)
Set-RemoteAgent -Slot 5 -Ticket 99 -RunId 899 -HostName 'r1'
Set-Dispatch -Mode 'spread' -Order @('r1', '@board')
New-Tickets 1
$null = Get-WorkerHosts
$null = Start-Fill
Check 'spread counts what is already running, not just this pass' ($script:Placed -join ' ') '@board:11'

Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
$script:Refuse = @('bravo-host')
New-Tickets 3
$null = Get-WorkerHosts
$null = Start-Fill
Check 'a host with nothing it can take is done for the pass, and only it' ($script:Placed -join ' ') '@board:11 @board:12'
Check '... asked once' (@($script:ClaimHosts | Where-Object { $_ -eq 'bravo-host' }).Count) 1

Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
Set-Dispatch -Mode 'spread' -Order @('bravo-host', '@board')
$script:Refuse = @('@board')
New-Tickets 3
$null = Get-WorkerHosts
$null = Start-Fill
Check 'spread: this machine with nothing to take leaves the host to fill' ($script:Placed -join ' ') 'bravo-host:11 bravo-host:12'

Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
Set-Dispatch -Mode 'fill' -Order @('@board', 'bravo-host')
New-Tickets 3
$null = Get-WorkerHosts
$null = Start-Fill -LocalOpen $false
Check 'this machine at its usage limit is left out, whatever its place' ($script:Placed -join ' ') 'bravo-host:11 bravo-host:12'
Check '... and never asked' (@($script:ClaimHosts | Where-Object { $_ -eq '@board' }).Count) 0

Reset-Board
$script:Hosts = @((New-HostView -Name 'r1' -FirstSlot 5 -Dispatchable $false), (New-HostView -Name 'r2' -FirstSlot 7 -Slots 1))
Set-Dispatch -Mode 'spread' -Order @('r1', 'r2', '@board')
New-Tickets 2
$null = Get-WorkerHosts
$null = Start-Fill
Check 'a host that is not dispatchable keeps its place but is given nothing' ($script:Placed -join ' ') 'r2:11 @board:12'

Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
New-Tickets 2
$null = Get-WorkerHosts
New-Item -ItemType File -Path $StopFile -Force | Out-Null
Check 'a stop claims nothing' (Start-Fill) 0
Remove-Item $StopFile -Force
Check '... and asks nobody' $script:ClaimHosts.Count 0

Write-Host "== Get-Dispatch"
Reset-Board
$script:DispatchDown = $true
$d = Get-Dispatch
Check 'never read: fill, in no written order' "$($d.mode) $(@($d.order).Count)" 'fill 0'
$script:DispatchDown = $false
Set-Dispatch -Mode 'spread' -Order @('@board', 'bravo-host')
$null = Get-Dispatch
$script:DispatchDown = $true
$d = Get-Dispatch
Check 'unreadable after a good read: the last answer stands' "$($d.mode) $($d.order -join ',')" 'spread @board,bravo-host'

Write-Host "== Resolve-DispatchOrder"
Check 'unwritten: hosts in config order, then this machine' ((Resolve-DispatchOrder -Order @() -HostNames @('a', 'b')) -join ',') 'a,b,@board'
Check 'no hosts: this machine alone' ((Resolve-DispatchOrder -Order @() -HostNames @()) -join ',') '@board'
Check 'strangers and repeats dropped; the unlisted go after the board' ((Resolve-DispatchOrder -Order @('B', 'gone', '@board', 'b') -HostNames @('a', 'b', 'c')) -join ',') 'b,@board,a,c'
Check 'the board unlisted goes last' ((Resolve-DispatchOrder -Order @('c') -HostNames @('a', 'b', 'c')) -join ',') 'c,a,b,@board'

# ---- Reading runs back ----------------------------------------------------------------------
Write-Host "== Update-RemoteFleet"
Reset-Board
Set-RemoteAgent -Slot 5 -Ticket 11 -RunId 811
Set-RemoteAgent -Slot 6 -Ticket 12 -RunId 812 -Seen $true
Set-RemoteAgent -Slot 7 -Ticket 13 -RunId 813 -Ago 5
Set-RemoteAgent -Slot 8 -Ticket 14 -RunId 814 -Ago 300
Set-RemoteAgent -Slot 9 -Ticket 15 -RunId 815 -Seen $true
$script:Hosts = @(New-HostView -Slots 5 -Runs @((New-Run 5 11 811), (New-Run 6 12 812 $true 0)))
Update-RemoteFleet
Check 'a running run is kept and marked seen' "$($script:Agents.ContainsKey(5)) $($script:Agents[5].Seen)" 'True True'
Check 'a finished run frees its berth' $script:Agents.ContainsKey(6) $false
Check 'an order not yet picked up is waited for' $script:Agents.ContainsKey(7) $true
Check 'an order never picked up is given up on' $script:Agents.ContainsKey(8) $false
Check 'a run seen and no longer reported is taken as finished' $script:Agents.ContainsKey(9) $false
Check 'only the never-started one is released' ($script:Released -join ',') '14'
Check 'freed berths are reported idle' ((@($script:Idle) | Sort-Object) -join ',') '6,8,9'

Reset-Board
Set-RemoteAgent -Slot 5 -Ticket 11 -RunId 811 -Seen $true -Ago 3600
$script:Hosts = @(New-HostView -Online $false -LastCheckin (Get-Date).AddMinutes(-3).ToUniversalTime().ToString('o'))
Update-RemoteFleet
Check 'a host offline briefly keeps its runs' $script:Agents.ContainsKey(5) $true
$script:Hosts = @(New-HostView -Online $false -LastCheckin (Get-Date).AddMinutes(-11).ToUniversalTime().ToString('o'))
Update-RemoteFleet
Check 'a host offline past the limit loses them' $script:Agents.ContainsKey(5) $false
Check '... and their tickets are released' ($script:Released -join ',') '11'

Reset-Board
Set-RemoteAgent -Slot 5 -Ticket 11 -RunId 811 -Seen $true
$script:Hosts = @(New-HostView -Name 'other-host')
Update-RemoteFleet
Check 'a host removed from the config: its runs are released' "$($script:Agents.ContainsKey(5)) $($script:Released -join ',')" 'False 11'

Reset-Board
Set-RemoteAgent -Slot 5 -Ticket 11 -RunId 811 -Ago 3600
$script:HostsDown = $true
Update-RemoteFleet
Check 'hosts unreadable: nothing changes' "$($script:Agents.ContainsKey(5)) $($script:Released.Count)" 'True 0'

# ---- Adoption -------------------------------------------------------------------------------
Write-Host "== Add-RemoteAgents"
Reset-Board
$script:Agents[1] = @{ Process = 'local'; TicketId = '1' }
$script:Hosts = @(New-HostView -Runs @((New-Run 5 21 921), (New-Run 6 22 922 $true 0)))
$keep = Add-RemoteAgents
Check 'a live remote run is adopted' "$($script:Agents.ContainsKey(5)) $($script:Agents[5].RunId) $($script:Agents[5].Seen)" 'True 921 True'
Check 'a finished one is not' $script:Agents.ContainsKey(6) $false
Check 'its ticket is kept from the reset' (@($keep) -join ',') '21'

Reset-Board
$script:Hosts = @(New-HostView)
$keep = Add-RemoteAgents
Check 'nothing to adopt is an empty list, not unknown' "$($null -eq $keep) $(@($keep).Count)" 'False 0'

Reset-Board
$keep = Add-RemoteAgents
Check 'no worker hosts at all is an empty list, not unknown' "$($null -eq $keep) $(@($keep).Count)" 'False 0'

Reset-Board
$script:HostsNotFound = $true
$keep = Add-RemoteAgents
Check 'an app with no /api/hosts yet has no hosts, so the reset still runs' "$($null -eq $keep) $(@($keep).Count)" 'False 0'
$script:HostsNotFound = $false

Reset-Board
$script:HostsDown = $true
Check 'hosts unreadable: unknown, so the caller resets nothing' ($null -eq (Add-RemoteAgents)) $true

Check 'slots past the steward are remote' "$(Test-RemoteSlot 4) $(Test-RemoteSlot 5)" 'False True'

# ---- Two checkouts on one machine ---------------------------------------------------------------
# A board of its own beside a worker host's checkout: every scan by script name must see only its
# own checkout's processes. Test-RunsScript lives in heartbeat-common.ps1, which cannot be
# dot-sourced here (it loads config), so it is lifted out of its syntax tree.
Write-Host "== Test-RunsScript"
$common = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'heartbeat-common.ps1'), [ref]$null, [ref]$null)
. ([scriptblock]::Create(($common.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -eq 'Test-RunsScript' }, $false) | Select-Object -First 1).Extent.Text))

$mine = 'C:\Whetstone\data\worker.ps1'
Check 'own script, unquoted' (Test-RunsScript -CommandLine "powershell -ExecutionPolicy Bypass -File $mine -ClaimFile x -Agent 1" -Script $mine) $true
Check 'own script, quoted' (Test-RunsScript -CommandLine "powershell -ExecutionPolicy Bypass -File `"$mine`" -Agent 1" -Script $mine) $true
Check 'own script, other case and slashes' (Test-RunsScript -CommandLine 'powershell -File c:/whetstone/DATA/worker.ps1 -Agent 1' -Script $mine) $true
Check "another checkout's worker.ps1 is not ours" (Test-RunsScript -CommandLine 'powershell -File C:\Whetstone-worker\data\worker.ps1 -Agent 5 -RunId 812' -Script $mine) $false
Check 'a file merely ending in the name is not ours' (Test-RunsScript -CommandLine 'powershell -File C:\Whetstone\data\old-worker.ps1' -Script $mine) $false
Check 'a -Command diagnostic that mentions it is not a run' (Test-RunsScript -CommandLine "powershell -Command Get-CimInstance -Filter x | ? CommandLine -like '*-File*worker.ps1*'" -Script $mine) $false
Check 'a relative -File counts as ours, as before' (Test-RunsScript -CommandLine 'powershell -File data\worker.ps1 -Agent 1' -Script $mine) $true
Check 'no -File at all is not a run' (Test-RunsScript -CommandLine 'powershell worker.ps1' -Script $mine) $false
Check 'no command line is not a run' (Test-RunsScript -CommandLine '' -Script $mine) $false
Check "the board's own launch of its heartbeat" (Test-RunsScript -CommandLine '"powershell" -ExecutionPolicy Bypass -File "C:\Whetstone\data\heartbeat.ps1"' -Script 'C:\Whetstone\data\heartbeat.ps1') $true

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
