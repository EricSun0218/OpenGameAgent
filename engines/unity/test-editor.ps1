[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $UnityEditor,
    [Parameter(Mandatory = $true)]
    [string] $UnityManagedDir,
    [switch] $KeepProject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) {
    throw 'UnityEditor must point to Unity.exe.'
}

$engineRoot = Split-Path -Parent $PSCommandPath
$packageOutput = @(& (Join-Path $engineRoot 'test-package.ps1') -UnityManagedDir $UnityManagedDir)
$packagePath = [string]$packageOutput[-1]
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenGameAgent.Unity.Tests\' + [Guid]::NewGuid().ToString('N'))
$createLog = $testRoot + '.create.log'
$testParent = Split-Path -Parent $testRoot
New-Item -ItemType Directory -Path $testParent -Force | Out-Null
$creation = Start-Process `
    -FilePath $UnityEditor `
    -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', $testRoot, '-logFile', $createLog) `
    -PassThru `
    -WindowStyle Hidden
$creation.WaitForExit()
$creationExitCode = $creation.ExitCode
$creation.Dispose()
if ($creationExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $testRoot 'Assets') -PathType Container)) {
    $creationDetails = if (Test-Path -LiteralPath $createLog) { Get-Content -LiteralPath $createLog -Raw } else { 'No creation log was written.' }
    throw "Unity could not create the smoke project. $creationDetails"
}

$assetsEditor = Join-Path $testRoot 'Assets\Editor'
$assetsSample = Join-Path $testRoot 'Assets\OpenGameAgentSample'
$packages = Join-Path $testRoot 'Packages'
New-Item -ItemType Directory -Path $assetsEditor, $assetsSample -Force | Out-Null
try {
    $packageUri = 'file:' + ([IO.Path]::GetFullPath($packagePath).Replace('\', '/'))
    $manifestPath = Join-Path $packages 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.dependencies | Add-Member -NotePropertyName 'com.opengameagent.runtime' -NotePropertyValue $packageUri -Force
    $manifest | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Copy-Item `
        -LiteralPath (Join-Path $packagePath 'Samples~\Minimal Local Agent\OpenGameAgentQuickstart.cs') `
        -Destination (Join-Path $assetsSample 'OpenGameAgentQuickstart.cs')
    @'
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent;
using OpenGameAgent.Kernel;
using OpenGameAgent.Unity;
using UnityEditor;
using UnityEngine;

public static class OpenGameAgentEditorSmoke
{
    public static void Run()
    {
        var input = new GameInput(
            "session",
            "actor",
            "observation",
            "{\"position\":[1.5,2.25],\"visible\":true}",
            new GameMoment("world", 7),
            "unity-smoke");
        var roundTrip = GameAgentWire.ParseInput(GameAgentWire.SerializeInput(input));
        if (!roundTrip.PayloadJson.Contains("2.25", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Structured input did not round-trip.");
        }

        var gameObject = new GameObject("OpenGameAgent smoke");
        var host = gameObject.AddComponent<OpenGameAgentBehaviour>();
        host.Configure(new GameAgentRuntime(new GameAgentRuntimeOptions(new SmokeProvider(), "smoke")));
        string completed = null;
        string failed = null;
        host.RunCompleted.AddListener((inputId, result) =>
        {
            if (string.Equals(inputId, "unity-smoke", StringComparison.Ordinal))
            {
                completed = result;
            }
        });
        host.RunFailed.AddListener((inputId, error) => failed = inputId + ": " + error);
        var returnedId = host.RunJson(GameAgentWire.SerializeInput(input));
        if (!string.Equals(returnedId, input.InputId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Unity adapter returned the wrong run ID.");
        }

        for (var attempt = 0; attempt < 1000 && completed == null && failed == null; attempt++)
        {
            host.PumpCallbacks();
            Thread.Sleep(5);
        }

        if (failed != null)
        {
            throw new InvalidOperationException("The Unity adapter run failed: " + failed);
        }

        if (completed == null)
        {
            throw new TimeoutException("The Unity adapter did not deliver a terminal callback.");
        }

        using (var document = JsonDocument.Parse(completed))
        {
            if (document.RootElement.GetProperty("status").GetString() != "Completed")
            {
                throw new InvalidOperationException("The Unity adapter returned an invalid completion result.");
            }
        }

        UnityEngine.Object.DestroyImmediate(gameObject);
        Debug.Log("OPENGAMEAGENT_UNITY_SMOKE_OK");
    }

    private sealed class SmokeProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("unity-ok") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }
    }
}
'@ | Set-Content -LiteralPath (Join-Path $assetsEditor 'OpenGameAgentEditorSmoke.cs') -Encoding utf8NoBOM

    $logPath = Join-Path $testRoot 'unity-smoke.log'
    $run = Start-Process `
        -FilePath $UnityEditor `
        -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', '.', '-executeMethod', 'OpenGameAgentEditorSmoke.Run', '-logFile', 'unity-smoke.log') `
        -WorkingDirectory $testRoot `
        -PassThru `
        -WindowStyle Hidden
    $run.WaitForExit()
    $runExitCode = $run.ExitCode
    $run.Dispose()
    if ($runExitCode -ne 0) {
        $failureLog = if (Test-Path -LiteralPath $logPath) {
            Get-Content -LiteralPath $logPath -Tail 200 | Out-String
        }
        else {
            'Unity did not write a smoke log.'
        }
        throw "Unity Editor smoke test failed with exit code $runExitCode. $failureLog"
    }

    $log = Get-Content -LiteralPath $logPath -Raw
    if (-not $log.Contains('OPENGAMEAGENT_UNITY_SMOKE_OK', [StringComparison]::Ordinal)) {
        throw 'Unity Editor did not execute the OpenGameAgent smoke method successfully.'
    }
}
finally {
    if (Test-Path -LiteralPath $createLog) {
        Remove-Item -LiteralPath $createLog -Force -ErrorAction SilentlyContinue
    }
    if ($KeepProject) {
        Write-Verbose "Unity smoke project retained at $testRoot"
    }
    elseif (Test-Path -LiteralPath $testRoot) {
        try {
            [IO.Directory]::Delete($testRoot, $true)
        }
        catch {
            Write-Warning "Unity smoke passed, but its temporary project could not be removed: $testRoot"
        }
    }
}
