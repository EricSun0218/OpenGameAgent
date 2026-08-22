#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace opengameagent::runtime::v1
{
inline constexpr std::int32_t protocol_version = 1;
inline constexpr std::int32_t maximum_page_size = 1024;

enum class event_kind { run, turn, item, result, gap, heartbeat };
enum class item_kind { message, tool, action, approval, interaction, artifact, delegation, plan, media, status };
enum class lifecycle { started, delta, completed };
enum class control_status { accepted, idle, run_not_started, run_mismatch, turn_mismatch, control_closed, unauthorized };

struct initialize_request
{
    std::int32_t minimum_version = protocol_version;
    std::int32_t maximum_version = protocol_version;
    std::vector<std::string> capabilities;
};

struct initialize_response
{
    std::int32_t version = protocol_version;
    std::vector<std::string> capabilities;
    std::string server_name;
    std::string server_version;
};

struct start_request
{
    std::string request_id;
    std::string input_json;
};

struct control_request
{
    std::string session_id;
    std::string actor_id;
    std::string expected_run_id;
    std::string expected_turn_id;
    std::int32_t expected_turn = 0;
    std::optional<std::string> message_json;
};

struct control_response
{
    control_status status = control_status::control_closed;
    std::optional<std::string> active_run_id;
    std::optional<std::int32_t> active_turn;
    bool accepted = false;
};

struct event_envelope
{
    std::int32_t protocol_version = v1::protocol_version;
    std::string event_id;
    std::int64_t sequence = 0;
    std::string occurred_at;
    std::string session_id;
    std::string actor_id;
    std::string input_id;
    std::optional<std::string> run_id;
    std::optional<std::int32_t> turn;
    std::optional<std::string> turn_id;
    std::optional<std::string> item_id;
    event_kind kind = event_kind::run;
    std::optional<item_kind> item;
    lifecycle state = lifecycle::started;
    std::string name;
    std::string payload_json;
    bool terminal = false;
};

struct event_page
{
    std::string session_id;
    std::string actor_id;
    std::int64_t requested_after_sequence = 0;
    std::int64_t first_retained_sequence = 0;
    std::int64_t last_sequence = 0;
    std::int64_t next_after_sequence = 0;
    bool gap = false;
    std::vector<event_envelope> events;
};
}
