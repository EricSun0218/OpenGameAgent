#pragma once

#include "CoreMinimal.h"

struct OPENGAMEAGENT_API FOpenGameAgentClientLimits
{
	int64 MaxRequestBytes = 0;
	int64 MaxResponseBytes = 0;
	int64 MaxAttachmentResponseBytes = 0;
	int64 MaxResponseHeaderBytes = 0;
	int32 MaxResponseHeaders = 0;
	int64 MaxAccessTokenBytes = 0;
	int64 MaxUrlBytes = 0;
	int64 RequestTimeoutMilliseconds = 0;
	bool IsValid() const;
};

struct OPENGAMEAGENT_API FOpenGameAgentClientOptions
{
	FString BaseUrl;
	TFunction<FString()> AccessTokenProvider;
	FOpenGameAgentClientLimits Limits;

	bool IsValid() const;
};

struct OPENGAMEAGENT_API FOpenGameAgentClientError
{
	int32 StatusCode = 0;
	FString Message;
	FString Code;
};

struct OPENGAMEAGENT_API FOpenGameAgentImageReference
{
	FString AttachmentId;
	FString MediaType;
	int64 Bytes = 0;
	int64 Width = 0;
	int64 Height = 0;
	TOptional<FString> Name;

	bool IsValid(int64 MaxAttachmentResponseBytes) const;
};

struct OPENGAMEAGENT_API FOpenGameAgentAction
{
	FString SessionId;
	FString ActorId;
	FString OperationId;
	FString ActionName;
	FString Attempt;
	FString ClaimToken;
	FString ClaimExpiresAt;
	FString Status;
	FString PayloadJson;
	FString ExpectedAuthorityJson;
	FString RecordJson;
};

struct OPENGAMEAGENT_API FOpenGameAgentActionResult
{
	FString Outcome;
	TOptional<FString> ResultJson;
	TOptional<FString> ModelResultJson;
	TOptional<FString> AuthorityAfterJson;
};

struct OPENGAMEAGENT_API FOpenGameAgentActionReconciliation
{
	FString Resolution;
	TOptional<FString> ResultJson;
	TOptional<FString> ModelResultJson;
	TOptional<FString> AuthorityAfterJson;
};

enum class EOpenGameAgentActionDeliveryKind : uint8
{
	Dispatch,
	Reconcile,
};

struct OPENGAMEAGENT_API FOpenGameAgentActionDelivery
{
	EOpenGameAgentActionDeliveryKind Kind = EOpenGameAgentActionDeliveryKind::Dispatch;
	FOpenGameAgentAction Action;
};

struct OPENGAMEAGENT_API FOpenGameAgentCoordinate
{
	FString SessionId;
	FString ActorId;
	FString RunId;
	FString Turn;
};

struct OPENGAMEAGENT_API FOpenGameAgentDeferredOperation
{
	FString OperationId;
	FString InputId;
	FOpenGameAgentCoordinate Coordinate;
};

struct OPENGAMEAGENT_API FOpenGameAgentRunAccepted
{
	FString RunId;
	FOpenGameAgentCoordinate InitialCoordinate;
};

enum class EOpenGameAgentProviderCancellation : uint8
{
	Completed,
	Failed,
	Unavailable,
};

struct OPENGAMEAGENT_API FOpenGameAgentDeferredAbandonResult
{
	FString OperationId;
	EOpenGameAgentProviderCancellation ProviderCancellation = EOpenGameAgentProviderCancellation::Unavailable;
};

enum class EOpenGameAgentNextActionResult : uint8
{
	Delivery,
	Empty,
	Failure,
};

using FOpenGameAgentNextActionCallback =
	TFunction<void(EOpenGameAgentNextActionResult, FOpenGameAgentActionDelivery, FOpenGameAgentClientError)>;
using FOpenGameAgentGetActionCallback =
	TFunction<void(bool, TOptional<FOpenGameAgentAction>, FOpenGameAgentClientError)>;
using FOpenGameAgentReceiptCallback = TFunction<void(bool, FOpenGameAgentClientError)>;
using FOpenGameAgentAttachmentCallback =
	TFunction<void(bool, TArray<uint8>, FOpenGameAgentClientError)>;
using FOpenGameAgentDeferredReadCallback =
	TFunction<void(bool, TOptional<FOpenGameAgentDeferredOperation>, FOpenGameAgentClientError)>;
using FOpenGameAgentDeferredResumeCallback =
	TFunction<void(bool, FOpenGameAgentRunAccepted, FOpenGameAgentClientError)>;
using FOpenGameAgentDeferredAbandonCallback =
	TFunction<void(bool, FOpenGameAgentDeferredAbandonResult, FOpenGameAgentClientError)>;

class OPENGAMEAGENT_API IOpenGameAgentRequest
{
public:
	virtual ~IOpenGameAgentRequest() = default;
	virtual void Cancel() = 0;
};

using FOpenGameAgentRequestPtr = TSharedPtr<IOpenGameAgentRequest, ESPMode::ThreadSafe>;

class FOpenGameAgentTransportState;

class OPENGAMEAGENT_API FOpenGameAgentRuntimeClient final
{
public:
	explicit FOpenGameAgentRuntimeClient(FOpenGameAgentClientOptions InOptions);

	bool IsConfigured() const;

	FOpenGameAgentRequestPtr NextAction(
		const FString& SessionId,
		const FString& ActorId,
		FOpenGameAgentNextActionCallback Callback) const;

	FOpenGameAgentRequestPtr RecoverAction(
		const FString& SessionId,
		const FString& ActorId,
		FOpenGameAgentNextActionCallback Callback) const;

	FOpenGameAgentRequestPtr GetAction(
		const FString& SessionId,
		const FString& ActorId,
		const FString& OperationId,
		FOpenGameAgentGetActionCallback Callback) const;

	FOpenGameAgentRequestPtr RecordActionReceipt(
		const FOpenGameAgentAction& Action,
		const FOpenGameAgentActionResult& Result,
		FOpenGameAgentReceiptCallback Callback) const;

	FOpenGameAgentRequestPtr ReconcileAction(
		const FOpenGameAgentAction& Action,
		const FOpenGameAgentActionReconciliation& Reconciliation,
		FOpenGameAgentReceiptCallback Callback) const;

	FOpenGameAgentRequestPtr ReadAttachment(
		const FString& SessionId,
		const FString& ActorId,
		const FOpenGameAgentImageReference& Reference,
		FOpenGameAgentAttachmentCallback Callback) const;

	FOpenGameAgentRequestPtr ReadDeferred(
		const FString& SessionId,
		const FString& ActorId,
		FOpenGameAgentDeferredReadCallback Callback) const;

	FOpenGameAgentRequestPtr ResumeDeferred(
		const FString& SessionId,
		const FString& ActorId,
		const FString& OperationId,
		FOpenGameAgentDeferredResumeCallback Callback) const;

	FOpenGameAgentRequestPtr AbandonDeferred(
		const FString& SessionId,
		const FString& ActorId,
		const FString& OperationId,
		FOpenGameAgentDeferredAbandonCallback Callback) const;

private:
	using FHttpResultCallback = TFunction<void(bool, int32, FString, FOpenGameAgentClientError)>;

	FOpenGameAgentRequestPtr ReadActionDelivery(
		const FString& SessionId,
		const FString& ActorId,
		bool bRecoveryOnly,
		FOpenGameAgentNextActionCallback Callback) const;
	FOpenGameAgentRequestPtr PostJson(const FString& Path, const FString& BodyJson, FHttpResultCallback Callback) const;

	FOpenGameAgentClientOptions Options;
	TSharedPtr<FOpenGameAgentTransportState, ESPMode::ThreadSafe> TransportState;
};
