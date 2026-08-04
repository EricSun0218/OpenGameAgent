# Native model providers

`GameAgent.Providers.Native` contains direct streaming adapters for OpenAI
Responses and the Gemini Interactions API. Both adapters translate the same
bounded normalized transcript to the selected wire protocol and translate
provider events back to `ModelStreamEvent`. A provider switch therefore keeps
user text or JSON, assistant output, function calls, function results, usage,
and finish state in one runtime-owned representation.

The adapters intentionally use `store: false` and resend the admitted local
history. They do not depend on a provider-side conversation ID. This keeps
durable recovery and route fallback under runtime control.

The API contracts are documented by the vendors at [OpenAI
Responses](https://platform.openai.com/docs/api-reference/responses) and
[Gemini Interactions](https://ai.google.dev/api/interactions-api-v1).

## OpenAI Responses

```csharp
var options = new OpenAiResponsesProviderOptions
{
    Model = "your-model-id",
    MaxContextTokens = 200_000,
    MaxOutputTokens = 32_768,
    ToolChoice = "auto",
    ParallelToolCalls = true,
    StrictToolSchemas = true,
    // Declare only capabilities verified for this exact model route.
    SupportsReasoningEffort = true
};

await using var built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(journalPath)
    .UseOpenAiResponsesProvider(
        options,
        new StaticNativeApiCredentialSource(apiKey))
    .WithTools(gameTools)
    .Build();
```

The adapter maps Responses message items, function calls,
`function_call_output` items, text and function-argument deltas, usage details,
reasoning summaries, and terminal state. It rejects a requested inference
control when the configured route has not declared support. It does not infer
capabilities from the model name.

## Gemini Interactions

```csharp
var options = new GeminiInteractionsProviderOptions
{
    Model = "your-model-id",
    MaxContextTokens = 1_000_000,
    MaxOutputTokens = 32_768,
    ToolChoice = "auto",
    IncludeThoughtSummaries = true,
    // Declare only capabilities verified for this exact model route.
    SupportsThinkingLevel = true
};

await using var built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(journalPath)
    .UseGeminiInteractionsProvider(
        options,
        new StaticNativeApiCredentialSource(apiKey))
    .WithTools(gameTools)
    .Build();
```

The adapter maps `user_input`, `model_output`, `function_call`, and
`function_result` steps. Streaming text, thought summaries, function arguments,
usage, required actions, and terminal state are normalized to the same runtime
events as other providers.

## Model catalog and capability negotiation

Every built runtime exposes an immutable catalog of its exact configured
routes:

```csharp
var matches = built.Models.Select(new ProviderCapabilityRequirements
{
    ToolCalling = true,
    StructuredInput = true,
    ParallelToolCalls = true,
    ReasoningEffort = true,
    MinimumTools = 32,
    MinimumOutputTokens = 8_192
});
```

`Evaluate` returns every route and stable codes for missing capabilities;
`Select` returns only full matches. A numeric provider limit of zero means
unspecified, never unlimited. Catalog entries contain the exact provider,
model, dialect, capability, and route digests used by durable dispatch.

The current normalized transcript does not claim replay of provider-private
signed reasoning state, so native routes declare `ReasoningInput = false` and
`StatefulContinuation = false`. Reasoning effort and provider-produced summary
deltas remain supported where declared. This prevents a fallback from
pretending that plain text is equivalent to private continuation state.

## Transport and billing boundary

- Remote endpoints require HTTPS; HTTP is accepted only for explicitly enabled
  loopback development endpoints.
- Redirects are rejected. Credentials are acquired after request preparation
  and are never included in wire evidence or route digests.
- Requests, SSE lines, SSE events, event counts, tools, schemas, and output
  limits are bounded. Invalid UTF-8, truncated events, missing terminal events,
  missing terminal usage, and inconsistent tool streams fail closed.
- Exact request bytes receive non-content SHA-256 evidence before dispatch.
- The native adapters report provider token counters but leave total cost
  unavailable because model pricing is mutable and not part of these adapters.
  A game that needs authoritative billing should reconcile it against its
  gateway or provider account.

Do not ship a long-lived commercial provider secret in a client executable.
Use a player-supplied key, short-lived scoped credential, or a game-owned model
gateway. Always run a live conformance gate with the exact model route before a
release; a provider may change model-specific limits independently of this
package.
