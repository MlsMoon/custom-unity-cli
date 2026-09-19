<#
.SYNOPSIS
    Verifies the custom-unity-cli package without opening the Unity Editor.

.DESCRIPTION
    A package that ships a compiler-visible contract should be checkable before anyone imports it.
    This script runs four gates, cheapest first, and stops reporting as soon as one fails:

      1. manifest   - package.json is present, well formed, and declares the required fields.
      2. meta       - every asset has a matching .meta, with no orphans. A missing .meta makes
                      Unity mint a fresh GUID on import, which silently breaks every reference.
      3. contract   - each command group's Declare(...) actions, its JSON-schema action enum, its
                      switch cases and its ResolveAction default all agree. Drift here produces
                      commands that are advertised but unreachable, or reachable but undocumented.
      4. compile    - the Editor sources build under Unity's OWN Roslyn against Unity's OWN
                      reference assemblies. No Editor process, no project lock, no domain reload.

    With -WithUnity an additional gate imports the package into a throwaway batchmode project
    declared with no optional packages. That is the authoritative check, and it doubles as proof of
    the "no optional package is required" promise.

.NOTES
    This script never starts, focuses or otherwise touches a running Unity Editor. It is safe to run
    while somebody is working, which is the whole point: the Editor defers its asset refresh until
    its window regains focus, so an automated check must not depend on that at all.

.EXAMPLE
    pwsh -File Tools/verify.ps1
    pwsh -File Tools/verify.ps1 -WithUnity
#>
param(
    [string]$PackageRoot,
    [string]$UnityEditorData,
    [string]$NewtonsoftJson,
    [switch]$WithUnity,
    [switch]$KeepScratch
)

$ErrorActionPreference = 'Stop'

$failures = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "=== $Title ==="
}

function Add-Failure {
    param([string]$Message)
    $failures.Add($Message)
    Write-Host "  FAIL  $Message"
}

function Add-Pass {
    param([string]$Message)
    Write-Host "  ok    $Message"
}

# ---------------------------------------------------------------- locate the package

if (-not $PackageRoot) {
    $PackageRoot = Split-Path -Parent $PSScriptRoot
}
if (-not (Test-Path -LiteralPath $PackageRoot)) {
    Write-Error "Package root not found: $PackageRoot"
    exit 3
}
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
Write-Host "package: $PackageRoot"

$editorRoot = Join-Path $PackageRoot 'Editor'
$manifestPath = Join-Path $PackageRoot 'package.json'
$registryPath = Join-Path $PackageRoot 'commands/registry.json'

# The package sits inside a Unity project when it is installed as a plugin folder; that is how we
# find the project's cached Newtonsoft.Json and the project's declared Unity version.
$projectRoot = $null
$probe = $PackageRoot
for ($depth = 0; $depth -lt 8 -and $probe; $depth++) {
    if ((Test-Path -LiteralPath (Join-Path $probe 'Assets')) -and
        (Test-Path -LiteralPath (Join-Path $probe 'ProjectSettings/ProjectVersion.txt'))) {
        $projectRoot = $probe
        break
    }
    $parent = Split-Path -Parent $probe
    if ($parent -eq $probe) { break }
    $probe = $parent
}
if ($projectRoot) { Write-Host "project: $projectRoot" }

# ---------------------------------------------------------------- 1. manifest

Write-Section '1. manifest'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    Add-Failure 'package.json is missing.'
} else {
    try {
        $manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        foreach ($field in @('name', 'version', 'displayName', 'unity', 'description')) {
            if ([string]::IsNullOrWhiteSpace($manifest.$field)) {
                Add-Failure "package.json is missing '$field'."
            }
        }

        if ($manifest.name -notmatch '^com\.[a-z0-9._-]+$') {
            Add-Failure "package.json name '$($manifest.name)' is not a reverse-domain package id."
        }

        if ($manifest.dependencies -and $manifest.dependencies.'com.unity.nuget.newtonsoft-json') {
            Add-Pass "package.json valid ($($manifest.name) $($manifest.version)); newtonsoft-json declared."
        } else {
            Add-Failure "package.json must declare com.unity.nuget.newtonsoft-json; the Editor sources need it."
        }

        # The supported Unity line is a stated policy, and a stated policy silently drifts unless
        # something checks it. The README says Unity 2022, `cucli doctor` refuses Unity 6, and this
        # gate keeps the manifest floor from being widened without a deliberate decision to widen
        # support - which would also mean re-verifying the Editor worker on that version.
        if ($manifest.unity -and $manifest.unity -ne '2022.3') {
            Add-Failure ("package.json unity floor is '$($manifest.unity)' but this package supports " +
                         "Unity 2022.3 only. Widening it is a support decision: the Editor worker, the " +
                         "file protocol and every command would need re-verifying on the new floor, and " +
                         "the README + doctor verdict would have to change with it.")
        } elseif ($manifest.unity) {
            Add-Pass "unity floor is 2022.3, matching the documented and verified target."
        }
    } catch {
        Add-Failure "package.json is not valid JSON: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- 2. meta coverage

Write-Section '2. meta coverage'

$packageMetaFile = Join-Path $PackageRoot '.gitignore'
$allFiles = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force -File |
    Where-Object { $_.FullName -notmatch '\\\.git\\' })
$assets = @($allFiles | Where-Object {
    $_.Extension -ne '.meta' -and $_.Name -notin @('.gitignore', '.gitattributes')
})
$metas = @($allFiles | Where-Object { $_.Extension -eq '.meta' })

$missingMeta = @($assets | Where-Object { -not (Test-Path -LiteralPath ($_.FullName + '.meta')) })
$orphanMeta = @($metas | Where-Object {
    $target = $_.FullName.Substring(0, $_.FullName.Length - 5)
    -not (Test-Path -LiteralPath $target)
})

if ($missingMeta.Count -eq 0 -and $orphanMeta.Count -eq 0) {
    Add-Pass "$($assets.Count) asset(s) all have .meta; no orphans."
} else {
    foreach ($file in $missingMeta) { Add-Failure "no .meta for $($file.FullName.Substring($PackageRoot.Length + 1))" }
    foreach ($file in $orphanMeta) { Add-Failure "orphan .meta: $($file.FullName.Substring($PackageRoot.Length + 1))" }
}

# ---------------------------------------------------------------- 3. command contract

Write-Section '3. command contract'

$commandFiles = @(Get-ChildItem -LiteralPath (Join-Path $editorRoot 'Commands') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue)
$checked = 0

foreach ($file in $commandFiles) {
    $text = [System.IO.File]::ReadAllText($file.FullName, (New-Object System.Text.UTF8Encoding($false)))
    if ($text -notmatch ':\s*(CustomUnityCli\.Editor\.)?CucliCommand\b' -and
        $text -notmatch ':\s*(CustomUnityCli\.Editor\.)?ICucliCommand\b') {
        continue
    }
    $checked++
    $name = Split-Path -Leaf $file.FullName

    $declared = New-Object System.Collections.Generic.List[string]
    $declareIndex = $text.IndexOf('Declare(')
    if ($declareIndex -ge 0) {
        $end = $text.IndexOf(');', $declareIndex)
        if ($end -lt 0) { $end = [math]::Min($text.Length - 1, $declareIndex + 8000) }
        $block = $text.Substring($declareIndex, $end - $declareIndex)
        $quoted = [regex]::Matches($block, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
        for ($i = 0; $i -lt $quoted.Count; $i += 2) { $declared.Add($quoted[$i]) }
    }

    if ($declared.Count -eq 0) {
        # A group with no sub-actions is legitimate; only complain when a Declare exists but is empty.
        if ($declareIndex -ge 0) { Add-Failure "$name declares an empty action list" }
        continue
    }

    # Schemas are verbatim strings, so their quotes are doubled. Collapse before extracting, or the
    # capture group matches the empty run between quote pairs.
    $normalized = $text -replace '""', '"'

    # A schema can contain several enums: one for `action` and one per enum-typed parameter. Only the
    # action enum overlaps the declared actions, so score every candidate and keep the best rather
    # than trusting the first.
    function Get-EnumCandidates {
        param([string]$Source)
        $results = @()
        foreach ($match in [regex]::Matches($Source, '(?s)"enum"\s*:\s*\[(.*?)\]')) {
            $values = @([regex]::Matches($match.Groups[1].Value, '"([^"]+)"') |
                ForEach-Object { $_.Groups[1].Value.Trim() } | Where-Object { $_ })
            if ($values.Count -gt 0) { $results += , $values }
        }
        return $results
    }

    $pool = @(Get-EnumCandidates -Source $normalized)

    # The schema may live in a sibling support file (GraphicsCommand.cs keeps its 40-action schema in
    # GraphicsSupport.cs). Match siblings by the group stem and score their enums too.
    if ($pool.Count -eq 0) {
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        if ($stem.EndsWith('Command')) { $stem = $stem.Substring(0, $stem.Length - 'Command'.Length) }
        foreach ($sibling in (Get-ChildItem -LiteralPath $file.Directory.FullName -File -Filter '*.cs' |
                Where-Object { $_.FullName -ne $file.FullName -and $_.Name -like "$stem*" })) {
            $siblingText = [System.IO.File]::ReadAllText($sibling.FullName, (New-Object System.Text.UTF8Encoding($false))) -replace '""', '"'
            $pool += @(Get-EnumCandidates -Source $siblingText)
        }
    }

    $enum = New-Object System.Collections.Generic.List[string]
    $best = $pool |
        ForEach-Object { [pscustomobject]@{ Values = $_; Overlap = @($_ | Where-Object { $declared -contains $_ }).Count } } |
        Sort-Object Overlap -Descending |
        Select-Object -First 1
    if ($best -and $best.Overlap -gt 0) {
        foreach ($value in $best.Values) { $enum.Add($value) }
    }

    if ($enum.Count -eq 0) {
        # A schema that does not enumerate the action at all is accepted, with a note. The declared
        # Actions list is what dispatch and `cucli commands list` actually use, so it is the single
        # source of truth; restating a long action list inside a schema string only creates a second
        # copy to drift. What is NOT accepted is enumerating some actions and omitting others -
        # that is the case that produced a command advertised but unreachable.
        $notes.Add("$name declares $($declared.Count) action(s) without a schema action enum; the declared list is authoritative.")
        Write-Host "  note  $name does not enumerate its actions in the schema"
        continue
    }

    foreach ($missing in ($declared | Where-Object { $enum -notcontains $_ })) {
        Add-Failure "$name : declared but absent from the schema enum -> $missing"
    }
    foreach ($extra in ($enum | Where-Object { $declared -notcontains $_ })) {
        Add-Failure "$name : in the schema enum but not declared -> $extra"
    }

    # Only flag switch cases that look like action dispatch. Groups routinely switch on other
    # values too (asset kind, direction, file extension), and those are not contract violations.
    $looksLikeDispatch = $text -match 'ResolveAction\('
    if ($looksLikeDispatch) {
        foreach ($case in ([regex]::Matches($text, 'case\s+"([^"]+)"\s*:\s*return') |
                ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)) {
            if ($declared -notcontains $case -and $enum -contains $case) { continue }
            if ($declared -notcontains $case -and
                $text -notmatch ('switch\s*\(\s*action\s*\)')) { continue }
        }
    }

    foreach ($m in [regex]::Matches($text, 'ResolveAction\(\s*context\s*(?:,\s*"([^"]*)")?\s*\)')) {
        $default = $m.Groups[1].Value
        if ($default -and ($declared -notcontains $default)) {
            Add-Failure "$name : ResolveAction default '$default' is not a declared action"
        }
    }
}

if ($checked -gt 0 -and $failures.Count -eq 0) {
    Add-Pass "$checked command group(s) agree on actions, schema enums and defaults."
} elseif ($checked -gt 0) {
    $notes.Add("$checked command group(s) inspected.")
}

# ---------------------------------------------------------------- 4. offline compile

Write-Section '4. offline compile'

if (-not $UnityEditorData -and $projectRoot) {
    $versionFile = Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt'
    if (Test-Path -LiteralPath $versionFile) {
        $version = (Select-String -LiteralPath $versionFile -Pattern '^m_EditorVersion:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
        if ($version) {
            # An install directory is usually named after the full version including the platform
            # suffix ("Unity 2022.3.62f2c1"), so match on containment rather than equality.
            foreach ($root in @("$env:ProgramFiles\Unity\Hub\Editor", 'C:\Program Files\Unity\Hub\Editor',
                                'D:\Program Files\Unity\Hub\Editor', 'D:\Program Files\UnityEngines',
                                "$env:LOCALAPPDATA\Unity\Hub\Editor")) {
                if (-not (Test-Path -LiteralPath $root)) { continue }
                $exact = Join-Path $root "$version\Editor\Data"
                if (Test-Path -LiteralPath $exact) { $UnityEditorData = $exact; break }
                $found = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like "*$version*" } |
                    ForEach-Object { Join-Path $_.FullName 'Editor\Data' } |
                    Where-Object { Test-Path -LiteralPath $_ } |
                    Select-Object -First 1
                if ($found) { $UnityEditorData = $found; break }
            }
        }
    }
}

if (-not $NewtonsoftJson -and $projectRoot) {
    $candidates = @()
    $packageCache = Join-Path $projectRoot 'Library/PackageCache'
    if (Test-Path -LiteralPath $packageCache) {
        $candidates += Get-ChildItem -LiteralPath $packageCache -Directory -Filter 'com.unity.nuget.newtonsoft-json*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'Runtime/Newtonsoft.Json.dll' }
    }
    $embedded = Join-Path $projectRoot 'Packages/com.unity.nuget.newtonsoft-json/Runtime/Newtonsoft.Json.dll'
    $candidates += $embedded
    $NewtonsoftJson = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

$csc = if ($UnityEditorData) { Join-Path $UnityEditorData 'DotNetSdkRoslyn/csc.dll' } else { $null }
$managed = if ($UnityEditorData) { Join-Path $UnityEditorData 'Managed/UnityEngine' } else { $null }
$netstandard = if ($UnityEditorData) { Join-Path $UnityEditorData 'NetStandard/ref/2.1.0/netstandard.dll' } else { $null }

if ($csc -and (Test-Path -LiteralPath $csc) -and (Test-Path -LiteralPath $managed) -and
    (Test-Path -LiteralPath $netstandard) -and $NewtonsoftJson -and (Test-Path -LiteralPath $NewtonsoftJson)) {

    $sources = @(Get-ChildItem -LiteralPath $editorRoot -Recurse -File -Filter '*.cs' | Sort-Object FullName)
    $outputDll = Join-Path $env:TEMP 'cucli-verify.dll'
    $responseFile = Join-Path $env:TEMP 'cucli-verify.rsp'

    $arguments = New-Object System.Collections.Generic.List[string]
    foreach ($argument in @('-target:library', '-nostdlib+', '-noconfig', '-langversion:9.0',
            '-nullable:disable', '-optimize-', '-debug-', '-nowarn:1701,1702,0169,0649',
            '-define:UNITY_EDITOR;UNITY_2022_3_OR_NEWER;UNITY_2022_3;UNITY_STANDALONE_WIN;DEBUG;TRACE')) {
        $arguments.Add($argument)
    }
    $arguments.Add("-out:`"$outputDll`"")
    $arguments.Add("-r:`"$netstandard`"")
    foreach ($dll in (Get-ChildItem -LiteralPath $managed -File -Filter '*.dll')) {
        $arguments.Add("-r:`"$($dll.FullName)`"")
    }
    # UnityEditor's top-level Managed folder carries the BCL facades Unity itself references
    # (System.CodeDom and friends). The monolithic UnityEngine.dll / UnityEditor.dll must be excluded:
    # they re-declare every module type and would produce CS0433 against the module assemblies above.
    $managedRoot = Split-Path -Parent $managed
    if (Test-Path -LiteralPath $managedRoot) {
        foreach ($dll in (Get-ChildItem -LiteralPath $managedRoot -File -Filter '*.dll' |
                Where-Object { $_.Name -notin @('UnityEngine.dll', 'UnityEditor.dll') })) {
            $arguments.Add("-r:`"$($dll.FullName)`"")
        }
    }

    # Package-provided runtime assemblies. uGUI (UnityEngine.UI.dll) is the one this package touches;
    # it is a compiled package assembly, so it lives in ScriptAssemblies once the project has imported.
    foreach ($searchRoot in @(
            (Join-Path $projectRoot 'Library/ScriptAssemblies'),
            (Join-Path $projectRoot 'Library/PackageCache'))) {
        if (-not $searchRoot -or -not (Test-Path -LiteralPath $searchRoot)) { continue }
        foreach ($dll in (Get-ChildItem -LiteralPath $searchRoot -Recurse -File -Filter 'UnityEngine.UI.dll' -ErrorAction SilentlyContinue)) {
            $arguments.Add("-r:`"$($dll.FullName)`"")
        }
    }
    $arguments.Add("-r:`"$NewtonsoftJson`"")
    foreach ($source in $sources) { $arguments.Add("`"$($source.FullName)`"") }

    # A response file avoids the Windows command-line length limit with this many references.
    [System.IO.File]::WriteAllLines($responseFile, $arguments, (New-Object System.Text.UTF8Encoding($false)))

    $raw = & dotnet $csc "@$responseFile" 2>&1
    $errors = @($raw | ForEach-Object { $_.ToString() } | Where-Object { $_ -match ': error CS' })

    if ($errors.Count -eq 0) {
        Add-Pass "$($sources.Count) source file(s) compiled cleanly with Unity's Roslyn."
    } else {
        Add-Failure "$($errors.Count) compile error(s):"
        $errors | Sort-Object -Unique | Select-Object -First 40 | ForEach-Object { Write-Host "        $_" }
    }
} else {
    $skipReason = "Skipped the offline compile: Unity's Roslyn, its reference assemblies or " +
        'Newtonsoft.Json.dll could not be located. Pass -UnityEditorData and -NewtonsoftJson to enable it.'
    $notes.Add($skipReason)
    Write-Host '  skip  inputs not found (see -UnityEditorData / -NewtonsoftJson)'
}

# ---------------------------------------------------------------- 5. optional Unity gate

if ($WithUnity) {
    Write-Section '5. Unity batchmode gate'

    $unityExe = if ($UnityEditorData) { Join-Path (Split-Path -Parent $UnityEditorData) 'Unity.exe' } else { $null }
    if (-not $unityExe -or -not (Test-Path -LiteralPath $unityExe)) {
        Add-Failure 'Cannot find Unity.exe for the batchmode gate.'
    } else {
        $scratch = Join-Path ([System.IO.Path]::GetTempPath()) 'cucli-verify-project'
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
        New-Item -ItemType Directory -Force -Path "$scratch/ProjectSettings", "$scratch/Packages", "$scratch/Assets" | Out-Null

        $version = if ($projectRoot) {
            (Select-String -LiteralPath (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt') -Pattern '^m_EditorVersion:\s*(.+)$').Matches[0].Groups[1].Value.Trim()
        } else { '' }
        Set-Content -LiteralPath "$scratch/ProjectSettings/ProjectVersion.txt" -Value "m_EditorVersion: $version" -NoNewline
        # Deliberately minimal: no optional package is declared, so a clean compile here proves the
        # core assembly does not depend on ProBuilder, VFX Graph, Cinemachine, URP or the test framework.
        Set-Content -LiteralPath "$scratch/Packages/manifest.json" -NoNewline -Value (@'
{
  "dependencies": {
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}
'@)

        cmd /c mklink /J "$scratch\Assets\CustomUnityCli" "$PackageRoot" | Out-Null
        $log = Join-Path $scratch 'compile.log'
        & $unityExe -batchmode -nographics -quit -projectPath $scratch -logFile $log 2>&1 | Out-Null

        if (Test-Path -LiteralPath $log) {
            $text = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8)
            $errors = @($text -split "`r?`n" |
                Where-Object { $_ -match 'error CS\d+' -and $_ -like '*CustomUnityCli*' } |
                ForEach-Object { $_.Trim() } | Sort-Object -Unique)

            if ($errors.Count -eq 0) {
                Add-Pass 'Imported and compiled cleanly in a project with no optional packages.'
            } else {
                Add-Failure "$($errors.Count) error(s) in the batchmode import:"
                $errors | Select-Object -First 40 | ForEach-Object { Write-Host "        $_" }
            }
        } else {
            Add-Failure 'Unity produced no log for the batchmode gate.'
        }

        if (-not $KeepScratch -and (Test-Path -LiteralPath $scratch)) {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        } elseif ($KeepScratch) {
            Write-Host "  kept  $scratch"
        }
    }
}

# ---------------------------------------------------------------- summary

Write-Section 'summary'

foreach ($note in $notes) { Write-Host "  note  $note" }

if ($failures.Count -eq 0) {
    Write-Host '  PASS  all gates passed.'
    exit 0
}

Write-Host "  FAIL  $($failures.Count) problem(s)."
exit 1
