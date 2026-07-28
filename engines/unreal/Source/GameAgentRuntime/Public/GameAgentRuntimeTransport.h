#pragma once

#include "Containers/Array.h"
#include "Containers/ArrayView.h"
#include "Containers/UnrealString.h"
#include "CoreTypes.h"
#include "Templates/Function.h"

enum class EGameAgentRuntimeBackend : uint8
{
    Sidecar,
    InProcessNative
};

using FGameAgentRuntimeEventSink =
    TUniqueFunction<void(uint64 CorrelationId, TArray<uint8>&& RuntimeEventJsonUtf8)>;

class GAMEAGENTRUNTIME_API IGameAgentRuntimeTransport
{
public:
    virtual ~IGameAgentRuntimeTransport() = default;

    virtual EGameAgentRuntimeBackend GetBackend() const = 0;
    virtual bool Start(FString& OutError) = 0;
    virtual void Stop() = 0;
    virtual bool IsRunning() const = 0;

    virtual void SetEventSink(FGameAgentRuntimeEventSink&& EventSink) = 0;

    virtual bool SubmitObservation(
        uint64 CorrelationId,
        TArrayView<const uint8> ObservationJsonUtf8,
        FString& OutError) = 0;

    virtual bool SubmitActionReceipt(
        uint64 CorrelationId,
        TArrayView<const uint8> ActionReceiptJsonUtf8,
        FString& OutError) = 0;

    virtual bool SendControl(
        uint64 CorrelationId,
        TArrayView<const uint8> ControlJsonUtf8,
        FString& OutError) = 0;
};
