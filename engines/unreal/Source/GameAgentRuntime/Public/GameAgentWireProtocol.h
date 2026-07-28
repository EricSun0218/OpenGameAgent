#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

#ifndef GAMEAGENTRUNTIME_API
#define GAMEAGENTRUNTIME_API
#endif

namespace game_agent::wire
{
struct JsonNumber final
{
    std::string Lexeme;
};

class JsonValue final
{
public:
    using Array = std::vector<JsonValue>;
    using Object = std::map<std::string, JsonValue, std::less<>>;
    using Storage = std::variant<std::nullptr_t, bool, JsonNumber, std::string, Array, Object>;

    JsonValue() noexcept;
    explicit JsonValue(std::nullptr_t) noexcept;
    explicit JsonValue(bool Value) noexcept;
    explicit JsonValue(JsonNumber Value);
    explicit JsonValue(std::string Value);
    explicit JsonValue(Array Value);
    explicit JsonValue(Object Value);

    [[nodiscard]] bool IsNull() const noexcept;
    [[nodiscard]] const bool* AsBool() const noexcept;
    [[nodiscard]] const JsonNumber* AsNumber() const noexcept;
    [[nodiscard]] const std::string* AsString() const noexcept;
    [[nodiscard]] const Array* AsArray() const noexcept;
    [[nodiscard]] const Object* AsObject() const noexcept;
    [[nodiscard]] const Storage& GetStorage() const noexcept;

private:
    Storage Value_;
};

struct ParseLimits final
{
    std::size_t MaxInputBytes = 1024U * 1024U;
    std::size_t MaxDepth = 64U;
    std::size_t MaxContainerEntries = 4096U;
    std::size_t MaxStringBytes = 256U * 1024U;
};

struct ParseError final
{
    std::string Message;
    std::size_t Offset = 0U;
    std::size_t Line = 1U;
    std::size_t Column = 1U;
};

template <typename TValue>
struct ParseResult final
{
    bool Ok = false;
    TValue Value{};
    ParseError Error{};

    [[nodiscard]] explicit operator bool() const noexcept
    {
        return Ok;
    }
};

struct ResourceReference final
{
    std::string Uri;
    std::string MediaType;
    std::optional<std::string> Digest;
    std::optional<std::int64_t> SizeBytes;
};

struct VisibilityRule final
{
    std::string Scope;
    std::vector<std::string> AudienceIds;
};

struct ObservationEnvelope final
{
    std::string ProtocolVersion;
    std::string SchemaVersion;
    std::string ObservationId;
    std::string WorldId;
    std::optional<std::string> SessionId;
    std::string Source;
    std::string Kind;
    std::vector<std::string> SubjectIds;
    std::string ContentType;
    std::optional<std::string> SchemaRef;
    std::optional<std::string> ContentSchemaVersion;
    std::optional<JsonValue> Payload;
    std::optional<ResourceReference> ResourceRef;
    std::string ObservedAt;
    std::optional<std::int64_t> TtlMs;
    std::optional<std::int64_t> Sequence;
    std::optional<std::string> StateVersion;
    std::string Trust;
    VisibilityRule Visibility;
    std::int64_t Priority = 0;
    std::optional<std::string> CacheKey;
    JsonValue::Object Extensions;
};

struct ActionRequest final
{
    std::string ProtocolVersion;
    std::string SchemaVersion;
    std::string OperationId;
    std::string RunId;
    std::string TurnId;
    std::string ToolCallId;
    std::string AgentId;
    std::string WorldId;
    std::string ActionName;
    std::string ActionVersion;
    JsonValue Arguments;
    std::optional<std::string> BasedOnStateVersion;
    std::vector<std::string> ExpectedEffects;
    std::optional<std::string> ReasonCode;
    std::string RequestedAt;
    std::optional<std::string> Deadline;
    JsonValue::Object Extensions;
};

enum class ReceiptStatus : std::uint8_t
{
    Succeeded,
    Rejected,
    Failed,
    Unknown
};

struct ActionReceipt final
{
    std::string ProtocolVersion;
    std::string SchemaVersion;
    std::string OperationId;
    std::int64_t Revision = 0;
    ReceiptStatus Status = ReceiptStatus::Unknown;
    std::optional<JsonValue> Result;
    std::optional<JsonValue> StateDiff;
    std::vector<ObservationEnvelope> AuthoritativeObservations;
    std::optional<std::string> ErrorCode;
    bool Retryable = false;
    std::optional<std::string> CommittedAt;
    std::string ReceivedAt;
    JsonValue::Object Extensions;
};

enum class EventDurability : std::uint8_t
{
    Durable,
    Ephemeral
};

struct RuntimeEvent final
{
    std::string ProtocolVersion;
    std::string SchemaVersion;
    std::string EventId;
    std::optional<std::string> RunId;
    std::optional<std::string> TurnId;
    std::int64_t Sequence = 0;
    std::string Kind;
    EventDurability Durability = EventDurability::Ephemeral;
    std::int64_t RuntimeGeneration = 1;
    std::optional<std::string> AttemptId;
    std::optional<std::string> StreamAttemptId;
    std::string Timestamp;
    JsonValue Payload;
    JsonValue::Object Extensions;
};

GAMEAGENTRUNTIME_API ParseResult<JsonValue> ParseJson(
    std::string_view Json,
    const ParseLimits& Limits = {});

GAMEAGENTRUNTIME_API ParseResult<ObservationEnvelope> ParseObservationEnvelope(
    std::string_view Json,
    const ParseLimits& Limits = {});

GAMEAGENTRUNTIME_API ParseResult<ActionRequest> ParseActionRequest(
    std::string_view Json,
    const ParseLimits& Limits = {});

GAMEAGENTRUNTIME_API ParseResult<ActionReceipt> ParseActionReceipt(
    std::string_view Json,
    const ParseLimits& Limits = {});

GAMEAGENTRUNTIME_API ParseResult<RuntimeEvent> ParseRuntimeEvent(
    std::string_view Json,
    const ParseLimits& Limits = {});

GAMEAGENTRUNTIME_API std::string SerializeJson(const JsonValue& Value);
GAMEAGENTRUNTIME_API std::string SerializeActionReceipt(const ActionReceipt& Receipt);
GAMEAGENTRUNTIME_API const char* ToWireString(ReceiptStatus Status) noexcept;
GAMEAGENTRUNTIME_API const char* ToWireString(EventDurability Durability) noexcept;
} // namespace game_agent::wire
