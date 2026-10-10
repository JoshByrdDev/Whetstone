# ============================================================
# Send one test notification through the channel on the Settings form.
#
# Run by the dashboard's "Send test" button (POST /api/notifications/test), which writes the form's
# notification fields - saved or not - to a temp file and names it here. Going through
# notifiers.ps1 rather than a second sender in C# is the point: a test that passes is a test of the
# exact code the heartbeat will run.
#
# Ignores which events are ticked: asking for a test is asking for a message.
# Writes ONE line of JSON to stdout - {"ok":bool,"message":string} - and always exits 0; the caller
# reads the verdict from that line, not from the exit code.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================
param(
    [Parameter(Mandatory=$true)][string]$ConfigFile,
    [string]$DefaultsFile = ''
)

# The verdict line is read back as UTF-8; an exception message can carry anything.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$DataRoot = $PSScriptRoot
function Write-Log { param([string]$Message) }
. (Join-Path $DataRoot "notifiers.ps1")

function Out-Verdict([bool]$Ok, [string]$Message) {
    @{ ok = $Ok; message = $Message } | ConvertTo-Json -Compress
    exit 0
}

$files = @($ConfigFile)
if ($DefaultsFile) { $files += $DefaultsFile }

try {
    $cfg = Get-NotificationConfig -Files $files
    $ch  = $cfg.Channel
    if (-not (& $ch.IsConfigured $cfg)) {
        $need = if ($ch.Key -eq 'ntfy') { 'a topic' } else { 'a webhook URL' }
        Out-Verdict $false "$($ch.DisplayName) needs $need before it can send."
    }
    $when = Get-Date -Format 'HH:mm:ss'
    & $ch.Send $cfg "Whetstone test" "Test notification from Whetstone Settings at $when. If you can read this, the heartbeat can reach you here."
    Out-Verdict $true "Sent via $($ch.DisplayName) ($(& $ch.Describe $cfg))."
}
catch {
    Out-Verdict $false "$($_.Exception.Message)"
}
