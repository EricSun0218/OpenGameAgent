# Traces, performance, benchmarks, and offline evaluation

[中文](devtools.zh-CN.md)

`@opengameagent/devtools` is an optional observation package for `@opengameagent/runtime`. It does not sit in the model/tool loop and cannot authorize or execute game actions.

## Record a run

```ts
import {
  GameRuntimeTraceObserver,
  JsonLinesGameTraceSink,
} from "@opengameagent/devtools";
import { GameAgentRuntime } from "@opengameagent/runtime";

const traceSink = new JsonLinesGameTraceSink("traces/session-001.jsonl");
const traceObserver = new GameRuntimeTraceObserver(traceSink);
const runtime = new GameAgentRuntime({
  kernel,
  baseSystemPrompt: "Act only through registered game tools.",
  defaultModelProfileId: "default",
  observer: traceObserver,
});

const actions = new DurableGameActionDispatcher(journal, executor, {
  observer: traceObserver,
});

for await (const event of runtime.run(input)) {
  render(event);
}
await traceSink.close();
```

The default projection records correlation IDs, lifecycle kinds, audience, model identity, usage, safe error categories, character counts, tool names, tool success, and bounded timings. It excludes:

- input content and context;
- message text;
- tool arguments, progress payloads, results, and details;
- provider failure messages and response bodies;
- credentials and hidden reasoning.

`includeVisibleText` is an explicit local-debug opt-in. Internal-audience text remains excluded even when this option is enabled. Each record and queue is bounded. Trace failures are isolated from Agent execution.

When continuing an existing JSONL file after a process restart, read its last record and pass that sequence as `initialSequence`; the reader rejects duplicate or decreasing sequences.

## Timing and usage

The runtime observer separates:

- actor queue time;
- total turn preparation;
- each named context, post-tool context, and tool provider;
- tool-catalog construction and schema preflight;
- model-profile selection;
- runtime event-store and usage-ledger writes;
- each tool execution and total run duration.

`summarizeGamePerformance(recording)` derives per-run and aggregate TTFT, first-tool latency, tool duration/failure rate, provider/model grouping, tokens, reasoning/cache usage, and known or unknown cost. Passing the same observer to `DurableGameActionDispatcher` also records framework versus authoritative-host time, uncertain writes, reconcile requirements, conflict blocking, and duplicate-write prevention without recording action arguments or results. Unknown prices remain `null`; they are never reported as zero.

```ts
import { readGameTraceRecording, summarizeGamePerformance } from "@opengameagent/devtools";

const recording = await readGameTraceRecording("traces/session-001.jsonl");
const summary = summarizeGamePerformance(recording);
console.log(summary.runLatency.p95, summary.timeToFirstOutput.p95);
```

Event timestamps measure the provider-facing interval from `turn.started` to the first visible message or tool call. Named stage observations isolate framework work around that interval. Provider adapters and authoritative action handlers can add their own safe records to the same sink when deeper network/host attribution is needed.

## Deterministic benchmarks

`runGameBenchmark` accepts a caller-owned scenario. The scenario can create a runtime with a fixed or fake provider and deterministic tools. Warmups, bounded concurrency, iteration timeout, fault injection, and thresholds are built in.

```ts
const report = await runGameBenchmark(
  {
    name: "npc-tool-loop",
    run: async ({ iteration, signal }) => runFixture(iteration, signal),
  },
  {
    warmupIterations: 2,
    iterations: 50,
    concurrency: 4,
    iterationTimeoutMilliseconds: 30_000,
    thresholds: {
      maximumP95RunMilliseconds: 2_000,
      maximumToolFailureRate: 0.01,
      maximumFailedIterations: 0,
    },
  },
);
```

Reports can be written as JSON or JSONL, or formatted as bounded human-readable text. A timeout still completes when a faulty scenario ignores its cancellation signal; the signal is also aborted so cooperative providers and tools can stop their work.

## Offline evaluation and replay

`evaluateGameTrace` provides bounded run-duration, TTFT, tool-failure, unknown-cost, required-event, forbidden-event, and forbidden-tool checks. Custom rules receive an independent timeout signal and cannot hang the evaluation result.

`replayGameTrace` replays immutable observations to a callback. It never calls a provider, executes a tool, dispatches a durable action, restores a save, or writes game state. Durable journals and authoritative receipts remain the only source of truth for action recovery.

## Security and retention

- Treat trace files as private game telemetry even with the default projection.
- Keep visible-text capture disabled outside controlled local debugging.
- Apply game-owned access, retention, and deletion policies.
- Use game time and timeline/generation coordinates for narrative order; operational timestamps are only performance evidence.
- Do not infer that a world mutation committed from a trace. Reconcile the durable action journal and receipt.
