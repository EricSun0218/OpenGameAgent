using Amazon.BedrockRuntime.Model;

namespace OpenGameAgent.Providers.Bedrock;

public delegate IAsyncEnumerable<BedrockProtocolEvent> BedrockConverseTransport(
    ConverseStreamRequest request,
    CancellationToken cancellationToken);

public enum BedrockProtocolEventKind
{
    MessageStarted,
    ContentStarted,
    ContentDelta,
    ContentStopped,
    MessageStopped,
    Metadata,
}

public sealed class BedrockProtocolEvent
{
    private byte[]? _redactedReasoning;

    private BedrockProtocolEvent(BedrockProtocolEventKind kind)
    {
        Kind = kind;
    }

    public BedrockProtocolEventKind Kind { get; }

    public int ContentIndex { get; private set; } = -1;

    public string? Role { get; private set; }

    public string? Text { get; private set; }

    public string? ReasoningText { get; private set; }

    public string? ReasoningSignature { get; private set; }

    public bool HasRedactedReasoning => _redactedReasoning is not null;

    public ReadOnlyMemory<byte> RedactedReasoning => _redactedReasoning ?? ReadOnlyMemory<byte>.Empty;

    public string? ToolCallId { get; private set; }

    public string? ToolName { get; private set; }

    public string? ToolArgumentsDelta { get; private set; }

    public string? StopReason { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    public static BedrockProtocolEvent MessageStart(string role) => new(BedrockProtocolEventKind.MessageStarted)
    {
        Role = role ?? throw new ArgumentNullException(nameof(role)),
    };

    public static BedrockProtocolEvent ContentStart(int index, string? toolCallId = null, string? toolName = null) =>
        new(BedrockProtocolEventKind.ContentStarted)
        {
            ContentIndex = RequireIndex(index),
            ToolCallId = toolCallId,
            ToolName = toolName,
        };

    public static BedrockProtocolEvent TextDelta(int index, string text) => new(BedrockProtocolEventKind.ContentDelta)
    {
        ContentIndex = RequireIndex(index),
        Text = text ?? throw new ArgumentNullException(nameof(text)),
    };

    public static BedrockProtocolEvent ReasoningDelta(int index, string? text = null, string? signature = null) =>
        new(BedrockProtocolEventKind.ContentDelta)
        {
            ContentIndex = RequireIndex(index),
            ReasoningText = text,
            ReasoningSignature = signature,
        };

    public static BedrockProtocolEvent RedactedReasoningDelta(int index, ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("Redacted reasoning data cannot be empty.", nameof(data));
        }

        return new BedrockProtocolEvent(BedrockProtocolEventKind.ContentDelta)
        {
            ContentIndex = RequireIndex(index),
            _redactedReasoning = data.ToArray(),
        };
    }

    public static BedrockProtocolEvent ToolDelta(int index, string arguments) => new(BedrockProtocolEventKind.ContentDelta)
    {
        ContentIndex = RequireIndex(index),
        ToolArgumentsDelta = arguments ?? throw new ArgumentNullException(nameof(arguments)),
    };

    public static BedrockProtocolEvent ContentStop(int index) => new(BedrockProtocolEventKind.ContentStopped)
    {
        ContentIndex = RequireIndex(index),
    };

    public static BedrockProtocolEvent MessageStop(string stopReason) => new(BedrockProtocolEventKind.MessageStopped)
    {
        StopReason = string.IsNullOrWhiteSpace(stopReason)
            ? throw new ArgumentException("A Bedrock stop reason is required.", nameof(stopReason))
            : stopReason,
    };

    public static BedrockProtocolEvent Usage(long input, long output, long cacheRead = 0, long cacheWrite = 0) =>
        new(BedrockProtocolEventKind.Metadata)
        {
            InputTokens = RequireCount(input, nameof(input)),
            OutputTokens = RequireCount(output, nameof(output)),
            CacheReadTokens = RequireCount(cacheRead, nameof(cacheRead)),
            CacheWriteTokens = RequireCount(cacheWrite, nameof(cacheWrite)),
        };

    private static int RequireIndex(int index) => index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(index));

    private static long RequireCount(long count, string name) => count >= 0 ? count : throw new ArgumentOutOfRangeException(name);
}
