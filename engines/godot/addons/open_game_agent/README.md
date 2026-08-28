# OpenGameAgent Client for Godot

This Godot 4 .NET addon projects a separately hosted OpenGameAgent runtime onto Godot's main thread. It does not embed or duplicate the Agent loop.

`OpenGameAgentNode` streams run and durable-action events, exposes exact run/turn control, and keeps callbacks bounded. The game stays authoritative: validate and apply typed action intents in Godot, then submit a durable receipt through `Client`.

The packaged addon includes `OpenGameAgent.EngineClient.dll`. Remote services require HTTPS; plaintext HTTP is limited to loopback. Authentication is supplied separately from model-visible `GameInput`.
