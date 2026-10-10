# ============================================================
# Where the heartbeat's notifications go, and how to speak to each place.
#
# The same shape as providers.ps1: one descriptor per channel, holding everything that differs
# between them, and nothing downstream that knows which one is live. A caller names an EVENT and a
# message; whether that becomes a Slack webhook post or an ntfy push is decided here and only here.
#
# The table is mirrored in ConfigEndpoints.NotificationChannels on the dashboard side, which is what
# the Settings panel renders its picker from. A channel exists when it is in both.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

# ---- Channel descriptors ---------------------------------------------------

# Every channel takes the same two things - the live settings (Get-NotificationConfig) and one
# message - and says whether it has what it needs to send. Unknown names fall back to slack, the
# incumbent, for the same reason Get-AgentProvider falls back to claude: a typo in config.json must
# not quietly change behaviour for someone who never touched this setting.
function Get-NotificationChannel {
    param([string]$Name)

    switch ("$Name".Trim().ToLowerInvariant()) {
        'ntfy' {
            return [pscustomobject]@{
                Key         = 'ntfy'
                DisplayName = 'ntfy'
                IsConfigured = { param($cfg) [bool]$cfg.NtfyTopic }
                Describe     = { param($cfg) "topic '$($cfg.NtfyTopic)' on $($cfg.NtfyServer)" }
                Send = {
                    param($cfg, [string]$Title, [string]$Message)
                    $uri = ($cfg.NtfyServer.TrimEnd('/')) + '/' + [Uri]::EscapeDataString($cfg.NtfyTopic)
                    # ntfy reads the title and tags from headers and the body as the message itself.
                    # Headers are latin-1 on the wire, so the title stays plain ASCII; the body is
                    # sent as UTF-8 BYTES, because PS 5.1 encodes a string body as ISO-8859-1 and
                    # would mangle anything a ticket title carried in.
                    $headers = @{ Title = $Title; Tags = 'zzz' }
                    if ($cfg.NtfyToken) { $headers['Authorization'] = "Bearer $($cfg.NtfyToken)" }
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Message)
                    Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType 'text/plain; charset=utf-8' -Body $bytes -TimeoutSec 10 | Out-Null
                }
            }
        }
    }

    return [pscustomobject]@{
        Key         = 'slack'
        DisplayName = 'Slack'
        IsConfigured = { param($cfg) [bool]$cfg.SlackWebhookUrl }
        Describe     = { param($cfg) 'webhook set' }
        Send = {
            param($cfg, [string]$Title, [string]$Message)
            # Slack has no separate title on an incoming webhook, so the message carries its own
            # first line - which is already the headline for every notification we send.
            $body = @{ text = $Message } | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri $cfg.SlackWebhookUrl -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10 | Out-Null
        }
    }
}

# ---- Live settings ---------------------------------------------------------

# The ticked events, from whichever key the object carries. `notificationEvents` is the name now;
# `slackEvents` is what a config.json written before there was a choice of channel still holds, and
# honouring it means nobody's unticked box comes back ticked on upgrade. $null means "this object
# says nothing", which is different from an empty list.
function Get-NotificationEventsFrom {
    param($Obj)
    if (-not $Obj) { return $null }
    if ($null -ne $Obj.notificationEvents) { return ,@($Obj.notificationEvents) }
    if ($null -ne $Obj.slackEvents)        { return ,@($Obj.slackEvents) }
    return $null
}

# Everything a channel needs, read fresh at the moment of sending rather than trusted from startup.
# The orchestrator runs for days; the alternative to a live read is restarting it to pick up a
# checkbox, and restarting it kills whatever its agents are in the middle of. config.json is layered
# over config.defaults.json exactly as at startup, and a file that has gone missing or unparseable
# mid-run simply contributes nothing.
#
# Present-but-empty is an answer, not a gap: clearing a field in Settings writes "" to config.json,
# and reading that as "nothing said here" would go on notifying from the defaults. Only an ABSENT
# key falls through. Same rule for the event list, where [] means the user unticked everything.
#
# -Files replaces the two config files, highest precedence first. Only notify-test.ps1 passes it, to
# test what is on the Settings form before it has been saved.
function Get-NotificationConfig {
    param([string[]]$Files = @($GlobalConfigFile, $GlobalDefaultsFile))
    $layers = @()
    foreach ($path in $Files) {
        if (-not (Test-Path $path)) { continue }
        try { $layers += (Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { }
    }

    $pick = {
        param([string]$Name, $Fallback)
        foreach ($l in $layers) { if ($null -ne $l.$Name) { return $l.$Name } }
        return $Fallback
    }

    $events = $null
    foreach ($l in $layers) {
        $found = Get-NotificationEventsFrom $l
        if ($null -ne $found) { $events = $found; break }
    }
    if ($null -eq $events) { $events = @() }

    $server = "$(& $pick 'ntfyServerUrl' '')".Trim()
    if (-not $server) { $server = 'https://ntfy.sh' }

    return [pscustomobject]@{
        Channel         = (Get-NotificationChannel (& $pick 'notificationChannel' 'slack'))
        Events          = @($events)
        SlackWebhookUrl = "$(& $pick 'slackWebhookUrl' '')".Trim()
        NtfyServer      = $server
        NtfyTopic       = "$(& $pick 'ntfyTopic' '')".Trim()
        NtfyToken       = "$(& $pick 'ntfyToken' '')".Trim()
    }
}

# One line for the startup banner: which channel, and whether it will actually send anything.
function Get-NotificationStatus {
    $cfg = Get-NotificationConfig
    $ch  = $cfg.Channel
    if (-not (& $ch.IsConfigured $cfg)) { return "$($ch.DisplayName), not configured" }
    if ($cfg.Events.Count -eq 0)        { return "$($ch.DisplayName), nothing ticked" }
    return "$($ch.DisplayName) ($(& $ch.Describe $cfg)), notifying on: $($cfg.Events -join ', ')"
}

# ---- Sending ---------------------------------------------------------------

# Send one notification, if the user has asked for that kind. Silence is the ordinary outcome: the
# channel not configured, or the box unticked, and nothing leaves the machine.
#
# Every send names an event key, and the key has to exist in the dashboard's catalogue
# (ConfigEndpoints.NotificationEvents) for anyone to be able to tick it. That is the point of naming
# them: a notification exists exactly when both halves know about it, so nothing can fire unasked
# and nothing offered in Settings can turn out to be wired to nothing.
#
# Timed out inside every channel (10s). A notification is the least important thing this process
# does, and an endpoint that accepts the connection and then says nothing must not hold the fleet
# still. Failures are logged and swallowed for the same reason.
function Send-Notification {
    param(
        [Parameter(Mandatory=$true)][string]$EventKey,
        [Parameter(Mandatory=$true)][string]$Title,
        [Parameter(Mandatory=$true)][string]$Message
    )
    $cfg = Get-NotificationConfig
    $ch  = $cfg.Channel
    if (-not (& $ch.IsConfigured $cfg)) { return }
    if ($cfg.Events -notcontains $EventKey) { return }
    try {
        & $ch.Send $cfg $Title $Message
        Write-Log "  [$($ch.DisplayName)] Sent '$EventKey'."
    }
    catch { Write-Log "  [$($ch.DisplayName)] Failed to send '$EventKey': $($_.Exception.Message)" }
}
