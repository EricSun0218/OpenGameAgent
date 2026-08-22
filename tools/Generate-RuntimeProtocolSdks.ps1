[CmdletBinding()]
param([switch] $Check)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$protocolRoot = Join-Path $repositoryRoot 'protocol\runtime\v1'
$schemaPath = Join-Path $protocolRoot 'runtime.schema.json'
$schemaText = Get-Content -LiteralPath $schemaPath -Raw
$schema = $schemaText | ConvertFrom-Json -Depth 128
$schemaHash = (Get-FileHash -LiteralPath $schemaPath -Algorithm SHA256).Hash.ToLowerInvariant()

function Get-EnumValues([string] $definition, [string] $property) {
    $values = $schema.'$defs'.$definition.properties.$property.enum
    if ($null -eq $values -or $values.Count -eq 0) {
        throw "Schema enum '$definition.$property' is missing."
    }

    @($values | ForEach-Object { [string]$_ })
}

$eventKinds = Get-EnumValues 'eventEnvelope' 'eventKind'
$lifecycles = Get-EnumValues 'eventEnvelope' 'lifecycle'
$controlStatuses = Get-EnumValues 'controlResponse' 'status'
$itemKinds = @($schema.'$defs'.eventEnvelope.properties.itemKind.oneOf[0].enum | ForEach-Object { [string]$_ })
if ($itemKinds.Count -eq 0) { throw 'Schema item-kind enum is missing.' }

$replacements = [ordered]@{
    '@SCHEMA_SHA256@' = $schemaHash
    '@EVENT_KIND_UNION@' = ($eventKinds | ForEach-Object { "'$_'" }) -join ' | '
    '@EVENT_KIND_ARRAY@' = ($eventKinds | ForEach-Object { "'$_'" }) -join ', '
    '@EVENT_KIND_PY@' = ($eventKinds | ForEach-Object { "'$_'" }) -join ', '
    '@ITEM_KIND_UNION@' = ($itemKinds | ForEach-Object { "'$_'" }) -join ' | '
    '@ITEM_KIND_ARRAY@' = ($itemKinds | ForEach-Object { "'$_'" }) -join ', '
    '@ITEM_KIND_PY@' = ($itemKinds | ForEach-Object { "'$_'" }) -join ', '
    '@LIFECYCLE_UNION@' = ($lifecycles | ForEach-Object { "'$_'" }) -join ' | '
    '@LIFECYCLE_ARRAY@' = ($lifecycles | ForEach-Object { "'$_'" }) -join ', '
    '@LIFECYCLE_PY@' = ($lifecycles | ForEach-Object { "'$_'" }) -join ', '
    '@CONTROL_STATUS_UNION@' = ($controlStatuses | ForEach-Object { "'$_'" }) -join ' | '
    '@CONTROL_STATUS_ARRAY@' = ($controlStatuses | ForEach-Object { "'$_'" }) -join ', '
    '@CONTROL_STATUS_PY@' = ($controlStatuses | ForEach-Object { "'$_'" }) -join ', '
}

$outputs = @(
    @{
        Template = Join-Path $PSScriptRoot 'runtime-protocol-sdk\typescript.template'
        Output = Join-Path $protocolRoot 'typescript\src\index.ts'
    },
    @{
        Template = Join-Path $PSScriptRoot 'runtime-protocol-sdk\python.template'
        Output = Join-Path $protocolRoot 'python\opengameagent_runtime_protocol\__init__.py'
    }
)

foreach ($entry in $outputs) {
    $expected = Get-Content -LiteralPath $entry.Template -Raw
    foreach ($replacement in $replacements.GetEnumerator()) {
        $expected = $expected.Replace($replacement.Key, $replacement.Value, [StringComparison]::Ordinal)
    }

    $expected = $expected.Replace("`r`n", "`n", [StringComparison]::Ordinal)
    if ($Check) {
        if (-not (Test-Path -LiteralPath $entry.Output -PathType Leaf)) {
            throw "Generated Runtime Protocol SDK '$($entry.Output)' is missing."
        }

        $actual = (Get-Content -LiteralPath $entry.Output -Raw).Replace("`r`n", "`n", [StringComparison]::Ordinal)
        if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) {
            throw "Generated Runtime Protocol SDK '$($entry.Output)' is stale."
        }
    }
    else {
        $parent = Split-Path -Parent $entry.Output
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [IO.File]::WriteAllText($entry.Output, $expected, [Text.UTF8Encoding]::new($false))
    }
}

if ($Check) {
    Write-Output "Runtime Protocol SDKs match schema SHA-256 $schemaHash."
}
