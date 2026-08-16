using System.Xml.Linq;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class ProjectDependencyBoundaryTests
{
    [Fact]
    public void ReusableAiFoundationDoesNotDependOnGameHostPackages()
    {
        var root = FindRepositoryRoot();
        var reusableProjects = new[]
        {
            "OpenGameAgent.Kernel",
            "OpenGameAgent.Models",
            "OpenGameAgent.Models.BuiltIn",
            "OpenGameAgent.Providers.Anthropic",
            "OpenGameAgent.Providers.Bedrock",
            "OpenGameAgent.Providers.Google",
            "OpenGameAgent.Providers.Mistral",
            "OpenGameAgent.Providers.OpenAI",
            "OpenGameAgent.Providers.OpenAICompatible",
            "OpenGameAgent.Providers.Remote",
        };
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "OpenGameAgent",
            "OpenGameAgent.Client",
            "OpenGameAgent.Extensions",
            "OpenGameAgent.Persistence",
            "OpenGameAgent.Server",
        };

        foreach (var project in reusableProjects)
        {
            Assert.Equal("netstandard2.1", ReadTargetFramework(root, project));
            var references = ReadProjectReferences(root, project);
            Assert.DoesNotContain(references, reference => forbidden.Contains(reference));
        }
    }

    [Fact]
    public void AttachmentContractsAreDependencyFreeAndKernelUsesOnlyAttachmentContracts()
    {
        var root = FindRepositoryRoot();

        Assert.Empty(ReadProjectReferences(root, "OpenGameAgent.Attachments"));
        Assert.Equal(
            new[] { "OpenGameAgent.Attachments" },
            ReadProjectReferences(root, "OpenGameAgent.Kernel"));
        Assert.Equal(
            new[] { "OpenGameAgent.Kernel" },
            ReadProjectReferences(root, "OpenGameAgent.Models"));
    }

    private static IReadOnlyList<string> ReadProjectReferences(string root, string project)
    {
        var document = ReadProject(root, project);
        return document.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => Path.GetFileNameWithoutExtension(value!.Replace('\\', '/')))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadTargetFramework(string root, string project) =>
        ReadProject(root, project).Descendants("TargetFramework").Single().Value;

    private static XDocument ReadProject(string root, string project) =>
        XDocument.Load(Path.Combine(root, "src", project, project + ".csproj"), LoadOptions.None);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenGameAgent.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
