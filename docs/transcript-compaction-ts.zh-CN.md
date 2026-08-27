# 会话压缩

长期运行的角色会话可以按需启用 `@opengameagent/transcript`。它负责限制规范会话的长度，但不会把记忆、世界状态或动作回执混成一段不可验证的提示词。

`SummarizingGameConversationCompactor` 会估算上下文用量，只在完整的玩家回合边界切分：旧前缀交给摘要器，最近的完整后缀原样保留。摘要本身是规范会话消息，会随存档读写；再次压缩时会继续吸收旧摘要，而不是把已经移除的历史偷偷恢复回来。

摘要请求比正常 Agent 请求更严格：

- 不提供工具，也不允许异步延迟任务；
- 不保留 Provider 缓存；
- 不包含隐藏 reasoning 或 reasoning 签名；
- 不包含工具参数、工具调用 ID、回执详情、session/actor 身份或游戏权威坐标；
- 不发送图片字节，只保留“这里有图片但已省略”的标记；
- 对输入、输出、摘要长度、消息数和保留上下文都设置明确上限。

Pi 内核适配包提供 `PiGameTranscriptSummarizer`，可以给摘要单独选择一个受信任的模型配置。摘要产生的 token 与费用会合并进本次运行的 usage，不会从成本统计中消失。

```ts
const compactor = new SummarizingGameConversationCompactor({
  summarizer: new PiGameTranscriptSummarizer({
    models,
    modelProfileId: "background-summary",
  }),
  reserveTokens: 16_384,
  keepRecentTokens: 20_000,
  maximumSummaryTokens: 2_048,
});

const kernel = new PiGameAgentKernel({
  models,
  conversationStore,
  conversationCompactor: compactor,
});
```

压缩采用 fail-closed：摘要为空、截断、非法、超限或意外产生工具调用时，规范历史不会被替换。如果最新的一个完整回合本身已经超出预算，框架会明确失败，而不会拆开 assistant/tool exchange 或丢弃权威证据。

会话压缩不等于长期记忆。角色事实、关系、目标、世界事件和学习到的行为仍应进入类型化持久存储，再由上下文提供器按需召回；摘要只负责提供一个有界的对话续接检查点。
