#if WITH_DEV_AUTOMATION_TESTS

#include "GameAgentHostBoundary.h"
#include "GameAgentMainThreadDispatcher.h"
#include "GameAgentWireProtocol.h"
#include "Misc/AutomationTest.h"

namespace
{
constexpr std::string_view ValidActionJson = R"({
    "protocolVersion":"0.2",
    "schemaVersion":"0.2",
    "operationId":"operation-1",
    "runId":"run-1",
    "turnId":"turn-1",
    "toolCallId":"call-1",
    "agentId":"agent-1",
    "worldId":"world-1",
    "actionName":"read_state",
    "actionVersion":"1",
    "arguments":{"region":"north"},
    "requestedAt":"2026-07-28T00:00:00Z"
})";

class FDuplicateCompletionHost final : public IGameAgentHostBoundary
{
public:
    virtual void ExecuteAction(
        const game_agent::wire::ActionRequest& Request,
        FGameAgentActionCompletion&& Completion) override
    {
        game_agent::wire::ActionReceipt First;
        First.ProtocolVersion = "0.2";
        First.SchemaVersion = "0.2";
        First.OperationId = Request.OperationId;
        First.Status = game_agent::wire::ReceiptStatus::Succeeded;
        First.ReceivedAt = "2026-07-28T00:00:00Z";
        Completion(MoveTemp(First));

        game_agent::wire::ActionReceipt Duplicate;
        Duplicate.ProtocolVersion = "0.2";
        Duplicate.SchemaVersion = "0.2";
        Duplicate.OperationId = Request.OperationId;
        Duplicate.Status = game_agent::wire::ReceiptStatus::Succeeded;
        Duplicate.ReceivedAt = "2026-07-28T00:00:01Z";
        Completion(MoveTemp(Duplicate));
    }

    virtual void StopAndDrainActions() override
    {
    }
};

class FMismatchedCompletionHost final : public IGameAgentHostBoundary
{
public:
    virtual void ExecuteAction(
        const game_agent::wire::ActionRequest&,
        FGameAgentActionCompletion&& Completion) override
    {
        game_agent::wire::ActionReceipt Receipt;
        Receipt.ProtocolVersion = "0.2";
        Receipt.SchemaVersion = "0.2";
        Receipt.OperationId = "another-operation";
        Receipt.Status = game_agent::wire::ReceiptStatus::Succeeded;
        Receipt.ReceivedAt = "2026-07-28T00:00:00Z";
        Completion(MoveTemp(Receipt));
    }

    virtual void StopAndDrainActions() override
    {
    }
};

class FDeferredCompletionHost final : public IGameAgentHostBoundary
{
public:
    virtual void ExecuteAction(
        const game_agent::wire::ActionRequest& Request,
        FGameAgentActionCompletion&& Completion) override
    {
        OperationId_ = Request.OperationId;
        Completion_ = MoveTemp(Completion);
        ++ExecuteCount_;
    }

    virtual void StopAndDrainActions() override
    {
        ++StopAndDrainCount_;
    }

    void EmitSucceeded()
    {
        if (!Completion_)
        {
            return;
        }

        game_agent::wire::ActionReceipt Receipt;
        Receipt.ProtocolVersion = "0.2";
        Receipt.SchemaVersion = "0.2";
        Receipt.OperationId = OperationId_;
        Receipt.Status = game_agent::wire::ReceiptStatus::Succeeded;
        Receipt.ReceivedAt = "2026-07-28T00:00:01Z";
        Completion_(MoveTemp(Receipt));
    }

    int32 GetExecuteCount() const
    {
        return ExecuteCount_;
    }

    int32 GetStopAndDrainCount() const
    {
        return StopAndDrainCount_;
    }

private:
    std::string OperationId_;
    FGameAgentActionCompletion Completion_;
    int32 ExecuteCount_ = 0;
    int32 StopAndDrainCount_ = 0;
};

class FReentrantDrainHost final : public IGameAgentHostBoundary
{
public:
    void SetRouter(
        const TSharedPtr<FGameAgentHostRouter, ESPMode::ThreadSafe>& Router)
    {
        Router_ = Router;
    }

    virtual void ExecuteAction(
        const game_agent::wire::ActionRequest& Request,
        FGameAgentActionCompletion&& Completion) override
    {
        if (const auto Router = Router_.Pin(); Router.IsValid())
        {
            bReentrantExecuteStopResult_ = Router->Stop();
        }
        game_agent::wire::ActionReceipt Receipt;
        Receipt.ProtocolVersion = "0.2";
        Receipt.SchemaVersion = "0.2";
        Receipt.OperationId = Request.OperationId;
        Receipt.Status = game_agent::wire::ReceiptStatus::Succeeded;
        Receipt.ReceivedAt = "2026-07-28T00:00:00Z";
        Completion(MoveTemp(Receipt));
    }

    virtual void StopAndDrainActions() override
    {
        ++StopAndDrainCount_;
        if (const auto Router = Router_.Pin(); Router.IsValid())
        {
            bReentrantStopResult_ = Router->Stop();
        }
    }

    bool GetReentrantStopResult() const
    {
        return bReentrantStopResult_;
    }

    bool GetReentrantExecuteStopResult() const
    {
        return bReentrantExecuteStopResult_;
    }

    int32 GetStopAndDrainCount() const
    {
        return StopAndDrainCount_;
    }

private:
    TWeakPtr<FGameAgentHostRouter, ESPMode::ThreadSafe> Router_;
    int32 StopAndDrainCount_ = 0;
    bool bReentrantExecuteStopResult_ = true;
    bool bReentrantStopResult_ = true;
};
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FGameAgentWireParserAutomationTest,
    "GameAgent.Runtime.Unreal.WireParser",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FGameAgentWireParserAutomationTest::RunTest(const FString&)
{
    const auto Parsed = game_agent::wire::ParseActionRequest(ValidActionJson);
    TestTrue(TEXT("A valid action request parses"), Parsed.Ok);
    if (Parsed)
    {
        TestTrue(
            TEXT("Action name is preserved"),
            Parsed.Value.ActionName == "read_state");
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FGameAgentDispatcherAutomationTest,
    "GameAgent.Runtime.Unreal.GameThreadDispatcher",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FGameAgentDispatcherAutomationTest::RunTest(const FString&)
{
    auto Dispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>(1);
    int32 Executed = 0;
    TestTrue(
        TEXT("Work is accepted before shutdown"),
        Dispatcher->Enqueue([&Executed]() { ++Executed; }));
    TestFalse(
        TEXT("The bounded queue rejects overflow"),
        Dispatcher->Enqueue([]() {}));
    TestEqual(TEXT("Pending count reflects accepted work"), Dispatcher->GetPendingCount(), 1);
    TestEqual(TEXT("Queued work has not run inline"), Executed, 0);
    TestEqual(TEXT("One work item is drained"), Dispatcher->Drain(1), 1);
    TestEqual(TEXT("Work runs on drain"), Executed, 1);
    TestEqual(TEXT("Pending count returns to zero"), Dispatcher->GetPendingCount(), 0);
    int32 Abandoned = 0;
    TestTrue(
        TEXT("Work with an abandonment callback is accepted"),
        Dispatcher->Enqueue(
            []() {},
            [&Abandoned]() { ++Abandoned; }));
    Dispatcher->Stop();
    TestEqual(
        TEXT("Shutdown completes accepted queued work"),
        Abandoned,
        1);
    TestFalse(
        TEXT("Work is rejected after shutdown"),
        Dispatcher->Enqueue([]() {}));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FGameAgentHostRouterAutomationTest,
    "GameAgent.Runtime.Unreal.HostRouter",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FGameAgentHostRouterAutomationTest::RunTest(const FString&)
{
    auto Dispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto Router =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(Dispatcher);
    TestTrue(
        TEXT("The first host binding is accepted"),
        Router->BindHost(
            MakeShared<FDuplicateCompletionHost, ESPMode::ThreadSafe>()));

    int32 CompletionCount = 0;
    const FString ActionJson(UTF8_TO_TCHAR(ValidActionJson.data()));
    const auto DispatchResult = Router->DispatchActionJson(
        ActionJson,
        [&CompletionCount](game_agent::wire::ActionReceipt&&)
        {
            ++CompletionCount;
        });
    TestTrue(TEXT("A valid request is accepted"), DispatchResult.WasAccepted());
    TestEqual(TEXT("Host execution is not inline"), CompletionCount, 0);
    TestEqual(TEXT("Host work is drained"), Dispatcher->Drain(1), 1);
    TestEqual(TEXT("Duplicate completions are suppressed"), CompletionCount, 1);
    Router->UnbindHost();
    Dispatcher->Stop();

    auto MismatchDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto MismatchRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            MismatchDispatcher);
    TestTrue(
        TEXT("The mismatch test host is bound"),
        MismatchRouter->BindHost(
            MakeShared<FMismatchedCompletionHost, ESPMode::ThreadSafe>()));
    game_agent::wire::ActionReceipt Correlated;
    const auto MismatchResult = MismatchRouter->DispatchActionJson(
        ActionJson,
        [&Correlated](game_agent::wire::ActionReceipt&& Receipt)
        {
            Correlated = MoveTemp(Receipt);
        });
    TestTrue(
        TEXT("A request with a mismatched host receipt is accepted"),
        MismatchResult.WasAccepted());
    MismatchDispatcher->Drain(1);
    TestTrue(
        TEXT("A mismatched receipt is converted to unknown"),
        Correlated.Status == game_agent::wire::ReceiptStatus::Unknown);
    TestTrue(
        TEXT("The request operation id is preserved"),
        Correlated.OperationId == "operation-1");
    MismatchRouter->UnbindHost();
    MismatchDispatcher->Stop();

    auto ShutdownDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto ShutdownRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            ShutdownDispatcher);
    TestTrue(
        TEXT("The shutdown test host is bound"),
        ShutdownRouter->BindHost(
            MakeShared<FDuplicateCompletionHost, ESPMode::ThreadSafe>()));
    game_agent::wire::ActionReceipt ShutdownReceipt;
    const auto ShutdownResult = ShutdownRouter->DispatchActionJson(
        ActionJson,
        [&ShutdownReceipt](game_agent::wire::ActionReceipt&& Receipt)
        {
            ShutdownReceipt = MoveTemp(Receipt);
        });
    TestTrue(
        TEXT("Queued work is accepted before shutdown"),
        ShutdownResult.WasAccepted());
    ShutdownDispatcher->Stop();
    TestTrue(
        TEXT("Abandoned work completes as unknown"),
        ShutdownReceipt.Status == game_agent::wire::ReceiptStatus::Unknown);
    TestTrue(
        TEXT("Abandoned work preserves operation id"),
        ShutdownReceipt.OperationId == "operation-1");
    ShutdownRouter->Stop();

    auto DeferredDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto DeferredRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            DeferredDispatcher);
    auto DeferredHost =
        MakeShared<FDeferredCompletionHost, ESPMode::ThreadSafe>();
    TestTrue(
        TEXT("The deferred test host is bound"),
        DeferredRouter->BindHost(DeferredHost));
    int32 DeferredCompletionCount = 0;
    game_agent::wire::ActionReceipt DeferredReceipt;
    const auto DeferredResult = DeferredRouter->DispatchActionJson(
        ActionJson,
        [&DeferredCompletionCount, &DeferredReceipt](
            game_agent::wire::ActionReceipt&& Receipt)
        {
            ++DeferredCompletionCount;
            DeferredReceipt = MoveTemp(Receipt);
        });
    TestTrue(
        TEXT("A deferred host action is accepted"),
        DeferredResult.WasAccepted());
    TestEqual(
        TEXT("Deferred host action starts on drain"),
        DeferredDispatcher->Drain(1),
        1);
    TestEqual(
        TEXT("Deferred host action started exactly once"),
        DeferredHost->GetExecuteCount(),
        1);
    TestEqual(
        TEXT("Deferred action has not completed before shutdown"),
        DeferredCompletionCount,
        0);
    DeferredRouter->Stop();
    TestEqual(
        TEXT("Router shutdown completes a started deferred action once"),
        DeferredCompletionCount,
        1);
    TestTrue(
        TEXT("Router shutdown returns an unknown receipt"),
        DeferredReceipt.Status == game_agent::wire::ReceiptStatus::Unknown);
    TestTrue(
        TEXT("Router shutdown reports its stable error code"),
        DeferredReceipt.ErrorCode.has_value() &&
            *DeferredReceipt.ErrorCode == "router_stopped");
    TestEqual(
        TEXT("Router shutdown asks the host to quiesce"),
        DeferredHost->GetStopAndDrainCount(),
        1);
    DeferredRouter->Stop();
    TestEqual(
        TEXT("Repeated router shutdown remains idempotent"),
        DeferredHost->GetStopAndDrainCount(),
        1);
    DeferredHost->EmitSucceeded();
    DeferredHost->EmitSucceeded();
    TestEqual(
        TEXT("Late host callbacks after shutdown are ignored"),
        DeferredCompletionCount,
        1);
    DeferredDispatcher->Stop();

    auto QueuedStopDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto QueuedStopRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            QueuedStopDispatcher);
    auto QueuedStopHost =
        MakeShared<FDeferredCompletionHost, ESPMode::ThreadSafe>();
    TestTrue(
        TEXT("The queued-stop test host is bound"),
        QueuedStopRouter->BindHost(QueuedStopHost));
    int32 QueuedStopCompletionCount = 0;
    game_agent::wire::ActionReceipt QueuedStopReceipt;
    const auto QueuedStopResult = QueuedStopRouter->DispatchActionJson(
        ActionJson,
        [&QueuedStopCompletionCount, &QueuedStopReceipt](
            game_agent::wire::ActionReceipt&& Receipt)
        {
            ++QueuedStopCompletionCount;
            QueuedStopReceipt = MoveTemp(Receipt);
        });
    TestTrue(
        TEXT("An action is queued before terminal unbind"),
        QueuedStopResult.WasAccepted());
    TestTrue(
        TEXT("Terminal unbind completes before the dispatcher drains"),
        QueuedStopRouter->UnbindHost());
    TestEqual(
        TEXT("Terminal unbind completes queued work exactly once"),
        QueuedStopCompletionCount,
        1);
    TestTrue(
        TEXT("Queued work receives the router shutdown receipt"),
        QueuedStopReceipt.Status ==
                game_agent::wire::ReceiptStatus::Unknown &&
            QueuedStopReceipt.ErrorCode.has_value() &&
            *QueuedStopReceipt.ErrorCode == "router_stopped");
    TestEqual(
        TEXT("The host is quiesced during terminal unbind"),
        QueuedStopHost->GetStopAndDrainCount(),
        1);
    TestEqual(
        TEXT("The stopped queued item is drained without host execution"),
        QueuedStopDispatcher->Drain(1),
        1);
    TestEqual(
        TEXT("No host action starts after terminal unbind"),
        QueuedStopHost->GetExecuteCount(),
        0);
    TestEqual(
        TEXT("Draining stopped work does not complete it again"),
        QueuedStopCompletionCount,
        1);
    QueuedStopDispatcher->Stop();

    auto DestructionDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    TSharedPtr<FGameAgentHostRouter, ESPMode::ThreadSafe> DestructionRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            DestructionDispatcher);
    auto DestructionHost =
        MakeShared<FDeferredCompletionHost, ESPMode::ThreadSafe>();
    TestTrue(
        TEXT("The destruction test host is bound"),
        DestructionRouter->BindHost(DestructionHost));
    int32 DestructionCompletionCount = 0;
    game_agent::wire::ActionReceipt DestructionReceipt;
    const auto DestructionResult = DestructionRouter->DispatchActionJson(
        ActionJson,
        [&DestructionCompletionCount, &DestructionReceipt](
            game_agent::wire::ActionReceipt&& Receipt)
        {
            ++DestructionCompletionCount;
            DestructionReceipt = MoveTemp(Receipt);
        });
    TestTrue(
        TEXT("A deferred action is accepted before router destruction"),
        DestructionResult.WasAccepted());
    DestructionDispatcher->Drain(1);
    DestructionRouter.Reset();
    TestEqual(
        TEXT("Router destruction completes a deferred action once"),
        DestructionCompletionCount,
        1);
    TestTrue(
        TEXT("Router destruction returns the shutdown receipt"),
        DestructionReceipt.Status ==
                game_agent::wire::ReceiptStatus::Unknown &&
            DestructionReceipt.ErrorCode.has_value() &&
            *DestructionReceipt.ErrorCode == "router_stopped");
    TestEqual(
        TEXT("Router destruction quiesces the host"),
        DestructionHost->GetStopAndDrainCount(),
        1);
    DestructionHost->EmitSucceeded();
    TestEqual(
        TEXT("Late callbacks after router destruction stay suppressed"),
        DestructionCompletionCount,
        1);
    DestructionDispatcher->Stop();

    auto UnbindDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto UnbindRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            UnbindDispatcher);
    auto UnbindHost =
        MakeShared<FDeferredCompletionHost, ESPMode::ThreadSafe>();
    auto ReplacementHost =
        MakeShared<FDeferredCompletionHost, ESPMode::ThreadSafe>();
    TestTrue(
        TEXT("The initial host is accepted before unbind"),
        UnbindRouter->BindHost(UnbindHost));
    TestFalse(
        TEXT("Replacing a live host is rejected"),
        UnbindRouter->BindHost(ReplacementHost));
    int32 UnbindCompletionCount = 0;
    game_agent::wire::ActionReceipt UnbindReceipt;
    const auto UnbindResult = UnbindRouter->DispatchActionJson(
        ActionJson,
        [&UnbindCompletionCount, &UnbindReceipt](
            game_agent::wire::ActionReceipt&& Receipt)
        {
            ++UnbindCompletionCount;
            UnbindReceipt = MoveTemp(Receipt);
        });
    TestTrue(
        TEXT("An action is accepted before unbind"),
        UnbindResult.WasAccepted());
    UnbindDispatcher->Drain(1);
    UnbindRouter->UnbindHost();
    TestEqual(
        TEXT("Unbind completes a started deferred action once"),
        UnbindCompletionCount,
        1);
    TestTrue(
        TEXT("Unbind uses the router shutdown receipt"),
        UnbindReceipt.Status == game_agent::wire::ReceiptStatus::Unknown &&
            UnbindReceipt.ErrorCode.has_value() &&
            *UnbindReceipt.ErrorCode == "router_stopped");
    TestEqual(
        TEXT("Unbind synchronously quiesces the bound host"),
        UnbindHost->GetStopAndDrainCount(),
        1);
    TestEqual(
        TEXT("A rejected replacement host never starts"),
        ReplacementHost->GetExecuteCount(),
        0);
    TestFalse(
        TEXT("A stopped router cannot bind another host"),
        UnbindRouter->BindHost(ReplacementHost));
    UnbindHost->EmitSucceeded();
    TestEqual(
        TEXT("Late callbacks from an unbound host stay suppressed"),
        UnbindCompletionCount,
        1);
    UnbindDispatcher->Stop();

    auto ReentrantDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    TSharedPtr<FGameAgentHostRouter, ESPMode::ThreadSafe> ReentrantRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            ReentrantDispatcher);
    auto ReentrantHost =
        MakeShared<FReentrantDrainHost, ESPMode::ThreadSafe>();
    ReentrantHost->SetRouter(ReentrantRouter);
    TestTrue(
        TEXT("The reentrant drain test host is bound"),
        ReentrantRouter->BindHost(ReentrantHost));
    int32 ReentrantCompletionCount = 0;
    const auto ReentrantDispatchResult =
        ReentrantRouter->DispatchActionJson(
            ActionJson,
            [&ReentrantCompletionCount](
                game_agent::wire::ActionReceipt&&)
            {
                ++ReentrantCompletionCount;
            });
    TestTrue(
        TEXT("The reentrant ExecuteAction test is accepted"),
        ReentrantDispatchResult.WasAccepted());
    ReentrantDispatcher->Drain(1);
    TestFalse(
        TEXT("Stop rejects reentry from ExecuteAction"),
        ReentrantHost->GetReentrantExecuteStopResult());
    TestEqual(
        TEXT("ExecuteAction still completes after rejected reentry"),
        ReentrantCompletionCount,
        1);
    TestTrue(
        TEXT("Outer shutdown completes when a host reenters Stop"),
        ReentrantRouter->Stop());
    TestFalse(
        TEXT("Stop rejects reentry from the host drain hook"),
        ReentrantHost->GetReentrantStopResult());
    TestEqual(
        TEXT("The host drain hook runs exactly once"),
        ReentrantHost->GetStopAndDrainCount(),
        1);
    ReentrantRouter->UnbindHost();
    ReentrantDispatcher->Stop();
    return true;
}

#endif
