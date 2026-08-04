# OpenGameAgent packages

This package is one module of the OpenGameAgent .NET SDK. For a complete
in-engine runtime, start with:

```shell
dotnet add package GameAgent.Runtime --version 0.2.0-alpha.1
```

Choose a narrower package only when you are building a custom composition:

| Package | Purpose |
| --- | --- |
| `GameAgent.Runtime` | Recommended composition entry point |
| `GameAgent.Core` | Agent loop, tools, skills, context, control, and scheduling |
| `GameAgent.Evaluation` | Deterministic gameplay-quality evidence scoring |
| `GameAgent.Generation` | Durable image, video, speech, and structured-content jobs |
| `GameAgent.Persistence` | Crash-tolerant journal, operation ledger, and local memory store |
| `GameAgent.Protocol` | Versioned host and wire contracts |
| `GameAgent.Providers.OpenAICompatible` | Streaming chat-completions provider adapter |
| `GameAgent.Providers.Anthropic` | Native Anthropic Messages streaming provider adapter |
| `GameAgent.Providers.Native` | Native OpenAI Responses and Gemini Interactions streaming adapters |
| `GameAgent.Providers.MediaHttp` | Local or remote HTTP transport for generated content |
| `GameAgent.Remote.Client` | Godot/Unity-compatible remote action connector |
| `GameAgent.Simulation` | Deterministic living-world activation and admission |
| `GameAgent.Workflow` | Optional bounded, durable workflow composition |
| `GameAgent.Testing` | Deterministic test doubles and contract helpers |
| `GameAgent.Hosting` | .NET 8 lifecycle, tenant admission, and remote action bridge |
| `GameAgent.Storage.Relational` | Provider-neutral .NET 8 relational journal foundation |
| `GameAgent.Storage.Sqlite` | .NET 8 relational journal for one local process |
| `GameAgent.Storage.Postgres` | .NET 8 relational journal for multi-instance services |
| `GameAgent.Observability.OpenTelemetry` | .NET 8 metrics and tracing export |

All pre-1.0 modules use exact lockstep dependencies. Upgrade their versions
together and rerun your engine integration tests.

Start with the
[getting-started guide](https://github.com/EricSun0218/OpenGameAgent/blob/main/docs/getting-started.md).
See the
[architecture](https://github.com/EricSun0218/OpenGameAgent/blob/main/docs/architecture.md),
[protocol](https://github.com/EricSun0218/OpenGameAgent/blob/main/docs/protocol.md),
[engine compatibility](https://github.com/EricSun0218/OpenGameAgent/blob/main/docs/compatibility.md),
and
[security policy](https://github.com/EricSun0218/OpenGameAgent/blob/main/SECURITY.md)
for the complete integration contract.

The source and Apache-2.0 license are available in the
[repository](https://github.com/EricSun0218/OpenGameAgent).
