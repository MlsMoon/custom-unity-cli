<#
.SYNOPSIS
    Self-test driver for cucli: exercises the CLI through the CLI itself.

.DESCRIPTION
    This script never reaches into the package internals. It reads the command surface from the live
    worker catalog and then talks to the Editor exclusively through Tools/cucli.ps1, so a green run
    proves the whole chain: PowerShell parser -> job file -> Editor worker -> command -> result file
    -> envelope -> exit code. Nothing about the implementation is consulted, which is the point -
    the CLI is tested as a black box, by using it.

    Three tiers, and every declared action lands in exactly one of them:

      Tier A  Contract probe. Invokes the READ-ONLY actions with no arguments. A correctly wired
              command answers with a real payload or a validation error; anything else (unknown
              command, dispatch mismatch, unhandled exception, worker loss) is a defect. This is
              what catches registry drift, schema gaps and mode-gate bugs across the surface.

      Tier B  Functional round trips. Real, sandboxed, end-to-end work with real arguments: build a
              GameObject tree and mutate it, create and modify real assets, assemble a prefab, page
              a hierarchy, round-trip a project tag, and confirm the --force guard rails actually
              refuse. Everything it creates is deleted on the way out, including on failure.

      Tier C  Documented skips. Actions that would build a player, bake lighting, mutate
              Packages/manifest.json, run test suites or otherwise change machine state. Each skip
              carries a reason and is reported, never silently ignored.

    The allow-list in Tier A is deliberate. A no-argument call is only safe for an action that
    cannot write, so "when in doubt, skip and say why" is the rule rather than "probe and hope".
    Actions that write are covered by Tier B with real sandboxed arguments instead.

.PARAMETER Group
    Restrict the run to these command groups. Empty means every group.

.PARAMETER Action
    Restrict the run to specific "group/action" keys, e.g. object/create, asset/search.

.PARAMETER Tier
    Which tiers to run. Default ABC.

.PARAMETER IncludeHeavy
    Also run the skipped-but-slow actions (compilation, package resolve, test run). Never enables a
    real player build, light baking or a manifest write.

.PARAMETER Report
    Markdown report path. Defaults to Library/CustomUnityCli/selftest-report.md.

.PARAMETER TimeoutSec
    Per-command timeout. Default 120.

.PARAMETER KeepSandbox
    Leave Assets/CucliSelfTest behind for inspection instead of deleting it.

.EXAMPLE
    ./cucli-selftest.ps1
    ./cucli-selftest.ps1 -Group object,asset -Tier AB
    ./cucli-selftest.ps1 -Action object/create,asset/search -Tier B
#>

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

# ---------------------------------------------------------------- arguments

$Script:OptionGroup = @()
$Script:OptionAction = @()
$Script:OptionTier = 'ABC'
$Script:OptionIncludeHeavy = $false
$Script:OptionReport = $null
$Script:OptionTimeoutSec = 120
$Script:OptionKeepSandbox = $false
$Script:ShowHelp = $false

$raw = @($args)
for ($i = 0; $i -lt $raw.Count; $i++) {
    $token = $raw[$i]
    $next = if ($i + 1 -lt $raw.Count) { $raw[$i + 1] } else { $null }
    switch -Regex ($token) {
        '^-+Group$'           { $Script:OptionGroup = @($next -split ','); $i++ }
        '^-+Action$'          { $Script:OptionAction = @($next -split ','); $i++ }
        '^-+Tier$'            { $Script:OptionTier = $next; $i++ }
        '^-+Report$'          { $Script:OptionReport = $next; $i++ }
        '^-+TimeoutSec$'      { $Script:OptionTimeoutSec = [int]$next; $i++ }
        '^-+IncludeHeavy$'    { $Script:OptionIncludeHeavy = $true }
        '^-+KeepSandbox$'     { $Script:OptionKeepSandbox = $true }
        '^-+(h|help|\?)$'     { $Script:ShowHelp = $true }
        '^-+Group=(.*)$'      { $Script:OptionGroup = @($Matches[1] -split ',') }
        '^-+Action=(.*)$'     { $Script:OptionAction = @($Matches[1] -split ',') }
        '^-+Tier=(.*)$'       { $Script:OptionTier = $Matches[1] }
        '^-+Report=(.*)$'     { $Script:OptionReport = $Matches[1] }
        '^-+TimeoutSec=(.*)$' { $Script:OptionTimeoutSec = [int]$Matches[1] }
    }
}
$Script:OptionGroup = @($Script:OptionGroup | Where-Object { $_ })
$Script:OptionAction = @($Script:OptionAction | Where-Object { $_ })
$Script:OptionTier = $Script:OptionTier.ToUpperInvariant()

if ($Script:ShowHelp) { Get-Help $PSCommandPath -Detailed; exit 0 }

$runA = $Script:OptionTier.Contains('A')
$runB = $Script:OptionTier.Contains('B')
$runC = $Script:OptionTier.Contains('C')

# ---------------------------------------------------------------- plumbing

$Script:SelfDir = $PSScriptRoot
$Script:Cli = Join-Path $Script:SelfDir 'cucli.ps1'
if (-not (Test-Path -LiteralPath $Script:Cli)) {
    Write-Host "[selftest] cucli.ps1 not found next to this script at $($Script:Cli)" -ForegroundColor Red
    exit 2
}

# ArrayList, not List[object]: under Windows PowerShell 5.1 `New-Object
# System.Collections.Generic.List[object]` binds as List[PSObject], and enumerating it later throws
# "Argument types do not match" - which destroyed the report after all the work was already done.
$Script:Results = New-Object System.Collections.ArrayList
$Script:Sandbox = 'Assets/CucliSelfTest'
$Script:ProjectRoot = $null
$Script:CatalogMap = @{}
$Script:Bootstrapped = $false

function Add-Result {
    param(
        [string]$Tier, [string]$Key, [string]$Status,
        [string]$ErrorCode = '', [long]$DurationMs = 0, [string]$Detail = '', [string[]]$Artifacts = @()
    )
    # pscustomobject, not [ordered]: OrderedDictionary instances raise "Argument types do not match"
    # once they are enumerated inside a strongly-typed collection, which silently killed the whole
    # report and then the summary. A plain property bag is also what Where-Object/Sort-Object expect.
    # [void] because ArrayList.Add returns the new index, which would otherwise leak into the output.
    [void]$Script:Results.Add([pscustomobject]@{
        tier = $Tier; key = $Key; status = $Status; errorCode = $ErrorCode
        durationMs = $DurationMs; detail = $Detail; artifacts = @($Artifacts)
    })
}

function Invoke-Cucli {
    <#
        Runs one cucli invocation and normalises the envelope.

        Three real CLI behaviours are handled here rather than assumed away: output goes to the
        pipeline (so `& cucli ... | ConvertFrom-Json` works), the process exit code is authoritative
        for pass/fail, and blank entries can appear in warnings/artifacts when a caller forwards an
        absent optional field - they are filtered so they are not mistaken for findings.
    #>
    param([string[]]$CliArgs, [int]$TimeoutSec = 0)

    $effectiveTimeout = if ($TimeoutSec -gt 0) { $TimeoutSec } else { $Script:OptionTimeoutSec }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $text = $null
    $exit = 0
    try {
        $text = (& $Script:Cli @CliArgs '--format' 'json' '--timeout' "$effectiveTimeout" 2>&1 | Out-String)
        $exit = $LASTEXITCODE
    } catch {
        $text = "$text`n$($_.Exception.Message)"
        $exit = 1
    } finally {
        $sw.Stop()
    }

    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        $candidate = $text.Trim()
        $start = $candidate.IndexOf('{')
        if ($start -ge 0) {
            try { $json = $candidate.Substring($start) | ConvertFrom-Json } catch { $json = $null }
        }
    }

    $errorCode = ''
    if ($json -and $json.errors -and @($json.errors).Count -gt 0) { $errorCode = [string]@($json.errors)[0].code }
    $warnings = @()
    if ($json -and $json.warnings) { $warnings = @($json.warnings | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }
    $artifacts = @()
    if ($json -and $json.artifacts) { $artifacts = @($json.artifacts | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }

    return [ordered]@{
        ExitCode = $exit; Ok = ($exit -eq 0); Json = $json
        Success = ($json -and $json.success); ErrorCode = $errorCode
        Message = if ($json) { [string]$json.message } else { $text.Trim() }
        Data = if ($json) { $json.data } else { $null }
        Warnings = $warnings; Artifacts = $artifacts
        DurationMs = $sw.ElapsedMilliseconds; Raw = $text
    }
}

function Find-ProjectRoot {
    <#
        Walks up until a Unity project root appears. The script cannot assume it lives under Assets/:
        installed as a UPM package it sits in Library/PackageCache, so the project's own marker files
        are the only portable anchor.
    #>
    param([string]$Start)
    $current = $Start
    for ($depth = 0; $depth -lt 12; $depth++) {
        if ([string]::IsNullOrWhiteSpace($current)) { break }
        if ((Test-Path -LiteralPath (Join-Path $current 'ProjectSettings/ProjectVersion.txt')) -and
            (Test-Path -LiteralPath (Join-Path $current 'Assets'))) { return $current }
        $next = Split-Path -Parent $current
        if ($next -eq $current) { break }
        $current = $next
    }
    return $null
}

function Get-Catalog {
    if (-not $Script:ProjectRoot) { return $null }
    $path = Join-Path $Script:ProjectRoot 'Library/CustomUnityCli/worker/commands.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

function Test-ActionExists {
    param([string]$Group, [string]$Action)
    if (-not $Script:CatalogMap.ContainsKey($Group)) { return $false }
    return $Script:CatalogMap[$Group].ContainsKey($Action)
}

function Has-Group {
    param([string]$Name)
    if ($Script:OptionGroup.Count -gt 0 -and ($Script:OptionGroup -notcontains $Name)) { return $false }
    return $Script:CatalogMap.ContainsKey($Name)
}

# ---------------------------------------------------------------- classification

# Answers that mean "the command was reached, dispatched and validated its input". Any of these is a
# pass for a no-argument probe: Tier A tests wiring, not semantics.
$Script:ContractAnswers = @(
    'MISSING_PARAMETER', 'INVALID_PARAMETER', 'USAGE_ERROR', 'CONFIRMATION_REQUIRED', 'ASSET_EXISTS',
    'TARGET_NOT_FOUND', 'TARGET_AMBIGUOUS', 'ASSET_NOT_FOUND', 'MEMBER_NOT_FOUND', 'TYPE_NOT_FOUND',
    'WRONG_MODE', 'OPTIONAL_PACKAGE_MISSING', 'COMPILATION_IN_PROGRESS', 'WORKER_BUSY',
    'UNSUPPORTED_ACTION'
)

# Answers that always mean a defect, whatever the arguments were.
$Script:FatalAnswers = @(
    'EDITOR_COMMAND_EXCEPTION', 'EDITOR_COMMAND_NOT_FOUND', 'EDITOR_COMMAND_DUPLICATE',
    'WORKER_UNAVAILABLE', 'JOB_INVALID', 'JOB_EXPIRED'
)

# Actions that cannot change machine state, so a no-argument probe is safe. Anything not listed here
# is skipped in Tier A and covered by Tier B or reported as an explicit skip.
$Script:ReadOnlyProbe = @(
    'editor/state', 'editor/ping', 'editor/tool-list', 'editor/tag-list', 'editor/layer-list',
    'editor/selection-get',
    'refresh/status',
    'console/get',
    'scene/get_hierarchy', 'scene/get_active', 'scene/get_build_settings', 'scene/get_loaded_scenes',
    'scene/validate',
    'meta/catalog', 'meta/schema', 'meta/doctor', 'meta/extensions', 'meta/verify', 'meta/environment',
    'asset/search', 'asset/get_info', 'asset/get_components',
    'shader/read',
    'material/ping', 'material/get_material_info',
    'script/read', 'script/validate', 'script/get_sha',
    'object/find', 'object/list', 'object/get-property', 'object/inspect',
    'prefab/get_info', 'prefab/get_hierarchy',
    'package/status', 'package/list_packages', 'package/search_packages', 'package/get_package_info',
    'package/list_registries', 'package/ping',
    'build/status', 'build/settings', 'build/scenes', 'build/profiles', 'build/platform',
    'graphics/ping', 'graphics/volume_get_info', 'graphics/volume_list_effects', 'graphics/bake_status',
    'graphics/bake_get_settings', 'graphics/stats_get', 'graphics/stats_list_counters',
    'graphics/stats_get_memory', 'graphics/pipeline_get_info', 'graphics/pipeline_get_settings',
    'graphics/feature_list', 'graphics/skybox_get',
    'camera/ping', 'camera/list_cameras', 'camera/get_brain_status',
    'test/status', 'test/result', 'test/list',
    'ui/ping', 'ui/read', 'ui/get_visual_tree', 'ui/list',
    'animation/clip_get_info', 'animation/controller_get_info', 'animation/animator_get_info',
    'animation/animator_get_parameter'
)

# Actions whose no-argument behaviour would be slow or state-changing even though they are nominally
# safe to call. Only reached with -IncludeHeavy.
$Script:HeavyList = @(
    'refresh/compile', 'package/resolve_packages', 'test/run', 'ui/render_ui', 'scene/screenshot',
    'camera/screenshot', 'camera/screenshot_multiview'
)

# Explicit, human-readable reasons for the noteworthy skips. Actions not listed here still get a
# generic "mutating" reason from Tier C, so nothing is left unexplained.
$Script:SkipNotes = @{
    'editor/play'                    = 'enters Play Mode'
    'editor/stop'                    = 'leaves Play Mode'
    'editor/pause'                   = 'toggles pause'
    'editor/step'                    = 'advances a frame'
    'editor/undo'                    = 'would undo the user''s last edit'
    'editor/redo'                    = 'would replay an edit'
    'editor/menu'                    = 'executes an arbitrary menu item by path'
    'refresh/compile'                = 'triggers a full script recompile'
    'scene/save'                     = 'would save the open scene to disk'
    'scene/create'                   = 'changes the open scene'
    'scene/load'                     = 'changes the open scene'
    'scene/close_scene'              = 'closes an open scene'
    'scene/set_active_scene'         = 'changes the active scene'
    'scene/move_to_scene'            = 'moves objects between scenes'
    'build/build'                    = 'performs a real player build'
    'build/batch'                    = 'performs real player builds'
    'build/cancel'                   = 'cancels a build job'
    'build/platform'                 = 'switches the active build target'
    'package/add_package'            = 'mutates Packages/manifest.json'
    'package/remove_package'         = 'mutates Packages/manifest.json'
    'package/add_registry'           = 'mutates Packages/manifest.json'
    'package/remove_registry'        = 'mutates Packages/manifest.json'
    'package/embed_package'          = 'moves a package into the project'
    'package/resolve_packages'       = 'rewrites packages-lock.json'
    'graphics/bake_start'            = 'starts a lightmap bake'
    'graphics/bake_reflection_probe' = 'starts a reflection probe bake'
    'graphics/bake_create_light_probe_group'      = 'creates scene objects'
    'graphics/bake_create_reflection_probe'       = 'creates scene objects'
    'graphics/bake_set_probe_positions'           = 'rewrites probe data'
    'test/run'                       = 'runs the whole test suite'
    'meta/export_registry'           = 'rewrites the shipped commands/registry.json'
    'meta/new-command'               = 'scaffolds new command files into the project'
    'physics/simulate_step'          = 'advances the physics simulation'
    'ui/render_ui'                   = 'renders and writes image files'
}

function Get-SkipReason {
    param([string]$Key)
    $parts = $Key -split '/'
    $group = $parts[0]
    $action = $parts[1]

    if ($Script:SkipNotes.ContainsKey($Key)) {
        if ($Script:OptionIncludeHeavy -and ($Script:HeavyList -contains $Key)) { return $null }
        return $Script:SkipNotes[$Key]
    }

    if ($Script:HeavyList -contains $Key) {
        if ($Script:OptionIncludeHeavy) { return $null }
        return 'slow or writes files; re-run with -IncludeHeavy'
    }

    # Safe to probe with no arguments?
    if ($Script:ReadOnlyProbe -contains $Key) { return $null }

    # Everything else mutates. Tier B drives the ones worth driving; the rest need real arguments.
    return 'mutating action; exercised by Tier B or requires arguments'
}

# ---------------------------------------------------------------- cleanup

function Remove-Sandbox {
    if ($Script:OptionKeepSandbox) {
        Write-Host "[selftest] keeping $($Script:Sandbox) (-KeepSandbox)" -ForegroundColor DarkYellow
        return
    }
    if (-not $Script:Bootstrapped) { return }
    $r = Invoke-Cucli -CliArgs @('asset', 'delete', '--path', $Script:Sandbox, '--force') -TimeoutSec 60
    if (-not $r.Ok) {
        Write-Host "[selftest] could not remove $($Script:Sandbox): $($r.ErrorCode) $($r.Message)" -ForegroundColor DarkYellow
    }
}

function Remove-TestObjects {
    if (-not $Script:Bootstrapped) { return }
    foreach ($name in @('CucliSelfTestRoot', 'CucliSelfTestCopy')) {
        Invoke-Cucli -CliArgs @('object', 'delete', '--target', $name, '--force', '--allow-multiple') -TimeoutSec 60 | Out-Null
    }
}

# ---------------------------------------------------------------- tier A

function Wait-ForWorker {
    <#
        Waits for the Editor worker to answer, then returns $true.

        Without this, running the self-test right after changing code - which is exactly when a
        developer runs it - produces one identical WORKER_UNAVAILABLE failure per action while a
        domain reload is in flight. That is noise, and noise in a test tool is worse than a slow
        test tool: 300 identical red lines hide the one real defect underneath them.

        A worker that is merely mid-compile is not a failure; a worker that never arrives is.
    #>
    param([int]$TimeoutSeconds = 0)

    if ($TimeoutSeconds -le 0) {
        $TimeoutSeconds = 180
        if ($env:CUCLI_SELFTEST_READY_TIMEOUT) {
            [void][int]::TryParse($env:CUCLI_SELFTEST_READY_TIMEOUT, [ref]$TimeoutSeconds)
        }
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    while ($true) {
        $attempt++
        $probe = Invoke-Cucli -CliArgs @('meta', 'doctor')
        if ($probe.Ok) {
            if ($attempt -gt 1) {
                Write-Host "[selftest] the worker answered after $attempt probes." -ForegroundColor DarkGray
            }
            return $true
        }
        if ($probe.ErrorCode -ne 'WORKER_UNAVAILABLE') {
            # Any other error is a real defect and should be reported by the tier itself.
            return $true
        }
        if ((Get-Date) -ge $deadline) { return $false }
        if ($attempt -eq 1) {
            Write-Host "[selftest] waiting up to ${TimeoutSeconds}s for the Editor worker (compile or domain reload in flight)..." -ForegroundColor Yellow
        }
        Start-Sleep -Seconds 5
    }
}

function Invoke-TierA {
    Write-Host "`n=== Tier A: contract probe (read-only actions, no arguments) ===" -ForegroundColor Cyan

    # One readiness gate instead of one failure per action. See Wait-ForWorker.
    if (-not (Wait-ForWorker)) {
        Add-Result -Tier 'A' -Key 'worker/readiness' -Status 'FAIL' -ErrorCode 'WORKER_UNAVAILABLE' `
            -Detail 'The Editor worker never answered, so no action could be probed. Open the project or wait for the reload to finish, then re-run.'
        Write-Host "[selftest] the worker is not answering; Tier A cannot run." -ForegroundColor Red
        return
    }

    foreach ($group in ($Script:CatalogMap.Keys | Sort-Object)) {
        if ($Script:OptionGroup.Count -gt 0 -and ($Script:OptionGroup -notcontains $group)) { continue }
        foreach ($action in ($Script:CatalogMap[$group].Keys | Sort-Object)) {
            $key = "$group/$action"
            if ($Script:OptionAction.Count -gt 0 -and ($Script:OptionAction -notcontains $key)) { continue }

            # Tier C owns the skip reporting; Tier A would otherwise duplicate every skip line.
            if ((Get-SkipReason $key) -ne $null) { continue }

            $r = Invoke-Cucli -CliArgs @($group, $action)
            $code = $r.ErrorCode

            if ($r.Ok) {
                Add-Result -Tier 'A' -Key $key -Status 'PASS' -DurationMs $r.DurationMs -Detail 'ran with no arguments'
            } elseif ($Script:FatalAnswers -contains $code) {
                Add-Result -Tier 'A' -Key $key -Status 'FAIL' -ErrorCode $code -DurationMs $r.DurationMs -Detail $r.Message
                if ($code -eq 'WORKER_UNAVAILABLE' -or $code -eq 'JOB_INVALID') {
                    Write-Host "[selftest] worker lost ($code); stopping the remaining contract probes." -ForegroundColor Red
                    return
                }
            } elseif ($code -eq 'TIMEOUT') {
                Add-Result -Tier 'A' -Key $key -Status 'FAIL' -ErrorCode $code -DurationMs $r.DurationMs -Detail $r.Message
            } elseif ($Script:ContractAnswers -contains $code) {
                Add-Result -Tier 'A' -Key $key -Status 'PASS' -ErrorCode $code -DurationMs $r.DurationMs -Detail 'validated input as expected'
            } else {
                Add-Result -Tier 'A' -Key $key -Status 'FAIL' -ErrorCode $code -DurationMs $r.DurationMs -Detail $r.Message
            }
        }
    }
}

# ---------------------------------------------------------------- tier B

<#
    The one invariant worth asserting everywhere: a successful command must return a data payload.
    Asserting each payload's shape would couple this harness to every command author's naming choices
    and produce false failures that hide real ones, so deeper checks appear only where the shape is
    part of the documented contract.
#>
$expectData = { param($x) if ($null -eq $x.Data) { 'succeeded without a data payload' } else { $true } }

function Assert-Cucli {
    param([string]$Key, [string[]]$CliArgs, [scriptblock]$Expect = $null, [int]$TimeoutSec = 0)

    if ($Script:OptionAction.Count -gt 0 -and ($Script:OptionAction -notcontains $Key)) { return $null }

    $parts = $Key -split '/'
    $groupName = $CliArgs[0]
    $actionName = if ($CliArgs.Count -gt 1) { $CliArgs[1] } else { $parts[1] }
    if (-not (Test-ActionExists $groupName $actionName)) {
        Add-Result -Tier 'B' -Key $Key -Status 'SKIP' -Detail "this build has no '$actionName' action on '$groupName'"
        return $null
    }

    if (-not $Expect) { $Expect = $expectData }
    $r = Invoke-Cucli -CliArgs $CliArgs -TimeoutSec $TimeoutSec
    if (-not $r.Ok -and $r.ErrorCode -eq 'WORKER_UNAVAILABLE') {
        Write-Host "[selftest] worker unavailable on $Key; waiting and retrying once." -ForegroundColor Yellow
        if (Wait-ForWorker) {
            $r = Invoke-Cucli -CliArgs $CliArgs -TimeoutSec $TimeoutSec
        }
    }
    if (-not $r.Ok) {
        Add-Result -Tier 'B' -Key $Key -Status 'FAIL' -ErrorCode $r.ErrorCode -DurationMs $r.DurationMs `
            -Detail "exit=$($r.ExitCode) $($r.Message)"
        return $r
    }

    $verdict = & $Expect $r
    if ($verdict -ne $true) {
        Add-Result -Tier 'B' -Key $Key -Status 'FAIL' -DurationMs $r.DurationMs -Detail "assertion failed: $verdict"
        return $r
    }

    Add-Result -Tier 'B' -Key $Key -Status 'PASS' -DurationMs $r.DurationMs -Artifacts $r.Artifacts
    return $r
}

function Probe-GuardRail {
    <# A destructive action without --force must refuse; the expected outcome is a non-zero exit. #>
    param([string]$Key, [string[]]$CliArgs)

    if ($Script:OptionAction.Count -gt 0 -and ($Script:OptionAction -notcontains $Key)) { return }
    $parts = $Key -split '/'
    if (-not (Test-ActionExists $parts[0] $parts[1])) {
        Add-Result -Tier 'B' -Key $Key -Status 'SKIP' -Detail "this build has no '$($parts[1])' action"
        return
    }

    $probe = Invoke-Cucli -CliArgs $CliArgs
    $refusals = @('CONFIRMATION_REQUIRED', 'ASSET_EXISTS', 'INVALID_PARAMETER', 'MISSING_PARAMETER')
    if ($probe.Ok) {
        Add-Result -Tier 'B' -Key $Key -Status 'FAIL' -DurationMs $probe.DurationMs `
            -Detail 'the destructive action succeeded without --force'
    } elseif ($refusals -contains $probe.ErrorCode) {
        Add-Result -Tier 'B' -Key $Key -Status 'PASS' -ErrorCode $probe.ErrorCode -DurationMs $probe.DurationMs `
            -Detail 'refused without --force'
    } else {
        Add-Result -Tier 'B' -Key $Key -Status 'FAIL' -ErrorCode $probe.ErrorCode -DurationMs $probe.DurationMs `
            -Detail "expected a confirmation refusal, got $($probe.ErrorCode): $($probe.Message)"
    }
}

function Invoke-TierB {
    Write-Host "`n=== Tier B: functional round trips (sandboxed) ===" -ForegroundColor Cyan

    # Same readiness gate as Tier A: a mid-reload Editor is not a Tier B failure.
    if (-not (Wait-ForWorker)) {
        Add-Result -Tier 'B' -Key 'worker/readiness' -Status 'FAIL' -ErrorCode 'WORKER_UNAVAILABLE' `
            -Detail 'The Editor worker never answered, so the sandboxed round trips could not run.'
        Write-Host "[selftest] the worker is not answering; Tier B cannot run." -ForegroundColor Red
        return
    }

    $doctor = Invoke-Cucli -CliArgs @('meta', 'doctor')
    if (-not $doctor.Ok) {
        Add-Result -Tier 'B' -Key 'meta/doctor' -Status 'FAIL' -ErrorCode $doctor.ErrorCode -Detail $doctor.Message
        Write-Host "[selftest] the worker is not answering; Tier B cannot run." -ForegroundColor Red
        return
    }
    Add-Result -Tier 'B' -Key 'meta/doctor' -Status 'PASS' -DurationMs $doctor.DurationMs
    $Script:Bootstrapped = $true

    $sandboxAbs = Join-Path $Script:ProjectRoot $Script:Sandbox

    # --- editor surface --------------------------------------------------
    Assert-Cucli 'editor/state'         @('editor', 'state')         | Out-Null
    Assert-Cucli 'editor/tool-list'     @('editor', 'tool-list')     | Out-Null
    Assert-Cucli 'editor/layer-list'    @('editor', 'layer-list')    { param($x) if (@($x.Data.layers).Count -ne 32) { "expected 32 layers, got $(@($x.Data.layers).Count)" } else { $true } } | Out-Null
    Assert-Cucli 'editor/selection-get' @('editor', 'selection-get') | Out-Null

    # Tag round trip: add, confirm, remove. Mutates ProjectSettings/TagManager.asset, so it must
    # restore the original state in both directions.
    $tag = 'CucliSelfTestTag'
    $added = Assert-Cucli 'editor/tag-add' @('editor', 'tag-add', '--tag', $tag)
    if ($added -and $added.Ok) {
        $tags = Invoke-Cucli -CliArgs @('editor', 'tag-list')
        $present = $false
        if ($tags.Ok -and $tags.Data.tags) { $present = (@($tags.Data.tags) -contains $tag) }
        if ($present) { Add-Result -Tier 'B' -Key 'editor/tag-roundtrip' -Status 'PASS' }
        else { Add-Result -Tier 'B' -Key 'editor/tag-roundtrip' -Status 'FAIL' -Detail 'tag was not present after tag-add' }
        Assert-Cucli 'editor/tag-remove' @('editor', 'tag-remove', '--tag', $tag) | Out-Null
    }

    if (Has-Group 'refresh') { Assert-Cucli 'refresh/status' @('refresh', 'status') | Out-Null }
    if (Has-Group 'console') { Assert-Cucli 'console/get' @('console', 'get', '--count', '5', '--page-size', '5') | Out-Null }
    if (Has-Group 'scene') {
        Assert-Cucli 'scene/get_active'         @('scene', 'get_active')         | Out-Null
        Assert-Cucli 'scene/get_loaded_scenes'  @('scene', 'get_loaded_scenes')  | Out-Null
        Assert-Cucli 'scene/get_hierarchy'      @('scene', 'get_hierarchy', '--page-size', '5', '--max-nodes', '500') | Out-Null
        Assert-Cucli 'scene/get_build_settings' @('scene', 'get_build_settings', '--page-size', '5') | Out-Null
        Assert-Cucli 'scene/validate'           @('scene', 'validate')           | Out-Null
    }
    if (Has-Group 'meta') {
        Assert-Cucli 'meta/catalog' @('meta', 'catalog') `
            { param($x) if (@($x.Data.commands).Count -lt 1) { 'empty catalog' } else { $true } } | Out-Null
        Assert-Cucli 'meta/schema'      @('meta', 'schema', '--group', 'object') `
            { param($x) if (-not $x.Data.schema) { 'no schema returned' } else { $true } } | Out-Null
        Assert-Cucli 'meta/environment' @('meta', 'environment') | Out-Null
        Assert-Cucli 'meta/extensions'  @('meta', 'extensions')  | Out-Null
        Assert-Cucli 'meta/verify'      @('meta', 'verify')      | Out-Null
    }
    if (Has-Group 'package') {
        Assert-Cucli 'package/status'          @('package', 'status') | Out-Null
        Assert-Cucli 'package/list_packages'   @('package', 'list_packages', '--page-size', '5') | Out-Null
        Assert-Cucli 'package/list_registries' @('package', 'list_registries') | Out-Null
        Assert-Cucli 'package/search_packages' @('package', 'search_packages', '--query', 'newtonsoft', '--page-size', '5') | Out-Null
    }
    if (Has-Group 'build') {
        Assert-Cucli 'build/status'   @('build', 'status')   | Out-Null
        Assert-Cucli 'build/settings' @('build', 'settings', '--property', 'product_name') | Out-Null
        Assert-Cucli 'build/scenes'   @('build', 'scenes', '--page-size', '5') | Out-Null
        Assert-Cucli 'build/profiles' @('build', 'profiles') | Out-Null
    }
    if (Has-Group 'graphics') {
        Assert-Cucli 'graphics/stats_get'            @('graphics', 'stats_get')            | Out-Null
        Assert-Cucli 'graphics/pipeline_get_info'    @('graphics', 'pipeline_get_info')    | Out-Null
        Assert-Cucli 'graphics/pipeline_get_settings' @('graphics', 'pipeline_get_settings') | Out-Null
        Assert-Cucli 'graphics/skybox_get'           @('graphics', 'skybox_get')           | Out-Null
        Assert-Cucli 'graphics/feature_list'         @('graphics', 'feature_list')         | Out-Null
        Assert-Cucli 'graphics/bake_status'          @('graphics', 'bake_status')          | Out-Null
    }
    if (Has-Group 'camera') {
        Assert-Cucli 'camera/list_cameras' @('camera', 'list_cameras') | Out-Null
        Assert-Cucli 'camera/ping'         @('camera', 'ping')         | Out-Null
    }
    if (Has-Group 'test') { Assert-Cucli 'test/status' @('test', 'status') | Out-Null }
    if (Has-Group 'ui') {
        Assert-Cucli 'ui/ping' @('ui', 'ping') | Out-Null
        Assert-Cucli 'ui/list' @('ui', 'list') | Out-Null
    }

    # --- sandbox folder --------------------------------------------------
    Invoke-Cucli -CliArgs @('asset', 'delete', '--path', $Script:Sandbox, '--force') | Out-Null
    if (Has-Group 'asset') {
        Assert-Cucli 'asset/create_folder' @('asset', 'create_folder', '--path', $Script:Sandbox) `
            { param($x) if (-not (Test-Path -LiteralPath $sandboxAbs)) { "folder was not created at $sandboxAbs" } else { $true } } | Out-Null
        Assert-Cucli 'asset/get_info'      @('asset', 'get_info', '--path', $Script:Sandbox) | Out-Null
        Assert-Cucli 'asset/search'        @('asset', 'search', '--folder', $Script:Sandbox, '--page-size', '10') | Out-Null
    }

    # --- asset creation and mutation -------------------------------------
    if (Has-Group 'shader') {
        Assert-Cucli 'shader/create' @('shader', 'create', '--path', "$($Script:Sandbox)/SelfTest.shader", '--template', 'builtin', '--force') | Out-Null
        Assert-Cucli 'shader/read'   @('shader', 'read', '--path', "$($Script:Sandbox)/SelfTest.shader", '--max-chars', '4000') | Out-Null
    }
    if (Has-Group 'material') {
        Assert-Cucli 'material/create'                  @('material', 'create', '--path', "$($Script:Sandbox)/SelfTest.mat", '--force') | Out-Null
        Assert-Cucli 'material/get_material_info'       @('material', 'get_material_info', '--path', "$($Script:Sandbox)/SelfTest.mat") | Out-Null
        Assert-Cucli 'material/set_material_color'      @('material', 'set_material_color', '--path', "$($Script:Sandbox)/SelfTest.mat", '--property', '_Color', '--color', '#3366FF') | Out-Null
        Assert-Cucli 'material/set_material_shader_property' @('material', 'set_material_shader_property', '--path', "$($Script:Sandbox)/SelfTest.mat", '--property', '_Glossiness', '--value', '0.25') | Out-Null
    }
    if (Has-Group 'texture') {
        Assert-Cucli 'texture/create'             @('texture', 'create', '--path', "$($Script:Sandbox)/SelfTest.png", '--width', '8', '--height', '8', '--color', '#FF8800', '--force') | Out-Null
        Assert-Cucli 'texture/apply_noise'        @('texture', 'apply_noise', '--path', "$($Script:Sandbox)/SelfTest.png", '--seed', '7', '--force') | Out-Null
        Assert-Cucli 'texture/apply_pattern'      @('texture', 'apply_pattern', '--path', "$($Script:Sandbox)/SelfTest.png", '--pattern', 'checker', '--force') | Out-Null
        Assert-Cucli 'texture/apply_gradient'     @('texture', 'apply_gradient', '--path', "$($Script:Sandbox)/SelfTest.png", '--force') | Out-Null
        Assert-Cucli 'texture/set_import_settings' @('texture', 'set_import_settings', '--path', "$($Script:Sandbox)/SelfTest.png", '--filter-mode', 'Point') | Out-Null
    }
    if (Has-Group 'animation') {
        Assert-Cucli 'animation/clip_create' @('animation', 'clip_create', '--clip-path', "$($Script:Sandbox)/SelfTest.anim", '--force') | Out-Null
        Assert-Cucli 'animation/clip_get_info' @('animation', 'clip_get_info', '--clip-path', "$($Script:Sandbox)/SelfTest.anim") | Out-Null
        Assert-Cucli 'animation/controller_create' @('animation', 'controller_create', '--controller-path', "$($Script:Sandbox)/SelfTest.controller", '--force') | Out-Null
        Assert-Cucli 'animation/controller_get_info' @('animation', 'controller_get_info', '--controller-path', "$($Script:Sandbox)/SelfTest.controller") | Out-Null
    }
    if (Has-Group 'script') {
        $body = "using System;`nnamespace CucliSelfTest { internal static class Generated { public const int Value = 42; } }"
            Assert-Cucli 'script/create'            @('script', 'create', '--path', "$($Script:Sandbox)/SelfTest.cs", '--contents', $body, '--force') | Out-Null
        Assert-Cucli 'script/read'              @('script', 'read', '--path', "$($Script:Sandbox)/SelfTest.cs", '--max-chars', '4000') | Out-Null
        Assert-Cucli 'script/get_sha'           @('script', 'get_sha', '--path', "$($Script:Sandbox)/SelfTest.cs") | Out-Null
        Assert-Cucli 'script/validate'          @('script', 'validate', '--path', "$($Script:Sandbox)/SelfTest.cs") | Out-Null
        $sha = Invoke-Cucli -CliArgs @('script', 'get_sha', '--path', "$($Script:Sandbox)/SelfTest.cs")
        $shaValue = if ($sha.Ok -and $sha.Data.sha256) { [string]$sha.Data.sha256 } elseif ($sha.Ok -and $sha.Data.sha) { [string]$sha.Data.sha } else { '' }
        if ($shaValue) {
            Assert-Cucli 'script/apply_text_edits' @('script', 'apply_text_edits', '--path', "$($Script:Sandbox)/SelfTest.cs", '--precondition-sha256', $shaValue, '--edits', '[{"startLine":2,"startCol":1,"endLine":2,"endCol":1,"newText":"// touched by cucli-selftest\n"}]') | Out-Null
        } else {
            Add-Result -Tier 'B' -Key 'script/apply_text_edits' -Status 'FAIL' -Detail 'could not read script SHA before apply_text_edits'
        }
    }

    # --- GameObject round trip ------------------------------------------
    if (Has-Group 'object') {
        Remove-TestObjects
        Assert-Cucli 'object/create'      @('object', 'create', '--name', 'CucliSelfTestRoot') | Out-Null
        Assert-Cucli 'object/create-child' @('object', 'create', '--name', 'CucliSelfTestChild', '--primitive', 'sphere', '--parent', 'CucliSelfTestRoot', '--position', '0,1,0') | Out-Null
        Assert-Cucli 'object/find'        @('object', 'find', '--search-method', 'by_name', '--name', 'CucliSelfTestChild') | Out-Null
        Assert-Cucli 'object/inspect'     @('object', 'inspect', '--target', 'CucliSelfTestChild', '--max-depth', '2', '--max-items', '20') | Out-Null
        Assert-Cucli 'object/list'        @('object', 'list', '--target', 'CucliSelfTestRoot') | Out-Null
        Assert-Cucli 'object/get-property' @('object', 'get-property', '--target', 'CucliSelfTestChild', '--component', 'Transform', '--property', 'position') | Out-Null
        Assert-Cucli 'object/set-property' @('object', 'set-property', '--target', 'CucliSelfTestChild', '--component', 'Transform', '--property', 'localScale', '--value', '[2,2,2]') | Out-Null
        Assert-Cucli 'object/add'         @('object', 'add', '--target', 'CucliSelfTestChild', '--component', 'BoxCollider') | Out-Null
        Assert-Cucli 'object/modify'      @('object', 'modify', '--target', 'CucliSelfTestChild', '--name', 'CucliSelfTestChild') | Out-Null
        Assert-Cucli 'object/duplicate'   @('object', 'duplicate', '--target', 'CucliSelfTestChild', '--name', 'CucliSelfTestCopy') | Out-Null
        Assert-Cucli 'object/move_relative' @('object', 'move_relative', '--target', 'CucliSelfTestCopy', '--position', '1,0,0') | Out-Null
        Assert-Cucli 'object/look_at'     @('object', 'look_at', '--target', 'CucliSelfTestCopy', '--look-at-target', 'CucliSelfTestRoot') | Out-Null
        Assert-Cucli 'object/remove'      @('object', 'remove', '--target', 'CucliSelfTestChild', '--component', 'BoxCollider') | Out-Null
    }

    # --- prefab round trip ----------------------------------------------
    if ((Has-Group 'prefab') -and (Has-Group 'object')) {
        Assert-Cucli 'prefab/create_from_gameobject' @('prefab', 'create_from_gameobject', '--target', 'CucliSelfTestRoot', '--path', "$($Script:Sandbox)/SelfTest.prefab", '--force') | Out-Null
        Assert-Cucli 'prefab/get_info'      @('prefab', 'get_info', '--path', "$($Script:Sandbox)/SelfTest.prefab") | Out-Null
        Assert-Cucli 'prefab/get_hierarchy' @('prefab', 'get_hierarchy', '--path', "$($Script:Sandbox)/SelfTest.prefab", '--page-size', '10') | Out-Null
    }

    # --- asset duplicate / move / rename --------------------------------
    if (Has-Group 'asset') {
        Assert-Cucli 'asset/duplicate' @('asset', 'duplicate', '--path', "$($Script:Sandbox)/SelfTest.mat", '--target-path', "$($Script:Sandbox)/Copy.mat") | Out-Null
        Assert-Cucli 'asset/rename'    @('asset', 'rename', '--path', "$($Script:Sandbox)/Copy.mat", '--name', 'Renamed.mat') | Out-Null
        Assert-Cucli 'asset/import'    @('asset', 'import', '--path', "$($Script:Sandbox)/SelfTest.png") | Out-Null
        Assert-Cucli 'asset/modify'    @('asset', 'modify', '--path', "$($Script:Sandbox)/SelfTest.mat", '--properties', '{"name":"SelfTest"}') | Out-Null
    }

    # --- ScriptableObject dry run against a real project asset -----------
    if (Has-Group 'so') {
        $found = Invoke-Cucli -CliArgs @('asset', 'search', '--filter-type', 'ScriptableObject', '--page-size', '20')
        $soPath = $null
        if ($found.Ok) {
            $candidates = @()
            if ($found.Data.items) { $candidates += @($found.Data.items) }
            elseif ($found.Data.assets) { $candidates += @($found.Data.assets) }
            foreach ($entry in $candidates) {
                if ($null -eq $entry) { continue }
                $entryPath = if ($entry -is [string]) { $entry } else { $entry.path }
                $entryType = if ($entry -is [string]) { '' } else { [string]$entry.assetType }
                if ([string]::IsNullOrWhiteSpace($entryPath)) { continue }
                if ($entryType -eq 'Folder') { continue }
                $soPath = [string]$entryPath
                break
            }
        }
        if ($soPath) {
            Assert-Cucli 'so/modify-dry-run' @('so', 'modify', '--target-path', $soPath, '--dry-run', '--patches', '[]') | Out-Null
        } else {
            Add-Result -Tier 'B' -Key 'so/modify-dry-run' -Status 'SKIP' -Detail 'no ScriptableObject asset found to probe'
        }
    }

    # --- guard rails ----------------------------------------------------
    Probe-GuardRail 'asset/delete'  @('asset', 'delete', '--path', "$($Script:Sandbox)/SelfTest.mat")
    Probe-GuardRail 'script/delete' @('script', 'delete', '--path', "$($Script:Sandbox)/SelfTest.cs")
    Probe-GuardRail 'object/delete' @('object', 'delete', '--target', 'CucliSelfTestChild')

    # --- cleanup --------------------------------------------------------
    if (Has-Group 'object') {
        $r = Invoke-Cucli -CliArgs @('object', 'delete', '--target', 'CucliSelfTestChild', '--force', '--allow-multiple')
        if ($r.Ok) { Add-Result -Tier 'B' -Key 'object/delete-with-force' -Status 'PASS' -DurationMs $r.DurationMs }
        else { Add-Result -Tier 'B' -Key 'object/delete-with-force' -Status 'FAIL' -ErrorCode $r.ErrorCode -Detail $r.Message }
    }
    Remove-TestObjects

    if (Has-Group 'asset') {
        $r = Invoke-Cucli -CliArgs @('asset', 'delete', '--path', $Script:Sandbox, '--force')
        if ($r.Ok) { Add-Result -Tier 'B' -Key 'asset/delete-sandbox' -Status 'PASS' -DurationMs $r.DurationMs }
        else { Add-Result -Tier 'B' -Key 'asset/delete-sandbox' -Status 'FAIL' -ErrorCode $r.ErrorCode -Detail $r.Message }
    }
}

# ---------------------------------------------------------------- tier C

function Invoke-TierC {
    Write-Host "`n=== Tier C: documented skips ===" -ForegroundColor Cyan
    foreach ($group in ($Script:CatalogMap.Keys | Sort-Object)) {
        if ($Script:OptionGroup.Count -gt 0 -and ($Script:OptionGroup -notcontains $group)) { continue }
        foreach ($action in ($Script:CatalogMap[$group].Keys | Sort-Object)) {
            $key = "$group/$action"
            if ($Script:OptionAction.Count -gt 0 -and ($Script:OptionAction -notcontains $key)) { continue }
            $reason = Get-SkipReason $key
            if (-not $reason) { continue }
            Add-Result -Tier 'C' -Key $key -Status 'SKIP' -Detail $reason
        }
    }
}

# ---------------------------------------------------------------- report

function Write-Report {
    param([string]$Path)

    # Enumerated defensively: the results collection is appended to from several tiers, and a report
    # generator must never be the reason a run is declared broken.
    #
    # Do NOT wrap this in @(...): on PowerShell 7.6, `@($list)` where $list is a
    # System.Collections.Generic.List[object] holding PSCustomObjects throws
    # "ArgumentException: Argument types do not match". List[string] and List[psobject] are fine,
    # which is exactly why the trap stays hidden until the collection holds a result row.
    # Plain `foreach` over the list is both correct and faster.
    $all = @()
    if ($null -ne $Script:Results) {
        foreach ($entry in $Script:Results) { if ($null -ne $entry) { $all += $entry } }
    }
    $pass = @($all | Where-Object { $_.status -eq 'PASS' })
    $fail = @($all | Where-Object { $_.status -eq 'FAIL' })
    $skip = @($all | Where-Object { $_.status -eq 'SKIP' })
    $covered = $pass.Count + $fail.Count

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# cucli self-test report')
    $lines.Add('')
    $lines.Add("- Run at (UTC): $([datetime]::UtcNow.ToString('O'))")
    $lines.Add("- Project: $($Script:ProjectRoot)")
    $lines.Add("- Tiers: $($Script:OptionTier)")
    $lines.Add("- Surface: $(($Script:CatalogMap.Keys | Measure-Object).Count) groups, $(@($all | Where-Object { $_.tier -ne 'B' }).Count) declared actions")
    $lines.Add("- Result: **$(if ($fail.Count -eq 0) { 'PASS' } else { 'FAIL' })** - $($pass.Count) passed, $($fail.Count) failed, $($skip.Count) skipped")
    $lines.Add("- Executed (pass+fail): $covered")
    $lines.Add('')
    $lines.Add('## Results')
    $lines.Add('')
    $lines.Add('| tier | key | status | errorCode | ms | detail |')
    $lines.Add('| --- | --- | --- | --- | --- | --- |')
    foreach ($row in $all) {
        $detail = ([string]$row.detail).Replace('|', '\|').Replace("`n", ' ')
        if ($detail.Length -gt 160) { $detail = $detail.Substring(0, 160) + '...' }
        $lines.Add("| $($row.tier) | ``$($row.key)`` | $($row.status) | $($row.errorCode) | $($row.durationMs) | $detail |")
    }

    if ($fail.Count -gt 0) {
        $lines.Add('')
        $lines.Add('## Failures')
        $lines.Add('')
        foreach ($row in $fail) {
            $lines.Add("- ``$($row.key)`` [$($row.errorCode)] $($row.detail)")
        }
    }

    $lines.Add('')
    $lines.Add('## Skipped, with reasons')
    $lines.Add('')
    foreach ($row in ($skip | Sort-Object key)) {
        $lines.Add("- ``$($row.key)`` - $($row.detail)")
    }

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    [System.IO.File]::WriteAllText($Path, ($lines -join "`n"), (New-Object System.Text.UTF8Encoding($false)))

    return [ordered]@{ Pass = $pass.Count; Fail = $fail.Count; Skip = $skip.Count; Total = $all.Count; Path = $Path }
}

# ---------------------------------------------------------------- main

Write-Host 'cucli self-test' -ForegroundColor Green
Write-Host "  cli     : $($Script:Cli)"
Write-Host "  tiers   : $($Script:OptionTier)"
Write-Host "  groups  : $(if ($Script:OptionGroup.Count) { $Script:OptionGroup -join ',' } else { 'all' })"
Write-Host "  heavy   : $($Script:OptionIncludeHeavy)"

$Script:ProjectRoot = Find-ProjectRoot -Start $Script:SelfDir
if (-not $Script:ProjectRoot) { $Script:ProjectRoot = Find-ProjectRoot -Start (Get-Location).Path }
if (-not $Script:ProjectRoot) {
    Write-Host '[selftest] no Unity project found above this script or the current directory.' -ForegroundColor Red
    exit 4
}

$catalog = Get-Catalog
if (-not $catalog) {
    Write-Host "[selftest] no worker catalog at Library/CustomUnityCli/worker/commands.json." -ForegroundColor Red
    Write-Host "[selftest] Open Unity on $($Script:ProjectRoot) first (or run 'cucli open --wait')." -ForegroundColor Red
    exit 7
}

foreach ($group in $catalog.commands) {
    $map = @{}
    foreach ($action in @($group.actions)) { $map[$action.name] = $true }
    $Script:CatalogMap[$group.name] = $map
}

$declared = 0
foreach ($group in $Script:CatalogMap.Keys) { $declared += $Script:CatalogMap[$group].Count }
Write-Host "  catalog : $(($Script:CatalogMap.Keys | Measure-Object).Count) groups, $declared actions"
Write-Host "  project : $($Script:ProjectRoot)"

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    if ($runA) { Invoke-TierA }
    if ($runB) { Invoke-TierB }
    if ($runC) { Invoke-TierC }
} finally {
    Remove-Sandbox
    Remove-TestObjects
    $stopwatch.Stop()
}

$reportPath = $Script:OptionReport
if ([string]::IsNullOrWhiteSpace($reportPath)) { $reportPath = 'Library/CustomUnityCli/selftest-report.md' }
if (-not [System.IO.Path]::IsPathRooted($reportPath)) { $reportPath = Join-Path $Script:ProjectRoot $reportPath }

# A broken report writer must never invalidate the run: the verdict below is computed from the raw
# results, and the report is a convenience. Previously an ArgumentException here aborted the whole
# self-test before it printed anything, which is the worst possible failure mode for a test tool.
$summary = $null
try {
    $summary = Write-Report -Path $reportPath
} catch {
    Write-Host "report generation failed (the run itself is unaffected): $($_.Exception.Message)" -ForegroundColor Yellow
}

$allResults = @()
# See the note in Write-Report: `@($Script:Results)` throws on PowerShell 7.6 for List[object].
foreach ($entry in $Script:Results) { if ($null -ne $entry) { $allResults += $entry } }
$failures = @($allResults | Where-Object { $_.status -eq 'FAIL' })
if ($null -eq $summary) {
    $summary = [pscustomobject]@{
        Pass = @($allResults | Where-Object { $_.status -eq 'PASS' }).Count
        Fail = $failures.Count
        Skip = @($allResults | Where-Object { $_.status -eq 'SKIP' }).Count
        Path = '(not written)'
    }
}

Write-Host ''
Write-Host ('=' * 72)
Write-Host ("RESULT: {0}   passed={1} failed={2} skipped={3}  ({4:N1}s)" -f `
    $(if ($summary.Fail -eq 0) { 'PASS' } else { 'FAIL' }), $summary.Pass, $summary.Fail, $summary.Skip, `
    ($stopwatch.Elapsed.TotalSeconds)) -ForegroundColor $(if ($summary.Fail -eq 0) { 'Green' } else { 'Red' })
Write-Host "report: $($summary.Path)"

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failures:' -ForegroundColor Red
    foreach ($row in $failures) {
        Write-Host ("  [{0}] {1}  {2}  {3}" -f $row.tier, $row.key, $row.errorCode, $row.detail) -ForegroundColor Red
    }
}
Write-Host ('=' * 72)
exit $(if ($summary.Fail -eq 0) { 0 } else { 1 })
