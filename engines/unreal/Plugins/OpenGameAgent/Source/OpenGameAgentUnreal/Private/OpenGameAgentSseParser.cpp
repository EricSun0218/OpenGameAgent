#include "OpenGameAgentSseParser.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

FOpenGameAgentSseParser::FOpenGameAgentSseParser(
    const int32 InMaximumEventCharacters,
    const int32 InMaximumResponseBytes,
    const int32 InMaximumEvents)
    : MaximumEventCharacters(InMaximumEventCharacters)
    , MaximumResponseBytes(InMaximumResponseBytes)
    , MaximumEvents(InMaximumEvents)
{
    check(MaximumEventCharacters > 0);
    check(MaximumResponseBytes > 0);
    check(MaximumEvents > 0);
}

bool FOpenGameAgentSseParser::Feed(
    const TArrayView<const uint8> Bytes,
    const FEventSink Sink,
    FString& Error)
{
    if (bFinished)
    {
        Error = TEXT("The event stream was already finished.");
        return false;
    }

    TotalBytes += Bytes.Num();
    if (TotalBytes > MaximumResponseBytes)
    {
        Error = TEXT("The event stream exceeded its response byte limit.");
        return false;
    }

    for (const uint8 Byte : Bytes)
    {
        if (Byte == '\n')
        {
            if (!ConsumeLine(PendingLine, Sink, Error))
            {
                return false;
            }
            PendingLine.Reset();
            continue;
        }

        PendingLine.Add(Byte);
        if (PendingLine.Num() > MaximumEventCharacters * 4)
        {
            Error = TEXT("An event-stream line exceeded its byte limit.");
            return false;
        }
    }

    return true;
}

bool FOpenGameAgentSseParser::Finish(const FEventSink Sink, FString& Error)
{
    return FinishInternal(true, Sink, Error);
}

bool FOpenGameAgentSseParser::FinishOpenStream(const FEventSink Sink, FString& Error)
{
    return FinishInternal(false, Sink, Error);
}

bool FOpenGameAgentSseParser::FinishInternal(
    const bool bRequireTerminalResult,
    const FEventSink Sink,
    FString& Error)
{
    if (bFinished)
    {
        return !bRequireTerminalResult || bSawTerminalResult;
    }

    if (PendingLine.Num() > 0 && !ConsumeLine(PendingLine, Sink, Error))
    {
        return false;
    }
    PendingLine.Reset();

    if (!EventData.IsEmpty() && !Dispatch(Sink, Error))
    {
        return false;
    }

    bFinished = true;
    if (bRequireTerminalResult && !bSawTerminalResult)
    {
        Error = TEXT("The server stream ended without a terminal result event.");
        return false;
    }
    if (bSawTerminalResult)
    {
        Sink(TEXT("result"), TerminalResultData);
    }
    return true;
}

bool FOpenGameAgentSseParser::ConsumeLine(
    const TArrayView<const uint8> Bytes,
    const FEventSink Sink,
    FString& Error)
{
    int32 Length = Bytes.Num();
    if (Length > 0 && Bytes[Length - 1] == '\r')
    {
        --Length;
    }

    FString Line;
    if (!DecodeUtf8(TArrayView<const uint8>(Bytes.GetData(), Length), Line, Error))
    {
        return false;
    }

    if (Line.IsEmpty())
    {
        return Dispatch(Sink, Error);
    }
    if (Line[0] == ':')
    {
        return true;
    }

    FString Field;
    FString Value;
    if (!Line.Split(TEXT(":"), &Field, &Value, ESearchCase::CaseSensitive, ESearchDir::FromStart))
    {
        Field = Line;
    }
    if (Value.StartsWith(TEXT(" ")))
    {
        Value.RightChopInline(1, EAllowShrinking::No);
    }

    if (Field.Equals(TEXT("event"), ESearchCase::IgnoreCase))
    {
        if (Value.IsEmpty() || Value.Len() > 256 || Value.Contains(TEXT("\r")) || Value.Contains(TEXT("\n")))
        {
            Error = TEXT("The server emitted an invalid event name.");
            return false;
        }
        EventName = MoveTemp(Value);
    }
    else if (Field.Equals(TEXT("data"), ESearchCase::IgnoreCase))
    {
        const int64 NextLength = static_cast<int64>(EventData.Len())
            + (EventData.IsEmpty() ? 0 : 1)
            + Value.Len();
        if (NextLength > MaximumEventCharacters)
        {
            Error = TEXT("A server event exceeded its character limit.");
            return false;
        }
        if (!EventData.IsEmpty())
        {
            EventData.AppendChar('\n');
        }
        EventData.Append(Value);
    }

    return true;
}

bool FOpenGameAgentSseParser::Dispatch(const FEventSink Sink, FString& Error)
{
    if (EventData.IsEmpty())
    {
        EventName = TEXT("message");
        return true;
    }
    if (bSawTerminalResult)
    {
        Error = TEXT("The server emitted data after its terminal result.");
        return false;
    }
    ++EventCount;
    if (EventCount > MaximumEvents)
    {
        Error = TEXT("The server stream exceeded its event-count limit.");
        return false;
    }

    TSharedPtr<FJsonValue> Parsed;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(EventData);
    if (!FJsonSerializer::Deserialize(Reader, Parsed) || !Parsed.IsValid())
    {
        Error = TEXT("The server emitted invalid JSON event data.");
        return false;
    }

    const FString CurrentName = EventName;
    const FString CurrentData = EventData;
    EventName = TEXT("message");
    EventData.Reset();

    if (CurrentName.Equals(TEXT("result"), ESearchCase::CaseSensitive))
    {
        bSawTerminalResult = true;
        TerminalResultData = CurrentData;
        return true;
    }
    Sink(CurrentName, CurrentData);
    return true;
}

bool FOpenGameAgentSseParser::DecodeUtf8(
    const TArrayView<const uint8> Bytes,
    FString& Value,
    FString& Error)
{
    if (Bytes.Num() == 0)
    {
        Value.Reset();
        return true;
    }

    const FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Bytes.GetData()), Bytes.Num());
    Value = FString(Converted.Length(), Converted.Get());

    const FTCHARToUTF8 RoundTrip(*Value);
    if (RoundTrip.Length() != Bytes.Num()
        || FMemory::Memcmp(RoundTrip.Get(), Bytes.GetData(), Bytes.Num()) != 0)
    {
        Error = TEXT("The server stream contains invalid UTF-8.");
        return false;
    }
    return true;
}
