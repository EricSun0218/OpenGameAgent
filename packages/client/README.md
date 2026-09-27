# @opengameagent/client

Generated HTTP types plus a thin resumable SSE client for the OpenGameAgent runtime protocol.

`readHealth()` reads the optional authenticated Runtime dependency snapshot through the same bounded HTTP response path. It performs one explicit request and adds no polling, retry, cache, or readiness policy.

`readHealth()` 通过同一条有界 HTTP 响应路径读取可选的、需要认证的 Runtime 依赖快照。它只执行一次显式请求，不增加轮询、重试、缓存或就绪策略。

Hosts must provide separate byte limits for ordinary HTTP responses, attachment responses, and individual SSE records.
SSE default and maximum reconnect delays are also mandatory and may not exceed Node's `2,147,483,647 ms` timer
ceiling. Server-provided retry values are capped by the selected maximum before the generated client sleeps.
`RuntimeClient.streamEvents` treats a clean EOF as a disconnected durable subscription, reconnects with the last
fully delivered event or cursor revision in `Last-Event-ID`, and counts every connection against the host-provided
attempt limit. A reconciliation gap ends the stream normally; exhaustion raises `RuntimeSseDisconnectedError` and
preserves the final connection failure as its cause. Aborting the request ends the stream without that error.
`RuntimeClient.readAttachment` returns binary PNG data only after checking its declared length, media type,
content-addressed identifier, RFC 9530 content digest, ETag, and privacy headers against the requested reference.

`RuntimeClient.readDeferred`, `resumeDeferred`, and `abandonDeferred` are thin generated requests and do not retry a
command automatically. If a response is uncertain, read the current deferred state and canonical events before choosing
whether another explicit resume is intended.

`RuntimeClient.recoverAction` is the recovery-only Action call for a restarted host that retained only `sessionId` and
`actorId`. It returns the oldest already claimed or uncertain reconciliation delivery without expiring, claiming, or
changing any Action. Drain it before calling `nextAction` or opening `streamActions`.

Run requests may opt into `admissionAuthority: { scope, generation }`; the generation is a canonical decimal string.
`submitRun` and `startRunSession` capture this authority with the input payload before asynchronous work. The session
validates both fields against `run.queued`. `closeAuthority` permanently closes that exact actor stream, scope, and
generation, while `readAuthorityClosure` returns its durable closure or `null`. Both calls snapshot their request and
reject a closure for another key. Replay an uncertain close with the same complete request and `operationId`; these
methods do not allocate generations or retry commands. Other generations and unscoped ordinary runs remain independent.

A close can include `workRetirement: { retirementId, partition, recordedAt }`; its exact partition requires owner,
timeline, generation, session and actor. The corresponding `workRetirementResult` includes the durable `fenced` or
`finalized` phase and cancellation counts as canonical decimal strings. A fenced result remains closed to execution
but does not yet prove host drain or final lease release. Close and read validate this request/result pairing and preserve
all retirement metadata without inferring authority from timeline names.

`RuntimeClient.startRunSession` submits one run and owns its exact client-side lifecycle until the matching
`run.completed` event. The session filters the actor event stream by the accepted run, requests Action deliveries and
pending approvals with that run ID, tracks approval revision/fingerprint fences for `decideApproval`, and uses the latest
observed turn coordinate for an explicit remote `abort()`. It begins event replay at the accepted run's required
`eventCursor`, the authoritative revision immediately before `run.queued`, so older actor history is not replayed. Action
streaming and Approval discovery remain unopened until the same run emits a validated `run.started`; a queued session
therefore occupies only its event SSE connection. Event and Action retry policies are mandatory when their streams are
enabled. Approval page size and a strictly increasing `pollDelayScheduleMs` containing at least two host-selected delays
are mandatory. Discovery runs once immediately after `run.started`; an unchanged result advances through the schedule
and repeats its final delay, while a changed pending set resets to the first delay. A successful local decision also
resets the schedule and wakes the current delay for immediate reconciliation. The steady-state request-rate bound is
`startedApprovalSessions * 1000 / finalDelayMs`, excluding the one activation request and genuine change bursts. Event,
Action-delivery, and pending-Approval callbacks each require an explicit deadline. An event gap, premature enabled
stream EOF, identity mismatch, or handler failure rejects `completion` and stops sibling observers. An `onEvent`
observer receives every matching event, including the terminal notification; every callback is awaited within its
configured deadline, and a callback failure or timeout rejects `completion`. `close(reason)` and
caller cancellation stop only local observation with the exact reason: they never abort or roll back the durable run.

For sessions observed concurrently, configure the server with
`maxSseConnections >= observedAcceptedNonTerminalSessions + startedActionSessions`. If every observed run enables
Actions and the host observes at most `Q` queued plus `A` active runs, the bound is `Q + 2 * A`; add direct non-session
streams separately.

Runtime endpoints must use HTTPS or exact loopback HTTP without URL credentials, query strings, fragments, or
backslashes. The client overrides generated request settings with Fetch `redirect: "error"` for JSON, attachment, and
SSE requests, and defensively rejects a response that reports redirect history or a different final URL.
