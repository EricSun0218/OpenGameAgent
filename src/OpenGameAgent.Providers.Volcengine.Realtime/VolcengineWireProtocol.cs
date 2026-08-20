using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenGameAgent.Providers.Volcengine.Realtime;

internal enum VolcengineMessageType : byte
{
    FullClientRequest = 1,
    AudioOnlyClient = 2,
    FullServerResponse = 9,
    AudioOnlyServer = 11,
    FrontEndResultServer = 12,
    Error = 15,
}

internal enum VolcengineSerialization : byte
{
    Raw = 0,
    Json = 1,
}

internal enum VolcengineCompression : byte
{
    None = 0,
    Gzip = 1,
}

internal static class VolcengineEvents
{
    internal const int StartConnection = 1;
    internal const int FinishConnection = 2;
    internal const int ConnectionStarted = 50;
    internal const int ConnectionFailed = 51;
    internal const int ConnectionFinished = 52;
    internal const int StartSession = 100;
    internal const int CancelSession = 101;
    internal const int FinishSession = 102;
    internal const int SessionStarted = 150;
    internal const int SessionCanceled = 151;
    internal const int SessionFinished = 152;
    internal const int SessionFailed = 153;
    internal const int TaskRequest = 200;
    internal const int TtsSentenceStart = 350;
    internal const int TtsSentenceEnd = 351;
    internal const int TtsResponse = 352;
    internal const int TtsEnded = 359;
    internal const int TtsSubtitle = 364;
    internal const int AsrInfo = 450;
    internal const int AsrResponse = 451;
    internal const int AsrEnded = 459;
    internal const int ClientInterrupt = 515;
    internal const int ChatResponse = 550;
    internal const int ChatEnded = 559;

    internal static bool HasNoSessionId(int eventType) =>
        eventType is StartConnection
            or FinishConnection
            or ConnectionStarted
            or ConnectionFailed
            or ConnectionFinished;

    internal static bool HasConnectId(int eventType) =>
        eventType is ConnectionStarted or ConnectionFailed or ConnectionFinished;
}

internal sealed class VolcengineWireMessage
{
    internal VolcengineWireMessage(
        VolcengineMessageType messageType,
        int? eventType,
        string? sessionId,
        string? connectId,
        int? errorCode,
        VolcengineSerialization serialization,
        ReadOnlyMemory<byte> payload)
    {
        MessageType = messageType;
        EventType = eventType;
        SessionId = sessionId;
        ConnectId = connectId;
        ErrorCode = errorCode;
        Serialization = serialization;
        Payload = payload;
    }

    internal VolcengineMessageType MessageType { get; }

    internal int? EventType { get; }

    internal string? SessionId { get; }

    internal string? ConnectId { get; }

    internal int? ErrorCode { get; }

    internal VolcengineSerialization Serialization { get; }

    internal ReadOnlyMemory<byte> Payload { get; }
}

internal static class VolcengineWireProtocol
{
    private const byte WithEvent = 4;
    private const byte PositiveSequence = 1;
    private const byte NegativeSequence = 3;

    internal static byte[] Encode(
        VolcengineMessageType messageType,
        int eventType,
        string? sessionId,
        ReadOnlyMemory<byte> payload,
        VolcengineSerialization serialization,
        VolcengineCompression compression)
    {
        if (messageType is not VolcengineMessageType.FullClientRequest
            and not VolcengineMessageType.AudioOnlyClient)
        {
            throw new ArgumentOutOfRangeException(nameof(messageType));
        }

        if (eventType < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(eventType));
        }

        var effectiveSession = VolcengineEvents.HasNoSessionId(eventType)
            ? null
            : RequireId(sessionId, nameof(sessionId));
        var body = compression == VolcengineCompression.Gzip
            ? Compress(payload.Span)
            : payload.ToArray();
        var sessionBytes = effectiveSession is null
            ? Array.Empty<byte>()
            : System.Text.Encoding.UTF8.GetBytes(effectiveSession);
        var length = checked(4 + 4 + (effectiveSession is null ? 0 : 4 + sessionBytes.Length) + 4 + body.Length);
        var result = new byte[length];
        result[0] = 0x11;
        result[1] = (byte)(((byte)messageType << 4) | WithEvent);
        result[2] = (byte)(((byte)serialization << 4) | (byte)compression);
        result[3] = 0;
        var offset = 4;
        WriteInt32(result, ref offset, eventType);
        if (effectiveSession is not null)
        {
            WriteInt32(result, ref offset, sessionBytes.Length);
            sessionBytes.CopyTo(result, offset);
            offset += sessionBytes.Length;
        }

        WriteInt32(result, ref offset, body.Length);
        body.CopyTo(result, offset);
        return result;
    }

    internal static VolcengineWireMessage Decode(ReadOnlyMemory<byte> source, int maximumPayloadBytes)
    {
        if (maximumPayloadBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        var span = source.Span;
        if (span.Length < 8)
        {
            throw new InvalidDataException("The provider frame is truncated.");
        }

        var version = span[0] >> 4;
        var headerWords = span[0] & 0x0f;
        if (version != 1 || headerWords < 1)
        {
            throw new InvalidDataException("The provider frame has an unsupported protocol header.");
        }

        var headerBytes = checked(headerWords * 4);
        if (headerBytes > span.Length)
        {
            throw new InvalidDataException("The provider frame header is truncated.");
        }

        var messageTypeValue = (byte)(span[1] >> 4);
        if (!Enum.IsDefined(typeof(VolcengineMessageType), messageTypeValue))
        {
            throw new InvalidDataException("The provider frame has an unsupported message type.");
        }

        var messageType = (VolcengineMessageType)messageTypeValue;
        if (messageType is not VolcengineMessageType.FullServerResponse
            and not VolcengineMessageType.AudioOnlyServer
            and not VolcengineMessageType.FrontEndResultServer
            and not VolcengineMessageType.Error)
        {
            throw new InvalidDataException("The provider sent a client-only message type.");
        }

        var flags = (byte)(span[1] & 0x0f);
        var serializationValue = span[2] >> 4;
        var compressionValue = span[2] & 0x0f;
        if (serializationValue is not (byte)VolcengineSerialization.Raw
            and not (byte)VolcengineSerialization.Json)
        {
            throw new InvalidDataException("The provider frame has an unsupported serialization.");
        }

        if (compressionValue is not (byte)VolcengineCompression.None
            and not (byte)VolcengineCompression.Gzip)
        {
            throw new InvalidDataException("The provider frame has an unsupported compression.");
        }

        var offset = headerBytes;
        int? errorCode = null;
        int? eventType = null;
        string? sessionId = null;
        string? connectId = null;
        if (messageType == VolcengineMessageType.Error)
        {
            errorCode = ReadInt32(span, ref offset, "error code");
        }
        else
        {
            if ((flags & 0x03) is PositiveSequence or NegativeSequence)
            {
                _ = ReadInt32(span, ref offset, "sequence");
            }

            if ((flags & WithEvent) != 0)
            {
                eventType = ReadInt32(span, ref offset, "event type");
                if (!VolcengineEvents.HasNoSessionId(eventType.Value))
                {
                    sessionId = ReadString(span, ref offset, 512, "session ID");
                }

                if (VolcengineEvents.HasConnectId(eventType.Value))
                {
                    connectId = ReadString(span, ref offset, 512, "connection ID");
                }
            }
        }

        var payloadLength = ReadInt32(span, ref offset, "payload length");
        if (payloadLength < 0
            || payloadLength > maximumPayloadBytes
            || offset + payloadLength != span.Length)
        {
            throw new InvalidDataException("The provider frame payload length is invalid.");
        }

        var encodedPayload = span.Slice(offset, payloadLength);
        var payload = compressionValue == (byte)VolcengineCompression.Gzip
            ? Decompress(encodedPayload, maximumPayloadBytes)
            : encodedPayload.ToArray();
        if (payload.Length > maximumPayloadBytes)
        {
            throw new InvalidDataException("The provider frame payload exceeded its configured limit.");
        }

        return new VolcengineWireMessage(
            messageType,
            eventType,
            sessionId,
            connectId,
            errorCode,
            (VolcengineSerialization)serializationValue,
            payload);
    }

    private static byte[] Compress(ReadOnlySpan<byte> source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(source);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> source, int maximumBytes)
    {
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximumBytes)];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The decompressed provider payload exceeded its configured limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset, string field)
    {
        if (offset > source.Length - 4)
        {
            throw new InvalidDataException($"The provider frame {field} is truncated.");
        }

        var value = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static string ReadString(
        ReadOnlySpan<byte> source,
        ref int offset,
        int maximumBytes,
        string field)
    {
        var length = ReadInt32(source, ref offset, field + " length");
        if (length < 0 || length > maximumBytes || offset + length > source.Length)
        {
            throw new InvalidDataException($"The provider frame {field} is invalid.");
        }

        string value;
        try
        {
            value = new System.Text.UTF8Encoding(false, true).GetString(source.Slice(offset, length));
        }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The provider frame {field} is not valid UTF-8.", exception);
        }

        offset += length;
        return RequireId(value, field);
    }

    private static string RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"A bounded {name} is required.");
        }

        return value;
    }

    private static void WriteInt32(byte[] destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.AsSpan(offset, 4), value);
        offset += 4;
    }
}
