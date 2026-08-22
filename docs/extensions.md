# Building OpenGameAgent extensions

OpenGameAgent keeps the stateful model/tool loop compact and moves optional capabilities into extensions. First-party and third-party extensions use the same `IGameAgentExtension` and `GameAgentExtensionApi` contracts.

Extensions can contribute context, tools, input-aware tool visibility, skills, route rules, pending-work signals, workflows, hooks, prompt fragments, model providers, typed services, lifecycle handlers, and typed cross-extension channels. Each registration retains its owner, priority, order, and disposable lifetime. Namespaced extension state persists with the game session but is not shown to the model unless the extension explicitly contributes it.

## Create a project

From the repository checkout:

```powershell
./tools/New-GameAgentExtension.ps1 `
  -Id my-studio.world-observation `
  -OutputDirectory ../MyWorldObservation

dotnet build ../MyWorldObservation/Extension.csproj -c Release
```

The scaffold targets `netstandard2.1`, references the local `OpenGameAgent.Extensions` project, and includes an `extension.json` development manifest. It does not install or load executable code dynamically.

## Development manifest

The manifest is a host/development contract, not model input:

```json
{
  "schemaVersion": "1",
  "id": "my-studio.world-observation",
  "version": "1.0.0",
  "permissions": ["context.contribute", "tools.register"],
  "dependencies": [
    { "id": "my-studio.shared", "minimumVersion": "1.2.0" }
  ]
}
```

`GameExtensionDevelopmentManifest.Parse` performs strict, bounded JSON, identity, semantic-version, permission, and dependency validation. Known permissions are exposed by `GameExtensionPermissions` and map to extension resource types. Manifests must declare every resource the extension actually registers; hosts may allow a subset.

## Conformance smoke

`GameExtensionConformance.RunAsync` verifies descriptor/manifest identity, dependency versions, host permission grants, actual registered resources, configuration diagnostics, lifecycle failures, timeout, disposal, and one real `GameAgentRuntime` request using a bounded fake provider:

```csharp
var manifest = GameExtensionDevelopmentManifest.Parse(
    await File.ReadAllTextAsync("extension.json"));

var report = await GameExtensionConformance.RunAsync(
    new WorldObservationExtension(),
    manifest,
    new GameExtensionConformanceOptions
    {
        AllowedPermissions = new[]
        {
            GameExtensionPermissions.ContextContribute,
            GameExtensionPermissions.ToolsRegister,
        },
        AvailableExtensions = installedDescriptors,
        Timeout = TimeSpan.FromSeconds(10),
    },
    cancellationToken);

if (!report.Passed)
{
    throw new InvalidOperationException(string.Join(
        Environment.NewLine,
        report.Diagnostics.Select(value => value.Code + ": " + value.Message)));
}
```

Use this in an extension's tests and package gate. The fake model never calls a tool; game-owned action adapters need their own success, rejection, uncertain outcome, duplicate, restart, and revision tests.

## Dependencies and services

Manifest dependencies express required extension versions. Runtime collaboration remains typed: a provider extension registers a named service or channel, and the consumer resolves it through `GameAgentExtensionRunContext.Services`. Missing services fail with owner/resource diagnostics instead of string-only discovery.

Avoid hidden global state. A dynamic registration may be disposed while the runtime remains alive; retained run contexts become invalid when their lease closes. The runtime waits for active actor lanes before disposing extensions.

## Reload boundary

Data resources such as skills can be refreshed by their source. Executable C# extensions are compiled host code: replace them only at an editor-controlled boundary after gameplay is stopped, dispose the old `GameAgentRuntime`, load the new host generation, and construct a new runtime. Never unload or replace executable extension code during an active run. This boundary keeps Unity, Godot, Unreal sidecars, and server placement deterministic and prevents stale callbacks from mutating a replacement session.

## Packaging boundary

- Use a normal project/package reference for executable C# extensions.
- Use Agent Plugin packages for portable skills and MCP configuration.
- Keep engine SDK types and business rules in the game adapter.
- Keep credentials in host authentication/provider configuration, never manifests, prompts, traces, or extension state.
