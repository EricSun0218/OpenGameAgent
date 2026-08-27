# `@opengameagent/evolution`

Optional, self-hosted behavior learning for game agents. The package turns completed, model-visible outcomes into structured reflections and validated composite skills without creating a second agent loop or bypassing the game's tools.

## How it works

1. Build a bounded `agent.reflection` input with `createGameBehaviorReviewInput`. It copies the authoritative session and game moment, but it does not copy the original prompt or hidden reasoning.
2. Run that input through the same `GameAgentRuntime`. The extension advertises `record_game_reflection` and `propose_game_behavior_skill` only for configured reflection input types.
3. Every proposed step must name a tool currently exposed by the host. A host-supplied `GameBehaviorSkillValidator` checks the proposal and its evidence before it is stored.
4. Validated versions are immutable and bounded. `conservative` mode requires explicit activation. `aggressive` mode may activate actor-local skills automatically; world-scoped activation remains explicit unless the host opts in.
5. `EvolvedGameSkillSource` feeds active versions into the normal progressive skill loader. Executing the skill still uses the ordinary tool policy, approval middleware, action journal, receipts, and game authority.

Actor-scoped and world-scoped learning are separate. World scope includes world, save, timeline, generation, and owner, so loading a save or changing owner cannot inherit an unrelated learned behavior. Use `CompositeGameSkillSource` to combine developer-authored and learned skills.

The public controller can activate, retire, or roll back to an older validated version. Replayed reflection inputs and proposals are idempotent; changed content under the same input identity fails closed.

## 中文

`@opengameagent/evolution` 是一个可选、可自托管的 NPC 行为学习扩展。它把已完成任务中模型可见的结果整理成结构化复盘，再生成经过验证、可版本化和可回滚的复合技能；不会创建第二套 Agent 循环，也不会绕过游戏已有的工具权限。

- `off`：完全关闭，不向模型暴露学习工具。
- `conservative`：保存验证通过的候选版本，但必须由宿主明确启用。
- `aggressive`：可自动启用当前 NPC 的技能；跨 NPC 的世界级技能默认仍需宿主明确启用。
- 复合技能只能组合宿主当次确实开放的工具。技能执行仍经过原有工具策略、批准门禁、可靠动作、回执与游戏权威校验。
- NPC 私有技能与世界共享技能分别存储；世界范围同时绑定存档、时间线、generation 和 owner，读档不会误继承旧世界能力。
- 结构化复盘不接收或保存隐藏思维链。宿主只传入允许模型看到的结果摘要和证据。
