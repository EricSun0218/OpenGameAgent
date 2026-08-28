# OpenGameAgent Client for Unreal Engine

The Unreal plugin is a native C++ transport and game-thread projection for a separately hosted OpenGameAgent runtime. It does not embed Node.js or reproduce the Agent loop in C++.

`UOpenGameAgentSubsystem` supports streamed runs, durable action delivery, generic JSON endpoints, and exact run/turn steer, follow-up, and abort. Unity, Godot, Unreal, and server games therefore share the same Agent behavior and wire contracts while each game remains authoritative for world writes.

Remote services require HTTPS; plaintext HTTP is limited to loopback. Authentication is attached beside the model-visible input. The SSE decoder is byte-bounded and uses Unreal's response streaming callback, then projects events onto the game thread.
