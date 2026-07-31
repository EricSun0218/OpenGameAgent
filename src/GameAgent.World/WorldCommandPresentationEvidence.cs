using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

/// <summary>
/// Projects one terminal applied native-world receipt into the compact,
/// immutable evidence shape consumed by durable presentations and settlement
/// outboxes. The projection carries only receipt identities and digests; it
/// does not copy game-private effect payloads into presentation storage.
/// </summary>
public static class WorldCommandPresentationEvidence
{
    public const string ContractId =
        "game-agent.world-command-presentation-evidence.v1";

    public static CommittedWorldPresentationEvidence CreateApplied(
        WorldCommandReceipt receipt,
        GameTimePoint? gameTime = null)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (receipt.Status != WorldCommandReceiptStatus.Applied
            || receipt.ResultingCoordinate is null
            || receipt.ResultingStateDigest is null
            || receipt.Effect is null
            || !receipt.Effect.Applied)
        {
            throw new ArgumentException(
                "Presentation evidence requires a terminal applied "
                + "native-world receipt.",
                nameof(receipt));
        }

        var coordinate = receipt.ResultingCoordinate;
        var receiptTime = receipt.Request.EventOccurrence?.OccurredAt;
        if (gameTime is not null
            && (receiptTime is null
                || !string.Equals(
                    gameTime.ClockId,
                    receiptTime.ClockId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    gameTime.TimelineId,
                    receiptTime.TimelineId,
                    StringComparison.Ordinal)
                || gameTime.Epoch != receiptTime.Epoch
                || gameTime.Tick != receiptTime.Tick
                || !string.Equals(
                    gameTime.TimelineId,
                    coordinate.TimelineId,
                    StringComparison.Ordinal)
                || gameTime.Epoch != coordinate.TimelineEpoch))
        {
            throw new ArgumentException(
                "Game time must exactly match authoritative time carried "
                + "by the receipt.",
                nameof(gameTime));
        }

        var receiptEvidence = ToReceiptEvidence(receipt);
        var source = new WorldPresentationSource(
            receipt.ReceiptId,
            CanonicalJsonDigest.ComputeSha256(receiptEvidence),
            BoundedSourceId(receipt.EventInstanceId, 128),
            BoundedSourceId(receipt.CommandId, 128),
            BoundedSourceId(
                receipt.OperationId,
                WorldValidation.MaximumIdentifierUtf8Bytes));
        var binding = new WorldPresentationBinding(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion,
            coordinate.CatalogDigest,
            gameTime,
            receipt.ResultingStateDigest);
        return new CommittedWorldPresentationEvidence(
            source,
            binding,
            WorldPresentationCommitStatus.Applied,
            receipt.OutcomeCode,
            receiptEvidence);
    }

    public static JsonElement ToReceiptEvidence(
        WorldCommandReceipt receipt)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        var effect = receipt.Effect;
        return JsonArrayBuilder.Object(
            ("contract", JsonArrayBuilder.String(ContractId)),
            ("receiptId", JsonArrayBuilder.String(receipt.ReceiptId)),
            ("requestFingerprint", JsonArrayBuilder.String(
                receipt.RequestFingerprint)),
            ("operationId", JsonArrayBuilder.String(
                receipt.OperationId)),
            ("commandId", JsonArrayBuilder.String(receipt.CommandId)),
            ("commandPayloadDigest", JsonArrayBuilder.String(
                receipt.Request.CommandPayloadDigest)),
            ("status", JsonArrayBuilder.String(
                receipt.Status switch
                {
                    WorldCommandReceiptStatus.Applied => "applied",
                    WorldCommandReceiptStatus.Rejected => "rejected",
                    WorldCommandReceiptStatus.Cancelled => "cancelled",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(receipt))
                })),
            ("outcomeCode", JsonArrayBuilder.String(
                receipt.OutcomeCode)),
            ("expectedCoordinate", CoordinateToJson(
                receipt.ExpectedCoordinate)),
            ("resultingCoordinate",
                receipt.ResultingCoordinate is null
                    ? JsonArrayBuilder.Null()
                    : CoordinateToJson(receipt.ResultingCoordinate)),
            ("resultingStateDigest",
                receipt.ResultingStateDigest is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(
                        receipt.ResultingStateDigest)),
            ("eventInstanceId",
                receipt.EventInstanceId is null
                    ? JsonArrayBuilder.Null()
                    : JsonArrayBuilder.String(receipt.EventInstanceId)),
            ("effect", effect is null
                ? JsonArrayBuilder.Null()
                : JsonArrayBuilder.Object(
                    ("applied", JsonArrayBuilder.Boolean(
                        effect.Applied)),
                    ("outcomeCode", JsonArrayBuilder.String(
                        effect.OutcomeCode)),
                    ("typedResultDigest",
                        effect.TypedResult.HasValue
                            ? JsonArrayBuilder.String(
                                CanonicalJsonDigest.ComputeSha256(
                                    effect.TypedResult.Value))
                            : JsonArrayBuilder.Null()))));
    }

    private static JsonElement CoordinateToJson(
        WorldAuthoritativeCoordinate coordinate)
    {
        return JsonArrayBuilder.Object(
            ("worldId", JsonArrayBuilder.String(coordinate.WorldId)),
            ("timelineId", JsonArrayBuilder.String(
                coordinate.TimelineId)),
            ("timelineEpoch", JsonArrayBuilder.Number(
                coordinate.TimelineEpoch)),
            ("saveRevision", JsonArrayBuilder.Number(
                coordinate.SaveRevision)),
            ("stateVersion", JsonArrayBuilder.Number(
                coordinate.StateVersion)),
            ("catalogDigest", JsonArrayBuilder.String(
                coordinate.CatalogDigest)));
    }

    private static string? BoundedSourceId(
        string? value,
        int maximumUtf8Bytes)
    {
        return value is not null
               && Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes
            ? value
            : null;
    }
}
