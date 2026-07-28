#include "GameAgentMainThreadDispatcher.h"

#include "CoreGlobals.h"
#include "Misc/AssertionMacros.h"
#include "Misc/ScopeLock.h"

FGameAgentMainThreadDispatcher::FGameAgentMainThreadDispatcher(
    const int32 MaxPendingWorkItems)
    : MaxPendingWorkItems_(MaxPendingWorkItems > 0 ? MaxPendingWorkItems : 1)
{
}

FGameAgentMainThreadDispatcher::~FGameAgentMainThreadDispatcher()
{
}

bool FGameAgentMainThreadDispatcher::Enqueue(TUniqueFunction<void()>&& Work)
{
    return Enqueue(MoveTemp(Work), TUniqueFunction<void()>());
}

bool FGameAgentMainThreadDispatcher::Enqueue(
    TUniqueFunction<void()>&& Work,
    TUniqueFunction<void()>&& OnAbandoned)
{
    if (!Work)
    {
        return false;
    }

    FScopeLock Lock(&Gate_);
    if (!bAccepting_)
    {
        return false;
    }
    if (PendingCount_ >= MaxPendingWorkItems_)
    {
        return false;
    }
    FPendingWork PendingWork;
    PendingWork.Execute = MoveTemp(Work);
    PendingWork.Abandon = MoveTemp(OnAbandoned);
    Pending_.Enqueue(MoveTemp(PendingWork));
    ++PendingCount_;
    return true;
}

int32 FGameAgentMainThreadDispatcher::Drain(const int32 MaxWorkItems)
{
    check(IsInGameThread());
    if (MaxWorkItems <= 0)
    {
        return 0;
    }

    int32 Executed = 0;
    while (Executed < MaxWorkItems)
    {
        FPendingWork Work;
        {
            FScopeLock Lock(&Gate_);
            if (!Pending_.Dequeue(Work))
            {
                break;
            }
            --PendingCount_;
        }

        Work.Execute();
        ++Executed;
    }
    return Executed;
}

void FGameAgentMainThreadDispatcher::Stop()
{
    check(IsInGameThread());
    TArray<TUniqueFunction<void()>> Abandoned;
    {
        FScopeLock Lock(&Gate_);
        bAccepting_ = false;
        FPendingWork Work;
        while (Pending_.Dequeue(Work))
        {
            if (Work.Abandon)
            {
                Abandoned.Add(MoveTemp(Work.Abandon));
            }
        }
        PendingCount_ = 0;
    }

    for (TUniqueFunction<void()>& Callback : Abandoned)
    {
        Callback();
    }
}

bool FGameAgentMainThreadDispatcher::IsAccepting() const
{
    FScopeLock Lock(&Gate_);
    return bAccepting_;
}

int32 FGameAgentMainThreadDispatcher::GetPendingCount() const
{
    FScopeLock Lock(&Gate_);
    return PendingCount_;
}
