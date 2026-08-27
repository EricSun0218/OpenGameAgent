# Transcript compaction

Long-lived character conversations can enable the optional `@opengameagent/transcript` package. It keeps the canonical transcript bounded without turning memory, world state, or tool receipts into an unstructured prompt.

`SummarizingGameConversationCompactor` estimates current context use, cuts only at complete user-turn boundaries, summarizes the old prefix, and retains the recent complete suffix. The resulting summary is a first-class canonical conversation message and survives save/reload. A later compaction incorporates the earlier summary rather than silently restoring removed history.

The summary request is deliberately narrower than the live agent request:

- no tools or deferred work;
- no provider cache retention;
- no hidden reasoning or reasoning signatures;
- no tool arguments, tool-call IDs, receipt details, session/actor identity, or game authority coordinates;
- no inline image bytes; image presence is represented only as an omission marker;
- bounded input, output, summary, message count, and retained context.

The Pi-backed kernel includes `PiGameTranscriptSummarizer`, which uses an independently selected trusted model profile. Compaction usage is added to the run usage instead of disappearing from cost accounting.

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

Compaction fails closed. An empty, truncated, invalid, oversized, or tool-producing summary never replaces canonical history. If the latest single complete turn cannot fit while preserving the configured boundary, the run fails instead of splitting an assistant/tool exchange or dropping authority evidence.

Compaction is not long-term memory. Games should keep durable character facts, relationships, goals, world events, and learned behavior in their typed stores and context providers. The summary only keeps a bounded conversational continuation checkpoint.
