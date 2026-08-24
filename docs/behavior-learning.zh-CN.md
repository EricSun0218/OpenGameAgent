# NPC 行为学习与自进化

`BehaviorLearningExtension` 让长期存在的 NPC 学会可复用的做事方法，但不会把模型变成权限管理员、规则编辑器或代码修改器。另一个独立安装的 `SharedBehaviorCatalogExtension` 允许宿主发布经过验证的不可变过程，并让符合条件的 NPC 显式采用。两者都是可选的 `OpenGameAgent.Extensions` 能力，不会扩大稳定内核的模型/工具循环。

## 权威边界

学习流程刻意设计为不对称：

1. Agent 只能通过 `propose_behavior_learning` **提交候选**；
2. 宿主注入的 `GameBehaviorLearningValidator` 核验游戏拥有的证据；
3. 在默认审核模式下，通过核验的候选仍然不生效；
4. 可信宿主使用当前 session CAS revision 和精确世界边界调用 `ActivateAsync`，或显式启用验证后自动激活；
5. 只有激活的不可变版本会在后续输入中作为普通 `GameSkill` 注入；
6. 宿主记录的评测可以自动降级坏版本，也可以精确恢复旧版本。

学习版本包含说明、必需的 `GameBehaviorReflection`、有序的 `GameBehaviorStep`、适用输入类型和依赖工具名。Reflection 明确记录观察、策略、结果、适用条件和已知失败方式，不是隐藏思维链。每个步骤只能引用提案中声明的工具依赖，不能在 Runtime 背后注册或直接执行工具。只有全部依赖当前已经注册且对该输入可见时，最终 `GameSkill` 才会进入上下文并指导正常 ReAct 循环组合这些工具；工具策略、批准、Schema 校验、持久动作派发和游戏权威都不会被绕过。

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

```csharp
var proposal = new GameBehaviorLearningProposal(
    "safe-resource-route",
    "安全资源路线",
    "复用已验证路线；前提失效时立即停止。",
    GameLearnedBehaviorScope.WorldGeneration,
    new GameBehaviorReflection(
        observation: "权威观察确认直线路线存在危险。",
        strategy: "改走已经检查的替代路线。",
        outcome: "角色抵达目标，动作 receipt 已提交。",
        applicability: "只在替代路线仍可通行时使用。",
        failureModes: new[] { "后续世界变化可能阻断替代路线。" }),
    evidence: new[] { new GameBehaviorEvidence("action-receipt", operationId) },
    inputTypes: new[] { "npc.travel" },
    toolNames: new[] { "move_to" },
    steps: new[]
    {
        new GameBehaviorStep("move-alternate", "move_to", "沿经过验证的替代路线移动。"),
    });
```

## 个体学习与通用行为

个人版本保存在 `(sessionId, actorId)` 的会话状态中。共享是另一项宿主操作，模型没有发布或采用工具。只有 `GameSharedBehaviorPublicationValidator` 通过后，`SharedBehaviorCatalogExtension.PublishAsync` 才会把一个已激活的不可变定义写入 `IGameSharedBehaviorStore`。目录支持由宿主定义的 `Game`、`WorldGeneration`、`Role` 和 `Faction` 受众。

来源 `BehaviorId` 和版本只在一个角色 session 内有效，因此每次发布还必须提供由宿主分配、在目录中稳定的 `BehaviorFamilyId` 与单调递增的 `FamilyVersion`。共享升级和回滚以这两个值为准，不能让 NPC 自己决定。发布校验器会收到 publication ID、family ID 和 family version，可拒绝倒退或未授权的版本谱系；所有内置存储还会原子地把每个 `(BehaviorFamilyId, FamilyVersion)` 绑定到唯一 publication 与内容哈希。显式采用较旧的 family version 就是共享技能回滚，并会替代该 NPC 在同一 family 中当前生效的版本；不同 NPC 即使拥有相同的本地行为 ID，只要 family ID 不同就不会碰撞。

发布只意味着**可发现**，不意味着自动生效。`DiscoverAsync` 返回符合条件的记录；`AdoptAsync` 重新核对可信世界边界、受众成员关系、该 NPC 的 `GameSharedBehaviorAdoptionValidator` 和 session CAS，之后才记录这个 NPC 的采用状态。只有仍未撤销、内容哈希不变、受众仍匹配且依赖工具当前可见的采用版本，才会进入模型上下文。

```csharp
var shared = new SharedBehaviorCatalogExtension(
    sharedBehaviorStore,
    boundaryProvider,
    audienceProvider: (input, cancellationToken) =>
        new ValueTask<IReadOnlyList<GameSharedBehaviorAudience>>(new[]
        {
            new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, GetRole(input.ActorId)),
            new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Faction, GetFaction(input.ActorId)),
        }),
    publicationValidator: ValidateForSharingAsync,
    adoptionValidator: ValidateForActorAsync);

var publication = await shared.PublishAsync(
    sessionStore,
    sourceSession,
    behaviorId: "build-with-light",
    behaviorVersion: 4,              // 来源 session 的版本
    behaviorFamilyId: "safe-house", // 宿主目录身份
    familyVersion: 2,                // 宿主目录版本
    expectedSessionRevision: expectedSessionRevision,
    publicationId: "safe-house-v2-review-17",
    audience: new GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind.Role, "builder"),
    boundary: boundary,
    auditReference: "publication-review-17",
    cancellationToken: cancellationToken);

await using var runtime = new GameAgentBuilder(provider, model)
    .UseSessionStore(sessionStore)
    .UseExtension(learning) // 可选：NPC 个体学习
    .UseExtension(shared)   // 可选：通用行为目录
    .Build();
```

测试可使用 `InMemoryGameSharedBehaviorStore`，需要崩溃恢复的本地游戏使用 `FileGameSharedBehaviorStore`。文件 Store 维护可恢复的哈希分片受众索引、校验有序 publication ID 的受众清单和技能族版本保留记录；正常发现只读取匹配目录分区，不反序列化无关发布。技能族保留记录是插入的线性化点：并发读取要么看到旧状态，要么先协调待恢复事务再暴露新的不可变发布。缺失的派生受众索引会在目录租约内根据已提交记录重建；同数量 ID 被篡改、错误跨受众映射或其他不一致都会失败关闭。`RevokeAsync` 会让发布从未来 run 和后续 Skill 选择中消失，但不会改写已经发出的模型请求，也不篡改每个 NPC 的历史记录。采用后的效果评测相互隔离：连续失败只会暂停该 NPC 的采用，其他 NPC 不受影响；只有自己的证据或宿主撤销才会停止。再次采用同一个被暂停的精确发布版本，是显式的恢复决定；宿主也可以撤回生效或暂停中的采用并释放容量。`MaximumAdoptionsPerActor` 限制生效和暂停中的采用，`MaximumRetainedInactiveAdoptions` 独立限制已撤回和已替代的审计记录，绝不会清理生效或暂停中的记录。`MaximumDiscoverableBehaviors` 限制返回数量，`MaximumCatalogRecordsScannedPerDiscovery` 会同时计入已发布和已撤销记录，并独立限制跨分页进行世界边界筛选时最多检查的目录记录数。

## 作用域、评测与恢复

默认并推荐 `WorldGeneration`。只有可信边界 Provider 返回同一时间线和存档代次时，这类行为才会被选中，因此废弃存档分支学到的过程不会污染新世界。

`Actor` 可以在同一角色 session 中跨世界 revision 和存档代次使用，但默认关闭（`BehaviorLearningOptions.AllowActorScope = false`）。只有与存档分支无关、确实通用的过程才应开启这一作用域。

版本记录保存在 session 的扩展命名空间里，内存和文件 Store 都能完整 round-trip。候选绑定框架生成的 run ID、input ID、世界边界和证据引用；模型不能自行填写 run ID。并发宿主修改使用 session CAS，发生冲突时返回结构化结果，不覆盖其他运行。

离线测试或权威游戏观察完成后，用 `RecordEvaluationAsync` 写入不含正文的证据引用。成功会清零连续失败数；达到 `BehaviorLearningOptions.ConsecutiveFailuresBeforeDemotion` 后版本立即降级，并从后续模型上下文消失。宿主也可以直接调用 `DemoteAsync` 或 `RejectAsync`。

重新激活被降级或被替代的旧版本就是回滚：恢复的是原来保存的精确 instructions，不会再让模型临场重写一份。已拒绝版本不能激活。无效审计记录会受 `MaximumRetainedInactiveVersions` 限制；当前生效版本和等待处理的候选不会因保留策略被清理。`MaximumVersionsPerBehavior` 是保留上限，不是生命周期创建上限：最旧的非活动版本会为新候选腾出空间，会话级持久版本高水位则保证版本号不会被重用。因此版本号在同一会话内单调递增，但对单个行为不保证连续。

## 明确不做的事

- 不收集私有 reasoning 或隐藏思维链；
- 不隐式启动后台模型请求。游戏可以安排低优先级审查器，通过 `ProposeAsync` 提交类型化结果，并单独核算模型用量；
- 不自动广播个人经验。共享发布和每个 NPC 的采用是两个独立的宿主授权操作；
- 不训练模型权重，不生成可执行代码或新工具实现，不修改 Provider 凭据；学习结果只是对宿主已注册工具的声明式过程；
- 事实、关系、偏好和事件仍属于 Memory；行为学习只保存可复用的过程说明。
