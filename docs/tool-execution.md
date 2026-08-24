# Tool execution safety and concurrency

The kernel prepares every accepted tool call before dispatch: compatibility argument preparation,
JSON Schema validation, host policy rewriting, final authorization, and conflict-key resolution all
finish first. Execution order and exact-repeat protection therefore use the same arguments that the
executor receives, not the model's untrusted draft.

## Ordered execution epochs

`AgentOptions.ToolExecution` selects the default:

- `Sequential` makes every call an ordered one-call epoch.
- `SafeParallel` overlaps consecutive read-only calls and tools explicitly marked `Parallel`.
  Writes that have not explicitly opted into parallel execution are ordered barriers.
- `Parallel` overlaps consecutive calls unless a tool is explicitly marked `Sequential`.

A sequential call is a barrier, not a switch that serializes the entire model response. All eligible
calls before the barrier settle first, the barrier runs alone, then eligible calls after it may overlap.
`MaxConcurrentTools` applies to every parallel epoch. Completion events retain real completion order,
while canonical tool-result messages remain in model source order.

Conflict keys and uncertain writes remain authoritative safety constraints. Matching keys serialize
inside a parallel epoch. An uncertain keyed write blocks later writes with that key; an uncertain write
without a key blocks every later write in the batch. Cancellation never starts a later epoch after the
current epoch settles.

## Model-visible durable action receipts

`GameActionTool` keeps one complete canonical receipt for the action journal, host events, recovery,
conflict coordination, duplicate detection, and metrics. By default, the same receipt JSON is also sent
to the model for compatibility. A host that does not want authority coordinates or observability data in
model context can pass `modelReceiptProjector` when it creates the tool:

```csharp
var tool = GameActionTool.Create(
    input,
    "build",
    "Build a registered structure.",
    schemaJson,
    dispatcher,
    modelReceiptProjector: context =>
    {
        using var result = JsonDocument.Parse(context.Receipt.ResultJson);
        return JsonSerializer.Serialize(new
        {
            action = context.Intent.Action,
            status = context.Receipt.Status.ToString().ToLowerInvariant(),
            label = result.RootElement.GetProperty("label").GetString(),
            reward = result.RootElement.GetProperty("reward").Clone(),
        });
    });
```

Only the `JsonContent` returned to the model changes. `ToolResult.DetailsJson` and the durable
`GameActionReceipt` remain canonical, so the host still receives operation identity, state revision,
timeline, dispatch/replay/recovery state, and timing. The trusted host projector receives only the stable
intent and receipt; replay-only disposition and timing are deliberately absent. Keep the projector
deterministic for the same inputs.

Custom projections must be an unambiguous JSON object no longer than
`GameActionTool.MaximumModelReceiptCharacters`. If the projector throws or returns null, invalid,
non-object, or oversized JSON, the tool returns a fixed, terminating `projection_failed` result. A batch
closed by that result makes no further model request. It never falls back to the canonical receipt. The
authoritative action may already have committed, so inspect the canonical receipt rather than retrying a
world write from UI error handling.

## Exact-repeat loop protection

The kernel fingerprints the tool name and deep-canonicalized prepared JSON arguments without exposing
the arguments in telemetry. Consecutive matches are tracked for one run across model turns. By default:

- repeat 3 emits `AgentEventKind.ToolRepeatDetected` with an `Advisory` action and appends one bounded
  `agent_policy` message for the next model request;
- repeat 8 emits `Terminated`, returns an error result without dispatching that call, and ends the loop.

Configure these boundaries with
`AgentLimits.ExactToolRepeatAdvisoryThreshold` and
`AgentLimits.ExactToolRepeatTerminationThreshold`. Zero disables the corresponding action. A genuine
steering or follow-up message resets the sequence because the model has received new evidence.

Some observation tools intentionally poll the same state. Construct those tools with
`trackExactRepeats: false`. An exempt call neither advances nor resets another tracked sequence; global
turn, tool-call, timeout, and token limits still apply. Do not exempt a state-changing tool merely to
silence a faulty loop.

Tracing records `kernel.toolrepeatdetected` without arguments. `GameAgentPerformanceSummary` exposes
per-run and aggregate advisory and termination counts. Tool lifecycle events, including repeat policy
events, remain internal under the stock audience policy.
