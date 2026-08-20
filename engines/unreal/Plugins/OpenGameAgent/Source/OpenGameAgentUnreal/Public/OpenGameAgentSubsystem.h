#pragma once

#include "CoreMinimal.h"
#include "HttpFwd.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "OpenGameAgentSubsystem.generated.h"

struct FOpenGameAgentRunState;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_ThreeParams(
    FOpenGameAgentRunEvent,
    const FString&, InputId,
    const FString&, EventName,
    const FString&, EventJson);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
    FOpenGameAgentRunCompleted,
    const FString&, InputId,
    const FString&, ResultJson);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
    FOpenGameAgentRunFailed,
    const FString&, InputId,
    const FString&, Error);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FiveParams(
    FOpenGameAgentControlCompleted,
    const FString&, Operation,
    const FString&, SessionId,
    const FString&, ActorId,
    bool, Accepted,
    const FString&, Error);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FiveParams(
    FOpenGameAgentActionResponse,
    const FString&, Operation,
    const FString&, SessionId,
    const FString&, ActorId,
    const FString&, ResponseJson,
    const FString&, Error);

/**
 * Blueprint and C++ bridge to a trusted OpenGameAgent HTTP/SSE sidecar or server.
 * Gameplay authority remains in Unreal; this subsystem only transports structured inputs,
 * streamed agent events, steering, and cancellation on the game thread.
 */
UCLASS(BlueprintType)
class OPENGAMEAGENTUNREAL_API UOpenGameAgentSubsystem final : public UGameInstanceSubsystem
{
    GENERATED_BODY()

public:
    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentRunEvent OnRunEvent;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentRunCompleted OnRunCompleted;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentRunFailed OnRunFailed;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentControlCompleted OnControlCompleted;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentActionResponse OnActionResponse;

    /** Configure a remote sidecar. Plain HTTP is accepted only for loopback unless explicitly allowed. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool ConfigureRemote(
        const FString& ServerBaseUrl,
        const FString& ApiKey,
        bool bAllowInsecureRemoteHttp,
        FString& Error);

    /**
     * Start a server-streaming run from canonical GameAgentWire input JSON.
     * Returns false before network dispatch when the request is invalid or capacity is exhausted.
     */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool RunJson(const FString& InputJson, FString& InputId, FString& Error);

    /** Inject a structured JSON observation into the active session/actor run. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool SteerActor(
        const FString& SessionId,
        const FString& ActorId,
        const FString& PayloadJson,
        FString& Error);

    /** Request an abort for the active session/actor run. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool AbortActor(const FString& SessionId, const FString& ActorId, FString& Error);

    /** Cancel only this Unreal HTTP caller. The durable action boundary remains authoritative. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool CancelRun(const FString& InputId);

    /** Read a bounded batch of already-durable external action deliveries for this authorized actor. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Actions")
    bool ClaimActions(
        const FString& SessionId,
        const FString& ActorId,
        int32 Limit,
        FString& Error);

    /**
     * Submit the canonical /v1/actions/receipt request JSON after Unreal has reconciled and settled the operation.
     * The request must bind session, actor, operation, timeline, generation, and authority revisions.
     */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Actions")
    bool SubmitActionReceiptJson(const FString& ReceiptRequestJson, FString& Error);

    /** Read the durable prepared/dispatched/completed state for one operation. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Actions")
    bool ReconcileAction(
        const FString& SessionId,
        const FString& ActorId,
        const FString& OperationId,
        FString& Error);

    UFUNCTION(BlueprintPure, Category = "OpenGameAgent")
    int32 GetActiveRunCount() const;

    virtual void Deinitialize() override;

private:
    static constexpr int32 MaximumActiveRuns = 64;
    static constexpr int32 MaximumActiveRequests = 128;
    static constexpr int32 MaximumRequestCharacters = 8000000;
    static constexpr int32 MaximumResponseBytes = 16000000;
    static constexpr int32 MaximumEventCharacters = 4000000;

    FString BaseUrl;
    FString AuthorizationValue;
    TMap<FString, TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>> ActiveRuns;
    TSet<FHttpRequestPtr> ActiveRequests;
    bool bConfigured = false;
    bool bShuttingDown = false;

    void HandleProgress(const FString& InputId);
    void HandleComplete(const FString& InputId, bool bConnectedSuccessfully);
    bool FeedAvailableResponseBytes(
        const FString& InputId,
        const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>& State,
        FString& Error);
    void FailRun(const FString& InputId, const FString& Error);
    bool SendControl(
        const FString& Operation,
        const FString& Path,
        const FString& SessionId,
        const FString& ActorId,
        const FString* PayloadJson,
        FString& Error);
    bool SendActionRequest(
        const FString& Operation,
        const FString& Path,
        const FString& SessionId,
        const FString& ActorId,
        const FString& RequestJson,
        FString& Error);
};
