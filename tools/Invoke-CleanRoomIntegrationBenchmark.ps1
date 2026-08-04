[CmdletBinding()]
param(
    [ValidateSet('godot', 'unity', 'all')]
    [string]$Engine = 'all',
    [string]$JsonPath = 'artifacts/clean-room-integration.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedJson = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $JsonPath))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $resolvedJson.StartsWith(
        $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'JsonPath must stay inside the repository artifacts directory.'
}

$temporaryRoot = [IO.Path]::Combine(
    [IO.Path]::GetTempPath(),
    'game-agent-clean-room-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$checks = [Collections.Generic.List[object]]::new()

function Invoke-Checked {
    param([string]$Name, [scriptblock]$Action)
    $started = [Diagnostics.Stopwatch]::StartNew()
    try {
        $global:LASTEXITCODE = 0
        & $Action
        if ($LASTEXITCODE -ne 0) {
            throw "Command exited with code $LASTEXITCODE."
        }
        $checks.Add([ordered]@{
            name = $Name
            passed = $true
            durationMs = $started.ElapsedMilliseconds
        })
    }
    catch {
        $checks.Add([ordered]@{
            name = $Name
            passed = $false
            durationMs = $started.ElapsedMilliseconds
            error = $_.Exception.Message
        })
        throw
    }
}

try {
    $manifest = Get-Content -LiteralPath (
        Join-Path $repositoryRoot 'docs\integration-benchmark.json') -Raw |
        ConvertFrom-Json
    if ($manifest.requiredSlices.Count -ne 6) {
        throw 'The public integration benchmark manifest is incomplete.'
    }

    if ($Engine -in @('godot', 'all')) {
        $archiveOutput = & (Join-Path $repositoryRoot `
            'engines\godot\tools\package-addon.ps1') -Configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Godot package build failed.' }
        $archive = @($archiveOutput)[-1]
        $consumer = Join-Path $temporaryRoot 'GodotConsumer'
        Invoke-Checked 'godot-scaffold' {
            & (Join-Path $repositoryRoot 'tools\New-GameAgentGodotProject.ps1') `
                -Destination $consumer `
                -ProjectName 'Clean Room Consumer' `
                -PackageArchive $archive | Out-Null
        }
        Invoke-Checked 'godot-consumer-build' {
            $project = Get-ChildItem -LiteralPath $consumer -Filter '*.csproj' |
                Select-Object -First 1
            & dotnet build $project.FullName -c Release -m:1
        }
        $projectText = Get-Content -LiteralPath (
            (Get-ChildItem -LiteralPath $consumer -Filter '*.csproj' |
                Select-Object -First 1).FullName) -Raw
        if ($projectText -match 'ProjectReference') {
            throw 'The Godot clean-room consumer referenced repository source.'
        }
    }

    if ($Engine -in @('unity', 'all')) {
        Invoke-Checked 'unity-package-build' {
            & (Join-Path $repositoryRoot `
                'engines\unity\scripts\Build-UpmPackage.ps1') `
                -Configuration Release -Force
        }
        $unityArtifact = Join-Path $repositoryRoot `
            'engines\unity\artifacts\com.gameagent.runtime.unity'
        Invoke-Checked 'unity-artifact-contract' {
            & (Join-Path $repositoryRoot `
                'engines\unity\scripts\Test-UnityPackageArtifact.ps1') `
                -ArtifactPath $unityArtifact
        }
        Invoke-Checked 'unity-artifact-consumer-build' {
            & dotnet build (Join-Path $repositoryRoot `
                'tests\UnityCompileSmoke\UnityCompileSmoke.csproj') `
                -c Release -m:1 -p:UnityArtifactPath=$unityArtifact
        }
    }
}
finally {
    $result = [ordered]@{
        schemaVersion = '1'
        engine = $Engine
        passed = ($checks.Count -gt 0 -and @($checks | Where-Object {
            -not $_.passed
        }).Count -eq 0)
        checks = $checks
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedJson)) |
        Out-Null
    [IO.File]::WriteAllText(
        $resolvedJson,
        ($result | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $requiredPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar +
        'game-agent-clean-room-'
    if ($resolvedTemporary.StartsWith(
            $requiredPrefix,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporary -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

if (@($checks | Where-Object { -not $_.passed }).Count -ne 0) {
    throw "Clean-room integration benchmark failed. See '$resolvedJson'."
}
Write-Output $resolvedJson
