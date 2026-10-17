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

function Write-Log { param([string]$Message) }
function Send-DashboardEvent { param($Type, $Text, $Agent) if ($Text -eq 'idle') { $script:Idle += $Agent } }
function Send-LeaseRelease { param([string]$TicketId, [string]$Reason) $script:Released += $TicketId }
function Add-WorkedTicket { param($TicketId, $Project) }

function Invoke-RestMethod {
    param([string]$Uri, [string]$Method, [string]$ContentType, [string]$Body, [int]$TimeoutSec)
    $path = $Uri.Substring($DashboardUrl.Length)
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
        $script:ClaimHosts += ($Body | ConvertFrom-Json).host
        $next = if ($script:Claims.Count -gt 0) { $script:Claims.Dequeue() } else { $null }
        return [pscustomobject]@{ claimed = $next }
    }
    if ($path -like '/api/hosts/*/orders') {
        if ($script:OrdersDown) { throw "500" }
        $script:Orders += [pscustomobject]@{ path = $path; body = ($Body | ConvertFrom-Json) }
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
}

# A remote agent as Start-RemoteAgent books one, ordered $Ago seconds ago.
function Set-RemoteAgent { param([int]$Slot, [int]$Ticket, [int]$RunId, [bool]$Seen = $false, [int]$Ago = 0, [string]$HostName = 'bravo-host')
    $script:Agents[$Slot] = @{ Process = $null; Host = $HostName; RunId = $RunId; Seen = $Seen; TicketId = "$Ticket"
                               Project = 'alpha'; Phase = 'x'; ClaimFile = $null; StartedAt = (Get-Date).AddSeconds(-$Ago) }
}

# ---- Filling --------------------------------------------------------------------------------
Write-Host "== Start-RemoteFill"
Reset-Board
$script:Hosts = @(New-HostView -Slots 2)
foreach ($t in 11, 12, 13) { $script:Claims.Enqueue((New-Claim $t)) }
$null = Get-WorkerHosts
$n = Start-RemoteFill
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
Check 'a host that is not dispatchable is not claimed for' (Start-RemoteFill) 0
Check '... and not asked' $script:ClaimHosts.Count 0

Reset-Board
$script:Claims.Enqueue((New-Claim 11))
Check 'hosts unknown: nothing is claimed' (Start-RemoteFill) 0

Reset-Board
$script:Hosts = @(New-HostView -Slots 1)
$script:Claims.Enqueue((New-Claim -Ticket 11 -RunId 0))
$null = Get-WorkerHosts
Check 'a claim with no run id is not sent' (Start-RemoteFill) 0
Check '... and is given back' ($script:Released -join ',') '11'

Reset-Board
$script:Hosts = @(New-HostView -Slots 1)
$script:Claims.Enqueue((New-Claim 11))
$script:OrdersDown = $true
$null = Get-WorkerHosts
Check 'an order that cannot be placed starts nothing' (Start-RemoteFill) 0
Check '... gives the claim back' ($script:Released -join ',') '11'
Check '... and books no agent' $script:Agents.Count 0

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

Write-Host ""
if ($script:fail -eq 0) { Write-Host "ALL $($script:total) CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "$($script:fail) of $($script:total) FAILED" -ForegroundColor Red }
exit $script:fail
