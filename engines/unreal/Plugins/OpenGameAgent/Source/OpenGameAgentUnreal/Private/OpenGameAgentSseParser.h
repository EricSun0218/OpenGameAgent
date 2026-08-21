#pragma once

#include "CoreMinimal.h"

class FOpenGameAgentSseParser final
{
public:
    using FEventSink = TFunctionRef<void(const FString& EventName, const FString& Json)>;

    FOpenGameAgentSseParser(
        int32 InMaximumEventCharacters,
        int32 InMaximumResponseBytes,
        int32 InMaximumEvents = 65'536);

    bool Feed(const TArrayView<const uint8> Bytes, FEventSink Sink, FString& Error);
    bool Finish(FEventSink Sink, FString& Error);
    bool FinishOpenStream(FEventSink Sink, FString& Error);
    bool HasTerminalResult() const { return bSawTerminalResult; }

private:
    int32 MaximumEventCharacters;
    int32 MaximumResponseBytes;
    int32 MaximumEvents;
    int32 EventCount = 0;
    int64 TotalBytes = 0;
    TArray<uint8> PendingLine;
    FString EventName = TEXT("message");
    FString EventData;
    FString TerminalResultData;
    bool bSawTerminalResult = false;
    bool bFinished = false;

    bool ConsumeLine(const TArrayView<const uint8> Bytes, FEventSink Sink, FString& Error);
    bool Dispatch(FEventSink Sink, FString& Error);
    bool FinishInternal(bool bRequireTerminalResult, FEventSink Sink, FString& Error);
    static bool DecodeUtf8(const TArrayView<const uint8> Bytes, FString& Value, FString& Error);
};
