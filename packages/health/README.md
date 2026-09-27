# `@opengameagent/health`

Optional, bounded dependency inspection and readiness aggregation for OpenGameAgent hosts. It performs one host-requested inspection wave and does not poll, retry, restart services, or run another Agent loop.

OpenGameAgent 宿主可选的有界依赖检查与就绪聚合。它只执行一次由宿主发起的检查，不轮询、不重试、不重启服务，也不创建第二个 Agent loop。

```ts
import { RuntimeHealthMonitor } from "@opengameagent/health";

const health = new RuntimeHealthMonitor({
	dependencies: [
		{
			id: "game-model",
			kind: "model",
			requirement: "required",
			inspect: async (signal) => {
				await inspectConfiguredModel(signal);
				return { state: "ready" };
			},
		},
	],
	limits: {
		maxDependencies,
		maxDependencyIdBytes,
		maxConcurrentInspections,
		inspectionTimeoutMs,
	},
});

const snapshot = await health.inspect(signal);
```

For dependencies whose only trustworthy evidence comes from real game traffic, a host or Adapter can expose one
explicitly bounded passive observation instead of issuing a synthetic request:

```ts
import { RuntimePassiveHealthDependency } from "@opengameagent/health";

const modelEvidence = new RuntimePassiveHealthDependency({
	id: "game-model-traffic",
	kind: "provider",
	requirement: "required",
	maxObservationAgeMs: hostSelectedEvidenceLifetime,
	now: hostMonotonicClock,
});

modelEvidence.observe({ state: "ready" }); // only after a real successful operation
const definitions = [modelEvidence.definition];
```

The passive definition starts as `declared` and stores at most one normalized observation. A fresh observation is
returned exactly; a future-dated or expired observation becomes `available` with reason `stale`. Observing `declared`
clears it. Reading the definition never contacts the dependency. The host must supply both the clock and the positive
safe-integer evidence lifetime; the package supplies no freshness default.

`declared` means configuration only, `available` means a transport or metadata path is reachable, `ready` proves the exact advertised capability, `degraded` remains usable with a known impairment, and `unavailable` has no serving route. A required dependency must be `ready` or `degraded`; optional failures never make the base Runtime not-ready.

Every numeric value is mandatory host policy. An inspector performs one read-only operation and must honor its `AbortSignal`. The monitor adds no polling interval, retry policy, cache, TTL, startup grace, health threshold, fallback selection, or restart behavior. `RuntimePassiveHealthDependency` is the only optional retained-observation component; its single observation and host-selected freshness boundary are explicit in the constructor above. Dependency IDs are returned to authenticated operators by the Runtime integration, so hosts must use stable non-secret labels rather than URLs, credentials, or provider account names.

`declared` 仅表示已经配置，`available` 表示传输或元数据路径可达，`ready` 表示已证明精确能力，`degraded` 表示仍可服务但存在已知降级，`unavailable` 表示没有可用路线。必选依赖必须为 `ready` 或 `degraded`；可选依赖失败不会让基础 Runtime 变成未就绪。

所有数字都必须由宿主提供。每个 inspector 只执行一次只读操作，并且必须响应 `AbortSignal`。monitor 不提供轮询周期、重试、缓存、TTL、启动宽限、阈值、fallback 选择或重启策略。`RuntimePassiveHealthDependency` 是唯一可选的保留观察组件；它只保存一条观察，且新鲜度边界必须由宿主显式提供。依赖 ID 会通过 Runtime 集成返回给已认证的运维方，因此宿主只能使用稳定且不含秘密的标签，不能写 URL、凭据或供应商账户名。
