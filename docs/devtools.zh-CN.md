# 轨迹、性能、基准测试与离线评测

[English](devtools.md)

`@opengameagent/devtools` 是 `@opengameagent/runtime` 的可选观测包。它不位于模型/工具循环内，也不能授权或执行游戏动作。

## 记录一次运行

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
  baseSystemPrompt: "只能通过已注册的游戏工具行动。",
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

默认投影只记录关联 ID、生命周期类型、audience、模型身份、用量、安全错误分类、字符数、工具名、工具成功状态和有界耗时。默认不会记录：

- 输入正文与上下文；
- 消息正文；
- 工具参数、进度 payload、结果与 details；
- Provider 失败正文和响应 body；
- 凭证与隐藏推理。

`includeVisibleText` 只用于显式开启本地调试。即使开启，`internal` audience 的文本仍不会写入轨迹。单条记录和写入队列都有上限；轨迹故障与 Agent 执行隔离。

进程重启后若要继续追加同一个 JSONL 文件，应先读取最后一条记录，并把其序号传给 `initialSequence`；Reader 会拒绝重复或倒退的序号。

## 耗时与用量

Runtime Observer 可以区分：

- Actor 排队；
- 每轮准备总耗时；
- 每个具名 Context、Post-tool Context 与 Tool Provider；
- 工具目录构建与 Schema 预检；
- 模型配置选择；
- Runtime 事件存储与用量账本写入；
- 每次工具执行和整次 Run。

`summarizeGamePerformance(recording)` 会生成单次和聚合的 TTFT、首次工具调用、工具耗时/失败率、Provider/模型分组、Token、推理与缓存用量，以及已知或未知费用。把同一个 Observer 传给 `DurableGameActionDispatcher` 后，还会记录框架与权威宿主耗时、未知写入、对账请求、冲突阻塞和重复写入拦截，但不会保存动作参数或结果。没有价格时费用保持 `null`，不会伪装成零。

```ts
import { readGameTraceRecording, summarizeGamePerformance } from "@opengameagent/devtools";

const recording = await readGameTraceRecording("traces/session-001.jsonl");
const summary = summarizeGamePerformance(recording);
console.log(summary.runLatency.p95, summary.timeToFirstOutput.p95);
```

事件时间戳用于测量从 `turn.started` 到首个可见消息或工具调用的 Provider-facing 区间；具名 Stage 用于分离其前后的框架工作。若需要进一步拆分网络或游戏宿主耗时，Provider Adapter 与权威动作处理器可以向同一个 Sink 写入额外的安全记录。

## 确定性基准测试

`runGameBenchmark` 接受调用方定义的 Scenario。Scenario 可以使用固定或 Fake Provider 与确定性工具；Runner 内置预热、有界并发、单轮超时、故障注入和阈值。

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

报告可输出 JSON、JSONL 或有界纯文本。即使错误 Scenario 忽略取消信号，超时也会按时返回；同时 Runner 会中止该信号，使正常配合取消的 Provider 和工具停止工作。

## 离线评测与回放

`evaluateGameTrace` 提供 Run 耗时、TTFT、工具失败率、未知费用、必需事件、禁用事件和禁用工具等有界规则。自定义规则会收到独立的超时信号，不能卡死评测结果。

`replayGameTrace` 只把不可变观测记录交给回调。它不会调用 Provider、执行工具、派发可靠动作、恢复存档或写入游戏状态。动作恢复必须以 durable journal 和权威 receipt 为准。

## 安全与保留

- 即使使用默认投影，也应把轨迹文件视为私密游戏遥测。
- 除受控本地调试外，不要开启消息正文捕获。
- 访问、保留和删除策略由游戏宿主负责。
- 叙事顺序应使用游戏时间及 timeline/generation 坐标；运行时间戳只作为性能证据。
- 不能根据轨迹推断世界写入已经提交；必须核对可靠动作日志与回执。
