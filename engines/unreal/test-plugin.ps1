[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnrealRoot,
    [switch]$KeepProject
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$UnrealRoot = [IO.Path]::GetFullPath($UnrealRoot)
$engineRoot = Split-Path -Parent $PSCommandPath
$packageOutput = @(& (Join-Path $engineRoot 'test-package.ps1'))
$packageRoot = [string]$packageOutput[-1]
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenGameAgent.Unreal.Tests\' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $testRoot 'OpenGameAgentUnrealTests.uproject'
$plugins = Join-Path $testRoot 'Plugins'
$log = Join-Path $testRoot 'Saved\Logs\OpenGameAgentAutomation.log'
$build = Join-Path $UnrealRoot 'Engine\Build\BatchFiles\Build.bat'
$editor = Join-Path $UnrealRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'

foreach ($required in @($build, $editor)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required Unreal file not found: $required"
    }
}

New-Item -ItemType Directory -Path $plugins -Force | Out-Null
try {
    Copy-Item -LiteralPath $packageRoot -Destination (Join-Path $plugins 'OpenGameAgent') -Recurse
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'Content') | Out-Null
    @'
{
  "FileVersion": 3,
  "EngineAssociation": "5.8",
  "Category": "",
  "Description": "Headless OpenGameAgent Unreal plugin verification project.",
  "Plugins": [
    {
      "Name": "OpenGameAgent",
      "Enabled": true
    }
  ]
}
'@ | Set-Content -LiteralPath $project -Encoding utf8NoBOM

    & $build UnrealEditor Win64 Development "-Project=$project" -WaitMutex -NoHotReloadFromIDE
    if ($LASTEXITCODE -ne 0) {
        throw "Unreal Build Tool failed with exit code $LASTEXITCODE."
    }

    & $editor $project -unattended -nop4 -nosplash -nullrhi `
        '-ExecCmds=Automation RunTests OpenGameAgent.Unreal; Quit' `
        '-TestExit=Automation Test Queue Empty' `
        "-abslog=$log"
    if ($LASTEXITCODE -ne 0) {
        throw "Unreal automation failed with exit code $LASTEXITCODE. See $log"
    }

    $text = Get-Content -LiteralPath $log -Raw
    $successfulTests = [regex]::Matches(
        $text,
        'Test Completed\. Result=\{Success\}.*Path=\{OpenGameAgent\.Unreal\.')
    if ($text -notmatch '\*\*\*\* TEST COMPLETE\. EXIT CODE: 0 \*\*\*\*' -or
        $successfulTests.Count -ne 2) {
        throw "Unreal automation did not report success. See $log"
    }

    Write-Output 'OPENGAMEAGENT_UNREAL_SMOKE_OK'
}
finally {
    if ($KeepProject) {
        Write-Verbose "Unreal smoke project retained at $testRoot"
    }
    elseif (Test-Path -LiteralPath $testRoot) {
        try {
            [IO.Directory]::Delete($testRoot, $true)
        }
        catch {
            Write-Warning "Unreal smoke passed, but its temporary project could not be removed: $testRoot"
        }
    }
}
