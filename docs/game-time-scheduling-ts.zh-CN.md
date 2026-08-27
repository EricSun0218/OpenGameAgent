# 游戏时间信号与调度

`@opengameagent/scheduling` 负责把宿主权威的游戏时间推进转换成持久、可恢复的 Agent 输入。它不会使用现实时间计时器：游戏时间由宿主推进，游戏暂停时调度器也不会暗中运行。

## 契约

- 每条日程绑定完整 `GameSessionKey`，包括时间线与存档代次。
- 支持一次性日程和按游戏 tick 固定周期重复的日程。
- `advance` 在一个事务中生成所有到期 occurrence，并推进对应日程。
- 重复提交相同 advance ID 只会返回同一批 occurrence，不会生成重复信号。
- occurrence 使用租约。若进程在 `complete` 前崩溃，租约到期后仍领取原 occurrence，不会产生新的信号 ID。
- 不同角色、存档、时间线和代次严格隔离。
- 创建、推进、领取和完成日程都不会调用模型；宿主仍可根据相关性和预算决定是否唤醒 Agent。

```ts
using schedules = new SqliteGameTimeScheduler("world-ai.db");

await schedules.schedule({
  id: "monthly-life",
  session,
  kind: "world.monthly-life",
  payload: { source: "calendar" },
  due: { tick: 30 },
  intervalTicks: 30,
});

await schedules.advance(
  {
    id: "calendar-step-12",
    session,
    fromExclusive: { tick: 29 },
    toInclusive: { tick: 60, calendar: "第一年二月" },
  },
  128,
);

for (const delivery of await schedules.claim(session, 16, Date.now(), 30_000)) {
  const input = gameSignalToInput(delivery.occurrence);
  // 先应用宿主的相关性与预算策略，需要时再交给 runtime。
  await schedules.complete(session, delivery.occurrence.id, delivery.leaseToken);
}
```

调度器是可靠 outbox，不承诺游戏副作用天然 exactly-once。消费者应使用稳定 occurrence ID 做幂等；权威世界写入仍必须经过 durable game action 与 receipt。

已完成 occurrence、终态日程和 advance 幂等记录都采用可配置的有界保留。超过保留窗口后再次使用旧 advance ID，不再属于幂等保证范围。
