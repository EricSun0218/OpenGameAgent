using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 1)
{
    throw new InvalidOperationException("Pass one Unity plugin directory.");
}

string pluginRoot = Path.GetFullPath(args[0]);
string[] expectedNames =
[
    "GameAgent.Core.dll",
    "GameAgent.Generation.dll",
    "GameAgent.Persistence.dll",
    "GameAgent.Protocol.dll",
    "GameAgent.Providers.Anthropic.dll",
    "GameAgent.Providers.Native.dll",
    "GameAgent.Providers.OpenAICompatible.dll",
    "GameAgent.Providers.MediaHttp.dll",
    "GameAgent.Runtime.dll",
    "GameAgent.Remote.Client.dll",
    "GameAgent.Simulation.dll",
    "GameAgent.Workflow.dll",
    "Microsoft.Bcl.AsyncInterfaces.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Text.Encodings.Web.dll",
    "System.Text.Json.dll",
    "System.Threading.Tasks.Extensions.dll"
];

string[] actualNames = Directory
    .EnumerateFiles(pluginRoot, "*.dll", SearchOption.TopDirectoryOnly)
    .Select(Path.GetFileName)
    .Order(StringComparer.Ordinal)
    .ToArray()!;
if (!actualNames.SequenceEqual(
        expectedNames.Order(StringComparer.Ordinal),
        StringComparer.Ordinal))
{
    throw new InvalidOperationException(
        "The Unity plugin directory has an invalid assembly closure.");
}

var paths = expectedNames.ToDictionary(
    static name => Path.GetFileNameWithoutExtension(name),
    name => Path.Combine(pluginRoot, name),
    StringComparer.Ordinal);
var context = new ArtifactLoadContext(paths);
try
{
    foreach ((string expectedAssemblyName, string path) in paths)
    {
        Assembly assembly = context.LoadFromAssemblyPath(path);
        if (!string.Equals(
                assembly.GetName().Name,
                expectedAssemblyName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Assembly identity mismatch for '{path}'.");
        }
    }
}
finally
{
    context.Unload();
}

Console.WriteLine($"UNITY_ARTIFACT_ASSEMBLY_LOAD_PASS count={paths.Count}");

internal sealed class ArtifactLoadContext(
    IReadOnlyDictionary<string, string> paths)
    : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null &&
            paths.TryGetValue(assemblyName.Name, out string? path))
        {
            return LoadFromAssemblyPath(path);
        }

        return null;
    }
}
