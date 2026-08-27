# Artifacts and external knowledge

`@opengameagent/artifacts` keeps large tool output and game-owned knowledge out of the active model context without creating another agent loop.

## Agent artifacts

`createGameArtifactResources` provides two resources that should be installed together:

- an execution middleware that replaces oversized text or JSON tool output with a bounded preview and a stable artifact reference;
- a `read_agent_artifact` tool that reads bounded pages from the exact world, save, timeline, generation, owner, session, and actor that created the artifact.

The included `SqliteGameArtifactStore` is local and self-hosted. Artifact IDs are content-derived and stable for the same input, run, turn, tool call, and result. Retried storage does not create a different artifact. A storage failure after a tool has completed returns the original result instead of asking the model to repeat a potentially state-changing tool.

```ts
import { createGameArtifactResources, SqliteGameArtifactStore } from "@opengameagent/artifacts";

const artifactStore = new SqliteGameArtifactStore("./save/agent-artifacts.db");
const artifacts = createGameArtifactResources({ store: artifactStore });

// Add artifacts.toolProvider to runtime tool providers.
// Add artifacts.execution to the tool execution middleware chain.
```

The store is not a substitute for a game's authoritative world state. It is intended for immutable model-facing output such as reports, inspection results, and retrieved documents.

## External knowledge

`createExternalKnowledgeToolProvider` exposes only sources registered by the host. The model selects a source identifier and a structured query; it cannot supply an endpoint or credential.

```ts
import { createExternalKnowledgeToolProvider, JsonHttpGameKnowledgeSource } from "@opengameagent/artifacts";

const knowledge = createExternalKnowledgeToolProvider({
  artifactStore,
  sources: [
    new JsonHttpGameKnowledgeSource({
      id: "world-lore",
      endpoint: "http://127.0.0.1:7777/query",
    }),
  ],
});
```

Remote HTTP sources require HTTPS; loopback HTTP is allowed for local sidecars. Redirects and URL credentials are rejected. Player input and game context are not forwarded unless the host explicitly enables context forwarding. Responses, item count, metadata, and inline output are bounded. Large results become session-scoped artifacts.

Both resources enforce the session again at execution time, so a tool instance accidentally retained by a host cannot cross actor or save boundaries.
