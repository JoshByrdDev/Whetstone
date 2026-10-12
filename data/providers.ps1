# ============================================================
# Which agent CLI the fleet drives, and how to speak to it.
#
# The pipeline used to shell out to `claude` in four places and parse its stream-json in one. This
# file is now the only part of the PowerShell half that knows any of that. Everything downstream -
# the worker loop, the activity feed, the mutation counter, the cycle report - works in terms of a
# CANONICAL event, and each provider is mapped onto it here:
#
#   @{ Kind = 'tool_use'; ToolName = 'mcp__tickets__add_comment'; Detail = 'ticketId=42' }
#   @{ Kind = 'text';     Text = '...' }
#   @{ Kind = 'thinking'; Text = '...' }
#   @{ Kind = 'result';   Text; DurationMs; NumTurns; IsError;
#                         InputTokens; OutputTokens; CacheReadTokens; CacheCreateTokens }
#
# and two that say where a run's context went, which only claude's stream reports:
#
#   @{ Kind = 'context';     Tokens }                      one model call's whole prompt
#   @{ Kind = 'tool_result'; ToolUseId; Chars; Images }    matched to a tool_use by its Id
#
# The tool NAME is part of that contract rather than a display detail. The mutation counter, the
# read-only tool list and the dashboard's feed all key off the `mcp__<server>__<tool>` shape, so
# Codex - which reports the server and the tool in separate fields - and Gemini - which namespaces
# them as `server__tool` - are both reassembled into that shape here. One mapping in one file,
# instead of a second naming convention taught to four other places.
#
# ASCII ONLY - see heartbeat-common.ps1.
# ============================================================

# ---- Provider descriptors --------------------------------------------------

# Everything that differs between CLIs, in one object. Anything not on here is shared behaviour and
# belongs in the caller. Unknown names fall back to claude, which is the incumbent: a typo in
# config.json must not silently stop the fleet working.
function Get-AgentProvider {
    param([string]$Name)

    switch ("$Name".Trim().ToLowerInvariant()) {
        'codex' {
            return [pscustomobject]@{
                Key                 = 'codex'
                Exe                 = 'codex'
                DisplayName         = 'OpenAI Codex'
                # What the timeout watchdog is allowed to kill, matched against Win32_Process.Name
                # among THIS worker's descendants and nothing else.
                ProcessPattern      = @('codex*')
                # The conventions file this CLI loads on its own. Naming another one in the prompt
                # would mean the agent is told to follow a file it was never given.
                ConventionsFile     = 'AGENTS.md'
                DefaultWorkerModel  = 'gpt-5.6-sol'
                DefaultReflectModel = 'gpt-5.6-luna'
                SupportsReasoning   = $true
            }
        }
        'gemini' {
            return [pscustomobject]@{
                Key                 = 'gemini'
                Exe                 = 'gemini'
                DisplayName         = 'Google Gemini CLI'
                # 'node*' is not padding. The CLI is an npm package, so on Windows `gemini` is a
                # .cmd shim and the process actually holding the session is node.exe - a pattern of
                # 'gemini*' alone would match nothing and the watchdog would silently stop being a
                # watchdog. It is safe because the search is already confined to the descendants of
                # ONE worker: the only node processes under there are this run's agent and this
                # run's own stdio MCP servers, both of which a timeout is meant to take with it.
                ProcessPattern      = @('gemini*', 'node*')
                ConventionsFile     = 'GEMINI.md'
                # Routing ALIASES rather than dated slugs: Google's concrete ids carry a version and
                # a -preview suffix that both move, and a default naming one goes stale silently -
                # every run failing on a model name the CLI no longer serves. 'pro' does not move.
                DefaultWorkerModel  = 'pro'
                DefaultReflectModel = 'flash'
                # Thinking is a property of the model here, not a CLI dial, so there is nothing for
                # workerReasoningEffort to set.
                SupportsReasoning   = $false
            }
        }
    }

    return [pscustomobject]@{
        Key                 = 'claude'
        Exe                 = 'claude'
        DisplayName         = 'Anthropic Claude Code'
        ProcessPattern      = @('claude*')
        ConventionsFile     = 'CLAUDE.md'
        DefaultWorkerModel  = 'claude-opus-5-5'
        DefaultReflectModel = 'haiku'
        # --effort. Claude Code's own default is per MODEL rather than per CLI - Opus 5 ran at high,
        # Opus 5.5 at medium - so leaving it unset let a model change quietly lower how hard the
        # fleet thinks, with nothing in Settings to say so.
        SupportsReasoning   = $true
    }
}

# What a model name looks like when it belongs to a given provider.
#
# One table rather than a foreign-model pattern per provider, because with three of them the
# question is no longer "is this the other one's" but "is this unmistakably SOMEBODY else's", and
# three overlapping unions would be three things to keep in step. The C# half reads the same table
# from AgentModels in Services/AgentProvider.cs; the two must not disagree about which models a
# provider will take, because the dashboard drops a mismatched one on save and the heartbeat
# corrects whatever it finds in the file.
#
# Deliberately narrow, exactly as it was with two providers: a name matching NOTHING here belongs to
# nobody in particular and is passed through, so a model slug released after this was written still
# works with no code change.
function Get-AgentModelSignatures {
    return @(
        [pscustomobject]@{ Key = 'claude'; Own = '^(claude|opus|sonnet|haiku|fable)' }
        [pscustomobject]@{ Key = 'codex';  Own = '^gpt' }
        # pro / flash / auto are the CLI's own routing aliases, and no other provider's models begin
        # with them.
        [pscustomobject]@{ Key = 'gemini'; Own = '^(gemini|pro|flash|auto)' }
    )
}

# Could this model name plausibly be this provider's? False only when it is unmistakably another's.
function Test-AgentModelBelongsTo {
    param([string]$ProviderKey, [string]$Model)

    $m = "$Model".Trim()
    if (-not $m) { return $false }
    foreach ($sig in (Get-AgentModelSignatures)) {
        if ($sig.Key -eq $ProviderKey) { continue }
        if ($m -match $sig.Own) { return $false }
    }
    return $true
}

# Which file the prompt should tell this run to treat as the repo's code conventions.
#
# The provider's own convention is only a PREFERENCE. What decides it is which file is actually in
# the worktree: a repo that has kept its conventions in CLAUDE.md for a year does not grow an
# AGENTS.md because the provider was switched in Settings, and naming a file that is not there would
# silently drop every convention the project has - branch naming, build commands, the QA safety
# check - on the run that was supposed to follow them.
#
# With neither present the preferred name is returned anyway, which is what the prompt said before
# any of this existed: it names the file that governs, and an agent that cannot find it carries on.
function Get-ConventionsFile {
    param($Provider, [string]$WorkDir)

    $preferred = $Provider.ConventionsFile
    # The preferred name first, then every other convention file the fleet knows about, so a repo
    # that keeps its rules in one of them is found whichever CLI is driving.
    $order = @($preferred) + (@('CLAUDE.md', 'AGENTS.md', 'GEMINI.md') | Where-Object { $_ -ne $preferred })
    foreach ($name in $order) {
        if ($WorkDir -and (Test-Path (Join-Path $WorkDir $name))) { return $name }
    }
    return $preferred
}

# Guard against a model name left over from the other provider. Switching provider in Settings and
# forgetting the model would otherwise hand `claude-opus-5` to codex, which fails per run, every run,
# with an error that says nothing about the real cause. Only an unambiguous mismatch is corrected -
# anything else is passed through untouched, so a new model slug never needs code here to work.
function Resolve-AgentModel {
    param($Provider, [string]$Model, [string]$Fallback)

    $m = "$Model".Trim()
    if (-not $m) { return $Fallback }
    if (-not (Test-AgentModelBelongsTo -ProviderKey $Provider.Key -Model $m)) {
        if (Get-Command Write-Log -ErrorAction SilentlyContinue) {
            Write-Log "  Model '$m' is not an $($Provider.DisplayName) model - falling back to '$Fallback'."
        }
        return $Fallback
    }
    return $m
}

# ---- Per-run tool configuration --------------------------------------------

function ConvertTo-TomlString {
    param([string]$Value)
    # TOML basic strings, not literal strings: a Windows path is full of backslashes and a literal
    # string cannot escape the one character it might also contain.
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

# The dashboard's ticket tools (HTTP) plus whatever stdio servers this run earned, in Codex's TOML.
function ConvertTo-CodexMcpToml {
    param([hashtable]$Servers)

    $sb = New-Object System.Text.StringBuilder
    foreach ($name in $Servers.Keys) {
        $s = $Servers[$name]
        [void]$sb.AppendLine("[mcp_servers.$name]")
        if ($s.url) {
            [void]$sb.AppendLine("url = $(ConvertTo-TomlString $s.url)")
        }
        else {
            [void]$sb.AppendLine("command = $(ConvertTo-TomlString $s.command)")
            $rendered = @()
            foreach ($a in $s.args) { $rendered += (ConvertTo-TomlString "$a") }
            [void]$sb.AppendLine("args = [$($rendered -join ', ')]")
        }
        [void]$sb.AppendLine("")
    }
    return $sb.ToString()
}

# Where Codex keeps config and credentials when we have not redirected it.
function Get-CodexUserHome {
    if ($env:CODEX_HOME) { return $env:CODEX_HOME }
    return (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex')
}

# Where the Gemini CLI keeps config and credentials when we have not redirected it. GEMINI_CLI_HOME
# names the PARENT of the .gemini folder rather than the folder itself, which is the one detail worth
# getting right here - point it at the folder and the CLI quietly makes a .gemini INSIDE it and finds
# none of the credentials we just copied.
function Get-GeminiUserDir {
    $root = $env:GEMINI_CLI_HOME
    if (-not $root) { $root = [Environment]::GetFolderPath('UserProfile') }
    return (Join-Path $root '.gemini')
}

# The user's chosen sign-in method, lifted out of their own settings file.
#
# Without it a redirected run has the credentials but not the record of what KIND they are, and
# settling that is an interactive question - which a headless run cannot be asked.
function Get-GeminiSecuritySetting {
    param([string]$UserDir)

    $path = Join-Path $UserDir 'settings.json'
    if (-not (Test-Path $path)) { return $null }
    try {
        # ReadAllText, not Get-Content: Get-Content decodes in the ANSI code page, and this is the
        # user's file rather than one we wrote.
        $obj = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop
        if ($obj -and $obj.PSObject.Properties['security']) { return $obj.security }
    }
    catch { }   # their file, their problem: this run just goes without.
    return $null
}

# The dashboard's servers in Gemini's own settings shape. Two differences from the JSON Claude gets.
#
# An HTTP server's address goes under `httpUrl`. Gemini's `url` field means Server-Sent Events, which
# is a different transport - putting a streamable-HTTP endpoint there does not degrade, it fails to
# connect, and the run then discovers minutes later that it has no way to record anything.
#
# And every server is marked trusted, which suppresses the per-tool confirmation. That is the same
# class of failure as openai/codex#24135: a headless run has nobody to answer a confirmation prompt,
# and every phase of this pipeline drives the ticket through MCP tools. --approval-mode yolo already
# covers it; this is the belt to that pair of braces, and it costs nothing because these are the
# servers WE wrote into a config directory of our own.
function ConvertTo-GeminiMcpServers {
    param([hashtable]$Servers)

    $out = @{}
    foreach ($name in $Servers.Keys) {
        $s = $Servers[$name]
        $entry = @{ trust = $true }
        if ($s.url) {
            if ("$($s.type)" -eq 'sse') { $entry.url = "$($s.url)" }
            else { $entry.httpUrl = "$($s.url)" }
        }
        else {
            $entry.command = "$($s.command)"
            $entry.args = @($s.args)
        }
        $out[$name] = $entry
    }
    return $out
}

# Give this run its own tool configuration, seeing exactly the servers we hand it and no others.
#
# That isolation is not tidiness. Two agents on the same project used to overwrite a shared config
# file between one's write and the other's read, and the loser started pointed at the wrong worktree.
# Both providers get a per-TICKET config for that reason; they just get there differently.
#
#   claude: --mcp-config <file> --strict-mcp-config, which is what "only these servers" means there.
#   codex:  CODEX_HOME redirected at a per-run directory holding only our config.toml. Codex has no
#           --mcp-config, and its servers otherwise come from the user's own ~/.codex/config.toml -
#           so replacing the whole directory IS the strict flag.
#
# Redirecting CODEX_HOME also moves where Codex looks for its LOGIN, so auth.json is copied across.
# Nothing else is: inheriting the user's config.toml would be exactly the ambient-server leak this
# exists to prevent.
#
# Returns the extra CLI args, the environment to set, and a directory to delete when the run is done
# (it holds a copy of a credential, so it does not outlive the run).
function New-AgentToolSetup {
    param($Provider, [hashtable]$Servers, [string]$RunDir, [string]$Key, [string]$Project)

    if ($Provider.Key -eq 'codex') {
        # NOT $home - that is PowerShell's own variable for the user's profile directory.
        $runHome = Join-Path $RunDir "codex-home.$Key"
        if (-not (Test-Path $runHome)) { New-Item -ItemType Directory -Path $runHome -Force | Out-Null }

        $auth = Join-Path (Get-CodexUserHome) 'auth.json'
        if (Test-Path $auth) {
            Copy-Item -Path $auth -Destination (Join-Path $runHome 'auth.json') -Force
        }
        else {
            Write-Log "  WARNING: no Codex login found at $auth - this run will not be able to sign in. Run 'codex login' once."
        }

        # BOM-less: PowerShell 5.1's -Encoding UTF8 writes one, and a BOM ahead of the first table
        # header is not something every TOML parser forgives.
        $cfg = Join-Path $runHome 'config.toml'
        [System.IO.File]::WriteAllText($cfg, (ConvertTo-CodexMcpToml -Servers $Servers), (New-Object System.Text.UTF8Encoding($false)))

        return [pscustomobject]@{
            Args    = @()
            Env     = @{ CODEX_HOME = $runHome }
            Cleanup = $runHome
            Path    = $cfg
        }
    }

    if ($Provider.Key -eq 'gemini') {
        # NOT $home - that is PowerShell's own variable for the user's profile directory. And note
        # the extra level: GEMINI_CLI_HOME is the PARENT of the .gemini directory.
        $runHome = Join-Path $RunDir "gemini-home.$Key"
        $cfgDir  = Join-Path $runHome '.gemini'
        if (-not (Test-Path $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null }

        # Redirecting the home moves where the CLI looks for its LOGIN as well as its config, so the
        # cached OAuth files come across. An API key needs none of this - it arrives in the inherited
        # environment as GEMINI_API_KEY - which is why its absence is only a warning when there is no
        # key either.
        $userDir = Get-GeminiUserDir
        $signedIn = $false
        foreach ($name in @('oauth_creds.json', 'google_accounts.json')) {
            $src = Join-Path $userDir $name
            if (Test-Path $src) {
                Copy-Item -Path $src -Destination (Join-Path $cfgDir $name) -Force
                $signedIn = $true
            }
        }
        if (-not $signedIn -and -not $env:GEMINI_API_KEY) {
            Write-Log "  WARNING: no Gemini login found in $userDir and no GEMINI_API_KEY - this run will not be able to sign in. Run 'gemini' once and sign in."
        }

        $settings = @{ mcpServers = (ConvertTo-GeminiMcpServers -Servers $Servers) }
        $security = Get-GeminiSecuritySetting -UserDir $userDir
        if ($security) { $settings.security = $security }

        # BOM-less, same reason as the Codex config: 5.1's -Encoding UTF8 writes one, and a BOM ahead
        # of the opening brace is not something every JSON parser forgives.
        $cfg = Join-Path $cfgDir 'settings.json'
        [System.IO.File]::WriteAllText($cfg, ($settings | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))

        return [pscustomobject]@{
            Args    = @()
            Env     = @{
                GEMINI_CLI_HOME = $runHome
                # The CLI asks whether a folder is trusted before it will use its tools there, and a
                # headless run has nobody to ask. The folder is a git worktree the fleet made itself.
                GEMINI_CLI_TRUST_WORKSPACE = 'true'
                # The stream is parsed as JSON. Escape codes in it would not be.
                NO_COLOR = '1'
            }
            Cleanup = $runHome
            Path    = $cfg
        }
    }

    $path = Join-Path $RunDir "mcp-config.$Key.json"
    (@{ mcpServers = $Servers } | ConvertTo-Json -Depth 6) | Set-Content -Path $path -Encoding UTF8

    # The run's hooks and settings ride along in a per-run settings file. It has to be a FILE rather
    # than the JSON string --settings also accepts: 5.1 truncates an argument at its first double
    # quote, which is the same bug Assert-NoPromptInArgs exists for.
    #
    # They go here rather than in any project's own .claude/settings.json for two reasons. They
    # apply to every project the fleet ever picks up, including ones that do not exist yet; and
    # they never touch the human's interactive sessions in those repos, where reaching for `az` is
    # a legitimate thing to do with somebody sitting there to answer for it.
    #
    # The one exception is the TFVC rule. TFVC has exactly one writer, interactive or not, so a
    # TFVC project's own .claude/settings.json may run tfvc-guard.ps1 too - see that file's
    # header. A worker in such a worktree picks that settings file up from git as well and so
    # runs the TFVC rule twice, once there and once inside worker-guard.ps1. Harmless.
    #
    # Claude only - see the header of worker-guard.ps1 for what that leaves uncovered.

    # No AI attribution trailer on the fleet's commits. Claude Code appends a Co-Authored-By line by
    # default, and it tells the model to do so in a system message the ticket prompt cannot outrank -
    # one ticket shipped one past a prompt rule AND a project note that both forbid it. This setting
    # switches the instruction off at the source, which is the only place it can be switched off.
    # The prompt still carries the rule, because codex and gemini have no equivalent switch.
    $runSettings = @{ includeCoAuthoredBy = $false }

    $hooks = @{}

    # The local-only guard (worker-guard.ps1), as a PreToolUse hook. It is told which project
    # the run is for so that project's own rules (data/projects/<key>/guard.ps1, if it has
    # any) are the ones added to the shared set; told nothing, it applies every project's.
    $guard = Join-Path $PSScriptRoot 'worker-guard.ps1'
    $guardArgs = ''
    if ($Project -match '^[A-Za-z0-9._-]+$') { $guardArgs = " -Project $Project" }
    if (Test-Path $guard) {
        $hooks['PreToolUse'] = @(
            @{
                matcher = 'Bash|PowerShell|.*navigate.*'
                hooks   = @(
                    @{
                        type          = 'command'
                        command       = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$guard`"$guardArgs"
                        timeout       = 15
                        statusMessage = 'Checking this stays on the local machine'
                    }
                )
            }
        )
    }
    else {
        # Loud, because the alternative is a fleet that quietly lost its only hard boundary.
        Write-Log "  WARNING: $guard is missing - this run has NO local-only enforcement."
    }

    # Augment usage (augment-hook.ps1), as a PostToolUse hook on the one retrieval tool: it logs
    # the call to logs/augment-calls.jsonl and asks for a rating in the hand-off comment. Registered
    # whether or not this run has auggie - a matcher that never matches costs nothing.
    $augmentHook = Join-Path $PSScriptRoot 'augment-hook.ps1'
    if (Test-Path $augmentHook) {
        $hooks['PostToolUse'] = @(
            @{
                matcher = 'mcp__auggie__codebase-retrieval'
                hooks   = @(
                    @{
                        type          = 'command'
                        command       = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$augmentHook`""
                        timeout       = 15
                        statusMessage = 'Logging Augment usage'
                    }
                )
            }
        )
    }

    if ($hooks.Count -gt 0) { $runSettings['hooks'] = $hooks }

    $settings = Join-Path $RunDir "settings.$Key.json"
    ($runSettings | ConvertTo-Json -Depth 8) | Set-Content -Path $settings -Encoding UTF8
    $settingsArgs = @('--settings', $settings)

    return [pscustomobject]@{
        Args    = @('--mcp-config', $path, '--strict-mcp-config') + $settingsArgs
        # How long claude waits for each MCP server to start, in ms. The default is about 30s, and
        # playwright via `npx @latest` normally answers in 5 - but on 2026-09-26 it missed that window
        # twice running while another agent was tearing down its stack, and the required-server
        # check blocked a healthy ticket. A slow start costs a minute; a missed one costs the run.
        Env     = @{ MCP_TIMEOUT = '90000' }
        Cleanup = $null
        Path    = $path
    }
}

# ---- Command lines ---------------------------------------------------------

# The one call that does a ticket's work. The prompt travels on STDIN for both providers, and the
# returned object carries it in .Stdin rather than in .Args.
#
# It used to be an argument, on the belief that PowerShell quotes each element of a splatted array
# itself. It does not - not in Windows PowerShell 5.1, which is what runs the fleet. 5.1 builds the
# child's command line by wrapping each argument in double quotes and does NOT escape the double
# quotes already inside it, so the C runtime on the other side ends the argument at the FIRST such
# quote and splits the rest on whitespace. The heartbeat prompt's first quote is the one around the
# ticket title, 398 characters in. Every worker run, every phase, for as long as this has been here,
# received 407 characters of a 10,600-character prompt and improvised the rest of its job from the
# ticket alone - which is why an implement run could hand off without ever seeing the hard rule that
# says commit before you do. Reproduce it with:
#     & node -e "console.log(process.argv.length)" ('a "b" c')     # -> 3 arguments, not 1
#
# Stdin has no such layer: the bytes are the bytes. The reflection call has been piping multi-
# kilobyte prompts this way the whole time, which is the evidence that the path works.
# The effort setting in each CLI's own spelling, or nothing for a CLI without the dial. One place,
# so the worker and the reflection cannot disagree about how it is spelled. Call it as
# @(Get-AgentEffortArgs ...): an empty result is $null otherwise, and `$a += $null` adds an argument.
function Get-AgentEffortArgs {
    param($Provider, [string]$ReasoningEffort)

    $e = "$ReasoningEffort".Trim()
    if (-not $Provider.SupportsReasoning -or -not $e) { return }
    if ($Provider.Key -eq 'codex') {
        # Unquoted deliberately. Codex parses a -c value as TOML and falls back to "it's a
        # string" when that fails, so `=high` and `="high"` arrive as the same thing - and the
        # quoted form never survived the trip anyway: 5.1 strips those quotes on the way out
        # (see Get-AgentWorkerCommand), so this is what codex has always actually received.
        return @('-c', "model_reasoning_effort=$e")
    }
    # A model with no effort dial of its own (haiku) is not an error: the CLI runs it as it would
    # have anyway.
    return @('--effort', $e)
}

function Get-AgentWorkerCommand {
    param(
        $Provider, [string]$Model, [string]$Prompt, [string]$WorkDir,
        $ToolSetup, [string]$ReasoningEffort
    )

    if ($Provider.Key -eq 'codex') {
        $a = @('exec', '--json', '-m', $Model, '--cd', $WorkDir)
        $a += @(Get-AgentEffortArgs -Provider $Provider -ReasoningEffort $ReasoningEffort)
        # Codex cancels every MCP tool call in exec mode unless approvals are bypassed: stdin is
        # closed, the approval reader sees EOF, and EOF reads as "no". Since every phase drives the
        # ticket through MCP tools, there is no partial version of this - openai/codex#24135.
        # The sandbox goes with it (they are one flag), which costs nothing here: the run is already
        # confined to its own git worktree and that is what the isolation was ever resting on.
        $a += '--dangerously-bypass-approvals-and-sandbox'
        # A worktree's .git is a FILE, not a directory. Not every repo check expects that, and being
        # wrong about it would fail the run before the model is ever reached.
        $a += '--skip-git-repo-check'
        $a += $ToolSetup.Args
        $a += '-'              # read the prompt from stdin; positional, and last
        return Assert-NoPromptInArgs ([pscustomobject]@{ Exe = $Provider.Exe; Args = $a; Stdin = $Prompt })
    }

    if ($Provider.Key -eq 'gemini') {
        $a = @('--output-format', 'stream-json', '-m', $Model)
        # Auto-approve, for the same reason Codex gets its bypass: a headless run cannot answer a
        # tool-confirmation prompt, and every phase here drives the ticket through MCP tools. What
        # keeps this honest is that the run is already confined to its own git worktree.
        $a += @('--approval-mode', 'yolo')
        $a += $ToolSetup.Args
        # No prompt flag and no positional `-`: this CLI reads stdin when it is not a terminal, and
        # --prompt is deprecated anyway. Stdin is where the prompt has to go regardless - see the
        # note above about 5.1 truncating an argument at its first double quote.
        return Assert-NoPromptInArgs ([pscustomobject]@{ Exe = $Provider.Exe; Args = $a; Stdin = $Prompt })
    }

    # `-p` with no value after it is claude's "print mode, prompt on stdin".
    $a = @('-p', '--model', $Model)
    $a += @(Get-AgentEffortArgs -Provider $Provider -ReasoningEffort $ReasoningEffort)
    $a += $ToolSetup.Args
    $a += @('--output-format', 'stream-json', '--verbose', '--permission-mode', 'bypassPermissions')
    return Assert-NoPromptInArgs ([pscustomobject]@{ Exe = $Provider.Exe; Args = $a; Stdin = $Prompt })
}

# The guard over the bug above. Nothing about a truncated prompt LOOKS wrong from here - the run
# starts, the model answers, the cycle reports success - so the only way this stays fixed is for
# something to fail loudly the moment a prompt finds its way back onto the command line. An
# argument carrying a double quote is that signal: none of the real ones (flags, model names,
# config paths) has any business containing one.
function Assert-NoPromptInArgs {
    param($Command)
    foreach ($a in $Command.Args) {
        if ("$a".Contains('"')) {
            throw "Refusing to launch the agent: argument '$a' contains a double quote, which Windows PowerShell 5.1 will silently truncate the command line at. Prompts and other quoted text belong on stdin - see Get-AgentWorkerCommand."
        }
    }
    return $Command
}

# The post-cycle reflection: read a run log, report durable facts, write nothing. No tools are
# needed, so Codex gets a real read-only sandbox here rather than the bypass the worker needs.
function Get-AgentReflectionCommand {
    param($Provider, [string]$Model, [string]$ReasoningEffort)

    $effort = @(Get-AgentEffortArgs -Provider $Provider -ReasoningEffort $ReasoningEffort)
    if ($Provider.Key -eq 'codex') {
        return [pscustomobject]@{
            Exe  = $Provider.Exe
            Args = @('exec', '-m', $Model) + $effort + @('--sandbox', 'read-only',
                     '--ask-for-approval', 'never', '--skip-git-repo-check', '-')
        }
    }

    if ($Provider.Key -eq 'gemini') {
        # No tools are needed - the run log is embedded in the prompt - so this one keeps the
        # default approval mode. That is the closest thing this CLI has to Codex's "never": a run
        # that reaches for a tool anyway is refused rather than turned loose on the repo.
        return [pscustomobject]@{ Exe = $Provider.Exe; Args = @('-m', $Model, '--approval-mode', 'default') }
    }
    return [pscustomobject]@{ Exe = $Provider.Exe; Args = @('-p', '--model', $Model) + $effort }
}

# ---- Stream events ---------------------------------------------------------

# Safely read a named property off a parsed JSON object (missing -> $null).
function Get-Prop {
    param($Obj, [string]$Name)
    if ($Obj -and $Obj.PSObject.Properties[$Name]) { return [string]$Obj.PSObject.Properties[$Name].Value }
    return $null
}

function Get-JsonNumber {
    param($Obj, [string]$Name)
    if ($Obj -and $Obj.PSObject.Properties[$Name] -and $null -ne $Obj.$Name) { return $Obj.$Name }
    return $null
}

# The first of several names that is actually present. One tool means one thing across the fleet,
# but each CLI has its own word for the argument - Claude's Read takes file_path, Gemini's takes
# absolute_path - and the caption should say the file either way.
function Get-FirstProp {
    param($Obj, [string[]]$Names)
    foreach ($n in $Names) {
        $v = Get-Prop $Obj $n
        if ($v) { return $v }
    }
    return $null
}

# Turn a tool call into a compact, human-readable target so the activity feed reads like a
# transcript ("Read TicketService.cs", "Bash: dotnet test") instead of a bare, contextless tool name.
# Written against Claude's tool vocabulary; Codex's tools are mapped onto the same names before they
# get here, so one formatter serves both.
function Format-ToolCall {
    param($Block)
    $in = $Block.input
    if (-not $in) { return $null }
    $s = $null
    switch -Wildcard ($Block.name) {
        "Read"         { $s = (Get-FirstProp $in 'file_path','absolute_path','path') -split '[\\/]' | Select-Object -Last 1 }
        "Edit"         { $s = (Get-FirstProp $in 'file_path','absolute_path','path') -split '[\\/]' | Select-Object -Last 1 }
        "Write"        { $s = (Get-FirstProp $in 'file_path','absolute_path','path') -split '[\\/]' | Select-Object -Last 1 }
        "NotebookEdit" { $s = (Get-Prop $in 'notebook_path') -split '[\\/]' | Select-Object -Last 1 }
        "Bash"         { $s = Get-Prop $in 'command' }
        "Grep"         { $s = Get-Prop $in 'pattern'; $g = Get-Prop $in 'glob'; if ($g) { $s = "$s  ($g)" } }
        "Glob"         { $s = Get-Prop $in 'pattern' }
        "ToolSearch"   { $s = Get-Prop $in 'query' }
        "WebFetch"     { $s = Get-Prop $in 'url' }
        "WebSearch"    { $s = Get-Prop $in 'query' }
        "Task"         { $s = Get-Prop $in 'description' }
        "mcp__tickets__*" {
            $parts = @()
            foreach ($k in 'ticketId','id','projectKey','project','phase','skill','status') {
                $v = Get-Prop $in $k; if ($v) { $parts += "${k}=${v}" }
            }
            $s = ($parts -join ' ')
        }
        default {
            foreach ($k in 'file_path','absolute_path','path','command','pattern','query','url','description') {
                $v = Get-Prop $in $k; if ($v) { $s = $v; break }
            }
        }
    }
    if (-not $s) { return $null }
    $s = ($s -replace "\s+", " ").Trim()
    if ($s.Length -gt 120) { $s = $s.Substring(0, 120) + "..." }
    return $s
}

# Codex reports a tool call's arguments as either a JSON object or a JSON string holding one.
function ConvertTo-ArgObject {
    param($Arguments)
    if ($null -eq $Arguments) { return $null }
    if ($Arguments -is [string]) {
        try { return ($Arguments | ConvertFrom-Json -ErrorAction Stop) } catch { return $null }
    }
    return $Arguments
}

# NB: the parameter is ToolInput, not Input - $Input is PowerShell's pipeline enumerator and binding
# a parameter to that name shadows it inside the function.
function New-CanonicalToolUse {
    param([string]$ToolName, $ToolInput)
    return [pscustomobject]@{
        Kind     = 'tool_use'
        ToolName = $ToolName
        Detail   = (Format-ToolCall -Block ([pscustomobject]@{ name = $ToolName; input = $ToolInput }))
    }
}

# Map one line of a provider's event stream onto zero or more canonical events.
#
# $State is a per-run bag the caller owns. Codex needs it: its closing `turn.completed` carries the
# accounting but not the summary text, which arrived earlier as the last agent message.
function ConvertFrom-AgentEvent {
    param($Provider, $Event, $State)

    $out = @()

    if ($Provider.Key -eq 'gemini') {
        return ,(ConvertFrom-GeminiEvent -Event $Event -State $State)
    }

    if ($Provider.Key -eq 'codex') {
        switch ("$($Event.type)") {
            'item.started'   { $out += (ConvertFrom-CodexItem -Event $Event -State $State) }
            'item.updated'   { }
            'item.completed' { $out += (ConvertFrom-CodexItem -Event $Event -State $State) }
            'turn.completed' {
                $u = $Event.usage
                $out += [pscustomobject]@{
                    Kind              = 'result'
                    Text              = $State.LastMessage
                    # Codex reports no wall-clock or turn count on the stream. The worker's own
                    # stopwatch already covers duration; a turn count we do not have is left null
                    # rather than invented, and the metrics panel renders it as unknown.
                    DurationMs        = $null
                    NumTurns          = $null
                    IsError           = $false
                    InputTokens       = (Get-JsonNumber $u 'input_tokens')
                    OutputTokens      = (Get-JsonNumber $u 'output_tokens')
                    # Codex's cached_input_tokens are prompt tokens served from cache, which is what
                    # cache_read_input_tokens means on the other side. There is no separate
                    # cache-WRITE number, so that one stays null instead of being reported as zero.
                    CacheReadTokens   = (Get-JsonNumber $u 'cached_input_tokens')
                    CacheCreateTokens = $null
                }
            }
            'turn.failed' {
                $msg = if ($Event.error) { "$($Event.error.message)" } else { 'the run failed' }
                $out += [pscustomobject]@{
                    Kind = 'result'; Text = $msg; DurationMs = $null; NumTurns = $null; IsError = $true
                    InputTokens = $null; OutputTokens = $null; CacheReadTokens = $null; CacheCreateTokens = $null
                }
            }
            'error' {
                $out += [pscustomobject]@{
                    Kind = 'result'; Text = "$($Event.message)"; DurationMs = $null; NumTurns = $null; IsError = $true
                    InputTokens = $null; OutputTokens = $null; CacheReadTokens = $null; CacheCreateTokens = $null
                }
            }
        }
        return ,$out
    }

    switch ("$($Event.type)") {
        'system' {
            # The session's opening event, and the only place the CLI says whether each MCP server
            # actually came up. Writing the config proves nothing: a stdio server that fails its
            # handshake is dropped silently, the session starts with a smaller tool set, and the
            # first thing that notices is the model, minutes later, when it reaches for a tool that
            # is not there (seen 2026-08-25 on ticket 43 - a QA run that blocked over a Playwright
            # server the worker had cheerfully logged as enabled).
            #
            # It arrives before the model's first token, which is what makes acting on it cheap:
            # relaunching here costs a process start and no thinking at all.
            #
            # Claude only. Codex reports no equivalent, so a Codex run emits no session_start and
            # goes unchecked rather than being checked against a status nobody sent.
            #
            # 'connected' is the server's word, not the tool set's. The same event carries the
            # session's actual tool names, and the two can disagree: on 2026-09-08 a CLI update
            # moved HTTP servers onto a newer MCP protocol our server SDK answered wrongly, so
            # `tickets` reported connected while contributing none of its 21 tools, and six runs
            # in a row passed the status gate with no way to record anything. So the names go in
            # the event too, and the caller checks what a run HAS rather than what it was told.
            # A tool arrives as mcp__<server>__<tool>; the server is what sits between the pairs.
            if ("$($Event.subtype)" -eq 'init') {
                $status = @{}
                foreach ($s in $Event.mcp_servers) { $status["$($s.name)"] = "$($s.status)" }
                $withTools = @{}
                foreach ($t in $Event.tools) {
                    $n = "$t"
                    if ($n -like 'mcp__*') {
                        $i = $n.IndexOf('__', 5)
                        if ($i -gt 5) { $withTools[$n.Substring(5, $i - 5)] = $true }
                    }
                }
                $out += [pscustomobject]@{ Kind = 'session_start'; ServerStatus = $status; ServersWithTools = $withTools }
            }
        }
        'assistant' {
            # How much this one model call was sent: everything the session has accumulated, which
            # is re-read on every turn and is what a run's cache-read total is the sum of. A
            # sub-agent's calls carry parent_tool_use_id and are a different, smaller conversation,
            # so they are left out - the number is the main thread's, or it means nothing.
            if ($Event.message -and $Event.message.usage -and -not $Event.parent_tool_use_id) {
                $ctx = [long]0
                foreach ($k in 'input_tokens', 'cache_read_input_tokens', 'cache_creation_input_tokens') {
                    $n = Get-JsonNumber $Event.message.usage $k
                    if ($null -ne $n) { $ctx += [long]$n }
                }
                if ($ctx -gt 0) { $out += [pscustomobject]@{ Kind = 'context'; Tokens = $ctx } }
            }
            if ($Event.message -and $Event.message.content) {
                foreach ($block in $Event.message.content) {
                    if ($block.type -eq 'tool_use') {
                        $out += [pscustomobject]@{
                            Kind = 'tool_use'; ToolName = $block.name; Detail = (Format-ToolCall -Block $block)
                            Id = "$($block.id)"
                        }
                    }
                    elseif ($block.type -eq 'text' -and $block.text) {
                        $out += [pscustomobject]@{ Kind = 'text'; Text = $block.text }
                    }
                    elseif ($block.type -eq 'thinking' -and $block.thinking) {
                        $out += [pscustomobject]@{ Kind = 'thinking'; Text = $block.thinking }
                    }
                }
            }
        }
        'user' {
            # What each tool call handed back, by size. A result enters the conversation once and
            # is then re-read on every later turn, so the big ones are where a run's context goes.
            # Main thread only, as above: a sub-agent's results stay in the sub-agent, and what the
            # main thread pays for is the one Task result that comes back.
            if ($Event.message -and -not $Event.parent_tool_use_id) {
                foreach ($block in @($Event.message.content)) {
                    if ($block -is [string] -or $block.type -ne 'tool_result') { continue }
                    $chars = 0
                    $images = 0
                    if ($block.content -is [string]) { $chars = $block.content.Length }
                    else {
                        foreach ($part in @($block.content)) {
                            if ($part.type -eq 'text') { $chars += "$($part.text)".Length }
                            elseif ($part.type -eq 'image') { $images++ }
                        }
                    }
                    $out += [pscustomobject]@{
                        Kind = 'tool_result'; ToolUseId = "$($block.tool_use_id)"; Chars = $chars; Images = $images
                    }
                }
            }
        }
        'result' {
            $u = $Event.usage
            $ms = Get-JsonNumber $Event 'duration_ms'
            $out += [pscustomobject]@{
                Kind              = 'result'
                Text              = $Event.result
                DurationMs        = $ms
                NumTurns          = (Get-JsonNumber $Event 'num_turns')
                IsError           = [bool](Get-JsonNumber $Event 'is_error')
                InputTokens       = (Get-JsonNumber $u 'input_tokens')
                OutputTokens      = (Get-JsonNumber $u 'output_tokens')
                CacheReadTokens   = (Get-JsonNumber $u 'cache_read_input_tokens')
                CacheCreateTokens = (Get-JsonNumber $u 'cache_creation_input_tokens')
            }
        }
    }
    return ,$out
}

# One Codex thread item -> canonical events.
#
# Which lifecycle phase each item is taken at is deliberate. A tool call is surfaced when it STARTS,
# because that is when Claude emits tool_use and the feed is meant to narrate work as it happens. A
# message or a reasoning block is only whole once COMPLETED, so those are taken there.
function ConvertFrom-CodexItem {
    param($Event, $State)

    $item = $Event.item
    if (-not $item) { return @() }
    $started = ("$($Event.type)" -eq 'item.started')
    $out = @()

    switch ("$($item.type)") {
        'agent_message' {
            if (-not $started -and $item.text) {
                # Remembered for the closing result, which is where the summary belongs.
                if ($State) { $State.LastMessage = "$($item.text)" }
                $out += [pscustomobject]@{ Kind = 'text'; Text = "$($item.text)" }
            }
        }
        'reasoning' {
            if (-not $started -and $item.text) {
                $out += [pscustomobject]@{ Kind = 'thinking'; Text = "$($item.text)" }
            }
        }
        'command_execution' {
            if ($started) {
                $out += (New-CanonicalToolUse -ToolName 'Bash' -ToolInput ([pscustomobject]@{ command = "$($item.command)" }))
            }
        }
        'file_change' {
            if (-not $started -and $item.changes) {
                # Claude reports one edit per tool call; Codex batches them. The feed wants the file
                # names, so they are joined rather than reduced to the first one.
                $names = @()
                foreach ($c in $item.changes) {
                    $leaf = ("$($c.path)" -split '[\\/]' | Select-Object -Last 1)
                    if ($leaf) { $names += $leaf }
                }
                # file_path, not path: that is the field Format-ToolCall reads for an Edit, and the
                # leaf names are already extracted so its own trailing-segment split is a no-op.
                if ($names.Count -gt 0) {
                    $out += (New-CanonicalToolUse -ToolName 'Edit' -ToolInput ([pscustomobject]@{ file_path = ($names -join ', ') }))
                }
            }
        }
        'mcp_tool_call' {
            if ($started -and $item.server -and $item.tool) {
                # Reassembled into the name every consumer already understands.
                $name = "mcp__$($item.server)__$($item.tool)"
                $out += (New-CanonicalToolUse -ToolName $name -ToolInput (ConvertTo-ArgObject $item.arguments))
            }
        }
        'web_search' {
            if (-not $started -and $item.query) {
                $out += (New-CanonicalToolUse -ToolName 'WebSearch' -ToolInput ([pscustomobject]@{ query = "$($item.query)" }))
            }
        }
        'error' {
            if (-not $started -and $item.message) {
                $out += [pscustomobject]@{ Kind = 'text'; Text = "$($item.message)" }
            }
        }
    }
    return ,$out
}

# One Gemini stream-json event -> canonical events.
#
# NOT VERIFIED AGAINST A LIVE CLI - this half of the provider was written from Google's published
# event list so that switching is a Settings change rather than a project. That is why every field is
# read through a fallback: an event shape this does not recognise costs the feed a line, and a
# recognised one with a renamed field costs it a caption. Neither costs the run its work, which is
# the property to preserve if any of this turns out to be wrong.
#
# No session_start is emitted. That event exists so the worker can check every required MCP server
# actually came up before letting the model start, and this CLI reports no equivalent - so a Gemini
# run goes unchecked rather than being checked against a status nobody sent, exactly as Codex does.
function ConvertFrom-GeminiEvent {
    param($Event, $State)

    $out = @()

    switch ("$($Event.type)") {
        'tool_use' {
            $name = "$($Event.tool_name)"
            if (-not $name) { $name = "$($Event.name)" }
            if ($name) {
                $toolArgs = $Event.parameters
                if ($null -eq $toolArgs) { $toolArgs = $Event.args }
                if ($null -eq $toolArgs) { $toolArgs = $Event.input }
                $out += (New-CanonicalToolUse -ToolName (ConvertTo-GeminiToolName $name) `
                                              -ToolInput (ConvertTo-ArgObject $toolArgs))
            }
        }
        # The result of a call, not the call - the feed already has the line, and a second one saying
        # the same thing finished would double every row.
        'tool_result' { }
        'init' { }
        'message' {
            $text = Get-GeminiMessageText -Event $Event
            if ($text) {
                if ($State) { $State.LastMessage = $text }
                $out += [pscustomobject]@{ Kind = 'text'; Text = $text }
            }
        }
        'assistant' {
            $text = Get-GeminiMessageText -Event $Event
            if ($text) {
                if ($State) { $State.LastMessage = $text }
                $out += [pscustomobject]@{ Kind = 'text'; Text = $text }
            }
        }
        'thought' {
            $text = Get-GeminiMessageText -Event $Event
            if ($text) { $out += [pscustomobject]@{ Kind = 'thinking'; Text = $text } }
        }
        'result' {
            $u = Get-GeminiUsage -Stats $Event.stats
            $failure = Get-GeminiError -Event $Event
            $text = "$($Event.response)"
            if (-not $text) { $text = "$($Event.content)" }
            if (-not $text -and $State) { $text = $State.LastMessage }
            $out += [pscustomobject]@{
                Kind              = 'result'
                Text              = $(if ($failure) { $failure } else { $text })
                # No wall-clock or turn count on this stream. The worker's own stopwatch already
                # covers duration; a turn count we do not have is left null rather than invented.
                DurationMs        = $null
                NumTurns          = $null
                IsError           = [bool]$failure
                InputTokens       = $u.Input
                OutputTokens      = $u.Output
                CacheReadTokens   = $u.CacheRead
                # No cache-WRITE figure is reported, so that one stays null instead of being
                # reported as a zero somebody could mistake for a measurement.
                CacheCreateTokens = $null
            }
        }
        'error' {
            $failure = Get-GeminiError -Event $Event
            if (-not $failure) { $failure = 'the run failed' }
            $out += [pscustomobject]@{
                Kind = 'result'; Text = $failure; DurationMs = $null; NumTurns = $null; IsError = $true
                InputTokens = $null; OutputTokens = $null; CacheReadTokens = $null; CacheCreateTokens = $null
            }
        }
    }
    return ,$out
}

# This CLI's tool names in Claude's vocabulary - the one the mutation counter, the feed and
# Format-ToolCall all already speak.
#
# An MCP tool arrives fully qualified as `server__tool` and is reassembled into `mcp__server__tool`,
# which is the shape the mutation counter keys off: without this, a run's ticket comments and phase
# moves would be counted as zero changes and the ticket would look like it had stalled. No built-in
# name contains a double underscore, which is what makes that test safe. Anything unrecognised keeps
# its own name - a tool the feed cannot label should read as itself rather than as something it is
# not.
function ConvertTo-GeminiToolName {
    param([string]$Name)

    if ($Name -like 'mcp__*') { return $Name }
    switch ($Name) {
        'read_file'           { return 'Read' }
        'ReadFileTool'        { return 'Read' }
        'read_many_files'     { return 'Read' }
        'ReadManyFilesTool'   { return 'Read' }
        'write_file'          { return 'Write' }
        'WriteFileTool'       { return 'Write' }
        'replace'             { return 'Edit' }
        'EditTool'            { return 'Edit' }
        'run_shell_command'   { return 'Bash' }
        'ShellTool'           { return 'Bash' }
        'search_file_content' { return 'Grep' }
        'GrepTool'            { return 'Grep' }
        'ripGrep'             { return 'Grep' }
        'glob'                { return 'Glob' }
        'GlobTool'            { return 'Glob' }
        'findFiles'           { return 'Glob' }
        'google_web_search'   { return 'WebSearch' }
        'WebSearchTool'       { return 'WebSearch' }
        'web_fetch'           { return 'WebFetch' }
        'WebFetchTool'        { return 'WebFetch' }
    }
    $i = $Name.IndexOf('__')
    if ($i -gt 0) { return 'mcp__' + $Name.Substring(0, $i) + '__' + $Name.Substring($i + 2) }
    return $Name
}

# The text of one message event, whether the CLI put it in a string or in a parts array.
function Get-GeminiMessageText {
    param($Event)

    $role = "$($Event.role)"
    if ($role -and $role -ne 'assistant') { return $null }

    $c = $Event.content
    if ($null -eq $c) { $c = $Event.text }
    if ($null -eq $c) { return $null }
    if ($c -is [string]) { return $c }

    $parts = @()
    foreach ($p in $c) {
        if ($p -is [string]) { $parts += $p }
        elseif ($p -and $p.PSObject.Properties['text']) { $parts += "$($p.text)" }
    }
    return ($parts -join '')
}

# The message off an error, from either the nested or the flat spelling. $null when there is none,
# which is what the caller reads as "this run succeeded".
function Get-GeminiError {
    param($Event)

    if ($Event.error) {
        if ($Event.error -is [string]) { return "$($Event.error)" }
        $m = "$($Event.error.message)"
        if ($m) { return $m }
        return 'the run failed'
    }
    if ("$($Event.type)" -eq 'error') {
        $m = "$($Event.message)"
        if ($m) { return $m }
        return 'the run failed'
    }
    $status = "$($Event.status)"
    if ($status -and $status -ne 'success') { return "the run ended with status '$status'" }
    return $null
}

# The accounting, from either shape of `stats`.
#
# The documented one is nested per model - stats.models['gemini-...'].tokens - and there may be more
# than one of them, because the `auto` alias routes a single session between a fast model and a
# capable one. The totals are therefore SUMMED across models rather than read off the first.
function Get-GeminiUsage {
    param($Stats)

    if ($Stats -and $Stats.PSObject.Properties['models'] -and $Stats.models) {
        $in = 0; $out = 0; $cached = 0
        foreach ($p in $Stats.models.PSObject.Properties) {
            $t = $p.Value
            if ($t -and $t.PSObject.Properties['tokens']) { $t = $t.tokens }
            $in     += (Get-NumberOrZero $t 'prompt')
            # Thinking tokens are billed as output and reported apart from the answer's own, so
            # leaving them out would under-report every run on a reasoning model.
            $out    += (Get-NumberOrZero $t 'candidates') + (Get-NumberOrZero $t 'thoughts')
            $cached += (Get-NumberOrZero $t 'cached')
        }
        return [pscustomobject]@{ Input = $in; Output = $out; CacheRead = $cached }
    }

    return [pscustomobject]@{
        Input     = (Get-JsonNumber $Stats 'input_tokens')
        Output    = (Get-JsonNumber $Stats 'output_tokens')
        CacheRead = (Get-JsonNumber $Stats 'cached_tokens')
    }
}

function Get-NumberOrZero {
    param($Obj, [string]$Name)
    $v = Get-JsonNumber $Obj $Name
    if ($null -eq $v) { return 0 }
    return $v
}
