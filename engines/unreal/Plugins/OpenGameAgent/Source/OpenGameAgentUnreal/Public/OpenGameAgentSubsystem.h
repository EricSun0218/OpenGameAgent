#pragma once

#include "CoreMinimal.h"
#include "Interfaces/IHttpRequest.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "OpenGameAgentSubsystem.generated.h"

USTRUCT(BlueprintType)
struct OPENGAMEAGENTUNREAL_API FOpenGameAgentEvent
{
    GENERATED_BODY()

    UPROPERTY(BlueprintReadOnly, Category = "OpenGameAgent")
    FString EventId;

    UPROPERTY(BlueprintReadOnly, Category = "OpenGameAgent")
    FString EventName;

    UPROPERTY(BlueprintReadOnly, Category = "OpenGameAgent")
    FString Json;
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOpenGameAgentStreamEventSignature, const FString&, RequestId, const FOpenGameAgentEvent&, Event);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_FourParams(FOpenGameAgentJsonResponseSignature, const FString&, RequestId, const FString&, Path, int32, StatusCode, const FString&, Json);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOpenGameAgentFailureSignature, const FString&, RequestId, const FString&, Category);

/**
 * Game-instance scoped native client. The Agent loop remains in the OpenGameAgent service;
 * Unreal owns world validation, action execution, and authoritative receipts.
 */
UCLASS()
class OPENGAMEAGENTUNREAL_API UOpenGameAgentSubsystem : public UGameInstanceSubsystem
{
    GENERATED_BODY()

public:
    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentStreamEventSignature OnStreamEvent;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentJsonResponseSignature OnJsonResponse;

    UPROPERTY(BlueprintAssignable, Category = "OpenGameAgent")
    FOpenGameAgentFailureSignature OnRequestFailed;

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool Configure(const FString& InServerUrl, const FString& InAuthenticationObjectJson);

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    FString StartRun(const FString& InputObjectJson, const FString& RunId = FString());

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    FString StreamActions(const FString& SessionObjectJson, int32 Maximum = 1);

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    FString PostJson(const FString& Path, const FString& BodyObjectJson);

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    FString Steer(const FString& SessionObjectJson, const FString& ExpectedRunCoordinateJson, const FString& InputObjectJson);

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    FString FollowUp(const FString& SessionObjectJson, const FString& ExpectedRunCoordinateJson, const FString& InputObjectJson);

    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    FString Abort(const FString& SessionObjectJson, const FString& ExpectedRunCoordinateJson);

    /** Cancels only the local HTTP request. It does not claim rollback of a dispatched durable action. */
    UFUNCTION(BlueprintCallable, Category = "OpenGameAgent")
    bool CancelLocalRequest(const FString& RequestId);

    virtual void Deinitialize() override;

private:
    FString StartStream(const FString& Path, const FString& BodyObjectJson);
    FString StartJsonRequest(const FString& Path, const FString& BodyObjectJson);
    FString StartControl(const FString& Operation, const FString& SessionObjectJson, const FString& ExpectedRunCoordinateJson, const FString* InputObjectJson);
    bool CreateAuthenticatedBody(const FString& BodyObjectJson, FString& OutBody) const;
    bool ValidateServerUrl(const FString& Value, FString& OutNormalized) const;
    bool ValidatePath(const FString& Path) const;
    void CompleteRequest(const FString& RequestId);

    FString ServerUrl = TEXT("http://127.0.0.1:4317/");
    FString AuthenticationObjectJson;
    TMap<FString, FHttpRequestPtr> ActiveRequests;
    int32 MaximumRequestBytes = 1024 * 1024;
    int32 MaximumResponseBytes = 4 * 1024 * 1024;
    int32 MaximumEventBytes = 1024 * 1024;
};
