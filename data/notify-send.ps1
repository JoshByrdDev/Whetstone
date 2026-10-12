# ============================================================
# Send one notification on the dashboard's behalf.
#
# The heartbeat sends its own (notifiers.ps1, Send-Notification). This is the same call for the
# things only the dashboard knows about - a steward asking its owner a question - made through the
# same file for the reason notify-test.ps1 gives: one sender, so what the Settings test proves is
# what every caller gets.
#
# Unlike the test it honours the ticked events: an event nobody asked for sends nothing, and says so.
# The message comes from a file, never an argument - it is prose a model wrote, and a command line
# is no place for that.
#
# Writes ONE line of JSON to stdout - {"sent":bool,"message":string} - and always exits 0.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================
param(
    [Parameter(Mandatory=$true)][string]$EventKey,
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$MessageFile
)

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$DataRoot = $PSScriptRoot
$GlobalDefaultsFile = Join-Path $DataRoot "config.defaults.json"
$GlobalConfigFile   = Join-Path $DataRoot "config.json"
function Write-Log { param([string]$Message) }
. (Join-Path $DataRoot "notifiers.ps1")

function Out-Verdict([bool]$Sent, [string]$Message) {
    @{ sent = $Sent; message = $Message } | ConvertTo-Json -Compress
    exit 0
}

try {
    $cfg = Get-NotificationConfig
    $ch  = $cfg.Channel
    if (-not (& $ch.IsConfigured $cfg)) { Out-Verdict $false "$($ch.DisplayName) is not set up." }
    if ($cfg.Events -notcontains $EventKey) { Out-Verdict $false "'$EventKey' is not ticked in Settings." }

    $message = [System.IO.File]::ReadAllText($MessageFile, [System.Text.Encoding]::UTF8)
    # A Slack webhook has no title of its own, so there the message has to open with it.
    if ($ch.Key -eq 'slack') { $message = "*$Title*`n$message" }
    & $ch.Send $cfg $Title $message
    Out-Verdict $true "Sent via $($ch.DisplayName)."
}
catch {
    Out-Verdict $false "$($_.Exception.Message)"
}
