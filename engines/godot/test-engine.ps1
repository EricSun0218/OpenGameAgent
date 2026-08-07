[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Godot,
    [Parameter(Mandatory = $true)]
    [string] $GodotSharpDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw 'Godot must point to a Godot .NET executable.'
}

$scriptRoot = Split-Path -Parent $PSCommandPath
$packageOutput = @(& (Join-Path $scriptRoot 'test-package.ps1') -GodotSharpDir $GodotSharpDir)
$packageRoot = [string]$packageOutput[-1]
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenGameAgent.Godot.Tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $packageRoot 'addons') -Destination (Join-Path $testRoot 'addons') -Recurse
    @'
[application]
config/name="OpenGameAgent Smoke"
run/main_scene="res://Main.tscn"

[display]
window/size/viewport_width=320
window/size/viewport_height=180

[dotnet]
project/assembly_name="OpenGameAgent.Godot.Smoke"

[rendering]
renderer/rendering_method="gl_compatibility"
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'project.godot') -Encoding utf8NoBOM
    @'
<Project Sdk="Godot.NET.Sdk/4.7.1">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>OpenGameAgent.Godot.Smoke</RootNamespace>
  </PropertyGroup>
  <Import Project="addons\open_game_agent\OpenGameAgent.Godot.props" />
</Project>
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'OpenGameAgent.Godot.Smoke.csproj') -Encoding utf8NoBOM
    @'
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenGameAgent;
using OpenGameAgent.Godot;
using OpenGameAgent.Kernel;

public partial class Main : Node
{
    private int _frames;

    public override void _Ready()
    {
        var input = new GameInput(
            "session",
            "actor",
            "observation",
            "{\"position\":[1.5,2.25],\"visible\":true}",
            new GameMoment("world", 7),
            "godot-smoke");
        var roundTrip = GameAgentWire.ParseInput(GameAgentWire.SerializeInput(input));
        if (!roundTrip.PayloadJson.Contains("2.25", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Structured input did not round-trip.");
        }

        var host = new OpenGameAgentNode();
        AddChild(host);
        host.Connect(OpenGameAgentNode.RunCompletedSignal, Callable.From<string, string>(OnCompleted));
        host.Connect(OpenGameAgentNode.RunFailedSignal, Callable.From<string, string>(OnFailed));
        host.Configure(new GameAgentRuntime(new GameAgentRuntimeOptions(new SmokeProvider(), "smoke")));
        var returnedId = host.RunJson(GameAgentWire.SerializeInput(input));
        if (!string.Equals(returnedId, input.InputId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Godot adapter returned the wrong run ID.");
        }
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (++_frames > 1200)
        {
            throw new TimeoutException("The Godot adapter did not deliver a terminal signal.");
        }
    }

    private void OnCompleted(string inputId, string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        if (!string.Equals(inputId, "godot-smoke", StringComparison.Ordinal)
            || document.RootElement.GetProperty("status").GetString() != "Completed")
        {
            throw new InvalidOperationException("The Godot adapter returned an invalid completion result.");
        }

        GD.Print("OPENGAMEAGENT_GODOT_SMOKE_OK");
        GetTree().Quit(0);
    }

    private static void OnFailed(string inputId, string error)
    {
        throw new InvalidOperationException($"Godot adapter run '{inputId}' failed: {error}");
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
                new AgentContent[] { new TextContent("godot-ok") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
        }
    }
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Main.cs') -Encoding utf8NoBOM
    @'
[gd_scene load_steps=2 format=3]

[ext_resource path="res://Main.cs" type="Script" id="1"]

[node name="Main" type="Node"]
script = ExtResource("1")
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Main.tscn') -Encoding utf8NoBOM

    & dotnet build (Join-Path $testRoot 'OpenGameAgent.Godot.Smoke.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Godot smoke project build failed.' }
    & $Godot --headless --path $testRoot --editor --quit
    if ($LASTEXITCODE -ne 0) { throw 'Godot editor import failed.' }
    & $Godot --headless --path $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Godot runtime smoke test failed.' }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
