# Final-output admission

Strict final-output admission is an opt-in boundary for games that need a
machine-checkable result instead of treating any final assistant text as the
run outcome. It is disabled by default, so existing runs keep their direct
completion behavior.

## Enable it

Enable the runtime option and register exactly one game-owned policy:

```csharp
var runtimeOptions = new DurableAgentRuntimeOptions
{
    FinalOutputAdmission = new FinalOutputAdmissionOptions
    {
        Enabled = true,
        MaxAttempts = 4,
        PolicyTimeout = TimeSpan.FromMilliseconds(500)
    }
};

await using var built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(journalPath)
    .AddProvider(provider)
    .WithRuntimeOptions(runtimeOptions)
    .WithFinalOutputAdmissionPolicy(finalOutputPolicy)
    .Build();
```

Supplying only the option or only the policy is a configuration error. The
runtime binds the policy ID, policy version, and bounded-options digest to the
durable run. A nonterminal resume must use the exact same binding.

An optional `FinalOutputContract` on `DurableRunRequest` or
`DurableRunContinuation` adds a durable schema ID, version, schema, and digest.
The runtime validates the submitted JSON value before calling the policy.
Changing or removing that contract during a nonterminal resume fails closed.

## Submission protocol

The runtime adds a local control tool named
`runtime_submit_final_output`. Applications cannot register another tool with
that name. It is evaluated in the agent loop and is never sent to
`IGameHost`.

```json
{
  "output": {
    "result": "completed"
  },
  "evidence": [
    {
      "operationId": "operation-id",
      "revision": 0,
      "sourceEventId": "<exact eventId from evidenceReference>"
    }
  ]
}
```

`output` may be any JSON value accepted by the optional contract. `evidence`
may cite only terminal action receipts durably committed by the current run.
The source event ID, operation ID, revision, and receipt digest must match the
journal exactly. Unknown receipts, another run's receipts, fabricated IDs, and
stale revisions are rejected before the policy runs.
Event IDs are opaque: copy the exact value supplied by the runtime instead of
constructing one from an operation ID.

For strict runs, terminal tool results shown to the model use a typed wrapper:

```json
{
  "contentType": "application/vnd.game-agent.action-receipt-evidence+json",
  "receipt": {
    "operationId": "operation-id",
    "revision": 0,
    "status": "succeeded"
  },
  "evidenceReference": {
    "operationId": "operation-id",
    "revision": 0,
    "finalOutputEvidenceSourceEventId": "<opaque durable event ID>"
  }
}
```

The host receipt remains unchanged. The evidence reference is runtime-owned
presentation metadata, so a full host extension bag does not lose capacity and
the host cannot pre-populate the reserved field. Recovery reconstructs the same
wrapper from the exact durable receipt event, including after a process loss
between receipt commit and tool-result transcript commit.

## Policy boundary

`IFinalOutputAdmissionPolicy.EvaluateAsync` receives:

- the current run and turn identity;
- the exact structured proposal and cited receipt evidence;
- all citable terminal receipts committed by the current run;
- the selected context for the current turn.

The game can enforce any domain-specific completion rule in this policy.
The policy does not receive authority to mutate world state, register tools,
activate skills, or bypass host action checks. Returning a rejection produces
a typed tool result with a bounded reason code and optional bounded feedback,
then the normal agent loop continues.

Every evaluation has bounded queueing, concurrency, JSON bytes, depth, nodes,
feedback bytes, and wall-clock time. The policy is run away from the caller's
thread so a synchronous prefix cannot extend the wall timeout. Cancellation
callbacks also use a bounded dispatcher and never run inline on the runtime
thread.

Policy execution capacity is process-wide as well as runtime-local. If all
execution leases are held by unsettled policies, a new evaluation fails closed
with `final_output_admission_policy_capacity_exhausted`; it is not queued behind
an uncooperative callback. The lease is returned only when the complete policy
evaluation settles, including any asynchronous suffix.

An implementation that ignores cancellation remains isolated and keeps its
evaluation slot until it actually returns. Shutdown waits only for the bounded
policy window. After shutdown:

- `FinalOutputAdmissionPolicyCallsDrainedOnStop` reports the bounded result;
- `DetachedFinalOutputAdmissionPolicyCallCount` reports detached evaluations
  whose policy execution or cancellation cleanup is still unsettled.

`ShutdownResourceCleanupCompleted` means the runtime completed its bounded
cancellation and isolation work. It does not claim that a non-cooperative
policy callback exited.

## Provisional and admitted output

In strict mode, provider text and rejected submissions are provisional:

- streamed `assistant.delta` events carry
  `"presentationState": "provisional"`;
- every durable `provider.result_committed` event carries typed
  `finalOutputPresentation` metadata;
- provisional attempts do not emit `assistant.completed`;
- memory commit policy receives `AssistantOutput = null`,
  `AssistantMessage = null`, and a committed transcript with provisional
  assistant messages removed.

For a bare-text response, the assistant transcript, provisional provider
result, and typed `final_output_submission_required` feedback commit as one
atomic batch. Recovery therefore cannot retain the response while losing its
attempt count or correction prompt.
Rejected submissions use the same atomic boundary for every typed tool-result
message in that provider turn.

Attempt accounting is reconstructed only from current-run
`provider.result_committed` events and their runtime-owned presentation
metadata. Initial transcript content, historical submission-shaped messages,
and ordinary game-tool arguments cannot consume the current run's attempt
budget.

An accepted submission atomically commits the assistant transcript, provider
result, structured output, and admission evidence. Its provider-result and
`assistant.completed` events carry an `admitted` presentation with the exact
evidence digest. Only that admitted JSON value becomes
`DurableRunOutcome.FinalOutput` and is supplied as memory
`AssistantOutput`. Once admitted, that same output remains present in a failed
or cancelled outcome if a later memory or completion step fails, and recovery
returns the identical value. The terminal failure event also carries bounded
runtime-owned outcome metadata, so replay restores the same error code,
category, and safe message without persisting an exception or sensitive
diagnostic text.

Recovery verifies every presentation marker, its state, and its evidence
digest. Missing metadata on a strict provider result, admission metadata on a
non-strict run, duplicate or extra admitted states, and tampered digests all
fail closed. If the final allowed rejection was durable but the process stopped
before the live loop recorded failure, resume commits the stable
`final_output_admission_attempts_exhausted` failure before any new provider,
policy, or host dispatch.
