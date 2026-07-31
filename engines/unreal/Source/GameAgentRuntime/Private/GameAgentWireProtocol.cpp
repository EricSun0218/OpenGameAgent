#include "GameAgentWireProtocol.h"

#include <charconv>
#include <initializer_list>
#include <limits>
#include <set>
#include <system_error>
#include <utility>

namespace game_agent::wire
{
JsonValue::JsonValue() noexcept : Value_(nullptr)
{
}

JsonValue::JsonValue(std::nullptr_t) noexcept : Value_(nullptr)
{
}

JsonValue::JsonValue(const bool Value) noexcept : Value_(Value)
{
}

JsonValue::JsonValue(JsonNumber Value) : Value_(std::move(Value))
{
}

JsonValue::JsonValue(std::string Value) : Value_(std::move(Value))
{
}

JsonValue::JsonValue(Array Value) : Value_(std::move(Value))
{
}

JsonValue::JsonValue(Object Value) : Value_(std::move(Value))
{
}

bool JsonValue::IsNull() const noexcept
{
    return std::holds_alternative<std::nullptr_t>(Value_);
}

const bool* JsonValue::AsBool() const noexcept
{
    return std::get_if<bool>(&Value_);
}

const JsonNumber* JsonValue::AsNumber() const noexcept
{
    return std::get_if<JsonNumber>(&Value_);
}

const std::string* JsonValue::AsString() const noexcept
{
    return std::get_if<std::string>(&Value_);
}

const JsonValue::Array* JsonValue::AsArray() const noexcept
{
    return std::get_if<Array>(&Value_);
}

const JsonValue::Object* JsonValue::AsObject() const noexcept
{
    return std::get_if<Object>(&Value_);
}

const JsonValue::Storage& JsonValue::GetStorage() const noexcept
{
    return Value_;
}

namespace
{
constexpr std::string_view ProtocolVersion = "0.2";
constexpr std::string_view SchemaVersion = "0.2";

class JsonParser final
{
public:
    JsonParser(const std::string_view Input, const ParseLimits& Limits)
        : Input_(Input), Limits_(Limits)
    {
    }

    ParseResult<JsonValue> Run()
    {
        ParseResult<JsonValue> Result;
        if (Input_.size() > Limits_.MaxInputBytes)
        {
            Fail("JSON input exceeds the configured byte limit");
            Result.Error = Error_;
            return Result;
        }

        SkipWhitespace();
        if (!ParseValue(1U, Result.Value))
        {
            Result.Error = Error_;
            return Result;
        }

        SkipWhitespace();
        if (Position_ != Input_.size())
        {
            Fail("unexpected data after the JSON value");
            Result.Error = Error_;
            return Result;
        }

        Result.Ok = true;
        return Result;
    }

private:
    bool ParseValue(const std::size_t Depth, JsonValue& Out)
    {
        if (Depth > Limits_.MaxDepth)
        {
            return Fail("JSON nesting exceeds the configured depth limit");
        }

        if (Position_ >= Input_.size())
        {
            return Fail("unexpected end of JSON input");
        }

        const char Token = Input_[Position_];
        if (Token == '{')
        {
            JsonValue::Object Object;
            if (!ParseObject(Depth, Object))
            {
                return false;
            }
            Out = JsonValue(std::move(Object));
            return true;
        }

        if (Token == '[')
        {
            JsonValue::Array Array;
            if (!ParseArray(Depth, Array))
            {
                return false;
            }
            Out = JsonValue(std::move(Array));
            return true;
        }

        if (Token == '"')
        {
            std::string String;
            if (!ParseString(String))
            {
                return false;
            }
            Out = JsonValue(std::move(String));
            return true;
        }

        if (Token == 't')
        {
            if (!ConsumeLiteral("true"))
            {
                return false;
            }
            Out = JsonValue(true);
            return true;
        }

        if (Token == 'f')
        {
            if (!ConsumeLiteral("false"))
            {
                return false;
            }
            Out = JsonValue(false);
            return true;
        }

        if (Token == 'n')
        {
            if (!ConsumeLiteral("null"))
            {
                return false;
            }
            Out = JsonValue(nullptr);
            return true;
        }

        if (Token == '-' || (Token >= '0' && Token <= '9'))
        {
            JsonNumber Number;
            if (!ParseNumber(Number))
            {
                return false;
            }
            Out = JsonValue(std::move(Number));
            return true;
        }

        return Fail("unexpected token while reading a JSON value");
    }

    bool ParseObject(const std::size_t Depth, JsonValue::Object& Out)
    {
        ++Position_;
        SkipWhitespace();
        if (ConsumeIf('}'))
        {
            return true;
        }

        while (true)
        {
            if (Position_ >= Input_.size() || Input_[Position_] != '"')
            {
                return Fail("expected a quoted JSON object key");
            }

            std::string Key;
            if (!ParseString(Key))
            {
                return false;
            }

            SkipWhitespace();
            if (!ConsumeIf(':'))
            {
                return Fail("expected ':' after a JSON object key");
            }

            SkipWhitespace();
            JsonValue Value;
            if (!ParseValue(Depth + 1U, Value))
            {
                return false;
            }

            if (Out.size() >= Limits_.MaxContainerEntries)
            {
                return Fail("JSON object exceeds the configured entry limit");
            }

            const auto Inserted = Out.emplace(std::move(Key), std::move(Value));
            if (!Inserted.second)
            {
                return Fail("duplicate JSON object key");
            }

            SkipWhitespace();
            if (ConsumeIf('}'))
            {
                return true;
            }
            if (!ConsumeIf(','))
            {
                return Fail("expected ',' or '}' in a JSON object");
            }
            SkipWhitespace();
        }
    }

    bool ParseArray(const std::size_t Depth, JsonValue::Array& Out)
    {
        ++Position_;
        SkipWhitespace();
        if (ConsumeIf(']'))
        {
            return true;
        }

        while (true)
        {
            if (Out.size() >= Limits_.MaxContainerEntries)
            {
                return Fail("JSON array exceeds the configured entry limit");
            }

            JsonValue Value;
            if (!ParseValue(Depth + 1U, Value))
            {
                return false;
            }
            Out.emplace_back(std::move(Value));

            SkipWhitespace();
            if (ConsumeIf(']'))
            {
                return true;
            }
            if (!ConsumeIf(','))
            {
                return Fail("expected ',' or ']' in a JSON array");
            }
            SkipWhitespace();
        }
    }

    bool ParseString(std::string& Out)
    {
        ++Position_;
        while (Position_ < Input_.size())
        {
            const unsigned char Byte = static_cast<unsigned char>(Input_[Position_++]);
            if (Byte == '"')
            {
                if (!IsValidUtf8(Out))
                {
                    return Fail("JSON string contains invalid UTF-8");
                }
                return true;
            }

            if (Byte < 0x20U)
            {
                return Fail("JSON strings cannot contain unescaped control bytes");
            }

            if (Byte != '\\')
            {
                Out.push_back(static_cast<char>(Byte));
            }
            else
            {
                if (Position_ >= Input_.size())
                {
                    return Fail("unterminated JSON escape sequence");
                }

                const char Escape = Input_[Position_++];
                switch (Escape)
                {
                case '"':
                case '\\':
                case '/':
                    Out.push_back(Escape);
                    break;
                case 'b':
                    Out.push_back('\b');
                    break;
                case 'f':
                    Out.push_back('\f');
                    break;
                case 'n':
                    Out.push_back('\n');
                    break;
                case 'r':
                    Out.push_back('\r');
                    break;
                case 't':
                    Out.push_back('\t');
                    break;
                case 'u':
                    if (!ParseUnicodeEscape(Out))
                    {
                        return false;
                    }
                    break;
                default:
                    return Fail("invalid JSON escape sequence");
                }
            }

            if (Out.size() > Limits_.MaxStringBytes)
            {
                return Fail("JSON string exceeds the configured byte limit");
            }
        }

        return Fail("unterminated JSON string");
    }

    bool ParseUnicodeEscape(std::string& Out)
    {
        std::uint32_t First = 0U;
        if (!ReadHexQuad(First))
        {
            return false;
        }

        std::uint32_t CodePoint = First;
        if (First >= 0xD800U && First <= 0xDBFFU)
        {
            if (Position_ + 2U > Input_.size() ||
                Input_[Position_] != '\\' ||
                Input_[Position_ + 1U] != 'u')
            {
                return Fail("high surrogate must be followed by a low surrogate");
            }
            Position_ += 2U;
            std::uint32_t Second = 0U;
            if (!ReadHexQuad(Second))
            {
                return false;
            }
            if (Second < 0xDC00U || Second > 0xDFFFU)
            {
                return Fail("invalid low surrogate in JSON string");
            }
            CodePoint = 0x10000U + ((First - 0xD800U) << 10U) + (Second - 0xDC00U);
        }
        else if (First >= 0xDC00U && First <= 0xDFFFU)
        {
            return Fail("unexpected low surrogate in JSON string");
        }

        AppendUtf8(CodePoint, Out);
        return true;
    }

    bool ReadHexQuad(std::uint32_t& Out)
    {
        if (Position_ + 4U > Input_.size())
        {
            return Fail("incomplete Unicode escape sequence");
        }

        Out = 0U;
        for (int Index = 0; Index < 4; ++Index)
        {
            const char Character = Input_[Position_++];
            std::uint32_t Digit = 0U;
            if (Character >= '0' && Character <= '9')
            {
                Digit = static_cast<std::uint32_t>(Character - '0');
            }
            else if (Character >= 'a' && Character <= 'f')
            {
                Digit = static_cast<std::uint32_t>(Character - 'a' + 10);
            }
            else if (Character >= 'A' && Character <= 'F')
            {
                Digit = static_cast<std::uint32_t>(Character - 'A' + 10);
            }
            else
            {
                return Fail("invalid hexadecimal digit in Unicode escape");
            }
            Out = (Out << 4U) | Digit;
        }
        return true;
    }

    bool ParseNumber(JsonNumber& Out)
    {
        const std::size_t Start = Position_;
        ConsumeIf('-');
        if (Position_ >= Input_.size())
        {
            return Fail("incomplete JSON number");
        }

        if (Input_[Position_] == '0')
        {
            ++Position_;
            if (Position_ < Input_.size() &&
                Input_[Position_] >= '0' &&
                Input_[Position_] <= '9')
            {
                return Fail("JSON numbers cannot contain leading zeroes");
            }
        }
        else if (Input_[Position_] >= '1' && Input_[Position_] <= '9')
        {
            while (Position_ < Input_.size() &&
                   Input_[Position_] >= '0' &&
                   Input_[Position_] <= '9')
            {
                ++Position_;
            }
        }
        else
        {
            return Fail("invalid integer part in JSON number");
        }

        if (ConsumeIf('.'))
        {
            const std::size_t FractionStart = Position_;
            while (Position_ < Input_.size() &&
                   Input_[Position_] >= '0' &&
                   Input_[Position_] <= '9')
            {
                ++Position_;
            }
            if (FractionStart == Position_)
            {
                return Fail("JSON number fraction requires at least one digit");
            }
        }

        if (Position_ < Input_.size() &&
            (Input_[Position_] == 'e' || Input_[Position_] == 'E'))
        {
            ++Position_;
            if (Position_ < Input_.size() &&
                (Input_[Position_] == '+' || Input_[Position_] == '-'))
            {
                ++Position_;
            }
            const std::size_t ExponentStart = Position_;
            while (Position_ < Input_.size() &&
                   Input_[Position_] >= '0' &&
                   Input_[Position_] <= '9')
            {
                ++Position_;
            }
            if (ExponentStart == Position_)
            {
                return Fail("JSON number exponent requires at least one digit");
            }
        }

        Out.Lexeme = std::string(Input_.substr(Start, Position_ - Start));
        return true;
    }

    bool ConsumeLiteral(const std::string_view Literal)
    {
        if (Input_.substr(Position_, Literal.size()) != Literal)
        {
            return Fail("invalid JSON literal");
        }
        Position_ += Literal.size();
        return true;
    }

    bool ConsumeIf(const char Expected)
    {
        if (Position_ >= Input_.size() || Input_[Position_] != Expected)
        {
            return false;
        }
        ++Position_;
        return true;
    }

    void SkipWhitespace()
    {
        while (Position_ < Input_.size())
        {
            const char Character = Input_[Position_];
            if (Character != ' ' && Character != '\t' && Character != '\r' && Character != '\n')
            {
                return;
            }
            ++Position_;
        }
    }

    bool Fail(std::string Message)
    {
        Error_.Message = std::move(Message);
        Error_.Offset = Position_;
        Error_.Line = 1U;
        Error_.Column = 1U;
        for (std::size_t Index = 0U; Index < Position_ && Index < Input_.size(); ++Index)
        {
            if (Input_[Index] == '\n')
            {
                ++Error_.Line;
                Error_.Column = 1U;
            }
            else
            {
                ++Error_.Column;
            }
        }
        return false;
    }

    static void AppendUtf8(const std::uint32_t CodePoint, std::string& Out)
    {
        if (CodePoint <= 0x7FU)
        {
            Out.push_back(static_cast<char>(CodePoint));
        }
        else if (CodePoint <= 0x7FFU)
        {
            Out.push_back(static_cast<char>(0xC0U | (CodePoint >> 6U)));
            Out.push_back(static_cast<char>(0x80U | (CodePoint & 0x3FU)));
        }
        else if (CodePoint <= 0xFFFFU)
        {
            Out.push_back(static_cast<char>(0xE0U | (CodePoint >> 12U)));
            Out.push_back(static_cast<char>(0x80U | ((CodePoint >> 6U) & 0x3FU)));
            Out.push_back(static_cast<char>(0x80U | (CodePoint & 0x3FU)));
        }
        else
        {
            Out.push_back(static_cast<char>(0xF0U | (CodePoint >> 18U)));
            Out.push_back(static_cast<char>(0x80U | ((CodePoint >> 12U) & 0x3FU)));
            Out.push_back(static_cast<char>(0x80U | ((CodePoint >> 6U) & 0x3FU)));
            Out.push_back(static_cast<char>(0x80U | (CodePoint & 0x3FU)));
        }
    }

    static bool IsValidUtf8(const std::string_view Value)
    {
        std::size_t Index = 0U;
        while (Index < Value.size())
        {
            const auto Lead = static_cast<unsigned char>(Value[Index]);
            if (Lead <= 0x7FU)
            {
                ++Index;
                continue;
            }

            std::size_t Width = 0U;
            std::uint32_t CodePoint = 0U;
            std::uint32_t Minimum = 0U;
            if ((Lead & 0xE0U) == 0xC0U)
            {
                Width = 2U;
                CodePoint = Lead & 0x1FU;
                Minimum = 0x80U;
            }
            else if ((Lead & 0xF0U) == 0xE0U)
            {
                Width = 3U;
                CodePoint = Lead & 0x0FU;
                Minimum = 0x800U;
            }
            else if ((Lead & 0xF8U) == 0xF0U)
            {
                Width = 4U;
                CodePoint = Lead & 0x07U;
                Minimum = 0x10000U;
            }
            else
            {
                return false;
            }

            if (Index + Width > Value.size())
            {
                return false;
            }
            for (std::size_t Offset = 1U; Offset < Width; ++Offset)
            {
                const auto Continuation = static_cast<unsigned char>(Value[Index + Offset]);
                if ((Continuation & 0xC0U) != 0x80U)
                {
                    return false;
                }
                CodePoint = (CodePoint << 6U) | (Continuation & 0x3FU);
            }

            if (CodePoint < Minimum ||
                CodePoint > 0x10FFFFU ||
                (CodePoint >= 0xD800U && CodePoint <= 0xDFFFU))
            {
                return false;
            }
            Index += Width;
        }
        return true;
    }

    std::string_view Input_;
    ParseLimits Limits_;
    std::size_t Position_ = 0U;
    ParseError Error_;
};

ParseError ContractError(std::string Message)
{
    ParseError Error;
    Error.Message = std::move(Message);
    return Error;
}

const JsonValue* Find(const JsonValue::Object& Object, const std::string_view Key)
{
    const auto Iterator = Object.find(Key);
    return Iterator == Object.end() ? nullptr : &Iterator->second;
}

bool RejectUnknown(
    const JsonValue::Object& Object,
    const std::initializer_list<std::string_view> Allowed,
    ParseError& Error)
{
    for (const auto& Entry : Object)
    {
        bool Found = false;
        for (const auto Candidate : Allowed)
        {
            if (Entry.first == Candidate)
            {
                Found = true;
                break;
            }
        }
        if (!Found)
        {
            Error = ContractError("unknown protocol field: " + Entry.first);
            return false;
        }
    }
    return true;
}

bool ReadRequiredString(
    const JsonValue::Object& Object,
    const std::string_view Key,
    std::string& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    const std::string* String = Value == nullptr ? nullptr : Value->AsString();
    if (String == nullptr || String->empty())
    {
        Error = ContractError("required field must be a non-empty string: " + std::string(Key));
        return false;
    }
    Out = *String;
    return true;
}

bool ReadOptionalString(
    const JsonValue::Object& Object,
    const std::string_view Key,
    std::optional<std::string>& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    if (Value == nullptr)
    {
        return true;
    }
    const std::string* String = Value->AsString();
    if (String == nullptr || String->empty())
    {
        Error = ContractError("optional field must be a non-empty string when present: " + std::string(Key));
        return false;
    }
    Out = *String;
    return true;
}

bool ReadOptionalStringAllowEmpty(
    const JsonValue::Object& Object,
    const std::string_view Key,
    std::optional<std::string>& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    if (Value == nullptr)
    {
        return true;
    }
    const std::string* String = Value->AsString();
    if (String == nullptr)
    {
        Error = ContractError(
            "optional field must be a string when present: " +
            std::string(Key));
        return false;
    }
    Out = *String;
    return true;
}

bool ParseInteger(const JsonValue& Value, std::int64_t& Out)
{
    const JsonNumber* Number = Value.AsNumber();
    if (Number == nullptr ||
        Number->Lexeme.find_first_of(".eE") != std::string::npos)
    {
        return false;
    }
    const char* Begin = Number->Lexeme.data();
    const char* End = Begin + Number->Lexeme.size();
    const auto Conversion = std::from_chars(Begin, End, Out);
    return Conversion.ec == std::errc{} && Conversion.ptr == End;
}

bool ReadRequiredInteger(
    const JsonValue::Object& Object,
    const std::string_view Key,
    std::int64_t& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    if (Value == nullptr || !ParseInteger(*Value, Out))
    {
        Error = ContractError("required field must be an integer: " + std::string(Key));
        return false;
    }
    return true;
}

bool ReadOptionalInteger(
    const JsonValue::Object& Object,
    const std::string_view Key,
    std::optional<std::int64_t>& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    if (Value == nullptr)
    {
        return true;
    }
    std::int64_t Integer = 0;
    if (!ParseInteger(*Value, Integer))
    {
        Error = ContractError("optional field must be an integer when present: " + std::string(Key));
        return false;
    }
    Out = Integer;
    return true;
}

bool ReadRequiredBool(
    const JsonValue::Object& Object,
    const std::string_view Key,
    bool& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    const bool* Boolean = Value == nullptr ? nullptr : Value->AsBool();
    if (Boolean == nullptr)
    {
        Error = ContractError("required field must be a boolean: " + std::string(Key));
        return false;
    }
    Out = *Boolean;
    return true;
}

bool ReadStringArray(
    const JsonValue::Object& Object,
    const std::string_view Key,
    const bool Required,
    std::vector<std::string>& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, Key);
    if (Value == nullptr)
    {
        if (Required)
        {
            Error = ContractError("required field must be an array: " + std::string(Key));
            return false;
        }
        return true;
    }

    const JsonValue::Array* Array = Value->AsArray();
    if (Array == nullptr)
    {
        Error = ContractError("field must be an array of strings: " + std::string(Key));
        return false;
    }
    for (const JsonValue& Item : *Array)
    {
        const std::string* String = Item.AsString();
        if (String == nullptr)
        {
            Error = ContractError("field must contain only strings: " + std::string(Key));
            return false;
        }
        Out.emplace_back(*String);
    }
    return true;
}

bool ReadExtensions(
    const JsonValue::Object& Object,
    JsonValue::Object& Out,
    ParseError& Error)
{
    const JsonValue* Value = Find(Object, "extensions");
    if (Value == nullptr)
    {
        return true;
    }
    const JsonValue::Object* Extensions = Value->AsObject();
    if (Extensions == nullptr)
    {
        Error = ContractError("extensions must be a JSON object");
        return false;
    }
    if (Extensions->size() > MaxProtocolExtensions)
    {
        Error = ContractError(
            "extensions cannot exceed " +
            std::to_string(MaxProtocolExtensions) +
            " properties");
        return false;
    }
    Out = *Extensions;
    return true;
}

bool ValidateVersions(
    const std::string& ParsedProtocol,
    const std::string& ParsedSchema,
    ParseError& Error)
{
    if (ParsedProtocol != ProtocolVersion)
    {
        Error = ContractError("unsupported protocolVersion");
        return false;
    }
    if (ParsedSchema != SchemaVersion)
    {
        Error = ContractError("unsupported schemaVersion");
        return false;
    }
    return true;
}

bool IsProtocolId(const std::string_view Value)
{
    if (Value.empty() || Value.size() > 128U)
    {
        return false;
    }
    for (const char Character : Value)
    {
        const bool Allowed =
            (Character >= 'A' && Character <= 'Z') ||
            (Character >= 'a' && Character <= 'z') ||
            (Character >= '0' && Character <= '9') ||
            Character == '.' ||
            Character == '_' ||
            Character == ':' ||
            Character == '-';
        if (!Allowed)
        {
            return false;
        }
    }
    return true;
}

bool IsProtocolName(const std::string_view Value)
{
    if (Value.empty() ||
        Value.size() > 96U ||
        Value.front() < 'a' ||
        Value.front() > 'z')
    {
        return false;
    }
    for (const char Character : Value)
    {
        const bool Allowed =
            (Character >= 'a' && Character <= 'z') ||
            (Character >= '0' && Character <= '9') ||
            Character == '.' ||
            Character == '_' ||
            Character == '-';
        if (!Allowed)
        {
            return false;
        }
    }
    return true;
}

bool ValidateId(
    const std::string& Value,
    const std::string_view Field,
    ParseError& Error)
{
    if (IsProtocolId(Value))
    {
        return true;
    }
    Error = ContractError("field is not a valid protocol id: " + std::string(Field));
    return false;
}

bool ValidateOptionalId(
    const std::optional<std::string>& Value,
    const std::string_view Field,
    ParseError& Error)
{
    return !Value.has_value() || ValidateId(*Value, Field, Error);
}

bool ValidateUniqueIds(
    const std::vector<std::string>& Values,
    const std::string_view Field,
    ParseError& Error)
{
    std::set<std::string_view> Seen;
    for (const auto& Value : Values)
    {
        if (!IsProtocolId(Value))
        {
            Error = ContractError("field contains an invalid protocol id: " + std::string(Field));
            return false;
        }
        if (!Seen.emplace(Value).second)
        {
            Error = ContractError("field contains a duplicate protocol id: " + std::string(Field));
            return false;
        }
    }
    return true;
}

bool ValidateUnicodeScalarLength(
    const std::string& Value,
    std::size_t Maximum,
    std::string_view Field,
    ParseError& Error);

bool ValidateLength(
    const std::string& Value,
    const std::size_t Maximum,
    const std::string_view Field,
    ParseError& Error)
{
    return ValidateUnicodeScalarLength(
        Value,
        Maximum,
        Field,
        Error);
}

bool ValidateUnicodeScalarLength(
    const std::string& Value,
    const std::size_t Maximum,
    const std::string_view Field,
    ParseError& Error)
{
    if (Value.empty())
    {
        Error = ContractError(
            "field is outside its supported string length: " +
            std::string(Field));
        return false;
    }

    std::size_t Offset = 0U;
    std::size_t Scalars = 0U;
    while (Offset < Value.size())
    {
        const auto Lead =
            static_cast<unsigned char>(Value[Offset]);
        std::size_t Width = 0U;
        if (Lead <= 0x7FU)
        {
            Width = 1U;
        }
        else if (Lead >= 0xC2U && Lead <= 0xDFU)
        {
            Width = 2U;
        }
        else if (Lead >= 0xE0U && Lead <= 0xEFU)
        {
            Width = 3U;
        }
        else if (Lead >= 0xF0U && Lead <= 0xF4U)
        {
            Width = 4U;
        }
        else
        {
            Width = 0U;
        }

        if (Width == 0U || Offset + Width > Value.size())
        {
            Error = ContractError(
                "field contains invalid UTF-8: " + std::string(Field));
            return false;
        }

        for (std::size_t Index = 1U; Index < Width; ++Index)
        {
            const auto Continuation =
                static_cast<unsigned char>(Value[Offset + Index]);
            if ((Continuation & 0xC0U) != 0x80U)
            {
                Error = ContractError(
                    "field contains invalid UTF-8: " +
                    std::string(Field));
                return false;
            }
        }

        if ((Lead == 0xE0U
             && static_cast<unsigned char>(Value[Offset + 1U]) < 0xA0U)
            || (Lead == 0xEDU
                && static_cast<unsigned char>(Value[Offset + 1U]) >= 0xA0U)
            || (Lead == 0xF0U
                && static_cast<unsigned char>(Value[Offset + 1U]) < 0x90U)
            || (Lead == 0xF4U
                && static_cast<unsigned char>(Value[Offset + 1U]) > 0x8FU))
        {
            Error = ContractError(
                "field contains invalid UTF-8: " + std::string(Field));
            return false;
        }

        Offset += Width;
        ++Scalars;
        if (Scalars > Maximum)
        {
            Error = ContractError(
                "field is outside its supported string length: " +
                std::string(Field));
            return false;
        }
    }

    return true;
}

bool IsAsciiAlpha(const char Character) noexcept
{
    return (Character >= 'A' && Character <= 'Z') ||
           (Character >= 'a' && Character <= 'z');
}

bool IsAsciiDigit(const char Character) noexcept
{
    return Character >= '0' && Character <= '9';
}

bool IsHexDigit(const char Character) noexcept
{
    return IsAsciiDigit(Character) ||
           (Character >= 'A' && Character <= 'F') ||
           (Character >= 'a' && Character <= 'f');
}

bool IsUriUnreserved(const char Character) noexcept
{
    return IsAsciiAlpha(Character) ||
           IsAsciiDigit(Character) ||
           Character == '-' ||
           Character == '.' ||
           Character == '_' ||
           Character == '~';
}

bool IsUriSubDelimiter(const char Character) noexcept
{
    return Character == '!' ||
           Character == '$' ||
           Character == '&' ||
           Character == '\'' ||
           Character == '(' ||
           Character == ')' ||
           Character == '*' ||
           Character == '+' ||
           Character == ',' ||
           Character == ';' ||
           Character == '=';
}

bool ValidateUriComponent(
    const std::string_view Value,
    const std::string_view ExtraCharacters)
{
    std::size_t Offset = 0U;
    while (Offset < Value.size())
    {
        const char Character = Value[Offset];
        if (IsUriUnreserved(Character) ||
            IsUriSubDelimiter(Character) ||
            ExtraCharacters.find(Character) != std::string_view::npos)
        {
            ++Offset;
            continue;
        }
        if (Character == '%' &&
            Offset + 2U < Value.size() &&
            IsHexDigit(Value[Offset + 1U]) &&
            IsHexDigit(Value[Offset + 2U]))
        {
            Offset += 3U;
            continue;
        }
        return false;
    }
    return true;
}

bool ValidateIpv4Address(const std::string_view Value)
{
    std::size_t Offset = 0U;
    for (std::size_t Part = 0U; Part < 4U; ++Part)
    {
        const std::size_t End = Value.find('.', Offset);
        const bool IsLastPart = Part == 3U;
        if ((End == std::string_view::npos) != IsLastPart)
        {
            return false;
        }

        const std::size_t PartEnd =
            End == std::string_view::npos ? Value.size() : End;
        const std::string_view Segment =
            Value.substr(Offset, PartEnd - Offset);
        if (Segment.empty() ||
            Segment.size() > 3U ||
            (Segment.size() > 1U && Segment.front() == '0'))
        {
            return false;
        }

        unsigned int Number = 0U;
        for (const char Character : Segment)
        {
            if (!IsAsciiDigit(Character))
            {
                return false;
            }
            Number = Number * 10U +
                     static_cast<unsigned int>(Character - '0');
        }
        if (Number > 255U)
        {
            return false;
        }

        Offset = PartEnd + 1U;
    }
    return Offset == Value.size() + 1U;
}

bool CountIpv6Units(
    const std::string_view Value,
    std::size_t& Units)
{
    if (Value.empty())
    {
        return true;
    }

    std::size_t Offset = 0U;
    while (Offset < Value.size())
    {
        const std::size_t End = Value.find(':', Offset);
        const std::size_t SegmentEnd =
            End == std::string_view::npos ? Value.size() : End;
        const std::string_view Segment =
            Value.substr(Offset, SegmentEnd - Offset);
        if (Segment.empty())
        {
            return false;
        }

        if (Segment.find('.') != std::string_view::npos)
        {
            if (End != std::string_view::npos ||
                !ValidateIpv4Address(Segment))
            {
                return false;
            }
            Units += 2U;
        }
        else
        {
            if (Segment.size() > 4U)
            {
                return false;
            }
            for (const char Character : Segment)
            {
                if (!IsHexDigit(Character))
                {
                    return false;
                }
            }
            ++Units;
        }

        if (End == std::string_view::npos)
        {
            break;
        }
        Offset = End + 1U;
        if (Offset == Value.size())
        {
            return false;
        }
    }
    return true;
}

bool ValidateIpLiteral(const std::string_view Value)
{
    if (Value.empty())
    {
        return false;
    }
    if (Value.front() == 'v' || Value.front() == 'V')
    {
        const std::size_t Dot = Value.find('.');
        if (Dot == std::string_view::npos || Dot <= 1U ||
            Dot + 1U >= Value.size())
        {
            return false;
        }
        for (std::size_t Index = 1U; Index < Dot; ++Index)
        {
            if (!IsHexDigit(Value[Index]))
            {
                return false;
            }
        }
        for (std::size_t Index = Dot + 1U;
             Index < Value.size();
             ++Index)
        {
            const char Character = Value[Index];
            if (!IsUriUnreserved(Character) &&
                !IsUriSubDelimiter(Character) &&
                Character != ':')
            {
                return false;
            }
        }
        return true;
    }

    const std::size_t Compression = Value.find("::");
    if (Compression == std::string_view::npos)
    {
        std::size_t Units = 0U;
        return CountIpv6Units(Value, Units) && Units == 8U;
    }
    if (Value.find("::", Compression + 2U) != std::string_view::npos)
    {
        return false;
    }

    std::size_t Units = 0U;
    const std::string_view Left = Value.substr(0U, Compression);
    const std::string_view Right = Value.substr(Compression + 2U);
    return CountIpv6Units(Left, Units) &&
           CountIpv6Units(Right, Units) &&
           Units < 8U;
}

bool ValidateUriAuthority(const std::string_view Authority)
{
    std::string_view HostAndPort = Authority;
    const std::size_t UserInfoEnd = Authority.find('@');
    if (UserInfoEnd != std::string_view::npos)
    {
        if (Authority.find('@', UserInfoEnd + 1U) !=
                std::string_view::npos ||
            !ValidateUriComponent(
                Authority.substr(0U, UserInfoEnd),
                ":"))
        {
            return false;
        }
        HostAndPort = Authority.substr(UserInfoEnd + 1U);
    }

    std::string_view Host;
    std::string_view Port;
    bool HasPort = false;
    if (!HostAndPort.empty() && HostAndPort.front() == '[')
    {
        const std::size_t ClosingBracket = HostAndPort.find(']');
        if (ClosingBracket == std::string_view::npos ||
            !ValidateIpLiteral(
                HostAndPort.substr(1U, ClosingBracket - 1U)))
        {
            return false;
        }
        Host = HostAndPort.substr(0U, ClosingBracket + 1U);
        const std::string_view Remainder =
            HostAndPort.substr(ClosingBracket + 1U);
        if (!Remainder.empty())
        {
            if (Remainder.front() != ':')
            {
                return false;
            }
            HasPort = true;
            Port = Remainder.substr(1U);
        }
    }
    else
    {
        const std::size_t Colon = HostAndPort.find(':');
        if (Colon == std::string_view::npos)
        {
            Host = HostAndPort;
        }
        else
        {
            if (HostAndPort.find(':', Colon + 1U) !=
                std::string_view::npos)
            {
                return false;
            }
            Host = HostAndPort.substr(0U, Colon);
            Port = HostAndPort.substr(Colon + 1U);
            HasPort = true;
        }
        if (!ValidateUriComponent(Host, ""))
        {
            return false;
        }
    }

    if (HasPort)
    {
        for (const char Character : Port)
        {
            if (!IsAsciiDigit(Character))
            {
                return false;
            }
        }
    }
    return true;
}

bool ValidateUriPath(
    const std::string_view Path,
    const bool AllowColonInFirstSegment)
{
    if (!ValidateUriComponent(Path, "/:@"))
    {
        return false;
    }
    if (!AllowColonInFirstSegment)
    {
        const std::size_t FirstSlash = Path.find('/');
        const std::string_view FirstSegment =
            Path.substr(0U, FirstSlash);
        if (FirstSegment.find(':') != std::string_view::npos)
        {
            return false;
        }
    }
    return true;
}

bool ValidateScheme(const std::string_view Scheme)
{
    if (Scheme.empty() || !IsAsciiAlpha(Scheme.front()))
    {
        return false;
    }
    for (std::size_t Index = 1U; Index < Scheme.size(); ++Index)
    {
        const char Character = Scheme[Index];
        if (!IsAsciiAlpha(Character) &&
            !IsAsciiDigit(Character) &&
            Character != '+' &&
            Character != '-' &&
            Character != '.')
        {
            return false;
        }
    }
    return true;
}

bool ValidateUriReferenceSyntax(const std::string_view Value)
{
    const std::size_t FragmentStart = Value.find('#');
    if (FragmentStart != std::string_view::npos &&
        Value.find('#', FragmentStart + 1U) != std::string_view::npos)
    {
        return false;
    }
    const std::string_view BeforeFragment =
        Value.substr(0U, FragmentStart);
    const std::string_view Fragment =
        FragmentStart == std::string_view::npos
            ? std::string_view{}
            : Value.substr(FragmentStart + 1U);
    if (FragmentStart != std::string_view::npos &&
        !ValidateUriComponent(Fragment, ":@/?"))
    {
        return false;
    }

    const std::size_t QueryStart = BeforeFragment.find('?');
    const std::string_view Main =
        BeforeFragment.substr(0U, QueryStart);
    const std::string_view Query =
        QueryStart == std::string_view::npos
            ? std::string_view{}
            : BeforeFragment.substr(QueryStart + 1U);
    if (QueryStart != std::string_view::npos &&
        !ValidateUriComponent(Query, ":@/?"))
    {
        return false;
    }

    const std::size_t Colon = Main.find(':');
    const std::size_t Slash = Main.find('/');
    const bool HasScheme =
        Colon != std::string_view::npos &&
        (Slash == std::string_view::npos || Colon < Slash) &&
        ValidateScheme(Main.substr(0U, Colon));
    const std::string_view Hierarchy =
        HasScheme ? Main.substr(Colon + 1U) : Main;

    if (Hierarchy.size() >= 2U &&
        Hierarchy[0U] == '/' &&
        Hierarchy[1U] == '/')
    {
        const std::size_t PathStart = Hierarchy.find('/', 2U);
        const std::string_view Authority =
            PathStart == std::string_view::npos
                ? Hierarchy.substr(2U)
                : Hierarchy.substr(2U, PathStart - 2U);
        const std::string_view Path =
            PathStart == std::string_view::npos
                ? std::string_view{}
                : Hierarchy.substr(PathStart);
        return ValidateUriAuthority(Authority) &&
               ValidateUriPath(Path, true);
    }

    return ValidateUriPath(Hierarchy, HasScheme);
}

bool ValidateUriReference(
    const std::string& Value,
    const std::string_view Field,
    ParseError& Error)
{
    if (ValidateUriReferenceSyntax(Value))
    {
        return true;
    }
    Error = ContractError(
        "field is not a valid URI reference: " + std::string(Field));
    return false;
}

bool ParseFixedDigits(
    const std::string_view Value,
    const std::size_t Offset,
    const std::size_t Count,
    int& Out)
{
    if (Offset + Count > Value.size())
    {
        return false;
    }

    int Parsed = 0;
    for (std::size_t Index = Offset; Index < Offset + Count; ++Index)
    {
        const char Character = Value[Index];
        if (Character < '0' || Character > '9')
        {
            return false;
        }
        Parsed = Parsed * 10 + (Character - '0');
    }
    Out = Parsed;
    return true;
}

bool IsLeapYear(const int Year)
{
    return Year % 4 == 0 && (Year % 100 != 0 || Year % 400 == 0);
}

bool ValidateDateTime(
    const std::string& Value,
    const std::string_view Field,
    ParseError& Error)
{
    const auto Reject = [&Error, Field]()
    {
        Error = ContractError(
            "field is not a supported RFC 3339 date-time: " +
            std::string(Field));
        return false;
    };
    if (Value.size() < 20U || Value.size() > 64U)
    {
        return Reject();
    }

    int Year = 0;
    int Month = 0;
    int Day = 0;
    int Hour = 0;
    int Minute = 0;
    int Second = 0;
    if (!ParseFixedDigits(Value, 0U, 4U, Year) ||
        Value[4] != '-' ||
        !ParseFixedDigits(Value, 5U, 2U, Month) ||
        Value[7] != '-' ||
        !ParseFixedDigits(Value, 8U, 2U, Day) ||
        (Value[10] != 'T' && Value[10] != 't') ||
        !ParseFixedDigits(Value, 11U, 2U, Hour) ||
        Value[13] != ':' ||
        !ParseFixedDigits(Value, 14U, 2U, Minute) ||
        Value[16] != ':' ||
        !ParseFixedDigits(Value, 17U, 2U, Second))
    {
        return Reject();
    }

    static constexpr int DaysPerMonth[] = {
        0,
        31,
        28,
        31,
        30,
        31,
        30,
        31,
        31,
        30,
        31,
        30,
        31
    };
    const int MaximumDay =
        Month == 2 && IsLeapYear(Year)
            ? 29
            : (Month >= 1 && Month <= 12 ? DaysPerMonth[Month] : 0);
    if (Year < 1 ||
        MaximumDay == 0 ||
        Day < 1 ||
        Day > MaximumDay ||
        Hour > 23 ||
        Minute > 59 ||
        Second > 59)
    {
        return Reject();
    }

    std::size_t Position = 19U;
    if (Position < Value.size() && Value[Position] == '.')
    {
        ++Position;
        const std::size_t FractionStart = Position;
        while (Position < Value.size() &&
               Value[Position] >= '0' &&
               Value[Position] <= '9')
        {
            ++Position;
        }
        if (Position == FractionStart)
        {
            return Reject();
        }
    }

    if (Position >= Value.size())
    {
        return Reject();
    }
    if (Value[Position] == 'Z' || Value[Position] == 'z')
    {
        ++Position;
    }
    else
    {
        int OffsetHour = 0;
        int OffsetMinute = 0;
        if ((Value[Position] != '+' && Value[Position] != '-') ||
            !ParseFixedDigits(Value, Position + 1U, 2U, OffsetHour) ||
            Position + 3U >= Value.size() ||
            Value[Position + 3U] != ':' ||
            !ParseFixedDigits(Value, Position + 4U, 2U, OffsetMinute) ||
            OffsetHour > 14 ||
            OffsetMinute > 59 ||
            (OffsetHour == 14 && OffsetMinute != 0))
        {
            return Reject();
        }
        Position += 6U;
    }
    if (Position != Value.size())
    {
        return Reject();
    }
    return true;
}

bool DecodeResourceReference(
    const JsonValue& Value,
    ResourceReference& Out,
    ParseError& Error)
{
    const JsonValue::Object* Object = Value.AsObject();
    if (Object == nullptr)
    {
        Error = ContractError("resourceRef must be a JSON object");
        return false;
    }
    if (!RejectUnknown(*Object, {"uri", "mediaType", "digest", "sizeBytes"}, Error) ||
        !ReadRequiredString(*Object, "uri", Out.Uri, Error) ||
        !ReadRequiredString(*Object, "mediaType", Out.MediaType, Error) ||
        !ReadOptionalString(*Object, "digest", Out.Digest, Error) ||
        !ReadOptionalInteger(*Object, "sizeBytes", Out.SizeBytes, Error))
    {
        return false;
    }
    if (Out.SizeBytes.has_value() && *Out.SizeBytes < 0)
    {
        Error = ContractError("resourceRef.sizeBytes cannot be negative");
        return false;
    }
    if (!ValidateUnicodeScalarLength(
            Out.Uri,
            2048U,
            "resourceRef.uri",
            Error) ||
        !ValidateUriReference(Out.Uri, "resourceRef.uri", Error) ||
        !ValidateLength(Out.MediaType, 128U, "resourceRef.mediaType", Error) ||
        (Out.Digest.has_value() &&
         !ValidateLength(*Out.Digest, 256U, "resourceRef.digest", Error)))
    {
        return false;
    }
    return true;
}

bool DecodeVisibility(
    const JsonValue& Value,
    VisibilityRule& Out,
    ParseError& Error)
{
    const JsonValue::Object* Object = Value.AsObject();
    if (Object == nullptr)
    {
        Error = ContractError("visibility must be a JSON object");
        return false;
    }
    if (!RejectUnknown(*Object, {"scope", "audienceIds"}, Error) ||
        !ReadRequiredString(*Object, "scope", Out.Scope, Error) ||
        !ReadStringArray(*Object, "audienceIds", false, Out.AudienceIds, Error))
    {
        return false;
    }
    if (Out.Scope != "world" &&
        Out.Scope != "group" &&
        Out.Scope != "agent" &&
        Out.Scope != "private")
    {
        Error = ContractError("visibility.scope is not supported");
        return false;
    }
    if (!ValidateUniqueIds(Out.AudienceIds, "visibility.audienceIds", Error))
    {
        return false;
    }
    return true;
}

bool DecodeObservation(
    const JsonValue& Root,
    ObservationEnvelope& Out,
    ParseError& Error)
{
    const JsonValue::Object* Object = Root.AsObject();
    if (Object == nullptr)
    {
        Error = ContractError("observation envelope must be a JSON object");
        return false;
    }

    if (!RejectUnknown(
            *Object,
            {
                "protocolVersion",
                "schemaVersion",
                "observationId",
                "worldId",
                "sessionId",
                "source",
                "kind",
                "subjectIds",
                "contentType",
                "schemaRef",
                "contentSchemaVersion",
                "payload",
                "resourceRef",
                "observedAt",
                "ttlMs",
                "sequence",
                "stateVersion",
                "trust",
                "visibility",
                "priority",
                "cacheKey",
                "extensions"
            },
            Error) ||
        !ReadRequiredString(*Object, "protocolVersion", Out.ProtocolVersion, Error) ||
        !ReadRequiredString(*Object, "schemaVersion", Out.SchemaVersion, Error) ||
        !ValidateVersions(Out.ProtocolVersion, Out.SchemaVersion, Error) ||
        !ReadRequiredString(*Object, "observationId", Out.ObservationId, Error) ||
        !ReadRequiredString(*Object, "worldId", Out.WorldId, Error) ||
        !ReadOptionalString(*Object, "sessionId", Out.SessionId, Error) ||
        !ReadRequiredString(*Object, "source", Out.Source, Error) ||
        !ReadRequiredString(*Object, "kind", Out.Kind, Error) ||
        !ReadStringArray(*Object, "subjectIds", false, Out.SubjectIds, Error) ||
        !ReadRequiredString(*Object, "contentType", Out.ContentType, Error) ||
        !ReadOptionalStringAllowEmpty(
            *Object,
            "schemaRef",
            Out.SchemaRef,
            Error) ||
        !ReadOptionalString(*Object, "contentSchemaVersion", Out.ContentSchemaVersion, Error) ||
        !ReadRequiredString(*Object, "observedAt", Out.ObservedAt, Error) ||
        !ReadOptionalInteger(*Object, "ttlMs", Out.TtlMs, Error) ||
        !ReadOptionalInteger(*Object, "sequence", Out.Sequence, Error) ||
        !ReadOptionalString(*Object, "stateVersion", Out.StateVersion, Error) ||
        !ReadRequiredString(*Object, "trust", Out.Trust, Error) ||
        !ReadOptionalString(*Object, "cacheKey", Out.CacheKey, Error) ||
        !ReadExtensions(*Object, Out.Extensions, Error))
    {
        return false;
    }

    static constexpr std::string_view Kinds[] = {
        "event",
        "snapshot",
        "patch",
        "document",
        "metric",
        "relation",
        "resource_ref",
        "custom"
    };
    bool KnownKind = false;
    for (const auto Kind : Kinds)
    {
        KnownKind = KnownKind || Out.Kind == Kind;
    }
    if (!KnownKind)
    {
        Error = ContractError("observation kind is not supported");
        return false;
    }
    if (Out.Trust != "authoritative" &&
        Out.Trust != "trusted" &&
        Out.Trust != "untrusted")
    {
        Error = ContractError("observation trust is not supported");
        return false;
    }

    const JsonValue* Payload = Find(*Object, "payload");
    const JsonValue* Resource = Find(*Object, "resourceRef");
    if ((Payload == nullptr) == (Resource == nullptr))
    {
        Error = ContractError("observation must contain exactly one of payload or resourceRef");
        return false;
    }
    if (Payload != nullptr)
    {
        Out.Payload = *Payload;
    }
    else
    {
        ResourceReference Decoded;
        if (!DecodeResourceReference(*Resource, Decoded, Error))
        {
            return false;
        }
        Out.ResourceRef = std::move(Decoded);
    }

    const JsonValue* Visibility = Find(*Object, "visibility");
    if (Visibility == nullptr || !DecodeVisibility(*Visibility, Out.Visibility, Error))
    {
        if (Visibility == nullptr)
        {
            Error = ContractError("required field is missing: visibility");
        }
        return false;
    }

    const JsonValue* Priority = Find(*Object, "priority");
    if (Priority != nullptr && !ParseInteger(*Priority, Out.Priority))
    {
        Error = ContractError("priority must be an integer");
        return false;
    }
    if (Out.Priority < -1000 || Out.Priority > 1000)
    {
        Error = ContractError("priority is outside the supported range");
        return false;
    }
    if ((Out.TtlMs.has_value() && *Out.TtlMs < 0) ||
        (Out.Sequence.has_value() && *Out.Sequence < 0))
    {
        Error = ContractError("observation counters cannot be negative");
        return false;
    }
    if (!ValidateDateTime(Out.ObservedAt, "observedAt", Error))
    {
        return false;
    }
    if (!ValidateId(Out.ObservationId, "observationId", Error) ||
        !ValidateId(Out.WorldId, "worldId", Error) ||
        !ValidateOptionalId(Out.SessionId, "sessionId", Error) ||
        !ValidateUniqueIds(Out.SubjectIds, "subjectIds", Error) ||
        !ValidateLength(Out.Source, 128U, "source", Error) ||
        !ValidateLength(Out.ContentType, 128U, "contentType", Error) ||
        (Out.ContentSchemaVersion.has_value() &&
         !ValidateLength(*Out.ContentSchemaVersion, 32U, "contentSchemaVersion", Error)) ||
        (Out.StateVersion.has_value() &&
         !ValidateLength(*Out.StateVersion, 128U, "stateVersion", Error)) ||
        (Out.CacheKey.has_value() &&
         !ValidateLength(*Out.CacheKey, 256U, "cacheKey", Error)))
    {
        return false;
    }
    if (Out.Kind == "patch" && !Out.StateVersion.has_value())
    {
        Error = ContractError(
            "patch observations require stateVersion");
        return false;
    }
    return true;
}

bool DecodeActionRequest(
    const JsonValue& Root,
    ActionRequest& Out,
    ParseError& Error)
{
    const JsonValue::Object* Object = Root.AsObject();
    if (Object == nullptr)
    {
        Error = ContractError("action request must be a JSON object");
        return false;
    }
    if (!RejectUnknown(
            *Object,
            {
                "protocolVersion",
                "schemaVersion",
                "operationId",
                "runId",
                "turnId",
                "toolCallId",
                "agentId",
                "worldId",
                "actionName",
                "actionVersion",
                "arguments",
                "decisionKey",
                "batchId",
                "basedOnStateVersion",
                "expectedEffects",
                "reasonCode",
                "requestedAt",
                "deadline",
                "extensions"
            },
            Error) ||
        !ReadRequiredString(*Object, "protocolVersion", Out.ProtocolVersion, Error) ||
        !ReadRequiredString(*Object, "schemaVersion", Out.SchemaVersion, Error) ||
        !ValidateVersions(Out.ProtocolVersion, Out.SchemaVersion, Error) ||
        !ReadRequiredString(*Object, "operationId", Out.OperationId, Error) ||
        !ReadRequiredString(*Object, "runId", Out.RunId, Error) ||
        !ReadRequiredString(*Object, "turnId", Out.TurnId, Error) ||
        !ReadRequiredString(*Object, "toolCallId", Out.ToolCallId, Error) ||
        !ReadRequiredString(*Object, "agentId", Out.AgentId, Error) ||
        !ReadRequiredString(*Object, "worldId", Out.WorldId, Error) ||
        !ReadRequiredString(*Object, "actionName", Out.ActionName, Error) ||
        !ReadRequiredString(*Object, "actionVersion", Out.ActionVersion, Error) ||
        !ReadOptionalString(*Object, "decisionKey", Out.DecisionKey, Error) ||
        !ReadOptionalString(*Object, "batchId", Out.BatchId, Error) ||
        !ReadOptionalString(*Object, "basedOnStateVersion", Out.BasedOnStateVersion, Error) ||
        !ReadStringArray(*Object, "expectedEffects", false, Out.ExpectedEffects, Error) ||
        !ReadOptionalString(*Object, "reasonCode", Out.ReasonCode, Error) ||
        !ReadRequiredString(*Object, "requestedAt", Out.RequestedAt, Error) ||
        !ReadOptionalString(*Object, "deadline", Out.Deadline, Error) ||
        !ReadExtensions(*Object, Out.Extensions, Error))
    {
        return false;
    }
    const JsonValue* Arguments = Find(*Object, "arguments");
    if (Arguments == nullptr)
    {
        Error = ContractError("required field is missing: arguments");
        return false;
    }
    Out.Arguments = *Arguments;
    if (!ValidateId(Out.OperationId, "operationId", Error) ||
        !ValidateId(Out.RunId, "runId", Error) ||
        !ValidateId(Out.TurnId, "turnId", Error) ||
        !ValidateId(Out.ToolCallId, "toolCallId", Error) ||
        !ValidateId(Out.AgentId, "agentId", Error) ||
        !ValidateId(Out.WorldId, "worldId", Error) ||
        !IsProtocolName(Out.ActionName) ||
        !ValidateLength(Out.ActionVersion, 32U, "actionVersion", Error) ||
        (Out.DecisionKey.has_value() &&
         !ValidateUnicodeScalarLength(
             *Out.DecisionKey,
             256U,
             "decisionKey",
             Error)) ||
        !ValidateOptionalId(Out.BatchId, "batchId", Error) ||
        (Out.BasedOnStateVersion.has_value() &&
         !ValidateLength(*Out.BasedOnStateVersion, 128U, "basedOnStateVersion", Error)) ||
        (Out.ReasonCode.has_value() &&
         !ValidateLength(*Out.ReasonCode, 128U, "reasonCode", Error)))
    {
        if (Error.Message.empty())
        {
            Error = ContractError("actionName is not a valid protocol name");
        }
        return false;
    }
    if (Out.ExpectedEffects.size() > 32U)
    {
        Error = ContractError("expectedEffects exceeds the supported item limit");
        return false;
    }
    for (const auto& Effect : Out.ExpectedEffects)
    {
        if (!Effect.empty() &&
            !ValidateUnicodeScalarLength(
                Effect,
                256U,
                "expectedEffects",
                Error))
        {
            return false;
        }
    }
    if (!ValidateDateTime(Out.RequestedAt, "requestedAt", Error) ||
        (Out.Deadline.has_value() &&
         !ValidateDateTime(*Out.Deadline, "deadline", Error)))
    {
        return false;
    }
    return true;
}

bool DecodeActionReceipt(
    const JsonValue& Root,
    ActionReceipt& Out,
    ParseError& Error)
{
    const JsonValue::Object* Object = Root.AsObject();
    if (Object == nullptr)
    {
        Error = ContractError("action receipt must be a JSON object");
        return false;
    }
    if (!RejectUnknown(
            *Object,
            {
                "protocolVersion",
                "schemaVersion",
                "operationId",
                "revision",
                "status",
                "result",
                "stateDiff",
                "authoritativeObservations",
                "errorCode",
                "retryable",
                "committedAt",
                "receivedAt",
                "extensions"
            },
            Error) ||
        !ReadRequiredString(*Object, "protocolVersion", Out.ProtocolVersion, Error) ||
        !ReadRequiredString(*Object, "schemaVersion", Out.SchemaVersion, Error) ||
        !ValidateVersions(Out.ProtocolVersion, Out.SchemaVersion, Error) ||
        !ReadRequiredString(*Object, "operationId", Out.OperationId, Error) ||
        !ReadRequiredInteger(*Object, "revision", Out.Revision, Error) ||
        !ReadOptionalString(*Object, "errorCode", Out.ErrorCode, Error) ||
        !ReadRequiredBool(*Object, "retryable", Out.Retryable, Error) ||
        !ReadOptionalString(*Object, "committedAt", Out.CommittedAt, Error) ||
        !ReadRequiredString(*Object, "receivedAt", Out.ReceivedAt, Error) ||
        !ReadExtensions(*Object, Out.Extensions, Error))
    {
        return false;
    }
    if (Out.Revision < 0)
    {
        Error = ContractError("revision cannot be negative");
        return false;
    }
    if (!ValidateId(Out.OperationId, "operationId", Error) ||
        (Out.ErrorCode.has_value() &&
         !ValidateLength(*Out.ErrorCode, 128U, "errorCode", Error)))
    {
        return false;
    }
    if (!ValidateDateTime(Out.ReceivedAt, "receivedAt", Error) ||
        (Out.CommittedAt.has_value() &&
         !ValidateDateTime(*Out.CommittedAt, "committedAt", Error)))
    {
        return false;
    }

    std::string Status;
    if (!ReadRequiredString(*Object, "status", Status, Error))
    {
        return false;
    }
    if (Status == "succeeded")
    {
        Out.Status = ReceiptStatus::Succeeded;
    }
    else if (Status == "rejected")
    {
        Out.Status = ReceiptStatus::Rejected;
    }
    else if (Status == "failed")
    {
        Out.Status = ReceiptStatus::Failed;
    }
    else if (Status == "unknown")
    {
        Out.Status = ReceiptStatus::Unknown;
    }
    else
    {
        Error = ContractError("action receipt status is not supported");
        return false;
    }

    if (const JsonValue* Result = Find(*Object, "result"); Result != nullptr)
    {
        Out.Result = *Result;
    }
    if (const JsonValue* StateDiff = Find(*Object, "stateDiff"); StateDiff != nullptr)
    {
        Out.StateDiff = *StateDiff;
    }

    const JsonValue* Observations = Find(*Object, "authoritativeObservations");
    if (Observations != nullptr)
    {
        const JsonValue::Array* Array = Observations->AsArray();
        if (Array == nullptr)
        {
            Error = ContractError("authoritativeObservations must be an array");
            return false;
        }
        if (Array->size() >
            MaxAuthoritativeObservationsPerReceipt)
        {
            Error = ContractError(
                "authoritativeObservations exceeds the supported item limit");
            return false;
        }
        Out.AuthoritativeObservations.reserve(Array->size());
        for (const JsonValue& Item : *Array)
        {
            ObservationEnvelope Observation;
            if (!DecodeObservation(Item, Observation, Error))
            {
                return false;
            }
            Out.AuthoritativeObservations.emplace_back(std::move(Observation));
        }
    }
    return true;
}

bool DecodeRuntimeEvent(
    const JsonValue& Root,
    RuntimeEvent& Out,
    ParseError& Error)
{
    const JsonValue::Object* Object = Root.AsObject();
    if (Object == nullptr)
    {
        Error = ContractError("runtime event must be a JSON object");
        return false;
    }
    if (!RejectUnknown(
            *Object,
            {
                "protocolVersion",
                "schemaVersion",
                "eventId",
                "runId",
                "turnId",
                "sequence",
                "kind",
                "durability",
                "runtimeGeneration",
                "attemptId",
                "streamAttemptId",
                "providerId",
                "modelId",
                "transportDialect",
                "providerCapabilityDigest",
                "providerRouteDigest",
                "reasonCode",
                "timestamp",
                "payload",
                "extensions"
            },
            Error) ||
        !ReadRequiredString(*Object, "protocolVersion", Out.ProtocolVersion, Error) ||
        !ReadRequiredString(*Object, "schemaVersion", Out.SchemaVersion, Error) ||
        !ValidateVersions(Out.ProtocolVersion, Out.SchemaVersion, Error) ||
        !ReadRequiredString(*Object, "eventId", Out.EventId, Error) ||
        !ReadOptionalString(*Object, "runId", Out.RunId, Error) ||
        !ReadOptionalString(*Object, "turnId", Out.TurnId, Error) ||
        !ReadRequiredInteger(*Object, "sequence", Out.Sequence, Error) ||
        !ReadRequiredString(*Object, "kind", Out.Kind, Error) ||
        !ReadRequiredInteger(*Object, "runtimeGeneration", Out.RuntimeGeneration, Error) ||
        !ReadOptionalString(*Object, "attemptId", Out.AttemptId, Error) ||
        !ReadOptionalString(*Object, "streamAttemptId", Out.StreamAttemptId, Error) ||
        !ReadOptionalString(*Object, "providerId", Out.ProviderId, Error) ||
        !ReadOptionalString(*Object, "modelId", Out.ModelId, Error) ||
        !ReadOptionalString(*Object, "transportDialect", Out.TransportDialect, Error) ||
        !ReadOptionalString(
            *Object,
            "providerCapabilityDigest",
            Out.ProviderCapabilityDigest,
            Error) ||
        !ReadOptionalString(
            *Object,
            "providerRouteDigest",
            Out.ProviderRouteDigest,
            Error) ||
        !ReadOptionalString(*Object, "reasonCode", Out.ReasonCode, Error) ||
        !ReadRequiredString(*Object, "timestamp", Out.Timestamp, Error) ||
        !ReadExtensions(*Object, Out.Extensions, Error))
    {
        return false;
    }
    if (Out.Sequence < 0 || Out.RuntimeGeneration < 1)
    {
        Error = ContractError("runtime event counters are outside the supported range");
        return false;
    }
    if (!ValidateId(Out.EventId, "eventId", Error) ||
        !ValidateOptionalId(Out.RunId, "runId", Error) ||
        !ValidateOptionalId(Out.TurnId, "turnId", Error) ||
        !ValidateOptionalId(Out.AttemptId, "attemptId", Error) ||
        !ValidateOptionalId(Out.StreamAttemptId, "streamAttemptId", Error) ||
        (Out.ProviderId.has_value() &&
         !ValidateLength(*Out.ProviderId, 128U, "providerId", Error)) ||
        (Out.ModelId.has_value() &&
         !ValidateLength(*Out.ModelId, 256U, "modelId", Error)) ||
        (Out.TransportDialect.has_value() &&
         !ValidateLength(
             *Out.TransportDialect,
             128U,
             "transportDialect",
             Error)) ||
        (Out.ProviderCapabilityDigest.has_value() &&
         !ValidateLength(
             *Out.ProviderCapabilityDigest,
             256U,
             "providerCapabilityDigest",
             Error)) ||
        (Out.ProviderRouteDigest.has_value() &&
         !ValidateLength(
             *Out.ProviderRouteDigest,
             256U,
             "providerRouteDigest",
             Error)) ||
        (Out.ReasonCode.has_value() &&
         !ValidateLength(*Out.ReasonCode, 96U, "reasonCode", Error)) ||
        !ValidateLength(Out.Kind, 96U, "kind", Error))
    {
        return false;
    }
    if (!ValidateDateTime(Out.Timestamp, "timestamp", Error))
    {
        return false;
    }

    std::string Durability;
    if (!ReadRequiredString(*Object, "durability", Durability, Error))
    {
        return false;
    }
    if (Durability == "durable")
    {
        Out.Durability = EventDurability::Durable;
    }
    else if (Durability == "ephemeral")
    {
        Out.Durability = EventDurability::Ephemeral;
    }
    else
    {
        Error = ContractError("runtime event durability is not supported");
        return false;
    }

    const JsonValue* Payload = Find(*Object, "payload");
    if (Payload == nullptr)
    {
        Error = ContractError("required field is missing: payload");
        return false;
    }
    Out.Payload = *Payload;
    return true;
}

template <typename TValue, typename TDecoder>
ParseResult<TValue> ParseProtocolObject(
    const std::string_view Json,
    const ParseLimits& Limits,
    TDecoder Decoder)
{
    ParseResult<TValue> Result;
    ParseResult<JsonValue> Document = ParseJson(Json, Limits);
    if (!Document)
    {
        Result.Error = std::move(Document.Error);
        return Result;
    }
    if (!Decoder(Document.Value, Result.Value, Result.Error))
    {
        return Result;
    }
    Result.Ok = true;
    return Result;
}

void AppendEscapedString(const std::string_view Value, std::string& Out)
{
    static constexpr char Hex[] = "0123456789abcdef";
    Out.push_back('"');
    for (const unsigned char Byte : Value)
    {
        switch (Byte)
        {
        case '"':
            Out.append("\\\"");
            break;
        case '\\':
            Out.append("\\\\");
            break;
        case '\b':
            Out.append("\\b");
            break;
        case '\f':
            Out.append("\\f");
            break;
        case '\n':
            Out.append("\\n");
            break;
        case '\r':
            Out.append("\\r");
            break;
        case '\t':
            Out.append("\\t");
            break;
        default:
            if (Byte < 0x20U)
            {
                Out.append("\\u00");
                Out.push_back(Hex[(Byte >> 4U) & 0x0FU]);
                Out.push_back(Hex[Byte & 0x0FU]);
            }
            else
            {
                Out.push_back(static_cast<char>(Byte));
            }
            break;
        }
    }
    Out.push_back('"');
}

void AppendJson(const JsonValue& Value, std::string& Out)
{
    const JsonValue::Storage& Storage = Value.GetStorage();
    if (std::holds_alternative<std::nullptr_t>(Storage))
    {
        Out.append("null");
    }
    else if (const bool* Boolean = std::get_if<bool>(&Storage); Boolean != nullptr)
    {
        Out.append(*Boolean ? "true" : "false");
    }
    else if (const JsonNumber* Number = std::get_if<JsonNumber>(&Storage); Number != nullptr)
    {
        Out.append(Number->Lexeme);
    }
    else if (const std::string* String = std::get_if<std::string>(&Storage); String != nullptr)
    {
        AppendEscapedString(*String, Out);
    }
    else if (const JsonValue::Array* Array = std::get_if<JsonValue::Array>(&Storage); Array != nullptr)
    {
        Out.push_back('[');
        bool First = true;
        for (const JsonValue& Item : *Array)
        {
            if (!First)
            {
                Out.push_back(',');
            }
            First = false;
            AppendJson(Item, Out);
        }
        Out.push_back(']');
    }
    else
    {
        const JsonValue::Object& Object = std::get<JsonValue::Object>(Storage);
        Out.push_back('{');
        bool First = true;
        for (const auto& Entry : Object)
        {
            if (!First)
            {
                Out.push_back(',');
            }
            First = false;
            AppendEscapedString(Entry.first, Out);
            Out.push_back(':');
            AppendJson(Entry.second, Out);
        }
        Out.push_back('}');
    }
}

JsonValue ObjectValue(JsonValue::Object Value)
{
    return JsonValue(std::move(Value));
}

JsonValue ArrayValue(JsonValue::Array Value)
{
    return JsonValue(std::move(Value));
}

JsonValue NumberValue(const std::int64_t Value)
{
    return JsonValue(JsonNumber{std::to_string(Value)});
}

JsonValue ResourceToJson(const ResourceReference& Resource)
{
    JsonValue::Object Object;
    Object.emplace("uri", JsonValue(Resource.Uri));
    Object.emplace("mediaType", JsonValue(Resource.MediaType));
    if (Resource.Digest.has_value())
    {
        Object.emplace("digest", JsonValue(*Resource.Digest));
    }
    if (Resource.SizeBytes.has_value())
    {
        Object.emplace("sizeBytes", NumberValue(*Resource.SizeBytes));
    }
    return ObjectValue(std::move(Object));
}

JsonValue ObservationToJson(const ObservationEnvelope& Observation)
{
    JsonValue::Object Object;
    Object.emplace("protocolVersion", JsonValue(Observation.ProtocolVersion));
    Object.emplace("schemaVersion", JsonValue(Observation.SchemaVersion));
    Object.emplace("observationId", JsonValue(Observation.ObservationId));
    Object.emplace("worldId", JsonValue(Observation.WorldId));
    if (Observation.SessionId.has_value())
    {
        Object.emplace("sessionId", JsonValue(*Observation.SessionId));
    }
    Object.emplace("source", JsonValue(Observation.Source));
    Object.emplace("kind", JsonValue(Observation.Kind));
    JsonValue::Array SubjectIds;
    for (const auto& SubjectId : Observation.SubjectIds)
    {
        SubjectIds.emplace_back(JsonValue(SubjectId));
    }
    Object.emplace("subjectIds", ArrayValue(std::move(SubjectIds)));
    Object.emplace("contentType", JsonValue(Observation.ContentType));
    if (Observation.SchemaRef.has_value())
    {
        Object.emplace("schemaRef", JsonValue(*Observation.SchemaRef));
    }
    if (Observation.ContentSchemaVersion.has_value())
    {
        Object.emplace("contentSchemaVersion", JsonValue(*Observation.ContentSchemaVersion));
    }
    if (Observation.Payload.has_value())
    {
        Object.emplace("payload", *Observation.Payload);
    }
    if (Observation.ResourceRef.has_value())
    {
        Object.emplace("resourceRef", ResourceToJson(*Observation.ResourceRef));
    }
    Object.emplace("observedAt", JsonValue(Observation.ObservedAt));
    if (Observation.TtlMs.has_value())
    {
        Object.emplace("ttlMs", NumberValue(*Observation.TtlMs));
    }
    if (Observation.Sequence.has_value())
    {
        Object.emplace("sequence", NumberValue(*Observation.Sequence));
    }
    if (Observation.StateVersion.has_value())
    {
        Object.emplace("stateVersion", JsonValue(*Observation.StateVersion));
    }
    Object.emplace("trust", JsonValue(Observation.Trust));
    JsonValue::Object Visibility;
    Visibility.emplace("scope", JsonValue(Observation.Visibility.Scope));
    JsonValue::Array AudienceIds;
    for (const auto& AudienceId : Observation.Visibility.AudienceIds)
    {
        AudienceIds.emplace_back(JsonValue(AudienceId));
    }
    Visibility.emplace("audienceIds", ArrayValue(std::move(AudienceIds)));
    Object.emplace("visibility", ObjectValue(std::move(Visibility)));
    Object.emplace("priority", NumberValue(Observation.Priority));
    if (Observation.CacheKey.has_value())
    {
        Object.emplace("cacheKey", JsonValue(*Observation.CacheKey));
    }
    if (!Observation.Extensions.empty())
    {
        Object.emplace("extensions", ObjectValue(Observation.Extensions));
    }
    return ObjectValue(std::move(Object));
}
} // namespace

ParseResult<JsonValue> ParseJson(const std::string_view Json, const ParseLimits& Limits)
{
    return JsonParser(Json, Limits).Run();
}

ParseResult<ObservationEnvelope> ParseObservationEnvelope(
    const std::string_view Json,
    const ParseLimits& Limits)
{
    return ParseProtocolObject<ObservationEnvelope>(Json, Limits, DecodeObservation);
}

ParseResult<ActionRequest> ParseActionRequest(
    const std::string_view Json,
    const ParseLimits& Limits)
{
    return ParseProtocolObject<ActionRequest>(Json, Limits, DecodeActionRequest);
}

ParseResult<ActionReceipt> ParseActionReceipt(
    const std::string_view Json,
    const ParseLimits& Limits)
{
    return ParseProtocolObject<ActionReceipt>(Json, Limits, DecodeActionReceipt);
}

ParseResult<RuntimeEvent> ParseRuntimeEvent(
    const std::string_view Json,
    const ParseLimits& Limits)
{
    return ParseProtocolObject<RuntimeEvent>(Json, Limits, DecodeRuntimeEvent);
}

std::string SerializeJson(const JsonValue& Value)
{
    std::string Output;
    AppendJson(Value, Output);
    return Output;
}

std::string SerializeActionReceipt(const ActionReceipt& Receipt)
{
    JsonValue::Object Object;
    Object.emplace("protocolVersion", JsonValue(Receipt.ProtocolVersion));
    Object.emplace("schemaVersion", JsonValue(Receipt.SchemaVersion));
    Object.emplace("operationId", JsonValue(Receipt.OperationId));
    Object.emplace("revision", NumberValue(Receipt.Revision));
    Object.emplace("status", JsonValue(std::string(ToWireString(Receipt.Status))));
    if (Receipt.Result.has_value())
    {
        Object.emplace("result", *Receipt.Result);
    }
    if (Receipt.StateDiff.has_value())
    {
        Object.emplace("stateDiff", *Receipt.StateDiff);
    }
    JsonValue::Array Observations;
    for (const auto& Observation : Receipt.AuthoritativeObservations)
    {
        Observations.emplace_back(ObservationToJson(Observation));
    }
    Object.emplace("authoritativeObservations", ArrayValue(std::move(Observations)));
    if (Receipt.ErrorCode.has_value())
    {
        Object.emplace("errorCode", JsonValue(*Receipt.ErrorCode));
    }
    Object.emplace("retryable", JsonValue(Receipt.Retryable));
    if (Receipt.CommittedAt.has_value())
    {
        Object.emplace("committedAt", JsonValue(*Receipt.CommittedAt));
    }
    Object.emplace("receivedAt", JsonValue(Receipt.ReceivedAt));
    if (!Receipt.Extensions.empty())
    {
        Object.emplace("extensions", ObjectValue(Receipt.Extensions));
    }
    return SerializeJson(ObjectValue(std::move(Object)));
}

const char* ToWireString(const ReceiptStatus Status) noexcept
{
    switch (Status)
    {
    case ReceiptStatus::Succeeded:
        return "succeeded";
    case ReceiptStatus::Rejected:
        return "rejected";
    case ReceiptStatus::Failed:
        return "failed";
    case ReceiptStatus::Unknown:
    default:
        return "unknown";
    }
}

const char* ToWireString(const EventDurability Durability) noexcept
{
    return Durability == EventDurability::Durable ? "durable" : "ephemeral";
}
} // namespace game_agent::wire
