#if WITH_DEV_AUTOMATION_TESTS

#include "GameAgentHostBoundary.h"
#include "GameAgentMainThreadDispatcher.h"
#include "GameAgentWireProtocol.h"
#include "Misc/AutomationTest.h"

#include <array>
#include <utility>

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
    "decisionKey":"npc read decision",
    "batchId":"world-tick-1",
    "requestedAt":"2026-07-28T00:00:00Z"
})";

constexpr std::string_view ValidProviderLifecycleEventJson = R"({
    "protocolVersion":"0.2",
    "schemaVersion":"0.2",
    "eventId":"event-provider-1",
    "runId":"run-1",
    "turnId":"turn-1",
    "sequence":7,
    "kind":"provider.dispatch_started",
    "durability":"durable",
    "runtimeGeneration":1,
    "attemptId":"attempt-1",
    "streamAttemptId":"stream-1",
    "providerId":"provider-primary",
    "modelId":"model-gameplay-v1",
    "transportDialect":"chat-completions",
    "providerCapabilityDigest":"capability-digest-v1",
    "providerRouteDigest":"route-digest-v1",
    "reasonCode":"provider_dispatch",
    "timestamp":"2026-07-28T00:00:01Z",
    "payload":{"providerAttemptId":"attempt-1"},
    "extensions":{}
})";

constexpr std::string_view ValidResourceObservationJson = R"({
    "protocolVersion":"0.2",
    "schemaVersion":"0.2",
    "observationId":"observation-resource",
    "worldId":"world-1",
    "source":"game.resource",
    "kind":"resource_ref",
    "contentType":"application/octet-stream",
    "schemaRef":"",
    "resourceRef":{
        "uri":"game://state/actor-1",
        "mediaType":"application/octet-stream",
        "digest":"sha256:actor-1"
    },
    "observedAt":"2026-07-28T00:00:00Z",
    "trust":"trusted",
    "visibility":{"scope":"world","audienceIds":[]}
})";

constexpr std::string_view PatchWithoutStateVersionJson = R"({
    "protocolVersion":"0.2",
    "schemaVersion":"0.2",
    "observationId":"observation-patch",
    "worldId":"world-1",
    "source":"game.world",
    "kind":"patch",
    "contentType":"application/json",
    "payload":{"hungerDelta":-1},
    "observedAt":"2026-07-28T00:00:00Z",
    "trust":"authoritative",
    "visibility":{"scope":"world","audienceIds":[]}
})";

std::string RepeatUnicodeScalar(const std::size_t Count)
{
    std::string Result;
    Result.reserve(Count * 3U);
    for (std::size_t Index = 0U; Index < Count; ++Index)
    {
        Result += "\xE7\x95\x8C";
    }
    return Result;
}

std::string ActionReceiptWithObservations(const std::size_t Count)
{
    constexpr std::string_view Observation = R"({
        "protocolVersion":"0.2",
        "schemaVersion":"0.2",
        "observationId":"observation-shared",
        "worldId":"world-1",
        "source":"game.world",
        "kind":"event",
        "contentType":"application/json",
        "payload":{},
        "observedAt":"2026-07-28T00:00:00Z",
        "trust":"authoritative",
        "visibility":{"scope":"world","audienceIds":[]}
    })";
    std::string Json = R"({
        "protocolVersion":"0.2",
        "schemaVersion":"0.2",
        "operationId":"operation-observations",
        "revision":0,
        "status":"succeeded",
        "authoritativeObservations":[)";
    for (std::size_t Index = 0U; Index < Count; ++Index)
    {
        if (Index != 0U)
        {
            Json += ',';
        }
        Json += Observation;
    }
    Json += R"(],
        "retryable":false,
        "receivedAt":"2026-07-28T00:00:00Z"
    })";
    return Json;
}

std::string ActionReceiptWithObservation(
    const std::string_view Observation)
{
    std::string Json = R"({
        "protocolVersion":"0.2",
        "schemaVersion":"0.2",
        "operationId":"operation-nested-observation",
        "revision":1,
        "status":"succeeded",
        "authoritativeObservations":[)";
    Json += Observation;
    Json += R"(],
        "retryable":false,
        "receivedAt":"2026-07-28T00:00:01Z"
    })";
    return Json;
}

std::string ActionRequestWithExtensions(const std::size_t Count)
{
    std::string Json = R"({
        "protocolVersion":"0.2",
        "schemaVersion":"0.2",
        "extensions":{)";
    for (std::size_t Index = 0U; Index < Count; ++Index)
    {
        if (Index != 0U)
        {
            Json += ',';
        }
        Json += "\"extension_" + std::to_string(Index) + "\":true";
    }
    Json += R"(},
        "operationId":"operation-extensions",
        "runId":"run-extensions",
        "turnId":"turn-extensions",
        "toolCallId":"call-extensions",
        "agentId":"agent-extensions",
        "worldId":"world-extensions",
        "actionName":"read_state",
        "actionVersion":"1",
        "arguments":{},
        "requestedAt":"2026-07-28T00:00:00Z"
    })";
    return Json;
}

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

class FObservationCountCompletionHost final :
    public IGameAgentHostBoundary
{
public:
    explicit FObservationCountCompletionHost(
        const std::size_t ObservationCount)
        : ObservationCount_(ObservationCount)
    {
    }

    virtual void ExecuteAction(
        const game_agent::wire::ActionRequest& Request,
        FGameAgentActionCompletion&& Completion) override
    {
        game_agent::wire::ActionReceipt Receipt;
        Receipt.ProtocolVersion = "0.2";
        Receipt.SchemaVersion = "0.2";
        Receipt.OperationId = Request.OperationId;
        Receipt.Status = game_agent::wire::ReceiptStatus::Succeeded;
        Receipt.ReceivedAt = "2026-07-28T00:00:00Z";
        Receipt.AuthoritativeObservations.reserve(ObservationCount_);
        for (std::size_t Index = 0U;
             Index < ObservationCount_;
             ++Index)
        {
            game_agent::wire::ObservationEnvelope Observation;
            Observation.ProtocolVersion = "0.2";
            Observation.SchemaVersion = "0.2";
            Observation.ObservationId = "observation-shared";
            Observation.WorldId = "world-1";
            Observation.Source = "game.world";
            Observation.Kind = "event";
            Observation.ContentType = "application/json";
            Observation.Payload =
                game_agent::wire::JsonValue(
                    game_agent::wire::JsonValue::Object{});
            Observation.ObservedAt = "2026-07-28T00:00:00Z";
            Observation.Trust = "authoritative";
            Observation.Visibility.Scope = "world";
            Receipt.AuthoritativeObservations.emplace_back(
                std::move(Observation));
        }
        Completion(MoveTemp(Receipt));
    }

    virtual void StopAndDrainActions() override
    {
    }

private:
    std::size_t ObservationCount_;
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
    const auto ExtensionsAtLimit =
        game_agent::wire::ParseActionRequest(
            ActionRequestWithExtensions(
                game_agent::wire::MaxProtocolExtensions));
    TestTrue(
        TEXT("64 protocol extensions are accepted"),
        ExtensionsAtLimit.Ok &&
            ExtensionsAtLimit.Value.Extensions.size() ==
                game_agent::wire::MaxProtocolExtensions);
    const auto ExtensionsOverLimit =
        game_agent::wire::ParseActionRequest(
            ActionRequestWithExtensions(
                game_agent::wire::MaxProtocolExtensions + 1U));
    TestFalse(
        TEXT("65 protocol extensions are rejected before copying"),
        ExtensionsOverLimit.Ok);
    if (Parsed)
    {
        TestTrue(
            TEXT("Action name is preserved"),
            Parsed.Value.ActionName == "read_state");
        TestTrue(
            TEXT("Decision key is preserved"),
            Parsed.Value.DecisionKey.has_value() &&
                *Parsed.Value.DecisionKey == "npc read decision");
        TestTrue(
            TEXT("Batch id is preserved"),
            Parsed.Value.BatchId.has_value() &&
                *Parsed.Value.BatchId == "world-tick-1");
    }
    const auto EmptySchemaReference =
        game_agent::wire::ParseObservationEnvelope(
            ValidResourceObservationJson);
    TestTrue(
        TEXT("An empty optional schema URI reference parses"),
        EmptySchemaReference.Ok);
    if (EmptySchemaReference)
    {
        TestTrue(
            TEXT("Empty schema reference is preserved"),
            EmptySchemaReference.Value.SchemaRef.has_value() &&
                EmptySchemaReference.Value.SchemaRef->empty());
        TestTrue(
            TEXT("A non-empty resource digest is preserved"),
            EmptySchemaReference.Value.ResourceRef.has_value() &&
                EmptySchemaReference.Value.ResourceRef->Digest.has_value() &&
                *EmptySchemaReference.Value.ResourceRef->Digest ==
                    "sha256:actor-1");
    }
    TestTrue(
        TEXT("A valid nested resource observation parses"),
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservation(
                ValidResourceObservationJson))
            .Ok);

    constexpr std::string_view OriginalResourceUri =
        "game://state/actor-1";
    std::string AtResourceUriLimit(ValidResourceObservationJson);
    const auto ResourceUriOffset =
        AtResourceUriLimit.find(OriginalResourceUri);
    TestTrue(
        TEXT("The resource fixture contains its URI"),
        ResourceUriOffset != std::string::npos);
    if (ResourceUriOffset != std::string::npos)
    {
        const std::string BoundaryUri =
            "game:" + std::string(2043U, 'a');
        AtResourceUriLimit.replace(
            ResourceUriOffset,
            OriginalResourceUri.size(),
            BoundaryUri);
        TestTrue(
            TEXT("A 2048 Unicode scalar resource URI parses"),
            game_agent::wire::ParseObservationEnvelope(
                AtResourceUriLimit)
                .Ok);
        AtResourceUriLimit.replace(
            ResourceUriOffset,
            BoundaryUri.size(),
            BoundaryUri + "a");
        TestFalse(
            TEXT("A 2049 Unicode scalar resource URI is rejected"),
            game_agent::wire::ParseObservationEnvelope(
                AtResourceUriLimit)
                .Ok);
    }

    std::string MalformedResourceUri(ValidResourceObservationJson);
    const auto MalformedUriOffset =
        MalformedResourceUri.find(OriginalResourceUri);
    if (MalformedUriOffset != std::string::npos)
    {
        MalformedResourceUri.replace(
            MalformedUriOffset,
            OriginalResourceUri.size(),
            "game://state/actor with space");
    }
    TestFalse(
        TEXT("Malformed standalone resource URI references are rejected"),
        game_agent::wire::ParseObservationEnvelope(
            MalformedResourceUri)
            .Ok);
    TestFalse(
        TEXT("Malformed nested resource URI references are rejected"),
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservation(MalformedResourceUri))
            .Ok);

    std::string EmptyDigest(ValidResourceObservationJson);
    const auto DigestOffset = EmptyDigest.find("sha256:actor-1");
    if (DigestOffset != std::string::npos)
    {
        EmptyDigest.replace(
            DigestOffset,
            std::string_view("sha256:actor-1").size(),
            "");
    }
    TestFalse(
        TEXT("Empty standalone resource digests are rejected"),
        game_agent::wire::ParseObservationEnvelope(EmptyDigest).Ok);
    TestFalse(
        TEXT("Empty nested resource digests are rejected"),
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservation(EmptyDigest))
            .Ok);

    TestFalse(
        TEXT("Standalone patches require stateVersion"),
        game_agent::wire::ParseObservationEnvelope(
            PatchWithoutStateVersionJson)
            .Ok);
    TestFalse(
        TEXT("Nested patches require stateVersion"),
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservation(
                PatchWithoutStateVersionJson))
            .Ok);
    std::string PatchWithStateVersion(PatchWithoutStateVersionJson);
    const auto PatchTrustOffset =
        PatchWithStateVersion.find("\"trust\":\"authoritative\"");
    if (PatchTrustOffset != std::string::npos)
    {
        PatchWithStateVersion.insert(
            PatchTrustOffset,
            "\"stateVersion\":\"world-rev-2\",");
    }
    TestTrue(
        TEXT("Patches with stateVersion parse"),
        game_agent::wire::ParseObservationEnvelope(
            PatchWithStateVersion)
            .Ok);
    TestTrue(
        TEXT("Nested patches with stateVersion parse"),
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservation(PatchWithStateVersion))
            .Ok);

    const auto ProviderLifecycle =
        game_agent::wire::ParseRuntimeEvent(
            ValidProviderLifecycleEventJson);
    TestTrue(
        TEXT("A provider lifecycle runtime event parses"),
        ProviderLifecycle.Ok);
    if (ProviderLifecycle)
    {
        TestTrue(
            TEXT("Provider lifecycle metadata is preserved"),
            ProviderLifecycle.Value.ProviderId.has_value() &&
                *ProviderLifecycle.Value.ProviderId ==
                    "provider-primary" &&
                ProviderLifecycle.Value.ModelId.has_value() &&
                *ProviderLifecycle.Value.ModelId ==
                    "model-gameplay-v1" &&
                ProviderLifecycle.Value.TransportDialect.has_value() &&
                *ProviderLifecycle.Value.TransportDialect ==
                    "chat-completions" &&
                ProviderLifecycle.Value.ProviderCapabilityDigest
                    .has_value() &&
                *ProviderLifecycle.Value.ProviderCapabilityDigest ==
                    "capability-digest-v1" &&
                ProviderLifecycle.Value.ProviderRouteDigest.has_value() &&
                *ProviderLifecycle.Value.ProviderRouteDigest ==
                    "route-digest-v1" &&
                ProviderLifecycle.Value.ReasonCode.has_value() &&
                *ProviderLifecycle.Value.ReasonCode ==
                    "provider_dispatch");
    }

    const std::array<
        std::pair<std::string_view, std::size_t>,
        6U> ProviderFieldLimits{{
        {"provider-primary", 128U},
        {"model-gameplay-v1", 256U},
        {"chat-completions", 128U},
        {"capability-digest-v1", 256U},
        {"route-digest-v1", 256U},
        {"provider_dispatch", 96U}
    }};
    for (const auto& Limit : ProviderFieldLimits)
    {
        const std::string Boundary = RepeatUnicodeScalar(Limit.second);
        std::string AtLimit(ValidProviderLifecycleEventJson);
        const auto Offset = AtLimit.find(Limit.first);
        TestTrue(
            TEXT("The provider fixture contains its bounded field"),
            Offset != std::string::npos);
        if (Offset == std::string::npos)
        {
            continue;
        }

        AtLimit.replace(Offset, Limit.first.size(), Boundary);
        TestTrue(
            TEXT("Provider metadata accepts its Unicode scalar boundary"),
            game_agent::wire::ParseRuntimeEvent(AtLimit).Ok);
        AtLimit.replace(
            Offset,
            Boundary.size(),
            Boundary + "\xE7\x95\x8C");
        TestFalse(
            TEXT("Provider metadata rejects an extra Unicode scalar"),
            game_agent::wire::ParseRuntimeEvent(AtLimit).Ok);
    }

    const auto AtObservationLimit =
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservations(64U));
    TestTrue(
        TEXT("A receipt accepts 64 authoritative observations"),
        AtObservationLimit.Ok);
    TestTrue(
        TEXT("A receipt preserves 64 authoritative observations"),
        AtObservationLimit.Ok &&
            AtObservationLimit.Value.AuthoritativeObservations.size() ==
                64U);
    TestFalse(
        TEXT("A receipt rejects 65 authoritative observations"),
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservations(65U))
            .Ok);
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

    auto ReceiptLimitDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto ReceiptLimitRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            ReceiptLimitDispatcher);
    TestTrue(
        TEXT("The receipt limit test host is bound"),
        ReceiptLimitRouter->BindHost(
            MakeShared<
                FObservationCountCompletionHost,
                ESPMode::ThreadSafe>(
                game_agent::wire::MaxAuthoritativeObservationsPerReceipt)));
    game_agent::wire::ActionReceipt ReceiptAtLimit;
    const auto ReceiptLimitResult =
        ReceiptLimitRouter->DispatchActionJson(
            ActionJson,
            [&ReceiptAtLimit](
                game_agent::wire::ActionReceipt&& Receipt)
            {
                ReceiptAtLimit = MoveTemp(Receipt);
            });
    TestTrue(
        TEXT("A host receipt at the observation limit is accepted"),
        ReceiptLimitResult.WasAccepted());
    ReceiptLimitDispatcher->Drain(1);
    TestTrue(
        TEXT("A host receipt preserves 64 observations"),
        ReceiptAtLimit.Status ==
                game_agent::wire::ReceiptStatus::Succeeded &&
            ReceiptAtLimit.AuthoritativeObservations.size() ==
                game_agent::wire::MaxAuthoritativeObservationsPerReceipt);
    ReceiptLimitRouter->UnbindHost();
    ReceiptLimitDispatcher->Stop();

    auto ReceiptOverflowDispatcher =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    auto ReceiptOverflowRouter =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(
            ReceiptOverflowDispatcher);
    TestTrue(
        TEXT("The receipt overflow test host is bound"),
        ReceiptOverflowRouter->BindHost(
            MakeShared<
                FObservationCountCompletionHost,
                ESPMode::ThreadSafe>(
                game_agent::wire::MaxAuthoritativeObservationsPerReceipt +
                1U)));
    game_agent::wire::ActionReceipt ReceiptOverLimit;
    const auto ReceiptOverflowResult =
        ReceiptOverflowRouter->DispatchActionJson(
            ActionJson,
            [&ReceiptOverLimit](
                game_agent::wire::ActionReceipt&& Receipt)
            {
                ReceiptOverLimit = MoveTemp(Receipt);
            });
    TestTrue(
        TEXT("A host receipt over the observation limit reaches validation"),
        ReceiptOverflowResult.WasAccepted());
    ReceiptOverflowDispatcher->Drain(1);
    TestTrue(
        TEXT("A host receipt over the limit fails before serialization"),
        ReceiptOverLimit.Status ==
                game_agent::wire::ReceiptStatus::Unknown &&
            ReceiptOverLimit.ErrorCode.has_value() &&
            *ReceiptOverLimit.ErrorCode == "receipt_invalid" &&
            ReceiptOverLimit.AuthoritativeObservations.empty());
    ReceiptOverflowRouter->UnbindHost();
    ReceiptOverflowDispatcher->Stop();

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
