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
    if (ArgumentCount != 3)
    {
        std::cerr << "Usage: GameAgentUnrealPortableSmoke <protocol-fixture-dir> <runtime-event-fixture>\n";
        return 2;
    }

    CheckAbi();
    CheckActionRequest(Arguments[1]);
    CheckActionReceipt(Arguments[1]);
    CheckObservation(Arguments[1]);
    CheckRuntimeEvent(Arguments[2]);
    CheckParserGuards();

    if (Failures != 0)
    {
        std::cerr << Failures << " portable Unreal smoke assertion(s) failed.\n";
        return 1;
    }
    std::cout << "Portable Unreal wire and ABI smoke passed.\n";
    return 0;
}
