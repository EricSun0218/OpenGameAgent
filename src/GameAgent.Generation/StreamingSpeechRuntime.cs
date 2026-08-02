using System.Runtime.CompilerServices;

namespace GameAgent.Generation;

public sealed class StreamingSpeechRuntime
{
    private readonly IReadOnlyList<IStreamingSpeechProvider> _providers;
    private readonly GenerationRuntimeOptions _limits;

    public StreamingSpeechRuntime(
        IEnumerable<IStreamingSpeechProvider> providers,
        GenerationRuntimeOptions? limits = null)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        var materialized = providers.Take(129).ToArray();
        if (materialized.Length is 0 or > 128
            || materialized.Any(provider => provider is null))
        {
            throw new ArgumentException(
                "Configure between 1 and 128 streaming speech providers.",
                nameof(providers));
        }

        _providers = materialized;
        _limits = limits ?? new GenerationRuntimeOptions();
        _limits.Validate();
    }

    public async IAsyncEnumerable<SpeechStreamEvent> StreamAsync(
        GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = GenerationValidation.SnapshotRequest(request, _limits);
        if (snapshot.Modality != GenerationModalities.Speech)
        {
            throw new ArgumentException(
                "Streaming requires a speech generation request.",
                nameof(request));
        }

        GenerationProviderException? lastNotAccepted = null;
        foreach (var provider in _providers)
        {
            var emittedAudio = false;
            var tryNextProvider = false;
            var completedStream = false;
            SpeechStreamEvent? pendingStarted = null;
            long? previousSequence = null;
            string? mediaType = null;
            await using var enumerator = provider
                .StreamSpeechAsync(snapshot, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                SpeechStreamEvent? item = null;
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        item = enumerator.Current;
                    }
                }
                catch (GenerationProviderException exception) when (
                    !emittedAudio
                    && exception.Acceptance == GenerationAcceptance.NotAccepted)
                {
                    lastNotAccepted = exception;
                    tryNextProvider = true;
                    break;
                }
                catch (Exception exception) when (emittedAudio)
                {
                    throw new GenerationOperationException(
                        "speech_stream_interrupted_after_output",
                        "Speech output already reached the caller; provider fallback is disabled to prevent mixed audio.",
                        outcomeUncertain: true,
                        exception);
                }

                if (!moved)
                {
                    if (!completedStream)
                    {
                        throw new GenerationOperationException(
                            emittedAudio
                                ? "speech_stream_interrupted_after_output"
                                : "speech_stream_contract_invalid",
                            emittedAudio
                                ? "Speech output ended without a completion event."
                                : "A streaming speech provider ended without completing.",
                            outcomeUncertain: emittedAudio);
                    }

                    yield break;
                }

                Validate(item!);
                if (completedStream
                    || previousSequence.HasValue
                    && item!.Sequence <= previousSequence.Value
                    || mediaType is not null
                    && !string.Equals(mediaType, item!.MediaType, StringComparison.Ordinal))
                {
                    throw new GenerationOperationException(
                        "speech_stream_contract_invalid",
                        "Speech stream lifecycle, sequence, or media type changed unexpectedly.",
                        outcomeUncertain: emittedAudio);
                }

                previousSequence = item!.Sequence;
                mediaType ??= item.MediaType;
                if (item.Kind == SpeechStreamEventKinds.Started)
                {
                    if (pendingStarted is not null || emittedAudio)
                    {
                        throw new GenerationOperationException(
                            "speech_stream_contract_invalid",
                            "A speech stream emitted more than one start event.",
                            outcomeUncertain: emittedAudio);
                    }

                    pendingStarted = Snapshot(item);
                    continue;
                }

                if (pendingStarted is not null)
                {
                    yield return pendingStarted;
                    pendingStarted = null;
                }

                emittedAudio |= item!.Kind == SpeechStreamEventKinds.Audio
                                && !item.Audio.IsEmpty;
                completedStream = item.Kind == SpeechStreamEventKinds.Completed;
                yield return Snapshot(item);
            }

            if (!tryNextProvider)
            {
                yield break;
            }
        }

        throw new GenerationOperationException(
            lastNotAccepted?.ReasonCode ?? "speech_stream_provider_unavailable",
            lastNotAccepted?.Message
            ?? "No streaming speech provider could accept the request.");
    }

    private static void Validate(SpeechStreamEvent item)
    {
        if (item is null
            || item.Kind is not SpeechStreamEventKinds.Started
                and not SpeechStreamEventKinds.Audio
                and not SpeechStreamEventKinds.Completed
            || item.Sequence < 0
            || item.Elapsed < TimeSpan.Zero
            || item.MediaType.Length is < 1 or > 255
            || item.Kind == SpeechStreamEventKinds.Audio && item.Audio.IsEmpty
            || item.Kind != SpeechStreamEventKinds.Audio && !item.Audio.IsEmpty)
        {
            throw new GenerationOperationException(
                "speech_stream_contract_invalid",
                "A streaming speech provider emitted an invalid event.");
        }
    }

    private static SpeechStreamEvent Snapshot(SpeechStreamEvent item) => new()
    {
        Kind = item.Kind,
        MediaType = item.MediaType,
        Audio = item.Audio.ToArray(),
        Sequence = item.Sequence,
        Elapsed = item.Elapsed
    };
}
