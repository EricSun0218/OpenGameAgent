[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$WorkingPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'The normalized NuGet consumer check failed.'
    }
}

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]{0,127}$') {
    throw 'The normalized NuGet consumer version is invalid.'
}

$packageRoot = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $PackagePath))
$packageItem = Get-Item -LiteralPath $packageRoot
if (-not $packageItem.PSIsContainer -or
    ($packageItem.Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The normalized NuGet consumer source must be a regular directory.'
}
if (@(Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.nupkg').
        Count -eq 0) {
    throw 'The normalized NuGet consumer source contains no packages.'
}

$workingRoot = [IO.Path]::GetFullPath($WorkingPath)
if (Test-Path -LiteralPath $workingRoot) {
    throw 'The normalized NuGet consumer working path already exists.'
}

Invoke-Dotnet -Arguments @(
    'new',
    'console',
    '--framework',
    'net8.0',
    '--output',
    $workingRoot,
    '--no-restore')

$projectName = [IO.Path]::GetFileName($workingRoot)
$projectPath = Join-Path $workingRoot ($projectName + '.csproj')
if (-not [IO.File]::Exists($projectPath)) {
    throw 'The normalized NuGet consumer project was not created.'
}

Invoke-Dotnet -Arguments @(
    'add',
    $projectPath,
    'package',
    'GameAgent.Runtime',
    '--version',
    $Version,
    '--no-restore')
Invoke-Dotnet -Arguments @(
    'add',
    $projectPath,
    'package',
    'GameAgent.Testing',
    '--version',
    $Version,
    '--no-restore')
Invoke-Dotnet -Arguments @(
    'add',
    $projectPath,
    'package',
    'GameAgent.Providers.Anthropic',
    '--version',
    $Version,
    '--no-restore')
Invoke-Dotnet -Arguments @(
    'add',
    $projectPath,
    'package',
    'GameAgent.Workflow',
    '--version',
    $Version,
    '--no-restore')

$consumerSource = @'
using GameAgent.Core;
using GameAgent.Compatibility;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Providers.Anthropic;
using GameAgent.Providers.OpenAICompatible;
using GameAgent.Runtime;
using GameAgent.Testing;
using GameAgent.Workflow;
using GameAgent.World;

Type[] shippedTypes =
[
    typeof(AgentRun),
    typeof(CompatibilityImporter),
    typeof(HeadlessAgentRuntimeLimits),
    typeof(FileJournalOptions),
    typeof(AnthropicProviderOptions),
    typeof(OpenAiCompatibleProviderOptions),
    typeof(ProviderRequestPreparationChanges),
    typeof(ConsumerRequestAdapter),
    typeof(GameAgentRuntimeBuilder),
    typeof(FakeRuntimeClock),
    typeof(WorkflowCompiler),
    typeof(WorldPackageDefinition)
];
Console.WriteLine(string.Join(Environment.NewLine, shippedTypes.Select(
    static type => type.Assembly.FullName)));

sealed class ConsumerRequestAdapter : IProviderRequestAdapter
{
    public ValueTask<ProviderPreparedRequest> PrepareRequestAsync(
        ProviderRequestPreparationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Request;
        var output = new StreamingModelRequest
        {
            RunId = source.RunId,
            RunAttemptId = source.RunAttemptId,
            TurnId = source.TurnId,
            ProviderAttemptId = source.ProviderAttemptId,
            StreamAttemptId = source.StreamAttemptId,
            Messages = source.Messages,
            Tools = source.Tools,
            MaxOutputTokens = source.MaxOutputTokens
        };
        return new ValueTask<ProviderPreparedRequest>(
            context.CreatePreparedRequest(
                output,
                ProviderRequestPreparationChanges.None,
                cancellationToken));
    }
}
'@
[IO.File]::WriteAllText(
    (Join-Path $workingRoot 'Program.cs'),
    $consumerSource,
    [Text.UTF8Encoding]::new($false))

$escapedPackageRoot = [Security.SecurityElement]::Escape($packageRoot)
$configuration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="release" value="$escapedPackageRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json"
         protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="release">
      <package pattern="GameAgent.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
$configurationPath = Join-Path $workingRoot 'NuGet.Config'
[IO.File]::WriteAllText(
    $configurationPath,
    $configuration,
    [Text.UTF8Encoding]::new($false))

$packagesPath = Join-Path $workingRoot '.packages'
Invoke-Dotnet -Arguments @(
    'restore',
    $projectPath,
    '--configfile',
    $configurationPath,
    '--packages',
    $packagesPath,
    '--force',
    '--no-cache')

$restoredPackages = @(
    Get-ChildItem `
        -LiteralPath $packagesPath `
        -Directory `
        -Filter 'gameagent.*')
if ($restoredPackages.Count -ne 10) {
    throw 'The normalized NuGet consumer did not restore the complete package set.'
}
foreach ($restoredPackage in $restoredPackages) {
    if (($restoredPackage.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The normalized NuGet consumer restored a filesystem link.'
    }
    $metadataFiles = @(
        Get-ChildItem `
            -LiteralPath $restoredPackage.FullName `
            -Recurse `
            -Force `
            -File `
            -Filter '.nupkg.metadata')
    if ($metadataFiles.Count -ne 1) {
        throw 'A normalized NuGet consumer package has invalid source metadata.'
    }
    $metadata = Get-Content `
        -LiteralPath $metadataFiles[0].FullName `
        -Raw |
        ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$metadata.source) -or
        -not [IO.Path]::GetFullPath([string]$metadata.source).Equals(
            $packageRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A normalized NuGet consumer package came from another source.'
    }
}

Invoke-Dotnet -Arguments @(
    'build',
    $projectPath,
    '--configuration',
    'Release',
    '--no-restore')
Invoke-Dotnet -Arguments @(
    'run',
    '--project',
    $projectPath,
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore')

Write-Output 'NUGET_PACKAGE_CONSUMER_PASS'
