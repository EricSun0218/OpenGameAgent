#include "GameAgentHostBoundary.h"

#include "Containers/Array.h"
#include "Containers/Map.h"
#include "Containers/StringConv.h"
#include "HAL/Event.h"
#include "HAL/PlatformProcess.h"
#include "Misc/AssertionMacros.h"
#include "Misc/DateTime.h"
#include "Misc/ScopeLock.h"

namespace
{
thread_local const FGameAgentRouterState* LifecycleCallbackRouterState =
    nullptr;

class FGameAgentOnceCompletion final
{
public:
    explicit FGameAgentOnceCompletion(FGameAgentActionCompletion&& Completion)
        : Completion_(MoveTemp(Completion))
    {
    }

    void Invoke(game_agent::wire::ActionReceipt&& Receipt)
    {
        FGameAgentActionCompletion Completion;
        {
            FScopeLock Lock(&Gate_);
            if (!Completion_)
            {
                return;
            }
            Completion = MoveTemp(Completion_);
        }
        Completion(MoveTemp(Receipt));
    }

private:
    FCriticalSection Gate_;
    FGameAgentActionCompletion Completion_;
};

game_agent::wire::ActionReceipt MakeUnknownReceipt(
    const game_agent::wire::ActionRequest& Request,
    const char* ErrorCode)
{
    game_agent::wire::ActionReceipt Receipt;
    Receipt.ProtocolVersion = Request.ProtocolVersion;
    Receipt.SchemaVersion = Request.SchemaVersion;
    Receipt.OperationId = Request.OperationId;
    Receipt.Status = game_agent::wire::ReceiptStatus::Unknown;
    Receipt.ErrorCode = ErrorCode;
    Receipt.Retryable = false;
    const FString Timestamp = FDateTime::UtcNow().ToIso8601();
    const FTCHARToUTF8 TimestampUtf8(*Timestamp);
    Receipt.ReceivedAt.assign(
        TimestampUtf8.Get(),
        static_cast<std::size_t>(TimestampUtf8.Length()));
    return Receipt;
}

game_agent::wire::ActionReceipt MakeRejectedReceipt(
    const game_agent::wire::ActionRequest& Request,
    const char* ErrorCode)
{
    auto Receipt = MakeUnknownReceipt(Request, ErrorCode);
    Receipt.Status = game_agent::wire::ReceiptStatus::Rejected;
    return Receipt;
}
} // namespace

class FGameAgentPendingAction final
{
public:
    FGameAgentPendingAction(
        TSharedRef<game_agent::wire::ActionRequest, ESPMode::ThreadSafe> Request,
        TSharedRef<FGameAgentOnceCompletion, ESPMode::ThreadSafe> Completion)
        : Request(MoveTemp(Request))
        , Completion(MoveTemp(Completion))
    {
    }

    TSharedRef<game_agent::wire::ActionRequest, ESPMode::ThreadSafe> Request;
    TSharedRef<FGameAgentOnceCompletion, ESPMode::ThreadSafe> Completion;
};

class FGameAgentRouterState final
{
public:
    FGameAgentRouterState()
        : CompletionsDrained_(
              FPlatformProcess::GetSynchEventFromPool(true))
        , HostInvocationsDrained_(
              FPlatformProcess::GetSynchEventFromPool(true))
        , StopCompleted_(
              FPlatformProcess::GetSynchEventFromPool(true))
    {
        check(CompletionsDrained_ != nullptr);
        check(HostInvocationsDrained_ != nullptr);
        check(StopCompleted_ != nullptr);
        CompletionsDrained_->Trigger();
        HostInvocationsDrained_->Trigger();
        StopCompleted_->Reset();
    }

    ~FGameAgentRouterState()
    {
        FPlatformProcess::ReturnSynchEventToPool(CompletionsDrained_);
        CompletionsDrained_ = nullptr;
        FPlatformProcess::ReturnSynchEventToPool(HostInvocationsDrained_);
        HostInvocationsDrained_ = nullptr;
        FPlatformProcess::ReturnSynchEventToPool(StopCompleted_);
        StopCompleted_ = nullptr;
    }

    bool TryRegister(
        const TSharedRef<FGameAgentPendingAction, ESPMode::ThreadSafe>& Pending,
        uint64& PendingId)
    {
        FScopeLock Lock(&Gate_);
        if (!bAccepting_)
        {
            return false;
        }

        PendingId = NextPendingId_++;
        Pending_.Add(PendingId, Pending);
        return true;
    }

    void Complete(
        const uint64 PendingId,
        game_agent::wire::ActionReceipt&& Receipt)
    {
        TSharedPtr<FGameAgentPendingAction, ESPMode::ThreadSafe> Pending;
        {
            FScopeLock Lock(&Gate_);
            if (!bAccepting_
                || !Pending_.RemoveAndCopyValue(PendingId, Pending))
            {
                return;
            }
            if (InFlightCompletions_++ == 0)
            {
                CompletionsDrained_->Reset();
            }
        }

        const FGameAgentRouterState* PreviousRouterState =
            LifecycleCallbackRouterState;
        LifecycleCallbackRouterState = this;
        Pending->Completion->Invoke(MoveTemp(Receipt));
        LifecycleCallbackRouterState = PreviousRouterState;

        {
            FScopeLock Lock(&Gate_);
            check(InFlightCompletions_ > 0);
            if (--InFlightCompletions_ == 0)
            {
                CompletionsDrained_->Trigger();
            }
        }
    }

    void Discard(const uint64 PendingId)
    {
        FScopeLock Lock(&Gate_);
        Pending_.Remove(PendingId);
    }

    bool TryBeginHostInvocation(const uint64 PendingId)
    {
        FScopeLock Lock(&Gate_);
        if (!bAccepting_ || !Pending_.Contains(PendingId))
        {
            return false;
        }
        if (InFlightHostInvocations_++ == 0)
        {
            HostInvocationsDrained_->Reset();
        }
        return true;
    }

    void EndHostInvocation()
    {
        FScopeLock Lock(&Gate_);
        check(InFlightHostInvocations_ > 0);
        if (--InFlightHostInvocations_ == 0)
        {
            HostInvocationsDrained_->Trigger();
        }
    }

    void InvokeHostAction(
        const TSharedPtr<IGameAgentHostBoundary, ESPMode::ThreadSafe>& Host,
        const game_agent::wire::ActionRequest& Request,
        FGameAgentActionCompletion&& Completion)
    {
        const FGameAgentRouterState* PreviousRouterState =
            LifecycleCallbackRouterState;
        LifecycleCallbackRouterState = this;
        Host->ExecuteAction(Request, MoveTemp(Completion));
        LifecycleCallbackRouterState = PreviousRouterState;
    }

    bool TryBeginStop(
        TArray<TSharedPtr<FGameAgentPendingAction, ESPMode::ThreadSafe>>&
            Abandoned)
    {
        FScopeLock Lock(&Gate_);
        if (!bAccepting_)
        {
            return false;
        }

        bAccepting_ = false;
        Abandoned.Reserve(Pending_.Num());
        for (const auto& Entry : Pending_)
        {
            Abandoned.Add(Entry.Value);
        }
        Pending_.Empty();
        return true;
    }

    void InvokeStoppedCompletion(
        const TSharedPtr<FGameAgentPendingAction, ESPMode::ThreadSafe>& Pending)
    {
        const FGameAgentRouterState* PreviousRouterState =
            LifecycleCallbackRouterState;
        LifecycleCallbackRouterState = this;
        Pending->Completion->Invoke(
            MakeUnknownReceipt(
                *Pending->Request,
                "router_stopped"));
        LifecycleCallbackRouterState = PreviousRouterState;
    }

    void InvokeHostDrain(
        const TSharedPtr<IGameAgentHostBoundary, ESPMode::ThreadSafe>& Host)
    {
        const FGameAgentRouterState* PreviousRouterState =
            LifecycleCallbackRouterState;
        LifecycleCallbackRouterState = this;
        Host->StopAndDrainActions();
        LifecycleCallbackRouterState = PreviousRouterState;
    }

    bool IsInLifecycleCallbackOnCurrentThread() const
    {
        return LifecycleCallbackRouterState == this;
    }

    bool IsAccepting()
    {
        FScopeLock Lock(&Gate_);
        return bAccepting_;
    }

    void WaitForCompletions()
    {
        CompletionsDrained_->Wait();
    }

    void WaitForHostInvocations()
    {
        HostInvocationsDrained_->Wait();
    }

    void MarkStopCompleted()
    {
        StopCompleted_->Trigger();
    }

    void WaitForStop()
    {
        StopCompleted_->Wait();
    }

private:
    FCriticalSection Gate_;
    TMap<
        uint64,
        TSharedPtr<FGameAgentPendingAction, ESPMode::ThreadSafe>>
        Pending_;
    FEvent* CompletionsDrained_ = nullptr;
    FEvent* HostInvocationsDrained_ = nullptr;
    FEvent* StopCompleted_ = nullptr;
    uint64 NextPendingId_ = 1;
    int32 InFlightCompletions_ = 0;
    int32 InFlightHostInvocations_ = 0;
    bool bAccepting_ = true;
};

FGameAgentHostRouter::FGameAgentHostRouter(
    TSharedRef<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe> Dispatcher)
    : Dispatcher_(MoveTemp(Dispatcher))
    , RouterState_(
          MakeShared<FGameAgentRouterState, ESPMode::ThreadSafe>())
{
}

FGameAgentHostRouter::~FGameAgentHostRouter()
{
    if (RouterState_->IsInLifecycleCallbackOnCurrentThread())
    {
        checkf(
            false,
            TEXT(
                "The router owner must retain it until Stop or UnbindHost "
                "returns outside a lifecycle callback."));
        return;
    }

    if (!Stop())
    {
        checkf(
            false,
            TEXT("The game host router could not stop during destruction."));
    }
}

bool FGameAgentHostRouter::BindHost(
    TSharedRef<IGameAgentHostBoundary, ESPMode::ThreadSafe> Host)
{
    FScopeLock LifecycleLock(&LifecycleGate_);
    if (!RouterState_->IsAccepting())
    {
        return false;
    }

    FScopeLock HostLock(&HostGate_);
    if (Host_.IsValid())
    {
        return false;
    }
    Host_ = MoveTemp(Host);
    return true;
}

bool FGameAgentHostRouter::UnbindHost()
{
    if (RouterState_->IsInLifecycleCallbackOnCurrentThread())
    {
        return false;
    }

    if (!Stop())
    {
        return false;
    }
    FScopeLock Lock(&HostGate_);
    Host_.Reset();
    return true;
}

bool FGameAgentHostRouter::Stop()
{
    if (RouterState_->IsInLifecycleCallbackOnCurrentThread())
    {
        return false;
    }

    TArray<TSharedPtr<FGameAgentPendingAction, ESPMode::ThreadSafe>>
        Abandoned;
    bool bOwnsStop = false;
    {
        FScopeLock Lock(&LifecycleGate_);
        bOwnsStop = RouterState_->TryBeginStop(Abandoned);
    }
    if (!bOwnsStop)
    {
        RouterState_->WaitForStop();
        return true;
    }

    for (const auto& Pending : Abandoned)
    {
        RouterState_->InvokeStoppedCompletion(Pending);
    }

    TSharedPtr<IGameAgentHostBoundary, ESPMode::ThreadSafe> Host;
    {
        FScopeLock Lock(&HostGate_);
        Host = Host_;
    }
    RouterState_->WaitForHostInvocations();
    if (Host.IsValid())
    {
        RouterState_->InvokeHostDrain(Host);
    }
    RouterState_->WaitForCompletions();
    RouterState_->MarkStopCompleted();
    return true;
}

FGameAgentActionDispatchResult FGameAgentHostRouter::DispatchActionJson(
    const FString& ActionRequestJson,
    FGameAgentActionCompletion&& Completion)
{
    FGameAgentActionDispatchResult Result;
    if (!Completion)
    {
        Result.Error = TEXT("A completion callback is required.");
        return Result;
    }

    const FTCHARToUTF8 Utf8(*ActionRequestJson);
    const std::string_view Json(
        Utf8.Get(),
        static_cast<std::size_t>(Utf8.Length()));
    auto Parsed = game_agent::wire::ParseActionRequest(Json);
    if (!Parsed)
    {
        Result.Error = FString::Printf(
            TEXT("Action request rejected at byte %llu: %s"),
            static_cast<unsigned long long>(Parsed.Error.Offset),
            UTF8_TO_TCHAR(Parsed.Error.Message.c_str()));
        return Result;
    }

    TSharedPtr<IGameAgentHostBoundary, ESPMode::ThreadSafe> Host;
    {
        FScopeLock Lock(&HostGate_);
        Host = Host_;
    }
    if (!Host.IsValid())
    {
        Result.Status = EGameAgentActionDispatchStatus::HostUnavailable;
        Result.Error = TEXT("No game host is bound.");
        return Result;
    }

    auto OnceCompletion =
        MakeShared<FGameAgentOnceCompletion, ESPMode::ThreadSafe>(MoveTemp(Completion));
    auto Request =
        MakeShared<game_agent::wire::ActionRequest, ESPMode::ThreadSafe>(
            MoveTemp(Parsed.Value));
    auto Pending =
        MakeShared<FGameAgentPendingAction, ESPMode::ThreadSafe>(
            Request,
            OnceCompletion);
    uint64 PendingId = 0;
    FScopeLock LifecycleLock(&LifecycleGate_);
    if (!RouterState_->TryRegister(Pending, PendingId))
    {
        Result.Status = EGameAgentActionDispatchStatus::DispatcherStopped;
        Result.Error = TEXT("The game host router is stopped.");
        return Result;
    }

    const auto RouterState = RouterState_;
    const bool bQueued = Dispatcher_->Enqueue(
        [Host = MoveTemp(Host),
         Request,
         RouterState,
         PendingId]() mutable
        {
            check(IsInGameThread());
            if (!RouterState->TryBeginHostInvocation(PendingId))
            {
                return;
            }
            if (Request->Deadline.has_value())
            {
                FDateTime Deadline;
                const FString WireDeadline(
                    UTF8_TO_TCHAR(Request->Deadline->c_str()));
                if (!FDateTime::ParseIso8601(*WireDeadline, Deadline)
                    || FDateTime::UtcNow() >= Deadline)
                {
                    RouterState->Complete(
                        PendingId,
                        MakeRejectedReceipt(
                            *Request,
                            "action_deadline_expired"));
                    RouterState->EndHostInvocation();
                    return;
                }
            }

            RouterState->InvokeHostAction(
                Host,
                *Request,
                [Request, RouterState, PendingId](
                    game_agent::wire::ActionReceipt&& Receipt)
                {
                    if (Receipt.OperationId != Request->OperationId)
                    {
                        RouterState->Complete(
                            PendingId,
                            MakeUnknownReceipt(
                                *Request,
                                "receipt_operation_id_mismatch"));
                        return;
                    }

                    const auto Validated =
                        game_agent::wire::ParseActionReceipt(
                            game_agent::wire::SerializeActionReceipt(
                                Receipt));
                    if (!Validated)
                    {
                        RouterState->Complete(
                            PendingId,
                            MakeUnknownReceipt(
                                *Request,
                                "receipt_invalid"));
                        return;
                    }

                    RouterState->Complete(
                        PendingId,
                        game_agent::wire::ActionReceipt(
                            Validated.Value));
                });
            RouterState->EndHostInvocation();
        },
        [Request, RouterState, PendingId]() mutable
        {
            RouterState->Complete(
                PendingId,
                MakeUnknownReceipt(*Request, "dispatcher_stopped"));
        });
    if (!bQueued)
    {
        RouterState_->Discard(PendingId);
        if (Dispatcher_->IsAccepting())
        {
            Result.Status = EGameAgentActionDispatchStatus::QueueFull;
            Result.Error = TEXT("The game-thread dispatcher queue is full.");
        }
        else
        {
            Result.Status = EGameAgentActionDispatchStatus::DispatcherStopped;
            Result.Error = TEXT("The game-thread dispatcher is stopped.");
        }
        return Result;
    }

    Result.Status = EGameAgentActionDispatchStatus::Accepted;
    return Result;
}
