#pragma once

#include "CoreMinimal.h"
#include "HttpFwd.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "OpenGameAgentSubsystem.generated.h"

struct FOpenGameAgentRunState;
struct FOpenGameAgentActionStreamState;

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

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FiveParams(
    FOpenGameAgentQueryResponse,
    const FString&, Operation,
    const FString&, SessionId,
    const FString&, ActorId,
    const FString&, ResponseJson,
    const FString&, Error);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FiveParams(
    FOpenGameAgentActionStreamEvent,
    const FString&, StreamId,
    const FString&, SessionId,
    const FString&, ActorId,
    const FString&, EventName,
    const FString&, EventJson);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_FourParams(
    FOpenGameAgentActionStreamClosed,
    const FString&, StreamId,
    const FString&, SessionId,
    const FString&, ActorId,
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

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentQueryResponse OnQueryResponse;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent|Actions")
    FOpenGameAgentActionStreamEvent OnActionStreamEvent;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent|Actions")
    FOpenGameAgentActionStreamClosed OnActionStreamClosed;

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

    /** Read the server's bounded public capability document. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Queries")
    bool ReadServerCapabilities(FString& Error);

    /** Read the authorized persisted usage ledger for a session actor. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Queries")
    bool ReadUsage(const FString& SessionId, const FString& ActorId, FString& Error);

    /** Read one authorized, revision-bound page of the active persisted transcript. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Queries")
    bool ReadTranscript(
        const FString& SessionId,
        const FString& ActorId,
        int32 PageSize,
        const FString& Cursor,
        FString& Error);

    /** Read one authorized image attachment. The response JSON contains bounded base64 data. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Queries")
    bool ReadImageAttachment(
        const FString& SessionId,
        const FString& ActorId,
        const FString& AttachmentId,
        FString& Error);

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

    /** Start an authorized long-lived stream of already-durable external action deliveries. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Actions")
    bool StartActionStream(
        const FString& SessionId,
        const FString& ActorId,
        int32 Limit,
        FString& StreamId,
        FString& Error);

    /** Stop only the selected action-delivery stream. It does not change any action outcome. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent|Actions")
    bool StopActionStream(const FString& StreamId);

    UFUNCTION(BlueprintPure, Category = "OpenGameAgent")
    int32 GetActiveRunCount() const;

    UFUNCTION(BlueprintPure, Category = "OpenGameAgent|Actions")
    int32 GetActiveActionStreamCount() const;

    virtual void Deinitialize() override;

private:
    static constexpr int32 MaximumActiveRuns = 64;
    static constexpr int32 MaximumActiveActionStreams = 64;
    static constexpr int32 MaximumActiveRequests = 128;
    static constexpr int32 MaximumRequestCharacters = 8000000;
    static constexpr int32 MaximumResponseBytes = 16000000;
    static constexpr int32 MaximumEventCharacters = 4000000;

    FString BaseUrl;
    FString AuthorizationValue;
    TMap<FString, TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>> ActiveRuns;
    TMap<FString, TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>> ActiveActionStreams;
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
    void HandleActionStreamProgress(const FString& StreamId);
    void HandleActionStreamComplete(const FString& StreamId, bool bConnectedSuccessfully);
    bool FeedAvailableActionStreamBytes(
        const FString& StreamId,
        const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>& State,
        FString& Error);
    void FailActionStream(const FString& StreamId, const FString& Error);
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
    bool SendQueryRequest(
        const FString& Operation,
        const FString& Path,
        const FString& SessionId,
        const FString& ActorId,
        const FString& RequestJson,
        FString& Error);
    bool SendJsonResponseRequest(
        const FString& Operation,
        const FString& Path,
        const FString& SessionId,
        const FString& ActorId,
        const FString& RequestJson,
        bool bQueryResponse,
        FString& Error);
};
