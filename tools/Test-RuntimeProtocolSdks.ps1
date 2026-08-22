[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Generate-RuntimeProtocolSdks.ps1') -Check

$typescript = Join-Path $repositoryRoot 'protocol\runtime\v1\typescript'
& npm ci --ignore-scripts --prefix $typescript
if ($LASTEXITCODE -ne 0) { throw 'The TypeScript Runtime Protocol dependencies failed locked restore.' }
& npm run check --prefix $typescript
if ($LASTEXITCODE -ne 0) { throw 'The TypeScript Runtime Protocol type check failed.' }
& npm run build --prefix $typescript
if ($LASTEXITCODE -ne 0) { throw 'The TypeScript Runtime Protocol build failed.' }

& node (Join-Path $repositoryRoot 'protocol\runtime\v1\typescript\test\consumer.ts')
if ($LASTEXITCODE -ne 0) { throw 'The TypeScript Runtime Protocol clean consumer failed.' }

& python (Join-Path $repositoryRoot 'protocol\runtime\v1\python\tests\consumer.py')
if ($LASTEXITCODE -ne 0) { throw 'The Python Runtime Protocol clean consumer failed.' }

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenGameAgent.RuntimeSdk.Tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $nodeConsumer = Join-Path $temporaryRoot 'node-consumer'
    New-Item -ItemType Directory -Path $nodeConsumer -Force | Out-Null
    & npm pack $typescript --pack-destination $temporaryRoot --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Packing the TypeScript Runtime Protocol SDK failed.' }
    $tarball = Get-ChildItem -LiteralPath $temporaryRoot -Filter '*.tgz' | Select-Object -First 1
    if ($null -eq $tarball) { throw 'The TypeScript Runtime Protocol tarball was not created.' }
    & npm install --ignore-scripts --prefix $nodeConsumer $tarball.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Installing the packed TypeScript Runtime Protocol SDK failed.' }
    @'
import { PROTOCOL_VERSION, RuntimeReducer } from '@opengameagent/runtime-protocol';
if (PROTOCOL_VERSION !== 1 || typeof RuntimeReducer !== 'function') throw new Error('packed TypeScript SDK failed');
console.log('OPENGAMEAGENT_RUNTIME_TYPESCRIPT_PACKAGE_OK');
'@ | Set-Content -LiteralPath (Join-Path $nodeConsumer 'consumer.mjs') -Encoding utf8NoBOM
    & node (Join-Path $nodeConsumer 'consumer.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'The packed TypeScript Runtime Protocol consumer failed.' }

    $pythonRoot = Join-Path $repositoryRoot 'protocol\runtime\v1\python'
    $wheelRoot = Join-Path $temporaryRoot 'wheels'
    $pythonTarget = Join-Path $temporaryRoot 'python-consumer'
    New-Item -ItemType Directory -Path $wheelRoot, $pythonTarget -Force | Out-Null
    & python -m pip wheel --disable-pip-version-check --no-deps --wheel-dir $wheelRoot $pythonRoot
    if ($LASTEXITCODE -ne 0) { throw 'Packing the Python Runtime Protocol SDK failed.' }
    $wheel = Get-ChildItem -LiteralPath $wheelRoot -Filter '*.whl' | Select-Object -First 1
    if ($null -eq $wheel) { throw 'The Python Runtime Protocol wheel was not created.' }
    & python -m pip install --disable-pip-version-check --no-deps --target $pythonTarget $wheel.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Installing the packed Python Runtime Protocol SDK failed.' }
    $previousPythonPath = $env:PYTHONPATH
    try {
        $env:PYTHONPATH = $pythonTarget
        & python -c "from opengameagent_runtime_protocol import PROTOCOL_VERSION, RuntimeReducer; assert PROTOCOL_VERSION == 1; assert RuntimeReducer is not None; print('OPENGAMEAGENT_RUNTIME_PYTHON_PACKAGE_OK')"
        if ($LASTEXITCODE -ne 0) { throw 'The packed Python Runtime Protocol consumer failed.' }
    }
    finally {
        $env:PYTHONPATH = $previousPythonPath
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
