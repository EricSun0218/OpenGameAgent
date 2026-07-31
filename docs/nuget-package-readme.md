# Game Agent Runtime packages

This package is one module of the Game Agent Runtime .NET SDK. For a complete
in-engine runtime, start with:

```shell
dotnet add package GameAgent.Runtime --version 0.1.0-alpha.1
```

Choose a narrower package only when you are building a custom composition:

| Package | Purpose |
| --- | --- |
| `GameAgent.Runtime` | Recommended composition entry point |
| `GameAgent.Core` | Agent loop, tools, skills, context, control, and scheduling |
| `GameAgent.Persistence` | Crash-tolerant journal, operation ledger, and local memory store |
| `GameAgent.Protocol` | Versioned host and wire contracts |
| `GameAgent.Providers.OpenAICompatible` | Streaming chat-completions provider adapter |
| `GameAgent.Providers.Anthropic` | Native Anthropic Messages streaming provider adapter |
| `GameAgent.Workflow` | Optional bounded, durable workflow composition |
| `GameAgent.Testing` | Deterministic test doubles and contract helpers |

All pre-1.0 modules use exact lockstep dependencies. Upgrade their versions
together and rerun your engine integration tests.

Start with the
[getting-started guide](https://github.com/EricSun0218/game-agent-runtime/blob/main/docs/getting-started.md).
See the
[architecture](https://github.com/EricSun0218/game-agent-runtime/blob/main/docs/architecture.md),
[protocol](https://github.com/EricSun0218/game-agent-runtime/blob/main/docs/protocol.md),
[engine compatibility](https://github.com/EricSun0218/game-agent-runtime/blob/main/docs/compatibility.md),
and
[security policy](https://github.com/EricSun0218/game-agent-runtime/blob/main/SECURITY.md)
for the complete integration contract.

The source and Apache-2.0 license are available in the
[repository](https://github.com/EricSun0218/game-agent-runtime).
