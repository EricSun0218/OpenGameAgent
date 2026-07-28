# Changelog

## 0.1.0-alpha.1

- Added the versioned typed game-agent protocol.
- Added the durable streaming agent loop, controls, budgets, and recovery.
- Added strict tool validation, conflict-aware scheduling, skills, context, and
  local baseline memory.
- Added policy-scoped deferred-tool search, exact durable activation, recovery,
  and same-turn required-tool admission for active skills.
- Aligned runtime `ToolDescriptor` validation with the schema enum and timeout
  bounds; retry declarations remain metadata and do not automatically replay
  host actions in this alpha.
- Added a streaming chat-completions provider with DeepSeek V4 Pro defaults.
- Added Godot and Unity engine packages.
- Added an Unreal compatibility module and protocol probe.
