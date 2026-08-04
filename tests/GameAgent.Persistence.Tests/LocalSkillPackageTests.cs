using System.Collections;
using System.Text;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class LocalSkillPackageTests
{
    [Theory]
    [InlineData("trusted")]
    [InlineData("builtin")]
    public void PackageDeclaredTrustCannotElevateUntrustedHostSource(
        string declaredTrust)
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("observe", declaredTrust);
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource("mods", files.Root)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        Assert.True(result.Changed);
        var skill = Assert.Single(registry.Current.Skills);
        Assert.Equal("untrusted", skill.Trust);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.DeclaredTrustIgnored);
        var decision = DefaultSkillAdmissionPolicy.Instance.Evaluate(
            AdmissionRequest(skill));
        Assert.False(decision.Allowed);
        Assert.Equal(
            SkillAdmissionReasonCodes.Untrusted,
            decision.ReasonCode);
    }

    [Fact]
    public void TrustedHostSourceMayAdmitPackageDeclaredUntrusted()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("observe", declaredTrust: "untrusted");
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "first-party",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        var skill = Assert.Single(registry.Current.Skills);
        Assert.Equal("trusted", skill.Trust);
        Assert.True(
            DefaultSkillAdmissionPolicy.Instance
                .Evaluate(AdmissionRequest(skill))
                .Allowed);
    }

    [Fact]
    public void IdenticalReloadKeepsGenerationAndAllDigestsStable()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("stable", declaredTrust: "trusted");
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "stable-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });

        var first = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);
        var second = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Applied);
        Assert.True(first.Changed);
        Assert.True(second.Applied);
        Assert.False(second.Changed);
        Assert.Equal(first.Generation, second.Generation);
        Assert.Equal(first.CatalogDigest, second.CatalogDigest);
        var firstPackage = Assert.Single(first.Packages);
        var secondPackage = Assert.Single(second.Packages);
        Assert.Equal(
            firstPackage.ManifestFileDigest,
            secondPackage.ManifestFileDigest);
        Assert.Equal(firstPackage.SourceDigest, secondPackage.SourceDigest);
        Assert.Equal(
            firstPackage.SkillContentDigest,
            secondPackage.SkillContentDigest);
        Assert.DoesNotContain(files.Root, firstPackage.ManifestPath);
        Assert.Equal("package/skill.json", firstPackage.ManifestPath);
    }

    [Fact]
    public void BrokenAndDuplicateReloadsKeepLastKnownGoodCatalog()
    {
        using var files = new TemporarySkillFiles();
        var manifestPath = files.WriteManifest(
            "last-known-good",
            declaredTrust: "trusted");
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });
        var initial = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);
        var initialSkill = Assert.Single(registry.Current.Skills);

        File.WriteAllText(
            manifestPath,
            """{"not":"a skill manifest"}""",
            new UTF8Encoding(false));
        var broken = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(broken.Applied);
        Assert.False(broken.Changed);
        Assert.Equal(initial.Generation, registry.Current.Generation);
        Assert.Equal(initial.CatalogDigest, registry.Current.Digest);
        Assert.Equal(
            initialSkill.ContentDigest,
            Assert.Single(registry.Current.Skills).ContentDigest);

        files.WriteManifest(
            "last-known-good",
            declaredTrust: "trusted",
            packageName: "duplicate");
        files.WriteManifest(
            "last-known-good",
            declaredTrust: "trusted",
            packageName: "package");
        var duplicate = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(duplicate.Applied);
        Assert.Contains(
            duplicate.Diagnostics,
            diagnostic =>
                diagnostic.Code == "skill_reference_duplicate");
        Assert.Equal(initial.Generation, registry.Current.Generation);
        Assert.Equal(initial.CatalogDigest, registry.Current.Digest);
    }

    [Fact]
    public void OversizeManifestReloadKeepsLastKnownGoodCatalog()
    {
        using var files = new TemporarySkillFiles();
        var manifestPath = files.WriteManifest(
            "bounded",
            declaredTrust: "trusted");
        var original = File.ReadAllText(manifestPath);
        var options = new LocalSkillPackageOptions
        {
            MaxManifestBytes = Encoding.UTF8.GetByteCount(original) + 16,
            MaxAggregateBytes = 1_048_576
        };
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "bounded-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            },
            options);
        var initial = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        File.WriteAllText(
            manifestPath,
            original + new string(' ', 64),
            new UTF8Encoding(false));
        var rejected = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(rejected.Applied);
        Assert.Contains(
            rejected.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.FileBytesExceeded);
        Assert.Equal(initial.Generation, registry.Current.Generation);
        Assert.Equal(initial.CatalogDigest, registry.Current.Digest);
    }

    [Fact]
    public void EntryLimitRejectsCandidateWithoutPublishingPartialCatalog()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("bounded", declaredTrust: "trusted");
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "bounded-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            },
            new LocalSkillPackageOptions
            {
                MaxScannedEntries = 3
            });
        var initial = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(initial.Applied);

        File.WriteAllText(Path.Combine(files.Root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(files.Root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(files.Root, "c.txt"), "c");
        var rejected = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(rejected.Applied);
        Assert.Contains(
            rejected.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.EntryCountExceeded);
        Assert.Equal(initial.Generation, registry.Current.Generation);
        Assert.Single(registry.Current.Skills);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("..\\outside.json")]
    [InlineData("/absolute.json")]
    [InlineData("C:/absolute.json")]
    [InlineData("folder//payload.json")]
    public void NonPortableOrTraversingResourcePathFailsClosed(string uri)
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest(
            "traversal",
            declaredTrust: "trusted",
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = uri,
                    MediaType = "application/json"
                }
            });
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code == SkillPackageDiagnosticCodes.PathInvalid);
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void SymlinkedResourceIsRejectedWithoutReadingOutsideRoot()
    {
        using var files = new TemporarySkillFiles();
        var outside = files.WriteOutside(
            "outside.json",
            """{"sentinel":"must-not-load"}""");
        var resourcePath = files.PackagePath("payload.json");
        if (!TryCreateFileSymbolicLink(resourcePath, outside))
        {
            return;
        }

        files.TrackLink(resourcePath);
        files.WriteManifest(
            "linked-resource",
            declaredTrust: "trusted",
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = "payload.json",
                    MediaType = "application/json"
                }
            });
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.LinkRejected);
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void RootSymlinkOrJunctionIsRejected()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("root-link", declaredTrust: "trusted");
        var alias = Path.Combine(files.Parent, "root-alias");
        if (!TryCreateDirectorySymbolicLink(alias, files.Root))
        {
            return;
        }

        files.TrackLink(alias);
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "linked-root",
                    alias,
                    SkillPackageSourceTrust.Trusted)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code is SkillPackageDiagnosticCodes.LinkRejected
                    or SkillPackageDiagnosticCodes.FileIdentityChanged
                    or SkillPackageDiagnosticCodes.PathUnavailable);
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void DescendantDirectorySymlinkOrReparsePointIsRejected()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("directory-link", declaredTrust: "trusted");
        var outsideDirectory = Path.Combine(files.Parent, "outside");
        var link = files.PackagePath("linked-directory");
        if (!TryCreateDirectorySymbolicLink(link, outsideDirectory))
        {
            return;
        }

        files.TrackLink(link);
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.LinkRejected);
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void RelativePathByteLimitFailsClosed()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest(
            "long-path",
            declaredTrust: "trusted",
            packageName: new string('a', 32));
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            },
            new LocalSkillPackageOptions
            {
                MaxRelativePathUtf8Bytes = 16
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.PathBytesExceeded);
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void PathSwapAfterOpenFailsIdentityCheckAndKeepsLastKnownGood()
    {
        using var files = new TemporarySkillFiles();
        var manifestPath = files.WriteManifest(
            "race-safe",
            declaredTrust: "trusted");
        var registry = new SkillCatalogRegistry();
        var baseline = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "race-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });
        var initial = baseline.Reload(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(initial.Applied);

        var replacement = files.WriteOutside(
            "replacement.json",
            ProtocolJson.Serialize(
                Manifest("outside", declaredTrust: "trusted")));
        var observer = new SwapAfterOpenObserver(
            manifestPath,
            replacement);
        var racing = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "race-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            },
            options: null,
            observer);

        var rejected = racing.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(observer.Triggered);
        Assert.False(rejected.Applied);
        Assert.Contains(
            rejected.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.FileIdentityChanged);
        Assert.Equal(initial.Generation, registry.Current.Generation);
        Assert.Equal(initial.CatalogDigest, registry.Current.Digest);
        Assert.Equal(
            "race-safe",
            Assert.Single(registry.Current.Skills).SkillId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResourceDigestOrSizeMismatchFailsBeforeCatalogPublish(
        bool digestMismatch)
    {
        using var files = new TemporarySkillFiles();
        files.WritePackageFile("payload.txt", "hello");
        files.WriteManifest(
            "pinned-resource",
            declaredTrust: "trusted",
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = "payload.txt",
                    MediaType = "text/plain",
                    Digest = digestMismatch
                        ? "sha256:" + new string('0', 64)
                        : null,
                    SizeBytes = digestMismatch ? null : 1
                }
            });
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code == (
                    digestMismatch
                        ? SkillPackageDiagnosticCodes.ResourceDigestMismatch
                        : SkillPackageDiagnosticCodes.ResourceSizeMismatch));
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void CancellationBeforeReloadLeavesCatalogUntouched()
    {
        using var files = new TemporarySkillFiles();
        files.WriteManifest("cancel-safe", declaredTrust: "trusted");
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });
        var initial = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => catalog.Reload(cancellation.Token));
        Assert.Equal(initial.Generation, registry.Current.Generation);
        Assert.Equal(initial.CatalogDigest, registry.Current.Digest);
        Assert.Single(registry.Current.Skills);
    }

    [Fact]
    public async Task RelativeTextResourceResolvesFromPinnedInMemorySnapshot()
    {
        using var files = new TemporarySkillFiles();
        var resourcePath = files.WritePackageFile(
            "notes/readme.txt",
            "bounded hello");
        files.WriteManifest(
            "resource-skill",
            declaredTrust: "trusted",
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = "notes/readme.txt",
                    MediaType = "text/plain"
                }
            });
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource(
                    "trusted-source",
                    files.Root,
                    SkillPackageSourceTrust.Trusted)
            });
        var loaded = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);
        var skill = Assert.Single(registry.Current.Skills);
        var reference = SkillContentReference.FromResource(
            Assert.Single(skill.Resources));

        File.WriteAllText(resourcePath, "changed after reload");
        var resolved = await catalog.ResolveAsync(
            new SkillContentResolutionRequest(
                Run(),
                "turn-1",
                skill,
                reference,
                depth: 0),
            TestContext.Current.CancellationToken);

        Assert.True(loaded.Applied);
        Assert.StartsWith("sha256:", resolved.Digest);
        Assert.True(resolved.SizeBytes > 0);
        Assert.Equal(
            "application/vnd.game-agent.skill-package-resource+json",
            resolved.Content.GetProperty("contentType").GetString());
        Assert.Equal(
            "text/plain",
            resolved.Content.GetProperty("mediaType").GetString());
        Assert.Equal(
            "bounded hello",
            resolved.Content.GetProperty("text").GetString());
        var package = Assert.Single(loaded.Packages);
        Assert.StartsWith("sha256:", package.ManifestFileDigest);
        Assert.StartsWith("sha256:", package.SourceDigest);
        Assert.Equal(
            package.SourceDigest,
            skill.DeclaredDigest);

        var reloaded = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);
        var changedSkill = Assert.Single(registry.Current.Skills);
        var changedReference = SkillContentReference.FromResource(
            Assert.Single(changedSkill.Resources));
        var changed = await catalog.ResolveAsync(
            new SkillContentResolutionRequest(
                Run(),
                "turn-2",
                changedSkill,
                changedReference,
                depth: 0),
            TestContext.Current.CancellationToken);

        Assert.True(reloaded.Applied);
        Assert.True(reloaded.Changed);
        Assert.NotEqual(
            package.SourceDigest,
            Assert.Single(reloaded.Packages).SourceDigest);
        Assert.Equal(
            "changed after reload",
            changed.Content.GetProperty("text").GetString());
    }

    [Fact]
    public void InvalidUtf8ManifestFailsClosed()
    {
        using var files = new TemporarySkillFiles();
        var path = files.PackagePath("skill.json");
        File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0xfd });
        var registry = new SkillCatalogRegistry();
        var catalog = new LocalSkillPackageCatalog(
            registry,
            new[]
            {
                new LocalSkillPackageSource("mods", files.Root)
            });

        var result = catalog.Reload(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code
                == SkillPackageDiagnosticCodes.StrictUtf8Required);
        Assert.Empty(registry.Current.Skills);
    }

    [Fact]
    public void LyingSourceCountStopsAtFirstItemBeyondConfiguredLimit()
    {
        using var files = new TemporarySkillFiles();
        var enumerated = 0;
        IEnumerable<LocalSkillPackageSource> Sources()
        {
            for (var index = 0; index < 100; index++)
            {
                enumerated++;
                yield return new LocalSkillPackageSource(
                    "source-" + index,
                    Path.Combine(files.Parent, "source-" + index));
            }
        }

        var sources =
            new LyingReadOnlyCollection<LocalSkillPackageSource>(Sources());
        var error = Assert.Throws<RuntimeContentLimitException>(
            () => new LocalSkillPackageCatalog(
                new SkillCatalogRegistry(),
                sources,
                new LocalSkillPackageOptions
                {
                    MaxSources = 2
                }));

        Assert.Equal("skill_package_source_count_exceeded", error.LimitCode);
        Assert.Equal(3, enumerated);
    }

    [Fact]
    public void InfiniteSourceSequenceStopsAtFirstItemBeyondConfiguredLimit()
    {
        using var files = new TemporarySkillFiles();
        var enumerated = 0;
        IEnumerable<LocalSkillPackageSource> Sources()
        {
            while (true)
            {
                var index = enumerated++;
                yield return new LocalSkillPackageSource(
                    "source-" + index,
                    Path.Combine(files.Parent, "source-" + index));
            }
        }

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => new LocalSkillPackageCatalog(
                new SkillCatalogRegistry(),
                Sources(),
                new LocalSkillPackageOptions
                {
                    MaxSources = 2
                }));

        Assert.Equal("skill_package_source_count_exceeded", error.LimitCode);
        Assert.Equal(3, enumerated);
    }

    private static SkillAdmissionRequest AdmissionRequest(
        SkillCatalogEntry skill) =>
        new(
            Run(),
            "turn-1",
            skill,
            new ToolCatalogRegistry().Current,
            SkillAdmissionPurposes.Activation);

    private static AgentRun Run() =>
        new()
        {
            RunId = "local-skill-run",
            AgentId = "npc-1",
            WorldId = "world-1",
            State = RunStates.Queued,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };

    private static SkillManifest Manifest(
        string skillId,
        string declaredTrust,
        IReadOnlyList<ResourceReference>? resources = null) =>
        new()
        {
            SkillId = skillId,
            Version = "1.0.0",
            Digest = "package-declared",
            Description = "A local inert skill package.",
            PromptFragments = new List<string>
            {
                "Use only authoritative game evidence."
            },
            RequiredToolRefs = new List<string>(),
            OptionalToolRefs = new List<string>(),
            ContextProviderRefs = new List<string>(),
            ResourceRefs = resources?.ToList()
                           ?? new List<ResourceReference>(),
            CapabilityRequirements = ProtocolJson.ParseElement("{}"),
            Trust = declaredTrust,
            ActivationPolicy = ProtocolJson.ParseElement("{}")
        };

    private static bool TryCreateFileSymbolicLink(
        string link,
        string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception)
            when (exception is PlatformNotSupportedException
                  or UnauthorizedAccessException
                  or IOException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(
        string link,
        string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception)
            when (exception is PlatformNotSupportedException
                  or UnauthorizedAccessException
                  or IOException)
        {
            return false;
        }
    }

    private sealed class SwapAfterOpenObserver
        : ILocalSkillPackageFileObserver
    {
        private readonly string _path;
        private readonly string _replacement;

        public SwapAfterOpenObserver(string path, string replacement)
        {
            _path = path;
            _replacement = replacement;
        }

        public bool Triggered { get; private set; }

        public void OnFileRead(
            LocalSkillFileReadStage stage,
            string sourceId,
            string relativePath)
        {
            if (Triggered
                || stage != LocalSkillFileReadStage.PrimaryOpened
                || !string.Equals(
                    relativePath,
                    "package/skill.json",
                    StringComparison.Ordinal))
            {
                return;
            }

            Triggered = true;
            var moved = _path + ".opened";
            File.Move(_path, moved);
            File.Copy(_replacement, _path);
        }
    }

    private sealed class LyingReadOnlyCollection<T>
        : IReadOnlyCollection<T>
    {
        private readonly IEnumerable<T> _source;

        public LyingReadOnlyCollection(IEnumerable<T> source)
        {
            _source = source;
        }

        public int Count => 0;

        public IEnumerator<T> GetEnumerator() => _source.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TemporarySkillFiles : IDisposable
    {
        private readonly List<string> _links = new();

        public TemporarySkillFiles()
        {
            Parent = Path.Combine(
                Path.GetTempPath(),
                "game-agent-local-skill-tests",
                Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Parent, "root");
            Directory.CreateDirectory(PackagePath(string.Empty));
            Directory.CreateDirectory(Path.Combine(Parent, "outside"));
        }

        public string Parent { get; }

        public string Root { get; }

        public string WriteManifest(
            string skillId,
            string declaredTrust,
            IReadOnlyList<ResourceReference>? resources = null,
            string packageName = "package")
        {
            var directory = Path.Combine(Root, packageName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "skill.json");
            File.WriteAllText(
                path,
                ProtocolJson.Serialize(
                    Manifest(skillId, declaredTrust, resources)),
                new UTF8Encoding(false));
            return path;
        }

        public string WritePackageFile(string relativePath, string content)
        {
            var path = PackagePath(relativePath);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                content,
                new UTF8Encoding(false));
            return path;
        }

        public string WriteOutside(string name, string content)
        {
            var path = Path.Combine(Parent, "outside", name);
            File.WriteAllText(
                path,
                content,
                new UTF8Encoding(false));
            return path;
        }

        public string PackagePath(string relativePath) =>
            Path.Combine(
                Root,
                "package",
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));

        public void TrackLink(string path)
        {
            _links.Add(path);
        }

        public void Dispose()
        {
            foreach (var link in _links
                         .OrderByDescending(
                             value => value.Length))
            {
                try
                {
                    if (File.Exists(link))
                    {
                        File.Delete(link);
                    }
                    else if (Directory.Exists(link))
                    {
                        Directory.Delete(link);
                    }
                }
                catch
                {
                    // Best-effort test cleanup continues with the isolated
                    // parent directory below.
                }
            }

            if (Directory.Exists(Parent))
            {
                Directory.Delete(Parent, recursive: true);
            }
        }
    }
}
