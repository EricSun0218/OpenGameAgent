#pragma once

#include "CoreMinimal.h"

struct FOpenGameAgentDecodedEvent
{
    FString Id;
    FString Name;
    FString Json;
};

/** Incremental, byte-bounded SSE decoder. It is engine-independent and does not retain response bodies. */
class OPENGAMEAGENTUNREAL_API FOpenGameAgentSseDecoder
{
public:
    explicit FOpenGameAgentSseDecoder(int32 InMaximumEventBytes = 1024 * 1024);

    bool Push(const uint8* Data, int64 Length, TArray<FOpenGameAgentDecodedEvent>& OutEvents, FString& OutError);
    bool Finish(TArray<FOpenGameAgentDecodedEvent>& OutEvents, FString& OutError);

private:
    bool ParseAvailable(TArray<FOpenGameAgentDecodedEvent>& OutEvents, FString& OutError);
    bool ParseFrame(const TArray<uint8>& Frame, FOpenGameAgentDecodedEvent& OutEvent, bool& bOutHasEvent, FString& OutError) const;

    TArray<uint8> Pending;
    int32 MaximumEventBytes;
};
