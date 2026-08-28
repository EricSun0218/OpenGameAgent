param(
    [string]$Unity2022Editor = $env:UNITY_2022_EDITOR,
    [string]$Unity6Editor = $env:UNITY_6_EDITOR
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Unity2022Editor) -or [string]::IsNullOrWhiteSpace($Unity6Editor)) {
    throw "Set UNITY_2022_EDITOR and UNITY_6_EDITOR, or pass both editor paths explicitly."
}

$targets = @(
    @{
        Editor = $Unity2022Editor
        Project = "$PSScriptRoot/TestProjects/Unity2022"
        Log = "$PSScriptRoot/TestProjects/Unity2022/unity.log"
    },
    @{
        Editor = $Unity6Editor
        Project = "$PSScriptRoot/TestProjects/Unity6"
        Log = "$PSScriptRoot/TestProjects/Unity6/unity.log"
    }
)

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target.Editor)) {
        throw "Unity editor was not found: $($target.Editor)"
    }
    $projectPath = [IO.Path]::GetFullPath($target.Project)
    $projectParent = Split-Path -Parent $projectPath
    $projectName = Split-Path -Leaf $projectPath
    Push-Location $projectParent
    try {
        $process = Start-Process -FilePath $target.Editor -ArgumentList @(
            "-batchmode",
            "-nographics",
            "-quit",
            "-projectPath",
            $projectName,
            "-logFile",
            "$projectName/unity.log"
        ) -PassThru -WindowStyle Hidden
        $process.WaitForExit()
        $process.Refresh()
    }
    finally {
        Pop-Location
    }
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $target.Log -Tail 200
        exit $process.ExitCode
    }
    $errors = Select-String -LiteralPath $target.Log -Pattern "error CS[0-9]+|Compilation failed|Scripts have compiler errors"
    if ($errors) {
        $errors | ForEach-Object { $_.Line }
        exit 1
    }
}
