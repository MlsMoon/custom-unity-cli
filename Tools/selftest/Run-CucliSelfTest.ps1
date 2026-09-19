<#
.SYNOPSIS
    cucli self-test harness - exercises the CLI through its own public interface.

.DESCRIPTION
    Runs two tiers of tests against `cucli.ps1`, never against the Editor APIs directly, so the
    thing under test is exactly what a user or an agent would drive.

      CONTRACT tier (mechanical, covers every action in the compiled catalog)
        Invokes each group/action with no extra arguments and asserts the *protocol*, not the
        outcome: a well-formed cucli.result/v1 envelope, an exit code consistent with the envelope,
        a recognised error code, array-typed warnings/artifacts, and no unhandled exception.
        "Missing parameter" is a PASS here - the tier proves the whole surface is reachable,
        uniformly shaped and crash-free.

      BEHAVIOR tier (curated, asserts real results)
        Declarative cases from commands/selftest.json with real arguments and assertions against
        `data`, plus scratch round trips (create -> inspect -> modify -> delete) that only ever
        touch a dedicated self-test scratch folder.

    Actions that would change Editor or project state even with no arguments (undo, play, compile,
    clear, ...) are skipped in the contract tier and reported as such rather than being run.

.PARAMETER Tier
    all (default) | contract | behavior

.PARAMETER Group
    Only run cases whose group is in this list.

.PARAMETER Only
    Only run behavior cases whose id matches this wildcard.

.PARAMETER IncludeSkipped
    Also run the actions normally skipped as state-changing, still with no arguments.

.EXAMPLE
    ./Run-CucliSelfTest.ps1
    ./Run-CucliSelfTest.ps1 -Tier behavior -Group object,asset
    ./Run-CucliSelfTest.ps1 -Only 'object-*' -Format json
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'contract', 'behavior')]
    [string]$Tier = 'all',

    [string[]]$Group = @(),

    [string]$Only = '*',

    [int]$TimeoutSeconds = 120,

    [string]$Project,

    [string]$ReportPath,

    [switch]$IncludeSkipped,

    [switch]$IncludeMutating,

    [ValidateSet('human', 'json')]
    [string]$Format = 'human'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

#region ---------------------------------------------------------------- locations

$Script:HarnessRoot = $PSScriptRoot
$Script:PackageRoot = Split-Path -Parent (Split-Path -Parent $Script:HarnessRoot)
$Script:CucliPath = Join-Path $Script:PackageRoot 'Tools/cucli.ps1'
$Script:ManifestPath = Join-Path $Script:PackageRoot 'commands/selftest.json'

function Resolve-ProjectRoot {
    param([string]$Hint)
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Hint) { $candidates.Add($Hint) }
    if ($env:CUCLI_PROJECT) { $candidates.Add($env:CUCLI_PROJECT) }
    $candidates.Add((Get-Location).Path)
    $candidates.Add($Script:PackageRoot)

    foreach ($start in $candidates) {
        $current = $start
        for ($depth = 0; $depth -lt 12; $depth++) {
            if (-not $current) { break }
            if ((Test-Path (Join-Path $current 'Assets')) -and
                (Test-Path (Join-Path $current 'ProjectSettings/ProjectVersion.txt'))) {
                return $current
            }
            $next = Split-Path -Parent $current
            if ($next -eq $current) { break }
            $current = $next
        }
    }
    return $null
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $raw = [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    } catch { return $null }
}

$Script:ProjectRoot = Resolve-ProjectRoot -Hint $Project
if (-not $Script:ProjectRoot) { throw "Could not locate a Unity project. Pass -Project <path>." }
if (-not (Test-Path -LiteralPath $Script:CucliPath)) { throw "cucli.ps1 not found at $Script:CucliPath" }

$Script:RuntimeRoot = Join-Path $Script:ProjectRoot 'Library/CustomUnityCli'
$Script:CatalogPath = Join-Path $Script:RuntimeRoot 'worker/commands.json'
$Script:HeartbeatPath = Join-Path $Script:RuntimeRoot 'worker/heartbeat.json'

if (-not $ReportPath) {
    $ReportPath = Join-Path $Script:RuntimeRoot 'selftest/report.json'
}

#endregion

#region ---------------------------------------------------------------- json paths

function Get-JsonPath {
    <# Walks a dotted path such as data.state.isPlaying or data.items[0].name. #>
    param($Value, [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $Value }
    $current = $Value
    foreach ($segment in ($Path -split '\.')) {
        if ($null -eq $current) { return $null }
        $name = $segment
        $indexes = @()
        $open = $segment.IndexOf('[')
        if ($open -ge 0) {
            $name = $segment.Substring(0, $open)
            $rest = $segment.Substring($open)
            foreach ($m in [regex]::Matches($rest, '\[(\d+)\]')) { $indexes += [int]$m.Groups[1].Value }
        }

        if ($name.Length -gt 0) {
            $prop = $current.PSObject.Properties[$name]
            if ($null -eq $prop) { return $null }
            $current = $prop.Value
        }

        foreach ($index in $indexes) {
            if ($null -eq $current) { return $null }
            if ($current -is [string]) { return $null }
            $array = @($current)
            if ($index -ge $array.Count) { return $null }
            $current = $array[$index]
        }
    }
    return $current
}

#endregion

#region ---------------------------------------------------------------- invocation

$Script:HostExe = (Get-Process -Id $PID).Path
if (-not $Script:HostExe) { $Script:HostExe = 'pwsh' }

function Invoke-Cucli {
    <#
        Runs cucli.ps1 in a SEPARATE process.

        This is mandatory, not a style choice: cucli.ps1 ends with `exit <code>`, which would
        terminate the harness itself if the script were dot-sourced or invoked in-process.
    #>
    param([string[]]$CliArgs, [int]$Timeout)

    $stdout = Join-Path $env:TEMP ("cucli-selftest-" + [guid]::NewGuid().ToString('N') + ".out")
    $stderr = "$stdout.err"
    $argv = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $Script:CucliPath) + $CliArgs

    $exitCode = -1
    $started = [datetime]::UtcNow
    try {
        $process = Start-Process -FilePath $Script:HostExe -ArgumentList $argv -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if (-not $process.WaitForExit($Timeout * 1000)) {
            try { $process.Kill() } catch { }
            $exitCode = -2
        } else {
            $exitCode = $process.ExitCode
        }
    } catch {
        $exitCode = -3
    }

    $outText = ''
    $errText = ''
    try { if (Test-Path $stdout) { $outText = [System.IO.File]::ReadAllText($stdout) } } catch { }
    try { if (Test-Path $stderr) { $errText = [System.IO.File]::ReadAllText($stderr) } } catch { }
    foreach ($f in @($stdout, $stderr)) { try { Remove-Item $f -Force -ErrorAction SilentlyContinue } catch { } }

    $envelope = $null
    $trimmed = $outText.Trim()
    if ($trimmed.StartsWith('{')) {
        try { $envelope = $trimmed | ConvertFrom-Json } catch { $envelope = $null }
    }
    if ($null -eq $envelope) {
        # ndjson: take the last parseable frame.
        foreach ($line in ($outText -split "`r?`n" | Where-Object { $_.Trim().StartsWith('{') })) {
            try {
                $candidate = $line.Trim() | ConvertFrom-Json
                if ($candidate.PSObject.Properties['data'] -and $candidate.type) { $envelope = $candidate.data }
                else { $envelope = $candidate }
            } catch { }
        }
    }

    return [pscustomobject]@{
        ExitCode    = $exitCode
        Envelope    = $envelope
        StdOut      = $outText
        StdErr      = $errText
        DurationMs  = [int]([datetime]::UtcNow - $started).TotalMilliseconds
        TimedOut    = ($exitCode -eq -2)
        LaunchError = ($exitCode -eq -3)
    }
}

#endregion

#region ---------------------------------------------------------------- assertions

$Script:KnownErrorCodes = @(
    'USAGE_ERROR', 'PROJECT_NOT_FOUND', 'CONFIRMATION_REQUIRED', 'OPTIONAL_PACKAGE_MISSING',
    'MISSING_PARAMETER', 'INVALID_PARAMETER', 'UNSUPPORTED_ACTION', 'EDITOR_COMMAND_NOT_FOUND',
    'EDITOR_COMMAND_AMBIGUOUS', 'EDITOR_COMMAND_DUPLICATE', 'EDITOR_COMMAND_FAILED',
    'EDITOR_COMMAND_EXCEPTION', 'MEMBER_NOT_FOUND', 'TYPE_NOT_FOUND', 'ASSET_NOT_FOUND',
    'ASSET_EXISTS', 'TARGET_NOT_FOUND', 'TARGET_AMBIGUOUS', 'WRONG_MODE', 'CODE_COMPILE_ERROR',
    'SCRIPT_COMPILATION_FAILED', 'COMPILATION_IN_PROGRESS', 'WORKER_UNAVAILABLE', 'WORKER_BUSY',
    'WORKER_RELOADED', 'TIMEOUT', 'CANCELLED', 'JOB_INVALID', 'JOB_EXPIRED',
    'CONTROL_COMMAND_NOT_ALLOWED', 'BOOTSTRAP_FAILED'
)

$Script:ExpectedExitFor = @{
    'USAGE_ERROR'              = 2
    'MISSING_PARAMETER'        = 8
    'INVALID_PARAMETER'        = 8
    'UNSUPPORTED_ACTION'       = 8
    'EDITOR_COMMAND_NOT_FOUND' = 8
    'MEMBER_NOT_FOUND'         = 8
    'TYPE_NOT_FOUND'           = 8
    'ASSET_NOT_FOUND'          = 8
    'TARGET_NOT_FOUND'         = 8
    'TARGET_AMBIGUOUS'         = 8
    'PROJECT_NOT_FOUND'        = 4
    'CONFIRMATION_REQUIRED'    = 4
    'OPTIONAL_PACKAGE_MISSING' = 4
    'ASSET_EXISTS'             = 4
    'CANCELLED'                = 130
    'WORKER_UNAVAILABLE'       = 7
    'WORKER_BUSY'              = 7
    'WORKER_RELOADED'          = 7
    'COMPILATION_IN_PROGRESS'  = 7
}

function Test-Contract {
    <# Protocol-level checks that must hold for every single invocation. #>
    param($Run, [string]$Group, [string]$Action, [string[]]$Assertions)

    $problems = New-Object System.Collections.Generic.List[string]

    if ($Run.LaunchError) { $problems.Add('the CLI process could not be launched'); return $problems }
    if ($Run.TimedOut) { $problems.Add("timed out after $TimeoutSeconds s"); return $problems }

    $envelope = $Run.Envelope
    if ($null -eq $envelope) {
        $head = ($Run.StdOut + ' ' + $Run.StdErr).Trim()
        if ($head.Length -gt 200) { $head = $head.Substring(0, 200) }
        $problems.Add("no parseable cucli.result/v1 envelope on stdout (exit=$($Run.ExitCode)): $head")
        return $problems
    }

    if ($envelope.schemaVersion -ne 'cucli.result/v1') {
        $problems.Add("schemaVersion is '$($envelope.schemaVersion)', expected 'cucli.result/v1'")
    }
    foreach ($field in @('success', 'command', 'action', 'errors', 'warnings', 'artifacts', 'exitCode')) {
        if ($null -eq $envelope.PSObject.Properties[$field]) { $problems.Add("envelope is missing '$field'") }
    }
    if ($problems.Count -gt 0) { return $problems }

    if ($envelope.command -ne $Group) {
        $problems.Add("envelope.command is '$($envelope.command)', expected '$Group'")
    }

    # The process exit code and the envelope must agree; callers branch on both.
    if ([int]$envelope.exitCode -ne $Run.ExitCode) {
        $problems.Add("process exit code $($Run.ExitCode) disagrees with envelope exitCode $($envelope.exitCode)")
    }

    # Warnings and artifacts must always be arrays. A JSON null here means a caller doing
    # `foreach ($w in $result.warnings)` silently iterates once over nothing.
    foreach ($field in @('warnings', 'artifacts', 'errors')) {
        $value = $envelope.$field
        if ($null -eq $value) { $problems.Add("'$field' is null, expected an array") ; continue }
        if ($value -isnot [System.Array] -and $value -isnot [System.Collections.IList]) {
            $problems.Add("'$field' is $($value.GetType().Name), expected an array")
            continue
        }
        foreach ($item in $value) {
            if ($null -eq $item) { $problems.Add("'$field' contains a null element"); break }
        }
    }

    if ($envelope.success) {
        if ($Run.ExitCode -ne 0) { $problems.Add("success is true but the exit code is $($Run.ExitCode)") }
        if (@($envelope.errors).Count -gt 0) { $problems.Add('success is true but errors[] is not empty') }
    } else {
        $codes = @($envelope.errors | ForEach-Object { $_.code })
        if ($codes.Count -eq 0) {
            $problems.Add('success is false but errors[] is empty')
        } else {
            foreach ($code in $codes) {
                if ([string]::IsNullOrWhiteSpace($code)) { $problems.Add('an error has no code'); continue }
                if ($Script:KnownErrorCodes -notcontains $code) {
                    $problems.Add("unknown error code '$code'")
                }
            }
            $primary = $codes[0]
            if ($Script:ExpectedExitFor.ContainsKey($primary)) {
                $expected = $Script:ExpectedExitFor[$primary]
                if ([int]$envelope.exitCode -ne $expected) {
                    $problems.Add("error '$primary' should exit $expected, got $($envelope.exitCode)")
                }
            }
        }

        foreach ($err in @($envelope.errors)) {
            if ($err.message -and $err.message -match 'at [\w\.]+\(.*\)\s+in .*:line') {
                $problems.Add("error '$($err.code)' leaks a .NET stack trace instead of a message")
            }
            if ($err.PSObject.Properties['retryable'] -and $null -eq $err.retryable) {
                $problems.Add("error '$($err.code)' has a null 'retryable' flag")
            }
        }
    }

    # Anything the caller cannot distinguish from a crash is a contract failure.
    if ($envelope.success -eq $false) {
        $codes = @($envelope.errors | ForEach-Object { $_.code })
        if ($codes -contains 'BOOTSTRAP_FAILED') {
            $problems.Add('the CLI bootstrap failed')
        }
    }

    # Optional per-case assertions from the manifest.
    foreach ($assertion in @($Assertions)) {
        $value = Get-JsonPath -Value $envelope -Path $assertion.path
        if ($assertion.PSObject.Properties['exists']) {
            $want = [bool]$assertion.exists
            $have = ($null -ne $value)
            if ($want -ne $have) { $problems.Add("assert $($assertion.path): exists expected $want, got $have") }
        }
        if ($null -ne $assertion.PSObject.Properties['equals']) {
            $expected = $assertion.equals
            if ("$value" -ne "$expected") {
                $problems.Add("assert $($assertion.path): expected '$expected', got '$value'")
            }
        }
        if ($assertion.PSObject.Properties['contains'] -and "$value" -notlike "*$($assertion.contains)*") {
            $problems.Add("assert $($assertion.path): '$value' does not contain '$($assertion.contains)'")
        }
        if ($assertion.PSObject.Properties['min']) {
            $number = 0.0
            if (-not [double]::TryParse("$value", [ref]$number) -or $number -lt [double]$assertion.min) {
                $problems.Add("assert $($assertion.path): '$value' is below the minimum $($assertion.min)")
            }
        }
        if ($assertion.PSObject.Properties['countAtLeast']) {
            $count = @($value).Count
            if ($count -lt [int]$assertion.countAtLeast) {
                $problems.Add("assert $($assertion.path): has $count items, expected at least $($assertion.countAtLeast)")
            }
        }
    }

    return $problems
}

#endregion

#region ---------------------------------------------------------------- case building

$Script:Catalog = Read-JsonFile $Script:CatalogPath
if (-not $Script:Catalog -or -not $Script:Catalog.commands) {
    throw "No runtime catalog at $Script:CatalogPath. Open Unity so the worker publishes it, then retry."
}

$Script:Manifest = Read-JsonFile $Script:ManifestPath
if (-not $Script:Manifest) { throw "No self-test manifest at $Script:ManifestPath" }

$Script:DangerousActions = @()
if ($Script:Manifest.dangerousActions) { $Script:DangerousActions = @($Script:Manifest.dangerousActions) }

$Script:SkipGroups = @()
if ($Script:Manifest.skipContractGroups) { $Script:SkipGroups = @($Script:Manifest.skipContractGroups) }

function Get-CatalogGroups {
    $names = @($Script:Catalog.commands | ForEach-Object { $_.name })
    if ($Group.Count -gt 0) { $names = $names | Where-Object { $Group -contains $_ } }
    return $names
}

function New-ContractCases {
    $cases = New-Object System.Collections.Generic.List[object]
    foreach ($command in ($Script:Catalog.commands | Sort-Object name)) {
        if ($Group.Count -gt 0 -and ($Group -notcontains $command.name)) { continue }
        if ($SkipGroups -contains $command.name) { continue }

        foreach ($action in @($command.actions)) {
            $key = "$($command.name)/$($action.name)"
            $dangerous = ($Script:DangerousActions -contains $key)
            $cases.Add([pscustomobject]@{
                Tier      = 'contract'
                Id        = "contract:$key"
                Group     = $command.name
                Action    = $action.name
                CliArgs   = @($command.name, $action.name, '--format', 'json')
                Assertions = @()
                Skip      = ($dangerous -and -not $IncludeSkipped)
                SkipReason = if ($dangerous) { 'state-changing with no arguments' } else { $null }
            })
        }
    }
    return $cases
}

function New-BehaviorCases {
    $cases = New-Object System.Collections.Generic.List[object]
    foreach ($case in @($Script:Manifest.cases)) {
        if ($Group.Count -gt 0 -and ($Group -notcontains $case.group)) { continue }
        if ($Only -ne '*' -and $case.id -notlike $Only) { continue }

        $cli = New-Object System.Collections.Generic.List[string]
        $cli.Add($case.group)
        if ($case.action) { $cli.Add($case.action) }
        if ($case.args) { foreach ($a in @($case.args)) { $cli.Add([string]$a) } }
        $cli.Add('--format')
        $cli.Add('json')

        # Cases that write into the project are opt-in. The contract tier already proves the action
        # is reachable; running the mutating ones is a deliberate choice because they leave scratch
        # assets behind, trigger reimports and can force a script recompile.
        $mutating = ($case.PSObject.Properties['mutating'] -and [bool]$case.mutating)
        $cases.Add([pscustomobject]@{
            Tier       = 'behavior'
            Id         = $case.id
            Group      = $case.group
            Action     = $case.action
            CliArgs    = $cli.ToArray()
            Assertions = @($case.assert)
            Skip       = ($mutating -and -not $IncludeMutating)
            SkipReason = if ($mutating) { 'mutating; pass -IncludeMutating to run' } else { $null }
        })
    }
    return $cases
}

#endregion

#region ---------------------------------------------------------------- run

$cases = New-Object System.Collections.Generic.List[object]
if ($Tier -eq 'all' -or $Tier -eq 'contract') { foreach ($c in (New-ContractCases)) { $cases.Add($c) } }
if ($Tier -eq 'all' -or $Tier -eq 'behavior') { foreach ($c in (New-BehaviorCases)) { $cases.Add($c) } }

if ($cases.Count -eq 0) { throw "No cases selected. Check -Tier/-Group/-Only." }

$results = New-Object System.Collections.Generic.List[object]
$index = 0
foreach ($case in $cases) {
    $index++
    if ($case.Skip) {
        $results.Add([pscustomobject]@{
            id = $case.Id; tier = $case.Tier; group = $case.Group; action = $case.Action
            outcome = 'skipped'; reason = $case.SkipReason; durationMs = 0; problems = @()
            args = ($case.CliArgs -join ' ')
        })
        if ($Format -eq 'human') { Write-Host ("[{0,3}/{1}] SKIP  {2}  ({3})" -f $index, $cases.Count, $case.Id, $case.SkipReason) -ForegroundColor DarkGray }
        continue
    }

    $run = Invoke-Cucli -CliArgs $case.CliArgs -Timeout $TimeoutSeconds
    $problems = Test-Contract -Run $run -Group $case.Group -Action $case.Action -Assertions $case.Assertions
    $outcome = if ($problems.Count -eq 0) { 'pass' } else { 'fail' }

    $results.Add([pscustomobject]@{
        id = $case.Id; tier = $case.Tier; group = $case.Group; action = $case.Action
        outcome = $outcome; reason = $null; durationMs = $run.DurationMs; problems = @($problems)
        args = ($case.CliArgs -join ' ')
        errorCode = if ($run.Envelope -and @($run.Envelope.errors).Count -gt 0) { @($run.Envelope.errors)[0].code } else { $null }
        message = if ($run.Envelope) { @($run.Envelope.errors | ForEach-Object { $_.message }) -join ' | ' } else { $null }
    })

    if ($Format -eq 'human') {
        if ($outcome -eq 'pass') {
            Write-Host ("[{0,3}/{1}] ok    {2}" -f $index, $cases.Count, $case.Id) -ForegroundColor DarkGreen
        } else {
            Write-Host ("[{0,3}/{1}] FAIL  {2}" -f $index, $cases.Count, $case.Id) -ForegroundColor Red
            foreach ($p in $problems) { Write-Host ("            - $p") -ForegroundColor Red }
        }
    }
}

#endregion

#region ---------------------------------------------------------------- coverage

$coveredActions = New-Object System.Collections.Generic.HashSet[string]
foreach ($case in @($Script:Manifest.cases)) {
    if ($case.action) { [void]$coveredActions.Add("$($case.group)/$($case.action)") }
}

$coverage = New-Object System.Collections.Generic.List[object]
$uncoveredTotal = 0
foreach ($command in ($Script:Catalog.commands | Sort-Object name)) {
    if ($Group.Count -gt 0 -and ($Group -notcontains $command.name)) { continue }
    $total = @($command.actions).Count
    $covered = 0
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($action in @($command.actions)) {
        if ($coveredActions.Contains("$($command.name)/$($action.name)")) { $covered++ }
        else { $missing.Add($action.name) }
    }
    $uncoveredTotal += $missing.Count
    $coverage.Add([pscustomobject]@{
        group = $command.name
        actions = $total
        withBehaviorCase = $covered
        withoutBehaviorCase = $missing.Count
        missing = $missing.ToArray()
        optionalPackage = $command.optionalPackage
    })
}

# Which capability groups the original scope promised but the build does not have yet.
$expectedGroups = @()
if ($Script:Manifest.expectedGroups) { $expectedGroups = @($Script:Manifest.expectedGroups) }
$presentGroups = @($Script:Catalog.commands | ForEach-Object { $_.name })
$missingGroups = @($expectedGroups | Where-Object { $presentGroups -notcontains $_ })

#endregion

#region ---------------------------------------------------------------- report

$failed = @($results | Where-Object { $_.outcome -eq 'fail' })
$passed = @($results | Where-Object { $_.outcome -eq 'pass' })
$skipped = @($results | Where-Object { $_.outcome -eq 'skipped' })

$summary = [ordered]@{
    schemaVersion  = 'cucli.selftest-report/v1'
    generatedAtUtc = [datetime]::UtcNow.ToString('O')
    projectRoot    = $Script:ProjectRoot
    catalogHash    = $Script:Catalog.catalogHash
    unityVersion   = $Script:Catalog.unityVersion
    tier           = $Tier
    totals = [ordered]@{
        cases   = $results.Count
        passed  = $passed.Count
        failed  = $failed.Count
        skipped = $skipped.Count
    }
    catalog = [ordered]@{
        groups           = @($Script:Catalog.commands).Count
        actions          = (@($Script:Catalog.commands | ForEach-Object { @($_.actions).Count }) | Measure-Object -Sum).Sum
        diagnostics      = @($Script:Catalog.diagnostics).Count
        likelyGroups     = $presentGroups.Count
        expectedGroups   = $expectedGroups.Count
        missingGroups    = $missingGroups
        actionsWithoutBehaviorCase = $uncoveredTotal
    }
    coverage = $coverage.ToArray()
    failures = $failed
    skippedCases = $skipped
}

$reportDirectory = Split-Path -Parent $ReportPath
if ($reportDirectory -and -not (Test-Path $reportDirectory)) { New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null }
[System.IO.File]::WriteAllText($ReportPath, ($summary | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

if ($Format -eq 'json') {
    [Console]::Out.WriteLine(($summary | ConvertTo-Json -Depth 12))
} else {
    Write-Host ''
    Write-Host '=== coverage (behavior cases per group) ==='
    foreach ($row in $coverage) {
        $flag = if ($row.withoutBehaviorCase -eq 0) { ' ' } else { '!' }
        Write-Host (" {0} {1,-11} {2,3} actions, {3,3} with a behavior case, {4,3} without" -f `
            $flag, $row.group, $row.actions, $row.withBehaviorCase, $row.withoutBehaviorCase)
    }

    if ($missingGroups.Count -gt 0) {
        Write-Host ''
        Write-Host '=== capability groups promised by the scope but NOT implemented ===' -ForegroundColor Yellow
        foreach ($name in $missingGroups) { Write-Host "  - $name" -ForegroundColor Yellow }
    }

    Write-Host ''
    Write-Host ("=== result: {0} passed, {1} failed, {2} skipped ({3} cases) ===" -f `
        $passed.Count, $failed.Count, $skipped.Count, $results.Count) `
        -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
    Write-Host "report: $ReportPath"
}

if ($failed.Count -gt 0) { exit 1 }
exit 0

#endregion
