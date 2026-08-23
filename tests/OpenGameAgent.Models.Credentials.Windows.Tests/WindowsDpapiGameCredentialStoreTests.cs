using System.Text;
using System.Text.Json;
using OpenGameAgent.Models;
using OpenGameAgent.Models.Credentials.Windows;
using Xunit;

namespace OpenGameAgent.Models.Credentials.Windows.Tests;

public sealed class WindowsDpapiGameCredentialStoreTests
{
    [Fact]
    public void ConstructorHasAnExplicitPlatformBoundary()
    {
        using var directory = new TemporaryDirectory();
        if (WindowsDpapiGameCredentialStore.IsSupported)
        {
            _ = new WindowsDpapiGameCredentialStore(new(directory.Path));
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                new WindowsDpapiGameCredentialStore(new(directory.Path)));
        }
    }

    [Fact]
    public async Task SetGetAndRemoveRoundTripWithoutPlaintextAtRest()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        var key = new GameCredentialKey("private-provider", "private-profile");
        var credential = new GameCredential(
            GameCredentialKind.ApiKey,
            "unique-secret-value",
            DateTimeOffset.UtcNow.AddHours(1),
            new Dictionary<string, string> { ["refresh"] = "private-metadata" });

        await store.SetAsync(key, credential, TestContext.Current.CancellationToken);
        var restored = await store.GetAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal(credential.Kind, restored.Kind);
        Assert.Equal(credential.Secret, restored.Secret);
        Assert.Equal(credential.ExpiresAt, restored.ExpiresAt);
        Assert.Equal("private-metadata", restored.Metadata["refresh"]);
        var storedText = await File.ReadAllTextAsync(
            System.IO.Path.Combine(directory.Path, WindowsDpapiGameCredentialStore.DefaultFileName),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private-provider", storedText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-profile", storedText, StringComparison.Ordinal);
        Assert.DoesNotContain("unique-secret-value", storedText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-metadata", storedText, StringComparison.Ordinal);

        Assert.True(await store.RemoveAsync(key, TestContext.Current.CancellationToken));
        Assert.False(await store.RemoveAsync(key, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplacingAnEntryUsesFreshEntropyAndCiphertext()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        var key = new GameCredentialKey("provider");
        var credential = new GameCredential(GameCredentialKind.ApiKey, "same-secret");

        await store.SetAsync(key, credential, TestContext.Current.CancellationToken);
        var first = await ReadEnvelopeAsync(directory.Path);
        await store.SetAsync(key, credential, TestContext.Current.CancellationToken);
        var second = await ReadEnvelopeAsync(directory.Path);

        Assert.NotEqual(first.Entropy, second.Entropy);
        Assert.NotEqual(first.ProtectedData, second.ProtectedData);
    }

    [Fact]
    public async Task ModifyIsSerializedAcrossStoreInstances()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var stores = Enumerable.Range(0, 12).Select(_ => Create(directory.Path)).ToArray();
        var key = new GameCredentialKey("provider");
        await stores[0].SetAsync(
            key,
            new GameCredential(GameCredentialKind.ApiKey, "0"),
            TestContext.Current.CancellationToken);

        await Task.WhenAll(stores.Select(store => store.ModifyAsync(
            key,
            async (current, token) =>
            {
                await Task.Delay(5, token);
                return new GameCredential(
                    GameCredentialKind.ApiKey,
                    (int.Parse(current!.Secret, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
            },
            TestContext.Current.CancellationToken).AsTask()));

        var final = await stores[0].GetAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal("12", final?.Secret);
    }

    [Fact]
    public async Task LockWaitIsBoundedAndDoesNotCancelTheLeaseHolder()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = new WindowsDpapiGameCredentialStore(new(directory.Path)
        {
            LockTimeoutMilliseconds = 100,
        });
        var key = new GameCredentialKey("provider");
        await store.SetAsync(
            key,
            new GameCredential(GameCredentialKind.ApiKey, "before"),
            TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = store.ModifyAsync(
            key,
            async (current, _) =>
            {
                entered.SetResult();
                await release.Task;
                return new GameCredential(current!.Kind, "after");
            },
            TestContext.Current.CancellationToken).AsTask();
        await entered.Task;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            store.GetAsync(key, TestContext.Current.CancellationToken).AsTask());
        release.SetResult();
        await holding;

        Assert.Equal(
            "after",
            (await store.GetAsync(key, TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task ModifyCanCreateReplaceAndDelete()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        var key = new GameCredentialKey("provider");

        var created = await store.ModifyAsync(
            key,
            (_, _) => new ValueTask<GameCredential?>(new GameCredential(GameCredentialKind.ApiKey, "one")),
            TestContext.Current.CancellationToken);
        var replaced = await store.ModifyAsync(
            key,
            (current, _) => new ValueTask<GameCredential?>(
                new GameCredential(current!.Kind, current.Secret + "-two")),
            TestContext.Current.CancellationToken);
        var removed = await store.ModifyAsync(
            key,
            (_, _) => new ValueTask<GameCredential?>((GameCredential?)null),
            TestContext.Current.CancellationToken);

        Assert.Equal("one", created?.Secret);
        Assert.Equal("one-two", replaced?.Secret);
        Assert.Null(removed);
        Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CapacityAndCredentialSizeAreBounded()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = new WindowsDpapiGameCredentialStore(new(directory.Path)
        {
            Capacity = 1,
            MaximumCredentialBytes = 4 * 1024,
            MaximumStoreBytes = 64 * 1024,
        });
        await store.SetAsync(
            new GameCredentialKey("one"),
            new GameCredential(GameCredentialKind.ApiKey, "secret"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            new GameCredentialKey("two"),
            new GameCredential(GameCredentialKind.ApiKey, "secret"),
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(
            new GameCredentialKey("one"),
            new GameCredential(GameCredentialKind.ApiKey, new string('x', 8 * 1024)),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task CorruptionAndWrongCiphertextFailClosedWithoutLeakingValues()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        var key = new GameCredentialKey("provider");
        await store.SetAsync(
            key,
            new GameCredential(GameCredentialKind.ApiKey, "must-not-leak"),
            TestContext.Current.CancellationToken);
        var path = System.IO.Path.Combine(directory.Path, WindowsDpapiGameCredentialStore.DefaultFileName);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        using (var parsed = JsonDocument.Parse(json))
        {
            var root = parsed.RootElement;
            var entry = root.GetProperty("entries")[0];
            var tampered = new
            {
                version = root.GetProperty("version").GetInt32(),
                revision = root.GetProperty("revision").GetInt64(),
                entries = new[]
                {
                    new
                    {
                        version = entry.GetProperty("version").GetInt32(),
                        entropy = entry.GetProperty("entropy").GetString(),
                        protectedData = Convert.ToBase64String(Encoding.UTF8.GetBytes("tampered")),
                    },
                },
            };
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(tampered),
                TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.GetAsync(key, TestContext.Current.CancellationToken).AsTask());
        Assert.DoesNotContain("must-not-leak", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("dGFtcGVyZWQ=", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptedTemporaryWriteIsDiscarded()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        var key = new GameCredentialKey("provider");
        await store.SetAsync(
            key,
            new GameCredential(GameCredentialKind.ApiKey, "committed"),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(directory.Path, "credentials.v1.json.tmp"),
            "uncommitted plaintext",
            TestContext.Current.CancellationToken);

        var reopened = Create(directory.Path);
        var credential = await reopened.GetAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal("committed", credential?.Secret);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "credentials.v1.json.tmp")));
    }

    [Fact]
    public async Task CancellationBeforeMutationDoesNotCreateAStoreFile()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetAsync(
            new GameCredentialKey("provider"),
            new GameCredential(GameCredentialKind.ApiKey, "secret"),
            cancellation.Token).AsTask());
        Assert.False(File.Exists(
            System.IO.Path.Combine(directory.Path, WindowsDpapiGameCredentialStore.DefaultFileName)));
    }

    [Fact]
    public async Task StoredAuthenticationConsumesThePersistentStore()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var store = Create(directory.Path);
        var authentication = new StoredGameProviderAuthentication(
            "provider",
            store,
            schemes: new[] { "api-key" },
            login: (_, _, _) => new ValueTask<GameCredential>(
                new GameCredential(GameCredentialKind.ApiKey, "login-secret")));

        await authentication.LoginAsync(
            "api-key",
            new GameAuthInteraction(),
            TestContext.Current.CancellationToken);
        var reopened = new StoredGameProviderAuthentication("provider", Create(directory.Path));
        var resolution = await reopened.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("login-secret", resolution?.Credential?.Secret);
        await reopened.LogoutAsync(TestContext.Current.CancellationToken);
        Assert.Null(await authentication.ResolveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReparsePointStorageIsRejectedWhenThePlatformAllowsCreatingOne()
    {
        if (!WindowsDpapiGameCredentialStore.IsSupported)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var real = System.IO.Path.Combine(directory.Path, "real");
        var link = System.IO.Path.Combine(directory.Path, "link");
        System.IO.Directory.CreateDirectory(real);
        try
        {
            System.IO.Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<IOException>(() =>
            new WindowsDpapiGameCredentialStore(new(link)));
    }

    private static WindowsDpapiGameCredentialStore Create(string directory) =>
        new(new WindowsDpapiGameCredentialStoreOptions(directory));

    private static async Task<(string Entropy, string ProtectedData)> ReadEnvelopeAsync(string directory)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Combine(directory, WindowsDpapiGameCredentialStore.DefaultFileName),
            TestContext.Current.CancellationToken));
        var entry = document.RootElement.GetProperty("entries")[0];
        return (entry.GetProperty("entropy").GetString()!, entry.GetProperty("protectedData").GetString()!);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "oga-dpapi-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
