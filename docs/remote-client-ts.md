# Remote runtime client

`@opengameagent/client` is the typed TypeScript client for a runtime hosted outside the game process. It covers the same public operations used by engine adapters and server-side games:

- capability negotiation;
- streamed runs and bounded event parsing;
- exact run/turn steer, follow-up, and abort;
- persisted event replay with sequence-gap reporting;
- durable action claim, receipt submission, and reconciliation;
- bounded transcript pages, attachment retrieval, and usage summaries;
- pending tool-approval reads and decisions.

```ts
import { GameAgentClient } from "@opengameagent/client";

const client = new GameAgentClient({
  baseUrl: "http://127.0.0.1:4317",
  authentication: async () => ({ pairingToken: await hostPairingToken() }),
});

const capabilities = await client.capabilities();
for await (const event of client.run(input, { signal })) {
  await gameThreadQueue.enqueue(event);
}
```

The body credential is intended for constrained loopback clients that cannot set headers. It is mapped to a principal by the server authenticator and never becomes part of `GameInput`. Remote plaintext HTTP is rejected by the client; non-loopback deployments require HTTPS. URLs cannot contain credentials, redirects are rejected, and response/event sizes are bounded.

After a disconnect, do not replay a world-writing input blindly. Read persisted events with `readRunEvents(session, runId, { afterSequence })`, inspect `gap`, and reconcile every delivered action whose journal state is not terminal. Exact control always includes the expected run ID and turn so a late client cannot steer or abort a newer run for the same actor.
