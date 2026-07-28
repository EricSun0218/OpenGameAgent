#pragma once

#include "Containers/Array.h"
#include "Containers/Queue.h"
#include "CoreTypes.h"
#include "HAL/CriticalSection.h"
#include "Templates/Function.h"
#include "Templates/SharedPointer.h"

class GAMEAGENTRUNTIME_API FGameAgentMainThreadDispatcher final
    : public TSharedFromThis<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>
{
public:
    explicit FGameAgentMainThreadDispatcher(int32 MaxPendingWorkItems = 1024);
    ~FGameAgentMainThreadDispatcher();

    FGameAgentMainThreadDispatcher(const FGameAgentMainThreadDispatcher&) = delete;
    FGameAgentMainThreadDispatcher& operator=(const FGameAgentMainThreadDispatcher&) = delete;

    bool Enqueue(TUniqueFunction<void()>&& Work);
    bool Enqueue(
        TUniqueFunction<void()>&& Work,
        TUniqueFunction<void()>&& OnAbandoned);
    int32 Drain(int32 MaxWorkItems = 128);
    void Stop();
    bool IsAccepting() const;
    int32 GetPendingCount() const;

private:
    struct FPendingWork final
    {
        TUniqueFunction<void()> Execute;
        TUniqueFunction<void()> Abandon;
    };

    mutable FCriticalSection Gate_;
    TQueue<FPendingWork, EQueueMode::Mpsc> Pending_;
    int32 MaxPendingWorkItems_ = 1024;
    int32 PendingCount_ = 0;
    bool bAccepting_ = true;
};
