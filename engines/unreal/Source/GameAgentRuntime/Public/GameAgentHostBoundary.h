#pragma once

#include "CoreMinimal.h"
#include "GameAgentMainThreadDispatcher.h"
#include "GameAgentWireProtocol.h"
#include "HAL/CriticalSection.h"
#include "Templates/Function.h"
#include "Templates/SharedPointer.h"

using FGameAgentActionCompletion =
    TUniqueFunction<void(game_agent::wire::ActionReceipt&& Receipt)>;

class FGameAgentRouterState;

class GAMEAGENTRUNTIME_API IGameAgentHostBoundary
{
public:
    virtual ~IGameAgentHostBoundary() = default;

    virtual void ExecuteAction(
        const game_agent::wire::ActionRequest& Request,
        FGameAgentActionCompletion&& Completion) = 0;

    // Prevent new host-side action work and join every callback that may still
    // invoke a router-supplied completion before returning.
    virtual void StopAndDrainActions() = 0;
};

enum class EGameAgentActionDispatchStatus : uint8
{
    Accepted,
    InvalidRequest,
    HostUnavailable,
    QueueFull,
    DispatcherStopped
};

struct GAMEAGENTRUNTIME_API FGameAgentActionDispatchResult final
{
    EGameAgentActionDispatchStatus Status = EGameAgentActionDispatchStatus::InvalidRequest;
    FString Error;

    bool WasAccepted() const
    {
        return Status == EGameAgentActionDispatchStatus::Accepted;
    }
};

class GAMEAGENTRUNTIME_API FGameAgentHostRouter final
{
public:
    // Owners must retain the router until a terminal Stop or UnbindHost call
    // returns true outside any router lifecycle callback.
    explicit FGameAgentHostRouter(
        TSharedRef<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe> Dispatcher);
    ~FGameAgentHostRouter();

    // A router accepts one host binding for its lifetime. UnbindHost is
    // terminal and performs the same shutdown fence as Stop.
    bool BindHost(TSharedRef<IGameAgentHostBoundary, ESPMode::ThreadSafe> Host);
    bool UnbindHost();
    bool Stop();

    FGameAgentActionDispatchResult DispatchActionJson(
        const FString& ActionRequestJson,
        FGameAgentActionCompletion&& Completion);

private:
    TSharedRef<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe> Dispatcher_;
    TSharedRef<FGameAgentRouterState, ESPMode::ThreadSafe> RouterState_;
    FCriticalSection LifecycleGate_;
    FCriticalSection HostGate_;
    TSharedPtr<IGameAgentHostBoundary, ESPMode::ThreadSafe> Host_;
};
