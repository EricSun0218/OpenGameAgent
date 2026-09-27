#pragma once

#include "CoreMinimal.h"
#include "OpenGameAgentRuntimeClient.h"

#include <atomic>

enum class EOpenGameAgentActionPumpState : uint8
{
	Idle,
	WaitingForDelivery,
	Executing,
	Inspecting,
	DeliveringReceipt,
	Reconciling,
	ReadingConflict,
	WaitingToRetry,
	Halted,
	Stopped,
};

class FOpenGameAgentActionPump;

class OPENGAMEAGENT_API FOpenGameAgentActionExecutionContext final
{
public:
	const FOpenGameAgentAction& GetAction() const;
	uint64 GetEpoch() const;
	bool IsCancellationRequested() const;
	bool IsCompleted() const;
	bool TryCommit(
		TFunctionRef<FOpenGameAgentActionResult(const FOpenGameAgentAction&)> GameThreadTransaction);
	bool TryCompleteWithoutWrite(FOpenGameAgentActionResult Result);

private:
	friend class FOpenGameAgentActionPump;
	friend class FOpenGameAgentActionPumpValidationTest;

	FOpenGameAgentActionExecutionContext(
		FOpenGameAgentAction InAction,
		uint64 InEpoch,
		double InDeadlineSeconds,
		TFunction<bool(uint64)> InCanBeginWorldWrite);
	void Cancel();
	FOpenGameAgentActionResult TakeResult();

	FOpenGameAgentAction Action;
	uint64 Epoch;
	double DeadlineSeconds;
	TFunction<bool(uint64)> CanBeginWorldWrite;
	std::atomic<bool> bCancelled{false};
	bool bCompleted = false;
	FOpenGameAgentActionResult Result;
};

class OPENGAMEAGENT_API FOpenGameAgentActionInspectionContext final
{
public:
	const FOpenGameAgentAction& GetAction() const;
	uint64 GetEpoch() const;
	bool IsCancellationRequested() const;
	bool IsCompleted() const;
	bool TryComplete(FOpenGameAgentActionReconciliation Reconciliation);

private:
	friend class FOpenGameAgentActionPump;
	friend class FOpenGameAgentActionPumpValidationTest;

	FOpenGameAgentActionInspectionContext(
		FOpenGameAgentAction InAction,
		uint64 InEpoch,
		double InDeadlineSeconds,
		TFunction<bool(uint64)> InCanComplete);
	void Cancel();
	FOpenGameAgentActionReconciliation TakeReconciliation();

	FOpenGameAgentAction Action;
	uint64 Epoch;
	double DeadlineSeconds;
	TFunction<bool(uint64)> CanComplete;
	std::atomic<bool> bCancelled{false};
	bool bCompleted = false;
	FOpenGameAgentActionReconciliation Reconciliation;
};

class OPENGAMEAGENT_API IOpenGameAgentAuthoritativeActionAdapter
{
public:
	virtual ~IOpenGameAgentAuthoritativeActionAdapter() = default;
	virtual void Execute(TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> Context) = 0;
	virtual void Inspect(TSharedRef<FOpenGameAgentActionInspectionContext, ESPMode::ThreadSafe> Context) = 0;
	virtual bool RecheckExpectedAuthority(const FOpenGameAgentAction& Action) = 0;
};

struct OPENGAMEAGENT_API FOpenGameAgentActionDeliveryPolicy
{
	TArray<double> RetryDelaysSeconds;
	bool IsValid() const;
};

struct OPENGAMEAGENT_API FOpenGameAgentActionPumpOptions
{
	FOpenGameAgentClientOptions Client;
	FString SessionId;
	FString ActorId;
	double IdlePollIntervalSeconds = 0.0;
	double ExecutionDurationSeconds = 0.0;
	TOptional<FOpenGameAgentActionDeliveryPolicy> DeliveryPolicy;
	bool IsValid() const;
};

class OPENGAMEAGENT_API FOpenGameAgentActionPump final
	: public TSharedFromThis<FOpenGameAgentActionPump, ESPMode::ThreadSafe>
{
public:
	static TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Create(
		FOpenGameAgentActionPumpOptions Options,
		TSharedRef<IOpenGameAgentAuthoritativeActionAdapter, ESPMode::ThreadSafe> Adapter);
	~FOpenGameAgentActionPump();

	void Tick(float DeltaSeconds);
	void Stop();
	bool IsBusy() const;
	bool IsHalted() const;
	EOpenGameAgentActionPumpState GetState() const;
	const TOptional<FOpenGameAgentClientError>& GetLastError() const;

private:
	friend class FOpenGameAgentActionPumpValidationTest;

	enum class ERetryOperation : uint8
	{
		None,
		Receipt,
		Reconciliation,
		ConflictRead,
	};
	struct FConflictInspectionFence
	{
		FString ClaimToken;
		FString Attempt;
	};

	FOpenGameAgentActionPump(
		FOpenGameAgentActionPumpOptions InOptions,
		TSharedRef<IOpenGameAgentAuthoritativeActionAdapter, ESPMode::ThreadSafe> InAdapter);
	void BeginRecoverAction();
	void BeginNextAction();
	void BeginDelivery(FOpenGameAgentActionDelivery Delivery);
	void BeginExecute(FOpenGameAgentAction Action);
	void BeginInspect(FOpenGameAgentAction Action);
	void FinishAdapterOperation();
	void DeliverReceipt();
	void DeliverReconciliation();
	void ReadConflict();
	void HandleConflictRead(TOptional<FOpenGameAgentAction> Action);
	void HandleDeliveryFailure(ERetryOperation Operation, FOpenGameAgentClientError Error);
	void ScheduleRetry(ERetryOperation Operation, FOpenGameAgentClientError Error);
	void FinishResolvedDelivery();
	void Halt(FString Message, FOpenGameAgentClientError Error = {});
	void CancelActiveOperation();
	bool CanUseExecutionFence(uint64 ExpectedEpoch) const;
	bool CanBeginWorldWrite(uint64 ExpectedEpoch) const;
	bool CanCompleteInspection(uint64 ExpectedEpoch) const;
	bool IsCurrentNetworkCallback(uint64 ExpectedEpoch, EOpenGameAgentActionPumpState ExpectedState) const;
	static bool IsAmbiguousFailure(const FOpenGameAgentClientError& Error);

	FOpenGameAgentActionPumpOptions Options;
	FOpenGameAgentRuntimeClient Client;
	TSharedRef<IOpenGameAgentAuthoritativeActionAdapter, ESPMode::ThreadSafe> Adapter;
	FOpenGameAgentRequestPtr ActiveRequest;
	TSharedPtr<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> ExecutionContext;
	TSharedPtr<FOpenGameAgentActionInspectionContext, ESPMode::ThreadSafe> InspectionContext;
	TOptional<FOpenGameAgentAction> ActiveAction;
	TOptional<FOpenGameAgentActionResult> ActiveResult;
	TOptional<FOpenGameAgentActionReconciliation> ActiveReconciliation;
	TOptional<FConflictInspectionFence> LastConflictInspectionFence;
	TOptional<FOpenGameAgentClientError> LastError;
	EOpenGameAgentActionPumpState State = EOpenGameAgentActionPumpState::Idle;
	ERetryOperation RetryOperation = ERetryOperation::None;
	double ElapsedSeconds = 0.0;
	double OperationDeadlineSeconds = 0.0;
	double RetryAtSeconds = 0.0;
	int32 RetryDelayIndex = 0;
	uint64 NetworkEpoch = 0;
	uint64 OperationEpoch = 0;
	bool bRecoveryComplete = false;
};
