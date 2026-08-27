# Structured player interactions

`@opengameagent/interactions` is an optional tool provider for AI characters that need a player decision. It keeps presentation outside the Agent loop: the model creates a bounded set of questions and choices, while a game-owned `GameInteractionBroker` renders them in Unity, Godot, Unreal, a web UI, or a headless client and returns the player's answer.

Each question supports two to eight choices, optional custom text, optional multi-select, and at most one recommended option with an explanation. A single call may group up to eight related questions. Requests carry an immutable session/input/run/turn/tool-call identity, and the generated request ID is stable for an identical replay.

```ts
import { createStructuredGameInteractionToolProvider } from "@opengameagent/interactions";

const interactions = createStructuredGameInteractionToolProvider({
  broker: {
    async prompt(request, signal) {
      return await gameUi.askPlayer(request, signal);
    },
  },
});

const runtime = new GameAgentRuntime({
  // ...
  toolProviders: [interactions],
});
```

The tool is sequential and marked as medium risk, so the host may additionally govern it with the ordinary tool policy or approval middleware. Model arguments and broker responses are both validated. The broker must answer every question or cancel the whole interaction; unknown choices, duplicate answers, invalid custom text, ambiguous recommendations, and oversized payloads fail closed.

This package provides a UI-neutral contract, not a game screen. Games decide when the tool is visible and how questions, recommended replies, timeouts, accessibility, or controller input are presented.
