using System.Net.Http;

namespace OpenGameAgent.Client;

public sealed class RuntimeClientOptions
{
    public required Uri BaseAddress { get; init; }

    public required long MaxRequestBytes { get; init; }

    public required long MaxHttpResponseBytes { get; init; }

    public required long MaxAttachmentResponseBytes { get; init; }

    public required long MaxSseEventBytes { get; init; }

    public Func<CancellationToken, ValueTask<string?>>? AccessTokenProvider { get; init; }

    public Action<SocketsHttpHandler>? ConfigureHttpHandler { get; init; }
}

public sealed class RuntimeSseRetryPolicy
{
    public required TimeSpan DefaultDelay { get; init; }

    public required TimeSpan MaximumDelay { get; init; }

    public required int MaximumConnectionAttempts { get; init; }
}
