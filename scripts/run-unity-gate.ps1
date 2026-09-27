param(
    [Parameter(Mandatory = $true)]
    [string]$EditorPath,

    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
    throw 'Unity editor executable does not exist.'
}

$editorArguments = @(
    '-batchmode',
    '-nographics',
    '-projectPath',
    ('"{0}"' -f $ProjectPath),
    '-executeMethod',
    'OpenGameAgentUnityGate.Run',
    '-logFile',
    ('"{0}"' -f $LogPath)
)

$process = Start-Process `
    -FilePath $EditorPath `
    -ArgumentList $editorArguments `
    -WindowStyle Hidden `
    -Wait `
    -PassThru

exit $process.ExitCode
