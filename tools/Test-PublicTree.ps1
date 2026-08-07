[CmdletBinding()]
param(
    [string] $AdditionalDenyRegex = $env:OPEN_GAME_AGENT_RELEASE_DENY_REGEX
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$paths = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate repository files.'
}

$failures = [Collections.Generic.List[string]]::new()
$validatedPathCount = 0
$blockedPaths = '(?i)(^|/)(bin|obj|artifacts|Library|Temp|Logs|\.godot)(/|$)|(^|/)\.env($|\.)'
$defaultContentDeny = @(
    '(?i)C:\\Users\\[^\\\s]+',
    '(?i)-----BEGIN [A-Z ]*PRIVATE KEY-----',
    '(?i)(api[_-]?key|secret|token)\s*[:=]\s*["''][A-Za-z0-9_\-]{24,}["'']'
)

foreach ($relative in $paths) {
    $normalized = $relative.Replace('\', '/')
    $fullPath = Join-Path $repositoryRoot $relative
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $validatedPathCount++

    if ($normalized -match $blockedPaths) {
        $failures.Add("generated or private path: $normalized")
        continue
    }

    $file = Get-Item -Force -LiteralPath $fullPath
    if ($file.Length -gt 10MB) {
        $failures.Add("file exceeds 10 MiB: $normalized")
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Contains([byte]0)) {
        continue
    }

    $text = [Text.Encoding]::UTF8.GetString($bytes)
    foreach ($pattern in $defaultContentDeny) {
        if ($text -match $pattern) {
            $failures.Add("sensitive content pattern: $normalized")
            break
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($AdditionalDenyRegex) -and $text -match $AdditionalDenyRegex) {
        $failures.Add("release-deny expression: $normalized")
    }
}

if ($failures.Count -gt 0) {
    throw "Public-tree validation failed:`n$($failures -join "`n")"
}

Write-Output "Validated $validatedPathCount existing repository paths."
