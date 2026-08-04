Set-StrictMode -Version Latest

function ConvertTo-GodotProcessArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object Text.StringBuilder
    $null = $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            $null = $builder.Append(('\' * ($backslashes * 2 + 1)))
            $null = $builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            $null = $builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        $null = $builder.Append($character)
    }
    if ($backslashes -gt 0) {
        $null = $builder.Append(('\' * ($backslashes * 2)))
    }
    $null = $builder.Append('"')
    return $builder.ToString()
}

function Stop-GodotProcessTree {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)

    if ($Process.HasExited) {
        return
    }

    $treeKill = $Process.GetType().GetMethod(
        'Kill',
        [type[]]@([bool]))
    if ($null -ne $treeKill) {
        $null = $treeKill.Invoke($Process, @($true))
        return
    }

    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        & taskkill.exe /PID $Process.Id /T /F *> $null
        return
    }

    $Process.Kill()
}

function Invoke-CheckedGodotProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [int]$TimeoutSeconds = 300
    )

    if ($TimeoutSeconds -lt 1) {
        throw 'The Godot process timeout must be positive.'
    }

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['MSBUILDDISABLENODEREUSE'] = '1'
    $start.EnvironmentVariables['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
    $start.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    if ($null -ne $start.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $Arguments) {
            $start.ArgumentList.Add($argument)
        }
    }
    else {
        $start.Arguments = (
            $Arguments |
                ForEach-Object {
                    ConvertTo-GodotProcessArgument -Value $_
                }
        ) -join ' '
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $started = $false
    try {
        if (-not $process.Start()) {
            throw 'The Godot process could not be started.'
        }
        $started = $true
        $stdoutRead = $process.StandardOutput.ReadToEndAsync()
        $stderrRead = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-GodotProcessTree -Process $process
            if (-not $process.WaitForExit(10000)) {
                throw 'The timed-out Godot process did not terminate within 10 seconds.'
            }
            $null = $stdoutRead.GetAwaiter().GetResult()
            $null = $stderrRead.GetAwaiter().GetResult()
            throw "The Godot process exceeded $TimeoutSeconds seconds."
        }

        $stdout = $stdoutRead.GetAwaiter().GetResult()
        $stderr = $stderrRead.GetAwaiter().GetResult()
        $outputParts = @($stdout, $stderr) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $script:LastGodotOutput = $outputParts -join "`n"
        if (-not [string]::IsNullOrEmpty($stdout)) {
            Write-Host $stdout.TrimEnd()
        }
        if (-not [string]::IsNullOrEmpty($stderr)) {
            Write-Host $stderr.TrimEnd()
        }
        return $process.ExitCode
    }
    finally {
        if ($started -and -not $process.HasExited) {
            Stop-GodotProcessTree -Process $process
        }
        $process.Dispose()
    }
}
