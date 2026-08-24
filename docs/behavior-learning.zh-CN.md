# 有界行为学习

`BehaviorLearningExtension` 让长期存在的 NPC 学会可复用的做事方法，但不会把模型变成权限管理员、规则编辑器或代码修改器。它属于可选的 `OpenGameAgent.Extensions`，不会扩大稳定内核的模型/工具循环。

## 权威边界

学习流程刻意设计为不对称：

1. Agent 只能通过 `propose_behavior_learning` **提交候选**；
2. 宿主注入的 `GameBehaviorLearningValidator` 核验游戏拥有的证据；
3. 在默认审核模式下，通过核验的候选仍然不生效；
4. 可信宿主使用当前 session CAS revision 和精确世界边界调用 `ActivateAsync`，或显式启用验证后自动激活；
5. 只有激活的不可变版本会在后续输入中作为普通 `GameSkill` 注入；
6. 宿主记录的评测可以自动降级坏版本，也可以精确恢复旧版本。

学习版本只包含说明、适用输入类型和依赖的工具名，不能注册工具。只有当这些工具经过正常收集和可见性策略后已经存在，动态 Skill 才会进入模型上下文。因此工具策略、批准门禁、持久动作派发和游戏权威规则都不会被学习结果绕过。

宿主通过 `BehaviorLearningOptions.Mode` 选择学习强度：

- `Disabled`：不暴露提议工具、不接受新候选或激活，也不注入已经学习的 Skill；
- `ReviewRequired`：默认的保守模式；候选通过验证后仍需宿主显式调用 `ActivateAsync`；
- `ValidatedAutoActivate`：激进模式；候选通过宿主验证器后立即激活，并替代该行为原有的生效版本。

自动激活改变的是审核节奏，不是权威边界。验证仍然必需，学习版本仍不能增加工具或权限。产品还可以调整验证器、`AllowActorScope`、失败降级阈值和 in-run policy，形成更保守或更激进的策略。

不要让模型自己证明任务成功。校验器应从权威动作 receipt、已提交 input、trace/eval 记录或等价的游戏状态解析证据。瞬时环境故障、尚未解决的失败、一次性世界事实、对工具行为的猜测、密钥、可执行代码，以及任何试图扩大权限的候选都应拒绝。

## 最小接入

```csharp
var learning = new BehaviorLearningExtension(
    boundaryProvider: (input, cancellationToken) =>
        new ValueTask<GameBehaviorWorldBoundary>(new GameBehaviorWorldBoundary(
            input.Moment.TimelineId,
            currentSaveGeneration,
            worldRevision)),
    validator: async (request, cancellationToken) =>
        await receipts.VerifyAllAsync(
            request.Input.SessionId,
            request.Input.ActorId,
            request.Proposal.Evidence,
            cancellationToken),
    options: new BehaviorLearningOptions
    {
        Mode = GameBehaviorLearningMode.ReviewRequired,
    },
    // 可选。省略后，普通 NPC 运行不会多暴露一个工具；
    // 独立审查器通过 ProposeAsync 提交结果。
    inRunPolicy: input => input.Type == "post-task-review");

await using var runtime = new GameAgentBuilder(provider, model)
    .UseSessionStore(sessionStore)
    .UseExecutionScope((input, cancellationToken) =>
        new ValueTask<GameExecutionScope>(CanLearn(input.ActorId)
            ? GameExecutionScope.Restricted(new[]
            {
                GameExecutionCapabilities.BehaviorLearning,
                GameExecutionCapabilities.PersistentPlanning,
            })
            : GameExecutionScope.ShortTaskOnly))
    .UseExtension(learning)
    .Build();
```

一次运行完成并提交后，可信宿主读取并激活候选：

```csharp
var query = await BehaviorLearningExtension.ReadAsync(
    sessionStore,
    new GameSessionKey(sessionId, actorId),
    includeInactive: true,
    cancellationToken);

var candidate = query.Behaviors.Single(value =>
    value.Status == GameLearnedBehaviorStatus.Proposed);

var activation = await learning.ActivateAsync(
    sessionStore,
    query.Session,
    candidate.BehaviorId,
    candidate.Version,
    query.SessionRevision,
    new GameBehaviorWorldBoundary(timelineId, saveGeneration, worldRevision),
    cancellationToken);
```

首次激活时，只要 session revision、时间线、存档代次或世界 revision 与候选创建时不同，就会 fail closed。宿主应重新读取、重新核验，而不是强行启用过期经验。回滚曾经生效过的旧版本仍要求同一时间线和存档代次，并拒绝早于该版本证据的世界 revision，但不要求世界永远停留在最初 revision。

如需低优先级的任务后审查器，可构造类型化 `GameBehaviorLearningProposal`，用已经提交的源 input、当前 session revision、可信世界边界和审查 run ID 调用 `ProposeAsync`。它仍会经过同一校验器和持久化上限，但不会把审查 Prompt 或回复写进 NPC 对话。未提交 input 会被拒绝；同一行为和源 input 的重试返回 `AlreadyExists`，不会新增重复版本。

## 作用域、评测与恢复

默认并推荐 `WorldGeneration`。只有可信边界 Provider 返回同一时间线和存档代次时，这类行为才会被选中，因此废弃存档分支学到的过程不会污染新世界。

`Actor` 可以在同一角色 session 中跨世界 revision 和存档代次使用，但默认关闭（`BehaviorLearningOptions.AllowActorScope = false`）。只有与存档分支无关、确实通用的过程才应开启这一作用域。

版本记录保存在 session 的扩展命名空间里，内存和文件 Store 都能完整 round-trip。候选绑定框架生成的 run ID、input ID、世界边界和证据引用；模型不能自行填写 run ID。并发宿主修改使用 session CAS，发生冲突时返回结构化结果，不覆盖其他运行。

离线测试或权威游戏观察完成后，用 `RecordEvaluationAsync` 写入不含正文的证据引用。成功会清零连续失败数；达到 `BehaviorLearningOptions.ConsecutiveFailuresBeforeDemotion` 后版本立即降级，并从后续模型上下文消失。宿主也可以直接调用 `DemoteAsync` 或 `RejectAsync`。

重新激活被降级或被替代的旧版本就是回滚：恢复的是原来保存的精确 instructions，不会再让模型临场重写一份。已拒绝版本不能激活。无效审计记录会受 `MaximumRetainedInactiveVersions` 限制；当前生效版本和等待处理的候选不会因保留策略被清理。`MaximumVersionsPerBehavior` 是保留上限，不是生命周期创建上限：最旧的非活动版本会为新候选腾出空间，会话级持久版本高水位则保证版本号不会被重用。因此版本号在同一会话内单调递增，但对单个行为不保证连续。

## 明确不做的事

- 不收集私有 reasoning 或隐藏思维链；
- 不隐式启动后台模型请求。游戏可以安排低优先级审查器，通过 `ProposeAsync` 提交类型化结果，并单独核算模型用量；
- 不自动发布全局或共享 Skill。公共能力应走宿主审核的包或内容流水线；
- 不训练模型权重，不生成可执行 Skill，不修改 Provider 凭据；
- 事实、关系、偏好和事件仍属于 Memory；行为学习只保存可复用的过程说明。
