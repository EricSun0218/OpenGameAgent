#include "GameAgentRuntimeAbi.h"
#include "GameAgentWireProtocol.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <type_traits>

extern "C" int game_agent_abi_c11_smoke(void);

namespace
{
int Failures = 0;
int RuntimeToken = 0;
int EventCount = 0;

void Expect(const bool Condition, const char* Message)
{
    if (!Condition)
    {
        ++Failures;
        std::cerr << "FAIL: " << Message << '\n';
    }
}

std::string ReadAll(const std::filesystem::path& Path)
{
    std::ifstream Stream(Path, std::ios::binary);
    if (!Stream)
    {
        return {};
    }
    std::ostringstream Buffer;
    Buffer << Stream.rdbuf();
    return Buffer.str();
}

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

void CheckRuntimeEventFieldLimit(
    const std::string& Fixture,
    const std::string& Original,
    const std::size_t Maximum)
{
    const auto Offset = Fixture.find(Original);
    Expect(
        Offset != std::string::npos,
        "provider lifecycle fixture should contain its bounded field");
    if (Offset == std::string::npos)
    {
        return;
    }

    const std::string Boundary = RepeatUnicodeScalar(Maximum);
    std::string AtLimit = Fixture;
    AtLimit.replace(Offset, Original.size(), Boundary);
    Expect(
        game_agent::wire::ParseRuntimeEvent(AtLimit).Ok,
        "runtime event field should accept its Unicode scalar boundary");

    std::string OverLimit = AtLimit;
    OverLimit.replace(
        Offset,
        Boundary.size(),
        Boundary + "\xE7\x95\x8C");
    Expect(
        !game_agent::wire::ParseRuntimeEvent(OverLimit).Ok,
        "runtime event field should reject one Unicode scalar over its boundary");
}

int32_t GAR_CALL FakeCreate(
    const GAR_RuntimeConfigV1* Config,
    const GAR_CallbacksV1*,
    GAR_RuntimeHandle* OutRuntime)
{
    if (Config == nullptr ||
        Config->StructSize != sizeof(GAR_RuntimeConfigV1) ||
        OutRuntime == nullptr)
    {
        return -1;
    }
    *OutRuntime = &RuntimeToken;
    return 0;
}

void GAR_CALL FakeDestroy(GAR_RuntimeHandle)
{
}

int32_t GAR_CALL FakeSubmit(
    GAR_RuntimeHandle Runtime,
    const uint64_t,
    const GAR_ByteSpan Json)
{
    if (Runtime != &RuntimeToken || Json.Data == nullptr || Json.Size == 0U)
    {
        return -1;
    }
    return 0;
}

int32_t GAR_CALL FakePoll(GAR_RuntimeHandle Runtime, const uint32_t MaxEvents)
{
    if (Runtime != &RuntimeToken)
    {
        return -1;
    }
    EventCount += static_cast<int>(MaxEvents);
    return 0;
}

int32_t GAR_CALL FakeGetApi(
    const uint32_t RequestedAbiVersion,
    GAR_RuntimeApiV1* OutApi)
{
    if (RequestedAbiVersion != GAR_ABI_VERSION_1 || OutApi == nullptr)
    {
        return GAR_RESULT_UNSUPPORTED_ABI;
    }
    OutApi->AbiVersion = GAR_ABI_VERSION_1;
    return GAR_RESULT_OK;
}

void CheckAbi()
{
    static_assert(std::is_standard_layout<GAR_ByteSpan>::value, "wire spans must be standard layout");
    static_assert(std::is_standard_layout<GAR_RuntimeConfigV1>::value, "config must be standard layout");
    static_assert(std::is_standard_layout<GAR_CallbacksV1>::value, "callbacks must be standard layout");
    static_assert(std::is_standard_layout<GAR_RuntimeApiV1>::value, "API table must be standard layout");
    static_assert(offsetof(GAR_RuntimeApiV1, AbiVersion) == 0U, "ABI version must be the first field");
    static_assert(offsetof(GAR_RuntimeApiV1, StructSize) == sizeof(uint32_t), "struct size must be second");
    Expect(game_agent_abi_c11_smoke() == 0, "ABI header should compile and initialize as C11");

    GAR_RuntimeApiV1 Api{};
    Api.AbiVersion = GAR_ABI_VERSION_1;
    Api.StructSize = static_cast<uint32_t>(sizeof(Api));
    Api.Create = &FakeCreate;
    Api.Destroy = &FakeDestroy;
    Api.SubmitObservation = &FakeSubmit;
    Api.SubmitActionReceipt = &FakeSubmit;
    Api.SendControl = &FakeSubmit;
    Api.Poll = &FakePoll;
    GAR_GetRuntimeApiV1Fn GetApi = &FakeGetApi;
    Expect(
        GetApi(GAR_ABI_VERSION_1, &Api) == GAR_RESULT_OK,
        "ABI API-table negotiation should be callable");

    GAR_RuntimeConfigV1 Config{};
    Config.StructSize = static_cast<uint32_t>(sizeof(Config));
    GAR_RuntimeHandle Runtime = nullptr;
    Expect(Api.Create(&Config, nullptr, &Runtime) == 0, "ABI create function should be callable");

    constexpr std::uint8_t Json[] = {'{', '}'};
    const GAR_ByteSpan Span{Json, sizeof(Json)};
    Expect(Api.SubmitObservation(Runtime, 1U, Span) == 0, "ABI submit function should be callable");
    Expect(Api.Poll(Runtime, 3U) == 0 && EventCount == 3, "ABI poll function should be callable");
    Api.Destroy(Runtime);
}

void CheckActionRequest(const std::filesystem::path& FixtureRoot)
{
    const std::string Json = ReadAll(FixtureRoot / "action-request.json");
    Expect(!Json.empty(), "action request fixture should be readable");
    const auto Result = game_agent::wire::ParseActionRequest(Json);
    Expect(Result.Ok, "action request fixture should parse");
    if (!Result)
    {
        std::cerr << Result.Error.Message << '\n';
        return;
    }

    Expect(Result.Value.OperationId == "operation-0001", "operation id should be preserved");
    Expect(Result.Value.ActionName == "gather_food", "action name should be preserved");
    Expect(
        Result.Value.DecisionKey.has_value() &&
            *Result.Value.DecisionKey == "npc gather decision",
        "decision key should be preserved");
    Expect(
        Result.Value.BatchId.has_value() &&
            *Result.Value.BatchId == "world-tick-42",
        "batch id should be preserved");
    const auto* Arguments = Result.Value.Arguments.AsObject();
    Expect(Arguments != nullptr, "action arguments should remain structured JSON");
    if (Arguments != nullptr)
    {
        const auto Resource = Arguments->find("resource");
        Expect(Resource != Arguments->end(), "action arguments should contain resource");
        Expect(
            Resource != Arguments->end() &&
                Resource->second.AsString() != nullptr &&
                *Resource->second.AsString() == "berries",
            "action argument value should be preserved");
    }

    const std::string OriginalDecision = "npc gather decision";
    const auto DecisionOffset = Json.find(OriginalDecision);
    Expect(
        DecisionOffset != std::string::npos,
        "action fixture should contain its decision key");
    if (DecisionOffset != std::string::npos)
    {
        std::string Boundary;
        for (std::size_t Index = 0U; Index < 256U; ++Index)
        {
            Boundary += "\xE7\x95\x8C";
        }

        std::string UnicodeJson = Json;
        UnicodeJson.replace(
            DecisionOffset,
            OriginalDecision.size(),
            Boundary);
        Expect(
            game_agent::wire::ParseActionRequest(UnicodeJson).Ok,
            "256 Unicode scalar decision keys should parse");

        UnicodeJson.replace(
            DecisionOffset,
            Boundary.size(),
            Boundary + "\xE7\x95\x8C");
        Expect(
            !game_agent::wire::ParseActionRequest(UnicodeJson).Ok,
            "257 Unicode scalar decision keys should fail");
    }
}

void CheckActionReceipt(const std::filesystem::path& FixtureRoot)
{
    const std::string Json = ReadAll(FixtureRoot / "action-receipt.json");
    Expect(!Json.empty(), "action receipt fixture should be readable");
    const auto Result = game_agent::wire::ParseActionReceipt(Json);
    Expect(Result.Ok, "action receipt fixture should parse");
    if (!Result)
    {
        std::cerr << Result.Error.Message << '\n';
        return;
    }

    Expect(Result.Value.Revision == 1, "receipt revision should be preserved");
    Expect(
        Result.Value.Status == game_agent::wire::ReceiptStatus::Succeeded,
        "receipt status should be preserved");

    const std::string Serialized = game_agent::wire::SerializeActionReceipt(Result.Value);
    const auto RoundTrip = game_agent::wire::ParseActionReceipt(Serialized);
    Expect(RoundTrip.Ok, "serialized action receipt should parse");
    Expect(
        RoundTrip.Ok && RoundTrip.Value.OperationId == Result.Value.OperationId,
        "serialized action receipt should preserve operation id");

    const auto AtObservationLimit =
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservations(64U));
    Expect(
        AtObservationLimit.Ok,
        "64 authoritative observations should parse");
    Expect(
        AtObservationLimit.Ok &&
            AtObservationLimit.Value.AuthoritativeObservations.size() == 64U,
        "all 64 authoritative observations should be preserved");
    Expect(
        !game_agent::wire::ParseActionReceipt(
             ActionReceiptWithObservations(65U))
             .Ok,
        "65 authoritative observations should fail before item decoding");
}

void CheckObservation(const std::filesystem::path& FixtureRoot)
{
    const std::string Json = ReadAll(FixtureRoot / "observation.json");
    Expect(!Json.empty(), "observation fixture should be readable");
    const auto Result = game_agent::wire::ParseObservationEnvelope(Json);
    Expect(Result.Ok, "observation fixture should parse");
    if (!Result)
    {
        std::cerr << Result.Error.Message << '\n';
        return;
    }

    Expect(Result.Value.Kind == "snapshot", "observation kind should be preserved");
    Expect(Result.Value.Payload.has_value(), "inline observation payload should be preserved");
    Expect(!Result.Value.ResourceRef.has_value(), "inline observation should not create a resource reference");
    Expect(Result.Value.Visibility.Scope == "agent", "visibility scope should be preserved");
}

void CheckObservationSemanticContracts(
    const std::filesystem::path& FixtureRoot,
    const std::filesystem::path& InvalidFixtureRoot)
{
    const std::string ResourceJson =
        ReadAll(FixtureRoot / "resource-observation.json");
    Expect(
        !ResourceJson.empty(),
        "resource observation fixture should be readable");
    const auto Resource =
        game_agent::wire::ParseObservationEnvelope(ResourceJson);
    Expect(Resource.Ok, "valid resource observation fixture should parse");
    Expect(
        Resource.Ok &&
            Resource.Value.ResourceRef.has_value() &&
            Resource.Value.ResourceRef->Digest.has_value(),
        "resource observation fields should be preserved");
    Expect(
        game_agent::wire::ParseActionReceipt(
            ActionReceiptWithObservation(ResourceJson))
            .Ok,
        "valid nested resource observations should parse");

    constexpr std::string_view OriginalUri =
        "game://state/actors/agent-demo";
    const auto UriOffset = ResourceJson.find(OriginalUri);
    Expect(
        UriOffset != std::string::npos,
        "resource fixture should contain its URI");
    if (UriOffset != std::string::npos)
    {
        const std::string AtLimitUri =
            "game:" + std::string(2043U, 'a');
        std::string AtLimit = ResourceJson;
        AtLimit.replace(
            UriOffset,
            OriginalUri.size(),
            AtLimitUri);
        Expect(
            game_agent::wire::ParseObservationEnvelope(AtLimit).Ok,
            "2048 Unicode scalar resource URIs should parse");

        std::string OverLimit = AtLimit;
        OverLimit.replace(
            UriOffset,
            AtLimitUri.size(),
            AtLimitUri + "a");
        Expect(
            !game_agent::wire::ParseObservationEnvelope(OverLimit).Ok,
            "2049 Unicode scalar resource URIs should fail");
    }

    const std::string MalformedUri =
        ReadAll(
            InvalidFixtureRoot /
            "observation-resource-malformed-uri.json");
    Expect(
        !game_agent::wire::ParseObservationEnvelope(MalformedUri).Ok,
        "malformed resource URI references should fail");
    Expect(
        !game_agent::wire::ParseActionReceipt(
             ActionReceiptWithObservation(MalformedUri))
             .Ok,
        "nested malformed resource URI references should fail");

    const std::string EmptyDigest =
        ReadAll(
            InvalidFixtureRoot /
            "observation-resource-empty-digest.json");
    Expect(
        !game_agent::wire::ParseObservationEnvelope(EmptyDigest).Ok,
        "present empty resource digests should fail");
    Expect(
        !game_agent::wire::ParseActionReceipt(
             ActionReceiptWithObservation(EmptyDigest))
             .Ok,
        "nested empty resource digests should fail");

    const std::string PatchWithoutStateVersion =
        ReadAll(
            InvalidFixtureRoot /
            "observation-patch-missing-state-version.json");
    std::string PatchWithStateVersion = PatchWithoutStateVersion;
    constexpr std::string_view TrustField =
        "\"trust\": \"authoritative\"";
    const auto TrustOffset = PatchWithStateVersion.find(TrustField);
    Expect(
        TrustOffset != std::string::npos,
        "patch fixture should contain its trust field");
    if (TrustOffset != std::string::npos)
    {
        PatchWithStateVersion.insert(
            TrustOffset,
            "\"stateVersion\": \"world-rev-43\",\n  ");
        Expect(
            game_agent::wire::ParseObservationEnvelope(
                PatchWithStateVersion)
                .Ok,
            "patch observations with stateVersion should parse");
        Expect(
            game_agent::wire::ParseActionReceipt(
                ActionReceiptWithObservation(PatchWithStateVersion))
                .Ok,
            "nested patch observations with stateVersion should parse");
    }
    Expect(
        !game_agent::wire::ParseObservationEnvelope(
             PatchWithoutStateVersion)
             .Ok,
        "patch observations without stateVersion should fail");
    Expect(
        !game_agent::wire::ParseActionReceipt(
             ActionReceiptWithObservation(PatchWithoutStateVersion))
             .Ok,
        "nested patch observations without stateVersion should fail");

    const std::string NestedPatchWithoutStateVersion =
        ReadAll(
            InvalidFixtureRoot /
            "receipt-patch-missing-state-version.json");
    Expect(
        !game_agent::wire::ParseActionReceipt(
             NestedPatchWithoutStateVersion)
             .Ok,
        "nested patch fixture without stateVersion should fail");
}

void CheckRuntimeEvent(const std::filesystem::path& Fixture)
{
    const std::string Json = ReadAll(Fixture);
    Expect(!Json.empty(), "runtime event fixture should be readable");
    const auto Result = game_agent::wire::ParseRuntimeEvent(Json);
    Expect(Result.Ok, "runtime event fixture should parse");
    if (!Result)
    {
        std::cerr << Result.Error.Message << '\n';
        return;
    }
    Expect(Result.Value.Sequence == 7, "runtime event sequence should be preserved");
    Expect(
        Result.Value.Durability == game_agent::wire::EventDurability::Durable,
        "runtime event durability should be preserved");
    Expect(
        Result.Value.ProviderId.has_value() &&
            *Result.Value.ProviderId == "provider-primary",
        "runtime event provider id should be preserved");
    Expect(
        Result.Value.ModelId.has_value() &&
            *Result.Value.ModelId == "model-gameplay-v1",
        "runtime event model id should be preserved");
    Expect(
        Result.Value.TransportDialect.has_value() &&
            *Result.Value.TransportDialect == "chat-completions",
        "runtime event transport dialect should be preserved");
    Expect(
        Result.Value.ProviderCapabilityDigest.has_value() &&
            *Result.Value.ProviderCapabilityDigest == "capability-digest-v1",
        "runtime event provider capability digest should be preserved");
    Expect(
        Result.Value.ProviderRouteDigest.has_value() &&
            *Result.Value.ProviderRouteDigest == "route-digest-v1",
        "runtime event provider route digest should be preserved");
    Expect(
        Result.Value.ReasonCode.has_value() &&
            *Result.Value.ReasonCode == "provider_dispatch",
        "runtime event reason code should be preserved");

    CheckRuntimeEventFieldLimit(Json, "provider-primary", 128U);
    CheckRuntimeEventFieldLimit(Json, "model-gameplay-v1", 256U);
    CheckRuntimeEventFieldLimit(Json, "chat-completions", 128U);
    CheckRuntimeEventFieldLimit(Json, "capability-digest-v1", 256U);
    CheckRuntimeEventFieldLimit(Json, "route-digest-v1", 256U);
    CheckRuntimeEventFieldLimit(Json, "provider_dispatch", 96U);

    std::string EmptyProviderId = Json;
    const auto ProviderOffset = EmptyProviderId.find("provider-primary");
    if (ProviderOffset != std::string::npos)
    {
        EmptyProviderId.replace(
            ProviderOffset,
            std::string("provider-primary").size(),
            "");
        Expect(
            !game_agent::wire::ParseRuntimeEvent(EmptyProviderId).Ok,
            "present provider lifecycle fields must be non-empty");
    }
}

void CheckParserGuards()
{
    const auto Duplicate = game_agent::wire::ParseJson(R"({"id":1,"id":2})");
    Expect(!Duplicate, "duplicate object keys should be rejected");

    const auto Truncated = game_agent::wire::ParseJson(R"({"id":[1,2})");
    Expect(!Truncated, "truncated JSON should be rejected");

    const auto InvalidUtf8 =
        game_agent::wire::ParseJson(std::string("{\"value\":\"\xc0\x80\"}"));
    Expect(!InvalidUtf8, "overlong UTF-8 should be rejected");

    game_agent::wire::ParseLimits Limits;
    Limits.MaxInputBytes = 4U;
    const auto Oversized = game_agent::wire::ParseJson(R"({"value":1})", Limits);
    Expect(!Oversized, "configured input byte limits should be enforced");

    const auto ExtensionsAtLimit =
        game_agent::wire::ParseActionRequest(
            ActionRequestWithExtensions(
                game_agent::wire::MaxProtocolExtensions));
    Expect(
        ExtensionsAtLimit.Ok &&
            ExtensionsAtLimit.Value.Extensions.size() ==
                game_agent::wire::MaxProtocolExtensions,
        "64 protocol extensions should be accepted");

    const auto ExtensionsOverLimit =
        game_agent::wire::ParseActionRequest(
            ActionRequestWithExtensions(
                game_agent::wire::MaxProtocolExtensions + 1U));
    Expect(
        !ExtensionsOverLimit,
        "65 protocol extensions should be rejected before copying");

    const auto UnknownField = game_agent::wire::ParseActionRequest(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "operationId":"op",
            "runId":"run",
            "turnId":"turn",
            "toolCallId":"call",
            "agentId":"agent",
            "worldId":"world",
            "actionName":"read_state",
            "actionVersion":"1",
            "arguments":{},
            "requestedAt":"2026-07-28T00:00:00Z",
            "unexpected":true
        })");
    Expect(!UnknownField, "unknown top-level protocol fields should be rejected");

    const auto InvalidActionName = game_agent::wire::ParseActionRequest(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "operationId":"op",
            "runId":"run",
            "turnId":"turn",
            "toolCallId":"call",
            "agentId":"agent",
            "worldId":"world",
            "actionName":"Read State",
            "actionVersion":"1",
            "arguments":{},
            "requestedAt":"2026-07-28T00:00:00Z"
        })");
    Expect(!InvalidActionName, "invalid protocol action names should be rejected");

    const auto DuplicateAudience = game_agent::wire::ParseObservationEnvelope(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "observationId":"observation-1",
            "worldId":"world-1",
            "source":"game.world",
            "kind":"event",
            "contentType":"application/json",
            "payload":{},
            "observedAt":"2026-07-28T00:00:00Z",
            "trust":"trusted",
            "visibility":{
                "scope":"agent",
                "audienceIds":["agent-1","agent-1"]
            }
        })");
    Expect(!DuplicateAudience, "duplicate visibility audience ids should be rejected");

    const auto EmptySchemaReference =
        game_agent::wire::ParseObservationEnvelope(
            R"({
                "protocolVersion":"0.2",
                "schemaVersion":"0.2",
                "observationId":"observation-resource",
                "worldId":"world-1",
                "source":"game.world",
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
            })");
    Expect(
        EmptySchemaReference.Ok,
        "an empty optional schema URI reference should parse");
    if (!EmptySchemaReference)
    {
        std::cerr << EmptySchemaReference.Error.Message << '\n';
    }
    Expect(
        EmptySchemaReference.Ok &&
            EmptySchemaReference.Value.SchemaRef.has_value() &&
            EmptySchemaReference.Value.SchemaRef->empty() &&
            EmptySchemaReference.Value.ResourceRef.has_value() &&
            EmptySchemaReference.Value.ResourceRef->Digest.has_value() &&
            *EmptySchemaReference.Value.ResourceRef->Digest ==
                "sha256:actor-1",
        "empty schema references and non-empty digests should be preserved");

    const auto OverflowingRevision = game_agent::wire::ParseActionReceipt(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "operationId":"operation-1",
            "revision":9223372036854775808,
            "status":"unknown",
            "retryable":false,
            "receivedAt":"2026-07-28T00:00:00Z"
        })");
    Expect(!OverflowingRevision, "integers outside int64 should be rejected");

    const auto InvalidRequestedAt = game_agent::wire::ParseActionRequest(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "operationId":"op",
            "runId":"run",
            "turnId":"turn",
            "toolCallId":"call",
            "agentId":"agent",
            "worldId":"world",
            "actionName":"read_state",
            "actionVersion":"1",
            "arguments":{},
            "requestedAt":"2026-02-30T00:00:00Z"
        })");
    Expect(!InvalidRequestedAt, "invalid request date-times should be rejected");

    const auto ValidOffsetAndFraction =
        game_agent::wire::ParseActionRequest(
            R"({
                "protocolVersion":"0.2",
                "schemaVersion":"0.2",
                "operationId":"op",
                "runId":"run",
                "turnId":"turn",
                "toolCallId":"call",
                "agentId":"agent",
                "worldId":"world",
                "actionName":"read_state",
                "actionVersion":"1",
                "arguments":{},
                "requestedAt":"2024-02-29T23:59:59.1234567+14:00",
                "deadline":"2024-03-01T09:59:59.1234567-14:00"
            })");
    Expect(
        ValidOffsetAndFraction.Ok,
        "supported date-time offsets and fractional seconds should parse");

    const auto InvalidYearZero =
        game_agent::wire::ParseActionRequest(
            R"({
                "protocolVersion":"0.2",
                "schemaVersion":"0.2",
                "operationId":"op",
                "runId":"run",
                "turnId":"turn",
                "toolCallId":"call",
                "agentId":"agent",
                "worldId":"world",
                "actionName":"read_state",
                "actionVersion":"1",
                "arguments":{},
                "requestedAt":"0000-01-01T00:00:00Z"
            })");
    Expect(!InvalidYearZero, "year zero should be rejected");

    const auto UnsupportedLeapSecond =
        game_agent::wire::ParseActionRequest(
            R"({
                "protocolVersion":"0.2",
                "schemaVersion":"0.2",
                "operationId":"op",
                "runId":"run",
                "turnId":"turn",
                "toolCallId":"call",
                "agentId":"agent",
                "worldId":"world",
                "actionName":"read_state",
                "actionVersion":"1",
                "arguments":{},
                "requestedAt":"2026-06-30T23:59:60Z"
            })");
    Expect(
        !UnsupportedLeapSecond,
        "unsupported leap-second date-times should be rejected");

    const auto InvalidObservedAt =
        game_agent::wire::ParseObservationEnvelope(
            R"({
                "protocolVersion":"0.2",
                "schemaVersion":"0.2",
                "observationId":"observation-1",
                "worldId":"world-1",
                "source":"game.world",
                "kind":"event",
                "contentType":"application/json",
                "payload":{},
                "observedAt":"2026-07-28 00:00:00Z",
                "trust":"trusted",
                "visibility":{"scope":"world","audienceIds":[]}
            })");
    Expect(!InvalidObservedAt, "invalid observation date-times should be rejected");

    const auto InvalidReceivedAt = game_agent::wire::ParseActionReceipt(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "operationId":"operation-1",
            "revision":0,
            "status":"unknown",
            "retryable":false,
            "receivedAt":"2026-07-28T00:00:00"
        })");
    Expect(!InvalidReceivedAt, "receipt date-times must include a time zone");

    const auto InvalidEventTimestamp = game_agent::wire::ParseRuntimeEvent(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "eventId":"event-1",
            "sequence":0,
            "kind":"test",
            "durability":"durable",
            "runtimeGeneration":1,
            "timestamp":"2026-07-28T00:00:00+24:00",
            "payload":{}
        })");
    Expect(!InvalidEventTimestamp, "invalid event date-times should be rejected");

    const auto UnsupportedOffset = game_agent::wire::ParseRuntimeEvent(
        R"({
            "protocolVersion":"0.2",
            "schemaVersion":"0.2",
            "eventId":"event-1",
            "sequence":0,
            "kind":"test",
            "durability":"durable",
            "runtimeGeneration":1,
            "timestamp":"2026-07-28T00:00:00+14:01",
            "payload":{}
        })");
    Expect(
        !UnsupportedOffset,
        "date-time offsets outside the runtime profile should be rejected");

    const auto Unicode = game_agent::wire::ParseJson(R"({"value":"\ud83c\udfae"})");
    Expect(Unicode.Ok, "valid Unicode surrogate pairs should parse");
    if (Unicode)
    {
        const std::string Serialized = game_agent::wire::SerializeJson(Unicode.Value);
        Expect(!Serialized.empty(), "parsed Unicode JSON should serialize");
        Expect(game_agent::wire::ParseJson(Serialized).Ok, "serialized Unicode JSON should parse");
    }
}
} // namespace

int main(int ArgumentCount, char** Arguments)
{
    if (ArgumentCount != 4)
    {
        std::cerr
            << "Usage: GameAgentUnrealPortableSmoke "
               "<protocol-fixture-dir> <invalid-fixture-dir> "
               "<runtime-event-fixture>\n";
        return 2;
    }

    CheckAbi();
    CheckActionRequest(Arguments[1]);
    CheckActionReceipt(Arguments[1]);
    CheckObservation(Arguments[1]);
    CheckObservationSemanticContracts(Arguments[1], Arguments[2]);
    CheckRuntimeEvent(Arguments[3]);
    CheckParserGuards();

    if (Failures != 0)
    {
        std::cerr << Failures << " portable Unreal smoke assertion(s) failed.\n";
        return 1;
    }
    std::cout << "Portable Unreal wire and ABI smoke passed.\n";
    return 0;
}
