# MCP and portable plugins

OpenGameAgent keeps external tools and portable plugin packages outside the Agent kernel. They are optional resources composed by the host, so a game can use native tools only, connect one trusted MCP server, or install reusable Skill/MCP packages without changing the message/tool loop.

## MCP tool bridge

`@opengameagent/mcp` provides `GameMcpToolBridge`, a `GameToolProvider` that uses the runtime's existing collection and execution path. External tools therefore still pass through input-aware visibility, schema preflight, tool policy, approval middleware, cancellation, tracing, and any durable game-action adapter selected by the host.

The default `on-demand` exposure publishes one bounded `use_external_game_tool` proxy. The model searches the external catalog, describes an exact tool, and then calls it. This prevents a large or changing catalog from consuming every model request. `direct` exposure is available for small trusted catalogs.

```ts
import { connectHttpGameMcp, GameMcpToolBridge } from "@opengameagent/mcp";

const externalTools = new GameMcpToolBridge({
  servers: [
    {
      id: "world-tools",
      connect: () => connectHttpGameMcp({
        endpoint: "https://tools.example.com/mcp",
        headers: { Authorization: `Bearer ${credential}` },
      }),
      isVisible: input => input.type === "npc.chat",
    },
  ],
});

// Add externalTools to GameAgentRuntimeOptions.toolProviders.
```

Remote schemas are compiled before they can be advertised. Unsupported schemas are excluded and reported through bounded diagnostics. Catalog refresh is generation-safe; a list-change invalidates the current snapshot, and a closed connection is replaced on the next collection. Tool calls are never automatically retried because an external call may already have changed state.

HTTP endpoints require HTTPS, except explicitly trusted loopback HTTP. Redirects are rejected. Credentials remain host-owned transport configuration. Stdio uses an explicit executable plus argument array and never invokes a shell.

## Portable plugin packages

`@opengameagent/plugins` loads the published Agent Plugins 1.0.0 directory format: `plugin.json`, immediate child Skills under `skills/`, and optional MCP configuration in `mcp.json`.

```ts
import { loadPortableGamePlugin } from "@opengameagent/plugins";
import { createGameSkillExtension } from "@opengameagent/skills";

const plugin = await loadPortableGamePlugin("./installed/world-tools", {
  dataDirectory: "./plugin-data",
  httpHeaders: {
    remote: { Authorization: `Bearer ${credential}` },
  },
});

const skillResources = plugin.skills
  ? createGameSkillExtension({ source: plugin.skills })
  : undefined;

// Compose plugin.mcp and skillResources?.toolProvider into toolProviders.
// Compose skillResources?.postToolContextProvider into postToolContextProviders.
```

Package files cannot inject credentials. Host headers override package headers. A stdio component is enabled only when the host supplies persistent plugin data storage. Package paths are contained inside the resolved package or plugin-data root, components are bounded, and invalid Skill or MCP entries are isolated without hiding diagnostics.

Portable packages contain declarative Skills and MCP connection descriptions. They do not execute arbitrary JavaScript during discovery. Developers who need code extensions publish ordinary TypeScript packages implementing `GameContextProvider`, `GameToolProvider`, `GamePostToolContextProvider`, policy/middleware contracts, or other optional OGA interfaces; the game host explicitly imports and composes trusted code.

## Ownership boundary

- OpenGameAgent owns discovery, bounded validation, protocol adaptation, model-safe projection, and integration with the normal runtime pipeline.
- The host owns which packages and servers are trusted, credentials, process/network permission, per-input visibility, and authoritative game tools.
- An MCP tool is not automatically a durable game action. World-changing behavior should expose or call the host's durable action adapter so operation IDs, receipts, conflict coordination, and recovery remain authoritative.
