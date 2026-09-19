<#
.SYNOPSIS
    Custom Unity CLI - drive the Unity Editor from a terminal, CI or an AI agent.

.DESCRIPTION
    `cucli` is the single entry point of the custom-unity-cli package. It needs no SDK, no server
    and no per-project configuration:

      * Commands that can be answered without Unity (project info, logs, queue inspection,
        command catalog, doctor) are handled by this script directly.
      * Everything else is written as a JSON job into <project>/Library/CustomUnityCli/jobs and
        executed by the Editor worker, which publishes a result file the script waits for.

    Because the transport is a directory of files rather than a socket, the tool keeps working
    across domain reloads, needs no port, and cannot deadlock the Editor.

.PARAMETER Group
    Command group, e.g. `editor`, `scene`, `object`, `asset`, `build`.

.PARAMETER Action
    Sub-action of the group, e.g. `cucli object create`.

.EXAMPLE
    ./cucli.ps1 commands list
    ./cucli.ps1 status
    ./cucli.ps1 editor state
    ./cucli.ps1 object create --name Ball --primitive sphere
    ./cucli.ps1 scene get-hierarchy --page-size 50 --format json
    ./cucli.ps1 refresh compile --wait
    ./cucli.ps1 job list

.NOTES
    Exit codes: 0 ok, 1 generic, 2 usage, 4 missing project/confirmation, 6 failed,
                7 Editor unavailable (retryable), 8 invalid input, 130 cancelled.
#>

# Everything the caller typed, verbatim.
#
# This script deliberately declares NO param() block. PowerShell would treat `--format json` as a
# bound parameter and silently reshape it, and common parameters such as -Verbose would be eaten.
# Collecting the raw token list and parsing it ourselves is the only way
# `cucli <group> <action> --any-future-option value` keeps working without touching this file.
$Arguments = @($args)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Console output must be UTF-8 so Chinese messages survive redirection on Windows PowerShell 5.1.
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch {
    # Non-console hosts (some CI runners) have no encodable console; ignore.
}

$Script:SchemaVersion = 'cucli.result/v1'
$Script:ScriptRoot = $PSScriptRoot

# Project-local extension area. Must match CucliExtensionPaths.CandidateFolders and
# CucliExtensionPaths.PreferredFolder in the Editor assembly, in this exact order.
$Script:ExtensionCandidates = @(
    'Assets/CustomUnityCli.Extensions'
    'Assets/CustomUnityCliExtensions'
    'Assets/Plugins/CustomUnityCli.Extensions'
    'Assets/Editor/CustomUnityCli.Extensions'
    'Packages/CustomUnityCli.Extensions'
)
$Script:ExtensionPreferredFolder = 'Assets/CustomUnityCli.Extensions'
# Must match CucliExtensionPaths.LocalRegistryFileName.
$Script:ExtensionRegistryFileName = 'registry.local.json'

#region ---------------------------------------------------------------- exit codes

$Script:ExitOk = 0
$Script:ExitGeneric = 1
$Script:ExitUsage = 2
$Script:ExitProject = 4
$Script:ExitFailed = 6
$Script:ExitUnavailable = 7
$Script:ExitInvalid = 8
$Script:ExitCancelled = 130

$Script:ErrorExitMap = @{
    'USAGE_ERROR'                 = $Script:ExitUsage
    'PROJECT_NOT_FOUND'           = $Script:ExitProject
    'CONFIRMATION_REQUIRED'       = $Script:ExitProject
    'OPTIONAL_PACKAGE_MISSING'    = $Script:ExitProject
    'MISSING_PARAMETER'           = $Script:ExitInvalid
    'INVALID_PARAMETER'           = $Script:ExitInvalid
    'UNSUPPORTED_ACTION'          = $Script:ExitInvalid
    'EDITOR_COMMAND_NOT_FOUND'    = $Script:ExitInvalid
    'MEMBER_NOT_FOUND'            = $Script:ExitInvalid
    'TYPE_NOT_FOUND'              = $Script:ExitInvalid
    'ASSET_NOT_FOUND'             = $Script:ExitInvalid
    'TARGET_NOT_FOUND'            = $Script:ExitInvalid
    'TARGET_AMBIGUOUS'            = $Script:ExitInvalid
    'ASSET_EXISTS'                = $Script:ExitProject
    'WRONG_MODE'                  = $Script:ExitFailed
    'CODE_COMPILE_ERROR'          = $Script:ExitFailed
    'SCRIPT_COMPILATION_FAILED'   = $Script:ExitFailed
    'COMPILATION_IN_PROGRESS'     = $Script:ExitUnavailable
    'WORKER_UNAVAILABLE'          = $Script:ExitUnavailable
    'WORKER_BUSY'                 = $Script:ExitUnavailable
    'WORKER_RELOADED'             = $Script:ExitUnavailable
    'TIMEOUT'                     = $Script:ExitFailed
    'CANCELLED'                   = $Script:ExitCancelled
}

function Get-ExitCodeForError {
    param([string]$Code)
    if ([string]::IsNullOrWhiteSpace($Code)) { return $Script:ExitFailed }
    if ($Script:ErrorExitMap.ContainsKey($Code)) { return $Script:ErrorExitMap[$Code] }
    return $Script:ExitFailed
}

$Script:RetryableCodes = @(
    'WORKER_UNAVAILABLE', 'WORKER_BUSY', 'COMPILATION_IN_PROGRESS', 'WORKER_RELOADED'
)

# The single source of truth for the process exit code. Write-CucliResult sets it; the bottom of
# this script exits with it. Keeping it out of the pipeline is what lets callers capture the result
# payload without also swallowing the status.
$Script:CucliExitCode = $Script:ExitOk

#endregion

#region ---------------------------------------------------------------- json helpers

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [System.IO.File]::WriteAllText($temporary, $Content, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return $raw | ConvertFrom-Json
    } catch {
        return $null
    }
}

function ConvertTo-JsonSafe {
    param($Value, [int]$Depth = 32, [switch]$Compress)
    if ($null -eq $Value) { return 'null' }
    # -Compress exists for --format ndjson, where the contract is one JSON object per LINE. Emitting
    # pretty-printed multi-line JSON there breaks any line-oriented consumer (jq -c, streaming readers).
    return ($Value | ConvertTo-Json -Depth $Depth -Compress:$Compress.IsPresent)
}

#endregion

#region ---------------------------------------------------------------- project root

function Test-ProjectRoot {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $assets = Join-Path $Path 'Assets'
    $version = Join-Path $Path 'ProjectSettings/ProjectVersion.txt'
    return (Test-Path -LiteralPath $assets) -and (Test-Path -LiteralPath $version)
}

function Find-ProjectRoot {
    param([string]$Hint, [string]$StartPath)

    if (-not [string]::IsNullOrWhiteSpace($Hint)) {
        $full = [System.IO.Path]::GetFullPath($Hint)
        if (Test-ProjectRoot $full) { return $full }

        # Also accept pointing at the project's Assets folder.
        $parent = Split-Path -Parent $full
        if (Test-ProjectRoot $parent) { return $parent }

        # An explicit --project that is not a Unity project is a hard failure, never a fallback.
        # Silently resolving to a different project would mean a script targeting project A edits
        # project B, which is the worst possible failure mode for this tool.
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:CUCLI_PROJECT)) {
        $envPath = [System.IO.Path]::GetFullPath($env:CUCLI_PROJECT)
        if (Test-ProjectRoot $envPath) { return $envPath }
        return $null
    }

    foreach ($start in @($StartPath, $Script:ScriptRoot, (Get-Location).Path)) {
        if ([string]::IsNullOrWhiteSpace($start)) { continue }
        $current = $start
        for ($depth = 0; $depth -lt 12; $depth++) {
            if ([string]::IsNullOrWhiteSpace($current)) { break }
            if (Test-ProjectRoot $current) { return $current }
            $next = Split-Path -Parent $current
            if ($next -eq $current) { break }
            $current = $next
        }
    }

    return $null
}

#endregion

#region ---------------------------------------------------------------- package layout

function Get-PackageRoot {
    # Tools/cucli.ps1 lives inside the package; the package root is its grandparent.
    return (Split-Path -Parent $Script:ScriptRoot)
}

function Get-RuntimeRoot {
    param([string]$ProjectRoot)
    return (Join-Path $ProjectRoot 'Library/CustomUnityCli')
}

function Get-StaticRegistry {
    $path = Join-Path (Get-PackageRoot) 'commands/registry.json'
    return Read-JsonFile $path
}

function Get-ExtensionRoot {
    <#
        The project-local extension area.

        It lives OUTSIDE the package on purpose: the package is a published artifact replaced
        wholesale on upgrade, while project-specific commands must survive upgrades and must never
        be published.

        This MUST stay identical to CucliExtensionPaths on the Editor side (candidate list, order and
        preferred folder). If the two disagree, the CLI loses sight of project commands while Unity
        is closed, and 'cucli extend' would scaffold into a second, dead folder.
    #>
    param([string]$ProjectRoot)

    $configured = $env:CUCLI_EXTENSIONS
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        $trimmed = $configured.Trim()
        if ([System.IO.Path]::IsPathRooted($trimmed)) { return [System.IO.Path]::GetFullPath($trimmed) }
        return [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot $trimmed))
    }

    # An existing candidate wins, so a project that already put its extensions somewhere is never
    # silently given a second folder.
    foreach ($candidate in $Script:ExtensionCandidates) {
        $absolute = Join-Path $ProjectRoot $candidate
        if (Test-Path -LiteralPath $absolute) { return $absolute }
    }

    return (Join-Path $ProjectRoot $Script:ExtensionPreferredFolder)
}

function Get-ExtensionRelativeRoot {
    param([string]$ProjectRoot)
    $root = Get-ExtensionRoot $ProjectRoot
    if ($root.StartsWith($ProjectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $root.Substring($ProjectRoot.Length).TrimStart('\', '/')
        if ($relative.Length -gt 0) { return ($relative -replace '\\', '/') }
    }

    return ($root -replace '\\', '/')
}

function Get-LocalRegistry {
    <#
        Project-local command declarations, written by 'cucli commands export' beside the extension
        code. Merged over the package registry so project commands stay listable and validatable
        while Unity is closed.
    #>
    param([string]$ProjectRoot)
    $path = Join-Path (Get-ExtensionRoot $ProjectRoot) $Script:ExtensionRegistryFileName
    return Read-JsonFile $path
}

function Get-ExtensionSources {
    param([string]$ProjectRoot)
    $root = Get-ExtensionRoot $ProjectRoot
    if (-not (Test-Path -LiteralPath $root)) { return @() }

    return @(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue |
        ForEach-Object { ($_.FullName.Substring($ProjectRoot.Length + 1) -replace '\\', '/') } |
        Sort-Object)
}

function Get-RuntimeCatalog {
    param([string]$ProjectRoot)
    $path = Join-Path (Get-RuntimeRoot $ProjectRoot) 'worker/commands.json'
    return Read-JsonFile $path
}

function Get-Heartbeat {
    param([string]$ProjectRoot)
    $path = Join-Path (Get-RuntimeRoot $ProjectRoot) 'worker/heartbeat.json'
    return Read-JsonFile $path
}

function Get-HeartbeatAgeSeconds {
    param($Heartbeat)
    if ($null -eq $Heartbeat -or [string]::IsNullOrWhiteSpace($Heartbeat.timestampUtc)) { return $null }
    try {
        $stamp = [datetime]::Parse(
            $Heartbeat.timestampUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
        return ([datetime]::UtcNow - $stamp).TotalSeconds
    } catch {
        return $null
    }
}

function Test-WorkerOnline {
    param([string]$ProjectRoot, [int]$MaxAgeSeconds = 15)
    $heartbeat = Get-Heartbeat $ProjectRoot
    $age = Get-HeartbeatAgeSeconds $heartbeat
    if ($null -eq $age) { return $false }
    return ($age -le $MaxAgeSeconds)
}

#endregion

#region ---------------------------------------------------------------- argument parsing

function ConvertTo-CamelCase {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $Name }
    if ($Name -notmatch '[-_]') { return $Name }
    $parts = $Name -split '[-_]' | Where-Object { $_ -ne '' }
    $builder = New-Object System.Text.StringBuilder
    for ($index = 0; $index -lt $parts.Count; $index++) {
        $part = $parts[$index]
        if ($index -eq 0) {
            [void]$builder.Append($part)
        } else {
            [void]$builder.Append($part.Substring(0, 1).ToUpperInvariant())
            if ($part.Length -gt 1) { [void]$builder.Append($part.Substring(1)) }
        }
    }
    return $builder.ToString()
}

function Add-PayloadValue {
    <#
        Stores a payload key, promoting it to an array when the caller repeats the option.

        Repeating an option is the natural way to express a list on a command line -
        `--action trace:"..." --action clear:"..."` - and several generated-command workflows depend
        on it. A single occurrence stays a scalar so the common case serialises as a plain value.
    #>
    param([System.Collections.IDictionary]$Payload, [string]$Key, $Value)

    if (-not $Payload.Contains($Key)) {
        $Payload[$Key] = $Value
        return
    }

    $existing = $Payload[$Key]
    if ($existing -is [System.Collections.IList] -and -not ($existing -is [string])) {
        [void]$existing.Add($Value)
        return
    }

    $list = New-Object System.Collections.Generic.List[object]
    $list.Add($existing)
    $list.Add($Value)
    $Payload[$Key] = $list
}

function ConvertFrom-ArgumentValue {
    param([string]$Text)
    if ($null -eq $Text) { return $null }
    $trimmed = $Text.Trim()
    if ($trimmed.Length -eq 0) { return '' }
    # Leave @file references untouched: the Editor side resolves them.
    if ($trimmed.StartsWith('@')) { return $trimmed }
    if ($trimmed -eq 'true') { return $true }
    if ($trimmed -eq 'false') { return $false }
    if ($trimmed -eq 'null') { return $null }
    if ($trimmed -match '^-?\d+$') {
        $parsed = 0L
        if ([long]::TryParse($trimmed, [ref]$parsed)) { return $parsed }
    }
    if ($trimmed -match '^-?\d*\.\d+([eE][-+]?\d+)?$') {
        $double = 0.0
        if ([double]::TryParse($trimmed, [System.Globalization.NumberStyles]::Float,
                [System.Globalization.CultureInfo]::InvariantCulture, [ref]$double)) { return $double }
    }
    if (($trimmed.StartsWith('[') -and $trimmed.EndsWith(']')) -or
        ($trimmed.StartsWith('{') -and $trimmed.EndsWith('}'))) {
        try { return ($trimmed | ConvertFrom-Json) } catch { return $trimmed }
    }
    return $trimmed
}

function Test-LooksLikeOption {
    <#
        Decides whether the token following an option is that option's VALUE or a new option.

        A bare "does it start with a dash?" test is wrong, and wrong in a way that fails silently:
        Unity instance ids are routinely negative (an object's id is often something like -10844),
        so `--instance-id -10844` would consume no value at all and the key would be set to $true.
        The command then reports "an integer 'instanceId' is required", or worse - for an optional
        parameter - quietly does nothing and still succeeds.

        Negative numbers, therefore, are values. Everything else starting with a dash is an option.
    #>
    param([string]$Token)

    if ([string]::IsNullOrEmpty($Token)) { return $false }
    if (-not $Token.StartsWith('-')) { return $false }
    if ($Token.Length -eq 1) { return $false }

    # -1, -10844, -0.5, -.5, -1,2,3 and -1e3 are all values.
    if ($Token -match '^-[\d.]') { return $false }

    return $true
}

function Split-CucliArguments {
    <#
        Splits the raw argument list into positional tokens and a payload hashtable.

        Global options are consumed here; everything else becomes a payload key so a new Editor
        command never requires changing this parser.
    #>
    param([string[]]$Raw)

    $result = [ordered]@{
        Positional = New-Object System.Collections.Generic.List[string]
        Payload    = [ordered]@{}
        Format     = $null
        Project    = $null
        Timeout    = 60
        Action     = $null
        Detach     = $false
        Quiet      = $false
        Help       = $false
    }

    if ($null -eq $Raw) { return $result }

    $index = 0
    while ($index -lt $Raw.Count) {
        $token = $Raw[$index]
        $index++

        if ([string]::IsNullOrWhiteSpace($token)) { continue }

        if ($token -eq '--') {
            while ($index -lt $Raw.Count) { $result.Positional.Add($Raw[$index]); $index++ }
            break
        }

        if ($token -eq '-h' -or $token -eq '--help' -or $token -eq 'help') {
            $result.Help = $true
            continue
        }

        if ($token -eq '--json') { $result.Format = 'json'; continue }
        if ($token -eq '--ndjson') { $result.Format = 'ndjson'; continue }
        if ($token -eq '--quiet' -or $token -eq '-q') { $result.Quiet = $true; continue }
        if ($token -eq '--detach' -or $token -eq '--no-wait') { $result.Detach = $true; continue }

        if ($token -eq '--format' -or $token -eq '--project' -or $token -eq '--timeout') {
            if ($index -ge $Raw.Count) {
                throw "Option $token requires a value."
            }
            $value = $Raw[$index]
            $index++
            switch ($token) {
                '--format'  { $result.Format = $value.ToLowerInvariant() }
                '--project' { $result.Project = $value }
                '--timeout' { $result.Timeout = [int]$value }
            }
            continue
        }

        if ($token.StartsWith('--format=')) { $result.Format = $token.Substring(9).ToLowerInvariant(); continue }
        if ($token.StartsWith('--project=')) { $result.Project = $token.Substring(10); continue }
        if ($token.StartsWith('--timeout=')) { $result.Timeout = [int]$token.Substring(10); continue }

        if ($token.StartsWith('--')) {
            # Split on '=' BEFORE deriving the key. Deriving it from the whole token turns
            # `--instance-id=-10844` into the key `instanceId=-10844`, so the real key is never set
            # and the command sees a missing parameter - or silently does nothing.
            $equals = $token.IndexOf('=')
            $rawName = if ($equals -gt 0) { $token.Substring(2, $equals - 2) } else { $token.Substring(2) }
            $name = ConvertTo-CamelCase $rawName
            if ($name.Length -eq 0) { continue }

            # --flag, --flag=value or --flag value
            if ($equals -gt 0) {
                Add-PayloadValue -Payload $result.Payload -Key $name -Value (ConvertFrom-ArgumentValue $token.Substring($equals + 1))
                continue
            }

            $next = if ($index -lt $Raw.Count) { $Raw[$index] } else { $null }
            if ($null -ne $next -and -not (Test-LooksLikeOption $next)) {
                Add-PayloadValue -Payload $result.Payload -Key $name -Value (ConvertFrom-ArgumentValue $next)
                $index++
            } else {
                Add-PayloadValue -Payload $result.Payload -Key $name -Value $true
            }
            continue
        }

        if ($token.StartsWith('-') -and $token.Length -gt 1) {
            $name = ConvertTo-CamelCase ($token.Substring(1))
            $next = if ($index -lt $Raw.Count) { $Raw[$index] } else { $null }
            if ($null -ne $next -and -not (Test-LooksLikeOption $next)) {
                Add-PayloadValue -Payload $result.Payload -Key $name -Value (ConvertFrom-ArgumentValue $next)
                $index++
            } else {
                Add-PayloadValue -Payload $result.Payload -Key $name -Value $true
            }
            continue
        }

        $result.Positional.Add($token)
    }

    # `action` is reserved: it is the key that selects which sub-action of a group runs, and the CLI
    # always fills it in. A `--action` option is therefore consumed here as an alternative spelling of
    # the positional action instead of being forwarded as a payload key - otherwise a command group
    # that happened to declare a parameter named `action` would silently receive the CLI's value and
    # the user's would vanish. Meta-commands that need a sub-action list use `actionNames` /
    # `actionName` for exactly this reason.
    if ($result.Payload.Contains('action')) {
        if ([string]::IsNullOrWhiteSpace($result.Action)) {
            $result.Action = ([string]$result.Payload['action']).Trim()
        }

        $result.Payload.Remove('action')
    }

    return $result
}

#endregion

#region ---------------------------------------------------------------- output

function Write-CucliResult {
    param(
        [bool]$Success,
        [string]$Command,
        [string]$Action,
        $Data = $null,
        [string]$ErrorCode = $null,
        [string]$ErrorMessage = $null,
        [string[]]$Warnings = @(),
        [string[]]$Artifacts = @(),
        [long]$DurationMs = 0,
        [string]$RequestId = $null,
        [string]$JobId = $null,
        [string]$Format = 'human'
    )

    $exitCode = if ($Success) { $Script:ExitOk } else { Get-ExitCodeForError $ErrorCode }

    $errors = @()
    if (-not $Success) {
        $errors = @([ordered]@{
            code      = if ($ErrorCode) { $ErrorCode } else { 'EDITOR_COMMAND_FAILED' }
            message   = $ErrorMessage
            retryable = ($ErrorCode -and ($Script:RetryableCodes -contains $ErrorCode))
        })
    }

    # A $null element in a [string[]] and a single empty string both survive @() and then
    # serialise as [""], which looks like a bug in every result. Strip them.
    $cleanWarnings = @($Warnings | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $cleanArtifacts = @($Artifacts | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    # Offline commands have no caller-supplied request id; emit null rather than an empty string.
    $cleanRequestId = if ([string]::IsNullOrWhiteSpace($RequestId)) { $null } else { $RequestId }
    $cleanJobId = if ([string]::IsNullOrWhiteSpace($JobId)) { $null } else { $JobId }

    $envelope = [ordered]@{
        schemaVersion = $Script:SchemaVersion
        success       = $Success
        command       = $Command
        action        = $Action
        requestId     = $cleanRequestId
        jobId         = $cleanJobId
        data          = $Data
        errors        = $errors
        warnings      = $cleanWarnings
        artifacts     = $cleanArtifacts
        durationMs    = $DurationMs
        exitCode      = $exitCode
    }

    # Results go to the *pipeline*, never straight to [Console]::Out.
    #
    # This is what makes the CLI composable: both
    #     $r = ./cucli.ps1 status --format json
    #     ./cucli.ps1 status --format json | ConvertFrom-Json
    # work. Writing straight to the console produces an empty capture instead, which is the single
    # most common way a CLI like this ends up impossible to script or to test.
    $Script:CucliExitCode = $exitCode

    if ($Format -eq 'ndjson') {
        $frame = [ordered]@{
            type         = 'result'
            timestampUtc = [datetime]::UtcNow.ToString('O')
            data         = $envelope
        }
        # One object per line, per the NDJSON contract.
        Write-Output (ConvertTo-JsonSafe $frame -Compress)
    } elseif ($Format -eq 'json') {
        Write-Output (ConvertTo-JsonSafe $envelope)
    } else {
        # Human format: a compact, greppable summary. JSON stays available via --format json.
        if ($Success) {
            if ($null -ne $Data) {
                Write-Output (('RESULT {0} {1}' -f $Command, $Action).Trim())
                Write-Output (ConvertTo-JsonSafe $Data)
            } else {
                Write-Output (('OK {0} {1}' -f $Command, $Action).Trim())
            }
        } else {
            # Failures and warnings deliberately stay on stderr, so that
            # `cucli ... > out.json` captures only the result payload.
            [Console]::Error.WriteLine(('[FAIL] {0}: {1}' -f $errors[0].code, $errors[0].message))
            if ($null -ne $Data) {
                [Console]::Error.WriteLine((ConvertTo-JsonSafe $Data))
            }
        }

        foreach ($warning in @($Warnings)) {
            if (-not [string]::IsNullOrWhiteSpace($warning)) {
                [Console]::Error.WriteLine("[warn] $warning")
            }
        }
    }

    return
}

function Write-CucliFailure {
    param(
        [string]$ErrorCode,
        [string]$Message,
        [string]$Command = $null,
        [string]$Action = $null,
        $Data = $null,
        [string]$Format = 'human'
    )

    return Write-CucliResult -Success $false -Command $Command -Action $Action `
        -Data $Data -ErrorCode $ErrorCode -ErrorMessage $Message -Format $Format
}

#endregion

#region ---------------------------------------------------------------- Unity discovery

function Get-ProjectVersion {
    param([string]$ProjectRoot)
    $path = Join-Path $ProjectRoot 'ProjectSettings/ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $lines = Get-Content -LiteralPath $path -Encoding UTF8
    foreach ($line in $lines) {
        if ($line -match '^m_EditorVersion:\s*(.+)$') { return $Matches[1].Trim() }
    }
    return $null
}

function Get-UnityCandidates {
    <#
        Finds Unity editors, most-likely first:
          1. the Editor that owns the running worker (from the heartbeat),
          2. CUCLI_UNITY_PATH,
          3. an install whose folder matches the project's declared editor version,
          4. any other install, only as a last resort.

        Step 3 must finish across every root BEFORE step 4 starts. Opening a project with the wrong
        Unity version triggers a full reimport and can leave Library inconsistent, so an unrelated
        editor must never outrank the correct one just because its root came first.
    #>
    param([string]$ProjectRoot)

    $seen = New-Object System.Collections.Generic.HashSet[string]
    $results = New-Object System.Collections.Generic.List[string]

    $add = {
        param([string]$Path)
        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        if (-not (Test-Path -LiteralPath $Path)) { return }
        $full = [System.IO.Path]::GetFullPath($Path)
        if ($seen.Add($full.ToLowerInvariant())) { $results.Add($full) }
    }

    $heartbeat = Get-Heartbeat $ProjectRoot
    if ($null -ne $heartbeat -and $heartbeat.applicationPath) { & $add $heartbeat.applicationPath }
    if ($env:CUCLI_UNITY_PATH) { & $add $env:CUCLI_UNITY_PATH }

    $version = Get-ProjectVersion $ProjectRoot
    if ($version) {
        $roots = @('C:\Program Files\Unity\Hub\Editor', 'D:\Program Files\Unity\Hub\Editor',
                   "$env:LOCALAPPDATA\Unity\Hub\Editor", 'D:\Program Files\UnityEngines',
                   'C:\Program Files\Unity\Hub\Editor\Unity China',
                   'D:\Program Files\Unity\Hub\Editor\Unity China')
        $existing = @($roots | Where-Object { $_ -and (Test-Path -LiteralPath $_) })

        # Pass 1: installs that match the project's editor version.
        #
        # Folder layout is not standardised: Unity Hub uses "<root>/<version>/Editor", but engine
        # installs are commonly "<root>/Unity <version>/Editor" and China builds add a suffix. So
        # match on the folder name CONTAINING the version rather than on one assumed layout.
        foreach ($root in $existing) {
            $direct = Join-Path $root "$version/Editor/Unity.exe"
            if (Test-Path -LiteralPath $direct) { & $add $direct }

            Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "*$version*" } |
                ForEach-Object {
                    $candidate = Join-Path $_.FullName 'Editor/Unity.exe'
                    if (Test-Path -LiteralPath $candidate) { & $add $candidate }
                }
        }

        # Pass 2: anything else, and only after every exact match has been collected.
        foreach ($root in $existing) {
            Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $candidate = Join-Path $_.FullName 'Editor/Unity.exe'
                if (Test-Path -LiteralPath $candidate) { & $add $candidate }
            }
        }
    }

    return $results
}

function Get-UnityHubPath {
    foreach ($candidate in @(
            "$env:ProgramFiles\Unity Hub\Unity Hub.exe",
            "${env:ProgramFiles(x86)}\Unity Hub\Unity Hub.exe",
            "$env:LOCALAPPDATA\Programs\Unity Hub\Unity Hub.exe")) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

#endregion

#region ---------------------------------------------------------------- job transport

function Get-JobDirectories {
    param([string]$ProjectRoot)
    $jobs = Join-Path (Get-RuntimeRoot $ProjectRoot) 'jobs'
    return [ordered]@{
        Root      = $jobs
        Control   = Join-Path $jobs 'control'
        Queued    = Join-Path $jobs 'queued'
        Running   = Join-Path $jobs 'running'
        Waiting   = Join-Path $jobs 'waiting'
        Succeeded = Join-Path $jobs 'succeeded'
        Failed    = Join-Path $jobs 'failed'
        Cancelled = Join-Path $jobs 'cancelled'
    }
}

function Submit-CucliJob {
    param(
        [string]$ProjectRoot,
        [string]$Command,
        [string]$Action,
        [System.Collections.IDictionary]$Payload,
        [int]$TimeoutSeconds,
        [bool]$Control = $false
    )

    $directories = Get-JobDirectories $ProjectRoot
    foreach ($key in @('Control', 'Queued', 'Running', 'Waiting', 'Succeeded', 'Failed', 'Cancelled')) {
        $path = $directories[$key]
        if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
    }

    $id = [guid]::NewGuid().ToString('N')
    $effective = [ordered]@{}
    if ($Payload) {
        foreach ($key in $Payload.Keys) { $effective[$key] = $Payload[$key] }
    }
    if (-not [string]::IsNullOrWhiteSpace($Action)) { $effective['action'] = $Action }

    $request = [ordered]@{
        schemaVersion  = 'cucli.job-request/v1'
        id             = $id
        command        = $Command
        action         = $Action
        createdAtUtc   = [datetime]::UtcNow.ToString('O')
        payloadJson    = (ConvertTo-JsonSafe $effective)
        timeoutSeconds = $TimeoutSeconds
        detached       = $false
    }

    $target = if ($Control) { $directories.Control } else { $directories.Queued }
    Write-Utf8File -Path (Join-Path $target "$id.json") -Content (ConvertTo-JsonSafe $request)
    return $id
}

function Get-WorkerStallHint {
    <#
        Explains a timeout that is NOT a slow command.

        The worker publishes its heartbeat from a background thread, so `timestampUtc` stays fresh
        even when the Editor's main thread is blocked. Job execution runs ON the main thread, so a
        stale `stateSampledAtUtc` means the queue physically cannot drain no matter how long the
        caller waits - which is a completely different problem from "the command is slow", and needs a
        completely different response. Reported here so a caller is not left guessing.
    #>
    param([string]$ProjectRoot)

    $heartbeat = Get-Heartbeat $ProjectRoot
    if ($null -eq $heartbeat -or [string]::IsNullOrWhiteSpace($heartbeat.stateSampledAtUtc)) { return '' }

    try {
        $sampled = [datetime]::Parse(
            $heartbeat.stateSampledAtUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
        $age = [int]([datetime]::UtcNow - $sampled).TotalSeconds
    } catch {
        return ''
    }

    if ($age -lt 20) { return '' }

    return " The Editor's main thread has not ticked for $age seconds, so the queue cannot drain: " +
           "Unity is blocked in a synchronous operation or a modal dialog. Job execution happens on that " +
           "thread, so waiting longer will not help. Bring the Unity window to the front to check for a " +
           "dialog, or restart the Editor; queued jobs survive a restart."
}

function Find-JobFile {
    param([string]$ProjectRoot, [string]$JobId)
    $directories = Get-JobDirectories $ProjectRoot
    foreach ($key in @('Control', 'Queued', 'Running', 'Waiting', 'Succeeded', 'Failed', 'Cancelled')) {
        $path = Join-Path $directories[$key] "$JobId.json"
        if (Test-Path -LiteralPath $path) {
            return [ordered]@{ Path = $path; State = $key.ToLowerInvariant() }
        }
    }
    return $null
}

function Wait-CucliJob {
    param(
        [string]$ProjectRoot,
        [string]$JobId,
        [int]$TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $cancelRequested = $false

    while ($true) {
        $found = Find-JobFile -ProjectRoot $ProjectRoot -JobId $JobId
        if ($null -eq $found) {
            return [ordered]@{
                outcome = 'missing'
                message = "Job $JobId is not present in any queue; it may have been pruned."
            }
        }

        if ($found.State -in @('succeeded', 'failed', 'cancelled')) {
            $result = Read-JsonFile $found.Path
            return [ordered]@{ outcome = $found.State; result = $result; path = $found.Path }
        }

        if ([datetime]::UtcNow -ge $deadline) {
            if (-not $cancelRequested) {
                Request-JobCancellation -ProjectRoot $ProjectRoot -JobId $JobId
                $cancelRequested = $true
            }

            return [ordered]@{
                outcome = 'timeout'
                message = "Job $JobId did not finish within $TimeoutSeconds seconds. A cancellation request was written; use 'cucli job status $JobId' to follow it." + (Get-WorkerStallHint $ProjectRoot)
            }
        }

        Start-Sleep -Milliseconds 150
    }
}

function Request-JobCancellation {
    param([string]$ProjectRoot, [string]$JobId)
    $path = Join-Path (Get-RuntimeRoot $ProjectRoot) "jobs/cancel-requests/$JobId.json"
    $payload = [ordered]@{
        id              = $JobId
        requestedAtUtc  = [datetime]::UtcNow.ToString('O')
        reason          = 'timeout'
    }
    Write-Utf8File -Path $path -Content (ConvertTo-JsonSafe $payload)
}

function Get-QueueCounts {
    param([string]$ProjectRoot)
    $directories = Get-JobDirectories $ProjectRoot
    $counts = [ordered]@{}
    foreach ($key in @('Control', 'Queued', 'Running', 'Waiting', 'Succeeded', 'Failed', 'Cancelled')) {
        $path = $directories[$key]
        $count = 0
        if (Test-Path -LiteralPath $path) {
            $count = @(Get-ChildItem -LiteralPath $path -Filter '*.json' -File -ErrorAction SilentlyContinue).Count
        }
        $counts[$key.ToLowerInvariant()] = $count
    }
    return $counts
}

#endregion

#region ---------------------------------------------------------------- command catalog

function Get-GroupIndex {
    <#
        Builds the group -> metadata index the dispatcher needs.

        Precedence, lowest to highest:
          1. the shipped commands/registry.json  (package commands, always present)
          2. the project-local registry.local.json (project commands; only from the extension area)
          3. the live worker catalog              (authoritative while Unity is open)

        Aliases and offline groups merge with the project layer winning, because a project
        legitimately wants its own shorthand. Group *names* do not: a project group that collides
        with a package group is reported, not applied. The Editor registry refuses the same
        collision, so honouring it here would mean the CLI and the worker disagreed about what
        `cucli <group> <action>` does.
    #>
    param([string]$ProjectRoot)

    $runtime = Get-RuntimeCatalog $ProjectRoot
    $static = Get-StaticRegistry
    $local = Get-LocalRegistry $ProjectRoot

    $index = [ordered]@{
        Source  = 'static'
        Groups  = @{}
        Aliases = @{}
        Offline = @{}
        Hash    = $null
        Diagnostics = @()
        GroupConflicts = New-Object System.Collections.Generic.List[string]
        LocalRegistryPresent = ($null -ne $local)
        LocalGroupCount = 0
    }

    if ($null -ne $static) {
        if ($static.aliases) {
            foreach ($property in $static.aliases.PSObject.Properties) {
                $index.Aliases[$property.Name] = $property.Value
            }
        }
        if ($static.offline) {
            foreach ($entry in $static.offline) { $index.Offline[$entry.name] = $entry }
        }
        if ($static.groups) {
            foreach ($group in $static.groups) { $index.Groups[$group.name] = $group }
        }
        $index.Hash = $static.catalogHash
    }

    if ($null -ne $local) {
        if ($local.aliases) {
            foreach ($property in $local.aliases.PSObject.Properties) {
                $index.Aliases[$property.Name] = $property.Value
            }
        }
        if ($local.offline) {
            foreach ($entry in $local.offline) { $index.Offline[$entry.name] = $entry }
        }
        if ($local.groups) {
            foreach ($group in $local.groups) {
                if ($index.Groups.ContainsKey($group.name)) {
                    $index.GroupConflicts.Add($group.name)
                    continue
                }

                $index.Groups[$group.name] = $group
                $index.LocalGroupCount++
            }
        }
    }

    if ($null -ne $runtime -and $runtime.commands) {
        $index.Source = 'runtime'
        $index.Groups = @{}
        foreach ($command in $runtime.commands) { $index.Groups[$command.name] = $command }
        $index.Hash = $runtime.catalogHash
        if ($runtime.diagnostics) { $index.Diagnostics = @($runtime.diagnostics) }
    }

    return $index
}

function Resolve-Group {
    param($Index, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    $key = $Name.ToLowerInvariant()
    if ($Index.Aliases.ContainsKey($key)) { $key = $Index.Aliases[$key].ToLowerInvariant() }
    if ($Index.Offline.ContainsKey($key)) { return $Index.Offline[$key] }
    if ($Index.Groups.ContainsKey($key)) { return $Index.Groups[$key] }

    # Separator-insensitive fallback, mirroring Resolve-DeclaredName for actions.
    $probe = ($key -replace '[-_]', '')
    foreach ($table in @($Index.Offline, $Index.Groups)) {
        $hits = @($table.Keys | Where-Object { ($_ -replace '[-_]', '') -eq $probe })
        if ($hits.Count -eq 1) { return $table[$hits[0]] }
    }

    return $null
}

function Resolve-DeclaredName {
    <#
        Maps a token the user typed onto the exact spelling a command declares.

        Command actions are declared in C# and mostly use underscores (`get_hierarchy`), while a
        person at a shell reaches for hyphens (`get-hierarchy`). Rather than force one convention on
        everybody, accept either: match exactly first, then compare with '-' and '_' stripped. An
        ambiguous match returns nothing so the caller can list the real options instead of guessing.
    #>
    param([string[]]$Declared, [string]$Typed)

    if ([string]::IsNullOrWhiteSpace($Typed) -or $null -eq $Declared) { return $null }

    $token = $Typed.Trim()
    foreach ($candidate in $Declared) {
        if ($candidate -ieq $token) { return $candidate }
    }

    $key = ($token -replace '[-_]', '').ToLowerInvariant()
    $hits = @($Declared | Where-Object { ($_ -replace '[-_]', '').ToLowerInvariant() -eq $key })
    if ($hits.Count -eq 1) { return $hits[0] }
    return $null
}

function Get-GroupActions {
    param($Group)
    if ($null -eq $Group) { return @() }
    if ($Group.actions) { return @($Group.actions | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.name } }) }
    return @()
}

function Get-GroupActionObjects {
    <#
        Full action declarations, as { name, summary }.

        The runtime catalog publishes both, and `commands list` must not flatten them to bare names:
        a summary is the tool's self-description, so dropping it makes the catalog useless for
        discovering what an action does. A plain string array is still accepted, because a
        hand-written `extensions.json` or an older static registry may contain one.
    #>
    param($Group)
    if ($null -eq $Group -or -not $Group.actions) { return @() }

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($action in @($Group.actions)) {
        if ($null -eq $action) { continue }
        if ($action -is [string]) {
            $result.Add([ordered]@{ name = $action; summary = $null })
            continue
        }

        $name = $action.name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $result.Add([ordered]@{ name = $name; summary = $action.summary })
    }

    return $result.ToArray()
}

function Test-ControlAction {
    <# A project may add its own control actions, so both registries are consulted. #>
    param($Index, [string]$ProjectRoot, [string]$Group, [string]$Action)
    $key = "$Group/$Action"
    foreach ($registry in @((Get-StaticRegistry), (Get-LocalRegistry $ProjectRoot))) {
        if ($null -eq $registry -or -not $registry.controlActions) { continue }
        if (@($registry.controlActions) -contains $key) { return $true }
    }
    return $false
}

#endregion

#region ---------------------------------------------------------------- offline commands

function Invoke-HostProject {
    param([string]$ProjectRoot, [string]$Format)
    $version = Get-ProjectVersion $ProjectRoot
    $data = [ordered]@{
        projectRoot   = $ProjectRoot
        projectName   = Split-Path -Leaf $ProjectRoot
        unityVersion  = $version
        dataPath      = Join-Path $ProjectRoot 'Assets'
        packageRoot   = Get-PackageRoot
        runtimeRoot   = Get-RuntimeRoot $ProjectRoot
        workerOnline  = (Test-WorkerOnline $ProjectRoot)
    }
    return Write-CucliResult -Success $true -Command 'project' -Action 'show' -Data $data -Format $Format
}

function Invoke-HostStatus {
    param([string]$ProjectRoot, [string]$Format)
    $heartbeat = Get-Heartbeat $ProjectRoot
    $age = Get-HeartbeatAgeSeconds $heartbeat
    $online = (Test-WorkerOnline $ProjectRoot)
    $catalog = Get-RuntimeCatalog $ProjectRoot

    $data = [ordered]@{
        worker = [ordered]@{
            online        = $online
            heartbeatAgeSeconds = if ($null -eq $age) { $null } else { [math]::Round($age, 2) }
            # Liveness and state age are reported separately on purpose. The heartbeat timestamp is
            # refreshed by a background timer in the Editor, so it stays current even while Unity is
            # unfocused or importing; the Unity state fields are only re-sampled when the Editor's own
            # update loop runs. A fresh heartbeat with an old state sample means "alive but busy",
            # which is exactly what a caller needs to distinguish from "gone".
            stateSampledAtUtc = if ($heartbeat) { $heartbeat.stateSampledAtUtc } else { $null }
            stateAgeSeconds = if ($heartbeat -and $heartbeat.stateSampledAtUtc) {
                try {
                    $sampled = [datetime]::Parse(
                        $heartbeat.stateSampledAtUtc,
                        [System.Globalization.CultureInfo]::InvariantCulture,
                        [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
                    [math]::Round(([datetime]::UtcNow - $sampled).TotalSeconds, 2)
                } catch { $null }
            } else { $null }
            sessionId     = if ($heartbeat) { $heartbeat.sessionId } else { $null }
            workerVersion = if ($heartbeat) { $heartbeat.workerVersion } else { $null }
            protocolVersion = if ($heartbeat) { $heartbeat.protocolVersion } else { $null }
            unityVersion  = if ($heartbeat) { $heartbeat.unityVersion } else { $null }
            processId     = if ($heartbeat) { $heartbeat.processId } else { $null }
            isPlaying     = if ($heartbeat) { $heartbeat.isPlaying } else { $false }
            isPaused      = if ($heartbeat) { $heartbeat.isPaused } else { $false }
            isCompiling   = if ($heartbeat) { $heartbeat.isCompiling } else { $false }
            isUpdating    = if ($heartbeat) { $heartbeat.isUpdating } else { $false }
            hasCompilationErrors = if ($heartbeat) { $heartbeat.hasCompilationErrors } else { $false }
            activeScene   = if ($heartbeat) { $heartbeat.activeScenePath } else { $null }
        }
        queues = Get-QueueCounts $ProjectRoot
        catalog = [ordered]@{
            source        = if ($catalog) { 'runtime' } else { 'static' }
            commandCount  = if ($catalog -and $catalog.commands) { @($catalog.commands).Count } else { 0 }
            catalogHash   = if ($catalog) { $catalog.catalogHash } else { $null }
            diagnostics   = if ($catalog -and $catalog.diagnostics) { @($catalog.diagnostics).Count } else { 0 }
        }
    }

    $warnings = @()
    if (-not $online) {
        $warnings += "The Unity Editor worker is not responding. Editor commands will fail with WORKER_UNAVAILABLE until Unity is open on this project."
    }
    if ($heartbeat -and $heartbeat.hasCompilationErrors) {
        $warnings += "The Editor reports script compilation errors; asset writes may be rejected."
    }

    return Write-CucliResult -Success $true -Command 'status' -Action 'show' -Data $data `
        -Warnings $warnings -Format $Format
}

function Invoke-HostLogs {
    param([string]$ProjectRoot, $Arguments, [string]$Format)
    $source = if ($Arguments.Positional.Count -gt 1) { $Arguments.Positional[1] } else { 'game' }
    $tail = if ($Arguments.Payload.Contains('tail')) { [int]$Arguments.Payload['tail'] } else { 100 }
    $grep = if ($Arguments.Payload.Contains('grep')) { [string]$Arguments.Payload['grep'] } else { $null }

    $paths = [ordered]@{
        game = Join-Path $ProjectRoot 'log.txt'
        cli  = Join-Path (Get-RuntimeRoot $ProjectRoot) 'cli-log.ndjson'
        console = Join-Path (Get-RuntimeRoot $ProjectRoot) 'console/entries.ndjson'
    }

    if (-not $paths.Contains($source)) {
        return Write-CucliFailure -ErrorCode 'USAGE_ERROR' `
            -Message "Unknown log source '$source'. Available: game, cli, console." `
            -Command 'logs' -Action $source -Format $Format
    }

    $path = $paths[$source]
    if (-not (Test-Path -LiteralPath $path)) {
        return Write-CucliResult -Success $true -Command 'logs' -Action $source `
            -Data ([ordered]@{ source = $source; path = $path; exists = $false; lines = @(); returned = 0 }) `
            -Warnings @("No log file at $path yet.") -Format $Format
    }

    # Tail by reading only the end of the file so a multi-hundred-megabyte log stays fast.
    $lines = Read-TailLines -Path $path -Count $tail
    if ($grep) {
        $lines = @($lines | Where-Object { $_ -match $grep })
    }

    $data = [ordered]@{
        source   = $source
        path     = $path
        exists   = $true
        returned = @($lines).Count
        lines    = @($lines)
    }
    return Write-CucliResult -Success $true -Command 'logs' -Action $source -Data $data -Format $Format
}

function Read-TailLines {
    param([string]$Path, [int]$Count, [int]$MaxBytes = 4194304)

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $length = $stream.Length
        $start = [math]::Max(0, $length - $MaxBytes)
        [void]$stream.Seek($start, [System.IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ($length - $start)
        [void]$stream.Read($buffer, 0, $buffer.Length)
        $text = [System.Text.Encoding]::UTF8.GetString($buffer)
        if ($start -gt 0) {
            $firstBreak = $text.IndexOf("`n")
            if ($firstBreak -ge 0) { $text = $text.Substring($firstBreak + 1) }
        }
        $all = $text -split "`r?`n"
        if ($all.Count -gt $Count) { $all = $all[($all.Count - $Count)..($all.Count - 1)] }
        return $all
    } finally {
        $stream.Dispose()
    }
}

function Invoke-HostDoctor {
    param([string]$ProjectRoot, $Arguments, [string]$Format)

    # Both spellings are accepted because the shipped registry declares 'deep' as an ACTION, so a
    # caller who follows `cucli commands describe doctor` will write `cucli doctor deep`. Silently
    # running the shallow check in that case would make a diagnostic tool quietly do less than asked.
    $deep = [bool]($Arguments.Payload['deep'])
    if (-not $deep -and $Arguments.Positional.Count -gt 1) {
        $deep = ([string]$Arguments.Positional[1]).ToLowerInvariant() -eq 'deep'
    }

    $checks = New-Object System.Collections.Generic.List[object]
    # A hashtable is a reference type, so the scriptblock below can accumulate into it.
    $state = @{ failures = 0; warnings = 0 }

    $addCheck = {
        param([string]$Name, [bool]$Ok, [string]$Detail, [string]$Severity = 'error')
        if (-not $Ok) {
            if ($Severity -eq 'error') { $state.failures++ } else { $state.warnings++ }
        }
        $checks.Add([ordered]@{ name = $Name; ok = $Ok; severity = $Severity; detail = $Detail })
    }

    $version = Get-ProjectVersion $ProjectRoot
    & $addCheck 'project.version' ($null -ne $version) "ProjectSettings/ProjectVersion.txt declares Unity $version."

    # Scope boundary, stated where a user actually meets it.
    #
    # The README says this tool targets Unity 2022 and that Unity 6 should use Unity's own CLI. A
    # README is not a support policy, so the same verdict is reported here: a Unity 6 project is told
    # to switch tools before it burns time on a bridge package that was never built for it.
    $majorVersion = 0
    if ($version -and $version -match '^(\d+)\.') { $majorVersion = [int]$Matches[1] }

    if ($majorVersion -ge 6000) {
        & $addCheck 'unity.versionSupported' $false `
            ("Unity $version is out of scope. This tool targets Unity 2022 (verified on 2022.3) and its " +
             "design has not considered Unity 6 at all. On Unity 6 use Unity CLI, the official " +
             "command-line interface for that generation: " +
             "https://docs.unity.com/en-us/unity-cli/unity-cli-reference")
    } elseif ($majorVersion -eq 2022) {
        & $addCheck 'unity.versionSupported' $true "Unity $version is the supported and verified target."
    } elseif ($majorVersion -gt 0) {
        & $addCheck 'unity.versionSupported' $true `
            ("Unity $version is outside the tested range (2022.3). Nothing here has been verified on it; " +
             "treat 2022.3 as the supported target.") 'warning'
    }

    $packageJson = Join-Path (Get-PackageRoot) 'package.json'
    $package = Read-JsonFile $packageJson
    & $addCheck 'package.manifest' ($null -ne $package) `
        $(if ($package) { "Package $($package.name) v$($package.version) at $(Get-PackageRoot)" } else { "Missing package.json at $packageJson" })

    $registry = Get-StaticRegistry
    & $addCheck 'registry.static' ($null -ne $registry) `
        $(if ($registry) { "Static registry lists $(@($registry.groups).Count) group(s)." } else { 'commands/registry.json is missing; offline command listing is degraded.' }) 'warning'

    $heartbeat = Get-Heartbeat $ProjectRoot
    $online = Test-WorkerOnline $ProjectRoot
    & $addCheck 'worker.heartbeat' $online `
        $(if ($online) { "Worker session $($heartbeat.sessionId) is alive." } else { "No fresh heartbeat under $(Get-RuntimeRoot $ProjectRoot)/worker." }) 'warning'

    if ($online) {
        $catalog = Get-RuntimeCatalog $ProjectRoot
        $diagnosticCount = if ($catalog -and $catalog.diagnostics) { @($catalog.diagnostics).Count } else { 0 }
        & $addCheck 'registry.diagnostics' ($diagnosticCount -eq 0) `
            $(if ($diagnosticCount -eq 0) { 'No command registration diagnostics.' } else { "$diagnosticCount command registration diagnostic(s); run 'cucli commands list --diagnostics'." })

        if ($registry -and $catalog -and $registry.catalogHash -and $catalog.catalogHash) {
            $match = ($registry.catalogHash -eq $catalog.catalogHash)
            & $addCheck 'registry.drift' $match `
                $(if ($match) { 'Static registry matches the compiled command surface.' } else { "Static registry hash $($registry.catalogHash) differs from the compiled hash $($catalog.catalogHash). Run 'cucli commands export' to refresh it." }) 'warning'
        }
    }

    $unityCandidates = Get-UnityCandidates $ProjectRoot
    $projectVersion = Get-ProjectVersion $ProjectRoot
    $unityFound = @($unityCandidates).Count -gt 0
    # A wrong-version Editor triggers a full reimport and can leave Library inconsistent, so a
    # mismatch is reported as a failure rather than as a note. 'cucli open' would pick candidates[0].
    $unityMatches = $unityFound -and (
        [string]::IsNullOrWhiteSpace($projectVersion) -or ($unityCandidates[0] -like "*$projectVersion*"))
    & $addCheck 'unity.editor' ($unityFound -and $unityMatches) `
        $(if (-not $unityFound) {
            'No Unity editor found; pass CUCLI_UNITY_PATH or install the project version.'
        } elseif ($unityMatches) {
            "Found Unity $projectVersion at $($unityCandidates[0])"
        } else {
            "Best candidate $($unityCandidates[0]) does not match the project's Unity $projectVersion. " +
            "Set CUCLI_UNITY_PATH before running 'cucli open', or Unity will reimport the whole project."
        })

    # --- package purity ---------------------------------------------------
    #
    # The package is a published artifact and project-specific code must live outside it, or it ends
    # up upstream. These are the CLI-side half of that guarantee; the Editor-side half is
    # 'cucli meta verify', which walks the same boundary from inside Unity.

    $packageRoot = Get-PackageRoot
    $extensionRoot = Get-ExtensionRoot $ProjectRoot
    $extensionInsidePackage = $false
    if (Test-Path -LiteralPath $extensionRoot) {
        $packageFull = [System.IO.Path]::GetFullPath($packageRoot).TrimEnd('\', '/') +
            [System.IO.Path]::DirectorySeparatorChar
        $extensionFull = [System.IO.Path]::GetFullPath($extensionRoot)
        $extensionInsidePackage = $extensionFull.StartsWith(
            $packageFull, [System.StringComparison]::OrdinalIgnoreCase)
    }

    & $addCheck 'extensions.placement' (-not $extensionInsidePackage) `
        $(if ($extensionInsidePackage) {
            "The extension area '$extensionRoot' is INSIDE the package '$packageRoot'. " +
            "Project code placed there would be published upstream; move it outside the package."
        } elseif (Test-Path -LiteralPath $extensionRoot) {
            "Extension area '$extensionRoot' is outside the package."
        } else {
            "No extension area yet at '$extensionRoot'; create one with 'cucli extend init' when this project needs its own commands."
        }) 'warning'

    $index = Get-GroupIndex $ProjectRoot
    $conflicts = @($index.GroupConflicts)
    & $addCheck 'extensions.group-conflicts' ($conflicts.Count -eq 0) `
        $(if ($conflicts.Count -eq 0) {
            "$($index.LocalGroupCount) project-local command group(s); no name collisions with the package."
        } else {
            "Project extension(s) reuse package group name(s): $($conflicts -join ', '). " +
            "Pick a group name of your own; a shipped group cannot be redefined per project."
        })

    # A dirty git checkout of the package means the published artifact was edited in place. That is
    # the one mistake that ends up in the upstream repository, so it is reported rather than assumed.
    #
    # Two things are deliberately NOT counted as leakage:
    #   * untracked files (`??`) - nothing is staged, so nothing can be committed by accident;
    #   * in a repository with no commits yet, everything is staged as `A `, which says nothing about
    #     whether the package was edited. Reporting 150 "modifications" for a fresh checkout would be
    #     noise, and noise is how a real warning gets ignored.
    $packageGit = Join-Path $packageRoot '.git'
    if (Test-Path -LiteralPath $packageGit) {
        $porcelain = $null
        $hasHead = $true
        try {
            & git -C $packageRoot rev-parse --verify HEAD 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { $hasHead = $false }
        } catch {
            $hasHead = $true
        }

        try {
            $porcelain = & git -C $packageRoot status --porcelain --untracked-files=no 2>$null
            if ($LASTEXITCODE -ne 0) { $porcelain = $null }
        } catch {
            $porcelain = $null
        }

        if ($null -eq $porcelain) {
            & $addCheck 'package.checkout' $true `
                'The package is a git checkout but git could not be queried; skipped.' 'warning'
        } elseif (-not $hasHead) {
            # Before the first commit every file in the tree is only staged or modified relative to
            # nothing, so nothing can have been published yet. Calling each of them a "modification"
            # is pure noise, and noise is how a real warning gets ignored.
            & $addCheck 'package.checkout' $true `
                'The package git checkout has no commits yet; nothing could be published upstream.' 'warning'
        } else {
            $entries = @($porcelain | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

            & $addCheck 'package.checkout' ($entries.Count -eq 0) `
                $(if ($entries.Count -eq 0) {
                    'The package git checkout is clean: nothing local would be published upstream.'
                } else {
                    "$($entries.Count) local modification(s) inside the package checkout. Move project-specific work to the extension area or upstream it deliberately. First: $($entries[0].Trim())"
                }) 'warning'
        }
    }

    if ($deep) {
        if (-not $online) {
            & $addCheck 'worker.roundtrip' $false 'Cannot ping the worker while it is offline.'
        } else {
            $jobId = Submit-CucliJob -ProjectRoot $ProjectRoot -Command 'editor' -Action 'ping' `
                -Payload @{} -TimeoutSeconds 30
            $outcome = Wait-CucliJob -ProjectRoot $ProjectRoot -JobId $jobId -TimeoutSeconds 30
            $ok = ($outcome.outcome -eq 'succeeded' -and $outcome.result -and $outcome.result.success)
            & $addCheck 'worker.roundtrip' $ok `
                $(if ($ok) { 'A full CLI -> queue -> Editor -> result round trip succeeded.' } else { "Deep ping failed: $($outcome.outcome) $($outcome.message)" })
        }
    }

    $data = [ordered]@{
        projectRoot = $ProjectRoot
        deep        = $deep
        ok          = ($state.failures -eq 0)
        failureCount = $state.failures
        warningCount = $state.warnings
        checks      = $checks.ToArray()
    }

    return Write-CucliResult -Success ($state.failures -eq 0) -Command 'doctor' -Action $(if ($deep) { 'deep' } else { 'check' }) `
        -Data $data -ErrorCode $(if ($state.failures -eq 0) { $null } else { 'WORKER_UNAVAILABLE' }) `
        -ErrorMessage $(if ($state.failures -eq 0) { $null } else { "$($state.failures) diagnostic check(s) failed." }) `
        -Format $Format
}

function Invoke-HostCommands {
    param([string]$ProjectRoot, $Arguments, [string]$Format)
    $action = if ($Arguments.Positional.Count -gt 1) { $Arguments.Positional[1].ToLowerInvariant() } else { 'list' }
    $target = if ($Arguments.Positional.Count -gt 2) { $Arguments.Positional[2] } else { $null }
    $index = Get-GroupIndex $ProjectRoot

    switch ($action) {
        'list' {
            $groups = New-Object System.Collections.Generic.List[object]
            foreach ($name in ($index.Groups.Keys | Sort-Object)) {
                $group = $index.Groups[$name]
                $actionObjects = @(Get-GroupActionObjects $group)
                $groups.Add([ordered]@{
                    name           = $group.name
                    summary        = $group.summary
                    actionCount    = $actionObjects.Count
                    actions        = $actionObjects
                    actionNames    = @($actionObjects | ForEach-Object { $_.name })
                    requiresEditor = $group.requiresEditor
                    requiredMode   = $group.requiredMode
                    mutatesProject = $group.mutatesProject
                    mayReloadDomain = $group.mayReloadDomain
                    optionalPackage = $group.optionalPackage
                    source         = $group.source
                    sourcePath     = $group.sourcePath
                })
            }

            $offline = New-Object System.Collections.Generic.List[object]
            foreach ($name in ($index.Offline.Keys | Sort-Object)) {
                $entry = $index.Offline[$name]
                $offlineActions = @(Get-GroupActionObjects $entry)
                $offline.Add([ordered]@{
                    name        = $entry.name
                    summary     = $entry.summary
                    actionCount = $offlineActions.Count
                    actions     = $offlineActions
                    actionNames = @($offlineActions | ForEach-Object { $_.name })
                })
            }

            $data = [ordered]@{
                source      = $index.Source
                catalogHash = $index.Hash
                groupCount  = $groups.Count
                groups      = $groups.ToArray()
                offline     = $offline.ToArray()
                aliases     = $index.Aliases
            }

            if ($Arguments.Payload.Contains('diagnostics') -and $Arguments.Payload['diagnostics']) {
                $data['diagnostics'] = @($index.Diagnostics)
            }

            return Write-CucliResult -Success $true -Command 'commands' -Action 'list' -Data $data -Format $Format
        }

        'describe' {
            if (-not $target) {
                return Write-CucliFailure -ErrorCode 'MISSING_PARAMETER' `
                    -Message "commands describe needs a group name." -Command 'commands' -Action 'describe' -Format $Format
            }
            $group = Resolve-Group $index $target
            if ($null -eq $group) {
                return Write-CucliFailure -ErrorCode 'EDITOR_COMMAND_NOT_FOUND' `
                    -Message "Unknown group '$target'. Run 'cucli commands list' to see the available groups." `
                    -Command 'commands' -Action 'describe' -Format $Format
            }
            return Write-CucliResult -Success $true -Command 'commands' -Action 'describe' -Data $group -Format $Format
        }

        'groups' {
            $names = @($index.Groups.Keys | Sort-Object) + @($index.Offline.Keys | Sort-Object)
            return Write-CucliResult -Success $true -Command 'commands' -Action 'groups' -Data ([ordered]@{ groups = $names }) -Format $Format
        }

        'export' {
            return Invoke-JobDispatch -ProjectRoot $ProjectRoot -Group 'meta' -Action 'export-registry' `
                -Arguments $Arguments -Format $Format
        }

        default {
            return Write-CucliFailure -ErrorCode 'UNSUPPORTED_ACTION' `
                -Message "Unsupported action '$action'. Supported: list, describe, groups, export." `
                -Command 'commands' -Action $action -Format $Format
        }
    }
}

function Invoke-HostJob {
    param([string]$ProjectRoot, $Arguments, [string]$Format)
    $action = if ($Arguments.Positional.Count -gt 1) { $Arguments.Positional[1].ToLowerInvariant() } else { 'list' }
    $jobId = if ($Arguments.Positional.Count -gt 2) { $Arguments.Positional[2] } else { $null }
    $directories = Get-JobDirectories $ProjectRoot

    switch ($action) {
        'list' {
            $state = if ($Arguments.Payload.Contains('state')) { [string]$Arguments.Payload['state'] } else { $null }
            $limit = if ($Arguments.Payload.Contains('limit')) { [int]$Arguments.Payload['limit'] } else { 20 }
            $jobs = New-Object System.Collections.Generic.List[object]

            foreach ($key in @('Running', 'Waiting', 'Queued', 'Control', 'Succeeded', 'Failed', 'Cancelled')) {
                if ($state -and ($state.ToLowerInvariant() -ne $key.ToLowerInvariant())) { continue }
                $path = $directories[$key]
                if (-not (Test-Path -LiteralPath $path)) { continue }

                Get-ChildItem -LiteralPath $path -Filter '*.json' -File -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTimeUtc -Descending |
                    Select-Object -First $limit |
                    ForEach-Object {
                        $payload = Read-JsonFile $_.FullName
                        $jobs.Add([ordered]@{
                            id          = if ($payload) { $payload.id } else { $_.BaseName }
                            command     = if ($payload) { $payload.command } else { $null }
                            action      = if ($payload) { $payload.action } else { $null }
                            state       = $key.ToLowerInvariant()
                            success     = if ($payload -and $payload.PSObject.Properties['success']) { $payload.success } else { $null }
                            message     = if ($payload) { $payload.message } else { $null }
                            errorCode   = if ($payload) { $payload.errorCode } else { $null }
                            createdAtUtc = if ($payload) { $payload.createdAtUtc } else { $null }
                            completedAtUtc = if ($payload) { $payload.completedAtUtc } else { $null }
                            durationMs  = if ($payload) { $payload.durationMs } else { $null }
                        })
                    }
            }

            return Write-CucliResult -Success $true -Command 'job' -Action 'list' `
                -Data ([ordered]@{ counts = Get-QueueCounts $ProjectRoot; jobs = $jobs.ToArray() }) -Format $Format
        }

        'status' {
            if (-not $jobId) {
                return Write-CucliFailure -ErrorCode 'MISSING_PARAMETER' -Message 'job status needs a job id.' `
                    -Command 'job' -Action 'status' -Format $Format
            }
            $found = Find-JobFile -ProjectRoot $ProjectRoot -JobId $jobId
            if ($null -eq $found) {
                return Write-CucliFailure -ErrorCode 'JOB_INVALID' `
                    -Message "Job $jobId is not present in any queue." -Command 'job' -Action 'status' -Format $Format
            }
            return Write-CucliResult -Success $true -Command 'job' -Action 'status' `
                -Data ([ordered]@{ jobId = $jobId; state = $found.State; path = $found.Path; result = (Read-JsonFile $found.Path) }) `
                -Format $Format
        }

        'wait' {
            if (-not $jobId) {
                return Write-CucliFailure -ErrorCode 'MISSING_PARAMETER' -Message 'job wait needs a job id.' `
                    -Command 'job' -Action 'wait' -Format $Format
            }
            $outcome = Wait-CucliJob -ProjectRoot $ProjectRoot -JobId $jobId -TimeoutSeconds $Arguments.Timeout
            return Write-JobOutcome -Outcome $outcome -Command 'job' -Action 'wait' -Format $Format -JobId $jobId
        }

        'result' {
            if (-not $jobId) {
                return Write-CucliFailure -ErrorCode 'MISSING_PARAMETER' -Message 'job result needs a job id.' `
                    -Command 'job' -Action 'result' -Format $Format
            }
            $found = Find-JobFile -ProjectRoot $ProjectRoot -JobId $jobId
            if ($null -eq $found) {
                return Write-CucliFailure -ErrorCode 'JOB_INVALID' `
                    -Message "Job $jobId is not present in any queue." -Command 'job' -Action 'result' -Format $Format
            }
            $result = Read-JsonFile $found.Path
            if ($found.State -in @('queued', 'running', 'waiting', 'control')) {
                return Write-CucliFailure -ErrorCode 'WORKER_BUSY' `
                    -Message "Job $jobId is still $($found.State)." -Data $result -Command 'job' -Action 'result' -Format $Format
            }
            return Write-CucliResult -Success ([bool]$result.success) -Command 'job' -Action 'result' `
                -Data $result.data -ErrorCode $result.errorCode -ErrorMessage $result.message `
                -Warnings @($result.warnings) -Artifacts @($result.artifacts) `
                -DurationMs ([long]$result.durationMs) -JobId $jobId -Format $Format
        }

        'cancel' {
            if (-not $jobId) {
                return Write-CucliFailure -ErrorCode 'MISSING_PARAMETER' -Message 'job cancel needs a job id.' `
                    -Command 'job' -Action 'cancel' -Format $Format
            }
            Request-JobCancellation -ProjectRoot $ProjectRoot -JobId $jobId
            return Write-CucliResult -Success $true -Command 'job' -Action 'cancel' `
                -Data ([ordered]@{ jobId = $jobId; cancellationRequested = $true }) `
                -Message 'A cancellation request was written; the worker honours it at the next step boundary.' `
                -Format $Format
        }

        'clean' {
            $removed = 0
            foreach ($key in @('Succeeded', 'Cancelled', 'Failed')) {
                $path = $directories[$key]
                if (-not (Test-Path -LiteralPath $path)) { continue }
                $files = @(Get-ChildItem -LiteralPath $path -Filter '*.json' -File -ErrorAction SilentlyContinue)
                $removed += $files.Count
                $files | Remove-Item -Force -ErrorAction SilentlyContinue
            }
            return Write-CucliResult -Success $true -Command 'job' -Action 'clean' `
                -Data ([ordered]@{ removed = $removed }) -Format $Format
        }

        default {
            return Write-CucliFailure -ErrorCode 'UNSUPPORTED_ACTION' `
                -Message "Unsupported action '$action'. Supported: list, status, wait, result, cancel, clean." `
                -Command 'job' -Action $action -Format $Format
        }
    }
}

function Write-JobOutcome {
    param($Outcome, [string]$Command, [string]$Action, [string]$Format, [string]$JobId = $null)

    switch ($Outcome.outcome) {
        'succeeded' {
            $result = $Outcome.result
            return Write-CucliResult -Success ([bool]$result.success) -Command $(if ($result.command) { $result.command } else { $Command }) `
                -Action $(if ($result.action) { $result.action } else { $Action }) -Data $result.data `
                -ErrorCode $result.errorCode -ErrorMessage $result.message `
                -Warnings @($result.warnings) -Artifacts @($result.artifacts) `
                -DurationMs ([long]$result.durationMs) -JobId $JobId -Format $Format
        }
        'failed' {
            $result = $Outcome.result
            return Write-CucliResult -Success $false -Command $(if ($result -and $result.command) { $result.command } else { $Command }) `
                -Action $(if ($result -and $result.action) { $result.action } else { $Action }) `
                -Data $(if ($result) { $result.data } else { $null }) `
                -ErrorCode $(if ($result -and $result.errorCode) { $result.errorCode } else { 'EDITOR_COMMAND_FAILED' }) `
                -ErrorMessage $(if ($result) { $result.message } else { 'The job failed.' }) `
                -Warnings $(if ($result) { @($result.warnings) } else { @() }) `
                -DurationMs $(if ($result) { [long]$result.durationMs } else { 0 }) -JobId $JobId -Format $Format
        }
        'cancelled' {
            return Write-CucliResult -Success $false -Command $Command -Action $Action -JobId $JobId `
                -ErrorCode 'CANCELLED' -ErrorMessage 'The job was cancelled.' -Format $Format
        }
        'timeout' {
            return Write-CucliResult -Success $false -Command $Command -Action $Action -JobId $JobId `
                -ErrorCode 'TIMEOUT' -ErrorMessage $Outcome.message -Format $Format
        }
        default {
            return Write-CucliFailure -ErrorCode 'JOB_INVALID' `
                -Message $(if ($Outcome.message) { $Outcome.message } else { "Job outcome: $($Outcome.outcome)" }) `
                -Command $Command -Action $Action -Format $Format
        }
    }
}

function Invoke-HostOpen {
    param([string]$ProjectRoot, $Arguments, [string]$Format)

    if (Test-WorkerOnline $ProjectRoot) {
        $heartbeat = Get-Heartbeat $ProjectRoot
        return Write-CucliResult -Success $true -Command 'open' -Action 'start' `
            -Data ([ordered]@{ alreadyOpen = $true; pid = $heartbeat.processId; unityVersion = $heartbeat.unityVersion }) `
            -Message 'A Unity Editor worker is already alive for this project.' -Format $Format
    }

    $noLaunch = $Arguments.Payload.Contains('noLaunch') -and $Arguments.Payload['noLaunch']
    if ($noLaunch) {
        return Write-CucliResult -Success $true -Command 'open' -Action 'start' `
            -Data ([ordered]@{ launched = $false }) -Message 'Would launch Unity (dry run).' -Format $Format
    }

    $preferHub = -not ($Arguments.Payload.Contains('direct') -and $Arguments.Payload['direct'])
    $hub = Get-UnityHubPath
    $unity = @(Get-UnityCandidates $ProjectRoot) | Select-Object -First 1

    if ($preferHub -and $hub) {
        # Unity Hub reuses an already-running instance and respects the project's editor version,
        # which is safer than spawning a second Editor against the same project.
        Start-Process -FilePath $hub -ArgumentList @('--', '--projectPath', $ProjectRoot) | Out-Null
        $launched = [ordered]@{ launched = $true; via = 'unity-hub'; path = $hub; projectPath = $ProjectRoot }
    } elseif ($unity) {
        Start-Process -FilePath $unity -ArgumentList @('-projectPath', $ProjectRoot) | Out-Null
        $launched = [ordered]@{ launched = $true; via = 'editor'; path = $unity; projectPath = $ProjectRoot }
    } else {
        return Write-CucliFailure -ErrorCode 'WORKER_UNAVAILABLE' `
            -Message "No Unity editor could be found for this project. Set CUCLI_UNITY_PATH to Unity.exe, or open the project from Unity Hub." `
            -Command 'open' -Action 'start' -Format $Format
    }

    if ($Arguments.Payload.Contains('wait') -and $Arguments.Payload['wait']) {
        $deadline = [datetime]::UtcNow.AddSeconds($Arguments.Timeout)
        while ([datetime]::UtcNow -lt $deadline) {
            if (Test-WorkerOnline $ProjectRoot) {
                $launched['workerOnline'] = $true
                return Write-CucliResult -Success $true -Command 'open' -Action 'start' -Data $launched `
                    -Message 'Unity launched and the worker is responding.' -Format $Format
            }
            Start-Sleep -Seconds 2
        }
        $launched['workerOnline'] = $false
        return Write-CucliResult -Success $false -Command 'open' -Action 'start' -Data $launched `
            -ErrorCode 'WORKER_UNAVAILABLE' `
            -ErrorMessage "Unity was launched but the worker did not respond within $($Arguments.Timeout) seconds." -Format $Format
    }

    $launched['workerOnline'] = $false
    return Write-CucliResult -Success $true -Command 'open' -Action 'start' -Data $launched `
        -Message "Unity was launched; the worker will appear once the project finishes loading." -Format $Format
}

function Invoke-HostExtend {
    <#
        The project-local extension area: where a project grows its own command groups.

        Everything here works with Unity closed, because the point of scaffolding is to be able to
        start before the Editor is even open.
    #>
    param([string]$ProjectRoot, $Arguments, [string]$Format)

    $action = if ($Arguments.Positional.Count -gt 1) { $Arguments.Positional[1].ToLowerInvariant() } else { 'path' }
    $root = Get-ExtensionRoot $ProjectRoot
    $relative = Get-ExtensionRelativeRoot $ProjectRoot
    $manifest = Join-Path $root 'extensions.json'
    $localRegistry = Join-Path $root $Script:ExtensionRegistryFileName
    $index = Get-GroupIndex $ProjectRoot

    switch ($action) {
        'path' {
            $data = [ordered]@{
                extensionRoot          = $root
                extensionRootRelative  = $relative
                exists                 = (Test-Path -LiteralPath $root)
                manifestPath           = $manifest
                localRegistryPath      = $localRegistry
                localRegistryPresent   = (Test-Path -LiteralPath $localRegistry)
                insidePackage          = $relative.StartsWith('Assets/Plugins/CustomUnityCli')
                overriddenByEnvironment = -not [string]::IsNullOrWhiteSpace($env:CUCLI_EXTENSIONS)
                sourceFileCount        = @(Get-ExtensionSources $ProjectRoot).Count
            }
            return Write-CucliResult -Success $true -Command 'extend' -Action 'path' -Data $data -Format $Format
        }

        'list' {
            $sources = @(Get-ExtensionSources $ProjectRoot)
            $groups = New-Object System.Collections.Generic.List[object]
            foreach ($name in ($index.Groups.Keys | Sort-Object)) {
                $group = $index.Groups[$name]
                if ($group.source -ne 'project') { continue }
                $actionObjects = @(Get-GroupActionObjects $group)
                $groups.Add([ordered]@{
                    name        = $group.name
                    summary     = $group.summary
                    actions     = $actionObjects
                    actionNames = @($actionObjects | ForEach-Object { $_.name })
                    actionCount = $actionObjects.Count
                    sourcePath  = $group.sourcePath
                })
            }

            $data = [ordered]@{
                extensionRoot          = $root
                extensionRootRelative  = $relative
                exists                 = (Test-Path -LiteralPath $root)
                manifestPresent        = (Test-Path -LiteralPath $manifest)
                localRegistryPresent   = (Test-Path -LiteralPath $localRegistry)
                catalogSource          = $index.Source
                sourceFileCount        = $sources.Count
                sourceFiles            = $sources
                projectCommandGroups   = $groups.ToArray()
                projectCommandGroupCount = $groups.Count
            }

            $warnings = @()
            if ($relative.StartsWith('Assets/Plugins/CustomUnityCli')) {
                $warnings += "The extension area is INSIDE the package folder. Project code there can end up in the custom-unity-cli repository. Move it, or set `$env:CUCLI_EXTENSIONS to a path outside the package."
            }
            if (-not $data.exists) {
                $warnings += "The extension area does not exist yet. Run 'cucli extend init' to create it, or 'cucli meta new-command --group <name>' to scaffold a command group."
            } elseif ($sources.Count -eq 0) {
                $warnings += "The extension area exists but contains no .cs files yet. Run 'cucli meta new-command --group <name>' to scaffold one (needs Unity open), or add a file yourself."
            }
            if ($groups.Count -eq 0 -and -not $index.LocalRegistryPresent -and $index.Source -eq 'static') {
                $warnings += "No project-local command groups are declared. Run 'cucli commands export' with Unity open to write registry.local.json."
            }

            return Write-CucliResult -Success $true -Command 'extend' -Action 'list' -Data $data `
                -Warnings $warnings -Format $Format
        }

        'init' {
            $created = -not (Test-Path -LiteralPath $root)
            # MUST be named Editor. Only sources under a folder called `Editor` compile into
            # Assembly-CSharp-Editor; anywhere else lands in the runtime Assembly-CSharp, which cannot
            # see UnityEditor or the package's Editor-only assembly, and the command would silently
            # never register. Mirrors CucliExtensionPaths.CommandsFolder.
            $commandsFolder = Join-Path (Join-Path $root 'Editor') 'Commands'
            $notesFolder = Join-Path $root 'Notes'
            New-Item -ItemType Directory -Force -Path $commandsFolder, $notesFolder | Out-Null

            $manifestCreated = $false
            if (-not (Test-Path -LiteralPath $manifest)) {
                $payload = [ordered]@{
                    schemaVersion    = 'cucli.extensions/v1'
                    description      = 'Project-local custom-unity-cli extensions. This folder is intentionally outside the package: it holds command groups specific to THIS project and is never written into the custom-unity-cli repository.'
                    rootNamespace    = 'CustomUnityCli.Extensions'
                    commandsFolder   = 'Editor/Commands'
                    notesFolder      = 'Notes'
                    aliases          = [ordered]@{}
                    offline          = @()
                }
                Write-Utf8File -Path $manifest -Content (ConvertTo-JsonSafe $payload)
                $manifestCreated = $true
            }

            $readmePath = Join-Path $root 'README.md'
            $readmeCreated = $false
            if (-not (Test-Path -LiteralPath $readmePath)) {
                Write-Utf8File -Path $readmePath -Content (Get-ExtensionReadmeText)
                $readmeCreated = $true
            }

            $data = [ordered]@{
                extensionRoot         = $root
                extensionRootRelative = $relative
                created               = $created
                manifestCreated       = $manifestCreated
                readmeCreated         = $readmeCreated
                commandsFolder        = $commandsFolder
                notesFolder           = $notesFolder
            }

            $warnings = @()
            if ($relative.StartsWith('Assets/Plugins/CustomUnityCli')) {
                $warnings += "The extension area resolves INSIDE the package folder. Set `$env:CUCLI_EXTENSIONS to a path outside the package."
            }

            return Write-CucliResult -Success $true -Command 'extend' -Action 'init' -Data $data `
                -Warnings $warnings `
                -Message "Extension area ready. Add command groups under Editor/Commands/, or run 'cucli meta new-command --group <name>' with Unity open." `
                -Format $Format
        }

        default {
            return Write-CucliFailure -ErrorCode 'UNSUPPORTED_ACTION' `
                -Message "Unsupported action '$action'. Supported: path, list, init." `
                -Command 'extend' -Action $action -Format $Format
        }
    }
}

function Get-ExtensionReadmeText {
    return @'
# Custom Unity CLI - project extensions

Command groups in this folder belong to **this project only**. They are discovered automatically by
the Editor worker: put a public class deriving from `CustomUnityCli.Editor.CucliCommand` under
`Editor/Commands/`, let the project recompile, and `cucli <your-group> <action>` works immediately.

There is no assembly definition to add and nothing to configure: sources under a folder named
`Editor` compile into the prebuilt `Assembly-CSharp-Editor`, which already references the package
assembly automatically.

The folder **must** be called `Editor`. A source placed anywhere else under `Assets/` compiles into
the runtime `Assembly-CSharp`, which cannot see `UnityEditor` or the package's Editor-only assembly,
and the command would silently never register.

## This folder is not part of the package

`custom-unity-cli` lives in its own repository, and nothing here is ever written into it.
`cucli commands export` deliberately exports **package commands only** into the shipped
`commands/registry.json`, and writes project commands to `registry.local.json` in this folder
instead. `cucli extend verify` fails if a project command is found inside the package tree.

Version-control this folder in the project that owns it. Do not copy it into a fork of
custom-unity-cli.

## Scaffolding a new group

```powershell
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 meta new-command --group mytool --summary "what this does" --action scan
```

`meta new-command` needs Unity running, because it asks the Editor to compile-check the generated
file. With Unity closed you can still create the file by hand under `Editor/Commands/`.

## Recommended practice

- Grow one group per project concern: asset conventions, build steps, content checks.
- Keep project invariants in `Notes/` so an agent can rediscover them.
- When a command turns out to be useful everywhere, upstream it into the package deliberately, as a
  reviewed change -- never as a side effect of running a refresh.
'@
}

function Invoke-HostVersion {
    param([string]$ProjectRoot, [string]$Format)
    $package = Read-JsonFile (Join-Path (Get-PackageRoot) 'package.json')
    $data = [ordered]@{
        cliVersion = if ($package) { $package.version } else { 'unknown' }
        packageName = if ($package) { $package.name } else { $null }
        packageRoot = Get-PackageRoot
        powershell  = $PSVersionTable.PSVersion.ToString()
        projectRoot = $ProjectRoot
    }
    return Write-CucliResult -Success $true -Command 'version' -Action 'show' -Data $data -Format $Format
}

function Invoke-HostHelp {
    param([string]$Format = 'human')
    $help = @'
custom-unity-cli (cucli)

USAGE
  cucli <group> [action] [--option value ...] [--format human|json|ndjson]

GLOBAL OPTIONS
  --format human|json|ndjson   Output format (default: human, or json when redirected).
  --project <path>             Unity project root. Defaults to walking up from this script,
                               then from the current directory. Also honours $env:CUCLI_PROJECT.
  --timeout <seconds>          How long to wait for an Editor job (default 60).
  --action <name>              Explicit spelling of the group action. The positional form wins if
                               both are given. `action` is reserved and is never sent as a payload
                               key, so a group parameter must not be named `action`.
  --detach, --no-wait          Queue the job and return its id immediately.
  -h, --help                   Show help.

OFFLINE GROUPS (no Unity required)
  project                      Where am I and which Unity version?
  status                       Worker heartbeat, queue depths, catalog source.
  doctor [--deep]              Diagnose project, package, worker and registry drift.
  logs game|cli|console        Tail a log source.
  commands list|describe|groups|export
                               Inspect or refresh the command catalog.
  job list|status|wait|result|cancel|clean
                               Inspect and control queued Editor work.
  open [--wait] [--direct]     Launch Unity for this project.
  extend path|list|init        This project's own extension area, where local command groups live.
  version                      Print versions.

EDITOR GROUPS (need a running Editor worker)
  Run `cucli commands list` to see every group and its actions, and
  `cucli commands describe <group>` for the parameter schema of one group.

EXIT CODES
  0 ok   1 generic   2 usage   4 missing project/confirmation   6 failed
  7 editor unavailable (retryable)   8 invalid input   130 cancelled

EXAMPLES
  ./cucli.ps1 status
  ./cucli.ps1 editor state
  ./cucli.ps1 object create --name Ball --primitive sphere
  ./cucli.ps1 scene get-hierarchy --page-size 50 --format json
  ./cucli.ps1 refresh compile --wait
  ./cucli.ps1 commands describe object
'@

    if ($Format -eq 'json' -or $Format -eq 'ndjson') {
        return Write-CucliResult -Success $true -Command 'help' -Action 'show' -Data ([ordered]@{ text = $help }) -Format $Format
    }

    [Console]::Error.WriteLine($help)
    return $Script:ExitOk
}

#endregion

#region ---------------------------------------------------------------- editor dispatch

function Invoke-JobDispatch {
    param(
        [string]$ProjectRoot,
        [string]$Group,
        [string]$Action,
        $Arguments,
        [string]$Format
    )

    $started = [datetime]::UtcNow
    $index = Get-GroupIndex $ProjectRoot
    $descriptor = Resolve-Group $index $Group

    if ($null -eq $descriptor) {
        # Before the first 'cucli commands export' the static registry has no Editor groups, so an
        # Editor group looks "unknown" even though it exists. Reporting WORKER_UNAVAILABLE with the
        # real remedy is far more useful than claiming the group does not exist.
        if ($index.Source -eq 'static' -and $index.Groups.Count -eq 0 -and -not (Test-WorkerOnline $ProjectRoot)) {
            return Write-CucliFailure -ErrorCode 'WORKER_UNAVAILABLE' `
                -Message "No Unity Editor worker is running for this project, so '$Group' cannot be resolved. Open the project (or run 'cucli open'), then run 'cucli commands export' once to cache the command catalog for offline use." `
                -Command $Group -Action $Action -Format $Format
        }

        return Write-CucliFailure -ErrorCode 'EDITOR_COMMAND_NOT_FOUND' `
            -Message "Unknown command group '$Group'. Run 'cucli commands list' to see the available groups." `
            -Command $Group -Action $Action -Format $Format
    }

    # 'meta export-registry' has to be told where to write, and only the CLI knows where the package
    # is installed (a plugin folder, an embedded package, or the Package Manager cache). Injecting
    # the path here keeps that knowledge in one place and keeps 'cucli commands export' a one-liner.
    if ($Group -eq 'meta' -and $Action -eq 'export-registry' -and -not $Arguments.Payload.Contains('path')) {
        $scope = [string]$Arguments.Payload['scope']
        if ($scope -eq 'local') {
            # Route through Get-ExtensionRoot, NOT the preferred folder. Get-ExtensionRoot honours
            # $env:CUCLI_EXTENSIONS and returns the first EXISTING candidate, so a project that
            # already keeps its extensions in one of the fallback folders does not silently acquire
            # a second, dead registry.local.json that nothing ever reads. Hardcoding the preferred
            # folder here was exactly the divergence Get-ExtensionRoot's contract warns against.
            $Arguments.Payload['path'] = Join-Path (Get-ExtensionRoot $ProjectRoot) $Script:ExtensionRegistryFileName
        }
        else {
            $Arguments.Payload['path'] = Join-Path (Get-PackageRoot) 'commands/registry.json'
        }
    }

    # From here on use the declared spelling: it is what the worker matches on, and it is what
    # control-queue routing keys off. This is also what lets `cucli scene get-hierarchy` reach a
    # C# action declared as `get_hierarchy`.
    if ($descriptor.name) { $Group = $descriptor.name }

    $declared = @(Get-GroupActions $descriptor)
    if ($declared.Count -gt 0) {
        if ([string]::IsNullOrWhiteSpace($Action)) {
            $Action = $declared[0]
        } else {
            $resolvedAction = Resolve-DeclaredName -Declared $declared -Typed $Action
            if (-not $resolvedAction) {
                return Write-CucliFailure -ErrorCode 'UNSUPPORTED_ACTION' `
                    -Message "Group '$Group' has no action '$Action'. Supported: $($declared -join ', ')." `
                    -Command $Group -Action $Action -Format $Format
            }

            $Action = $resolvedAction
        }
    }

    if ($descriptor.optionalPackage) {
        $installed = $false
        $manifest = Read-JsonFile (Join-Path $ProjectRoot 'Packages/manifest.json')
        if ($manifest -and $manifest.dependencies) {
            $installed = $null -ne $manifest.dependencies.PSObject.Properties[$descriptor.optionalPackage]
        }
        if (-not $installed) {
            return Write-CucliFailure -ErrorCode 'OPTIONAL_PACKAGE_MISSING' `
                -Message "'$Group' needs $($descriptor.optionalPackage), which this project does not list in Packages/manifest.json." `
                -Data ([ordered]@{ optionalPackage = $descriptor.optionalPackage }) `
                -Command $Group -Action $Action -Format $Format
        }
    }

    if (-not (Test-WorkerOnline $ProjectRoot)) {
        return Write-CucliFailure -ErrorCode 'WORKER_UNAVAILABLE' `
            -Message "No live Unity Editor worker for this project. Open the project (or run 'cucli open') and retry; run 'cucli status' for details." `
            -Command $Group -Action $Action -Format $Format
    }

    $control = Test-ControlAction -Index $index -ProjectRoot $ProjectRoot -Group $Group -Action $Action
    $jobId = Submit-CucliJob -ProjectRoot $ProjectRoot -Command $Group -Action $Action `
        -Payload $Arguments.Payload -TimeoutSeconds $Arguments.Timeout -Control $control

    if ($Arguments.Detach) {
        return Write-CucliResult -Success $true -Command $Group -Action $Action -JobId $jobId `
            -Data ([ordered]@{ jobId = $jobId; queue = if ($control) { 'control' } else { 'queued' }; detached = $true }) `
            -Message "Queued. Follow it with 'cucli job wait $jobId'." -Format $Format
    }

    $outcome = Wait-CucliJob -ProjectRoot $ProjectRoot -JobId $jobId -TimeoutSeconds $Arguments.Timeout
    $exitCode = Write-JobOutcome -Outcome $outcome -Command $Group -Action $Action -Format $Format -JobId $jobId
    return $exitCode
}

#endregion

#region ---------------------------------------------------------------- main

function Invoke-CucliMain {
    param([string[]]$Raw)

    # `cucli selftest ...` is forwarded verbatim, before any of our own option parsing.
    #
    # The self-test engine has its own parameters (-Tier, -Group, -Action, -KeepScratch, ...) and
    # forwarding the raw tokens lets PowerShell bind them normally. Re-serialising our camelCased
    # payload back into flags would lose the original spelling and silently change the engine's
    # behaviour, which for a verification tool is the one thing that must not happen.
    if ($Raw.Count -gt 0 -and $Raw[0].ToLowerInvariant() -eq 'selftest') {
        $engine = Join-Path $Script:ScriptRoot 'cucli-selftest.ps1'
        if (-not (Test-Path -LiteralPath $engine)) {
            [Console]::Error.WriteLine(
                '[cucli] the self-test engine (Tools/cucli-selftest.ps1) is missing from this installation.')
            exit $Script:ExitProject
        }

        $forwarded = @()
        if ($Raw.Count -gt 1) { $forwarded = $Raw[1..($Raw.Count - 1)] }
        & $engine @forwarded
        exit $LASTEXITCODE
    }

    $arguments = Split-CucliArguments $Raw
    $format = $arguments.Format
    if ([string]::IsNullOrWhiteSpace($format)) {
        $format = if ([Console]::IsOutputRedirected) { 'json' } else { 'human' }
    }
    $format = $format.ToLowerInvariant()

    $positional = @($arguments.Positional)
    $group = if ($positional.Count -gt 0) { $positional[0].ToLowerInvariant() } else { $null }
    # The positional action wins; `--action <name>` is the equivalent explicit spelling. If both are
    # given the positional one is authoritative, and the conflict is reported on stderr rather than
    # silently resolved, so a mistyped script is visible.
    $action = if ($positional.Count -gt 1) { $positional[1].ToLowerInvariant() } else { $null }
    if (-not [string]::IsNullOrWhiteSpace($arguments.Action)) {
        $explicitAction = ([string]$arguments.Action).Trim().ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($action)) {
            $action = $explicitAction
        } elseif ($action -ne $explicitAction) {
            [Console]::Error.WriteLine(
                "[warn] both a positional action '$action' and --action '$explicitAction' were given; using the positional one.")
        }
    }

    if ($arguments.Help -or [string]::IsNullOrWhiteSpace($group) -or $group -eq 'help') {
        return Invoke-HostHelp -Format $format
    }

    if ($group -eq 'version') {
        $root = Find-ProjectRoot -Hint $arguments.Project -StartPath (Get-Location).Path
        return Invoke-HostVersion -ProjectRoot $root -Format $format
    }

    $projectRoot = Find-ProjectRoot -Hint $arguments.Project -StartPath (Get-Location).Path

    switch ($group) {
        'commands' {
            if ($action -eq 'export' -and $null -eq $projectRoot) {
                return Write-CucliFailure -ErrorCode 'PROJECT_NOT_FOUND' `
                    -Message "No Unity project found. Run from inside a project or pass --project <path>." `
                    -Command 'commands' -Action 'export' -Format $format
            }
            if ($action -eq 'export') {
                return Invoke-JobDispatch -ProjectRoot $projectRoot -Group 'meta' -Action 'export-registry' `
                    -Arguments $arguments -Format $format
            }
            if ($null -eq $projectRoot) {
                # Catalog listing still works without a project by falling back to the static registry.
                $projectRoot = Get-PackageRoot
            }
            return Invoke-HostCommands -ProjectRoot $projectRoot -Arguments $arguments -Format $format
        }
    }

    if ($null -eq $projectRoot) {
        return Write-CucliFailure -ErrorCode 'PROJECT_NOT_FOUND' `
            -Message "No Unity project found. Run cucli from inside a project, pass --project <path>, or set CUCLI_PROJECT." `
            -Command $group -Action $action -Format $format
    }

    switch ($group) {
        'project'  { return Invoke-HostProject -ProjectRoot $projectRoot -Format $format }
        'status'   { return Invoke-HostStatus -ProjectRoot $projectRoot -Format $format }
        'doctor'   { return Invoke-HostDoctor -ProjectRoot $projectRoot -Arguments $arguments -Format $format }
        'logs'     { return Invoke-HostLogs -ProjectRoot $projectRoot -Arguments $arguments -Format $format }
        'job'      { return Invoke-HostJob -ProjectRoot $projectRoot -Arguments $arguments -Format $format }
        'open'     { return Invoke-HostOpen -ProjectRoot $projectRoot -Arguments $arguments -Format $format }
        'extend'   { return Invoke-HostExtend -ProjectRoot $projectRoot -Arguments $arguments -Format $format }
        'selftest' { return Invoke-HostSelftest -Raw $Raw -Format $format }
    }

    return Invoke-JobDispatch -ProjectRoot $projectRoot -Group $group -Action $action `
        -Arguments $arguments -Format $format
}

try {
    # The result payload flows out through the pipeline; the exit code travels separately so that
    # capturing the payload cannot swallow the status.
    Invoke-CucliMain -Raw $Arguments
    exit $Script:CucliExitCode
} catch {
    $message = $_.Exception.Message
    $code = 'USAGE_ERROR'
    if ($_.Exception -is [System.Management.Automation.RuntimeException]) { $code = 'USAGE_ERROR' }
    [Console]::Error.WriteLine("[cucli] $message")
    exit (Get-ExitCodeForError $code)
}

#endregion
