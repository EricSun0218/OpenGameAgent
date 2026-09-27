#include "OpenGameAgentActionPump.h"

#include "HAL/PlatformTime.h"
#include "Misc/DateTime.h"

#if WITH_DEV_AUTOMATION_TESTS
#include "OpenGameAgentModule.h"

#include "Async/TaskGraphInterfaces.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "Misc/AutomationTest.h"
#endif

namespace
{
bool IsActionOutcome(const FString& Outcome)
{
	return Outcome == TEXT("succeeded") || Outcome == TEXT("rejected") || Outcome == TEXT("unknown");
}

bool IsReconciliationResolution(const FString& Resolution)
{
	return Resolution == TEXT("applied") || Resolution == TEXT("not-applied") || Resolution == TEXT("unknown");
}
}

FOpenGameAgentActionExecutionContext::FOpenGameAgentActionExecutionContext(
	FOpenGameAgentAction InAction,
	const uint64 InEpoch,
	const double InDeadlineSeconds,
	TFunction<bool(uint64)> InCanBeginWorldWrite)
	: Action(MoveTemp(InAction)), Epoch(InEpoch), DeadlineSeconds(InDeadlineSeconds),
	  CanBeginWorldWrite(MoveTemp(InCanBeginWorldWrite))
{
}

const FOpenGameAgentAction& FOpenGameAgentActionExecutionContext::GetAction() const
{
	return Action;
}

uint64 FOpenGameAgentActionExecutionContext::GetEpoch() const
{
	return Epoch;
}

bool FOpenGameAgentActionExecutionContext::IsCancellationRequested() const
{
	return bCancelled.load(std::memory_order_acquire) || FPlatformTime::Seconds() >= DeadlineSeconds;
}

bool FOpenGameAgentActionExecutionContext::IsCompleted() const
{
	check(IsInGameThread());
	return bCompleted;
}

bool FOpenGameAgentActionExecutionContext::TryCommit(
	TFunctionRef<FOpenGameAgentActionResult(const FOpenGameAgentAction&)> GameThreadTransaction)
{
	if (!IsInGameThread() || bCompleted || IsCancellationRequested())
	{
		return false;
	}
	bCompleted = true;
	if (!CanBeginWorldWrite(Epoch))
	{
		bCompleted = false;
		return false;
	}
	FOpenGameAgentActionResult Candidate = GameThreadTransaction(Action);
	if (!IsActionOutcome(Candidate.Outcome))
	{
		Candidate.Outcome = TEXT("unknown");
	}
	Result = MoveTemp(Candidate);
	return true;
}

bool FOpenGameAgentActionExecutionContext::TryCompleteWithoutWrite(FOpenGameAgentActionResult Candidate)
{
	if (!IsInGameThread() || bCompleted || IsCancellationRequested() ||
		Candidate.Outcome == TEXT("succeeded") ||
		!IsActionOutcome(Candidate.Outcome))
	{
		return false;
	}
	Result = MoveTemp(Candidate);
	bCompleted = true;
	return true;
}

void FOpenGameAgentActionExecutionContext::Cancel()
{
	bCancelled.store(true, std::memory_order_release);
}

FOpenGameAgentActionResult FOpenGameAgentActionExecutionContext::TakeResult()
{
	check(IsInGameThread() && bCompleted);
	return MoveTemp(Result);
}

FOpenGameAgentActionInspectionContext::FOpenGameAgentActionInspectionContext(
	FOpenGameAgentAction InAction,
	const uint64 InEpoch,
	const double InDeadlineSeconds,
	TFunction<bool(uint64)> InCanComplete)
	: Action(MoveTemp(InAction)), Epoch(InEpoch), DeadlineSeconds(InDeadlineSeconds),
	  CanComplete(MoveTemp(InCanComplete))
{
}

const FOpenGameAgentAction& FOpenGameAgentActionInspectionContext::GetAction() const
{
	return Action;
}

uint64 FOpenGameAgentActionInspectionContext::GetEpoch() const
{
	return Epoch;
}

bool FOpenGameAgentActionInspectionContext::IsCancellationRequested() const
{
	return bCancelled.load(std::memory_order_acquire) || FPlatformTime::Seconds() >= DeadlineSeconds;
}

bool FOpenGameAgentActionInspectionContext::IsCompleted() const
{
	check(IsInGameThread());
	return bCompleted;
}

bool FOpenGameAgentActionInspectionContext::TryComplete(FOpenGameAgentActionReconciliation Candidate)
{
	if (!IsInGameThread() || bCompleted || IsCancellationRequested() ||
		!CanComplete(Epoch) || !IsReconciliationResolution(Candidate.Resolution))
	{
		return false;
	}
	Reconciliation = MoveTemp(Candidate);
	bCompleted = true;
	return true;
}

void FOpenGameAgentActionInspectionContext::Cancel()
{
	bCancelled.store(true, std::memory_order_release);
}

FOpenGameAgentActionReconciliation FOpenGameAgentActionInspectionContext::TakeReconciliation()
{
	check(IsInGameThread() && bCompleted);
	return MoveTemp(Reconciliation);
}

bool FOpenGameAgentActionDeliveryPolicy::IsValid() const
{
	for (const double Delay : RetryDelaysSeconds)
	{
		if (!FMath::IsFinite(Delay) || Delay < 0.0)
		{
			return false;
		}
	}
	return true;
}

bool FOpenGameAgentActionPumpOptions::IsValid() const
{
	return Client.IsValid() && !SessionId.IsEmpty() && !ActorId.IsEmpty() &&
		FMath::IsFinite(IdlePollIntervalSeconds) && IdlePollIntervalSeconds > 0.0 &&
		FMath::IsFinite(ExecutionDurationSeconds) && ExecutionDurationSeconds > 0.0 &&
		DeliveryPolicy.IsSet() && DeliveryPolicy->IsValid();
}

TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> FOpenGameAgentActionPump::Create(
	FOpenGameAgentActionPumpOptions Options,
	TSharedRef<IOpenGameAgentAuthoritativeActionAdapter, ESPMode::ThreadSafe> Adapter)
{
	checkf(Options.IsValid(), TEXT("OpenGameAgent requires explicit finite execution, polling and delivery policies."));
	return MakeShareable(new FOpenGameAgentActionPump(MoveTemp(Options), MoveTemp(Adapter)));
}

FOpenGameAgentActionPump::FOpenGameAgentActionPump(
	FOpenGameAgentActionPumpOptions InOptions,
	TSharedRef<IOpenGameAgentAuthoritativeActionAdapter, ESPMode::ThreadSafe> InAdapter)
	: Options(MoveTemp(InOptions)), Client(Options.Client), Adapter(MoveTemp(InAdapter)),
	  ElapsedSeconds(Options.IdlePollIntervalSeconds)
{
}

FOpenGameAgentActionPump::~FOpenGameAgentActionPump()
{
	Stop();
}

void FOpenGameAgentActionPump::Tick(const float DeltaSeconds)
{
	check(IsInGameThread());
	if (State == EOpenGameAgentActionPumpState::Stopped || State == EOpenGameAgentActionPumpState::Halted)
	{
		return;
	}

	const double Now = FPlatformTime::Seconds();
	if (State == EOpenGameAgentActionPumpState::Executing)
	{
		if (ExecutionContext.IsValid() && ExecutionContext->IsCompleted())
		{
			FinishAdapterOperation();
		}
		else if (Now >= OperationDeadlineSeconds)
		{
			if (!ActiveAction.IsSet())
			{
				Halt(TEXT("The elapsed action execution has no authoritative identity."));
			}
			else
			{
				FOpenGameAgentAction Action = ActiveAction.GetValue();
				CancelActiveOperation();
				BeginInspect(MoveTemp(Action));
			}
		}
		return;
	}
	if (State == EOpenGameAgentActionPumpState::Inspecting)
	{
		if (InspectionContext.IsValid() && InspectionContext->IsCompleted())
		{
			FinishAdapterOperation();
		}
		else if (Now >= OperationDeadlineSeconds)
		{
			Halt(TEXT("The authoritative game adapter did not inspect the action before its deadline."));
		}
		return;
	}

	if (State == EOpenGameAgentActionPumpState::WaitingToRetry)
	{
		if (Now < RetryAtSeconds)
		{
			return;
		}
		const ERetryOperation Operation = RetryOperation;
		RetryOperation = ERetryOperation::None;
		switch (Operation)
		{
		case ERetryOperation::Receipt:
			DeliverReceipt();
			break;
		case ERetryOperation::Reconciliation:
			DeliverReconciliation();
			break;
		case ERetryOperation::ConflictRead:
			ReadConflict();
			break;
		default:
			Halt(TEXT("The action pump retry state is invalid."));
			break;
		}
		return;
	}

	if (State != EOpenGameAgentActionPumpState::Idle)
	{
		return;
	}
	ElapsedSeconds += FMath::Max(0.0, static_cast<double>(DeltaSeconds));
	if (ElapsedSeconds >= Options.IdlePollIntervalSeconds)
	{
		ElapsedSeconds = 0.0;
		if (bRecoveryComplete)
		{
			BeginNextAction();
		}
		else
		{
			BeginRecoverAction();
		}
	}
}

void FOpenGameAgentActionPump::Stop()
{
	check(IsInGameThread());
	if (State == EOpenGameAgentActionPumpState::Stopped)
	{
		return;
	}
	++NetworkEpoch;
	++OperationEpoch;
	if (ActiveRequest.IsValid())
	{
		ActiveRequest->Cancel();
		ActiveRequest.Reset();
	}
	CancelActiveOperation();
	State = EOpenGameAgentActionPumpState::Stopped;
}

bool FOpenGameAgentActionPump::IsBusy() const
{
	check(IsInGameThread());
	return State != EOpenGameAgentActionPumpState::Idle &&
		State != EOpenGameAgentActionPumpState::Halted && State != EOpenGameAgentActionPumpState::Stopped;
}

bool FOpenGameAgentActionPump::IsHalted() const
{
	check(IsInGameThread());
	return State == EOpenGameAgentActionPumpState::Halted;
}

EOpenGameAgentActionPumpState FOpenGameAgentActionPump::GetState() const
{
	check(IsInGameThread());
	return State;
}

const TOptional<FOpenGameAgentClientError>& FOpenGameAgentActionPump::GetLastError() const
{
	check(IsInGameThread());
	return LastError;
}

void FOpenGameAgentActionPump::BeginRecoverAction()
{
	State = EOpenGameAgentActionPumpState::WaitingForDelivery;
	const uint64 Epoch = ++NetworkEpoch;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	ActiveRequest = Client.RecoverAction(
		Options.SessionId,
		Options.ActorId,
		[WeakSelf, Epoch](
			const EOpenGameAgentNextActionResult Result,
			FOpenGameAgentActionDelivery Delivery,
			FOpenGameAgentClientError Error) mutable
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			if (!Self.IsValid() || !Self->IsCurrentNetworkCallback(
					Epoch,
					EOpenGameAgentActionPumpState::WaitingForDelivery))
			{
				return;
			}
			Self->ActiveRequest.Reset();
			if (Result == EOpenGameAgentNextActionResult::Failure)
			{
				if (IsAmbiguousFailure(Error))
				{
					Self->LastError = MoveTemp(Error);
					Self->State = EOpenGameAgentActionPumpState::Idle;
					Self->ElapsedSeconds = 0.0;
				}
				else
				{
					Self->Halt(TEXT("The Runtime rejected the recovery-only action request."), MoveTemp(Error));
				}
				return;
			}
			Self->LastError.Reset();
			if (Result == EOpenGameAgentNextActionResult::Empty)
			{
				Self->bRecoveryComplete = true;
				Self->State = EOpenGameAgentActionPumpState::Idle;
				Self->ElapsedSeconds = Self->Options.IdlePollIntervalSeconds;
				return;
			}
			Self->BeginDelivery(MoveTemp(Delivery));
		});
}

void FOpenGameAgentActionPump::BeginNextAction()
{
	State = EOpenGameAgentActionPumpState::WaitingForDelivery;
	const uint64 Epoch = ++NetworkEpoch;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	ActiveRequest = Client.NextAction(
		Options.SessionId,
		Options.ActorId,
		[WeakSelf, Epoch](
			const EOpenGameAgentNextActionResult Result,
			FOpenGameAgentActionDelivery Delivery,
			FOpenGameAgentClientError Error) mutable
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			if (!Self.IsValid() || !Self->IsCurrentNetworkCallback(
					Epoch,
					EOpenGameAgentActionPumpState::WaitingForDelivery))
			{
				return;
			}
			Self->ActiveRequest.Reset();
			if (Result == EOpenGameAgentNextActionResult::Failure)
			{
				if (IsAmbiguousFailure(Error))
				{
					Self->LastError = MoveTemp(Error);
					Self->State = EOpenGameAgentActionPumpState::Idle;
					Self->ElapsedSeconds = 0.0;
				}
				else
				{
					Self->Halt(TEXT("The Runtime rejected the next-action request."), MoveTemp(Error));
				}
				return;
			}
			Self->LastError.Reset();
			if (Result == EOpenGameAgentNextActionResult::Empty)
			{
				Self->State = EOpenGameAgentActionPumpState::Idle;
				return;
			}
			Self->BeginDelivery(MoveTemp(Delivery));
		});
}

void FOpenGameAgentActionPump::BeginDelivery(FOpenGameAgentActionDelivery Delivery)
{
	check(IsInGameThread());
	if (Delivery.Action.SessionId != Options.SessionId || Delivery.Action.ActorId != Options.ActorId)
	{
		Halt(TEXT("The Runtime action delivery does not belong to this action pump."));
		return;
	}
	RetryDelayIndex = 0;
	LastConflictInspectionFence.Reset();
	ActiveAction = Delivery.Action;
	if (Delivery.Kind == EOpenGameAgentActionDeliveryKind::Dispatch)
	{
		BeginExecute(MoveTemp(Delivery.Action));
	}
	else
	{
		BeginInspect(MoveTemp(Delivery.Action));
	}
}

void FOpenGameAgentActionPump::BeginExecute(FOpenGameAgentAction Action)
{
	const double StartedAtSeconds = FPlatformTime::Seconds();
	FDateTime ClaimExpiresAt;
	if (!FDateTime::ParseIso8601(*Action.ClaimExpiresAt, ClaimExpiresAt))
	{
		Halt(TEXT("The Runtime action claim expiry is invalid."));
		return;
	}
	const double RemainingClaimSeconds = (ClaimExpiresAt - FDateTime::UtcNow()).GetTotalSeconds();
	if (RemainingClaimSeconds <= 0.0)
	{
		BeginInspect(MoveTemp(Action));
		return;
	}
	State = EOpenGameAgentActionPumpState::Executing;
	OperationDeadlineSeconds = StartedAtSeconds + FMath::Min(Options.ExecutionDurationSeconds, RemainingClaimSeconds);
	const uint64 Epoch = ++OperationEpoch;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	ExecutionContext = MakeShareable(new FOpenGameAgentActionExecutionContext(
		MoveTemp(Action),
		Epoch,
		OperationDeadlineSeconds,
		[WeakSelf](const uint64 ExpectedEpoch)
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			return Self.IsValid() && Self->CanBeginWorldWrite(ExpectedEpoch);
		}));
	Adapter->Execute(ExecutionContext.ToSharedRef());
	if (ExecutionContext.IsValid() && ExecutionContext->IsCompleted())
	{
		FinishAdapterOperation();
	}
}

void FOpenGameAgentActionPump::BeginInspect(FOpenGameAgentAction Action)
{
	State = EOpenGameAgentActionPumpState::Inspecting;
	OperationDeadlineSeconds = FPlatformTime::Seconds() + Options.ExecutionDurationSeconds;
	const uint64 Epoch = ++OperationEpoch;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	InspectionContext = MakeShareable(new FOpenGameAgentActionInspectionContext(
		MoveTemp(Action),
		Epoch,
		OperationDeadlineSeconds,
		[WeakSelf](const uint64 ExpectedEpoch)
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			return Self.IsValid() && Self->CanCompleteInspection(ExpectedEpoch);
		}));
	Adapter->Inspect(InspectionContext.ToSharedRef());
	if (InspectionContext.IsValid() && InspectionContext->IsCompleted())
	{
		FinishAdapterOperation();
	}
}

void FOpenGameAgentActionPump::FinishAdapterOperation()
{
	check(IsInGameThread());
	if (State == EOpenGameAgentActionPumpState::Executing && ExecutionContext.IsValid())
	{
		ActiveResult = ExecutionContext->TakeResult();
		ExecutionContext->Cancel();
		ExecutionContext.Reset();
		DeliverReceipt();
		return;
	}
	if (State == EOpenGameAgentActionPumpState::Inspecting && InspectionContext.IsValid())
	{
		ActiveReconciliation = InspectionContext->TakeReconciliation();
		InspectionContext->Cancel();
		InspectionContext.Reset();
		DeliverReconciliation();
		return;
	}
	Halt(TEXT("The authoritative adapter completed outside its active action epoch."));
}

void FOpenGameAgentActionPump::DeliverReceipt()
{
	if (!ActiveAction.IsSet() || !ActiveResult.IsSet())
	{
		Halt(TEXT("The action receipt state is incomplete."));
		return;
	}
	State = EOpenGameAgentActionPumpState::DeliveringReceipt;
	const uint64 Epoch = ++NetworkEpoch;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	ActiveRequest = Client.RecordActionReceipt(
		ActiveAction.GetValue(),
		ActiveResult.GetValue(),
		[WeakSelf, Epoch](const bool bSucceeded, FOpenGameAgentClientError Error) mutable
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			if (!Self.IsValid() || !Self->IsCurrentNetworkCallback(
					Epoch,
					EOpenGameAgentActionPumpState::DeliveringReceipt))
			{
				return;
			}
			Self->ActiveRequest.Reset();
			if (!bSucceeded)
			{
				Self->HandleDeliveryFailure(ERetryOperation::Receipt, MoveTemp(Error));
				return;
			}
			if (Self->ActiveResult->Outcome == TEXT("unknown"))
			{
				Self->Halt(TEXT("The game reported an unresolved action outcome."));
				return;
			}
			Self->FinishResolvedDelivery();
		});
}

void FOpenGameAgentActionPump::DeliverReconciliation()
{
	if (!ActiveAction.IsSet() || !ActiveReconciliation.IsSet())
	{
		Halt(TEXT("The action reconciliation state is incomplete."));
		return;
	}
	State = EOpenGameAgentActionPumpState::Reconciling;
	const uint64 Epoch = ++NetworkEpoch;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	ActiveRequest = Client.ReconcileAction(
		ActiveAction.GetValue(),
		ActiveReconciliation.GetValue(),
		[WeakSelf, Epoch](const bool bSucceeded, FOpenGameAgentClientError Error) mutable
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			if (!Self.IsValid() || !Self->IsCurrentNetworkCallback(
					Epoch,
					EOpenGameAgentActionPumpState::Reconciling))
			{
				return;
			}
			Self->ActiveRequest.Reset();
			if (!bSucceeded)
			{
				Self->HandleDeliveryFailure(ERetryOperation::Reconciliation, MoveTemp(Error));
				return;
			}
			if (Self->ActiveReconciliation->Resolution == TEXT("unknown"))
			{
				Self->Halt(TEXT("The game could not resolve the authoritative action state."));
				return;
			}
			Self->FinishResolvedDelivery();
		});
}

void FOpenGameAgentActionPump::ReadConflict()
{
	if (!ActiveAction.IsSet())
	{
		Halt(TEXT("The conflicted action identity is unavailable."));
		return;
	}
	State = EOpenGameAgentActionPumpState::ReadingConflict;
	const uint64 Epoch = ++NetworkEpoch;
	const FString ExpectedOperationId = ActiveAction->OperationId;
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> WeakSelf = AsShared();
	ActiveRequest = Client.GetAction(
		Options.SessionId,
		Options.ActorId,
		ExpectedOperationId,
		[WeakSelf, Epoch, ExpectedOperationId](
			const bool bSucceeded,
			TOptional<FOpenGameAgentAction> Action,
			FOpenGameAgentClientError Error) mutable
		{
			const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Self = WeakSelf.Pin();
			if (!Self.IsValid() || !Self->IsCurrentNetworkCallback(
					Epoch,
					EOpenGameAgentActionPumpState::ReadingConflict))
			{
				return;
			}
			Self->ActiveRequest.Reset();
			if (!bSucceeded)
			{
				if (IsAmbiguousFailure(Error))
				{
					Self->ScheduleRetry(ERetryOperation::ConflictRead, MoveTemp(Error));
				}
				else
				{
					Self->Halt(TEXT("The authoritative conflict read was rejected."), MoveTemp(Error));
				}
				return;
			}
			Self->HandleConflictRead(MoveTemp(Action));
		});
}

void FOpenGameAgentActionPump::HandleConflictRead(TOptional<FOpenGameAgentAction> Action)
{
	check(IsInGameThread());
	if (!ActiveAction.IsSet() || !Action.IsSet() ||
		Action->SessionId != Options.SessionId || Action->ActorId != Options.ActorId ||
		Action->OperationId != ActiveAction->OperationId)
	{
		Halt(TEXT("The conflicted action is missing from Runtime authority."));
		return;
	}
	if (Action->Status == TEXT("pending") || Action->Status == TEXT("succeeded") || Action->Status == TEXT("failed"))
	{
		FinishResolvedDelivery();
		ElapsedSeconds = Options.IdlePollIntervalSeconds;
		return;
	}
	if ((Action->Status != TEXT("claimed") && Action->Status != TEXT("uncertain")) ||
		Action->ClaimToken.IsEmpty() || Action->ClaimExpiresAt.IsEmpty())
	{
		Halt(TEXT("The authoritative conflict read returned an invalid unresolved action."));
		return;
	}
	if (LastConflictInspectionFence.IsSet() &&
		LastConflictInspectionFence->ClaimToken == Action->ClaimToken &&
		LastConflictInspectionFence->Attempt == Action->Attempt)
	{
		Halt(TEXT("The authoritative conflict read made no progress from the inspected claim fence."));
		return;
	}
	LastConflictInspectionFence = FConflictInspectionFence{Action->ClaimToken, Action->Attempt};
	ActiveAction = Action.GetValue();
	ActiveResult.Reset();
	ActiveReconciliation.Reset();
	RetryDelayIndex = 0;
	BeginInspect(MoveTemp(Action.GetValue()));
}

void FOpenGameAgentActionPump::HandleDeliveryFailure(
	const ERetryOperation Operation,
	FOpenGameAgentClientError Error)
{
	if (Error.StatusCode == 409)
	{
		RetryDelayIndex = 0;
		LastError = MoveTemp(Error);
		ReadConflict();
		return;
	}
	if (IsAmbiguousFailure(Error))
	{
		ScheduleRetry(Operation, MoveTemp(Error));
		return;
	}
	Halt(TEXT("The Runtime rejected the authoritative action delivery."), MoveTemp(Error));
}

void FOpenGameAgentActionPump::ScheduleRetry(
	const ERetryOperation Operation,
	FOpenGameAgentClientError Error)
{
	const TArray<double>& RetryDelays = Options.DeliveryPolicy->RetryDelaysSeconds;
	if (!RetryDelays.IsValidIndex(RetryDelayIndex))
	{
		Halt(TEXT("The authoritative action delivery remains ambiguous after its configured attempts."), MoveTemp(Error));
		return;
	}
	LastError = MoveTemp(Error);
	RetryAtSeconds = FPlatformTime::Seconds() + RetryDelays[RetryDelayIndex++];
	RetryOperation = Operation;
	State = EOpenGameAgentActionPumpState::WaitingToRetry;
}

void FOpenGameAgentActionPump::FinishResolvedDelivery()
{
	ActiveAction.Reset();
	ActiveResult.Reset();
	ActiveReconciliation.Reset();
	LastConflictInspectionFence.Reset();
	LastError.Reset();
	RetryDelayIndex = 0;
	RetryOperation = ERetryOperation::None;
	ElapsedSeconds = 0.0;
	State = EOpenGameAgentActionPumpState::Idle;
}

void FOpenGameAgentActionPump::Halt(FString Message, FOpenGameAgentClientError Error)
{
	++NetworkEpoch;
	++OperationEpoch;
	if (ActiveRequest.IsValid())
	{
		ActiveRequest->Cancel();
		ActiveRequest.Reset();
	}
	CancelActiveOperation();
	if (Error.Message.IsEmpty())
	{
		Error.Message = MoveTemp(Message);
	}
	LastError = MoveTemp(Error);
	State = EOpenGameAgentActionPumpState::Halted;
}

void FOpenGameAgentActionPump::CancelActiveOperation()
{
	if (ExecutionContext.IsValid())
	{
		ExecutionContext->Cancel();
		ExecutionContext.Reset();
	}
	if (InspectionContext.IsValid())
	{
		InspectionContext->Cancel();
		InspectionContext.Reset();
	}
}

bool FOpenGameAgentActionPump::CanUseExecutionFence(const uint64 ExpectedEpoch) const
{
	return IsInGameThread() && State == EOpenGameAgentActionPumpState::Executing &&
		ExpectedEpoch == OperationEpoch && FPlatformTime::Seconds() < OperationDeadlineSeconds &&
		ActiveAction.IsSet();
}

bool FOpenGameAgentActionPump::CanBeginWorldWrite(const uint64 ExpectedEpoch) const
{
	if (!CanUseExecutionFence(ExpectedEpoch) || !Adapter->RecheckExpectedAuthority(ActiveAction.GetValue()))
	{
		return false;
	}
	return CanUseExecutionFence(ExpectedEpoch);
}

bool FOpenGameAgentActionPump::CanCompleteInspection(const uint64 ExpectedEpoch) const
{
	return IsInGameThread() && State == EOpenGameAgentActionPumpState::Inspecting &&
		ExpectedEpoch == OperationEpoch && FPlatformTime::Seconds() < OperationDeadlineSeconds;
}

bool FOpenGameAgentActionPump::IsCurrentNetworkCallback(
	const uint64 ExpectedEpoch,
	const EOpenGameAgentActionPumpState ExpectedState) const
{
	return IsInGameThread() && ExpectedEpoch == NetworkEpoch && State == ExpectedState;
}

bool FOpenGameAgentActionPump::IsAmbiguousFailure(const FOpenGameAgentClientError& Error)
{
	if (Error.StatusCode >= 300 && Error.StatusCode <= 499)
	{
		return false;
	}
	return (Error.StatusCode >= 500 && Error.StatusCode <= 599) ||
		Error.Code == TEXT("ambiguous_transport");
}

#if WITH_DEV_AUTOMATION_TESTS
namespace
{
class FActionPumpProbeAdapter final : public IOpenGameAgentAuthoritativeActionAdapter
{
public:
	void Execute(TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> Context) override
	{
		++ExecuteCount;
		Execution = Context;
	}

	void Inspect(TSharedRef<FOpenGameAgentActionInspectionContext, ESPMode::ThreadSafe> Context) override
	{
		++InspectCount;
		Inspection = Context;
	}

	bool RecheckExpectedAuthority(const FOpenGameAgentAction&) override
	{
		++AuthorityCheckCount;
		if (BeforeAuthorityCheck)
		{
			BeforeAuthorityCheck();
		}
		return bAuthorityMatches;
	}

	int32 ExecuteCount = 0;
	int32 InspectCount = 0;
	int32 AuthorityCheckCount = 0;
	bool bAuthorityMatches = true;
	TFunction<void()> BeforeAuthorityCheck;
	TSharedPtr<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> Execution;
	TSharedPtr<FOpenGameAgentActionInspectionContext, ESPMode::ThreadSafe> Inspection;
};

class FNetworkActionPumpAdapter final : public IOpenGameAgentAuthoritativeActionAdapter
{
public:
	void Execute(TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> Context) override
	{
		++ExecuteCount;
		ExecutedClaimToken = Context->GetAction().ClaimToken;
		bExecutionCommitted = Context->TryCommit(
			[this](const FOpenGameAgentAction&)
			{
				++WorldTransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			});
	}

	void Inspect(TSharedRef<FOpenGameAgentActionInspectionContext, ESPMode::ThreadSafe> Context) override
	{
		++InspectCount;
		InspectedClaimToken = Context->GetAction().ClaimToken;
		FOpenGameAgentActionReconciliation Reconciliation;
		Reconciliation.Resolution = TEXT("applied");
		bInspectionCompleted = Context->TryComplete(MoveTemp(Reconciliation));
	}

	bool RecheckExpectedAuthority(const FOpenGameAgentAction&) override
	{
		++AuthorityCheckCount;
		return true;
	}

	int32 ExecuteCount = 0;
	int32 InspectCount = 0;
	int32 AuthorityCheckCount = 0;
	int32 WorldTransactionCount = 0;
	bool bExecutionCommitted = false;
	bool bInspectionCompleted = false;
	FString ExecutedClaimToken;
	FString InspectedClaimToken;
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FOpenGameAgentActionPumpValidationTest,
	"OpenGameAgent.RuntimeClient.ActionPump",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentActionPumpValidationTest::RunTest(const FString&)
{
	FOpenGameAgentActionDeliveryPolicy NoRetries;
	TestTrue(TEXT("an explicit zero-retry policy is finite"), NoRetries.IsValid());
	FOpenGameAgentActionDeliveryPolicy InvalidPolicy;
	InvalidPolicy.RetryDelaysSeconds = {-1.0};
	TestFalse(TEXT("a negative retry delay is rejected"), InvalidPolicy.IsValid());

	FOpenGameAgentAction Action;
	Action.SessionId = TEXT("session");
	Action.ActorId = TEXT("actor");
	Action.OperationId = TEXT("operation");
	Action.Status = TEXT("claimed");
	Action.Attempt = TEXT("1");
	Action.ClaimToken = TEXT("claim-1");
	Action.ClaimExpiresAt = (FDateTime::UtcNow() + FTimespan::FromMinutes(1.0)).ToIso8601();
	FOpenGameAgentActionPumpOptions PumpOptions;
	PumpOptions.SessionId = TEXT("session");
	PumpOptions.ActorId = TEXT("actor");
	PumpOptions.IdlePollIntervalSeconds = 1.0;
	PumpOptions.ExecutionDurationSeconds = 60.0;
	PumpOptions.DeliveryPolicy = NoRetries;
	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> DispatchAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> DispatchPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, DispatchAdapter));
	DispatchPump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Dispatch, Action});
	TestEqual(TEXT("dispatch invokes Execute exactly once"), DispatchAdapter->ExecuteCount, 1);
	TestEqual(TEXT("dispatch never invokes Inspect"), DispatchAdapter->InspectCount, 0);
	DispatchPump->Stop();
	TestTrue(
		TEXT("stopping dispatch cooperatively cancels its execution context"),
		DispatchAdapter->Execution.IsValid() && DispatchAdapter->Execution->IsCancellationRequested());

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> TimedOutAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> TimedOutPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, TimedOutAdapter));
	TimedOutPump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Dispatch, Action});
	const TSharedPtr<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> TimedOutExecution =
		TimedOutAdapter->Execution;
	TimedOutPump->OperationDeadlineSeconds = FPlatformTime::Seconds() - 1.0;
	TimedOutPump->Tick(0.0F);
	TestEqual(
		TEXT("an elapsed execution fence enters authoritative inspection"),
		TimedOutPump->GetState(),
		EOpenGameAgentActionPumpState::Inspecting);
	TestEqual(TEXT("an elapsed execution fence invokes Inspect once"), TimedOutAdapter->InspectCount, 1);
	TestTrue(
		TEXT("inspection transition cancels the elapsed execution context"),
		TimedOutExecution.IsValid() && TimedOutExecution->IsCancellationRequested());
	TimedOutPump->Stop();

	FOpenGameAgentAction ExpiredAction = Action;
	ExpiredAction.ClaimExpiresAt = (FDateTime::UtcNow() - FTimespan::FromMinutes(1.0)).ToIso8601();
	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> ExpiredAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> ExpiredPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, ExpiredAdapter));
	ExpiredPump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Dispatch, MoveTemp(ExpiredAction)});
	TestEqual(TEXT("an expired claim never invokes Execute"), ExpiredAdapter->ExecuteCount, 0);
	TestEqual(TEXT("an expired claim invokes Inspect once"), ExpiredAdapter->InspectCount, 1);
	TestEqual(
		TEXT("an expired claim enters authoritative inspection"),
		ExpiredPump->GetState(),
		EOpenGameAgentActionPumpState::Inspecting);
	ExpiredPump->Stop();

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> ReconcileAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> ReconcilePump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, ReconcileAdapter));
	ReconcilePump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Reconcile, Action});
	TestEqual(TEXT("reconcile invokes Inspect exactly once"), ReconcileAdapter->InspectCount, 1);
	TestEqual(TEXT("reconcile never invokes Execute"), ReconcileAdapter->ExecuteCount, 0);
	ReconcilePump->Stop();
	TestTrue(
		TEXT("stopping reconcile cooperatively cancels its inspection context"),
		ReconcileAdapter->Inspection.IsValid() && ReconcileAdapter->Inspection->IsCancellationRequested());

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> AuthorityAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	AuthorityAdapter->bAuthorityMatches = false;
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> AuthorityPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, AuthorityAdapter));
	AuthorityPump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Dispatch, Action});
	int32 RejectedAuthorityTransactions = 0;
	TestFalse(
		TEXT("a changed expected authority closes the world-write permit"),
		AuthorityAdapter->Execution->TryCommit(
			[&RejectedAuthorityTransactions](const FOpenGameAgentAction&)
			{
				++RejectedAuthorityTransactions;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(TEXT("the changed authority was rechecked once"), AuthorityAdapter->AuthorityCheckCount, 1);
	TestEqual(TEXT("the closed authority performed no world transaction"), RejectedAuthorityTransactions, 0);
	FOpenGameAgentActionResult RejectedWithoutWrite;
	RejectedWithoutWrite.Outcome = TEXT("rejected");
	TestTrue(
		TEXT("a no-write completion uses only the local execution fence"),
		AuthorityAdapter->Execution->TryCompleteWithoutWrite(MoveTemp(RejectedWithoutWrite)));
	TestEqual(
		TEXT("a no-write completion does not repeat the expected-authority write check"),
		AuthorityAdapter->AuthorityCheckCount,
		1);
	AuthorityPump->Stop();

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> StopDuringAuthorityAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> StopDuringAuthorityPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, StopDuringAuthorityAdapter));
	StopDuringAuthorityPump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Dispatch, Action});
	const TWeakPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> StopDuringAuthorityWeak =
		StopDuringAuthorityPump;
	StopDuringAuthorityAdapter->BeforeAuthorityCheck = [StopDuringAuthorityWeak]()
	{
		if (const TSharedPtr<FOpenGameAgentActionPump, ESPMode::ThreadSafe> Pump = StopDuringAuthorityWeak.Pin())
		{
			Pump->Stop();
		}
	};
	int32 StopDuringAuthorityTransactions = 0;
	TestFalse(
		TEXT("closing the execution fence during authority recheck rejects the outer transaction"),
		StopDuringAuthorityAdapter->Execution->TryCommit(
			[&StopDuringAuthorityTransactions](const FOpenGameAgentAction&)
			{
				++StopDuringAuthorityTransactions;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(
		TEXT("closing the execution fence during authority recheck performs no world transaction"),
		StopDuringAuthorityTransactions,
		0);
	TestEqual(
		TEXT("the authority callback closed the action pump"),
		StopDuringAuthorityPump->GetState(),
		EOpenGameAgentActionPumpState::Stopped);

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> ForeignAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> ForeignPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, ForeignAdapter));
	FOpenGameAgentAction ForeignAction = Action;
	ForeignAction.ActorId = TEXT("other-actor");
	ForeignPump->BeginDelivery({EOpenGameAgentActionDeliveryKind::Dispatch, MoveTemp(ForeignAction)});
	TestTrue(TEXT("a cross-actor delivery halts before execution"), ForeignPump->IsHalted());
	TestEqual(TEXT("a cross-actor delivery never invokes Execute"), ForeignAdapter->ExecuteCount, 0);
	ForeignPump->Stop();

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> NetworkStopAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> NetworkStopPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, NetworkStopAdapter));
	NetworkStopPump->State = EOpenGameAgentActionPumpState::WaitingForDelivery;
	const uint64 WaitingNetworkEpoch = ++NetworkStopPump->NetworkEpoch;
	TestTrue(
		TEXT("the active network epoch accepts only its waiting-delivery callback"),
		NetworkStopPump->IsCurrentNetworkCallback(
			WaitingNetworkEpoch,
			EOpenGameAgentActionPumpState::WaitingForDelivery));
	NetworkStopPump->Stop();
	TestFalse(
		TEXT("stop invalidates the old network callback epoch"),
		NetworkStopPump->IsCurrentNetworkCallback(
			WaitingNetworkEpoch,
			EOpenGameAgentActionPumpState::WaitingForDelivery));

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> ConflictAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> ConflictPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, ConflictAdapter));
	ConflictPump->ActiveAction = Action;
	ConflictPump->State = EOpenGameAgentActionPumpState::ReadingConflict;
	FOpenGameAgentAction CurrentUnresolved = Action;
	CurrentUnresolved.Status = TEXT("uncertain");
	CurrentUnresolved.Attempt = TEXT("2");
	CurrentUnresolved.ClaimToken = TEXT("claim-2");
	ConflictPump->HandleConflictRead(CurrentUnresolved);
	TestEqual(TEXT("an unresolved conflict read invokes Inspect"), ConflictAdapter->InspectCount, 1);
	TestEqual(
		TEXT("inspection uses the current authoritative claim fence"),
		ConflictAdapter->Inspection->GetAction().ClaimToken,
		FString(TEXT("claim-2")));
	ConflictPump->State = EOpenGameAgentActionPumpState::ReadingConflict;
	ConflictPump->HandleConflictRead(CurrentUnresolved);
	TestTrue(TEXT("an unchanged authoritative conflict fence halts"), ConflictPump->IsHalted());
	TestEqual(TEXT("an unchanged authoritative conflict fence is not inspected twice"), ConflictAdapter->InspectCount, 1);
	ConflictPump->Stop();

	const TSharedRef<FActionPumpProbeAdapter, ESPMode::ThreadSafe> SettledAdapter =
		MakeShared<FActionPumpProbeAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> SettledPump = MakeShareable(
		new FOpenGameAgentActionPump(PumpOptions, SettledAdapter));
	SettledPump->ActiveAction = Action;
	SettledPump->State = EOpenGameAgentActionPumpState::ReadingConflict;
	FOpenGameAgentAction SettledAction = Action;
	SettledAction.Status = TEXT("succeeded");
	SettledAction.ClaimToken.Reset();
	SettledAction.ClaimExpiresAt.Reset();
	SettledPump->HandleConflictRead(SettledAction);
	TestEqual(TEXT("a settled conflict read returns the pump to idle"), SettledPump->GetState(), EOpenGameAgentActionPumpState::Idle);
	TestEqual(TEXT("a settled conflict read does not inspect"), SettledAdapter->InspectCount, 0);
	SettledPump->Stop();

	int32 TransactionCount = 0;
	TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> Execution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			11,
			FPlatformTime::Seconds() + 60.0,
			[](uint64 Epoch) { return Epoch == 11; }));
	TestTrue(
		TEXT("the current GameThread epoch admits one synchronous world transaction"),
		Execution->TryCommit(
			[&TransactionCount](const FOpenGameAgentAction&)
			{
				++TransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(TEXT("the world transaction ran exactly once"), TransactionCount, 1);
	TestFalse(
		TEXT("a duplicate completion cannot run another world transaction"),
		Execution->TryCommit(
			[&TransactionCount](const FOpenGameAgentAction&)
			{
				++TransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(TEXT("the duplicate transaction stayed suppressed"), TransactionCount, 1);

	int32 ReentrantTransactionCount = 0;
	bool bNestedCommit = true;
	TSharedPtr<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> ReentrantExecution;
	ReentrantExecution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			15,
			FPlatformTime::Seconds() + 60.0,
			[](uint64 Epoch) { return Epoch == 15; }));
	TestTrue(
		TEXT("the current epoch admits the outer synchronous world transaction"),
		ReentrantExecution->TryCommit(
			[&](const FOpenGameAgentAction&)
			{
				++ReentrantTransactionCount;
				bNestedCommit = ReentrantExecution->TryCommit(
					[&](const FOpenGameAgentAction&)
					{
						++ReentrantTransactionCount;
						FOpenGameAgentActionResult NestedResult;
						NestedResult.Outcome = TEXT("succeeded");
						return NestedResult;
					});
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestFalse(TEXT("a synchronous reentrant completion cannot open a second world write"), bNestedCommit);
	TestEqual(TEXT("the reentrant transaction ran exactly once"), ReentrantTransactionCount, 1);

	int32 ReentrantPermitTransactionCount = 0;
	bool bNestedPermitCommit = true;
	TSharedPtr<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> ReentrantPermitExecution;
	ReentrantPermitExecution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			17,
			FPlatformTime::Seconds() + 60.0,
			[&](uint64 Epoch)
			{
				bNestedPermitCommit = ReentrantPermitExecution->TryCommit(
					[&](const FOpenGameAgentAction&)
					{
						++ReentrantPermitTransactionCount;
						FOpenGameAgentActionResult NestedResult;
						NestedResult.Outcome = TEXT("succeeded");
						return NestedResult;
					});
				return Epoch == 17;
			}));
	TestTrue(
		TEXT("the outer commit survives a reentrant world-write permit callback"),
		ReentrantPermitExecution->TryCommit(
			[&](const FOpenGameAgentAction&)
			{
				++ReentrantPermitTransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestFalse(TEXT("the authority recheck cannot reenter a second world write"), bNestedPermitCommit);
	TestEqual(TEXT("the reentrant permit transaction ran exactly once"), ReentrantPermitTransactionCount, 1);

	int32 InvalidResultTransactionCount = 0;
	TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> InvalidResultExecution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			16,
			FPlatformTime::Seconds() + 60.0,
			[](uint64 Epoch) { return Epoch == 16; }));
	TestTrue(
		TEXT("an admitted world write remains a completed uncertain operation when its result is invalid"),
		InvalidResultExecution->TryCommit(
			[&](const FOpenGameAgentAction&)
			{
				++InvalidResultTransactionCount;
				return FOpenGameAgentActionResult{};
			}));
	TestEqual(TEXT("the invalid-result transaction ran exactly once"), InvalidResultTransactionCount, 1);
	TestEqual(
		TEXT("an invalid result cannot falsely describe the admitted world write"),
		InvalidResultExecution->TakeResult().Outcome,
		FString(TEXT("unknown")));

	TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> CancelledExecution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			12,
			FPlatformTime::Seconds() + 60.0,
			[](uint64) { return true; }));
	CancelledExecution->Cancel();
	TestTrue(TEXT("cooperative cancellation is observable"), CancelledExecution->IsCancellationRequested());
	TestFalse(
		TEXT("a late completion cannot begin a world transaction"),
		CancelledExecution->TryCommit(
			[&TransactionCount](const FOpenGameAgentAction&)
			{
				++TransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(TEXT("the late transaction stayed suppressed"), TransactionCount, 1);

	TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> NoPermitExecution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			13,
			FPlatformTime::Seconds() + 60.0,
			[](uint64) { return false; }));
	TestFalse(
		TEXT("a closed local epoch cannot begin a world transaction"),
		NoPermitExecution->TryCommit(
			[&TransactionCount](const FOpenGameAgentAction&)
			{
				++TransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(TEXT("the closed epoch stayed side-effect free"), TransactionCount, 1);
	TSharedRef<FOpenGameAgentActionExecutionContext, ESPMode::ThreadSafe> ExpiredExecution = MakeShareable(
		new FOpenGameAgentActionExecutionContext(
			Action,
			14,
			FPlatformTime::Seconds() - 1.0,
			[](uint64) { return true; }));
	TestTrue(TEXT("an elapsed execution deadline requests cancellation"), ExpiredExecution->IsCancellationRequested());
	TestFalse(
		TEXT("an elapsed deadline cannot begin a world transaction"),
		ExpiredExecution->TryCommit(
			[&TransactionCount](const FOpenGameAgentAction&)
			{
				++TransactionCount;
				FOpenGameAgentActionResult Result;
				Result.Outcome = TEXT("succeeded");
				return Result;
			}));
	TestEqual(TEXT("the expired transaction stayed suppressed"), TransactionCount, 1);

	TSharedRef<FOpenGameAgentActionInspectionContext, ESPMode::ThreadSafe> Inspection = MakeShareable(
		new FOpenGameAgentActionInspectionContext(
			Action,
			21,
			FPlatformTime::Seconds() + 60.0,
			[](uint64 Epoch) { return Epoch == 21; }));
	FOpenGameAgentActionReconciliation Reconciliation;
	Reconciliation.Resolution = TEXT("applied");
	TestTrue(TEXT("an inspection records one reconciliation"), Inspection->TryComplete(Reconciliation));
	TestFalse(TEXT("a duplicate inspection result is rejected"), Inspection->TryComplete(Reconciliation));

	TestTrue(
		TEXT("no response is an ambiguous retry candidate"),
		FOpenGameAgentActionPump::IsAmbiguousFailure({0, TEXT("no response"), TEXT("ambiguous_transport")}));
	TestTrue(
		TEXT("a 5xx is an ambiguous retry candidate"),
		FOpenGameAgentActionPump::IsAmbiguousFailure({599, TEXT("server failure")}));
	TestFalse(
		TEXT("a local pre-dispatch failure is not retried"),
		FOpenGameAgentActionPump::IsAmbiguousFailure({0, TEXT("invalid request"), TEXT("invalid_request")}));
	TestFalse(
		TEXT("a known 4xx remains definitive even when its body transfer is incomplete"),
		FOpenGameAgentActionPump::IsAmbiguousFailure({400, TEXT("truncated error"), TEXT("ambiguous_transport")}));
	TestFalse(
		TEXT("a known redirect remains definitive even when its body transfer is incomplete"),
		FOpenGameAgentActionPump::IsAmbiguousFailure({307, TEXT("truncated redirect"), TEXT("ambiguous_transport")}));
	for (const int32 Status : {400, 409, 499})
	{
		TestFalse(
			TEXT("a definitive 4xx is not an ambiguous retry candidate"),
			FOpenGameAgentActionPump::IsAmbiguousFailure({Status, TEXT("client failure")}));
	}

#if OGA_WITH_LIBCURL
	const FString BaseUrl = FPlatformMisc::GetEnvironmentVariable(TEXT("OGA_UNREAL_TEST_BASE_URL"));
	const FString TimeoutText = FPlatformMisc::GetEnvironmentVariable(TEXT("OGA_UNREAL_TEST_TIMEOUT_MS"));
	int64 GateTimeoutMilliseconds = 0;
	if (BaseUrl.IsEmpty() || !LexTryParseString(GateTimeoutMilliseconds, *TimeoutText) ||
		GateTimeoutMilliseconds <= 0 || GateTimeoutMilliseconds > MAX_int32)
	{
		AddError(TEXT("The action-pump network gate requires its base URL and an explicit positive timeout."));
		return false;
	}

	FOpenGameAgentTransportLimits TransportLimits;
	TransportLimits.MaxConcurrentRequests = 1;
	TransportLimits.MaxQueuedRequests = 0;
	if (!TestTrue(
		TEXT("the action-pump network gate uses the configured process transport"),
		FOpenGameAgentModule::ConfigureTransport(TransportLimits)))
	{
		return false;
	}

	const FString AccessToken = TEXT("gate-token");
	const TArray<FString> RequestFixtures = {
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"operationId\":\"operation-network\",\"claimToken\":\"claim-1\",\"attempt\":\"1\",\"outcome\":\"succeeded\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"operationId\":\"operation-network\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"operationId\":\"operation-network\",\"claimToken\":\"claim-2\",\"attempt\":\"2\",\"resolution\":\"applied\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-exhaust-actor\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-exhaust-actor\",\"operationId\":\"operation-exhaust\",\"claimToken\":\"claim-exhaust\",\"attempt\":\"1\",\"outcome\":\"succeeded\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-invalid-recovery-actor\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-invalid-pending-recovery-actor\"}"),
	};
	const TArray<FString> ResponseFixtures = {
		TEXT("{\"delivery\":{\"kind\":\"dispatch\",\"action\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"operationId\":\"operation-network\",\"actionName\":\"world.write\",\"attempt\":\"1\",\"status\":\"claimed\",\"claim\":{\"claimToken\":\"claim-1\",\"attempt\":\"1\",\"expiresAt\":\"9999-12-31T23:59:59.000Z\"},\"payload\":{\"value\":1},\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"8\"}}}}"),
		TEXT("{\"action\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"operationId\":\"operation-network\",\"actionName\":\"world.write\",\"attempt\":\"2\",\"status\":\"uncertain\",\"claim\":{\"claimToken\":\"claim-2\",\"attempt\":\"2\",\"expiresAt\":\"9999-12-31T23:59:59.000Z\"},\"payload\":{\"value\":1},\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"8\"}}}"),
		TEXT("{\"delivery\":{\"kind\":\"dispatch\",\"action\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-exhaust-actor\",\"operationId\":\"operation-exhaust\",\"actionName\":\"world.write\",\"attempt\":\"1\",\"status\":\"claimed\",\"claim\":{\"claimToken\":\"claim-exhaust\",\"attempt\":\"1\",\"expiresAt\":\"9999-12-31T23:59:59.000Z\"},\"payload\":{\"value\":1},\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"8\"}}}}"),
		TEXT("{\"delivery\":{\"kind\":\"dispatch\",\"action\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-invalid-recovery-actor\",\"operationId\":\"operation-invalid-recovery\",\"actionName\":\"world.write\",\"attempt\":\"1\",\"status\":\"claimed\",\"claim\":{\"claimToken\":\"claim-invalid-recovery\",\"attempt\":\"1\",\"expiresAt\":\"9999-12-31T23:59:59.000Z\"},\"payload\":{\"value\":1},\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"8\"}}}}"),
		TEXT("{\"delivery\":{\"kind\":\"reconcile\",\"action\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-invalid-pending-recovery-actor\",\"operationId\":\"operation-invalid-pending-recovery\",\"actionName\":\"world.write\",\"attempt\":\"1\",\"status\":\"pending\",\"claim\":{\"claimToken\":\"claim-pending\",\"attempt\":\"1\",\"expiresAt\":\"9999-12-31T23:59:59.000Z\"},\"payload\":{\"value\":1},\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"8\"}}}}"),
		TEXT("{\"error\":{\"code\":\"action_claim_conflict\",\"message\":\"conflict\"}}"),
		TEXT("{\"error\":{\"code\":\"runtime_unavailable\",\"message\":\"retry\"}}"),
		TEXT("{}"),
	};
	const TArray<FString> Paths = {
		TEXT("/runtime/v1/actions/next"),
		TEXT("/runtime/v1/actions/recovery"),
		TEXT("/runtime/v1/actions/receipt"),
		TEXT("/runtime/v1/actions/get"),
		TEXT("/runtime/v1/actions/reconcile"),
	};

	FOpenGameAgentClientOptions NetworkClientOptions;
	NetworkClientOptions.BaseUrl = BaseUrl;
	NetworkClientOptions.AccessTokenProvider = [AccessToken]
	{
		return AccessToken;
	};
	for (const FString& Fixture : RequestFixtures)
	{
		NetworkClientOptions.Limits.MaxRequestBytes = FMath::Max<int64>(
			NetworkClientOptions.Limits.MaxRequestBytes,
			FTCHARToUTF8(*Fixture).Length());
	}
	for (const FString& Fixture : ResponseFixtures)
	{
		NetworkClientOptions.Limits.MaxResponseBytes = FMath::Max<int64>(
			NetworkClientOptions.Limits.MaxResponseBytes,
			FTCHARToUTF8(*Fixture).Length());
	}
	for (const FString& Path : Paths)
	{
		NetworkClientOptions.Limits.MaxUrlBytes = FMath::Max<int64>(
			NetworkClientOptions.Limits.MaxUrlBytes,
			FTCHARToUTF8(*(BaseUrl + Path)).Length());
	}
	NetworkClientOptions.Limits.MaxAttachmentResponseBytes = NetworkClientOptions.Limits.MaxResponseBytes;
	NetworkClientOptions.Limits.MaxResponseHeaderBytes = MAX_int32;
	NetworkClientOptions.Limits.MaxResponseHeaders = 3;
	NetworkClientOptions.Limits.MaxAccessTokenBytes = FTCHARToUTF8(*AccessToken).Length();
	NetworkClientOptions.Limits.RequestTimeoutMilliseconds = GateTimeoutMilliseconds;
	if (!TestTrue(TEXT("the action-pump network client config is valid"), NetworkClientOptions.IsValid()))
	{
		return false;
	}
	FOpenGameAgentClientOptions UndersizedNextOptions = NetworkClientOptions;
	UndersizedNextOptions.Limits.MaxRequestBytes = FTCHARToUTF8(*RequestFixtures[0]).Length() - 1;
	FOpenGameAgentRuntimeClient UndersizedNextClient(MoveTemp(UndersizedNextOptions));
	bool bUndersizedNextCompleted = false;
	FOpenGameAgentClientError UndersizedNextError;
	const FOpenGameAgentRequestPtr UndersizedNextRequest = UndersizedNextClient.NextAction(
		TEXT("gate-session"),
		TEXT("gate-actor"),
		[&bUndersizedNextCompleted, &UndersizedNextError](
			const EOpenGameAgentNextActionResult,
			FOpenGameAgentActionDelivery,
			FOpenGameAgentClientError Error)
		{
			bUndersizedNextCompleted = true;
			UndersizedNextError = MoveTemp(Error);
		});
	TestFalse(TEXT("a one-byte-undersized next-action request is rejected before dispatch"), UndersizedNextRequest.IsValid());
	TestTrue(TEXT("a one-byte-undersized next-action request completes synchronously"), bUndersizedNextCompleted);
	TestEqual(
		TEXT("a one-byte-undersized next-action request has a stable local failure"),
		UndersizedNextError.Code,
		FString(TEXT("transport_rejected")));

	FOpenGameAgentActionDeliveryPolicy NetworkDeliveryPolicy;
	NetworkDeliveryPolicy.RetryDelaysSeconds = {0.0, 0.0};
	auto BuildNetworkOptions = [&NetworkClientOptions, &NetworkDeliveryPolicy, GateTimeoutMilliseconds](
		const FString& ActorId)
	{
		FOpenGameAgentActionPumpOptions Options;
		Options.Client = NetworkClientOptions;
		Options.SessionId = TEXT("gate-session");
		Options.ActorId = ActorId;
		Options.IdlePollIntervalSeconds = static_cast<double>(GateTimeoutMilliseconds) / 1000.0;
		Options.ExecutionDurationSeconds = static_cast<double>(GateTimeoutMilliseconds) / 1000.0;
		Options.DeliveryPolicy = NetworkDeliveryPolicy;
		return Options;
	};
	auto DriveUntil = [this, GateTimeoutMilliseconds](
		const FString& Label,
		const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe>& Pump,
		TFunctionRef<bool()> IsComplete)
	{
		const uint64 StartedAtCycles = FPlatformTime::Cycles64();
		while (!IsComplete() &&
			FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - StartedAtCycles) < GateTimeoutMilliseconds)
		{
			Pump->Tick(0.0F);
			FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
			FPlatformProcess::SleepNoStats(0.0F);
		}
		return TestTrue(Label, IsComplete());
	};

	const TSharedRef<FNetworkActionPumpAdapter, ESPMode::ThreadSafe> NetworkAdapter =
		MakeShared<FNetworkActionPumpAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> NetworkPump =
		FOpenGameAgentActionPump::Create(BuildNetworkOptions(TEXT("gate-actor")), NetworkAdapter);
	if (!DriveUntil(
		TEXT("the real dispatch-conflict-read-inspect-reconcile flow completes"),
		NetworkPump,
		[&NetworkPump, &NetworkAdapter]
		{
			return (NetworkAdapter->InspectCount == 1 &&
					NetworkPump->GetState() == EOpenGameAgentActionPumpState::Idle) ||
				NetworkPump->IsHalted() ||
				(NetworkPump->GetState() == EOpenGameAgentActionPumpState::Idle &&
					NetworkPump->GetLastError().IsSet());
		}))
	{
		NetworkPump->Stop();
		return false;
	}
	if (NetworkPump->IsHalted() || NetworkPump->GetLastError().IsSet())
	{
		const FString Failure = NetworkPump->GetLastError().IsSet()
			? NetworkPump->GetLastError()->Code + TEXT(": ") + NetworkPump->GetLastError()->Message
			: TEXT("halted without a client error");
		AddError(TEXT("The real action-pump flow failed: ") + Failure);
		NetworkPump->Stop();
		return false;
	}
	TestEqual(TEXT("the real network flow executes once"), NetworkAdapter->ExecuteCount, 1);
	TestEqual(TEXT("the real network flow writes once"), NetworkAdapter->WorldTransactionCount, 1);
	TestEqual(TEXT("the real network flow checks authority once"), NetworkAdapter->AuthorityCheckCount, 1);
	TestTrue(TEXT("the real network execution commits"), NetworkAdapter->bExecutionCommitted);
	TestTrue(TEXT("the real network inspection completes"), NetworkAdapter->bInspectionCompleted);
	TestEqual(TEXT("the receipt uses the dispatched claim"), NetworkAdapter->ExecutedClaimToken, FString(TEXT("claim-1")));
	TestEqual(TEXT("the inspection uses the conflict-read claim"), NetworkAdapter->InspectedClaimToken, FString(TEXT("claim-2")));
	NetworkPump->Stop();

	const TSharedRef<FNetworkActionPumpAdapter, ESPMode::ThreadSafe> ExhaustAdapter =
		MakeShared<FNetworkActionPumpAdapter, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> ExhaustPump =
		FOpenGameAgentActionPump::Create(BuildNetworkOptions(TEXT("gate-exhaust-actor")), ExhaustAdapter);
	if (!DriveUntil(
		TEXT("the real no-response receipt exhausts its finite retry table"),
		ExhaustPump,
		[&ExhaustPump]
		{
			return ExhaustPump->IsHalted();
		}))
	{
		ExhaustPump->Stop();
		return false;
	}
	TestEqual(TEXT("retry exhaustion never re-executes the action"), ExhaustAdapter->ExecuteCount, 1);
	TestEqual(TEXT("retry exhaustion writes once"), ExhaustAdapter->WorldTransactionCount, 1);
	TestTrue(TEXT("retry exhaustion preserves its transport error"), ExhaustPump->GetLastError().IsSet());
	if (ExhaustPump->GetLastError().IsSet())
	{
		TestEqual(
			TEXT("retry exhaustion is classified as ambiguous transport"),
			ExhaustPump->GetLastError()->Code,
			FString(TEXT("ambiguous_transport")));
	}
	ExhaustPump->Stop();

	for (const TCHAR* InvalidActorId : { TEXT("gate-invalid-recovery-actor"), TEXT("gate-invalid-pending-recovery-actor") })
	{
		const TSharedRef<FNetworkActionPumpAdapter, ESPMode::ThreadSafe> InvalidRecoveryAdapter =
			MakeShared<FNetworkActionPumpAdapter, ESPMode::ThreadSafe>();
		const TSharedRef<FOpenGameAgentActionPump, ESPMode::ThreadSafe> InvalidRecoveryPump =
			FOpenGameAgentActionPump::Create(
				BuildNetworkOptions(InvalidActorId),
				InvalidRecoveryAdapter);
		if (!DriveUntil(
			FString::Printf(TEXT("the invalid recovery response completes for %s"), InvalidActorId),
			InvalidRecoveryPump,
			[&InvalidRecoveryPump]
			{
				return InvalidRecoveryPump->IsHalted() ||
					(InvalidRecoveryPump->GetState() == EOpenGameAgentActionPumpState::Idle &&
						InvalidRecoveryPump->GetLastError().IsSet());
			}))
		{
			InvalidRecoveryPump->Stop();
			return false;
		}
		TestTrue(TEXT("an invalid recovery response halts the pump"), InvalidRecoveryPump->IsHalted());
		TestEqual(TEXT("an invalid recovery response is never executed"), InvalidRecoveryAdapter->ExecuteCount, 0);
		TestEqual(TEXT("an invalid recovery response never writes world state"), InvalidRecoveryAdapter->WorldTransactionCount, 0);
		TestEqual(TEXT("an invalid recovery response is never inspected"), InvalidRecoveryAdapter->InspectCount, 0);
		TestTrue(TEXT("an invalid recovery response preserves its protocol error"), InvalidRecoveryPump->GetLastError().IsSet());
		if (InvalidRecoveryPump->GetLastError().IsSet())
		{
			TestEqual(
				FString::Printf(TEXT("the invalid recovery response has a stable protocol error for %s"), InvalidActorId),
				InvalidRecoveryPump->GetLastError()->Code,
				FString(TEXT("invalid_response")));
		}
		InvalidRecoveryPump->Stop();
	}
#endif
	return true;
}
#endif
