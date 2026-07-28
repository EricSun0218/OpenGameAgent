param(
    [string]$Godot = "godot",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

function Resolve-GodotExecutable {
    param([string]$Command)

    $resolved = Get-Command $Command -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Source
    if ($item.LinkType -and $item.Target) {
        return [string]$item.Target[0]
    }

    return $resolved.Source
}

function Invoke-Godot {
    param([string[]]$Arguments)

    $executable = Resolve-GodotExecutable $Godot
    $token = [System.Guid]::NewGuid().ToString("N")
    $stdout = Join-Path ([System.IO.Path]::GetTempPath()) "game-agent-godot-$token.out"
    $stderr = Join-Path ([System.IO.Path]::GetTempPath()) "game-agent-godot-$token.err"
    $argumentLine = ($Arguments | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }) -join " "

    try {
        $startParameters = @{
            FilePath = $executable
            ArgumentList = $argumentLine
            RedirectStandardOutput = $stdout
            RedirectStandardError = $stderr
            Wait = $true
            PassThru = $true
        }
        if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
            $startParameters.WindowStyle = "Hidden"
        }
        $process = Start-Process @startParameters
        $stdoutLines = @(
            Get-Content -LiteralPath $stdout -Encoding utf8 -ErrorAction SilentlyContinue
        )
        $stderrLines = @(
            Get-Content -LiteralPath $stderr -Encoding utf8 -ErrorAction SilentlyContinue
        )
        $script:LastGodotOutput = ($stdoutLines + $stderrLines) -join "`n"
        $stdoutLines | ForEach-Object { Write-Host $_ }
        $stderrLines | ForEach-Object { Write-Host $_ }
        return $process.ExitCode
    }
    finally {
        Remove-Item -LiteralPath $stdout -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderr -Force -ErrorAction SilentlyContinue
    }
}

if (-not $SkipBuild) {
    $buildExitCode = Invoke-Godot @(
        "--headless",
        "--path",
        $projectRoot,
        "--build-solutions",
        "--quit-after",
        "10")
    if ($buildExitCode -ne 0) {
        throw "Godot C# solution build failed with exit code $buildExitCode."
    }
}

$testExitCode = Invoke-Godot @(
    "--headless",
    "--path",
    $projectRoot,
    "--scene",
    "res://tests/HeadlessAddonTest.tscn",
    "--quit-after",
    "100000")
if ($testExitCode -ne 0) {
    throw "Godot headless addon tests failed with exit code $testExitCode."
}
if ($script:LastGodotOutput -notmatch "GODOT_TEST_PASS") {
    throw "Godot headless addon tests exited without the GODOT_TEST_PASS marker."
}

$sampleExitCode = Invoke-Godot @(
    "--headless",
    "--path",
    $projectRoot,
    "--scene",
    "res://samples/basic/BasicSample.tscn",
    "--quit-after",
    "100000")
if ($sampleExitCode -ne 0) {
    throw "Godot basic sample smoke failed with exit code $sampleExitCode."
}
if ($script:LastGodotOutput -notmatch "GODOT_SAMPLE_PASS") {
    throw "Godot basic sample exited without the GODOT_SAMPLE_PASS marker."
}
