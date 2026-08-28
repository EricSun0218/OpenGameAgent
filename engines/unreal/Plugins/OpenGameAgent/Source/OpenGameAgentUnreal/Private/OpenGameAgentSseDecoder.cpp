#include "OpenGameAgentSseDecoder.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
    int32 FindDelimiter(const TArray<uint8>& Bytes, int32& OutLength)
    {
        for (int32 Index = 0; Index + 1 < Bytes.Num(); ++Index)
        {
            if (Bytes[Index] == '\n' && Bytes[Index + 1] == '\n')
            {
                OutLength = 2;
                return Index;
            }
            if (Index + 3 < Bytes.Num()
                && Bytes[Index] == '\r'
                && Bytes[Index + 1] == '\n'
                && Bytes[Index + 2] == '\r'
                && Bytes[Index + 3] == '\n')
            {
                OutLength = 4;
                return Index;
            }
        }
        OutLength = 0;
        return INDEX_NONE;
    }
}

FOpenGameAgentSseDecoder::FOpenGameAgentSseDecoder(const int32 InMaximumEventBytes)
    : MaximumEventBytes(FMath::Clamp(InMaximumEventBytes, 1024, 8 * 1024 * 1024))
{
}

bool FOpenGameAgentSseDecoder::Push(
    const uint8* Data,
    const int64 Length,
    TArray<FOpenGameAgentDecodedEvent>& OutEvents,
    FString& OutError)
{
    if (Length < 0 || Length > MAX_int32 || (Length > 0 && Data == nullptr))
    {
        OutError = TEXT("invalid-stream-chunk");
        return false;
    }
    if (Pending.Num() + Length > MaximumEventBytes)
    {
        OutError = TEXT("event-too-large");
        return false;
    }
    Pending.Append(Data, static_cast<int32>(Length));
    return ParseAvailable(OutEvents, OutError);
}

bool FOpenGameAgentSseDecoder::Finish(TArray<FOpenGameAgentDecodedEvent>& OutEvents, FString& OutError)
{
    if (!ParseAvailable(OutEvents, OutError)) return false;
    for (const uint8 Byte : Pending)
    {
        if (Byte != ' ' && Byte != '\t' && Byte != '\r' && Byte != '\n')
        {
            OutError = TEXT("incomplete-event");
            return false;
        }
    }
    Pending.Reset();
    return true;
}

bool FOpenGameAgentSseDecoder::ParseAvailable(TArray<FOpenGameAgentDecodedEvent>& OutEvents, FString& OutError)
{
    while (true)
    {
        int32 DelimiterLength = 0;
        const int32 Delimiter = FindDelimiter(Pending, DelimiterLength);
        if (Delimiter == INDEX_NONE) return true;
        if (Delimiter > MaximumEventBytes)
        {
            OutError = TEXT("event-too-large");
            return false;
        }

        TArray<uint8> Frame;
        Frame.Append(Pending.GetData(), Delimiter);
        Pending.RemoveAt(0, Delimiter + DelimiterLength, EAllowShrinking::No);

        FOpenGameAgentDecodedEvent Event;
        bool bHasEvent = false;
        if (!ParseFrame(Frame, Event, bHasEvent, OutError)) return false;
        if (bHasEvent) OutEvents.Add(MoveTemp(Event));
    }
}

bool FOpenGameAgentSseDecoder::ParseFrame(
    const TArray<uint8>& Frame,
    FOpenGameAgentDecodedEvent& OutEvent,
    bool& bOutHasEvent,
    FString& OutError) const
{
    bOutHasEvent = false;
    if (Frame.IsEmpty()) return true;
    FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Frame.GetData()), Frame.Num());
    FString Text(Converted.Length(), Converted.Get());
    Text.ReplaceInline(TEXT("\r"), TEXT(""));
    TArray<FString> Lines;
    Text.ParseIntoArrayLines(Lines, false);
    TArray<FString> DataLines;
    OutEvent.Name = TEXT("message");
    for (const FString& Line : Lines)
    {
        if (Line.StartsWith(TEXT("id:"))) OutEvent.Id = Line.RightChop(3).TrimStart();
        else if (Line.StartsWith(TEXT("event:"))) OutEvent.Name = Line.RightChop(6).TrimStart();
        else if (Line.StartsWith(TEXT("data:"))) DataLines.Add(Line.RightChop(5).TrimStart());
    }
    if (DataLines.IsEmpty()) return true;
    OutEvent.Json = FString::Join(DataLines, TEXT("\n"));
    TSharedPtr<FJsonValue> JsonValue;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(OutEvent.Json);
    if (!FJsonSerializer::Deserialize(Reader, JsonValue) || !JsonValue.IsValid())
    {
        OutError = TEXT("invalid-event-json");
        return false;
    }
    bOutHasEvent = true;
    return true;
}
