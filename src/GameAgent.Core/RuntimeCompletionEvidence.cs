using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal static class RuntimeCompletionEvidence
{
    internal const string ExtensionName = "runtimeCompletionEvidence";

    private const string EvidenceVersion =
        "runtime-completion-evidence.v1";

    internal static JsonElement Create(
        AgentRun run,
        string turnId,
        string attemptId,
        string streamAttemptId,
        JsonElement output)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        return JsonArrayBuilder.Object(
            ("evidenceVersion", JsonArrayBuilder.String(EvidenceVersion)),
            ("runId", JsonArrayBuilder.String(run.RunId)),
            ("runtimeGeneration",
                JsonArrayBuilder.Number(run.RuntimeGeneration)),
            ("turnId",
                JsonArrayBuilder.String(
                    RuntimeGuard.RequiredId(turnId, nameof(turnId)))),
            ("attemptId",
                JsonArrayBuilder.String(
                    RuntimeGuard.RequiredId(attemptId, nameof(attemptId)))),
            ("streamAttemptId",
                JsonArrayBuilder.String(
                    RuntimeGuard.RequiredId(
                        streamAttemptId,
                        nameof(streamAttemptId)))),
            ("outputDigest",
                JsonArrayBuilder.String(
                    CanonicalJsonDigest.ComputeSha256(output))));
    }

    internal static void Validate(
        JsonElement evidence,
        RuntimeEvent runtimeEvent)
    {
        try
        {
            if (evidence.ValueKind != JsonValueKind.Object
                || evidence.EnumerateObject().Count() != 7
                || !RequiredString(
                    evidence,
                    "evidenceVersion",
                    out var version)
                || !string.Equals(
                    version,
                    EvidenceVersion,
                    StringComparison.Ordinal)
                || !RequiredString(evidence, "runId", out var runId)
                || !RequiredInt64(
                    evidence,
                    "runtimeGeneration",
                    out var runtimeGeneration)
                || !RequiredString(evidence, "turnId", out var turnId)
                || !RequiredString(evidence, "attemptId", out var attemptId)
                || !RequiredString(
                    evidence,
                    "streamAttemptId",
                    out var streamAttemptId)
                || !RequiredString(
                    evidence,
                    "outputDigest",
                    out var outputDigest)
                || !CanonicalJsonDigest.IsSha256(outputDigest)
                || !string.Equals(
                    runId,
                    runtimeEvent.RunId,
                    StringComparison.Ordinal)
                || runtimeGeneration != runtimeEvent.RuntimeGeneration
                || !string.Equals(
                    turnId,
                    runtimeEvent.TurnId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    attemptId,
                    runtimeEvent.AttemptId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    streamAttemptId,
                    runtimeEvent.StreamAttemptId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    outputDigest,
                    CanonicalJsonDigest.ComputeSha256(runtimeEvent.Payload),
                    StringComparison.Ordinal)
                || runtimeEvent.ProviderId is not null)
            {
                throw new InvalidDataException(
                    "A runtime completion has invalid source evidence.");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "A runtime completion has invalid source evidence.",
                exception);
        }
    }

    private static bool RequiredString(
        JsonElement source,
        string name,
        out string? value)
    {
        value = null;
        return source.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(
                   value = property.GetString());
    }

    private static bool RequiredInt64(
        JsonElement source,
        string name,
        out long value)
    {
        value = 0;
        return source.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }
}
