using System.Collections.ObjectModel;

namespace OpenGameAgent.Realtime;

public enum RealtimeConversationState
{
    Idle,
    Starting,
    Active,
    Stopping,
    Closed,
    Faulted,
}

public enum RealtimeOutputModality
{
    Audio,
    Text,
}

public enum RealtimeTextRole
{
    User,
    Assistant,
    Developer,
}

public enum RealtimeConversationEventKind
{
    SessionUpdated,
    InputSpeechStarted,
    InputTranscriptDelta,
    InputTranscriptDone,
    OutputTranscriptDelta,
    OutputTranscriptDone,
    AudioOutput,
    ResponseStarted,
    ResponseCancelled,
    ResponseDone,
    HandoffRequested,
    BehaviorRequested,
    BehaviorCancelled,
    Error,
    Closed,
}

public enum RealtimeHandoffPhase
{
    Commentary,
    Final,
}

public enum RealtimeBehaviorDisposition
{
    Started,
    Replaced,
    Completed,
    Cancelled,
    Rejected,
    Failed,
}

public sealed class RealtimeAudioFrame
{
    public RealtimeAudioFrame(
        byte[] pcm16,
        int sampleRate = 24_000,
        int channels = 1,
        string? itemId = null)
    {
        if (pcm16 is null || pcm16.Length == 0 || pcm16.Length % 2 != 0)
        {
            throw new ArgumentException("A non-empty PCM16 frame with an even byte length is required.", nameof(pcm16));
        }

        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is < 1 or > 8 || pcm16.Length % (2 * channels) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (itemId is { Length: > 256 } || itemId?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The item ID is invalid.", nameof(itemId));
        }

        Pcm16 = (byte[])pcm16.Clone();
        SampleRate = sampleRate;
        Channels = channels;
        ItemId = itemId;
    }

    public ReadOnlyMemory<byte> Pcm16 { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int SamplesPerChannel => Pcm16.Length / 2 / Channels;

    public string? ItemId { get; }

    public int DurationMilliseconds => checked((int)((long)SamplesPerChannel * 1_000L / SampleRate));
}

public sealed class RealtimeConversationOptions
{
    public string Model { get; set; } = "gpt-realtime-1.5";

    public string Voice { get; set; } = "alloy";

    public string Instructions { get; set; } = string.Empty;

    public RealtimeOutputModality OutputModality { get; set; } = RealtimeOutputModality.Audio;

    public bool ClientManagedHandoffs { get; set; }

    public bool FlushTranscriptTailOnClose { get; set; } = true;

    public int AudioQueueCapacity { get; set; } = 256;

    public int CommandQueueCapacity { get; set; } = 64;

    public int EventQueueCapacity { get; set; } = 512;

    public int MaximumConcurrentBehaviors { get; set; } = 16;

    public int MaximumAudioFrameBytes { get; set; } = 262_144;

    public int MaximumTextCharacters { get; set; } = 65_536;

    public int MaximumEventCharacters { get; set; } = 1_000_000;

    public int MaximumStartupContextCharacters { get; set; } = 65_536;

    public int EventHandlerTimeoutMilliseconds { get; set; } = 5_000;

    public int ShutdownTimeoutMilliseconds { get; set; } = 10_000;

    public string? StartupContextJson { get; set; }

    internal RealtimeConversationOptions Snapshot()
    {
        var copy = (RealtimeConversationOptions)MemberwiseClone();
        copy.Validate();
        return copy;
    }

    internal void Validate()
    {
        RequireId(Model, nameof(Model));
        RequireId(Voice, nameof(Voice));
        if (!Enum.IsDefined(typeof(RealtimeOutputModality), OutputModality))
        {
            throw new ArgumentOutOfRangeException(nameof(OutputModality));
        }

        RequireRange(AudioQueueCapacity, 1, 4_096, nameof(AudioQueueCapacity));
        RequireRange(CommandQueueCapacity, 1, 4_096, nameof(CommandQueueCapacity));
        RequireRange(EventQueueCapacity, 1, 16_384, nameof(EventQueueCapacity));
        RequireRange(MaximumConcurrentBehaviors, 1, 1_024, nameof(MaximumConcurrentBehaviors));
        RequireRange(MaximumAudioFrameBytes, 2, 4_194_304, nameof(MaximumAudioFrameBytes));
        RequireRange(MaximumTextCharacters, 1, 4_000_000, nameof(MaximumTextCharacters));
        RequireRange(MaximumEventCharacters, 1, 8_000_000, nameof(MaximumEventCharacters));
        RequireRange(MaximumStartupContextCharacters, 1, 1_000_000, nameof(MaximumStartupContextCharacters));
        RequireRange(EventHandlerTimeoutMilliseconds, 10, 120_000, nameof(EventHandlerTimeoutMilliseconds));
        RequireRange(ShutdownTimeoutMilliseconds, 100, 120_000, nameof(ShutdownTimeoutMilliseconds));
        if (Instructions.Length > MaximumTextCharacters)
        {
            throw new ArgumentException("Realtime instructions exceed the configured text limit.", nameof(Instructions));
        }

        if (StartupContextJson is { } context)
        {
            if (context.Length > MaximumStartupContextCharacters)
            {
                throw new ArgumentException("Realtime startup context exceeds the configured limit.", nameof(StartupContextJson));
            }

            using var _ = System.Text.Json.JsonDocument.Parse(context, new System.Text.Json.JsonDocumentOptions
            {
                MaxDepth = 64,
            });
        }
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void RequireId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded identifier is required.", name);
        }
    }
}

public sealed class RealtimeHandoffRequest
{
    public RealtimeHandoffRequest(
        string handoffId,
        string transcript,
        string? contextJson = null,
        bool clientManaged = false,
        bool isTranscriptTail = false)
    {
        HandoffId = Require(handoffId, 256, nameof(handoffId));
        Transcript = Require(transcript, 1_000_000, nameof(transcript));
        ContextJson = contextJson;
        ClientManaged = clientManaged;
        IsTranscriptTail = isTranscriptTail;
        if (contextJson is not null)
        {
            if (contextJson.Length > 1_000_000)
            {
                throw new ArgumentException("Handoff context exceeds the configured contract limit.", nameof(contextJson));
            }

            using var _ = System.Text.Json.JsonDocument.Parse(
                contextJson,
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
        }
    }

    public string HandoffId { get; }

    public string Transcript { get; }

    public string? ContextJson { get; }

    /// <summary>
    /// Gets whether the host, rather than an attached automatic bridge, owns dispatch.
    /// </summary>
    public bool ClientManaged { get; }

    /// <summary>
    /// Gets whether this request flushes accepted input transcript when the realtime session closes.
    /// Tail handoffs update the authoritative agent without producing output on the closed session.
    /// </summary>
    public bool IsTranscriptTail { get; }

    private static string Require(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? throw new ArgumentException("A bounded value is required.", name)
            : value;
}

public sealed class RealtimeBehaviorRequest
{
    public RealtimeBehaviorRequest(
        string behaviorId,
        string channel,
        string behavior,
        string argumentsJson,
        int priority = 0)
    {
        BehaviorId = RequireId(behaviorId, nameof(behaviorId));
        Channel = RequireId(channel, nameof(channel));
        Behavior = RequireId(behavior, nameof(behavior));
        if (argumentsJson is null || argumentsJson.Length > 1_000_000)
        {
            throw new ArgumentException("Behavior arguments exceed the contract limit.", nameof(argumentsJson));
        }

        using var document = System.Text.Json.JsonDocument.Parse(
            argumentsJson,
            new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException("Behavior arguments must be a JSON object.", nameof(argumentsJson));
        }

        ArgumentsJson = argumentsJson;
        Priority = priority;
    }

    public string BehaviorId { get; }

    public string Channel { get; }

    public string Behavior { get; }

    public string ArgumentsJson { get; }

    public int Priority { get; }

    private static string RequireId(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded identifier is required.", name)
            : value;
}

public sealed class RealtimeConversationEvent
{
    public RealtimeConversationEvent(
        RealtimeConversationEventKind kind,
        string? text = null,
        RealtimeAudioFrame? audio = null,
        RealtimeHandoffRequest? handoff = null,
        RealtimeBehaviorRequest? behavior = null,
        string? itemId = null,
        string? responseId = null,
        string? error = null)
    {
        if (!Enum.IsDefined(typeof(RealtimeConversationEventKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Text = text;
        Audio = audio;
        Handoff = handoff;
        Behavior = behavior;
        ItemId = itemId;
        ResponseId = responseId;
        Error = error;
    }

    public RealtimeConversationEventKind Kind { get; }

    public string? Text { get; }

    public RealtimeAudioFrame? Audio { get; }

    public RealtimeHandoffRequest? Handoff { get; }

    public RealtimeBehaviorRequest? Behavior { get; }

    public string? ItemId { get; }

    public string? ResponseId { get; }

    public string? Error { get; }
}

public sealed class RealtimeBehaviorResult
{
    public RealtimeBehaviorResult(
        string behaviorId,
        RealtimeBehaviorDisposition disposition,
        string? detailsJson = null)
    {
        BehaviorId = behaviorId ?? throw new ArgumentNullException(nameof(behaviorId));
        if (!Enum.IsDefined(typeof(RealtimeBehaviorDisposition), disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (detailsJson is not null)
        {
            if (detailsJson.Length > 1_000_000)
            {
                throw new ArgumentException("Behavior result details exceed the contract limit.", nameof(detailsJson));
            }

            using var _ = System.Text.Json.JsonDocument.Parse(
                detailsJson,
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
        }

        Disposition = disposition;
        DetailsJson = detailsJson;
    }

    public string BehaviorId { get; }

    public RealtimeBehaviorDisposition Disposition { get; }

    public string? DetailsJson { get; }
}

public delegate ValueTask RealtimeConversationEventHandler(
    RealtimeConversationEvent value,
    CancellationToken cancellationToken);

public interface IRealtimeBehaviorHandler
{
    ValueTask<RealtimeBehaviorResult> ExecuteAsync(
        RealtimeBehaviorRequest request,
        CancellationToken cancellationToken);
}

public interface IRealtimeTransportSession : IAsyncDisposable
{
    IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(CancellationToken cancellationToken);

    ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken cancellationToken);

    ValueTask SendTextAsync(string text, RealtimeTextRole role, CancellationToken cancellationToken);

    ValueTask SendHandoffAsync(
        string handoffId,
        string text,
        RealtimeHandoffPhase phase,
        bool completed,
        CancellationToken cancellationToken);

    ValueTask SendBehaviorResultAsync(RealtimeBehaviorResult result, CancellationToken cancellationToken);

    ValueTask CancelResponseAsync(CancellationToken cancellationToken);

    ValueTask TruncateAudioAsync(string itemId, int audioEndMilliseconds, CancellationToken cancellationToken);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

public interface IRealtimeTransport
{
    ValueTask<IRealtimeTransportSession> ConnectAsync(
        RealtimeConversationOptions options,
        CancellationToken cancellationToken);
}
