using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Models;

namespace OpenGameAgent.Providers.Local;

public enum LocalGameModelRuntimeState
{
    Unknown,
    Unloaded,
    Loading,
    Ready,
    Unloading,
    Acquiring,
    Failed,
}

public enum LocalGameModelOperationKind
{
    Inventory,
    Warmup,
    Load,
    Unload,
    Acquire,
}

public sealed class LocalGameModelInventoryItem
{
    public LocalGameModelInventoryItem(
        string modelId,
        LocalGameModelRuntimeState state,
        long? sizeBytes = null,
        string? version = null)
    {
        ModelId = LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId));
        if (!Enum.IsDefined(typeof(LocalGameModelRuntimeState), state)
            || sizeBytes is < 0
            || version is { Length: > 256 }
            || version?.Any(char.IsControl) == true)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
        SizeBytes = sizeBytes;
        Version = version;
    }

    public string ModelId { get; }

    public LocalGameModelRuntimeState State { get; }

    public long? SizeBytes { get; }

    public string? Version { get; }
}

public sealed class LocalGameModelOperationProgress
{
    public LocalGameModelOperationProgress(
        string operationId,
        string modelId,
        LocalGameModelOperationKind kind,
        string stage,
        long? completedBytes = null,
        long? totalBytes = null)
    {
        OperationId = LocalGameModelLifecycleContract.RequireId(operationId, nameof(operationId));
        ModelId = LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId));
        Stage = LocalGameModelLifecycleContract.RequireId(stage, nameof(stage));
        if (!Enum.IsDefined(typeof(LocalGameModelOperationKind), kind)
            || completedBytes is < 0
            || totalBytes is < 0
            || completedBytes is { } completed
            && totalBytes is { } total
            && completed > total)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        CompletedBytes = completedBytes;
        TotalBytes = totalBytes;
    }

    public string OperationId { get; }

    public string ModelId { get; }

    public LocalGameModelOperationKind Kind { get; }

    public string Stage { get; }

    public long? CompletedBytes { get; }

    public long? TotalBytes { get; }

    public double? Ratio => CompletedBytes is { } completed && TotalBytes is > 0 and var total
        ? Math.Min(1, (double)completed / total)
        : null;
}

public sealed class LocalGameModelAcquisitionRequest
{
    public LocalGameModelAcquisitionRequest(string modelId, string? source = null)
    {
        ModelId = LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId));
        if (source is { Length: > 2_048 } || source?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The model source is invalid.", nameof(source));
        }

        Source = source;
    }

    public string ModelId { get; }

    /// <summary>Optional backend-specific source selected by trusted host code.</summary>
    public string? Source { get; }
}

public delegate ValueTask<bool> LocalGameModelAcquisitionAuthorizer(
    LocalGameModelAcquisitionRequest request,
    CancellationToken cancellationToken);

public delegate ValueTask LocalGameModelProgressHandler(
    LocalGameModelOperationProgress progress,
    CancellationToken cancellationToken);

/// <summary>Provider-specific lifecycle operations. They are never invoked implicitly by the agent runtime.</summary>
public interface ILocalGameModelLifecycleBackend
{
    ValueTask<IReadOnlyList<LocalGameModelInventoryItem>> ReadInventoryAsync(
        bool refresh,
        CancellationToken cancellationToken);

    ValueTask WarmupAsync(string modelId, CancellationToken cancellationToken);

    ValueTask LoadAsync(string modelId, CancellationToken cancellationToken);

    ValueTask UnloadAsync(string modelId, CancellationToken cancellationToken);

    IAsyncEnumerable<LocalGameModelOperationProgress> AcquireAsync(
        string operationId,
        LocalGameModelAcquisitionRequest request,
        CancellationToken cancellationToken);
}

public sealed class LocalGameModelLifecycleOptions
{
    public int MaximumInventoryItems { get; set; } = 4_096;

    public int MaximumConcurrentOperations { get; set; } = 2;

    public int OperationTimeoutMilliseconds { get; set; } = 600_000;

    public LocalGameModelAcquisitionAuthorizer? AuthorizeAcquisitionAsync { get; set; }

    internal LocalGameModelLifecycleOptions Snapshot()
    {
        if (MaximumInventoryItems is < 1 or > 65_536
            || MaximumConcurrentOperations is < 1 or > 64
            || OperationTimeoutMilliseconds is < 100 or > 3_600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(LocalGameModelLifecycleOptions));
        }

        return (LocalGameModelLifecycleOptions)MemberwiseClone();
    }
}

/// <summary>
/// Explicit developer/host control for local model inventory and lifecycle. Acquisition fails
/// closed without a host authorizer; the runtime never downloads, loads, or unloads a model on its own.
/// </summary>
public sealed class LocalGameModelLifecycle : IDisposable
{
    private readonly ILocalGameModelLifecycleBackend _backend;
    private readonly LocalGameModelLifecycleOptions _options;
    private readonly SemaphoreSlim _operations;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public LocalGameModelLifecycle(
        ILocalGameModelLifecycleBackend backend,
        LocalGameModelLifecycleOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = (options ?? new LocalGameModelLifecycleOptions()).Snapshot();
        _operations = new SemaphoreSlim(
            _options.MaximumConcurrentOperations,
            _options.MaximumConcurrentOperations);
    }

    public ValueTask<IReadOnlyList<LocalGameModelInventoryItem>> ReadInventoryAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => _backend.ReadInventoryAsync(refresh, token),
            ValidateInventory,
            cancellationToken);

    public ValueTask WarmupAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => _backend.WarmupAsync(
                LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId)),
                token),
            cancellationToken);

    public ValueTask LoadAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => _backend.LoadAsync(
                LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId)),
                token),
            cancellationToken);

    public ValueTask UnloadAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            token => _backend.UnloadAsync(
                LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId)),
                token),
            cancellationToken);

    public async ValueTask AcquireAsync(
        LocalGameModelAcquisitionRequest request,
        LocalGameModelProgressHandler? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (_options.AuthorizeAcquisitionAsync is null)
        {
            throw new UnauthorizedAccessException("Local model acquisition requires explicit host authorization.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        timeout.CancelAfter(_options.OperationTimeoutMilliseconds);
        if (!await _options.AuthorizeAcquisitionAsync(request, timeout.Token).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Local model acquisition was denied by the host.");
        }

        await _operations.WaitAsync(timeout.Token).ConfigureAwait(false);
        var operationId = "local-model-" + Guid.NewGuid().ToString("N");
        long? lastCompleted = null;
        long? lastTotal = null;
        try
        {
            await foreach (var value in _backend.AcquireAsync(operationId, request, timeout.Token)
                               .WithCancellation(timeout.Token)
                               .ConfigureAwait(false))
            {
                if (!string.Equals(value.OperationId, operationId, StringComparison.Ordinal)
                    || !string.Equals(value.ModelId, request.ModelId, StringComparison.Ordinal)
                    || value.Kind != LocalGameModelOperationKind.Acquire
                    || lastCompleted is { } previous && value.CompletedBytes is { } current && current < previous
                    || lastTotal is { } total && value.TotalBytes is { } nextTotal && nextTotal != total)
                {
                    throw new InvalidDataException("The local model backend emitted invalid progress.");
                }

                lastCompleted = value.CompletedBytes ?? lastCompleted;
                lastTotal = value.TotalBytes ?? lastTotal;
                if (progress is not null)
                {
                    await progress(value, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private async ValueTask RunAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        timeout.CancelAfter(_options.OperationTimeoutMilliseconds);
        await _operations.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await operation(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        Func<T, T> validate,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        timeout.CancelAfter(_options.OperationTimeoutMilliseconds);
        await _operations.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            return validate(await operation(timeout.Token).ConfigureAwait(false));
        }
        finally
        {
            _operations.Release();
        }
    }

    private IReadOnlyList<LocalGameModelInventoryItem> ValidateInventory(
        IReadOnlyList<LocalGameModelInventoryItem> items)
    {
        if (items is null
            || items.Count > _options.MaximumInventoryItems
            || items.Any(value => value is null)
            || items.Select(value => value.ModelId).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new InvalidDataException("The local model inventory was invalid or exceeded its configured limit.");
        }

        return new ReadOnlyCollection<LocalGameModelInventoryItem>(items.ToArray());
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LocalGameModelLifecycle));
        }
    }
}

public sealed class OllamaGameModelLifecycleOptions
{
    public OllamaGameModelLifecycleOptions(HttpClient httpClient, Uri? endpoint = null)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? new Uri("http://127.0.0.1:11434");
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public IGameProviderAuthentication Authentication { get; set; } =
        new StaticGameProviderAuthentication();

    public string KeepAlive { get; set; } = "5m";

    public bool AllowRemoteEndpoint { get; set; }

    public bool AllowInsecureRemoteHttp { get; set; }

    public int MaximumInventoryItems { get; set; } = 4_096;

    public int MaximumResponseBytes { get; set; } = 8_000_000;
}

/// <summary>Explicit Ollama lifecycle backend using tags/process inventory, keep-alive load/unload, and pull progress.</summary>
public sealed class OllamaGameModelLifecycleBackend : ILocalGameModelLifecycleBackend
{
    private readonly OllamaSettings _settings;

    public OllamaGameModelLifecycleBackend(OllamaGameModelLifecycleOptions options)
    {
        _settings = OllamaSettings.Create(options);
    }

    public async ValueTask<IReadOnlyList<LocalGameModelInventoryItem>> ReadInventoryAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        var installed = await ReadModelsAsync("api/tags", "models", cancellationToken).ConfigureAwait(false);
        var running = await ReadModelsAsync("api/ps", "models", cancellationToken).ConfigureAwait(false);
        var ready = new HashSet<string>(running.Select(value => value.Id), StringComparer.Ordinal);
        var result = installed.Select(value => new LocalGameModelInventoryItem(
                value.Id,
                ready.Contains(value.Id) ? LocalGameModelRuntimeState.Ready : LocalGameModelRuntimeState.Unloaded,
                value.Size,
                value.Version))
            .ToArray();
        return Array.AsReadOnly(result);
    }

    public ValueTask WarmupAsync(string modelId, CancellationToken cancellationToken) =>
        SetKeepAliveAsync(modelId, _settings.KeepAlive, cancellationToken);

    public ValueTask LoadAsync(string modelId, CancellationToken cancellationToken) =>
        SetKeepAliveAsync(modelId, _settings.KeepAlive, cancellationToken);

    public ValueTask UnloadAsync(string modelId, CancellationToken cancellationToken) =>
        SetKeepAliveAsync(modelId, "0", cancellationToken);

    public async IAsyncEnumerable<LocalGameModelOperationProgress> AcquireAsync(
        string operationId,
        LocalGameModelAcquisitionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var authentication = await _settings.Authentication.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = Endpoint(authentication, "api/pull");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = request.Source ?? request.ModelId,
            stream = true,
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyAuthentication(message, authentication);
        using var response = await _settings.HttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ValidateResponse(endpoint, response);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpFailure("acquisition", response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 16_384, leaveOpen: false);
        var totalRead = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await WaitWithCancellationAsync(reader.ReadLineAsync(), cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            totalRead = checked(totalRead + Encoding.UTF8.GetByteCount(line) + 1);
            if (totalRead > _settings.MaximumResponseBytes || line.Length > 1_000_000)
            {
                throw new InvalidDataException("The Ollama acquisition response exceeded its configured limit.");
            }

            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                throw new InvalidOperationException("Ollama reported a model acquisition failure.");
            }

            var status = SafeStage(root.TryGetProperty("status", out var statusValue)
                && statusValue.ValueKind == JsonValueKind.String
                    ? statusValue.GetString()
                    : null);
            var completed = TryInt64(root, "completed");
            var total = TryInt64(root, "total");
            yield return new LocalGameModelOperationProgress(
                operationId,
                request.ModelId,
                LocalGameModelOperationKind.Acquire,
                status,
                completed,
                total);
            if (string.Equals(status, "success", StringComparison.Ordinal))
            {
                yield break;
            }
        }

        throw new InvalidDataException("The Ollama acquisition stream ended without success.");
    }

    private async ValueTask SetKeepAliveAsync(
        string modelId,
        string keepAlive,
        CancellationToken cancellationToken)
    {
        LocalGameModelLifecycleContract.RequireId(modelId, nameof(modelId));
        var authentication = await _settings.Authentication.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = Endpoint(authentication, "api/generate");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = modelId,
            prompt = string.Empty,
            stream = false,
            keep_alive = keepAlive,
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyAuthentication(message, authentication);
        using var response = await _settings.HttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ValidateResponse(endpoint, response);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpFailure("lifecycle", response.StatusCode);
        }

        _ = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<ModelEntry>> ReadModelsAsync(
        string path,
        string arrayName,
        CancellationToken cancellationToken)
    {
        var authentication = await _settings.Authentication.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = Endpoint(authentication, path);
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyAuthentication(message, authentication);
        using var response = await _settings.HttpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ValidateResponse(endpoint, response);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpFailure("inventory", response.StatusCode);
        }

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(arrayName, out var values)
            || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() > _settings.MaximumInventoryItems)
        {
            throw new InvalidDataException("The Ollama model inventory was invalid.");
        }

        var result = new List<ModelEntry>();
        foreach (var value in values.EnumerateArray())
        {
            var id = OptionalString(value, "name") ?? OptionalString(value, "model");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl))
            {
                continue;
            }

            var size = TryInt64(value, "size");
            var version = value.TryGetProperty("digest", out var digest)
                && digest.ValueKind == JsonValueKind.String
                ? Bound(digest.GetString(), 256)
                : null;
            result.Add(new ModelEntry(id, size, version));
        }

        return result;
    }

    private Uri Endpoint(GameProviderAuthResolution? authentication, string path)
    {
        var baseUrl = authentication?.BaseUrl ?? _settings.Endpoint;
        ValidateEndpoint(baseUrl, _settings.AllowRemoteEndpoint, _settings.AllowInsecureRemoteHttp);
        return new UriBuilder(baseUrl) { Path = baseUrl.AbsolutePath.TrimEnd('/') + "/" + path }.Uri;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > _settings.MaximumResponseBytes)
        {
            throw new InvalidDataException("The Ollama response exceeded its configured limit.");
        }

        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > _settings.MaximumResponseBytes)
            {
                throw new InvalidDataException("The Ollama response exceeded its configured limit.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static void ApplyAuthentication(
        HttpRequestMessage request,
        GameProviderAuthResolution? authentication)
    {
        if (authentication?.Credential is { } credential)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        }

        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            if (pair.Value is not null && !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("An Ollama authentication header was invalid.");
            }
        }
    }

    private static void ValidateResponse(Uri endpoint, HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            throw new InvalidDataException("The Ollama service refused a redirect response.");
        }

        if (response.RequestMessage?.RequestUri is { } final
            && (!string.Equals(endpoint.Scheme, final.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(endpoint.Host, final.Host, StringComparison.OrdinalIgnoreCase)
                || endpoint.Port != final.Port))
        {
            throw new InvalidDataException("The Ollama service redirected across origins.");
        }
    }

    private static Exception HttpFailure(string operation, System.Net.HttpStatusCode status) =>
        new InvalidOperationException(
            "The Ollama " + operation + " request returned HTTP "
            + ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ".");

    private static string SafeStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character => char.IsControl(character) || !(char.IsLetterOrDigit(character) || character is '-' or '_' or ' ' or '.')))
        {
            return "progress";
        }

        return value.Trim().Replace(' ', '-').ToLowerInvariant();
    }

    private static async Task<T> WaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancelled);
        if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    private static long? TryInt64(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) && result >= 0
            ? result
            : null;

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? Bound(string? value, int maximum) =>
        value is null ? null : value.Length <= maximum ? value : value.Substring(0, maximum);

    private static void ValidateEndpoint(Uri endpoint, bool allowRemote, bool allowInsecureRemoteHttp)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid Ollama endpoint is required.", nameof(endpoint));
        }

        if (!endpoint.IsLoopback && !allowRemote)
        {
            throw new ArgumentException("Ollama lifecycle endpoints are loopback-only unless remote access is enabled.", nameof(endpoint));
        }

        if (!endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecureRemoteHttp)
        {
            throw new ArgumentException("Remote Ollama endpoints must use HTTPS.", nameof(endpoint));
        }
    }

    private sealed class OllamaSettings
    {
        private OllamaSettings(OllamaGameModelLifecycleOptions options)
        {
            HttpClient = options.HttpClient;
            Endpoint = options.Endpoint;
            Authentication = options.Authentication;
            KeepAlive = options.KeepAlive;
            AllowRemoteEndpoint = options.AllowRemoteEndpoint;
            AllowInsecureRemoteHttp = options.AllowInsecureRemoteHttp;
            MaximumInventoryItems = options.MaximumInventoryItems;
            MaximumResponseBytes = options.MaximumResponseBytes;
        }

        public HttpClient HttpClient { get; }
        public Uri Endpoint { get; }
        public IGameProviderAuthentication Authentication { get; }
        public string KeepAlive { get; }
        public bool AllowRemoteEndpoint { get; }
        public bool AllowInsecureRemoteHttp { get; }
        public int MaximumInventoryItems { get; }
        public int MaximumResponseBytes { get; }

        public static OllamaSettings Create(OllamaGameModelLifecycleOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ValidateEndpoint(options.Endpoint, options.AllowRemoteEndpoint, options.AllowInsecureRemoteHttp);
            if (options.Authentication is null
                || string.IsNullOrWhiteSpace(options.KeepAlive)
                || options.KeepAlive.Length > 64
                || options.KeepAlive.Any(char.IsControl)
                || options.MaximumInventoryItems is < 1 or > 65_536
                || options.MaximumResponseBytes is < 2 or > 256_000_000)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            return new OllamaSettings(options);
        }
    }

    private readonly struct ModelEntry
    {
        public ModelEntry(string id, long? size, string? version)
        {
            Id = id;
            Size = size;
            Version = version;
        }

        public string Id { get; }
        public long? Size { get; }
        public string? Version { get; }
    }
}

internal static class LocalGameModelLifecycleContract
{
    public static string RequireId(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded local model identifier is required.", name)
            : value.Trim();
}
